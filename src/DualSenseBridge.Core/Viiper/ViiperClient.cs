using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DualSenseBridge.Core.Viiper;

/// <summary>Диалект VIIPER-сервера.</summary>
public enum ViiperDialect
{
    /// <summary>
    /// Форк hbashton/VIIPER (бэкенд DS4Windows 5): кадры VPCM, устройство
    /// dualsensegamepadv5, обратная связь 474 байта с адаптивными триггерами
    /// и нативным отчётом 0x02. Рекомендуемый режим.
    /// </summary>
    HbashtonV5,

    /// <summary>
    /// Upstream Alia5/VIIPER (stable): устройство "dualsense", сырой стрим —
    /// ввод 33 байта, обратная связь 6 байт (rumble + RGB + player LEDs, БЕЗ триггеров).
    /// Совместимый запасной режим.
    /// </summary>
    Upstream,
}

/// <summary>Параметры добавленного виртуального устройства.</summary>
public sealed record ViiperDeviceHandle(uint BusId, string DevId, ViiperDialect Dialect)
{
    /// <summary>Устройство переиспользовано от прошлой сессии (не создавалось заново).</summary>
    public bool Reused { get; init; }
}

/// <summary>Событие обратной связи виртуального контроллера.</summary>
public sealed class FeedbackReceivedEventArgs : EventArgs
{
    public required DualSenseFeedback Feedback { get; init; }

    /// <summary>Сырой payload обратной связи (для upstream — 6 байт).</summary>
    public required byte[] RawPayload { get; init; }

    /// <summary>Тип кадра V5 (0x81/0x83/0x84) — для выбора дорожки хаптик.</summary>
    public byte FrameType { get; init; }

    /// <summary>Только upstream-диалект: rumble/LED без триггеров.</summary>
    public bool IsSimpleFeedback => RawPayload.Length == 6;

    /// <summary>
    /// PCM динамика из хвоста атомарного кадра 0x83 (1920 Б = 10 мс S16LE стерео
    /// 48 кГц) — v1.12.0: viiper НЕ кодирует дорожку динамика сам, это делает мост.
    /// null у кадров без звука.
    /// </summary>
    public byte[]? SpeakerPcm { get; init; }
}

/// <summary>
/// Минимальный TCP-клиент VIIPER: управление шиной/устройствами (строковые запросы,
/// завершаемые нулевым байтом, ответ JSON до закрытия соединения) и долгоживущий
/// двоичный стрим устройства. Протокол одинаков в upstream и форке hbashton;
/// отличие — в содержимом стрима (кадры V5 против сырых пакетов фиксированного размера).
/// Ссылки: alia5.github.io/VIIPER (API Reference); hbashton/VIIPER docs/api/overview.md.
/// </summary>
public sealed class ViiperClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private long _outboundSeq;

    public ViiperDialect Dialect { get; }

    public ViiperClient(string host = "127.0.0.1", int port = 3242, ViiperDialect dialect = ViiperDialect.HbashtonV5)
    {
        _host = host;
        _port = port;
        Dialect = dialect;
    }

    // ------------------------------------------------------------------ Management API

    /// <summary>Проверяет доступность сервера (ping). Возвращает версию или null.</summary>
    public async Task<string?> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var conn = await ConnectAsync(ct);
            await SendRequestAsync(conn, "ping", ct);
            string response = await ReadResponseAsync(conn, ct);
            using var doc = JsonDocument.Parse(response);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : response;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Создаёт шину (если нет) и добавляет виртуальный DualSense (с фолбэком типа).</summary>
    public Task<ViiperDeviceHandle> AddDualSenseAsync(CancellationToken ct = default)
        => AddWithFallbackAsync(ct);

    /// <summary>Тип виртуального DualSense: полный дуплекс (аудио/хаптики) или только геймпад.</summary>
    public string FullDuplexDeviceType { get; set; } = "dualsensecombinedaudioduplexv5";

    /// <summary>
    /// Конвертер задних PCM-каналов хаптик (создаётся с устройством, immutable):
    /// "sony-bt-wdl-sinc64-v1" — sinc-ресемпл 48→3 кГц (WDL), идеален для BT
    /// (подтверждён viiper docs/dualsense-rear-resampler.md); null — без опции
    /// (устойчиво к старым версиям viiper). Только для аудио-способных типов.
    /// </summary>
    public string? HapticsConverter { get; set; } = "sony-bt-wdl-sinc64-v1";

    /// <summary>Добавляет устройство указанного типа (создаёт шину при необходимости).</summary>
    public async Task<ViiperDeviceHandle> AddDeviceAsync(string type, string? hapticsConverter, CancellationToken ct = default)
    {
        uint busId = await GetOrCreateBusAsync(ct);

        // JSON собираем сериализатором (raw-string с фигурными скобками хрупок).
        string payload;
        using (var ms = new MemoryStream())
        {
            using (var w = new System.Text.Json.Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("type", type);
                if (hapticsConverter is not null)
                {
                    w.WriteStartObject("deviceSpecific");
                    w.WriteString("hapticsConverter", hapticsConverter);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            payload = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        using (var conn = await ConnectAsync(ct))
        {
            await SendRequestAsync(conn, $"bus/{busId}/add {payload}", ct);
            var added = JsonDocument.Parse(await ReadResponseAsync(conn, ct));
            string devId = added.RootElement.GetProperty("devId").GetString()
                           ?? throw new InvalidOperationException("VIIPER не вернул devId");
            return new ViiperDeviceHandle(busId, devId, Dialect);
        }
    }

    /// <summary>
    /// Добавляет виртуальный DualSense с фолбэком: если полный дуплекс не поддержан
    /// (старый viiper) — повторяет с обычным типом dualsensegamepadv5.
    /// </summary>
    public async Task<ViiperDeviceHandle> AddWithFallbackAsync(CancellationToken ct = default)
    {
        if (Dialect != ViiperDialect.HbashtonV5)
            return await AddDeviceAsync("dualsense", null, ct);

        uint busId = await GetOrCreateBusAsync(ct);

        // v1.9.1 — переиспользование устройства прошлой сессии: если приложение
        // завершилось аварийно, viiper и его виртуальный DualSense живут дальше,
        // и ИГРА уже держит их. Создание второго устройства бессмысленно (игра
        // читает старое) — подключаемся стримом к существующему.
        var existing = await TryReuseExistingDualSenseAsync(busId, ct);
        if (existing is not null)
        {
            Log.Info($"Найден виртуальный DualSense прошлой сессии (шина {busId}, устройство {existing.DevId}) — " +
                     "переиспользуем его (игра сохранит контроллер без переподключения)");
            return existing;
        }

        if (HapticsConverter is null)
            return await AddDeviceAsync(FullDuplexDeviceType, null, ct);

        try
        {
            return await AddDeviceAsync(FullDuplexDeviceType, HapticsConverter, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"Полный дуплекс ({FullDuplexDeviceType} + {HapticsConverter}) не принят сервером " +
                     $"({ex.Message}) — повтор с обычным dualsensegamepadv5 (хаптики PCM будут недоступны)");
            return await AddDeviceAsync("dualsensegamepadv5", null, ct);
        }
    }

    /// <summary>Находит (или создаёт) шину и возвращает её идентификатор.</summary>
    private async Task<uint> GetOrCreateBusAsync(CancellationToken ct)
    {
        using (var conn = await ConnectAsync(ct))
        {
            await SendRequestAsync(conn, "bus/list", ct);
            var list = JsonDocument.Parse(await ReadResponseAsync(conn, ct));
            if (list.RootElement.TryGetProperty("buses", out var buses) && buses.GetArrayLength() > 0)
                return (uint)buses[0].GetInt32();
        }
        using (var conn2 = await ConnectAsync(ct))
        {
            await SendRequestAsync(conn2, "bus/create", ct);
            var created = JsonDocument.Parse(await ReadResponseAsync(conn2, ct));
            return (uint)created.RootElement.GetProperty("busId").GetInt32();
        }
    }

    /// <summary>
    /// Ищет на шине существующий виртуальный DualSense (PID 0x0CE6 / Edge 0x0DF2) —
    /// например, оставшийся после аварийного завершения прошлой сессии.
    /// </summary>
    private async Task<ViiperDeviceHandle?> TryReuseExistingDualSenseAsync(uint busId, CancellationToken ct)
    {
        try
        {
            using var conn = await ConnectAsync(ct);
            await SendRequestAsync(conn, $"bus/{busId}/list", ct);
            var resp = JsonDocument.Parse(await ReadResponseAsync(conn, ct));
            if (!resp.RootElement.TryGetProperty("devices", out var devices)) return null;
            foreach (var d in devices.EnumerateArray())
            {
                string? pid = d.TryGetProperty("pid", out var p) ? p.GetString() : null;
                if (pid is null) continue;
                bool isDualSense = pid.Contains("0ce6", StringComparison.OrdinalIgnoreCase) ||
                                   pid.Contains("0df2", StringComparison.OrdinalIgnoreCase);
                if (!isDualSense) continue;
                string? devId = d.TryGetProperty("devId", out var id) ? id.GetString() : null;
                if (devId is null) continue;
                return new ViiperDeviceHandle(busId, devId, Dialect) { Reused = true };
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Поиск устройства прошлой сессии не удался: {ex.Message}");
        }
        return null;
    }

    /// <summary>Удаляет устройство с шины.</summary>
    public async Task RemoveDeviceAsync(ViiperDeviceHandle device, CancellationToken ct = default)
    {
        try
        {
            using var conn = await ConnectAsync(ct);
            await SendRequestAsync(conn, $"bus/{device.BusId}/remove {device.DevId}", ct);
            await ReadResponseAsync(conn, ct);
        }
        catch (Exception)
        {
            // удаление best-effort
        }
    }

    // ------------------------------------------------------------------ Стрим устройства

    /// <summary>
    /// Открывает стрим устройства: handshake "bus/{busId}/{devId}\0",
    /// далее — двоичный протокол (кадры V5 либо сырые пакеты, в зависимости от диалекта).
    /// </summary>
    public async Task<ViiperDeviceStream> OpenStreamAsync(ViiperDeviceHandle device, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(_host, _port, ct);
        var stream = tcp.GetStream();

        var handshake = Encoding.ASCII.GetBytes($"bus/{device.BusId}/{device.DevId}\0");
        await stream.WriteAsync(handshake, ct);
        await stream.FlushAsync(ct);

        return new ViiperDeviceStream(tcp, stream, this);
    }

    internal long NextSequence() => Interlocked.Increment(ref _outboundSeq);

    private async Task<TcpClient> ConnectAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3)); // localhost — больше не нужно
        try
        {
            await tcp.ConnectAsync(_host, _port, timeout.Token);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return tcp;
    }

    private static async Task SendRequestAsync(TcpClient conn, string request, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(request + "\0");
        await conn.GetStream().WriteAsync(bytes, ct);
        await conn.GetStream().FlushAsync(ct);
    }

    private static async Task<string> ReadResponseAsync(TcpClient conn, CancellationToken ct)
    {
        var buf = new byte[4096];
        var sb = new StringBuilder();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            int n = await conn.GetStream().ReadAsync(buf, cts.Token);
            if (n <= 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
        }
        return sb.ToString().TrimEnd('\0', '\n', '\r', ' ');
    }

    public void Dispose() { /* соединения открываются на каждый запрос и освобождаются через using */ }
}

/// <summary>
/// Долгоживущий двоичный стрим виртуального DualSense.
/// Отправка состояния: кадр 0x01 (payload 33 байта) в диалекте V5,
/// либо сырой 33-байтовый пакет в upstream. Приём обратной связи — событие.
/// </summary>
public sealed class ViiperDeviceStream : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly ViiperClient _owner;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private readonly byte[] _readBuf = new byte[64 * 1024];
    private int _readLen;

    public event EventHandler<FeedbackReceivedEventArgs>? FeedbackReceived;
    public event EventHandler<Exception>? StreamFailed;

    /// <summary>Число отправленных состояний ввода (для статистики).</summary>
    public long SentInputFrames;

    /// <summary>Число полученных пакетов обратной связи.</summary>
    public long ReceivedFeedbackFrames;

    internal ViiperDeviceStream(TcpClient tcp, NetworkStream stream, ViiperClient owner)
    {
        _tcp = tcp;
        _stream = stream;
        _owner = owner;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Отправляет состояние контроллера (33 байта) в виртуальное устройство.</summary>
    public async Task SendInputAsync(DualSenseWireInput input, CancellationToken ct = default)
    {
        byte[] payload = input.ToBytes();
        byte[] packet = _owner.Dialect == ViiperDialect.HbashtonV5
            ? V5Frame.Encode(V5Frame.TypeInputState, (uint)_owner.NextSequence(), payload)
            : payload;

        await _stream.WriteAsync(packet, ct);
        await _stream.FlushAsync(ct);
        Interlocked.Increment(ref SentInputFrames);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(_readBuf.AsMemory(_readLen), _cts.Token);
                if (n <= 0) throw new IOException("VIIPER закрыл стрим устройства");
                _readLen += n;

                while (TryConsumeFrame())
                {
                    // потребляем все полные кадры в буфере
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StreamFailed?.Invoke(this, ex);
        }
    }

    private bool TryConsumeFrame()
    {
        var buffer = _readBuf.AsSpan(0, _readLen);

        if (_owner.Dialect == ViiperDialect.Upstream)
        {
            // Сырой стрим: пакеты фиксированного размера; обратная связь = 6 байт.
            if (buffer.Length < 6) return false;
            var raw = buffer[..6].ToArray();
            Consume(6);
            OnFeedback(new DualSenseFeedback
            {
                RumbleSmall = raw[0],
                RumbleLarge = raw[1],
                LedRed = raw[2],
                LedGreen = raw[3],
                LedBlue = raw[4],
                PlayerLeds = raw[5],
            }, raw);
            return true;
        }

        if (!V5Frame.TryDecode(buffer, out byte type, out _, out var payload, out bool broken, out int consumed))
        {
            if (broken)
                throw new IOException("нарушен протокол V5 (CRC/версия/тип кадра) — сервер разорвёт соединение");
            return false; // ждём остаток кадра
        }

        Consume(consumed);

        switch (type)
        {
            case V5Frame.TypeFeedback when payload.Length == V5Frame.FeedbackPayloadSize
                                           && DualSenseFeedback.TryParse(payload, out var fb):
                OnFeedback(fb, payload, V5Frame.TypeFeedback);
                break;

            case V5Frame.TypeFeedbackAudio:
                // 0x83: атомарная обратная связь + PCM динамика (хвост 1920 Б).
                // v1.12.0: хвост НЕ выбрасываем — из него мост кодирует дорожку
                // динамика (Opus) в носителе 0x36.
                if (DualSenseFeedback.TryParseAtomic(payload, out var fbA, out var speakerPcm))
                    OnFeedback(fbA, payload.AsSpan(2, V5Frame.FeedbackPayloadSize).ToArray(),
                        V5Frame.TypeFeedbackAudio, speakerPcm);
                break;

            case V5Frame.TypeRealtimeHaptics:
                // 0x84: realtime-хаптики (v0.1.2): payload = те же 474 байта состояния,
                // но с заполненным BT-носителем 0x36 — обрабатываем как обычную отдачу.
                if (payload.Length == V5Frame.FeedbackPayloadSize && DualSenseFeedback.TryParse(payload, out var fbR))
                    OnFeedback(fbR, payload, V5Frame.TypeRealtimeHaptics);
                break;

            case V5Frame.TypeMicEvent:
                // событие микрофонного интерфейса — пока не используется (аудио вне скоупа v1)
                break;

            default:
                // неизвестные типы просто пропускаем
                break;
        }
        return true;
    }

    private void OnFeedback(DualSenseFeedback fb, byte[] raw, byte frameType = V5Frame.TypeFeedback, byte[]? speakerPcm = null)
    {
        Interlocked.Increment(ref ReceivedFeedbackFrames);
        FeedbackReceived?.Invoke(this, new FeedbackReceivedEventArgs { Feedback = fb, RawPayload = raw, FrameType = frameType, SpeakerPcm = speakerPcm });
    }

    private void Consume(int count)
    {
        if (count <= 0) return;
        Array.Copy(_readBuf, count, _readBuf, 0, _readLen - count);
        _readLen -= count;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _stream.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
        try { _readLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}

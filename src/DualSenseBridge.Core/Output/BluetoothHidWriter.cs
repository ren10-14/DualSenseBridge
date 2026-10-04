using System.Diagnostics;
using System.Runtime.InteropServices;
using DualSenseBridge.Core;
using DualSenseBridge.Core.Hid;
using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core.Output;

/// <summary>Статистика канала исходящих пакетов (для UI и журнала).</summary>
public sealed class OutputStats
{
    public long BytesIn;        // байт обратной связи, принятых от VIIPER
    public long FramesIn;       // кадров обратной связи
    public long BytesOut;       // байт, реально записанных в контроллер
    public long WritesOut;      // операций записи
    public long SuppressedDuplicates; // пакетов, отброшенных как не изменившиеся
    public double LastWriteMs;  // задержка последней обратной связи до записи
    public double AvgIntervalMs; // средний интервал записи
    public DateTime LastWriteUtc;

    /// <summary>Сводка аудиоканала хаптик (заполняется писателем).</summary>
    public string HapticsSummary = "";

    /// <summary>Какой канал ведёт носители 0x36: пейсер или синхронный резерв (v1.10.1).</summary>
    public string MediaMode = "";

    public double CompressionRatio => BytesIn == 0 || BytesOut == 0 ? 0 : (double)BytesIn / BytesOut;

    public string Describe() =>
        $"вход: {FramesIn} кадров ({BytesIn / 1024.0:F1} КиБ) | выход: {WritesOut} записей ({BytesOut / 1024.0:F1} КиБ) | " +
        $"сжатие 474Б->78Б: x{CompressionRatio:F1} | подавлено дублей: {SuppressedDuplicates} | " +
        $"задержка последней: {LastWriteMs:F1} мс | интервал: {AvgIntervalMs:F2} мс" +
        (HapticsSummary.Length > 0 ? $" | {HapticsSummary}" : "") +
        (MediaMode.Length > 0 ? $" | {MediaMode}" : "");
}

/// <summary>
/// Планировщик записи в физический DualSense по Bluetooth (или USB).
///
/// Оптимизация трафика (задача «пакеты меньшего размера, но читаемые контроллером»):
///  1) 78 байт — минимальный КОРРЕКТНЫЙ полный BT output-отчёт 0x31; уменьшать его
///     нельзя (протокол: report ID + seq/tag + 47-байтовый общий блок + 24 reserved + CRC32);
///  2) отправка только при изменении состояния + анти-дельта через флаги валидности
///     (в структурированном режиме выставляются только реально используемые секции);
///  3) коалесценция: если новое состояние пришло, пока предыдущее ещё не отправлено,
///     отправляется только самое свежее (newest-wins, как у VIIPER V5);
///  4) ограничение частоты (по умолчанию не чаще 1 записи / 4 мс — период опроса BT).
/// Фрагментация и «сжатие» сверх протокола невозможны и не используются —
/// контроллер принимает только валидные полные отчёты (см. docs/RESEARCH.md).
/// </summary>
public sealed class BluetoothHidWriter : IDisposable
{
    private readonly DualSenseHidDevice _device;
    private readonly object _lock = new();
    private OutputCommon? _pending;
    private OutputCommon? _lastSent;

    /// <summary>
    /// Джиттер-буфер аудиоканала 0x36 (дедупликация ТОЛЬКО пар 0x83+0x84, drop-oldest).
    /// v1.9.9: ёмкость 8 → 16 — при bursts (аудио 100 Гц + хаптики) маленький
    /// буфер переполнялся и терял блоки = «пропадания звука».
    /// </summary>
    private readonly HapticsChannel _haptics = new(capacity: 16);

    /// <summary>Мост нулевых «zero-fill» дорожек хаптик (v1.11.0 — фикс «жужжания»).</summary>
    private readonly RearLaneBridge _rearBridge = new();

    /// <summary>Канал записи мёртв (8+ подряд ошибок записи) — устройство потеряно.</summary>
    public bool IsDead { get; private set; }
    private int _consecutiveWriteFailures;
    private bool _degradedLogged;
    private DateTime _lastFailReportUtc = DateTime.MinValue;
    private DateTime _lastHapticsArrivalUtc = DateTime.MinValue;
    private bool _muteFlushed;
    private volatile bool _gameSilenceNotified; // игра явно прислала тишину (v1.10.1)
    private readonly Stopwatch _sinceLastWrite = Stopwatch.StartNew();
    private readonly Stopwatch _feedbackAge = new();
    private double _emaWriteMs; // EMA длительности фактической записи (адаптация к каналу)
    private readonly List<double> _intervals = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public OutputStats Stats { get; } = new();
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMilliseconds(4);

    /// <summary>
    /// Базовый темп записи аудиоканала 0x36 (используется, пока скорость
    /// поступления блоков не измерена). Нативные часы хаптик viiper = 93,75 Гц.
    /// </summary>
    public TimeSpan HapticsPace { get; set; } = TimeSpan.FromMilliseconds(10.5);

    /// <summary>
    /// EMA интервала поступления блоков 0x36 (мс). Аудио идёт с честных 100 Гц
    /// (10 мс — замер по захватам PS5: 7191 кадров за 71,91 с), хаптики — 93,75 Гц.
    /// Темп записи подстраивается под фактического производителя (v1.9.9).
    /// </summary>
    private double _arrivalEmaMs;
    private DateTime _lastArrivalUtc;

    /// <summary>
    /// Эффективный темп 0x36: базовый ИЛИ фактическая длительность записи ×1.25 —
    /// канал медленнее потока пишем реже, очередь сбрасывает излишек (leaky),
    /// контроллер не заваливается.
    /// </summary>
    public static double EffectiveHapticsPaceMs(double baseMs, double emaWriteMs) =>
        Math.Max(baseMs, emaWriteMs * 1.25);

    /// <summary>
    /// АДАПТИВНЫЙ темп 0x36 (v1.9.9 — фикс «пропаданий звука»). Раньше темп был
    /// фиксированным 10,5 мс (93,75 Гц), а аудио-носители приходят с частотой
    /// 100 Гц (10 мс): хронический дефицит ~0,5 мс на блок → очередь росла →
    /// переполнение → выброс блока = щель в звуке. Теперь темп подстраивается
    /// под фактическую скорость поступления (arrivalEmaMs), а при накоплении
    /// очереди — ускоряет слив:
    ///   - номинал = интервал поступления (клампится 4..12 мс) или база, если не измерено;
    ///   - очередь ≥ 4 — догоняем (темп ×0.75);
    ///   - очередь ≥ 8 — авральный слив (темп ×0.5).
    /// Записи синхронные: даже «слишком быстрый» темп не заваливает канал —
    /// следующая запись начнётся только после завершения текущей.
    /// </summary>
    public static double ComputeHapticsPaceMs(double baseMs, double arrivalEmaMs, int queueCount)
    {
        double nominal = arrivalEmaMs > 0 ? Math.Clamp(arrivalEmaMs, 4.0, 12.0) : baseMs;
        if (queueCount >= 8) return Math.Max(4.0, nominal * 0.5);
        if (queueCount >= 4) return Math.Max(4.0, nominal * 0.75);
        return nominal;
    }

    /// <summary>
    /// Критическая деградация: фактическая запись дольше 30 мс И очередь глубоко
    /// (>12 из 16) — канал реально тонет, пишем через блок (теряем часть), лишь
    /// бы не сорвать связь. v1.9.9: порог очереди 2 → 12 — раньше «деградация»
    /// включалась слишком рано и сама рвала поток (пропадания звука).
    /// </summary>
    public static bool ShouldDegrade(double emaWriteMs, int queueCount) =>
        emaWriteMs > 30 && queueCount > 12;

    /// <summary>Сколько ждать новые блоки, прежде чем «замолчать» (мс).</summary>
    /// <summary>
    /// Фолбэк-тишина: «немой» блок по таймауту, если производитель умер, не прислав
    /// явный тихий носитель. ОСНОВНОЙ сигнал конца эффекта — NotifyGameSilence
    /// (v1.10.1: таймаут 45 мс рубил эффекты между бурстами viiper — «жужжание»).
    /// </summary>
    public TimeSpan HapticsIdleAfter { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Статистика аудиоканала (для UI/диагностики).</summary>
    public HapticsChannel Haptics => _haptics;

    /// <summary>Событие ошибки записи (например, отключение контроллера).</summary>
    public event Action<Exception>? WriteFailed;

    /// <summary>Сводка отдельного медиа-тракта 0x36 (v1.10.0, для UI).</summary>
    public void UpdateMediaSummary(string summary) => Stats.HapticsSummary = summary;

    /// <summary>Игра явно прислала тихий носитель — погасить хаптики её сигналом, а не таймаутом.</summary>
    public void NotifyGameSilence() => _gameSilenceNotified = true;

    public BluetoothHidWriter(DualSenseHidDevice device)
    {
        _device = device;
        // Таймер Windows по умолчанию тикает каждые 15,6 мс — темп хаптик 10,5 мс
        // физически невозможен (очередь переполнялась, блоки терялись).
        // 1-мс разрешение таймера — стандартная практика (DS4Windows, игры).
        try { _ = timeBeginPeriod(1); } catch { }
        _loop = Task.Run(WriteLoop);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    /// <summary>Ставит общий блок в очередь (newest-wins). bytesIn — размер исходного пакета источника.</summary>
    public void Enqueue(OutputCommon common, int sourceBytes = V5Frame.FeedbackPayloadSize)
    {
        lock (_lock)
        {
            _pending = common;
            _feedbackAge.Restart();
            Interlocked.Increment(ref Stats.FramesIn);
            Interlocked.Add(ref Stats.BytesIn, sourceBytes);
        }
    }

    /// <summary>
    /// Ставит блок аудиоканала 0x36 (398 Б) в джиттер-буфер: дубликаты-двойники
    /// (0x84 сразу после такого же 0x83) отбрасываются, при переполнении теряется
    /// старейший блок. frameType — тип кадра V5 (для точной дедупликации).
    /// Попутно измеряется скорость поступления (EMA) — темп записи
    /// подстраивается под производителя (аудио 100 Гц / хаптики 93,75 Гц).
    /// </summary>
    public void EnqueueCarrier(byte[] carrier, int sourceBytes, byte frameType = V5Frame.TypeFeedbackAudio)
    {
        if (_haptics.Enqueue(carrier, frameType))
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                // EMA интервала поступления (для адаптивного темпа). Пауза > 200 мс —
                // новый поток: сброс, чтобы не занижать темп после тишины.
                if (_lastArrivalUtc != default)
                {
                    double dt = (now - _lastArrivalUtc).TotalMilliseconds;
                    if (dt <= 200)
                        _arrivalEmaMs = _arrivalEmaMs == 0 ? dt : (_arrivalEmaMs * 0.8 + dt * 0.2);
                    else
                        _arrivalEmaMs = 0;
                }
                _lastArrivalUtc = now;

                _feedbackAge.Restart();
                _muteFlushed = false;
                _gameSilenceNotified = false;
                _lastHapticsArrivalUtc = now;
                Interlocked.Increment(ref Stats.FramesIn);
                Interlocked.Add(ref Stats.BytesIn, sourceBytes);
            }
        }
    }

    private async Task WriteLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2, _cts.Token);

                // ---------- Аудиоканал 0x36: адаптивный темп (v1.9.9) ----------
                // Темп = скорость поступления (аудио 100 Гц / хаптики 93,75 Гц) +
                // ускоренный слив при накоплении очереди (раньше фикс 10,5 мс
                // при аудио 100 Гц давал переполнение и «пропадания звука»).
                double paceMs = ComputeHapticsPaceMs(HapticsPace.TotalMilliseconds, _arrivalEmaMs, _haptics.Count);
                var pace = TimeSpan.FromMilliseconds(paceMs);

                if (_haptics.Count > 0 && _sinceLastWrite.Elapsed >= pace)
                {
                    var block = _haptics.TryDequeue();
                    if (block is not null)
                    {
                        // v1.11.0: zero-fill мостится последним реальным блоком
                        // (см. RearLaneBridge) — «немой» блок ниже не мостится.
                        _rearBridge.Bridge(block, DateTime.UtcNow);
                        WriteToDevice(block);

                        // Деградация: канал тонет (запись >30 мс, очередь копится) —
                        // сбрасываем СТАРЕЙШИЙ блок: теряем часть хаптик, но НЕ заваливаем
                        // контроллер и не рвём связь (главный урок v1.8.0).
                        bool degraded = ShouldDegrade(_emaWriteMs, _haptics.Count);
                        if (degraded)
                        {
                            _haptics.DropOldest();
                            if (!_degradedLogged && _haptics.DroppedBackoff > 10)
                            {
                                _degradedLogged = true;
                                Log.Warn($"Канал записи медленный (запись {_emaWriteMs:F0} мс) — режим деградации: " +
                                         "хаптики пишутся через блок, чтобы не сорвать связь с контроллером");
                            }
                        }
                        else if (_degradedLogged && _emaWriteMs < 20)
                        {
                            _degradedLogged = false;
                            Log.Info("Канал записи восстановился — полный поток хаптик");
                        }
                        continue; // блок ~10,5 мс — на этой итерации больше ничего не пишем
                    }
                }

                // Поток хаптик кончился — ОДИН «немой» блок для плавного затухания.
                // v1.10.1: первичный сигнал — ЯВНАЯ тишина игры (тихий носитель,
                // NotifyGameSilence); таймаут — только фолбэк смерти производителя.
                bool gameSilence = _gameSilenceNotified;
                if (_haptics.Count == 0 && !_muteFlushed && _lastHapticsArrivalUtc != DateTime.MinValue &&
                    (gameSilence || DateTime.UtcNow - _lastHapticsArrivalUtc >= HapticsIdleAfter) &&
                    _sinceLastWrite.Elapsed >= MinInterval && _emaWriteMs < 15)
                {
                    if (gameSilence) _gameSilenceNotified = false;
                    var mute = _haptics.BuildMuteBlock();
                    if (mute is not null)
                    {
                        _muteFlushed = true;
                        WriteToDevice(mute);
                        continue;
                    }
                }

                // ---------- Обычные отчёты 0x31 (состояние) ----------
                // Пока аудиоканал активен, состояние уже едет внутри 0x36 —
                // не дублируем запись (двойная запись = лишний трафик BT).
                bool hapticsActive = _haptics.Count > 0;

                OutputCommon? toSend = null;
                lock (_lock)
                {
                    if (_pending is not null && !hapticsActive)
                    {
                        // Адаптивный темп: фактическая длительность записи отражает пропускную
                        // способность канала (BT ~125 Гц); не выстраиваем очередь больше неё.
                        var effective = TimeSpan.FromMilliseconds(Math.Max(MinInterval.TotalMilliseconds, _emaWriteMs));
                        // СРОЧНОСТЬ: изменение блока триггеров (R2/L2) отправляем сразу —
                        // эффекты триггеров живут «на миг» и не терпят задержки.
                        bool triggerChanged = _lastSent is null
                            || !CommonEquals(_lastSent.Data, _pending.Data,
                                DualSenseProtocol.OffTriggerRight, 2 * TriggerEffect.BlockSize);
                        if (_sinceLastWrite.Elapsed >= (triggerChanged ? TimeSpan.FromMilliseconds(1) : effective))
                        {
                            // Дедупликация: если состояние не изменилось и не пора keep-alive — пропускаем.
                            bool changed = _lastSent is null || !CommonEquals(_lastSent.Data, _pending.Data);
                            bool keepAliveDue = (DateTime.UtcNow - Stats.LastWriteUtc) >= KeepAlive;
                            if (changed || keepAliveDue)
                                toSend = _pending;
                            else
                                Interlocked.Increment(ref Stats.SuppressedDuplicates);
                            _pending = null;
                        }
                    }
                }

                if (toSend is not null)
                {
                    var sw = Stopwatch.StartNew();
                    int written = _device.WriteCommon(toSend);
                    sw.Stop();
                    UpdateWriteEma(sw.Elapsed.TotalMilliseconds);

                    var now = DateTime.UtcNow;
                    lock (_lock)
                    {
                        _lastSent = Clone(toSend);
                        double interval = _sinceLastWrite.Elapsed.TotalMilliseconds;
                        _sinceLastWrite.Restart();
                        _intervals.Add(interval);
                        if (_intervals.Count > 200) _intervals.RemoveAt(0);
                        Stats.AvgIntervalMs = _intervals.Count == 0 ? 0 : _intervals.Average();
                        Stats.LastWriteMs = _feedbackAge.Elapsed.TotalMilliseconds;
                        Stats.LastWriteUtc = now;
                        Interlocked.Add(ref Stats.BytesOut, written);
                        Interlocked.Increment(ref Stats.WritesOut);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 8+ ошибок подряд = устройство потеряно: не мучаем канал,
                // сообщаем и прекращаем цикл (мост переподключит контроллер).
                if (++_consecutiveWriteFailures >= 8)
                {
                    IsDead = true;
                    WriteFailed?.Invoke(ex);
                    break;
                }
                // Шторм ошибок при захлёбывании: сообщаем не чаще раза в 5 с —
                // иначе журнал и UI захлёбываются сотней ошибок в секунду.
                if ((DateTime.UtcNow - _lastFailReportUtc).TotalSeconds >= 5)
                {
                    _lastFailReportUtc = DateTime.UtcNow;
                    WriteFailed?.Invoke(ex);
                }
                try { await Task.Delay(200, _cts.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Запись «сырого» отчёта с учётом статистики и отказоустойчивости.</summary>
    private void WriteToDevice(byte[] report)
    {
        var sw = Stopwatch.StartNew();
        _device.Write(report);
        sw.Stop();
        UpdateWriteEma(sw.Elapsed.TotalMilliseconds);
        _consecutiveWriteFailures = 0;

        lock (_lock)
        {
            double interval = _sinceLastWrite.Elapsed.TotalMilliseconds;
            _sinceLastWrite.Restart();
            _intervals.Add(interval);
            if (_intervals.Count > 200) _intervals.RemoveAt(0);
            Stats.AvgIntervalMs = _intervals.Count == 0 ? 0 : _intervals.Average();
            Stats.LastWriteMs = _feedbackAge.Elapsed.TotalMilliseconds;
            Stats.LastWriteUtc = DateTime.UtcNow;
            Interlocked.Add(ref Stats.BytesOut, report.Length);
            Interlocked.Increment(ref Stats.WritesOut);
        }
        Stats.HapticsSummary = $"хаптики: записано {_haptics.Written}, принято {_haptics.Enqueued}, " +
                               $"дублей {_haptics.DroppedDuplicates}, отброшено {_haptics.DroppedOverflow}" +
                               (_haptics.DroppedBackoff > 0 ? $", деградация {_haptics.DroppedBackoff}" : "") +
                               $", мост {_rearBridge.Bridged}/{_rearBridge.Released}";
    }

    private void UpdateWriteEma(double ms)
    {
        // EMA с быстрым ростом и медленным спадом: канал хуже — темп падает сразу,
        // канал лучше — темп восстанавливается осторожно.
        _emaWriteMs = ms > _emaWriteMs
            ? _emaWriteMs * 0.6 + ms * 0.4
            : _emaWriteMs * 0.9 + ms * 0.1;
    }

    private static OutputCommon Clone(OutputCommon c) => OutputCommon.FromRaw(c.Data);

    private static bool CommonEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Сравнение только диапазона байтов [offset, offset+count).</summary>
    private static bool CommonEquals(byte[] a, byte[] b, int offset, int count)
    {
        if (a.Length != b.Length) return false;
        for (int i = offset; i < offset + count && i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _ = timeEndPeriod(1); } catch { }
        _cts.Dispose();
    }
}

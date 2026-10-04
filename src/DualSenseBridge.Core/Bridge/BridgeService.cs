using System.Diagnostics;
using DualSenseBridge.Core.Hid;
using DualSenseBridge.Core.Output;
using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Translation;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core.Bridge;

/// <summary>Сводный статус моста (для UI и CLI).</summary>
public sealed class BridgeStatus
{
    public bool ViiperConnected;
    public string? ViiperVersion;
    public bool VirtualDeviceCreated;
    public bool PhysicalDeviceConnected;
    public string? PhysicalDeviceName;
    public Transport PhysicalTransport = Transport.Unknown;
    public int BatteryPercent = -1;
    public bool HidHideAvailable;
    public bool DeviceHidden;
    public bool IsRunning;
    public string LastError = string.Empty;

    /// <summary>Диагностика отдачи от игры (v1.5.0).</summary>
    public string LastFeedbackSummary = "пока ничего не прислано";
    /// <summary>Тип виртуального устройства (полный дуплекс или только геймпад).</summary>
    public string VirtualDeviceType = "";
    public long FeedbackFrames;
    public DateTime FeedbackLastUtc;
}

/// <summary>
/// Оркестратор моста «физический DualSense (Bluetooth) <-> виртуальный DualSense (VIIPER)».
///
/// Поток данных:
///   ВВОД:    физический DualSense --HID Read (BT 0x31)--> разбор --> wire-формат (33Б)
///            --> VIIPER (TCP :3242, кадр V5 0x01) --> usbip-win2 --> игра/Steam
///   ОТДАЧА:  игра/Steam --> виртуальный DualSense (USB output 0x02) --> VIIPER
///            (кадр V5 0x81, 474Б) --> трансляция (общий блок + патчи безопасности)
///            --> BT output 0x31 (seq + tag 0x10 + CRC32) --> WriteFile --> контроллер.
/// </summary>
public sealed class BridgeService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly HidDeviceMonitor _monitor;
    private Task? _inputLoop;
    private ViiperClient? _viiper;
    private ViiperDeviceStream? _stream;
    private ViiperDeviceHandle? _handle;
    private DualSenseHidDevice? _physical;
    private BluetoothHidWriter? _writer;

    /// <summary>Отдельный медиа-тракт 0x36 (звук/хаптики): пейсер 10,667 мс + асинхронные слоты (v1.10.0).</summary>
    private CarrierPacer? _mediaPacer;

    /// <summary>Медиа-тракт не удалось открыть — не ретраить до переподключения (v1.10.1).</summary>
    private bool _mediaOpenFailed;

    /// <summary>Кодировщик звука динамика (v1.12.0): viiper присылает звук raw-PCM хвостом 0x83.</summary>
    private SpeakerOpusEncoder? _speakerEncoder;

    /// <summary>Последний атомарный кадр 0x83 (часы динамика 100 Гц) — v1.12.0.</summary>
    private DateTime _lastAudioFrameUtc = DateTime.MinValue;
    private readonly FeedbackTranslator _translator = new();
    private readonly FeedbackMonitor _feedbackMonitor = new();
    private readonly OutputMerger _merger = new();
    private DateTime _lastStatusPushUtc = DateTime.MinValue;
    private DateTime _lastReattachUtc = DateTime.MinValue;
    private DateTime _lastDetachUtc = DateTime.MinValue;
    private DualSenseState _lastState = new();
    private readonly object _reconnectLock = new();
    private volatile bool _running;

    public BridgeStatus Status { get; } = new();
    public OutputStats? OutputStats => _writer?.Stats;
    public TranslationMode TranslationMode { get => _translator.Mode; set => _translator.Mode = value; }
    public double RumbleScale { get => _translator.RumbleScale; set => _translator.RumbleScale = Math.Clamp(value, 0, 1); }
    public bool ForcePlayerOneLed { get => _translator.ForcePlayerOneLed; set => _translator.ForcePlayerOneLed = value; }

    /// <summary>Подсветка по умолчанию, пока игра не управляет ею (v1.9.9).</summary>
    public bool DefaultLightbar { get => _translator.DefaultLightbar; set => _translator.DefaultLightbar = value; }

    /// <summary>Цвет подсветки по умолчанию (v1.9.9).</summary>
    public (byte r, byte g, byte b) DefaultLightbarColor
    {
        get => (_translator.DefaultLightbarRed, _translator.DefaultLightbarGreen, _translator.DefaultLightbarBlue);
        set
        {
            _translator.DefaultLightbarRed = value.r;
            _translator.DefaultLightbarGreen = value.g;
            _translator.DefaultLightbarBlue = value.b;
        }
    }

    /// <summary>Усиление хаптик PCM (аудиоканал), 0.25..4.0 (1.0 — как есть).</summary>
    public double HapticsGain { get => _translator.HapticsGain; set => _translator.HapticsGain = Math.Clamp(value, 0.25, 4.0); }

    /// <summary>
    /// Полный дуплекс: виртуальный DualSense создаётся с аудио-интерфейсами
    /// (dualsensecombinedaudioduplexv5 + sinc-конвертер) — игры, передающие
    /// хаптики PCM через аудиоканал (TLOU, Returnal и др. PS-порты), получают
    /// полноценный путь: PCM → viiper ресемплирует 48→3 кГц → BT-отчёт 0x36 →
    /// физический контроллер. Отключение — совместимость со старыми viiper.
    /// </summary>
    public bool FullDuplexAudio { get; set; } = true;
    public TimeSpan MinWriteInterval { get; set; } = TimeSpan.FromMilliseconds(4);
    public ViiperDialect Dialect { get; set; } = ViiperDialect.HbashtonV5;

    public event Action? StatusChanged;

    public BridgeService()
    {
        _monitor = new HidDeviceMonitor(OnDevicesChanged);
        _translator.SafetyPatchApplied += patch => Log.Warn($"[патч безопасности] {patch}");
    }

    // ------------------------------------------------------------------ Запуск/остановка

    /// <summary>Запускает мост. Бросает исключение при критической ошибке.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_running) return;

        // 1. Подключение к VIIPER (сервер должен быть запущен; см. Setup/DriverSetup).
        _viiper = new ViiperClient(dialect: Dialect);
        string? version = null;
        for (int attempt = 1; ; attempt++)
        {
            version = await _viiper.PingAsync(ct);
            if (version is not null) break;
            if (attempt >= 5)
                throw new InvalidOperationException(
                    "VIIPER-сервер недоступен на 127.0.0.1:3242. Запустите viiper.exe (см. README, раздел «Установка»).");
            Log.Warn($"VIIPER ещё не отвечает (попытка {attempt}/5) — вероятно, viiper.exe только что запущен, ждём 2 с...");
            await Task.Delay(2000, ct);
        }
        Status.ViiperConnected = true;
        Status.ViiperVersion = version;
        Log.Info($"VIIPER подключён, версия {version}, диалект {Dialect}");

        // 2. Создание виртуального DualSense и открытие стрима (в течение 5 c после add!).
        _viiper.FullDuplexDeviceType = "dualsensecombinedaudioduplexv5";
        _viiper.HapticsConverter = FullDuplexAudio ? "sony-bt-wdl-sinc64-v1" : null;
        if (!FullDuplexAudio) _viiper.FullDuplexDeviceType = "dualsensegamepadv5";
        _merger.Reset();
        _handle = await _viiper.AddDualSenseAsync(ct);

        // v1.9.1: если переиспользовали устройство прошлой сессии, а стрим к нему
        // не открылся (устройство «осиротело») — удаляем и создаём свежее.
        try
        {
            _stream = await _viiper.OpenStreamAsync(_handle, ct);
        }
        catch (Exception ex) when (_handle.Reused)
        {
            Log.Warn($"Стрим к устройству прошлой сессии не открылся ({ex.Message}) — пересоздаю устройство");
            await _viiper.RemoveDeviceAsync(_handle);
            _handle = await _viiper.AddDeviceAsync(
                FullDuplexAudio ? "dualsensecombinedaudioduplexv5" : "dualsensegamepadv5",
                FullDuplexAudio ? "sony-bt-wdl-sinc64-v1" : null, ct);
            _stream = await _viiper.OpenStreamAsync(_handle, ct);
        }
        _stream.FeedbackReceived += OnFeedback;
        _stream.StreamFailed += (_, ex) =>
        {
            Log.Error("Стрим виртуального устройства разорван", ex);
            Status.VirtualDeviceCreated = false;
            StatusChanged?.Invoke();
        };
        Status.VirtualDeviceCreated = true;
        Status.VirtualDeviceType = FullDuplexAudio ? "dualsensecombinedaudioduplexv5 (аудио+хаптики)" : "dualsensegamepadv5";
        Log.Info($"Виртуальный DualSense создан: шина {_handle.BusId}, устройство {_handle.DevId}, " +
                 $"тип {Status.VirtualDeviceType}");
        StatusChanged?.Invoke();

        // 3. Физический контроллер.
        AttachPhysicalDevice(preferBluetooth: true);

        _running = true;
        Status.IsRunning = true;

        // 4. Планировщик записи (если контроллер уже подключён).
        if (_physical is not null && _writer is null)
        {
            _writer = new BluetoothHidWriter(_physical) { MinInterval = MinWriteInterval };
            _writer.WriteFailed += ex =>
                {
                    Log.Error("Ошибка записи в контроллер", ex);
                    if (_writer is { IsDead: true })
                    {
                        // 5+ ошибок подряд: устройство потеряно — переподключаем в фоне.
                        Task.Run(() => DetachPhysical("канал записи отказал"));
                    }
                };
        }

        _inputLoop = Task.Run(() => InputLoop(_cts.Token));
        StatusChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        _running = false;
        Status.IsRunning = false;
        try { _cts.Cancel(); } catch { }

        try { _mediaPacer?.Dispose(); } catch { }
        _mediaPacer = null;
        _mediaOpenFailed = false;
        _writer?.Dispose();
        _writer = null;

        if (_viiper is not null && _handle is not null)
            await _viiper.RemoveDeviceAsync(_handle);
        _stream?.Dispose();
        _stream = null;
        _handle = null;

        _physical?.Dispose();
        _physical = null;
        Status.PhysicalDeviceConnected = false;
        Status.VirtualDeviceCreated = false;
        StatusChanged?.Invoke();
        Log.Info("Мост остановлен");
    }

    // ------------------------------------------------------------------ Физическое устройство

    /// <summary>
    /// Находит и открывает физический DualSense (BT в приоритете).
    /// Работает и без запущенного моста (используется тестами эффектов).
    /// </summary>
    public DualSenseHidDevice? AttachPhysicalDevice(bool preferBluetooth)
    {
        lock (_reconnectLock)
        {
            if (_physical is { IsOpen: true }) return _physical;
            if (_physical is not null)
            {
                _physical.Dispose();
                _physical = null;
            }

            var devices = DualSenseHidDevice.Enumerate();
            if (devices.Count == 0)
            {
                // Не спамим в журнал при каждом изменении HID-устройств системы.
                if (Status.PhysicalDeviceConnected || _running)
                {
                    var (_, virtualDevices) = DualSenseHidDevice.EnumerateWithVirtuals();
                    if (virtualDevices.Count > 0)
                        Log.Warn($"DualSense не найден; виден только ВИРТУАЛЬНЫЙ usbip-контроллер ({virtualDevices.Count} шт.) — " +
                                 "физический не подключён или скрыт (HidHide без нашего exe в списке разрешённых?). " +
                                 "К виртуальному НЕ подключаемся (иначе мост замкнётся сам на себя)");
                    else
                        Log.Warn("DualSense не найден — ожидание подключения...");
                }
                Status.PhysicalDeviceConnected = false;
                StatusChanged?.Invoke();
                return null;
            }

            // Выбор как в v1.9.1: Bluetooth в приоритете, иначе первый.
            var pick = preferBluetooth
                ? devices.FirstOrDefault(d => d.Transport == Transport.Bluetooth) ?? devices[0]
                : devices[0];

            if (pick.HidDevice is null)
            {
                Log.Warn($"Кандидат без устройства HidSharp: {pick.InstanceId} — пропускаю");
                return null;
            }

            try
            {
                // Диагностика: сразу видно, какое устройство открыто.
                Log.Info($"Открываю контроллер: {pick.InstanceId} | " +
                         $"коллекция input {pick.MaxInputReportLength} Б / output {pick.MaxOutputReportLength} Б | " +
                         $"транспорт по дескриптору: {pick.Transport}");
                var dev = new DualSenseHidDevice(pick.HidDevice, pick);
                dev.Open();
                dev.Initialize(); // внутри уточняет транспорт по первому отчёту
                _physical = dev;

                // Планировщик записи — только при работающем мосте (тесты пишут напрямую).
                if (_running)
                {
                    _writer?.Dispose();
                    _writer = new BluetoothHidWriter(dev) { MinInterval = MinWriteInterval };
                    _writer.WriteFailed += ex =>
                    {
                        Log.Error("Ошибка записи в контроллер", ex);
                        if (_writer is { IsDead: true })
                        {
                            // 5+ ошибок подряд: устройство потеряно — переподключаем в фоне.
                            Task.Run(() => DetachPhysical("канал записи отказал"));
                        }
                    };
                }

                Status.PhysicalDeviceConnected = true;
                Status.PhysicalDeviceName = pick.ProductName;
                Status.PhysicalTransport = dev.Transport;
                Log.Info($"Физический контроллер подключён: {pick.ProductName} [{dev.Transport}] " +
                         $"(output max {dev.MaxOutputReportLength} Б, input max {dev.MaxInputReportLength} Б)");
                var mac = dev.TryReadMacAddress();
                if (mac is not null) Log.Info($"MAC контроллера: {mac}");
                var fw = dev.TryReadFirmwareVersions();
                if (fw is { } v) Log.Info($"Версии: HW 0x{v.hw:X8}, FW 0x{v.fw:X8}");
                StatusChanged?.Invoke();
                return dev;
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось открыть контроллер (занят другим приложением? включён HidHide без разрешения?)", ex);
                Status.PhysicalDeviceConnected = false;
                StatusChanged?.Invoke();
                return null;
            }
        }
    }

    /// <summary>
    /// Отсоединяет физическое устройство (после ошибок записи/чтения);
    /// InputLoop через несколько секунд подключит его заново.
    /// Антишторм: не чаще раза в 8 с (v1.9.0 — цикл disconnect/reconnect
    /// мог уходить в карусель и добивать BT-стек).
    /// </summary>
    public void DetachPhysical(string reason)
    {
        lock (_reconnectLock)
        {
            if ((DateTime.UtcNow - _lastDetachUtc).TotalSeconds < 8) return;
            _lastDetachUtc = DateTime.UtcNow;
            Log.Warn($"Физический контроллер отсоединён ({reason}) — попытка переподключения...");
            try { _mediaPacer?.Dispose(); } catch { }
            _mediaPacer = null;
            _mediaOpenFailed = false;
            try { _writer?.Dispose(); } catch { }
            try { _physical?.Dispose(); } catch { }
            _writer = null;
            _physical = null;
            Status.PhysicalDeviceConnected = false;
        }
        StatusChanged?.Invoke();
    }

    private void OnDevicesChanged()
    {
        // Реакция на подключение/отключение HID-устройств: (пере)подключаем контроллер.
        lock (_reconnectLock)
        {
            if (_physical is { Connected: true, IsOpen: true }) return;
        }
        Log.Info("Обнаружено изменение HID-устройств — попытка переподключения...");
        AttachPhysicalDevice(preferBluetooth: true);
    }

    // ------------------------------------------------------------------ Циклы данных

    /// <summary>
    /// Цикл ввода: читает input-отчёты физического контроллера и пересылает их
    /// в виртуальный. Минимальные BT-отчёты 0x01 (до переключения в 0x31)
    /// пересылаются тоже, чтобы ввод не пропадал.
    /// </summary>
    private async void InputLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DualSenseHidDevice? device = _physical;
                if (device is null || !device.IsOpen)
                {
                    await Task.Delay(300, ct);
                    // Автопереподключение (не чаще раза в 3 с): enumeration HID недешёв.
                    if (_running && (DateTime.UtcNow - _lastReattachUtc).TotalSeconds >= 5)
                    {
                        _lastReattachUtc = DateTime.UtcNow;
                        AttachPhysicalDevice(preferBluetooth: true);
                    }
                    continue;
                }

                byte[]? report = device.Read();
                if (report is null)
                    continue;

                if (DualSenseInputParser.TryParse(report, device.Transport, _lastState, out var state))
                {
                    _lastState = state;

                    // Батарея — обновляем статус.
                    if (!state.IsMinimalReport)
                    {
                        int battery = state.BatteryPercent;
                        if (battery != Status.BatteryPercent)
                        {
                            Status.BatteryPercent = battery;
                            if (state.BatteryError)
                                Log.Warn($"Контроллер сообщил об ошибке батареи/заряда (status0=0x{state.Status0:X2})");
                            else
                                Log.Info($"Батарея: {battery}%{(state.IsCharging ? " (заряжается)" : "")}");
                            StatusChanged?.Invoke();
                        }
                    }

                    // Пересылка ввода в виртуальное устройство.
                    ViiperDeviceStream? stream = _stream;
                    if (stream is not null)
                    {
                        var wire = InputMapper.Map(state);
                        await stream.SendInputAsync(wire, ct);
                    }
                }
                else
                {
                    Log.Debug($"Неизвестный input-отчёт: id=0x{report[0]:X2}, длина {report.Length}");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                Log.Error("Физический контроллер отключён, ожидание переподключения", ex);
                _physical?.Dispose();
                _physical = null;
                _writer?.Dispose();
                _writer = null;
                Status.PhysicalDeviceConnected = false;
                StatusChanged?.Invoke();
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка цикла ввода", ex);
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>
    /// Обратная связь от игры (кадр V5 0x81/0x83): диагностика, аудиоканал 0x36,
    /// трансляция и постановка в очередь записи.
    /// </summary>
    /// <summary>
    /// Пытается включить отдельный асинхронный медиа-тракт 0x36 (v1.10.1: один
    /// раз на подключение; неудача видна в статусе и журнале с кодом Win32).
    /// </summary>
    private void TryStartMediaTract(string devicePath, BluetoothHidWriter writer)
    {
        try
        {
            var mw = new BtMediaWriter(devicePath);
            if (mw.IsOpen)
            {
                _mediaPacer?.Dispose();
                _mediaPacer = new CarrierPacer(mw);
                writer.Stats.MediaMode = $"медиа-тракт: асинхронный пейсер {CarrierPacer.CadenceMs:F3} мс";
                Log.Info($"Медиа-тракт 0x36 активен: каденция {CarrierPacer.CadenceMs:F3} мс, " +
                         $"заряд {CarrierPacer.PrimeCount}, асинхронные слоты");
                return;
            }
            int err = mw.LastOpenError;
            mw.Dispose();
            _mediaOpenFailed = true;
            writer.Stats.MediaMode = $"медиа-тракт: НЕ открыт (Win32 {err}) — синхронный резерв";
            Log.Warn($"Медиа-тракт 0x36 не открыт (Win32 {err}) — носители пойдут через синхронный канал");
        }
        catch (Exception ex)
        {
            _mediaOpenFailed = true;
            writer.Stats.MediaMode = "медиа-тракт: не открыт — синхронный резерв";
            Log.Warn($"Медиа-тракт 0x36 не открыт ({ex.Message}) — носители пойдут через синхронный канал");
        }
    }

    private void OnFeedback(object? sender, FeedbackReceivedEventArgs e)
    {
        try
        {
            BluetoothHidWriter? writer = _writer;
            var device = _physical;

            // Диагностика: счётчики всегда, сводка/журнал — по троттлингу (внутри).
            string? changed = _feedbackMonitor.Register(e.Feedback);
            Status.LastFeedbackSummary = _feedbackMonitor.LastSummary;
            Status.FeedbackFrames = _feedbackMonitor.Frames;
            Status.FeedbackLastUtc = _feedbackMonitor.LastFrameUtc;
            if (changed is not null)
                Log.Info($"Отдача от игры: {changed}");

            if ((DateTime.UtcNow - _lastStatusPushUtc).TotalMilliseconds >= 500)
            {
                _lastStatusPushUtc = DateTime.UtcNow;
                StatusChanged?.Invoke();
            }

            // Сводка медиа-тракта (v1.10.0) — сразу видно здоровье аудиоканала.
            var pacerForStats = _mediaPacer;
            if (pacerForStats is not null && writer is not null)
                writer.UpdateMediaSummary(pacerForStats.Describe());
            var speakerForStats = _speakerEncoder;
            if (speakerForStats is not null && writer is not null && speakerForStats.Frames > 0)
                writer.Stats.MediaMode += $" | динамик Opus: {speakerForStats.Frames} кадров";

            if (writer is null || device is null) return;

            // v1.10.1: медиа-тракт включается ЛЕНИВО, на первом же носителе 0x36.
            if (_mediaPacer is null && !_mediaOpenFailed)
                TryStartMediaTract(device.Info.DevicePath, writer);

            // ---- Путь 1: медиаканал 0x36 (звук динамика + хаптики PCM + состояние).
            // v1.12.0 — ГЛАВНОЕ: viiper НЕ кодирует звук динамика (дорожка Opus в
            // носителе ПУСТАЯ), а несёт его отдельным PCM-хвостом кадра 0x83
            // (1920 Б = 10 мс, 48 кГц стерео). Кодируем Opus (160 кбит/с CBR,
            // контракт DS4Windows) и вписываем дорожку в носитель САМИ.
            if (e.SpeakerPcm is { } pcm && e.Feedback.HasCombinedCarrier)
            {
                _speakerEncoder ??= new SpeakerOpusEncoder();
                _speakerEncoder.EncodeInto(e.Feedback.CombinedBtCarrier, pcm);
            }

            // Выбор дорожки (v1.12.0): 0x83 — ОСНОВНОЙ медиапоток (динамик+
            // состояние+хаптики, 100 Гц), пишем ВСЕГДА: старое правило «пропускать
            // 0x83, пока идут 0x84» РЕЗАЛО ЗВУК при любой хаптик-активности —
            // это и были щелчки. 0x84 (realtime-хаптики, 93,75 Гц) — только
            // запасной поток, когда аудиокадров нет (аудиоинтерфейс закрыт):
            // интервал хаптик в нём дублирует следующий 0x83.
            if (e.FrameType == V5Frame.TypeFeedbackAudio)
                _lastAudioFrameUtc = DateTime.UtcNow;

            bool useCarrier =
                e.Feedback.HasCombinedCarrier &&
                !e.Feedback.IsCarrierSilent &&
                device.Transport == Transport.Bluetooth &&
                (e.FrameType == V5Frame.TypeFeedbackAudio ||
                 DateTime.UtcNow - _lastAudioFrameUtc > TimeSpan.FromMilliseconds(150));

            if (useCarrier)
            {
                var block = _translator.PatchCarrier(e.Feedback.CombinedBtCarrier);
                if (_mediaPacer is { } pacer)
                    pacer.Enqueue(block, e.FrameType); // отдельный тракт: каденция + слоты
                else
                    writer.EnqueueCarrier(block, e.RawPayload.Length, e.FrameType); // fallback
            }
            else if (e.Feedback.HasCombinedCarrier && e.Feedback.IsCarrierSilent &&
                     device.Transport == Transport.Bluetooth)
            {
                // Игра ЯВНО опубликовала тишину (тихий носитель) — гасим хаптики её
                // сигналом, а не таймаутом: бурсты viiper длиннее старого таймаута
                // 45 мс, таймаутное глушение резало эффекты на части («жужжание»).
                if (_mediaPacer is { } pacerSilent) pacerSilent.NotifyGameSilence();
                else writer.NotifyGameSilence();
            }

            // ---- Путь 2: состояние 0x31 (слияние по группам полей + патчи).
            // Пишется только когда очередь 0x36 пуста (состояние едет внутри 0x36).
            OutputCommon? common;
            if (_translator.Mode == TranslationMode.Native &&
                e.Feedback.UsbOutputReport.Length == DualSenseProtocol.OutputReportUsbSize &&
                e.Feedback.UsbOutputReport[0] == DualSenseProtocol.OutputReportUsb)
            {
                _merger.Apply(e.Feedback.UsbOutputReport.AsSpan(1, DualSenseProtocol.OutputCommonSize));
                common = _translator.TranslateMerged(_merger.Current);
            }
            else
            {
                common = _translator.Translate(e.Feedback);
            }
            if (common is not null)
                writer.Enqueue(common, e.RawPayload.Length);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка трансляции обратной связи", ex);
        }
    }

    // ------------------------------------------------------------------ Тестовые команды

    /// <summary>Тестовая вибрация (пульс 400 мс).</summary>
    public async Task TestRumbleAsync(byte right = 0xC0, byte left = 0xFF)
    {
        var c = new OutputCommon().SetRumble(right, left);
        SendTestCommon(c);
        await Task.Delay(400);
        SendTestCommon(new OutputCommon().SetRumble(0, 0));
    }

    /// <summary>Тест эффекта триггера (левый/правый).</summary>
    public async Task TestTriggerAsync(bool left, byte mode /* 0x21 feedback, 0x25 weapon, 0x26 vibration */)
    {
        var block = new byte[TriggerEffect.BlockSize];
        switch (mode)
        {
            case 0x25: TriggerEffect.Weapon(block, 3, 7, 7); break;
            case 0x26: TriggerEffect.Vibration(block, 2, 6, 30); break;
            default: TriggerEffect.Feedback(block, 3, 7); break;
        }

        var c = new OutputCommon();
        if (left) c.SetTriggerLeft(block); else c.SetTriggerRight(block);
        SendTestCommon(c);
        await Task.Delay(2500);
        var off = new OutputCommon();
        var offBlock = TriggerEffect.Off();
        if (left) off.SetTriggerLeft(offBlock); else off.SetTriggerRight(offBlock);
        SendTestCommon(off);
    }

    /// <summary>Тест подсветки (указанный RGB).</summary>
    public async Task TestLightbarAsync(byte r, byte g, byte b)
    {
        SendTestCommon(new OutputCommon().SetLightbar(r, g, b));
        await Task.Delay(1500);
        SendTestCommon(new OutputCommon().SetLightbar(0, 0, 64)); // мягкий синий после теста
    }

    /// <summary>Тест player-LED (все 5).</summary>
    public async Task TestPlayerLedsAsync()
    {
        SendTestCommon(new OutputCommon().SetPlayerLeds(0x1F));
        await Task.Delay(1500);
        SendTestCommon(new OutputCommon().SetPlayerLeds(0x04)); // один по центру
    }

    private void SendTestCommon(OutputCommon common)
    {
        try
        {
            var writer = _writer;
            var device = _physical;
            if (writer is not null && device is { IsOpen: true })
            {
                writer.Enqueue(common, 0); // быстрый путь: очередь писателя
                return;
            }
            if (device is { IsOpen: true })
            {
                device.WriteCommon(common);
                return;
            }
            // Подключение контроллера (открытие BT может занять секунды) — в фоне,
            // чтобы окно не «замерало» (v1.6.0).
            Task.Run(() =>
            {
                try
                {
                    var dev = AttachPhysicalDevice(preferBluetooth: true); // работает и без запущенного моста
                    if (dev is { IsOpen: true }) dev.WriteCommon(common);
                    else Log.Warn("Тест не выполнен: контроллер DualSense не найден. " +
                                  "Подключите его по Bluetooth или USB и повторите (подробности: DualSenseBridge.exe --list-devices).");
                }
                catch (Exception ex) { Log.Error("Тест не выполнен", ex); }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Тест не выполнен", ex);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _monitor.Dispose();
        try { _mediaPacer?.Dispose(); } catch { }
        _mediaPacer = null;
        _mediaOpenFailed = false;
        _writer?.Dispose();
        _stream?.Dispose();
        _physical?.Dispose();
        _viiper?.Dispose();
        try { StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}

using System.Runtime.InteropServices;
using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core.Output;

/// <summary>
/// Асинхронный писатель медиа-отчётов 0x36 (398 Б) в Bluetooth-контроллер —
/// ОТДЕЛЬНЫЙ тракт для звука/хаптик (v1.10.0).
///
/// Зачем: Windows HID не показывает кредиты L2CAP, и завершения записей
/// приходят ПАЧКАМИ с засухами 39–82 мс (подтверждено референс-реализацией
/// hbashton/DS4Windows, DualSenseBluetoothAudioPacer: «normal 39-82 ms
/// completion droughts do not starve the controller»). Синхронная запись
/// (HidSharp.Write) в эти засухи глохнет — контроллер недополучает кадры
/// Opus-аудио → хрипы и щелчки. Решение — несколько отчётов «в полёте»
/// (overlapped WriteFile без ожидания), строго по кольцу: новый отчёт не
/// обгоняет старейший слот.
/// </summary>
public sealed class BtMediaWriter : IDisposable
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint WaitObject0 = 0, WaitTimeout = 0x102;
    private const int ErrorIoPending = 997;

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public IntPtr Event;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, IntPtr lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle? _handle;
    private readonly IntPtr[] _ov;         // нативная OVERLAPPED на слот
    private readonly IntPtr[] _events;     // событие на слот (auto-reset)
    private readonly GCHandle[] _pins;     // закреплённые буферы
    private readonly byte[][] _buffers;    // 398 Б на слот
    private readonly bool[] _busy;
    private readonly int _slotCount;

    public long Submitted;
    public long Completed;
    public long Failed;
    public long TimedOut;

    /// <summary>Код ошибки Win32 при неудаче открытия (0 — успех). Для диагностики (v1.10.1).</summary>
    public int LastOpenError { get; }

    public bool IsOpen => _handle is not null && !_handle.IsInvalid;

    /// <summary>Открывает устройство (overlapped) и готовит кольцо слотов.</summary>
    public BtMediaWriter(string devicePath, int slots = 8)
    {
        _slotCount = Math.Clamp(slots, 2, 16);
        _handle = CreateFileW(devicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        LastOpenError = _handle is null || _handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (!IsOpen) return;

        _ov = new IntPtr[_slotCount];
        _events = new IntPtr[_slotCount];
        _pins = new GCHandle[_slotCount];
        _buffers = new byte[_slotCount][];
        _busy = new bool[_slotCount];
        for (int i = 0; i < _slotCount; i++)
        {
            _buffers[i] = new byte[DualSenseProtocol.CombinedBtReportSize];
            _pins[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);
            _events[i] = CreateEventW(IntPtr.Zero, false, false, null);
            _ov[i] = Marshal.AllocHGlobal(Marshal.SizeOf<OVERLAPPED>());
            Marshal.StructureToPtr(new OVERLAPPED(), _ov[i], false);
        }
    }

    /// <summary>Собирает завершённые слоты (без блокировки). Возвращает число освободившихся.</summary>
    public int Reap()
    {
        int freed = 0;
        for (int i = 0; i < _slotCount; i++)
        {
            if (!_busy[i]) continue;
            if (WaitForSingleObject(_events[i], 0) != WaitObject0) continue; // ещё в полёте
            if (GetOverlappedResult(_handle!, _ov[i], out _, false)) Completed++;
            else Failed++;
            _busy[i] = false;
            freed++;
        }
        return freed;
    }

    /// <summary>Есть свободный слот?</summary>
    public bool HasFreeSlot()
    {
        Reap();
        for (int i = 0; i < _slotCount; i++)
            if (!_busy[i]) return true;
        return false;
    }

    /// <summary>Ждёт освобождения любого слота (занятые не перестраиваются — порядок строгий).</summary>
    public bool WaitForSlot(int timeoutMs)
    {
        if (HasFreeSlot()) return true;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int remain = (int)Math.Max(0, deadline - Environment.TickCount64);
            // ждём любое из событий слотов
            for (int i = 0; i < _slotCount; i++)
            {
                if (!_busy[i]) continue;
                if (WaitForSingleObject(_events[i], (uint)Math.Min(remain, 50)) == WaitObject0)
                {
                    Reap();
                    if (HasFreeSlot()) return true;
                }
            }
        }
        Reap();
        return HasFreeSlot();
    }

    /// <summary>
    /// Отправляет отчёт 0x36 (ровно 398 Б) БЕЗ ожидания завершения. false — все
    /// слоты заняты (нужно WaitForSlot) или тракт закрыт.
    /// </summary>
    public bool TrySubmit(byte[] report)
    {
        if (!IsOpen) return false;
        if (report.Length != DualSenseProtocol.CombinedBtReportSize)
            throw new ArgumentException($"медиа-отчёт обязан быть {DualSenseProtocol.CombinedBtReportSize} Б", nameof(report));
        Reap();
        for (int i = 0; i < _slotCount; i++)
        {
            if (_busy[i]) continue;
            Array.Copy(report, _buffers[i], report.Length);
            Marshal.StructureToPtr(new OVERLAPPED { Event = _events[i] }, _ov[i], false);
            if (WriteFile(_handle!, _buffers[i], (uint)report.Length, out _, _ov[i]))
            {
                Completed++; // завершилось синхронно
                return true;
            }
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorIoPending)
            {
                _busy[i] = true;
                Submitted++;
                return true;
            }
            Failed++;
            return true; // слот свободен; ошибка зафиксирована (например, отвал устройства)
        }
        return false;
    }

    public void Dispose()
    {
        if (_handle is not null && !_handle.IsInvalid)
        {
            try { CancelIoEx(_handle, IntPtr.Zero); } catch { }
            // даём отменённым IRP мгновение на завершение
            for (int i = 0; i < _slotCount; i++)
            {
                if (_busy[i]) WaitForSingleObject(_events[i], 100);
            }
        }
        for (int i = 0; i < _slotCount; i++)
        {
            if (_pins != null && _pins[i].IsAllocated) _pins[i].Free();
            if (_events != null && _events[i] != IntPtr.Zero) CloseHandle(_events[i]);
            if (_ov != null && _ov[i] != IntPtr.Zero) Marshal.FreeHGlobal(_ov[i]);
        }
        _handle?.Dispose();
    }
}

/// <summary>
/// Пейсер медиа-канала 0x36: ФИКСИРОВАННАЯ нативная каденция 10,667 мс (93,75 Гц),
/// стартовый «заряд» и перепрошивка sequence-номеров (v1.10.0, настройка v1.10.1).
///
/// Референс (hbashton/DS4Windows, DualSenseBluetoothAudioPacer):
///  - «one complete speaker/haptics generation per 10.667 ms» — каденция не
///    подстраивается под arrival-джиттер TCP (EMA-подход v1.9.9 качался от
///    пачек viiper и сам портил ритм);
///  - «V5 arms its native media writer with eight complete reports» — стартовый
///    заряд заполняет резерв контроллера;
///  - «The game, not a wall-clock timeout..., owns the end of a native DualSense
///    effect by publishing an explicit silent haptics block» — конец эффекта
///    принадлежит ИГРЕ: гасим хаптики её явной тишиной, а не таймаутом.
///
/// v1.10.1 (по результатам эксплуатации):
///  - заряд 8 → 3 и ТОЛЬКО на первый поток соединения: 8 × 10,667 = 85 мс
///    лишней задержки, а повторные заряды на каждый бурст давали «лестницу»
///    вибрации и дёрганое ощущение (жужжание). Запас на засухи завершений
///    дают 8 слотов «в полёте», а не глубина буфера контроллера;
///  - досрочный слив: если очередь глубже CatchUpDepth — внеочередной кадр
///    (сетку не двигаем), очередь сходится к ≤4 (~43 мс) без потерь;
///  - «немой» блок: ПЕРВИЧНО по явной тишине игры (NotifyGameSilence), таймаут
///    250 мс — только фолбэк смерти производителя. Раньше таймаут 45 мс рубил
///    эффекты между бурстами viiper (средний интервал бурстов ~63 мс) —
///    это и было «жужжание вместо вибрации».
/// </summary>
public sealed class CarrierPacer : IDisposable
{
    /// <summary>Нативная каденция 0x36 (93,75 Гц) — по трассам референса.</summary>
    public const double CadenceMs = 10.667;

    /// <summary>
    /// Стартовый заряд отчётами — только на ПЕРВЫЙ поток соединения. Референс
    /// заряжает 8; v1.10.1 — 3 (низкая задержка по требованию пользователя).
    /// </summary>
    public const int PrimeCount = 3;

    /// <summary>Глубина очереди, при превышении которой пейсер сливает внеочередной кадр (догоняет очередь).</summary>
    public const int CatchUpDepth = 4;

    /// <summary>Ёмкость очереди (FIFO, drop-oldest).</summary>
    public const int QueueCapacity = 16;

    /// <summary>Фолбэк тишины: «немой» блок по таймауту, если производитель умер, не прислав явную тишину.</summary>
    public static readonly TimeSpan MuteFailsafeAfter = TimeSpan.FromMilliseconds(250);

    private readonly BtMediaWriter _writer;
    private readonly HapticsChannel _queue = new(capacity: QueueCapacity);
    private readonly RearLaneBridge _rearBridge = new(); // v1.11.0: мост zero-fill хаптик
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _loop;
    private readonly object _wakeLock = new();

    private byte _seq;
    private int _primeRemaining;
    private bool _primedOnce;                    // заряд выполняется один раз на соединение
    private DateTime _nextTickUtc;
    private DateTime _lastArrivalUtc = DateTime.MinValue;
    private bool _muteFlushed = true;
    private bool _streamActive;
    private volatile bool _gameSilenceNotified;  // игра прислала явную тишину

    public long Resequenced;
    public long Mutes;
    public long DroppedNoSlot;

    public CarrierPacer(BtMediaWriter writer)
    {
        _writer = writer;
        _loop = new Thread(PacerLoop) { IsBackground = true, Name = "CarrierPacer" };
        _loop.Start();
    }

    /// <summary>Ставит носитель 0x36 в очередь (дедупликация пар 0x83+0x84 — внутри канала).</summary>
    public void Enqueue(byte[] carrier, byte frameType)
    {
        if (_cts.IsCancellationRequested) return;
        bool firstAfterIdle;
        lock (_wakeLock)
        {
            firstAfterIdle = !_streamActive;
        }
        if (!_queue.Enqueue(carrier, frameType)) return;

        lock (_wakeLock)
        {
            _lastArrivalUtc = DateTime.UtcNow;
            _muteFlushed = false;
            _gameSilenceNotified = false;
            if (firstAfterIdle)
            {
                _streamActive = true;
                if (!_primedOnce)
                {
                    // Первый поток соединения: заряд — буфер контроллера прогревается,
                    // слоты «в полёте» дальше работают сами.
                    _primedOnce = true;
                    _primeRemaining = PrimeCount;
                    _nextTickUtc = DateTime.UtcNow; // заряд идёт сразу по мере поступления
                }
                else
                {
                    // Повторный поток: буфер уже прогрет — сразу ровная сетка.
                    // Повторный заряд = +N×10,667 мс задержки и «лестница» вибрации.
                    _primeRemaining = 0;
                    _nextTickUtc = DateTime.UtcNow.AddMilliseconds(CadenceMs);
                }
            }
            System.Threading.Monitor.Pulse(_wakeLock);
        }
    }

    /// <summary>
    /// Игра явно опубликовала тишину (тихий носитель 0x36) — гасим хаптики ЕЁ
    /// сигналом, а не таймаутом (v1.10.1: таймаут 45 мс рубил эффекты между
    /// бурстами viiper — «жужжание вместо вибрации»).
    /// </summary>
    public void NotifyGameSilence()
    {
        _gameSilenceNotified = true;
        lock (_wakeLock) { System.Threading.Monitor.Pulse(_wakeLock); }
    }

    private void PacerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            byte[]? block = null;
            bool mute = false;

            lock (_wakeLock)
            {
                while (_queue.Count == 0 && !_cts.IsCancellationRequested)
                {
                    // Конец потока: ОДИН «немой» блок. Первичный сигнал — явная
                    // тишина от игры; таймаут — только фолбэк (нативно конец
                    // эффекта принадлежит игре, см. комментарий класса).
                    bool gameSilence = _gameSilenceNotified;
                    bool timedOut = _lastArrivalUtc != DateTime.MinValue &&
                        DateTime.UtcNow - _lastArrivalUtc >= MuteFailsafeAfter;
                    if (!_muteFlushed && (gameSilence || timedOut))
                    {
                        var m = _queue.BuildMuteBlock();
                        if (m is not null)
                        {
                            mute = true;
                            block = m;
                        }
                        _muteFlushed = true;
                        _gameSilenceNotified = false;
                        _streamActive = false;
                        break;
                    }
                    if (gameSilence) _gameSilenceNotified = false; // уже замолчали — сигнал погашен
                    System.Threading.Monitor.Wait(_wakeLock, 20);
                }
                if (block is null && !mute)
                {
                    var now = DateTime.UtcNow;
                    bool regularDue = now >= _nextTickUtc;
                    bool catchUp = _queue.Count > CatchUpDepth; // накопленную очередь догоняем вне сетки
                    if (_primeRemaining > 0 || regularDue || catchUp)
                    {
                        block = _queue.TryDequeue();
                        if (block is not null)
                        {
                            if (_primeRemaining > 0)
                            {
                                _primeRemaining--;
                                if (_primeRemaining == 0)
                                    _nextTickUtc = now.AddMilliseconds(CadenceMs); // сетка стартует после заряда
                            }
                            else if (regularDue)
                            {
                                _nextTickUtc = _nextTickUtc.AddMilliseconds(CadenceMs);
                            }
                            // catch-up: сетку НЕ двигаем — внеочередной слив излишка
                            if (_queue.Count == 0) _streamActive = false;
                        }
                    }
                    if (block is null)
                    {
                        // ждём тик каденции (2 мс — при timeBeginPeriod(1) достаточно)
                        System.Threading.Monitor.Wait(_wakeLock, 2);
                        continue;
                    }
                }
            }

            // v1.11.0: нулевые «zero-fill» дорожки (нет нового интервала хаптик)
            // мостятся последним реальным блоком — иначе вибрация «жужжит».
            // «Немой» блок (явная тишина) не мостится.
            if (!mute) _rearBridge.Bridge(block!, DateTime.UtcNow);

            var report = Resequence(block!, _seq++);
            Resequenced++;

            if (!_writer.TrySubmit(report))
            {
                // Все слоты в полёте — ждём старейший (порядок не перестраиваем)
                if (!_writer.WaitForSlot(250))
                {
                    DroppedNoSlot++; // канал захлёбывается: теряем НОВЕЙШИЙ кадр (протокольной дыры нет — seq наш)
                }
                else
                {
                    _writer.TrySubmit(report);
                }
            }
            if (mute) Mutes++;
        }
    }

    /// <summary>
    /// Перепрошивает sequence (старший ниббл байта 1, 0..15 по кольцу; младший
    /// ниббл — тип кадра — сохраняется) и пересчитывает CRC32 носителя.
    /// Контроллер видит непрерывную нумерацию — внутренние потери не щёлкают.
    /// </summary>
    public static byte[] Resequence(byte[] carrier, byte seq)
    {
        carrier[1] = (byte)(((seq & 0x0F) << 4) | (carrier[1] & 0x0F));
        uint crc = Crc32.Update(DualSenseFeedback.CarrierCrcSeed,
            carrier.AsSpan(0, DualSenseFeedback.CarrierCrcOffset));
        carrier[DualSenseFeedback.CarrierCrcOffset + 0] = (byte)(crc & 0xFF);
        carrier[DualSenseFeedback.CarrierCrcOffset + 1] = (byte)((crc >> 8) & 0xFF);
        carrier[DualSenseFeedback.CarrierCrcOffset + 2] = (byte)((crc >> 16) & 0xFF);
        carrier[DualSenseFeedback.CarrierCrcOffset + 3] = (byte)((crc >> 24) & 0xFF);
        return carrier;
    }

    /// <summary>Сводка для UI/журнала.</summary>
    public string Describe() =>
        $"медиа-тракт: отправлено {_writer.Submitted + _writer.Completed}, " +
        $"завершено {_writer.Completed}, сбоев {_writer.Failed + _writer.TimedOut}, " +
        $"очередь {_queue.Count}/{QueueCapacity} (~{_queue.Count * CadenceMs:F0} мс), без слота {DroppedNoSlot}, " +
        $"мост хаптик {_rearBridge.Bridged}/{_rearBridge.Released}";

    /// <summary>Глубина очереди (для диагностики).</summary>
    public int QueueDepth { get { lock (_wakeLock) return _queue.Count; } }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_wakeLock) { System.Threading.Monitor.Pulse(_wakeLock); }
        try { _loop.Join(500); } catch { }
        _writer.Dispose();
        _cts.Dispose();
    }
}

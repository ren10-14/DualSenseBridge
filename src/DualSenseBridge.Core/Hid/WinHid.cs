using System.Runtime.InteropServices;
using System.Text;
using DualSenseBridge.Core.Protocol;

namespace DualSenseBridge.Core.Hid;

/// <summary>
/// Нативный (P/Invoke) слой HID для Windows (v1.9.2).
///
/// ПОЧЕМУ НЕ HidSharp: перечисление HID в Windows требует ОТКРЫТИЯ каждого
/// устройства (CreateFile + HidD_* — 30–80 открытий на проход). Когда в системе
/// есть виртуальный DualSense (usbip-win2) и ИГРА грузит его потоком, запросы
/// строк/дескрипторов к его драйверу зависают НАВСЕГДА (у user-mode нет таймаута
/// на застрявший IRP) — приложение «не запускалось», если игра уже работала.
///
/// Решение (как в WSGM issue #88 и практиках hidapi): перечислять ТОЛЬКО по
/// строкам путей интерфейсов через CM_Get_Device_Interface_List (чистый
/// конфиг-менеджер, БЕЗ открытий устройств), фильтровать по vid/pid в пути,
/// определять шину по РОДИТЕЛЮ девайса (BTHENUM = Bluetooth, USB = USB,
/// USBIP = виртуальный — пропускать), и открывать ТОЛЬКО выбранное физическое
/// устройство. Виртуальный usbip-DualSense никогда не открывается.
/// </summary>
public static class WinHid
{
    /// <summary>GUID класса интерфейсов HID (HID_DEVICE_INTERFACE_GUID).</summary>
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private const uint CmGetDeviceInterfaceListPresent = 0; // CM_GET_DEVICE_INTERFACE_LIST_PRESENT
    private const uint CmLocateDevnodeNormal = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint ulLen, ref Guid interfaceClassGuid, IntPtr pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, IntPtr pDeviceID, [Out] char[] buffer, uint ulLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder deviceId, uint bufferLen, uint ulFlags);

    // ------------------------------------------------------------------ Чистые парсеры (тестируемые)

    /// <summary>Парсит vid_/pid_ из пути интерфейса: \\?\hid#vid_054c&pid_0ce6&amp;col01#... → (0x054C, 0x0CE6).</summary>
    public static (ushort vid, ushort pid)? TryParseVidPid(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int i = path.IndexOf("vid_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 8 > path.Length) return null;
        if (!ushort.TryParse(path.AsSpan(i + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out ushort vid)) return null;
        int j = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (j < 0 || j + 8 > path.Length) return null;
        if (!ushort.TryParse(path.AsSpan(j + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out ushort pid)) return null;
        return (vid, pid);
    }

    /// <summary>Путь интерфейса → instance ID: \\?\hid#vid_...&amp;col01\8&amp;...&amp;0000#{guid} → HID\VID_...\8&amp;...&amp;0000.</summary>
    public static string InstanceIdFromPath(string path)
    {
        string s = path.StartsWith(@"\\?\") ? path[4..] : path;
        int brace = s.IndexOf("#{", StringComparison.Ordinal);
        if (brace >= 0) s = s[..brace];
        // разделитель интерфейсного пути '#' в instance ID — обратный слэш
        return s.Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>
    /// ID родительского девайса → (транспорт, виртуальный-usbip?).
    /// BTHENUM\ / BTHLE\ — Bluetooth; USB\ — физический USB; USBIP\ — виртуальное
    /// устройство usbip-win2 (наш мост) — НЕ открывать и НЕ трогать.
    /// ВНИМАНИЕ: у usbip-win2 подключённое устройство выглядит как обычное
    /// «USB\VID_...» — виртуальность видна только ВЫШЕ по цепочке родителей
    /// (vhub → vHCI «USBIPWIN\vhci» / «root\vhci_ude»), поэтому одного префикса
    /// недостаточно — см. <see cref="IsVirtualUsbipChain"/>.
    /// </summary>
    public static (Transport transport, bool isUsbip) ClassifyParent(string? parentId)
    {
        if (string.IsNullOrEmpty(parentId)) return (Transport.Unknown, false);
        string p = parentId;
        if (p.StartsWith("USBIP", StringComparison.OrdinalIgnoreCase)) return (Transport.Unknown, true);
        if (p.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
            return (Transport.Bluetooth, false);
        if (p.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return (Transport.Usb, false);
        return (Transport.Unknown, false);
    }

    /// <summary>
    /// Цепочка предков девайса (родитель, дед, …) — до maxLevels уровней вверх.
    /// Только конфиг-менеджер, без открытий устройств.
    /// v1.9.7: 6 → 12 уровней — у виртуального usbip-устройства цепочка длиннее
    /// (HID-коллекция → USB-интерфейс MI_xx → USB-устройство → порт → ROOT_HUB30 →
    /// vHCI «ROOT\USBIP_WIN2\UDE»), и 6 уровней на реальной системе не хватило.
    /// </summary>
    public static List<string> GetAncestorDeviceIds(string instanceId, int maxLevels = 12)
    {
        var chain = new List<string>();
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(instanceId)) return chain;
        try
        {
            if (CM_Locate_DevNodeW(out uint dev, instanceId, CmLocateDevnodeNormal) != 0) return chain;
            for (int i = 0; i < maxLevels; i++)
            {
                if (CM_Get_Parent(out uint parent, dev, 0) != 0) break;
                var sb = new StringBuilder(512);
                if (CM_Get_Device_IDW(parent, sb, 512, 0) != 0) break;
                string id = sb.ToString();
                if (string.IsNullOrEmpty(id)) break;
                chain.Add(id);
                dev = parent;
            }
        }
        catch { }
        return chain;
    }

    /// <summary>
    /// Чистая (тестируемая) проверка: указывает ли цепочка предков на виртуальный
    /// USB-стек usbip-win. Маркеры (по INF и исходникам cezanne/usbip-win):
    ///   «USBIPWIN\vhci»  — корневой vHCI (WDM-вариант);
    ///   «root\vhci_ude» / «…VHCI…» — корневой vHCI (UDE-вариант, 0.9.x);
    ///   «USBIP\…»        — серийники некоторых сборок;
    ///   «VID_1209&PID_8250» — виртуальный хаб usbip-win (vhub).
    /// Физические цепочки (BTHENUM→BTH, USB→хаб→PCI) маркеров не содержат.
    /// </summary>
    public static bool IsVirtualUsbipChain(IEnumerable<string?> ancestorIds)
    {
        foreach (var id in ancestorIds)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (id.Contains("USBIP", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("VHCI", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("VID_1209&PID_8250", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Виртуальное usbip-устройство? (по цепочке предков).</summary>
    public static bool IsVirtualUsbip(string instanceId) => IsVirtualUsbipChain(GetAncestorDeviceIds(instanceId));

    /// <summary>
    /// Транспорт по размерам отчётов коллекции (caps читаются из закэшированных
    /// данных hidclass БЕЗ обращения к драйверу): BT-дескриптор DualSense объявляет
    /// максимум input 78 Б (0x31), USB — 64 Б (0x01). Надёжнее строки пути.
    /// </summary>
    public static Transport ClassifyByCaps(int inputCap, int outputCap)
    {
        if (inputCap == DualSenseProtocol.InputReportBtSize) return Transport.Bluetooth;
        if (inputCap == DualSenseProtocol.InputReportUsbSize) return Transport.Usb;
        return Transport.Unknown;
    }

    // ------------------------------------------------------------------ Перечисление (БЕЗ открытий устройств)

    /// <summary>Все пути HID-интерфейсов (активные). Только чтение конфиг-менеджера.</summary>
    public static List<string> ListHidInterfacePaths()
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            var guid = HidInterfaceGuid;
            int cr = CM_Get_Device_Interface_List_SizeW(out uint size, ref guid, IntPtr.Zero, CmGetDeviceInterfaceListPresent);
            if (cr != 0 || size == 0) return result;
            var buf = new char[size];
            cr = CM_Get_Device_Interface_ListW(ref guid, IntPtr.Zero, buf, size, CmGetDeviceInterfaceListPresent);
            if (cr != 0) return result;
            foreach (var seg in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                result.Add(seg);
        }
        catch
        {
            // перечисление — не критичная операция
        }
        return result;
    }

    /// <summary>ID родительского девайса (по instance ID дочернего), без открытий.</summary>
    public static string? GetParentDeviceId(string instanceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(instanceId)) return null;
        try
        {
            if (CM_Locate_DevNodeW(out uint dev, instanceId, CmLocateDevnodeNormal) != 0) return null;
            if (CM_Get_Parent(out uint parent, dev, 0) != 0) return null;
            var sb = new StringBuilder(512);
            if (CM_Get_Device_IDW(parent, sb, 512, 0) != 0) return null;
            return sb.ToString();
        }
        catch { return null; }
    }

    /// <summary>
    /// ФИЗИЧЕСКИЕ DualSense в системе (Bluetooth в приоритете). Виртуальные
    /// usbip-устройства исключаются — их драйвер под нагрузкой игры не отвечает,
    /// и любое открытие зависает. Открытий устройств НЕ выполняется вовсе.
    /// </summary>
    public static List<DualSenseDeviceInfo> EnumeratePhysicalDualSense() => EnumerateWithVirtuals().Physical;

    /// <summary>
    /// Все DualSense-интерфейсы, разделённые на физические и виртуальные (usbip).
    /// Виртуальные определяются по ЦЕПОЧКЕ ПРЕДКОВ (vhub/vHCI usbip-win): прямой
    /// родитель виртуального устройства выглядит как обычное «USB\VID_054C…»,
    /// и префиксной проверки недостаточно (v1.9.2–1.9.3 из-за этого могли
    /// «подключиться» к виртуальному контроллеру вместо физического).
    ///
    /// Для ФИЗИЧЕСКИХ кандидатов кратко (хэндл с НУЛЕВЫМ доступом, только чтение
    /// кэшированных размеров отчётов) определяется ГЛАВНАЯ гейпадная коллекция:
    /// у DualSense несколько HID-коллекций (гейпад input 78/64 Б, сенсорные —
    /// меньше), и открывать нужно именно гейпадную. Это возвращает отбор, который
    /// в v1.9.0–1.9.1 неявно выполняла библиотека HidSharp (знала размеры всех
    /// коллекций при перечислении) и который потерялся в v1.9.2 — из-за этого
    /// мост мог «не видеть» контроллер, открыв побочную коллекцию (v1.9.6).
    /// </summary>
    public static (List<DualSenseDeviceInfo> Physical, List<DualSenseDeviceInfo> Virtual) EnumerateWithVirtuals()
    {
        var physical = new List<DualSenseDeviceInfo>();
        var virtualDevices = new List<DualSenseDeviceInfo>();
        foreach (var path in ListHidInterfacePaths())
        {
            var vp = TryParseVidPid(path);
            if (vp is null) continue;
            var (vid, pid) = vp.Value;
            if (vid != DualSenseProtocol.VendorIdSony) continue;
            if (pid != DualSenseProtocol.ProductIdDualSense && pid != DualSenseProtocol.ProductIdDualSenseEdge) continue;

            string instanceId = InstanceIdFromPath(path);
            var (transport, isUsbip) = ClassifyParent(GetParentDeviceId(instanceId));
            bool isVirtual = isUsbip || IsVirtualUsbip(instanceId);

            if (isVirtual)
            {
                // Виртуальные usbip-устройства НЕ трогаем ВООБЩЕ (никаких открытий).
                virtualDevices.Add(new DualSenseDeviceInfo
                {
                    DevicePath = path,
                    InstanceId = instanceId,
                    Transport = Transport.Unknown,
                    VendorId = vid,
                    ProductId = pid,
                    ProductName = pid == DualSenseProtocol.ProductIdDualSenseEdge
                        ? "DualSense Edge Wireless Controller"
                        : DualSenseProtocol.ProductName,
                });
                continue;
            }

            // Физический кандидат: читаем размеры отчётов коллекции (нулевой доступ —
            // безопасно и без конфликтов с уже открывшими приложение).
            int inCap = 0, outCap = 0;
            try { (inCap, outCap) = WinHidStream.ProbeCaps(path); } catch { }
            var byCaps = ClassifyByCaps(inCap, outCap);
            if (byCaps != Transport.Unknown && transport == Transport.Unknown)
                transport = byCaps; // caps надёжнее строки родителя

            physical.Add(new DualSenseDeviceInfo
            {
                DevicePath = path,
                InstanceId = instanceId,
                Transport = transport,
                VendorId = vid,
                ProductId = pid,
                ProductName = pid == DualSenseProtocol.ProductIdDualSenseEdge
                    ? "DualSense Edge Wireless Controller"
                    : DualSenseProtocol.ProductName,
                MaxInputReportLength = inCap,
                MaxOutputReportLength = outCap,
                SerialNumber = null,
            });
        }

        // Главная (гейпадная) коллекция — первой; затем Bluetooth раньше USB.
        return (OrderPhysicalCandidates(physical), virtualDevices);
    }

    /// <summary>
    /// Чистое упорядочивание кандидатов: ГЛАВНАЯ коллекция (input 78 Б = BT /
    /// 64 Б = USB) раньше побочных, Bluetooth раньше USB. Стабильно.
    /// </summary>
    public static List<DualSenseDeviceInfo> OrderPhysicalCandidates(List<DualSenseDeviceInfo> devices)
    {
        return devices
            .OrderByDescending(d => ClassifyByCaps(d.MaxInputReportLength, d.MaxOutputReportLength) != Transport.Unknown)
            .ThenBy(d => d.Transport == Transport.Bluetooth ? 0 : d.Transport == Transport.Usb ? 1 : 2)
            .ToList();
    }
}

/// <summary>
/// Поток ввода-вывода HID поверх CreateFile/ReadFile/WriteFile (overlapped).
/// Открывается ТОЛЬКО конкретное устройство (выбранный физический DualSense).
/// Чтение и запись — с жёстким таймаутом (CancelIoEx): застрявший запрос
/// прерывается вместо вечного ожидания.
/// </summary>
public sealed class WinHidStream : IDisposable
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint WaitAbandoned = 0x80, WaitTimeout = 0x102;
    // ERROR_IO_PENDING = 997 (0x3E5). ВНИМАНИЕ: 996 — это ERROR_IO_INCOMPLETE,
    // ДРУГАЯ ошибка: если сравнивать pending-результат с 996, каждая операция,
    // ушедшая в ожидание (на Bluetooth это почти каждая запись 398 Б), ошибочно
    // трактуется как сбой и бросает исключение (баг v1.9.2).
    private const int ErrorIoPending = 997;

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public IntPtr Event;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(Microsoft.Win32.SafeHandles.SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(Microsoft.Win32.SafeHandles.SafeFileHandle HidDeviceObject, byte[] lpReportBuffer, uint ReportBufferLength);

    private Microsoft.Win32.SafeHandles.SafeFileHandle? _handle;
    private IntPtr _event = IntPtr.Zero;
    private readonly bool _synchronous;

    /// <summary>Максимальная длина input-отчёта (включая Report ID).</summary>
    public int InputCap { get; private set; }

    /// <summary>Максимальная длина output-отчёта (включая Report ID).</summary>
    public int OutputCap { get; private set; }

    /// <summary>
    /// Открывает устройство. По умолчанию — overlapped (чтение/запись с таймаутом
    /// и CancelIoEx); synchronous=true — обычный хэндл для HidD_* (feature),
    /// которые рассчитаны на синхронные хэндлы.
    /// </summary>
    public WinHidStream(string devicePath, bool synchronous = false)
    {
        _synchronous = synchronous;
        _handle = CreateFileW(devicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, synchronous ? 0 : FileFlagOverlapped, IntPtr.Zero);
        if (_handle.IsInvalid)
            throw new IOException($"Не удалось открыть HID-устройство: {devicePath}");

        if (!synchronous)
        {
            _event = CreateEventW(IntPtr.Zero, false, false, null);
            if (_event == IntPtr.Zero)
            {
                _handle.Dispose();
                _handle = null;
                throw new IOException("не удалось создать событие ввода-вывода (CreateEventW)");
            }
        }

        // Caps из закэшированных данных hidclass — БЕЗ обращений к драйверу устройства.
        try
        {
            if (HidD_GetPreparsedData(_handle, out IntPtr prep) && prep != IntPtr.Zero)
            {
                var caps = new HIDP_CAPS { Reserved = new ushort[17] };
                if (HidP_GetCaps(prep, ref caps) == 0)
                {
                    InputCap = caps.InputReportByteLength;
                    OutputCap = caps.OutputReportByteLength;
                }
                HidD_FreePreparsedData(prep);
            }
        }
        catch
        {
            // caps не критичны: длины уточнятся по фактическим отчётам
        }
    }

    /// <summary>
    /// Читает input-отчёт. Возвращает отчёт (с Report ID в [0]) или null по таймауту.
    /// Застрявший запрос прерывается CancelIoEx — вечное ожидание исключено.
    /// </summary>
    public byte[]? Read(int timeoutMs)
    {
        var h = _handle ?? throw new ObjectDisposedException(nameof(WinHidStream));
        if (_synchronous) throw new NotSupportedException("Read не поддерживается на синхронном хэндле (он — только для feature)");
        int cap = InputCap > 0 ? InputCap : 78;
        var buf = new byte[cap];
        var ov = new OVERLAPPED { Event = _event };
        ResetEvent(_event); // страховка от «залипшего» сигнала прошлой операции

        if (ReadFile(h, buf, (uint)buf.Length, out _, ref ov))
            return buf; // завершилось синхронно

        int readError = Marshal.GetLastWin32Error();
        if (readError != ErrorIoPending)
            throw new IOException($"ReadFile FAILED (HID), код ошибки {readError}");

        uint w = WaitForSingleObject(_event, (uint)timeoutMs);
        if (w == WaitTimeout || w == WaitAbandoned)
        {
            // Отменяем и ДОЖИДАЕМСЯ завершения отменённой операции: её поздний
            // сигнал иначе «съест» следующую операцию на этом же событии (v1.9.3:
            // один таймаут выводил канал из строя каскадом ложных сбоев).
            CancelIoEx(h, IntPtr.Zero);
            WaitForSingleObject(_event, 200);
            try { GetOverlappedResult(h, ref ov, out _, false); } catch { }
            return null;
        }

        if (!GetOverlappedResult(h, ref ov, out uint n, false))
            throw new IOException("GetOverlappedResult (HID read) FAILED");
        if (n == 0 || n > buf.Length) return buf;
        int len = (int)n; return len == buf.Length ? buf : buf[..len];
    }

    /// <summary>
    /// Читает размеры отчётов коллекции, открыв устройство с НУЛЕВЫМ доступом
    /// (только запрос дескриптора). Такое открытие не конфликтует с другими
    /// процессами, держащими устройство, и не может повесить приложение.
    /// Вызывается ТОЛЬКО для физических DualSense — виртуальные usbip-устройства
    /// исключаются раньше (по цепочке предков) и не открываются никогда.
    /// ВНИМАНИЕ: это возвращает отбор главной гейпадной коллекции, который в
    /// v1.9.0–1.9.1 выполняла библиотека HidSharp и который был потерян в
    /// v1.9.2 (мост открывал побочную коллекцию и «не видел» контроллер).
    /// </summary>
    public static (int inputCap, int outputCap) ProbeCaps(string devicePath)
    {
        using var h = CreateFileW(devicePath, 0, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"не удалось открыть для опроса дескриптора: {devicePath}");
        if (HidD_GetPreparsedData(h, out IntPtr prep) && prep != IntPtr.Zero)
        {
            try
            {
                var caps = new HIDP_CAPS { Reserved = new ushort[17] };
                if (HidP_GetCaps(prep, ref caps) == 0)
                    return (caps.InputReportByteLength, caps.OutputReportByteLength);
            }
            finally
            {
                HidD_FreePreparsedData(prep);
            }
        }
        return (0, 0);
    }

    /// <summary>
    /// Готовит буфер записи для WriteFile: Windows HID требует, чтобы длина буфера
    /// была РОВНО OutputReportByteLength — размером МАКСИМАЛЬНОГО output-отчёта
    /// коллекции. У BT DualSense это 398 Б (в дескрипторе объявлена серия отчётов
    /// 0x31=78 … 0x36=398), поэтому обычный отчёт состояния 0x31 (78 Б) ОБЯЗАН
    /// дополняться нулями до 398. Именно так поступают HidSharp (CommonWrite
    /// принимает maxOutputReportLength) и hidapi (паддит нулями до максимума);
    /// без паддинга WriteFile отклоняет запись — v1.9.2: из-за этого физический
    /// контроллер не подключался к мосту по Bluetooth («пропала вибрация»,
    /// «тесты в приложении не работают»), а игра работала с контроллером напрямую.
    /// </summary>
    public static byte[] PrepareWriteBuffer(byte[] report, int outputCap)
    {
        if (outputCap <= 0 || report.Length == outputCap) return report;
        if (report.Length > outputCap)
            throw new IOException(
                $"Размер отчёта ({report.Length} Б) больше максимального output-отчёта коллекции ({outputCap} Б) — запись невозможна");
        var padded = new byte[outputCap];
        Array.Copy(report, padded, report.Length); // остаток добивается нулями
        return padded;
    }

    /// <summary>
    /// Пишет output-отчёт (Report ID в [0]). Буфер автоматически дополняется
    /// нулями до размера максимального отчёта коллекции (требование Windows).
    /// Таймаут — исключение.
    /// </summary>
    public void Write(byte[] report, int timeoutMs)
    {
        var h = _handle ?? throw new ObjectDisposedException(nameof(WinHidStream));
        if (_synchronous) throw new NotSupportedException("Write не поддерживается на синхронном хэндле (он — только для feature)");
        byte[] buffer = PrepareWriteBuffer(report, OutputCap);
        var ov = new OVERLAPPED { Event = _event };
        ResetEvent(_event); // страховка от «залипшего» сигнала прошлой операции

        if (WriteFile(h, buffer, (uint)buffer.Length, out _, ref ov))
            return;

        int writeError = Marshal.GetLastWin32Error();
        if (writeError != ErrorIoPending)
            throw new IOException($"WriteFile FAILED (HID), код ошибки {writeError}");

        uint w = WaitForSingleObject(_event, (uint)timeoutMs);
        if (w == WaitTimeout || w == WaitAbandoned)
        {
            // Отменяем и ДОЖИДАЕМСЯ завершения отменённой операции (см. Read).
            CancelIoEx(h, IntPtr.Zero);
            WaitForSingleObject(_event, 200);
            try { GetOverlappedResult(h, ref ov, out _, false); } catch { }
            throw new IOException($"Таймаут записи HID ({timeoutMs} мс) — вероятно, устройство не успевает");
        }

        if (!GetOverlappedResult(h, ref ov, out _, false))
            throw new IOException($"GetOverlappedResult (HID write) FAILED, код ошибки {Marshal.GetLastWin32Error()}");
    }

    /// <summary>Читает feature-отчёт (buffer[0] = Report ID, длина = полный размер).</summary>
    public byte[]? GetFeature(byte reportId, int length)
    {
        var h = _handle ?? throw new ObjectDisposedException(nameof(WinHidStream));
        var buffer = new byte[length];
        buffer[0] = reportId;
        return HidD_GetFeature(h, buffer, (uint)length) ? buffer : null;
    }

    public void Dispose()
    {
        if (_handle is not null)
        {
            try { CancelIoEx(_handle, IntPtr.Zero); } catch { }
            _handle.Dispose();
            _handle = null;
        }
        if (_event != IntPtr.Zero)
        {
            CloseHandle(_event);
            _event = IntPtr.Zero;
        }
    }
}

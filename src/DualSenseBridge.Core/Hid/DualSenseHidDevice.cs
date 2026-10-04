using DualSenseBridge.Core.Protocol;
using HidSharp;

namespace DualSenseBridge.Core.Hid;

/// <summary>Сведения о найденном физическом DualSense.</summary>
public sealed record DualSenseDeviceInfo
{
    public required string DevicePath { get; init; }
    public required string InstanceId { get; init; }   // для HidHide
    public required Transport Transport { get; init; }
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public required string ProductName { get; init; }
    public int MaxInputReportLength { get; init; }
    public int MaxOutputReportLength { get; init; }
    public string? SerialNumber { get; init; }

    /// <summary>Устройство HidSharp (заполняется при перечислении; для открытия).</summary>
    public HidDevice? HidDevice { get; init; }

    public bool IsEdge => ProductId == DualSenseProtocol.ProductIdDualSenseEdge;

    public override string ToString() =>
        $"{ProductName} [{(Transport == Transport.Bluetooth ? "Bluetooth" : "USB")}] VID={VendorId:X4} PID={ProductId:X4}";
}

/// <summary>
/// Физический DualSense, открытый через HidSharp — ПОЛНЫЙ ОТКАТ к слою v1.9.1
/// (v1.9.8): перечисление, открытие, чтение и запись выполняет библиотека
/// HidSharp, ровно как в версии, где всё работало. Запись output-отчётов —
/// через HidStream.Write (библиотека сама дополняет буфер до максимального
/// output-отчёта коллекции — поведение, на котором работал v1.9.0–1.9.1).
///
/// Известная цена отката (задача 13): перечисление HidSharp открывает каждое
/// HID-устройство системы, и если игра уже работает с виртуальным usbip-
/// DualSense, вызов перечисления может зависнуть до закрытия игры. Поэтому
/// перечисление выполняется только в фоновых потоках моста (супервизор),
/// интерфейс приложения при этом остаётся живым.
/// </summary>
public sealed class DualSenseHidDevice : IDisposable
{
    private readonly HidDevice _hidDevice;

    /// <summary>Стрим чтения input-отчётов.</summary>
    private HidStream? _read;

    /// <summary>Отдельный стрим записи (заблокировавшаяся запись не останавливает ввод).</summary>
    private HidStream? _write;
    private byte _outputSeq;

    public DualSenseDeviceInfo Info { get; }

    /// <summary>Фактический транспорт (уточняется в рантайме по input-отчётам).</summary>
    public Transport Transport { get; private set; }

    public int MaxInputReportLength => _hidDevice.GetMaxInputReportLength();
    public int MaxOutputReportLength => _hidDevice.GetMaxOutputReportLength();

    public bool IsOpen => _read is not null;

    public DualSenseHidDevice(HidDevice hidDevice, DualSenseDeviceInfo info)
    {
        _hidDevice = hidDevice;
        Info = info;
        Transport = info.Transport; // уточняется в Initialize() и по первому отчёту
    }

    // ------------------------------------------------------------------ Перечисление

    /// <summary>
    /// ФИЗИЧЕСКИЕ DualSense (Bluetooth в приоритете) — перечисление HidSharp,
    /// как в v1.9.1 (транспорт: input 78 Б = BT, 64 Б = USB по дескриптору).
    /// </summary>
    public static IReadOnlyList<DualSenseDeviceInfo> Enumerate()
    {
        var result = new List<DualSenseDeviceInfo>();
        void Add(HidDevice d, Transport t)
        {
            result.Add(new DualSenseDeviceInfo
            {
                DevicePath = d.DevicePath,
                InstanceId = WinHid.InstanceIdFromPath(d.DevicePath),
                Transport = t,
                VendorId = (ushort)d.VendorID,
                ProductId = (ushort)d.ProductID,
                ProductName = d.ProductName is { Length: > 0 } ? d.ProductName : DualSenseProtocol.ProductName,
                MaxInputReportLength = d.GetMaxInputReportLength(),
                MaxOutputReportLength = d.GetMaxOutputReportLength(),
                SerialNumber = d.SerialNumber,
                HidDevice = d,
            });
        }

        foreach (var d in DeviceList.Local.GetHidDevices(DualSenseProtocol.VendorIdSony, DualSenseProtocol.ProductIdDualSense))
            Add(d, d.GetMaxInputReportLength() == DualSenseProtocol.InputReportBtSize ? Transport.Bluetooth : Transport.Usb);
        foreach (var d in DeviceList.Local.GetHidDevices(DualSenseProtocol.VendorIdSony, DualSenseProtocol.ProductIdDualSenseEdge))
            Add(d, d.GetMaxInputReportLength() == DualSenseProtocol.InputReportBtSize ? Transport.Bluetooth : Transport.Usb);

        // Bluetooth в приоритете (как в v1.9.1); при равенстве — стабильный порядок.
        result.Sort(static (a, b) => (a.Transport == Transport.Bluetooth ? 0 : 1)
                                   - (b.Transport == Transport.Bluetooth ? 0 : 1));
        return result;
    }

    /// <summary>
    /// Диагностическое перечисление БЕЗ открытий устройств (WinHid): физические
    /// и виртуальные (usbip) коллекции с размерами отчётов. Используется только
    /// для --list-devices/--diagnose и предупреждений в журнал.
    /// </summary>
    public static (List<DualSenseDeviceInfo> Physical, List<DualSenseDeviceInfo> Virtual) EnumerateWithVirtuals()
        => WinHid.EnumerateWithVirtuals();

    /// <summary>
    /// Классификация транспорта по фактическому input-отчёту (как в v1.9.1).
    /// Bluetooth DualSense: полный режим — 0x31 (78 Б), до чтения feature 0x05 —
    /// минимальный 0x01 (~10 Б). USB: всегда 0x01 (64 Б). Пути неотличимы.
    /// (Необязательный inputCap используется только тестами/диагностикой.)
    /// </summary>
    public static Transport ClassifyFromReport(ReadOnlySpan<byte> report, int inputCap = 0)
    {
        if (report.IsEmpty) return Transport.Unknown;
        if (report[0] == DualSenseProtocol.InputReportBt && report.Length >= 70)
            return Transport.Bluetooth;                       // 0x31 / 78 Б
        if (report[0] == DualSenseProtocol.InputReportBtMinimal)
        {
            if (inputCap == DualSenseProtocol.InputReportBtSize) return Transport.Bluetooth;
            if (inputCap == DualSenseProtocol.InputReportUsbSize) return Transport.Usb;
            if (report.Length is >= 8 and <= 20) return Transport.Bluetooth;
            if (report.Length is >= 63 and <= 64) return Transport.Usb;
            if (report.Length > 64) return Transport.Bluetooth;
        }
        return Transport.Unknown;
    }

    /// <summary>
    /// Согласован ли отчёт с дескриптором своей коллекции (только для --diagnose:
    /// Windows обычно дополняет отчёты реальных коллекций до максимума).
    /// </summary>
    public static bool IsReportConsistentWithCaps(ReadOnlySpan<byte> report, int inputCap)
    {
        if (inputCap <= 0 || report.IsEmpty) return true;
        return report.Length == inputCap;
    }

    // ------------------------------------------------------------------ Открытие/закрытие

    /// <summary>Открывает устройство: стрим чтения + отдельный стрим записи.</summary>
    public void Open()
    {
        if (_read is not null) return;
        _read = _hidDevice.Open();
        _read.ReadTimeout = ReadTimeoutMs;

        try
        {
            _write = _hidDevice.Open();
            _write.WriteTimeout = WriteTimeoutMs;
        }
        catch
        {
            _write = null; // работаем через один стрим (как раньше)
        }
    }

    /// <summary>Стрим для записи: отдельный при возможности, иначе общий.</summary>
    private HidStream WriteStream => _write ?? _read ?? throw new InvalidOperationException("Устройство не открыто");

    /// <summary>
    /// Инициализация подключения (как в v1.9.1):
    ///  1) уточнение транспорта по первому input-отчёту;
    ///  2) для Bluetooth — чтение feature-отчёта калибровки 0x05 (переключает
    ///     контроллер с минимального BT-отчёта 0x01 на полный 0x31);
    ///  3) сброс подсветки (fade out).
    /// </summary>
    public void Initialize()
    {
        TryRefineTransport();

        if (Transport == Transport.Bluetooth)
        {
            var calibration = GetFeature(DualSenseProtocol.FeatureReportCalibration, DualSenseProtocol.FeatureReportCalibrationSize);
            if (calibration is not null)
                Log.Info($"Калибровка 0x05 прочитана ({calibration.Length} байт) — контроллер переключён в режим input 0x31");
            else
                Log.Warn("Не удалось прочитать feature 0x05 — контроллер может остаться в минимальном режиме 0x01");
        }

        var init = new OutputCommon().SetLightbarReset();
        WriteCommon(init);
        Log.Info($"Отправлен сброс подсветки (lightbar fade-out), транспорт: {Transport}");
    }

    /// <summary>
    /// Уточняет транспорт по первому прочитанному отчёту. DualSense непрерывно
    /// шлёт input-отчёты (~125 Гц), поэтому чтение с таймаутом надёжно.
    /// </summary>
    /// <returns>true, если транспорт определён.</returns>
    private bool TryRefineTransport()
    {
        try
        {
            var first = Read();
            if (first is null)
            {
                Log.Warn($"Транспорт не уточнён (нет input-отчёта за таймаут) — используется классификация по дескриптору: {Transport}");
                return Transport != Transport.Unknown;
            }

            var detected = ClassifyFromReport(first);
            if (detected == Transport.Unknown)
            {
                Log.Warn($"Неизвестный формат input-отчёта (id=0x{first[0]:X2}, {first.Length} Б) — транспорт: {Transport}");
                return Transport != Transport.Unknown;
            }
            if (detected != Transport)
            {
                Log.Info($"Транспорт уточнён по input-отчёту (id=0x{first[0]:X2}, {first.Length} Б): {Transport} → {detected}");
                Transport = detected;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Транспорт не уточнён ({ex.Message}) — используется классификация по дескриптору: {Transport}");
            return Transport != Transport.Unknown;
        }
    }

    /// <summary>MAC-адрес контроллера из feature-отчёта 0x09 (если доступен).</summary>
    public string? TryReadMacAddress()
    {
        try
        {
            var buf = GetFeature(DualSenseProtocol.FeatureReportPairingInfo, DualSenseProtocol.FeatureReportPairingInfoSize);
            if (buf is null || buf.Length < 7) return null;
            return string.Join(":", buf[1..7].Select(b => b.ToString("X2")));
        }
        catch { return null; }
    }

    /// <summary>Версии прошивки из feature-отчёта 0x20 (если доступен).</summary>
    public (uint hw, uint fw)? TryReadFirmwareVersions()
    {
        try
        {
            var buf = GetFeature(DualSenseProtocol.FeatureReportFirmwareInfo, DualSenseProtocol.FeatureReportFirmwareInfoSize);
            if (buf is null || buf.Length < 32) return null;
            uint hw = (uint)(buf[24] | (buf[25] << 8) | (buf[26] << 16) | (buf[27] << 24));
            uint fw = (uint)(buf[28] | (buf[29] << 8) | (buf[30] << 16) | (buf[31] << 24));
            return (hw, fw);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ Ввод/вывод

    /// <summary>
    /// Блокирующее чтение очередного input-отчёта. null — по таймауту.
    /// Попутно самокорректирует транспорт, если формат отчёта не соответствует.
    /// </summary>
    public byte[]? Read()
    {
        var stream = _read ?? throw new InvalidOperationException("Устройство не открыто");
        byte[] report;
        try
        {
            report = stream.Read();
        }
        catch (TimeoutException)
        {
            return null;
        }
        var detected = ClassifyFromReport(report);
        if (detected != Transport.Unknown && detected != Transport)
        {
            Log.Info($"Транспорт скорректирован по input-отчёту (id=0x{report[0]:X2}, {report.Length} Б): {Transport} → {detected}");
            Transport = detected;
        }
        return report;
    }

    /// <summary>Таймаут чтения input-отчёта (мс).</summary>
    public int ReadTimeoutMs { get; set; } = 500;

    /// <summary>Таймаут записи output-отчёта (мс).</summary>
    public int WriteTimeoutMs { get; set; } = 250;

    /// <summary>
    /// Записывает готовый output-отчёт (полный, с Report ID) — HidSharp сам
    /// дополняет буфер до максимального output-отчёта коллекции (как в v1.9.1).
    /// </summary>
    public void Write(byte[] report)
    {
        WriteStream.Write(report);
    }

    /// <summary>
    /// Отправляет общий блок: для Bluetooth оборачивается в 0x31 с seq/CRC,
    /// для USB — в 0x02. Возвращает число записанных байт.
    /// </summary>
    public int WriteCommon(OutputCommon common)
    {
        byte[] report = Transport == Transport.Bluetooth
            ? DualSenseReportCodec.WrapBluetooth(common, NextSeq())
            : DualSenseReportCodec.WrapUsb(common);
        Write(report);
        return report.Length;
    }

    private byte NextSeq()
    {
        byte s = _outputSeq;
        _outputSeq = (byte)((_outputSeq + 1) & 0x0F);
        return s;
    }

    /// <summary>
    /// Читает feature-отчёт. Для Bluetooth проверяет CRC32 (seed 0xA3).
    /// Возвращает буфер или null при неудаче.
    /// </summary>
    public byte[]? GetFeature(byte reportId, int length)
    {
        var stream = _read ?? WriteStream;
        try
        {
            var buffer = new byte[length];
            buffer[0] = reportId;
            stream.GetFeature(buffer);

            if (Transport == Transport.Bluetooth && length >= 5)
            {
                uint expected = (uint)(buffer[^4] | (buffer[^3] << 8) | (buffer[^2] << 16) | (buffer[^1] << 24));
                if (Crc32.ComputeSeeded(Crc32.SeedFeature, buffer.AsSpan(0, length - 4)) != expected)
                {
                    Log.Warn($"CRC32 feature-отчёта 0x{reportId:X2} не совпал");
                    return buffer; // данные всё равно возвращаем — CRC может не поддерживаться стеком
                }
            }
            return buffer;
        }
        catch (Exception ex)
        {
            Log.Warn($"GetFeature(0x{reportId:X2}) не удался: {ex.Message}");
            return null;
        }
    }

    /// <summary>Ложится, когда устройство физически отключено.</summary>
    public bool Connected { get; private set; } = true;

    public void Dispose()
    {
        try { _write?.Dispose(); } catch { }
        _write = null;
        try { _read?.Dispose(); } catch { }
        _read = null;
    }
}

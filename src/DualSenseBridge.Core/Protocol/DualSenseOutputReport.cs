namespace DualSenseBridge.Core.Protocol;

/// <summary>
/// Общий (47-байтовый) блок output-отчёта DualSense — единый для USB (0x02) и BT (0x31).
/// Строится из структурированных команд; флаги валидности выставляются автоматически.
/// </summary>
public sealed class OutputCommon
{
    /// <summary>Байты блока; индексы = смещения из <see cref="DualSenseProtocol"/>.</summary>
    public readonly byte[] Data = new byte[DualSenseProtocol.OutputCommonSize];

    public byte ValidFlag0 { get => Data[DualSenseProtocol.OffValidFlag0]; private set => Data[DualSenseProtocol.OffValidFlag0] = value; }
    public byte ValidFlag1 { get => Data[DualSenseProtocol.OffValidFlag1]; private set => Data[DualSenseProtocol.OffValidFlag1] = value; }
    public byte ValidFlag2 { get => Data[DualSenseProtocol.OffValidFlag2]; private set => Data[DualSenseProtocol.OffValidFlag2] = value; }

    /// <summary>
    /// Вибрация: right = высокочастотный мотор, left = низкочастотный (0..255).
    /// Ядро Linux для rumble всегда ставит ОБА бита — COMPATIBLE_VIBRATION (0x01)
    /// и HAPTICS_SELECT (0x02): «Select classic rumble style haptics and enable it».
    /// Без бита 0x02 моторы могут молча игнорироваться (подтверждено chargesense).
    /// </summary>
    public OutputCommon SetRumble(byte right, byte left)
    {
        Data[DualSenseProtocol.OffMotorRight] = right;
        Data[DualSenseProtocol.OffMotorLeft] = left;
        ValidFlag0 |= (byte)(DualSenseProtocol.Flag0CompatibleVibration | DualSenseProtocol.Flag0HapticsSelect);
        return this;
    }

    /// <summary>Эффект правого (R2) триггера: 11 байт, режим+параметры.</summary>
    public OutputCommon SetTriggerRight(ReadOnlySpan<byte> effectBlock)
    {
        effectBlock.CopyTo(Data.AsSpan(DualSenseProtocol.OffTriggerRight, TriggerEffect.BlockSize));
        ValidFlag0 |= DualSenseProtocol.Flag0RightTriggerEffect;
        return this;
    }

    /// <summary>Эффект левого (L2) триггера: 11 байт, режим+параметры.</summary>
    public OutputCommon SetTriggerLeft(ReadOnlySpan<byte> effectBlock)
    {
        effectBlock.CopyTo(Data.AsSpan(DualSenseProtocol.OffTriggerLeft, TriggerEffect.BlockSize));
        ValidFlag0 |= DualSenseProtocol.Flag0LeftTriggerEffect;
        return this;
    }

    /// <summary>RGB-подсветка (0..255 на канал).</summary>
    public OutputCommon SetLightbar(byte r, byte g, byte b)
    {
        Data[DualSenseProtocol.OffLightbarRed] = r;
        Data[DualSenseProtocol.OffLightbarGreen] = g;
        Data[DualSenseProtocol.OffLightbarBlue] = b;
        ValidFlag1 |= DualSenseProtocol.Flag1LightbarControl;
        return this;
    }

    /// <summary>Маска 5 LED игрока (бит 0 — левый).</summary>
    public OutputCommon SetPlayerLeds(byte mask)
    {
        Data[DualSenseProtocol.OffPlayerLeds] = mask;
        ValidFlag1 |= DualSenseProtocol.Flag1PlayerIndicatorControl;
        return this;
    }

    /// <summary>LED кнопки mute: 0=выкл, 1=вкл, 2=пульс.</summary>
    public OutputCommon SetMuteLed(byte mode)
    {
        Data[DualSenseProtocol.OffMuteButtonLed] = mode;
        ValidFlag1 |= DualSenseProtocol.Flag1MicMuteLedControl;
        return this;
    }

    /// <summary>
    /// Сброс подсветки (fade out). По данным hid-playstation.c это ОБЯЗАТЕЛЬНО
    /// после BT-подключения: контроллер проигрывает стартовую анимацию подсветки,
    /// и до явной переконфигурации он не даёт программировать цвет.
    /// </summary>
    public OutputCommon SetLightbarReset()
    {
        Data[DualSenseProtocol.OffLightbarSetup] = DualSenseProtocol.LightbarSetupLightOut;
        ValidFlag2 |= DualSenseProtocol.Flag2LightbarSetupControl;
        return this;
    }

    /// <summary>Скопировать содержимое из другого блока (с сохранением флагов источника).</summary>
    public static OutputCommon FromRaw(ReadOnlySpan<byte> common47)
    {
        if (common47.Length != DualSenseProtocol.OutputCommonSize)
            throw new ArgumentException($"ожидалось {DualSenseProtocol.OutputCommonSize} байт", nameof(common47));
        var c = new OutputCommon();
        common47.CopyTo(c.Data);
        return c;
    }

    /// <summary>True, если блок не содержит ни одного валидного раздела (все флаги нулевые).</summary>
    public bool IsEmpty => (ValidFlag0 | ValidFlag1 | ValidFlag2) == 0;
}

/// <summary>
/// Сборка финальных HID output-отчётов DualSense.
/// USB (0x02, 48 байт): Report ID + общий блок, без CRC.
/// Bluetooth (0x31, 78 байт): Report ID + seq/tag + общий блок + 24 reserved + CRC32.
/// </summary>
public static class DualSenseReportCodec
{
    /// <summary>
    /// Оборачивает общий блок в Bluetooth-отчёт 0x31 (78 байт).
    /// seq — 4-битный счётчик (0..15), увеличивается на каждый отчёт
    /// («needs to be increased every report» — hid-playstation.c).
    /// CRC32 (seed 0xA2) покрывает байты 0..73 и пишется в 74..77 LE.
    /// </summary>
    public static byte[] WrapBluetooth(OutputCommon common, byte seq)
    {
        var report = new byte[DualSenseProtocol.OutputReportBtSize];
        report[0] = DualSenseProtocol.OutputReportBt;
        report[1] = (byte)((seq & 0x0F) << 4); // старший ниббл = seq, младший = тег (0)
        report[2] = DualSenseProtocol.OutputBtTag; // магический тег 0x10 — обязателен
        common.Data.CopyTo(report, 3);
        Crc32.AppendLe(report, DualSenseProtocol.OutputReportBtSize - 4);
        return report;
    }

    /// <summary>Оборачивает общий блок в USB-отчёт 0x02 (48 байт: ID + общий блок).</summary>
    public static byte[] WrapUsb(OutputCommon common)
    {
        var report = new byte[DualSenseProtocol.OutputReportUsbSize];
        report[0] = DualSenseProtocol.OutputReportUsb;
        common.Data.CopyTo(report, 1);
        return report;
    }

    /// <summary>
    /// Извлекает общий блок из USB-отчёта 0x02, присланного игрой (например,
    /// из 474-байтного объекта обратной связи VIIPER, байты 28..75).
    /// </summary>
    public static bool TryExtractUsbCommon(ReadOnlySpan<byte> usbReport, out byte[] common)
    {
        common = Array.Empty<byte>();
        if (usbReport.Length < DualSenseProtocol.OutputReportUsbSize) return false;
        if (usbReport[0] != DualSenseProtocol.OutputReportUsb) return false;
        common = usbReport.Slice(1, DualSenseProtocol.OutputCommonSize).ToArray();
        return true;
    }
}

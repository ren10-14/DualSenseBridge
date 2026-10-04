namespace DualSenseBridge.Core.Protocol;

/// <summary>Состояние касания тачпада.</summary>
public struct TouchPoint
{
    public bool Active;
    public ushort X; // 0..1919
    public ushort Y; // 0..1079
}

/// <summary>Нормализованное состояние DualSense, разобранное из input-отчёта.</summary>
public sealed class DualSenseState
{
    public byte Lx = 128, Ly = 128, Rx = 128, Ry = 128; // 0..255, 128 = центр
    public byte L2, R2;                                  // 0..255
    public byte InputSequence;

    public bool Square, Cross, Circle, Triangle;
    public bool L1, R1, L2Button, R2Button, Create, Options, L3, R3;
    public bool Ps, TouchpadClick, MicMuteButton;

    /// <summary>Hat-значение D-pad: 0=N..7=СЗ, 8=нейтраль.</summary>
    public byte DpadHat = 8;

    public short GyroX, GyroY, GyroZ;       // сырые int16
    public short AccelX, AccelY, AccelZ;    // сырые int16
    public uint SensorTimestamp;            // тики по 0.33 мкс

    public TouchPoint Touch1, Touch2;

    public byte Status0; // батарея: младший ниббл — ёмкость 0..10, старший — статус заряда
    public byte Status1; // бит0 наушники, бит1 микрофон, бит2 mute

    /// <summary>Приблизительный уровень заряда, % (по формуле ядра Linux: n*10+5).</summary>
    public int BatteryPercent =>
        ChargingStatus switch
        {
            0x2 => 100,                                   // заряжен полностью
            0x0 or 0x1 => Math.Min(BatteryUnits * 10 + 5, 100),
            _ => 0,                                       // ошибки (0xa/0xb/0xf)
        };

    /// <summary>0=разряжается, 1=заряжается, 2=полностью, 0xa/0xb/0xf=ошибки.</summary>
    public int ChargingStatus => (Status0 >> 4) & 0x0F;

    public bool IsCharging => ChargingStatus == 0x1;
    public bool BatteryError => ChargingStatus is 0xA or 0xB or 0xF;
    private int BatteryUnits => Status0 & 0x0F;

    public bool HeadsetConnected => (Status1 & 0x01) != 0;
    public bool MicConnected => (Status1 & 0x02) != 0;
    public bool MicMuted => (Status1 & 0x04) != 0;

    /// <summary>Отчёт был минимальным (BT 0x01 до чтения калибровки) — часть полей недоступна.</summary>
    public bool IsMinimalReport;
}

/// <summary>
/// Разбор input-отчётов DualSense (USB 0x01/64 и BT 0x31/78, включая минимальный BT 0x01).
/// Для BT-отчётов проверяется CRC32 (seed 0xA1) — повреждённые отбрасываются.
/// </summary>
public static class DualSenseInputParser
{
    public static bool TryParse(ReadOnlySpan<byte> report, Transport transport, DualSenseState? reuse, out DualSenseState state)
    {
        state = reuse ?? new DualSenseState();
        if (report.IsEmpty) return false;

        switch (transport)
        {
            case Transport.Bluetooth when report[0] == DualSenseProtocol.InputReportBt
                                          && report.Length >= DualSenseProtocol.InputReportBtSize:
            {
                // CRC32 (seed 0xA1) по байтам 0..73, лежит в 74..77 LE.
                uint expected = (uint)(report[74] | (report[75] << 8) | (report[76] << 16) | (report[77] << 24));
                if (Crc32.ComputeSeeded(Crc32.SeedInput, report[..74]) != expected)
                    return false;
                ParseCommon(report[2..65], state);
                state.IsMinimalReport = false;
                return true;
            }

            case Transport.Bluetooth when report[0] == DualSenseProtocol.InputReportBtMinimal
                                          && report.Length >= 10:
            {
                // Минимальный BT-отчёт до чтения feature 0x05 (10 байт). Раскладка —
                // по распарсенному официальному HID-дескриптору (nondebug.github.io/dualsense):
                //   [1..4] = LX, LY, RX, RY; [5] = hat + Square/Cross/Circle/Triangle;
                //   [6] = L1/R1/L2/R2/Create/Options/L3/R3; [7] = PS/TouchpadClick (+vendor);
                //   [8] = L2 axis; [9] = R2 axis. MicMute и сенсоры/тач/батарея отсутствуют.
                state.Lx = report[1]; state.Ly = report[2];
                state.Rx = report[3]; state.Ry = report[4];
                ParseButtons0(report[5], state);
                ParseButtons1(report[6], state);
                state.Ps = (report[7] & DualSenseProtocol.Btn2Ps) != 0;
                state.TouchpadClick = (report[7] & DualSenseProtocol.Btn2Touchpad) != 0;
                state.L2 = report[8]; state.R2 = report[9];
                state.IsMinimalReport = true;
                return true;
            }

            case Transport.Usb when report[0] == DualSenseProtocol.InputReportUsb
                                    && report.Length >= DualSenseProtocol.InputReportUsbSize:
            {
                ParseCommon(report[1..64], state);
                state.IsMinimalReport = false;
                return true;
            }

            default:
                return false;
        }
    }

    private static void ParseCommon(ReadOnlySpan<byte> c, DualSenseState s)
    {
        // c — общий 63-байтовый блок input-отчёта.
        s.Lx = c[DualSenseProtocol.InOffLx];
        s.Ly = c[DualSenseProtocol.InOffLy];
        s.Rx = c[DualSenseProtocol.InOffRx];
        s.Ry = c[DualSenseProtocol.InOffRy];
        s.L2 = c[DualSenseProtocol.InOffL2];
        s.R2 = c[DualSenseProtocol.InOffR2];
        s.InputSequence = c[DualSenseProtocol.InOffSeq];

        ParseButtons0(c[DualSenseProtocol.InOffButtons0], s);
        ParseButtons1(c[DualSenseProtocol.InOffButtons1], s);
        ParseButtons2(c[DualSenseProtocol.InOffButtons2], s);

        s.GyroX = ReadI16(c, DualSenseProtocol.InOffGyro + 0);
        s.GyroY = ReadI16(c, DualSenseProtocol.InOffGyro + 2);
        s.GyroZ = ReadI16(c, DualSenseProtocol.InOffGyro + 4);
        s.AccelX = ReadI16(c, DualSenseProtocol.InOffAccel + 0);
        s.AccelY = ReadI16(c, DualSenseProtocol.InOffAccel + 2);
        s.AccelZ = ReadI16(c, DualSenseProtocol.InOffAccel + 4);
        s.SensorTimestamp = (uint)(c[DualSenseProtocol.InOffSensorTimestamp]
                                   | (c[DualSenseProtocol.InOffSensorTimestamp + 1] << 8)
                                   | (c[DualSenseProtocol.InOffSensorTimestamp + 2] << 16)
                                   | (c[DualSenseProtocol.InOffSensorTimestamp + 3] << 24));

        ParseTouch(c[DualSenseProtocol.InOffTouchPoints..], 0, ref s.Touch1);
        ParseTouch(c[DualSenseProtocol.InOffTouchPoints..], 4, ref s.Touch2);

        s.Status0 = c[DualSenseProtocol.InOffStatus0];
        s.Status1 = c[DualSenseProtocol.InOffStatus1];
    }

    private static void ParseButtons0(byte b0, DualSenseState s)
    {
        s.DpadHat = (byte)(b0 & 0x0F);
        s.Square = (b0 & DualSenseProtocol.Btn0Square) != 0;
        s.Cross = (b0 & DualSenseProtocol.Btn0Cross) != 0;
        s.Circle = (b0 & DualSenseProtocol.Btn0Circle) != 0;
        s.Triangle = (b0 & DualSenseProtocol.Btn0Triangle) != 0;
    }

    private static void ParseButtons1(byte b1, DualSenseState s)
    {
        s.L1 = (b1 & DualSenseProtocol.Btn1L1) != 0;
        s.R1 = (b1 & DualSenseProtocol.Btn1R1) != 0;
        s.L2Button = (b1 & DualSenseProtocol.Btn1L2) != 0;
        s.R2Button = (b1 & DualSenseProtocol.Btn1R2) != 0;
        s.Create = (b1 & DualSenseProtocol.Btn1Create) != 0;
        s.Options = (b1 & DualSenseProtocol.Btn1Options) != 0;
        s.L3 = (b1 & DualSenseProtocol.Btn1L3) != 0;
        s.R3 = (b1 & DualSenseProtocol.Btn1R3) != 0;
    }

    private static void ParseButtons2(byte b2, DualSenseState s)
    {
        s.Ps = (b2 & DualSenseProtocol.Btn2Ps) != 0;
        s.TouchpadClick = (b2 & DualSenseProtocol.Btn2Touchpad) != 0;
        s.MicMuteButton = (b2 & DualSenseProtocol.Btn2MicMute) != 0;
    }

    private static void ParseTouch(ReadOnlySpan<byte> t, int offset, ref TouchPoint p)
    {
        var contact = t[offset + 0];
        p.Active = (contact & DualSenseProtocol.TouchPointInactive) == 0;
        // X = 12 бит: x_lo | (x_hi << 8); Y = 12 бит: y_lo | (y_hi << 4) (hid-playstation.c)
        byte xLo = t[offset + 1];
        byte xy = t[offset + 2];
        byte yHi = t[offset + 3];
        p.X = (ushort)(xLo | ((xy & 0x0F) << 8));
        p.Y = (ushort)(((xy >> 4) & 0x0F) | (yHi << 4));
    }

    private static short ReadI16(ReadOnlySpan<byte> b, int i) => (short)(b[i] | (b[i + 1] << 8));
}

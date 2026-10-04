using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core.Translation;

/// <summary>
/// Отображение состояния физического DualSense в wire-формат виртуального
/// контроллера VIIPER (33 байта). Единицы сенсоров совпадают на обеих сторонах
/// (сырые int16-отсчёты: гироскоп ~16.384 отсчёта на °/с, акселерометр 8192 на g),
/// поэтому IMU передаётся как есть; тачпад 12-битные координаты 1920x1080 тоже совпадают.
/// </summary>
public static class InputMapper
{
    public static DualSenseWireInput Map(DualSenseState s)
    {
        uint buttons = 0;
        if (s.Square) buttons |= WireButtons.Square;
        if (s.Cross) buttons |= WireButtons.Cross;
        if (s.Circle) buttons |= WireButtons.Circle;
        if (s.Triangle) buttons |= WireButtons.Triangle;
        if (s.L1) buttons |= WireButtons.L1;
        if (s.R1) buttons |= WireButtons.R1;
        if (s.L2Button) buttons |= WireButtons.L2;
        if (s.R2Button) buttons |= WireButtons.R2;
        if (s.Create) buttons |= WireButtons.Create;
        if (s.Options) buttons |= WireButtons.Options;
        if (s.L3) buttons |= WireButtons.L3;
        if (s.R3) buttons |= WireButtons.R3;
        if (s.Ps) buttons |= WireButtons.Ps;
        if (s.TouchpadClick) buttons |= WireButtons.TouchpadClick;
        if (s.MicMuteButton) buttons |= WireButtons.MicMute;

        byte dpad = HatToDpadBits(s.DpadHat);

        var input = new DualSenseWireInput
        {
            Lx = SByteFromUnsigned(s.Lx),
            Ly = SByteFromUnsigned(s.Ly),
            Rx = SByteFromUnsigned(s.Rx),
            Ry = SByteFromUnsigned(s.Ry),
            Buttons = buttons,
            Dpad = dpad,
            L2 = s.L2,
            R2 = s.R2,
            Touch1X = s.Touch1.X,
            Touch1Y = s.Touch1.Y,
            Touch1Active = s.Touch1.Active,
            Touch2X = s.Touch2.X,
            Touch2Y = s.Touch2.Y,
            Touch2Active = s.Touch2.Active,
            GyroX = s.GyroX,
            GyroY = s.GyroY,
            GyroZ = s.GyroZ,
            AccelX = s.AccelX,
            AccelY = s.AccelY,
            AccelZ = s.AccelZ,
        };
        return input;
    }

    /// <summary>0..255 (128 центр) -> -128..127 (0 центр).</summary>
    private static sbyte SByteFromUnsigned(byte v) => (sbyte)(v - 128);

    /// <summary>
    /// Hat-значение DualSense (0=С..7=СЗ, 8=нейтраль) в битовую маску D-pad VIIPER.
    /// </summary>
    private static byte HatToDpadBits(byte hat) => hat switch
    {
        0 => WireDpad.Up,
        1 => WireDpad.Up | WireDpad.Right,
        2 => WireDpad.Right,
        3 => WireDpad.Down | WireDpad.Right,
        4 => WireDpad.Down,
        5 => WireDpad.Down | WireDpad.Left,
        6 => WireDpad.Left,
        7 => WireDpad.Up | WireDpad.Left,
        _ => 0,
    };
}

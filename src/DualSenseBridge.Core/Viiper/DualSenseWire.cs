using DualSenseBridge.Core.Protocol;
namespace DualSenseBridge.Core.Viiper;

/// <summary>
/// Модель ввода виртуального DualSense в «проводном» формате VIIPER (33 байта, LE).
/// Источник: Alia5/VIIPER device/dualsense/state.go + docs; идентично в форке hbashton.
/// </summary>
public struct DualSenseWireInput
{
    public sbyte Lx, Ly, Rx, Ry;      // -128..127, 0 = центр
    public uint Buttons;              // битовая маска (см. WireButtons)
    public byte Dpad;                 // Up 0x01 | Down 0x02 | Left 0x04 | Right 0x08
    public byte L2, R2;               // 0..255
    public ushort Touch1X, Touch1Y; public bool Touch1Active;
    public ushort Touch2X, Touch2Y; public bool Touch2Active;
    public short GyroX, GyroY, GyroZ;     // сырые значения (16.384 отсчёта на °/с)
    public short AccelX, AccelY, AccelZ;  // сырые значения (8192 отсчёта на g)

    public const int Size = 33;

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        b[0] = (byte)Lx; b[1] = (byte)Ly; b[2] = (byte)Rx; b[3] = (byte)Ry;
        b[4] = (byte)(Buttons & 0xFF);
        b[5] = (byte)((Buttons >> 8) & 0xFF);
        b[6] = (byte)((Buttons >> 16) & 0xFF);
        b[7] = (byte)((Buttons >> 24) & 0xFF);
        b[8] = Dpad;
        b[9] = L2;
        b[10] = R2;
        WriteU16(b, 11, Touch1X); WriteU16(b, 13, Touch1Y); b[15] = Touch1Active ? (byte)1 : (byte)0;
        WriteU16(b, 16, Touch2X); WriteU16(b, 18, Touch2Y); b[20] = Touch2Active ? (byte)1 : (byte)0;
        WriteI16(b, 21, GyroX); WriteI16(b, 23, GyroY); WriteI16(b, 25, GyroZ);
        WriteI16(b, 27, AccelX); WriteI16(b, 29, AccelY); WriteI16(b, 31, AccelZ);
        return b;
    }

    private static void WriteU16(byte[] b, int i, ushort v) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); }
    private static void WriteI16(byte[] b, int i, short v) { b[i] = (byte)v; b[i + 1] = (byte)((ushort)v >> 8); }
}

/// <summary>Битовые маски кнопок в wire-формате VIIPER.</summary>
public static class WireButtons
{
    public const uint Square = 0x00000010;
    public const uint Cross = 0x00000020;
    public const uint Circle = 0x00000040;
    public const uint Triangle = 0x00000080;
    public const uint L1 = 0x00000100;
    public const uint R1 = 0x00000200;
    public const uint L2 = 0x00000400;
    public const uint R2 = 0x00000800;
    public const uint Create = 0x00001000;
    public const uint Options = 0x00002000;
    public const uint L3 = 0x00004000;
    public const uint R3 = 0x00008000;
    public const uint Ps = 0x00010000;
    public const uint TouchpadClick = 0x00020000;
    public const uint MicMute = 0x00040000;
}

/// <summary>Биты D-pad в wire-формате VIIPER.</summary>
public static class WireDpad
{
    public const byte Up = 0x01, Down = 0x02, Left = 0x04, Right = 0x08;
}

/// <summary>
/// Структурированная обратная связь виртуального DualSense (форк hbashton, payload 474 байта):
///   [0..5]    — rumbleSmall, rumbleLarge, LedR, LedG, LedB, PlayerLeds;
///   [6..27]   — нативные 11-байтовые блоки эффектов R2 и L2;
///   [28..75]  — нативный USB output-отчёт 0x02 (48 байт) как его отправила игра;
///   [76..473] — текущий 398-байтовый комбинированный BT-носитель (аудио/haptics).
/// </summary>
public sealed class DualSenseFeedback
{
    public byte RumbleSmall;
    public byte RumbleLarge;
    public byte LedRed, LedGreen, LedBlue;
    public byte PlayerLeds;

    /// <summary>Блок эффекта R2: режим + 10 параметров (нативный формат прошивки).</summary>
    public readonly byte[] TriggerRight = new byte[11];

    /// <summary>Блок эффекта L2: режим + 10 параметров.</summary>
    public readonly byte[] TriggerLeft = new byte[11];

    /// <summary>Нативный USB-отчёт 0x02 (48 байт, включая Report ID) от игры/Steam.</summary>
    public readonly byte[] UsbOutputReport = new byte[48];

    /// <summary>Комбинированный BT-носитель 0x36 (состояние+аудио) — для аудио-пути.</summary>
    public readonly byte[] CombinedBtCarrier = new byte[398];

    /// <summary>
    /// Заполнен ли комбинированный BT-носитель 0x36 (аудиоканал: состояние +
    /// хаптики PCM + Opus-динамик). При true носитель [76..473] можно записать
    /// в физический контроллер как есть (BT-дескриптор: 398 байт).
    /// </summary>
    public bool HasCombinedCarrier => CombinedBtCarrier.Length == 398 && CombinedBtCarrier[0] == 0x36;

    // ---- Раскладка 398-байтового комбинированного отчёта 0x36 (viiper bthaptics.go) ----
    public const int CarrierStateOffset = 13;      // 63 Б состояния; первые 47 = общий блок output
    public const int CarrierHapticsOffset = 78;    // 64 Б PCM ХАПТИК (S8 стерео, 3 кГц) — «вибрация»
    public const int CarrierHapticsSize = 64;
    public const int CarrierSpeakerOffset = 142;   // [0]=0x93, [1]=длина Opus-динамика (0 = пусто)
    public const int CarrierCrcOffset = 394;       // CRC32 (seed 0xEADA2D49) по байтам [0..393], LE
    public const uint CarrierCrcSeed = 0xEADA2D49;

    /// <summary>
    /// Носитель «молчит»: блок хаптик [78..141] — нули И динамик пуст.
    /// Тихие носители НЕ записываются в контроллер — иначе BT-канал заливается
    /// сотней пустых пакетов в секунду (клок 0x83 = 100 Гц, 0x84 = 93,75 Гц),
    /// и ввод контроллера задыхается.
    /// </summary>
    public bool IsCarrierSilent
    {
        get
        {
            if (!HasCombinedCarrier) return true;
            for (int i = CarrierHapticsOffset; i < CarrierHapticsOffset + CarrierHapticsSize; i++)
                if (CombinedBtCarrier[i] != 0) return false;
            return CombinedBtCarrier[CarrierSpeakerOffset + 1] == 0;
        }
    }

    /// <summary>
    /// Разбирает атомарный кадр 0x83 (v1.12.0): [0..1] LE16 длина блока обратной
    /// связи, затем 474 Б блока, затем ровно 1920 Б PCM динамика (480 кадров
    /// S16LE стерео 48 кГц). Звук динамика viiper передаёт ТОЛЬКО этим хвостом —
    /// дорожка Opus в самом носителе пуста, кодировать её обязан мост.
    /// </summary>
    public static bool TryParseAtomic(ReadOnlySpan<byte> payload, out DualSenseFeedback fb, out byte[]? speakerPcm)
    {
        speakerPcm = null;
        if (payload.Length <= 2) { fb = new DualSenseFeedback(); return false; }
        int fbLen = payload[0] | (payload[1] << 8);
        if (fbLen != V5Frame.FeedbackPayloadSize || payload.Length < 2 + fbLen)
        { fb = new DualSenseFeedback(); return false; }
        if (!TryParse(payload.Slice(2, fbLen), out fb)) return false;
        if (payload.Length >= 2 + fbLen + Output.SpeakerOpusEncoder.PcmBytes)
            speakerPcm = payload.Slice(2 + fbLen, Output.SpeakerOpusEncoder.PcmBytes).ToArray();
        return true;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out DualSenseFeedback fb)
    {
        fb = new DualSenseFeedback();
        if (payload.Length != V5Frame.FeedbackPayloadSize) return false;

        fb.RumbleSmall = payload[0];
        fb.RumbleLarge = payload[1];
        fb.LedRed = payload[2];
        fb.LedGreen = payload[3];
        fb.LedBlue = payload[4];
        fb.PlayerLeds = payload[5];
        payload.Slice(6, 11).CopyTo(fb.TriggerRight);
        payload.Slice(17, 11).CopyTo(fb.TriggerLeft);
        payload.Slice(28, 48).CopyTo(fb.UsbOutputReport);
        // Носитель 0x36 копируем ТОЛЬКО когда он есть (payload[76]==0x36):
        // у state-only кадров это экономит 398 Б аллокаций на каждый кадр.
        if (payload[76] == DualSenseProtocol.CombinedBtReportId)
            payload.Slice(76, 398).CopyTo(fb.CombinedBtCarrier);
        return true;
    }
}

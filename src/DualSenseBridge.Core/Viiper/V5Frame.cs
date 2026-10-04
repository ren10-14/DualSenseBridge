namespace DualSenseBridge.Core.Viiper;

/// <summary>
/// Кадр двустороннего стрима VIIPER V5 (форк hbashton/VIIPER, используется DS4Windows 5).
/// Заголовок 16 байт:
///   [0..3]  = ASCII "VPCM"
///   [4]     = версия протокола 0x05
///   [5]     = тип кадра
///   [6..7]  = длина payload, LE16
///   [8..11] = монотонный порядковый номер, LE32 (общий для всех типов в одном направлении)
///   [12..15]= IEEE CRC32, LE — покрывает байты 4..11 заголовка + payload
/// Несовпадение версии/CRC/seq/длины или неизвестный тип закрывает стрим.
/// Источник: hbashton/VIIPER docs/devices/dualsense.md «V5 stream contract».
/// </summary>
public static class V5Frame
{
    public const int HeaderSize = 16;
    public const byte Version = 0x05;

    // Клиент -> VIIPER
    public const byte TypeInputState = 0x01;  // 33 (или 53) байта состояния контроллера
    public const byte TypeMicPcm = 0x02;      // 1920 байт: S16LE стерео 48 кГц, 10 мс

    // VIIPER -> клиент
    public const byte TypeFeedback = 0x81;    // 474 байта: rumble/LED/триггеры/отчёт 0x02/BT-носитель
    public const byte TypeFeedbackAudio = 0x83; // атомарно: LE16 длина + 474 байта + 1920 байт PCM
    /// <summary>0x84: realtime-хаптики — те же 474 байта состояния, публикуются СРАЗУ
    /// по завершении интервала задних каналов (часы 93,75 Гц), не дожидаясь
    /// границы динамика (v0.1.2+, StreamFrameRealtimeHaptics).</summary>
    public const byte TypeRealtimeHaptics = 0x84;
    public const byte TypeMicEvent = 0x85;    // событие микрофонного интерфейса

    public const int FeedbackPayloadSize = 474;
    public const int InputStateSize = 33;

    private static readonly byte[] Magic = { (byte)'V', (byte)'P', (byte)'C', (byte)'M' };

    /// <summary>Кодирует кадр. CRC — стандартный IEEE CRC-32 (zlib).</summary>
    public static byte[] Encode(byte type, uint sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
            throw new ArgumentException("payload слишком велик для кадра V5", nameof(payload));

        var frame = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(frame, 0);
        frame[4] = Version;
        frame[5] = type;
        frame[6] = (byte)(payload.Length & 0xFF);
        frame[7] = (byte)(payload.Length >> 8);
        frame[8] = (byte)(sequence & 0xFF);
        frame[9] = (byte)((sequence >> 8) & 0xFF);
        frame[10] = (byte)((sequence >> 16) & 0xFF);
        frame[11] = (byte)((sequence >> 24) & 0xFF);

        payload.CopyTo(frame.AsSpan(HeaderSize));

        // CRC покрывает байты [4..11] заголовка и payload; поле CRC ([12..15]) не входит.
        uint crc = Protocol.Crc32.Update(0, frame.AsSpan(4, 8));
        crc = Protocol.Crc32.Update(crc, payload);
        frame[12] = (byte)(crc & 0xFF);
        frame[13] = (byte)((crc >> 8) & 0xFF);
        frame[14] = (byte)((crc >> 16) & 0xFF);
        frame[15] = (byte)((crc >> 24) & 0xFF);
        return frame;
    }

    /// <summary>
    /// Пытается декодировать кадр из начала буфера. Возвращает false, если данных
    /// мало, сигнатура/версия/CRC некорректны — по спецификации такое соединение
    /// следует закрыть (флаг broken=true).
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out byte type, out uint sequence, out byte[] payload, out bool broken, out int consumed)
    {
        type = 0; sequence = 0; payload = Array.Empty<byte>(); consumed = 0;
        broken = false;

        if (buffer.Length < HeaderSize) return false;

        for (int i = 0; i < 4; i++)
            if (buffer[i] != Magic[i]) { broken = true; return false; }

        if (buffer[4] != Version) { broken = true; return false; }

        type = buffer[5];
        int len = buffer[6] | (buffer[7] << 8);
        sequence = (uint)(buffer[8] | (buffer[9] << 8) | (buffer[10] << 16) | (buffer[11] << 24));
        consumed = HeaderSize + len;

        if (buffer.Length < consumed) return false; // ждём остаток кадра

        var body = buffer.Slice(HeaderSize, len);
        uint expectedCrc = (uint)(buffer[12] | (buffer[13] << 8) | (buffer[14] << 16) | (buffer[15] << 24));
        // CRC: [4..11] заголовка + payload (поле CRC пропускается).
        uint actualCrc = Protocol.Crc32.Update(0, buffer.Slice(4, 8));
        actualCrc = Protocol.Crc32.Update(actualCrc, body);
        if (expectedCrc != actualCrc) { broken = true; return false; }

        payload = body.ToArray();
        return true;
    }
}

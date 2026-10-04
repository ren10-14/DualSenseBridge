namespace DualSenseBridge.Core.Protocol;

/// <summary>
/// Стандартный CRC-32 (IEEE 802.3, он же zlib/binascii CRC-32):
/// полином 0x04C11DB7 (отражённое представление 0xEDB88320),
/// init = 0xFFFFFFFF, отражённый ввод/вывод, xorout = 0xFFFFFFFF.
///
/// Именно этот вариант CRC используется DualSense/DualShock 4 в Bluetooth-отчётах.
/// Особенность Sony: CRC вычисляется по последовательности [байт-заголовка BT-канала]
/// + [байты отчёта без самого CRC]. Заголовок BT HID DATA не передаётся через
/// WriteFile — он добавляется стеком Windows при отправке, поэтому мы «виртуально»
/// подставляем seed-байт перед телом отчёта.
///
/// Источники:
///  - Linux kernel drivers/hid/hid-playstation.c (GPL-2.0):
///    crc = crc32_le(0xFFFFFFFF, &seed, 1); crc = ~crc32_le(crc, data, len);
///    где seed: 0xA1 (input), 0xA2 (output), 0xA3 (feature).
///    crc32_le с таким использованием эквивалентен zlib.crc32(seed_byte + data).
///  - esp-ps5 (hamzayslmn): «CRC32 LE ... poly 0xEDB88320», сид 0xA2.
///  - Подтверждено перекрёстно: awalol/dualsense-bt-haptics, egormanga/SAxense.
/// </summary>
public static class Crc32
{
    /// <summary>Seed-байт для проверки входных (input) BT-отчётов DualSense.</summary>
    public const byte SeedInput = 0xA1;

    /// <summary>Seed-байт для исходящих (output) BT-отчётов DualSense.</summary>
    public const byte SeedOutput = 0xA2;

    /// <summary>Seed-байт для feature-отчётов DualSense (чтение/запись).</summary>
    public const byte SeedFeature = 0xA3;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>
    /// Продолжает вычисление CRC в стиле zlib: Update(0, data) == zlib.crc32(data).
    /// Можно вызывать каскадно: Update(Update(0, a), b) == Update(0, a+b).
    /// </summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint r = ~crc;
        foreach (byte b in data)
            r = Table[(r ^ b) & 0xFF] ^ (r >> 8);
        return ~r;
    }

    /// <summary>CRC-32 от одного буфера (эквивалент zlib.crc32).</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>
    /// CRC-32 в манере Sony DualSense: сначала seed-байт (виртуальный заголовок
    /// BT-канала 0xA1/0xA2/0xA3), затем тело отчёта. Результат пишется в конец
    /// отчёта little-endian.
    /// </summary>
    public static uint ComputeSeeded(byte seed, ReadOnlySpan<byte> reportBody)
    {
        uint crc = Update(0, stackalloc byte[1] { seed });
        return Update(crc, reportBody);
    }

    /// <summary>Дописывает CRC (LE) в конец буфера и возвращает его.</summary>
    public static uint AppendLe(byte[] buffer, int crcOffset)
    {
        uint crc = ComputeSeeded(SeedOutput, buffer.AsSpan(0, crcOffset));
        buffer[crcOffset + 0] = (byte)(crc & 0xFF);
        buffer[crcOffset + 1] = (byte)((crc >> 8) & 0xFF);
        buffer[crcOffset + 2] = (byte)((crc >> 16) & 0xFF);
        buffer[crcOffset + 3] = (byte)((crc >> 24) & 0xFF);
        return crc;
    }
}

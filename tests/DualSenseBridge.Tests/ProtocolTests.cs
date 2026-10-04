using DualSenseBridge.Core.Hid;
using DualSenseBridge.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace DualSenseBridge.Tests;

/// <summary>
/// Юнит-тесты протокольного ядра. «Золотые» векторы вычислены независимой
/// реализацией CRC-32 (zlib/python) по спецификации hid-playstation.c.
/// </summary>
public class ProtocolTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ CRC-32

    [Fact]
    public void Crc32_MatchesZlib()
    {
        // zlib.crc32(b"123456789") == 0xCBF43926 — стандартный тестовый вектор CRC-32.
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void Crc32_Seeded_MatchesIndependentVector()
    {
        // Независимо вычислено: zlib.crc32(b'\xA2' + bytes([0x31,0x20,0x10,0x03] + [0]*70)) == 0x6E578D9E
        byte[] body = new byte[74];
        body[0] = 0x31; body[1] = 0x20; body[2] = 0x10; body[3] = 0x03;
        Assert.Equal(0x6E578D9Eu, Crc32.ComputeSeeded(Crc32.SeedOutput, body));
    }

    // ------------------------------------------------------------------ Сборка BT output-отчёта

    [Fact]
    public void WrapBluetooth_ProducesGoldenReport()
    {
        // Вектор независимо сгенерирован: seq=2, tag=0x10, flag0=0x03,
        // motor_right=0x80, motor_left=0x40, остальное нули.
        var common = new OutputCommon().SetRumble(0x80, 0x40);
        byte[] report = DualSenseReportCodec.WrapBluetooth(common, seq: 2);

        string golden = "31201003008040" + new string('0', 134) + "9938997f";
        output.WriteLine($"получено: {Convert.ToHexString(report)}");
        Assert.Equal(78, report.Length);
        Assert.Equal(golden, Convert.ToHexString(report).ToLowerInvariant());
    }

    [Fact]
    public void WrapBluetooth_SeqWrapsAndTagIsMagic()
    {
        var common = new OutputCommon().SetLightbar(1, 2, 3);
        for (byte seq = 0; seq < 20; seq++)
        {
            byte[] report = DualSenseReportCodec.WrapBluetooth(common, seq);
            Assert.Equal(0x31, report[0]);
            Assert.Equal((seq & 0x0F) << 4, report[1]); // seq в старшем ниббле
            Assert.Equal(0x10, report[2]);               // магический тег
            // CRC всегда валиден:
            uint crc = Crc32.ComputeSeeded(Crc32.SeedOutput, report.AsSpan(0, 74));
            uint embedded = (uint)(report[74] | (report[75] << 8) | (report[76] << 16) | (report[77] << 24));
            Assert.Equal(crc, embedded);
        }
    }

    [Fact]
    public void WrapUsb_ProducesGoldenReport()
    {
        // USB 0x02: flag0 = оба триггера, flag1 = подсветка, RGB (0x10,0xFF,0x20).
        // Вектор построен без заполнения блоков триггеров (только флаги), поэтому
        // используется FromRaw — флуентные методы одновременно пишут блоки Off.
        var raw = new byte[47];
        raw[0] = 0x0C; raw[1] = 0x04;               // flag0: триггеры, flag1: подсветка
        raw[44] = 0x10; raw[45] = 0xFF; raw[46] = 0x20; // RGB (common+44..46)
        var common = OutputCommon.FromRaw(raw);
        byte[] report = DualSenseReportCodec.WrapUsb(common);

        string golden = "020c04" + new string('0', 84) + "10ff20";
        output.WriteLine($"получено: {Convert.ToHexString(report)}");
        Assert.Equal(48, report.Length);
        Assert.Equal(golden, Convert.ToHexString(report).ToLowerInvariant());
    }

    // ------------------------------------------------------------------ Эффекты триггеров

    [Fact]
    public void TriggerFeedback_MatchesGolden()
    {
        var block = new byte[TriggerEffect.BlockSize];
        TriggerEffect.Feedback(block, position: 3, strength: 5);
        Assert.Equal("21f8030048922400000000", Convert.ToHexString(block).ToLowerInvariant());
    }

    [Fact]
    public void TriggerWeapon_MatchesGolden()
    {
        var block = new byte[TriggerEffect.BlockSize];
        TriggerEffect.Weapon(block, startPosition: 4, endPosition: 7, strength: 6);
        Assert.Equal("2590000500000000000000", Convert.ToHexString(block).ToLowerInvariant());
    }

    [Fact]
    public void TriggerVibration_MatchesGolden()
    {
        var block = new byte[TriggerEffect.BlockSize];
        TriggerEffect.Vibration(block, position: 2, amplitude: 4, frequency: 30);
        Assert.Equal("26fc03c0b66d1b00001e00", Convert.ToHexString(block).ToLowerInvariant());
    }

    [Theory]
    [InlineData(10, 5)]   // позиция вне диапазона
    [InlineData(3, 9)]    // сила вне диапазона
    public void TriggerFeedback_RejectsOutOfRange(byte position, byte strength)
    {
        var block = new byte[TriggerEffect.BlockSize];
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerEffect.Feedback(block, position, strength));
    }

    [Fact]
    public void TriggerSanitize_BlocksDebugModes()
    {
        var block = new byte[TriggerEffect.BlockSize];
        block[0] = 0xFD; // отладочный режим — опасен
        Assert.True(TriggerEffect.Sanitize(block));
        Assert.Equal((byte)TriggerEffectMode.Off, block[0]);

        block[0] = 0xFC;
        Assert.True(TriggerEffect.Sanitize(block));

        block[0] = 0x21; // безопасный
        Assert.False(TriggerEffect.Sanitize(block));
    }

    [Fact]
    public void TriggerZeroStrength_TurnsOff()
    {
        var block = new byte[TriggerEffect.BlockSize];
        TriggerEffect.Feedback(block, 3, 0);
        Assert.Equal((byte)TriggerEffectMode.Off, block[0]); // поведение Sony: сила 0 = Off
    }

    // ------------------------------------------------------------------ Разбор input-отчётов

    [Fact]
    public void ParseBluetoothInput_MatchesGolden()
    {
        // Вектор независимо сгенерирован (см. docs/RESEARCH.md): стики в центре,
        // R2=255, seq=0x2A, hat=5 (ЮЗ) + Cross, Options, PS, касание (100,200),
        // батарея 5 (55%, заряжается), наушники подключены.
        string hex = "31008080808000ff2a252001" + new string('0', 44) + "0164800c" +
                     "80" + new string('0', 30) + "1501" + new string('0', 36) + "466ca43a";
        byte[] report = Convert.FromHexString(hex);
        Assert.Equal(78, report.Length);

        Assert.True(DualSenseInputParser.TryParse(report, Transport.Bluetooth, null, out var state));
        Assert.False(state.IsMinimalReport);
        Assert.Equal(128, state.Lx);
        Assert.Equal(0, state.L2);
        Assert.Equal(255, state.R2);
        Assert.Equal(0x2A, state.InputSequence);
        Assert.Equal(5, state.DpadHat);
        Assert.True(state.Cross);
        Assert.False(state.Square);
        Assert.True(state.Options);
        Assert.True(state.Ps);
        Assert.True(state.Touch1.Active);
        Assert.Equal(100, state.Touch1.X);
        Assert.Equal(200, state.Touch1.Y);
        Assert.False(state.Touch2.Active);
        Assert.Equal(55, state.BatteryPercent);
        Assert.True(state.IsCharging);
        Assert.True(state.HeadsetConnected);
    }

    [Fact]
    public void ParseBluetoothInput_RejectsCorruptedCrc()
    {
        string hex = "31008080808000ff2a252001" + new string('0', 44) + "0164800c" +
                     "80" + new string('0', 30) + "1501" + new string('0', 36) + "466ca43a";
        byte[] report = Convert.FromHexString(hex);
        report[10] ^= 0xFF; // ломаем тело — CRC перестаёт сходиться
        Assert.False(DualSenseInputParser.TryParse(report, Transport.Bluetooth, null, out _));
    }

    [Fact]
    public void ParseBluetoothMinimal_ParsesBasics()
    {
        // Минимальный BT-отчёт 0x01 (до чтения feature 0x05): 10 байт.
        // Раскладка по официальному дескриптору: [5]=hat+лицо, [6]=L1..R3, [7]=PS/тачпад, [8]=L2, [9]=R2.
        byte[] report = { 0x01, 0x80, 0x80, 0x80, 0x80, 0x25, 0x20, 0x01, 0x10, 0xFF };
        Assert.True(DualSenseInputParser.TryParse(report, Transport.Bluetooth, null, out var state));
        Assert.True(state.IsMinimalReport);
        Assert.Equal(5, state.DpadHat);      // ЮЗ
        Assert.True(state.Cross);
        Assert.True(state.Options);
        Assert.True(state.Ps);
        Assert.Equal(0x10, state.L2);
        Assert.Equal(0xFF, state.R2);
    }

    [Fact]
    public void ParseUsbInput_CommonBlock()
    {
        byte[] report = new byte[64];
        report[0] = 0x01;
        report[1] = 0x7F; // LX
        report[5] = 0x30; // L2 (common+4)
        report[8] = 0x42; // buttons0 (common+7): hat=2 (В) + Circle
        report[53] = 0x0A; // status0 (common+52): ёмкость 10, не заряжается
        Assert.True(DualSenseInputParser.TryParse(report, Transport.Usb, null, out var state));
        Assert.Equal(0x7F, state.Lx);
        Assert.Equal(0x30, state.L2);
        Assert.Equal(2, state.DpadHat);
        Assert.True(state.Circle);
        Assert.Equal(100, state.BatteryPercent);
        Assert.False(state.IsCharging);
    }

    // -------------------------------------------------------------- Определение транспорта

    /// <summary>
    /// Транспорт нельзя определять по пути устройства (на Windows пути HID не содержат
    /// признак шины) — классифицируем по фактическому формату input-отчёта.
    /// </summary>
    [Fact]
    public void ClassifyFromReport_BtFullMode()
    {
        var report = new byte[78];
        report[0] = 0x31;
        Assert.Equal(Transport.Bluetooth, DualSenseHidDevice.ClassifyFromReport(report));
    }

    [Fact]
    public void ClassifyFromReport_BtMinimalMode()
    {
        // До чтения feature 0x05 BT-контроллер шлёт минимальный 0x01 (~10 Б).
        var report = new byte[10];
        report[0] = 0x01;
        Assert.Equal(Transport.Bluetooth, DualSenseHidDevice.ClassifyFromReport(report));
    }

    [Fact]
    public void ClassifyFromReport_Usb()
    {
        var report = new byte[64];
        report[0] = 0x01;
        Assert.Equal(Transport.Usb, DualSenseHidDevice.ClassifyFromReport(report));
    }

    [Fact]
    public void ClassifyFromReport_UnknownFormats()
    {
        Assert.Equal(Transport.Unknown, DualSenseHidDevice.ClassifyFromReport(Array.Empty<byte>()));
        Assert.Equal(Transport.Unknown, DualSenseHidDevice.ClassifyFromReport(new byte[] { 0x01, 0x02, 0x03 })); // слишком короткий 0x01
        Assert.Equal(Transport.Unknown, DualSenseHidDevice.ClassifyFromReport(new byte[] { 0x99, 0x00 }));       // чужой Report ID
    }

    [Fact]
    public void ClassifyFromReport_PaddedMinimalBt()
    {
        // Если HID-стек дополнил минимальный BT-отчёт 0x01 до максимальной длины буфера (78 Б),
        // это всё равно Bluetooth: USB не порождает 0x01 длиннее 64 Б.
        var report = new byte[78];
        report[0] = 0x01;
        Assert.Equal(Transport.Bluetooth, DualSenseHidDevice.ClassifyFromReport(report));
    }
}

using DualSenseBridge.Core.Bridge;
using DualSenseBridge.Core.Output;
using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Translation;
using DualSenseBridge.Core.Viiper;
using Xunit;

namespace DualSenseBridge.Tests;

/// <summary>Тесты трансляции обратной связи (Native/Structured) и маппера ввода.</summary>
public class TranslationTests
{
    /// <summary>Собирает payload 474 байта, как его прислал бы VIIPER (диалект hbashton).</summary>
    private static byte[] BuildFeedbackPayload(
        byte rumbleSmall = 0, byte rumbleLarge = 0,
        byte r = 0, byte g = 0, byte b = 0, byte playerLeds = 0,
        byte[]? triggerRight = null, byte[]? triggerLeft = null,
        byte[]? usbReport = null)
    {
        var payload = new byte[474];
        payload[0] = rumbleSmall;
        payload[1] = rumbleLarge;
        payload[2] = r; payload[3] = g; payload[4] = b; payload[5] = playerLeds;
        if (triggerRight != null) triggerRight.CopyTo(payload, 6);
        if (triggerLeft != null) triggerLeft.CopyTo(payload, 17);
        if (usbReport != null) usbReport.CopyTo(payload, 28);
        return payload;
    }

    private static byte[] BuildUsbReport(byte flag0, byte motorR, byte motorL,
        byte[]? triggerRight = null, byte[]? triggerLeft = null,
        byte r = 0, byte g = 0, byte b = 0)
    {
        var report = new byte[48];
        report[0] = 0x02;
        var common = new OutputCommon().SetRumble(motorR, motorL); // выставит 0x03
        if (triggerRight != null) common.SetTriggerRight(triggerRight);
        if (triggerLeft != null) common.SetTriggerLeft(triggerLeft);
        if ((r | g | b) != 0) common.SetLightbar(r, g, b);
        // Подменяем флаги, чтобы сымитировать поведение игры (например, только 0x01).
        common.Data[DualSenseProtocol.OffValidFlag0] = flag0;
        common.Data.CopyTo(report, 1);
        return report;
    }

    // -------------------------------------------------------------- Native

    [Fact]
    public void Native_UsesUsbReport_AndPatchesHapticsSelect()
    {
        // Игра отправила USB 0x02: моторы заданы, но выставлен только бит 0x01.
        var usb = BuildUsbReport(flag0: 0x01, motorR: 0x80, motorL: 0x40, r: 0x10, g: 0xFF, b: 0x20);
        var payload = BuildFeedbackPayload(usbReport: usb);

        var t = new FeedbackTranslator { Mode = TranslationMode.Native, DefaultLightbar = false, ForcePlayerOneLed = false }; // LED-патчи — отдельные тесты
        var patches = new List<string>();
        t.SafetyPatchApplied += patches.Add;

        var common = t.Translate(DualSenseFeedback.TryParse(payload, out var fb) ? fb : throw new InvalidOperationException());

        Assert.NotNull(common);
        Assert.Equal(0x80, common!.Data[DualSenseProtocol.OffMotorRight]);
        Assert.Equal(0x40, common.Data[DualSenseProtocol.OffMotorLeft]);
        // Патч: 0x01 | добавленный HAPTICS_SELECT 0x02 = 0x03 (триггеры в этом тесте не заданы).
        Assert.Equal(0x03, common.Data[DualSenseProtocol.OffValidFlag0]);
        Assert.Equal(0x04, common.Data[DualSenseProtocol.OffValidFlag1]); // подсветка
        Assert.Contains(patches, p => p.Contains("HAPTICS_SELECT"));
    }

    [Fact]
    public void Native_SanitizesDebugTriggerMode()
    {
        var badTrigger = new byte[11];
        badTrigger[0] = 0xFD; // отладочный режим

        // Игра задала эффект R2 (флаг 0x04) и «классическую» вибрацию (0x01).
        var usb = BuildUsbReport(flag0: 0x05, motorR: 0, motorL: 0, triggerRight: badTrigger);
        var payload = BuildFeedbackPayload(triggerRight: badTrigger, usbReport: usb);

        var t = new FeedbackTranslator { Mode = TranslationMode.Native };
        var common = t.Translate(DualSenseFeedback.TryParse(payload, out var fb) ? fb : throw new InvalidOperationException());

        Assert.NotNull(common);
        Assert.Equal((byte)TriggerEffectMode.Off, common!.Data[DualSenseProtocol.OffTriggerRight]); // 0x05
        Assert.Equal(0x04, common.Data[DualSenseProtocol.OffValidFlag0] & 0x0C); // флаг триггера сохранён
    }

    [Fact]
    public void Native_EmptyFeedback_ReturnsNull()
    {
        var payload = BuildFeedbackPayload(); // всё нулевое
        // при включённой индикации по умолчанию блок НЕ пуст (LED/подсветка) —
        // семантику «пусто → null» проверяем с выключенной индикацией
        var t = new FeedbackTranslator { Mode = TranslationMode.Native, ForcePlayerOneLed = false, DefaultLightbar = false };
        Assert.Null(t.Translate(DualSenseFeedback.TryParse(payload, out var fb) ? fb : throw new InvalidOperationException()));
    }

    [Fact]
    public void RumbleScale_HalvesMotors()
    {
        var usb = BuildUsbReport(flag0: 0x03, motorR: 0x80, motorL: 0x40);
        var payload = BuildFeedbackPayload(usbReport: usb);

        var t = new FeedbackTranslator { Mode = TranslationMode.Native, RumbleScale = 0.5 };
        var common = t.Translate(DualSenseFeedback.TryParse(payload, out var fb) ? fb : throw new InvalidOperationException());

        Assert.NotNull(common);
        Assert.Equal(0x40, common!.Data[DualSenseProtocol.OffMotorRight]);
        Assert.Equal(0x20, common.Data[DualSenseProtocol.OffMotorLeft]);
    }

    // -------------------------------------------------------------- Structured

    [Fact]
    public void Structured_BuildsFromFields()
    {
        var weapon = new byte[11];
        TriggerEffect.Weapon(weapon, 4, 7, 6);
        var payload = BuildFeedbackPayload(
            rumbleSmall: 0x20, rumbleLarge: 0x60,
            r: 0x11, g: 0x22, b: 0x33, playerLeds: 0x1F,
            triggerRight: weapon);

        // ForcePlayerOneLed=false: этот тест проверяет сборку полей, а не патч «игрок 1».
        var t = new FeedbackTranslator { Mode = TranslationMode.Structured, ForcePlayerOneLed = false };
        var common = t.Translate(DualSenseFeedback.TryParse(payload, out var fb) ? fb : throw new InvalidOperationException());

        Assert.NotNull(common);
        Assert.Equal(0x20, common!.Data[DualSenseProtocol.OffMotorRight]);
        Assert.Equal(0x60, common.Data[DualSenseProtocol.OffMotorLeft]);
        Assert.Equal(0x07, common.Data[DualSenseProtocol.OffValidFlag0]); // rumble 0x03 + триггер R2 0x04
        Assert.Equal((byte)TriggerEffectMode.Weapon, common.Data[DualSenseProtocol.OffTriggerRight]);
        Assert.Equal(0x04, common.Data[DualSenseProtocol.OffValidFlag0] & 0x0C); // флаг R2; L2 не задан
        Assert.Equal(0x14, common.Data[DualSenseProtocol.OffValidFlag1]); // RGB + playerLeds
        Assert.Equal(0x11, common.Data[DualSenseProtocol.OffLightbarRed]);
        Assert.Equal(0x1F, common.Data[DualSenseProtocol.OffPlayerLeds]);
    }

    // -------------------------------------------------------------- InputMapper

    [Fact]
    public void InputMapper_MapsButtonsSticksDpad()
    {
        var s = new DualSenseState
        {
            Lx = 0xFF, Ly = 0x00, Rx = 0x80, Ry = 0x81, // L: вправо/вниз до упора
            L2 = 0x10, R2 = 0xFF,
            DpadHat = 5, // ЮЗ
            Cross = true, Options = true, Ps = true,
            Touch1 = new TouchPoint { Active = true, X = 100, Y = 200 },
            GyroZ = -1234,
            AccelX = 567,
        };

        var w = InputMapper.Map(s);
        byte[] bytes = w.ToBytes();

        Assert.Equal(33, bytes.Length);
        Assert.Equal(127, w.Lx);     // 0xFF - 128
        Assert.Equal(-128, w.Ly);    // 0x00 - 128
        Assert.Equal(0, w.Rx);
        Assert.Equal(1, w.Ry);
        Assert.Equal(WireButtons.Cross | WireButtons.Options | WireButtons.Ps, w.Buttons);
        Assert.Equal(WireDpad.Down | WireDpad.Left, w.Dpad);
        Assert.Equal(0x10, w.L2);
        Assert.Equal(0xFF, w.R2);
        Assert.True(w.Touch1Active);
        Assert.Equal(100, w.Touch1X);
        Assert.Equal(200, w.Touch1Y);
        Assert.Equal(-1234, w.GyroZ);
        Assert.Equal(567, w.AccelX);

        // Проверка проводной кодировки: кнопки LE32 на [4..7], Dpad на [8].
        Assert.Equal(0x20 | 0x2000 | 0x10000, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(0x06, bytes[8]);
        Assert.Equal(0xFF, bytes[10]); // R2
    }

    [Fact]
    public void InputMapper_NeutralHat_MapsToZeroDpad()
    {
        var w = InputMapper.Map(new DualSenseState { DpadHat = 8 });
        Assert.Equal(0, w.Dpad);
    }

    // -------------------------------------------------------------- Round-trip: ввод физического → wire

    [Fact]
    public void GoldenBtInput_MapsToWireInput()
    {
        // Из протокольного «золотого» вектора: hat=5, Cross+Options+PS, R2=255, тач (100,200).
        string hex = "31008080808000ff2a252001" + new string('0', 44) + "0164800c" +
                     "80" + new string('0', 30) + "1501" + new string('0', 36) + "466ca43a";
        Assert.True(DualSenseInputParser.TryParse(Convert.FromHexString(hex), Transport.Bluetooth, null, out var state));

        var w = InputMapper.Map(state);
        Assert.Equal(0, w.Lx);
        Assert.Equal(WireButtons.Cross | WireButtons.Options | WireButtons.Ps, w.Buttons);
        Assert.Equal(WireDpad.Down | WireDpad.Left, w.Dpad);
        Assert.Equal(255, w.R2);
        Assert.True(w.Touch1Active);
        Assert.Equal(100, w.Touch1X);
        Assert.Equal(200, w.Touch1Y);
    }
}

// ============================================================== v1.5.0: патчи совместимости и диагностика

public class CompatPatchTests
{
    /// <summary>USB-отчёт 0x02 с общим блоком, флаги задаём вручную (как «молчаливая» игра).</summary>
    private static byte[] BuildRawUsb(byte flag0, byte flag1, byte motorR, byte motorL,
        byte trigR2Mode = 0, byte rgbR = 0, byte rgbG = 0, byte rgbB = 0, byte playerLeds = 0)
    {
        var report = new byte[48];
        report[0] = 0x02;
        report[1] = flag0;
        report[2] = flag1;
        report[3] = motorR;
        report[4] = motorL;
        if (trigR2Mode != 0) report[1 + DualSenseProtocol.OffTriggerRight] = trigR2Mode;
        if ((rgbR | rgbG | rgbB) != 0)
        {
            report[1 + DualSenseProtocol.OffLightbarRed] = rgbR;
            report[1 + DualSenseProtocol.OffLightbarGreen] = rgbG;
            report[1 + DualSenseProtocol.OffLightbarBlue] = rgbB;
        }
        if (playerLeds != 0) report[1 + DualSenseProtocol.OffPlayerLeds] = playerLeds;
        return report;
    }

    private static DualSenseFeedback FbFromUsb(byte[] usb)
    {
        var payload = new byte[474];
        usb.CopyTo(payload, 28);
        Assert.True(DualSenseFeedback.TryParse(payload, out var fb));
        return fb;
    }

    [Fact]
    public void Motors_WithoutAnyFlags_GetBothRumbleBits()
    {
        // Игра записала моторы, но не выставила НИ ОДНОГО флага (типичный «молчун»).
        var fb = FbFromUsb(BuildRawUsb(0, 0, 0xC0, 0xFF));
        var t = new FeedbackTranslator { ForcePlayerOneLed = false };
        var common = t.Translate(fb)!;

        Assert.NotNull(common);
        Assert.Equal((byte)(DualSenseProtocol.Flag0CompatibleVibration | DualSenseProtocol.Flag0HapticsSelect),
            common.Data[DualSenseProtocol.OffValidFlag0]);
        Assert.Equal(0xC0, common.Data[DualSenseProtocol.OffMotorRight]);
    }

    [Fact]
    public void TriggerEffect_WithoutFlag_GetsValidityBit()
    {
        var fb = FbFromUsb(BuildRawUsb(0, 0, 0, 0, trigR2Mode: 0x21));
        var t = new FeedbackTranslator { ForcePlayerOneLed = false };
        var common = t.Translate(fb)!;

        Assert.Equal(DualSenseProtocol.Flag0RightTriggerEffect, common.Data[DualSenseProtocol.OffValidFlag0]);
        Assert.Equal(0x21, common.Data[DualSenseProtocol.OffTriggerRight]);
    }

    [Fact]
    public void Lightbar_WithoutFlag_GetsValidityBit()
    {
        var fb = FbFromUsb(BuildRawUsb(0, 0, 0, 0, rgbR: 0xFF, rgbG: 0x40, rgbB: 0x10));
        var t = new FeedbackTranslator { ForcePlayerOneLed = false };
        var common = t.Translate(fb)!;

        Assert.Equal(DualSenseProtocol.Flag1LightbarControl, common.Data[DualSenseProtocol.OffValidFlag1]);
        Assert.Equal(0xFF, common.Data[DualSenseProtocol.OffLightbarRed]);
    }

    [Fact]
    public void PlayerLeds_WithoutFlag_GetsValidityBit()
    {
        var fb = FbFromUsb(BuildRawUsb(0, 0, 0, 0, playerLeds: 0x0A));
        var t = new FeedbackTranslator { ForcePlayerOneLed = false, DefaultLightbar = false };
        var common = t.Translate(fb)!;

        Assert.Equal(DualSenseProtocol.Flag1PlayerIndicatorControl, common.Data[DualSenseProtocol.OffValidFlag1]);
    }

    [Fact]
    public void PlayerLeds_SecondPlayer_ForcedToFirst_ByDefault()
    {
        // Steam/игра зажгла «игрок 2» (0x0A) — по умолчанию принудительно «игрок 1» (0x04).
        var fb = FbFromUsb(BuildRawUsb(0, 0x10, 0, 0, playerLeds: DualSenseProtocol.PlayerLedPlayer2));
        var t = new FeedbackTranslator { DefaultLightbar = false }; // ForcePlayerOneLed = true по умолчанию
        var common = t.Translate(fb)!;

        Assert.Equal(DualSenseProtocol.PlayerLedPlayer1, common.Data[DualSenseProtocol.OffPlayerLeds]);
        Assert.Equal(DualSenseProtocol.Flag1PlayerIndicatorControl, common.Data[DualSenseProtocol.OffValidFlag1]);
        // v1.9.9: яркость player-LED теперь задаётся явно (флаг2 бит 0 + 0 = ярко)
        Assert.True((common.Data[DualSenseProtocol.OffValidFlag2] & DualSenseProtocol.Flag2LedBrightnessControl) != 0);
        Assert.Equal(0, common.Data[DualSenseProtocol.OffLedBrightness]);
    }

    [Fact]
    public void PlayerLeds_SecondPlayer_Kept_WhenForcingDisabled()
    {
        var fb = FbFromUsb(BuildRawUsb(0, 0x10, 0, 0, playerLeds: DualSenseProtocol.PlayerLedPlayer2));
        var t = new FeedbackTranslator { ForcePlayerOneLed = false };
        var common = t.Translate(fb)!;

        Assert.Equal(DualSenseProtocol.PlayerLedPlayer2, common.Data[DualSenseProtocol.OffPlayerLeds]);
    }

    [Fact]
    public void StructuredMode_AlsoForcesPlayerOne()
    {
        var payload = new byte[474];
        payload[5] = DualSenseProtocol.PlayerLedPlayer3; // структурированное поле viiper
        Assert.True(DualSenseFeedback.TryParse(payload, out var fb));

        var t = new FeedbackTranslator { Mode = TranslationMode.Structured };
        var common = t.Translate(fb)!;
        Assert.Equal(DualSenseProtocol.PlayerLedPlayer1, common.Data[DualSenseProtocol.OffPlayerLeds]);
    }

    [Fact]
    public void StructuredFields_MergeIntoEmptyNative()
    {
        // Нативный отчёт пуст, но viiper заполнил структурированные поля — берём их.
        var payload = new byte[474];
        payload[0] = 0x80; payload[1] = 0x40; // rumbleSmall/Large
        payload[2] = 0x10; payload[3] = 0xFF; payload[4] = 0x20; // RGB
        Assert.True(DualSenseFeedback.TryParse(payload, out var fb));

        var t = new FeedbackTranslator { ForcePlayerOneLed = false };
        var common = t.Translate(fb)!;

        Assert.Equal(0x80, common.Data[DualSenseProtocol.OffMotorRight]);
        Assert.Equal(0x40, common.Data[DualSenseProtocol.OffMotorLeft]);
        Assert.Equal(0x10, common.Data[DualSenseProtocol.OffLightbarRed]);
        Assert.Equal((byte)(DualSenseProtocol.Flag0CompatibleVibration | DualSenseProtocol.Flag0HapticsSelect),
            common.Data[DualSenseProtocol.OffValidFlag0]);
        Assert.Equal(DualSenseProtocol.Flag1LightbarControl, common.Data[DualSenseProtocol.OffValidFlag1]);
    }

    [Fact]
    public void PlayerLedPatterns_MatchLinuxKernel()
    {
        // hid-playstation.c: static const int player_ids[5] = { BIT(2), BIT(3)|BIT(1), ... }
        Assert.Equal(0x04, DualSenseProtocol.PlayerLedPlayer1);
        Assert.Equal(0x0A, DualSenseProtocol.PlayerLedPlayer2);
        Assert.Equal(0x15, DualSenseProtocol.PlayerLedPlayer3);
        Assert.Equal(0x1B, DualSenseProtocol.PlayerLedPlayer4);
        Assert.Equal(0x1F, DualSenseProtocol.PlayerLedPlayer5);
    }

    [Fact]
    public void CombinedBtCarrier_DetectedAndPassedThrough()
    {
        // viiper прислал заполненный носитель 0x36 (аудиоканал: хаптики PCM + динамик).
        var payload = new byte[474];
        var usb = new byte[48];
        usb[0] = 0x02;
        usb.CopyTo(payload, 28);
        payload[76] = 0x36; // начало комбинированного отчёта
        payload[77] = 0x20;
        Assert.True(DualSenseFeedback.TryParse(payload, out var fb));

        Assert.True(fb.HasCombinedCarrier);
        Assert.Equal(398, fb.CombinedBtCarrier.Length);
        Assert.Equal(0x36, fb.CombinedBtCarrier[0]);
        Assert.Equal(0x20, fb.CombinedBtCarrier[1]);

        // Пустой носитель (dualsensegamepadv5) — не детектируется.
        Assert.False(DualSenseFeedback.TryParse(new byte[474], out var empty) && empty.HasCombinedCarrier);
    }
}

/// <summary>Тесты монитора диагностики отдачи.</summary>
public class FeedbackMonitorTests
{
    private static DualSenseFeedback MakeFeedback(byte motorR = 0, byte motorL = 0, byte r = 0, byte playerLeds = 0)
    {
        var payload = new byte[474];
        var usb = new byte[48];
        usb[0] = 0x02;
        usb[1] = (byte)((motorR | motorL) != 0 ? 0x03 : 0);
        usb[2] = (byte)((r | playerLeds) != 0 ? 0x14 : 0);
        usb[3] = motorR;
        usb[4] = motorL;
        usb[1 + DualSenseProtocol.OffLightbarRed] = r;
        usb[1 + DualSenseProtocol.OffPlayerLeds] = playerLeds;
        usb.CopyTo(payload, 28);
        DualSenseFeedback.TryParse(payload, out var fb);
        return fb;
    }

    [Fact]
    public void Reports_OnlyOnChange_And_RateLimitsInfo()
    {
        var m = new FeedbackMonitor();
        Assert.NotNull(m.Register(MakeFeedback(motorR: 0x80)));   // первое — в журнал
        Assert.Null(m.Register(MakeFeedback(motorR: 0x80)));      // дубль — тишина
        Assert.Null(m.Register(MakeFeedback(motorR: 0x80)));      // ещё дубль
        // Новое состояние в пределах 500 мс: изменение ЗАФИКСИРОВАНО (Changes),
        // но строка в журнал не идёт (лимит 2/с при аудиоканале ~200 Гц).
        Assert.Null(m.Register(MakeFeedback(motorR: 0x80, motorL: 0x40)));
        Assert.Equal(4, m.Frames);
        // Сводка строится не чаще 2/с — на 4-м кадре (в пределах окна) её ещё нет.
    }

    [Fact]
    public void Summary_ContainsAllFieldGroups()
    {
        var m = new FeedbackMonitor();
        var summary = m.Register(MakeFeedback(motorR: 0x80, r: 0xFF, playerLeds: 0x0A))!;
        Assert.Contains("моторы 128/0", summary);
        Assert.Contains("RGB #FF0000", summary);
        Assert.Contains("playerLED 0x0A", summary);
        Assert.Contains("аудиоканал 0x36", summary);
        Assert.Contains("viiper:", summary);
    }
}

// ============================================================== v1.6.0: слияние команд

public class OutputMergerTests
{
    private static byte[] Common(byte flag0, byte flag1, byte flag2,
        byte motorR = 0, byte motorL = 0, byte trigR2 = 0, byte r = 0, byte g = 0, byte b = 0, byte leds = 0)
    {
        var c = new byte[DualSenseProtocol.OutputCommonSize];
        c[DualSenseProtocol.OffValidFlag0] = flag0;
        c[DualSenseProtocol.OffValidFlag1] = flag1;
        c[DualSenseProtocol.OffValidFlag2] = flag2;
        c[DualSenseProtocol.OffMotorRight] = motorR;
        c[DualSenseProtocol.OffMotorLeft] = motorL;
        if (trigR2 != 0) c[DualSenseProtocol.OffTriggerRight] = trigR2;
        c[DualSenseProtocol.OffLightbarRed] = r;
        c[DualSenseProtocol.OffLightbarGreen] = g;
        c[DualSenseProtocol.OffLightbarBlue] = b;
        c[DualSenseProtocol.OffPlayerLeds] = leds;
        return c;
    }

    [Fact]
    public void SeparateReports_MergeIntoOneState()
    {
        var m = new OutputMerger();
        m.Apply(Common(0x04, 0, 0, trigR2: 0x25));  // только триггер R2
        m.Apply(Common(0x03, 0, 0, motorR: 0x80));  // только вибрация
        m.Apply(Common(0, 0x04, 0, r: 0xFF, g: 0x40)); // только подсветка

        var s = m.Current;
        Assert.Equal(0x07, s[DualSenseProtocol.OffValidFlag0]); // триггер + вибрация
        Assert.Equal(0x04, s[DualSenseProtocol.OffValidFlag1]); // подсветка
        Assert.Equal(0x25, s[DualSenseProtocol.OffTriggerRight]);
        Assert.Equal(0x80, s[DualSenseProtocol.OffMotorRight]);
        Assert.Equal(0xFF, s[DualSenseProtocol.OffLightbarRed]);
    }

    [Fact]
    public void TriggerExplicitOff_Applied_FlaglessRumble_DoesNotClearTrigger()
    {
        var m = new OutputMerger();
        m.Apply(Common(0x04, 0, 0, trigR2: 0x25));       // weapon R2
        m.Apply(Common(0x04, 0, 0, trigR2: 0x00));       // явный Off (флаг есть, режим 0)
        Assert.Equal(0x00, m.Current[DualSenseProtocol.OffTriggerRight]);

        m.Apply(Common(0x04, 0, 0, trigR2: 0x21));
        m.Apply(Common(0x03, 0, 0, motorR: 0x40));       // вибрация без флага триггера
        Assert.Equal(0x21, m.Current[DualSenseProtocol.OffTriggerRight]); // триггер не тронут
    }

    [Fact]
    public void VibrationV2_Selector_AlsoUpdatesMotors()
    {
        // Новые SDK: вибрация только через flag2 bit 2 (COMPATIBLE_VIBRATION2).
        var m = new OutputMerger();
        m.Apply(Common(0, 0, 0x04, motorR: 0x64, motorL: 0x32));
        var s = m.Current;
        Assert.Equal(0x64, s[DualSenseProtocol.OffMotorRight]);
        Assert.Equal(0x32, s[DualSenseProtocol.OffMotorLeft]);
        Assert.Equal(0x03, s[DualSenseProtocol.OffValidFlag0]); // классические биты добавлены
        Assert.Equal(0x04, s[DualSenseProtocol.OffValidFlag2]);
    }

    [Fact]
    public void PlayerLeds_And_Lightbar_MergeIndependently()
    {
        var m = new OutputMerger();
        m.Apply(Common(0, 0x10, 0, leds: 0x0A));
        m.Apply(Common(0, 0x04, 0, r: 0x10, g: 0xFF, b: 0x20));
        var s = m.Current;
        Assert.Equal(0x0A, s[DualSenseProtocol.OffPlayerLeds]);
        Assert.Equal(0x20, s[DualSenseProtocol.OffLightbarBlue]);
        Assert.Equal(0x14, s[DualSenseProtocol.OffValidFlag1]);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var m = new OutputMerger();
        m.Apply(Common(0x03, 0, 0, motorR: 0xFF));
        m.Reset();
        Assert.All(m.Current, b => Assert.Equal(0, b));
    }

    [Fact]
    public void MergedPath_TranslatorKeepsPatches()
    {
        // Слитое состояние проходит транслятор: флаги совместимости + игрок 1.
        var m = new OutputMerger();
        m.Apply(Common(0, 0x10, 0, leds: DualSenseProtocol.PlayerLedPlayer2));
        var t = new FeedbackTranslator { DefaultLightbar = false }; // ForcePlayerOneLed по умолчанию
        var common = t.TranslateMerged(m.Current)!;
        Assert.Equal(DualSenseProtocol.PlayerLedPlayer1, common.Data[DualSenseProtocol.OffPlayerLeds]);
        Assert.Equal(DualSenseProtocol.Flag1PlayerIndicatorControl, common.Data[DualSenseProtocol.OffValidFlag1]);
    }
}

// ============================================================== v1.6.1: 0x84, тишина, патч носителя

public class CarrierTests
{
    /// <summary>Собирает payload 474 Б с заполненным носителем 0x36.</summary>
    private static DualSenseFeedback MakeCarrier(bool haptics = false, bool speaker = false,
        byte playerLeds = 0, byte flag1 = 0)
    {
        var payload = new byte[474];
        var usb = new byte[48];
        usb[0] = 0x02;
        usb.CopyTo(payload, 28);
        payload[76] = 0x36;                       // начало носителя
        payload[76 + 1] = 0x20;                   // seq
        // состояние: [76+13 .. 76+59]; player LED на смещении 13+43
        payload[76 + 13 + 1] = flag1;             // valid_flag1 внутри состояния
        payload[76 + 13 + DualSenseProtocol.OffPlayerLeds] = playerLeds;
        payload[76 + DualSenseFeedback.CarrierSpeakerOffset] = 0x93;
        payload[76 + DualSenseFeedback.CarrierSpeakerOffset + 1] = speaker ? (byte)200 : (byte)0;
        if (haptics) payload[76 + DualSenseFeedback.CarrierHapticsOffset + 5] = 0x7F;
        Assert.True(DualSenseFeedback.TryParse(payload, out var fb));
        return fb;
    }

    [Fact]
    public void SilentCarrier_Detected()
    {
        Assert.True(MakeCarrier().IsCarrierSilent);                    // полная тишина
        Assert.False(MakeCarrier(haptics: true).IsCarrierSilent);      // есть хаптики
        Assert.False(MakeCarrier(speaker: true).IsCarrierSilent);      // есть звук динамика
    }

    [Fact]
    public void PatchCarrier_ForcesPlayerOne_And_RecomputesCrc()
    {
        var fb = MakeCarrier(haptics: true, playerLeds: DualSenseProtocol.PlayerLedPlayer2, flag1: 0x10);
        var t = new FeedbackTranslator();
        var carrier = t.PatchCarrier(fb.CombinedBtCarrier);

        // Состояние внутри носителя: игрок 2 -> игрок 1
        Assert.Equal(DualSenseProtocol.PlayerLedPlayer1,
            carrier[DualSenseFeedback.CarrierStateOffset + DualSenseProtocol.OffPlayerLeds]);

        // CRC пересчитан и валиден (seed 0xEADA2D49 по [0..393])
        uint crc = Crc32.Update(DualSenseFeedback.CarrierCrcSeed,
            carrier.AsSpan(0, DualSenseFeedback.CarrierCrcOffset));
        uint stored = (uint)(carrier[DualSenseFeedback.CarrierCrcOffset]
                           | (carrier[DualSenseFeedback.CarrierCrcOffset + 1] << 8)
                           | (carrier[DualSenseFeedback.CarrierCrcOffset + 2] << 16)
                           | (carrier[DualSenseFeedback.CarrierCrcOffset + 3] << 24));
        Assert.Equal(crc, stored);

        // Хаптики не тронуты
        Assert.Equal(0x7F, carrier[DualSenseFeedback.CarrierHapticsOffset + 5]);
    }

    [Fact]
    public void PatchCarrier_IgnoresMalformed()
    {
        var t = new FeedbackTranslator();
        Assert.Equal(Array.Empty<byte>(), t.PatchCarrier(Array.Empty<byte>()));
        var notCarrier = new byte[398]; // нет 0x36 в [0]
        var same = t.PatchCarrier(notCarrier);
        Assert.Same(notCarrier, same);
        Assert.Equal(0, same[394]); // CRC не перезаписан
    }

    [Fact]
    public void Frame084_EncodeDecode_RoundTrip()
    {
        // 0x84 — те же 474 байта: кадр кодируется/декодируется стандартным V5.
        var payload = new byte[474];
        payload[76] = 0x36;
        payload[76 + DualSenseFeedback.CarrierHapticsOffset] = 0x55;
        var frame = V5Frame.Encode(V5Frame.TypeRealtimeHaptics, 42, payload);

        Assert.True(V5Frame.TryDecode(frame, out byte type, out uint seq, out var decoded, out bool broken, out _));
        Assert.Equal((byte)0x84, type);
        Assert.Equal(42u, seq);
        Assert.False(broken);
        Assert.True(DualSenseFeedback.TryParse(decoded, out var fb));
        Assert.True(fb.HasCombinedCarrier);
        Assert.False(fb.IsCarrierSilent);
    }
}

// ============================================================== v1.7.0: джиттер-буфер хаптик

public class HapticsChannelTests
{
    private static byte[] Block(byte sample = 0x40, byte leds = 0x0A)
    {
        var b = new byte[398];
        b[0] = 0x36;
        b[1] = 0x20;
        b[DualSenseFeedback.CarrierHapticsOffset + 3] = sample;
        b[DualSenseFeedback.CarrierHapticsOffset + 40] = sample;
        b[DualSenseFeedback.CarrierStateOffset + 1] = 0x10; // flag1
        b[DualSenseFeedback.CarrierStateOffset + DualSenseProtocol.OffPlayerLeds] = leds;
        return b;
    }

    [Fact]
    public void Duplicates_Rejected()
    {
        var ch = new HapticsChannel(capacity: 4);
        Assert.True(ch.Enqueue(Block(), 0x83));
        Assert.False(ch.Enqueue(Block(), 0x84)); // двойник 0x84 сразу после 0x83 — дубликат
        Assert.Equal(1, ch.Count);
        Assert.Equal(1, ch.DroppedDuplicates);

        // Отличается дорожка хаптик — принимается
        Assert.True(ch.Enqueue(Block(sample: 0x55), 0x83));
        Assert.Equal(2, ch.Count);
    }

    [Fact]
    public void Overflow_DropsOldest()
    {
        var ch = new HapticsChannel(capacity: 3);
        for (int i = 1; i <= 5; i++)
            ch.Enqueue(Block(sample: (byte)i), 0x83);
        Assert.Equal(3, ch.Count);
        Assert.Equal(2, ch.DroppedOverflow);

        // В очереди остались САМЫЕ СВЕЖИЕ блоки (4 и 5 и ...): проверим порядок
        var first = ch.TryDequeue()!;
        Assert.Equal(3, first[DualSenseFeedback.CarrierHapticsOffset + 3]); // 3-й остался? нет: 1,2 выпали
    }

    [Fact]
    public void MuteBlock_ZerosHaptics_AndKeepsCrcValid()
    {
        var ch = new HapticsChannel();
        ch.Enqueue(Block(sample: 0x7F), 0x83);
        var written = ch.TryDequeue()!;
        Assert.NotNull(written);

        var mute = ch.BuildMuteBlock()!;
        // Хаптики занулены
        Assert.Equal(0, mute[DualSenseFeedback.CarrierHapticsOffset + 3]);
        Assert.Equal(0, mute[DualSenseFeedback.CarrierHapticsOffset + 40]);
        // Состояние сохранено
        Assert.Equal(written[DualSenseFeedback.CarrierStateOffset + DualSenseProtocol.OffPlayerLeds],
            mute[DualSenseFeedback.CarrierStateOffset + DualSenseProtocol.OffPlayerLeds]);
        // CRC валиден
        uint crc = Crc32.Update(DualSenseFeedback.CarrierCrcSeed,
            mute.AsSpan(0, DualSenseFeedback.CarrierCrcOffset));
        uint stored = (uint)(mute[DualSenseFeedback.CarrierCrcOffset]
                           | (mute[DualSenseFeedback.CarrierCrcOffset + 1] << 8)
                           | (mute[DualSenseFeedback.CarrierCrcOffset + 2] << 16)
                           | (mute[DualSenseFeedback.CarrierCrcOffset + 3] << 24));
        Assert.Equal(crc, stored);
    }

    [Fact]
    public void TryParse_NoCarrier_SkipsCopy()
    {
        var payload = new byte[474];
        var usb = new byte[48];
        usb[0] = 0x02;
        usb.CopyTo(payload, 28);
        Assert.True(DualSenseFeedback.TryParse(payload, out var fb));
        Assert.False(fb.HasCombinedCarrier);
        Assert.All(fb.CombinedBtCarrier, b => Assert.Equal(0, b));
    }
}

// ============================================================== v1.8.0: дорожки, окно дедупа, усиление

public class HapticsLaneTests
{
    private static byte[] Block(byte sample = 0x40)
    {
        var b = new byte[398];
        b[0] = 0x36;
        b[DualSenseFeedback.CarrierHapticsOffset + 3] = sample;
        b[DualSenseFeedback.CarrierStateOffset + 1] = 0x10;
        return b;
    }

    [Fact]
    public void IdenticalBlocks_AfterWindow_AreAccepted()
    {
        // Ровный тон: одинаковые блоки с интервалом >8 мс — это НОРМАЛЬНЫЙ поток,
        // а не пара 0x83+0x84. Отбрасывать их нельзя (иначе хаптика «вполовину»).
        var ch = new HapticsChannel();
        Assert.True(ch.Enqueue(Block(), 0x83));
        System.Threading.Thread.Sleep(HapticsChannel.DuplicateWindow + TimeSpan.FromMilliseconds(10));
        Assert.True(ch.Enqueue(Block(), 0x84)); // двойник позже окна — принят
        Assert.Equal(0, ch.DroppedDuplicates);
        Assert.Equal(2, ch.Count);
    }

    [Fact]
    public void BackToBack_Identical_Rejected()
    {
        var ch = new HapticsChannel();
        Assert.True(ch.Enqueue(Block(), 0x83));
        Assert.False(ch.Enqueue(Block(), 0x84)); // двойник вплотную — дубликат
        Assert.Equal(1, ch.DroppedDuplicates);

        // v1.9.9: одинаковые блоки БЕЗ пары 0x83→0x84 — легитимный поток (ровный
        // тон/тишина), НЕ дубликаты: ложное отбрасывание давало «пропадания звука».
        var ch2 = new HapticsChannel();
        Assert.True(ch2.Enqueue(Block(), 0x83));
        Assert.True(ch2.Enqueue(Block(), 0x83)); // подряд одинаковые 0x83 — принимать!
        Assert.False(ch2.Enqueue(Block(), 0x84)); // двойник ПОСЛЕДНЕГО 0x83 с тем же контентом — дубликат
        Assert.True(ch2.Enqueue(Block(sample: 0x41), 0x84)); // другой контент — принимать!
        var ch3 = new HapticsChannel();
        Assert.True(ch3.Enqueue(Block(), 0x84));
        Assert.True(ch3.Enqueue(Block(), 0x84)); // только 0x84 — принимать!
    }

    [Fact]
    public void PatchCarrier_HapticsGain_ScalesAndClamps()
    {
        var fb = new DualSenseFeedback();
        var carrier = new byte[398];
        carrier[0] = 0x36;
        carrier[DualSenseFeedback.CarrierHapticsOffset] = 0x20;     // +32
        carrier[DualSenseFeedback.CarrierHapticsOffset + 1] = 0xE0; // -32
        carrier[DualSenseFeedback.CarrierHapticsOffset + 2] = 0x7F; // +127 (максимум)
        fb.CombinedBtCarrier.AsSpan().Fill(0);
        carrier.CopyTo(fb.CombinedBtCarrier, 0);

        var t = new FeedbackTranslator { HapticsGain = 2.0, ForcePlayerOneLed = false };
        var patched = t.PatchCarrier(fb.CombinedBtCarrier);

        Assert.Equal(0x40, patched[DualSenseFeedback.CarrierHapticsOffset]);      // 32*2=64
        Assert.Equal(0xC0, (byte)patched[DualSenseFeedback.CarrierHapticsOffset + 1]); // -32*2=-64
        Assert.Equal(0x7F, patched[DualSenseFeedback.CarrierHapticsOffset + 2]);  // кламп 127

        // CRC валиден после усиления
        uint crc = Crc32.Update(DualSenseFeedback.CarrierCrcSeed,
            patched.AsSpan(0, DualSenseFeedback.CarrierCrcOffset));
        uint stored = (uint)(patched[DualSenseFeedback.CarrierCrcOffset]
                           | (patched[DualSenseFeedback.CarrierCrcOffset + 1] << 8)
                           | (patched[DualSenseFeedback.CarrierCrcOffset + 2] << 16)
                           | (patched[DualSenseFeedback.CarrierCrcOffset + 3] << 24));
        Assert.Equal(crc, stored);
    }
}

// ============================================================== v1.9.0: контроль потока

public class FlowControlTests
{
    [Theory]
    [InlineData(10.5, 0, 10.5)]     // быстрый канал — базовый темп
    [InlineData(10.5, 8, 10.5)]     // запись 8 мс — всё ещё базовый
    [InlineData(10.5, 20, 25.0)]    // запись 20 мс — темп 25 мс (не заливаем)
    [InlineData(10.5, 40, 50.0)]    // запись 40 мс — темп 50 мс
    public void EffectivePace_FollowsChannelSpeed(double baseMs, double emaMs, double expectedMs)
    {
        Assert.Equal(expectedMs, BluetoothHidWriter.EffectiveHapticsPaceMs(baseMs, emaMs), 3);
    }

    [Theory]
    [InlineData(31, 2, false)]   // очередь мала — деградация не нужна
    [InlineData(10, 9, false)]   // канал жив
    [InlineData(31, 3, false)]   // канал медленный, но очередь НЕ глубокая — НЕ рвём поток (v1.9.9)
    [InlineData(10, 12, false)]  // очередь глубокая, но канал жив — ускоренный слив без потерь
    [InlineData(31, 13, true)]   // канал тонет + очередь глубокая — деградация
    [InlineData(60, 15, true)]
    public void Degradation_OnlyWhenChannelDrowns(double emaMs, int queue, bool expected)
    {
        Assert.Equal(expected, BluetoothHidWriter.ShouldDegrade(emaMs, queue));
    }

    [Fact]
    public void DropOldest_DropsFrontBlock()
    {
        var ch = new HapticsChannel(capacity: 8);
        var b1 = new byte[398]; b1[0] = 0x36; b1[78] = 1;
        var b2 = new byte[398]; b2[0] = 0x36; b2[78] = 2;
        var b3 = new byte[398]; b3[0] = 0x36; b3[78] = 3;
        ch.Enqueue(b1); ch.Enqueue(b2); ch.Enqueue(b3);
        ch.DropOldest();
        Assert.Equal(2, ch.Count);
        var front = ch.TryDequeue()!;
        Assert.Equal(2, front[78]); // выпал ПЕРВЫЙ (старейший)
        Assert.Equal(1, ch.DroppedBackoff);
    }
}

// ============================================================== v1.9.9: адаптивный темп 0x36 и индикация

public class AdaptivePaceTests
{
    // ГЛАВНЫЙ кейс: аудио идёт с честных 100 Гц (10 мс — замер по захватам PS5:
    // 7191 кадр за 71,91 с) — темп должен стать 10 мс, а не фиксированные 10,5
    // (93,75 Гц): дефицит 0,5 мс на блок переполнял очередь = «пропадания звука».
    [Theory]
    [InlineData(10.5, 10.0, 0, 10.0)]    // аудио 100 Гц — темп 10 мс
    [InlineData(10.5, 10.67, 0, 10.67)]  // хаптики 93,75 Гц — темп 10,67 мс
    [InlineData(10.5, 0, 0, 10.5)]       // скорость не измерена — база
    [InlineData(10.5, 10.0, 4, 7.5)]     // очередь растёт — догоняем (×0.75)
    [InlineData(10.5, 10.0, 8, 5.0)]     // очередь глубокая — авральный слив (×0.5)
    [InlineData(10.5, 10.0, 16, 5.0)]    // переполнение на подходе — слив
    [InlineData(10.5, 2.0, 0, 4.0)]      // быстрее 4 мс не пишем (кламп)
    [InlineData(10.5, 50.0, 0, 12.0)]    // медленнее 12 мс не замедляемся (кламп)
    public void Pace_FollowsProducer_Rate_And_Queue(double baseMs, double arrivalMs, int queue, double expectedMs)
    {
        Assert.Equal(expectedMs, BluetoothHidWriter.ComputeHapticsPaceMs(baseMs, arrivalMs, queue), 3);
    }
}

public class IndicatorPatchTests
{
    private static byte[] EmptyCommon() => new byte[DualSenseProtocol.OutputCommonSize];

    [Fact]
    public void PlayerLed_LitEvenWhenGameSilent()
    {
        // игра НЕ управляет player-LED — зажигаем «игрок 1» с максимальной яркостью
        var t = new FeedbackTranslator { ForcePlayerOneLed = true, DefaultLightbar = false };
        var d = EmptyCommon();
        t.PatchStateBlock(d);
        Assert.Equal(DualSenseProtocol.PlayerLedPlayer1, d[DualSenseProtocol.OffPlayerLeds]);
        Assert.True((d[DualSenseProtocol.OffValidFlag1] & DualSenseProtocol.Flag1PlayerIndicatorControl) != 0);
        Assert.True((d[DualSenseProtocol.OffValidFlag2] & DualSenseProtocol.Flag2LedBrightnessControl) != 0);
        Assert.Equal(0, d[DualSenseProtocol.OffLedBrightness]); // 0 = максимальная яркость
    }

    [Fact]
    public void PlayerLed_ForcedWhenGameSetsOtherMask()
    {
        var t = new FeedbackTranslator { ForcePlayerOneLed = true, DefaultLightbar = false };
        var d = EmptyCommon();
        d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1PlayerIndicatorControl;
        d[DualSenseProtocol.OffPlayerLeds] = DualSenseProtocol.PlayerLedPlayer3;
        t.PatchStateBlock(d);
        Assert.Equal(DualSenseProtocol.PlayerLedPlayer1, d[DualSenseProtocol.OffPlayerLeds]);
    }

    [Fact]
    public void PlayerLed_Disabled_RespectsGame()
    {
        var t = new FeedbackTranslator { ForcePlayerOneLed = false, DefaultLightbar = false };
        var d = EmptyCommon();
        t.PatchStateBlock(d);
        Assert.Equal(0, d[DualSenseProtocol.OffPlayerLeds]); // не трогаем
    }

    [Fact]
    public void DefaultLightbar_AppliedWhenGameSilent()
    {
        var t = new FeedbackTranslator { ForcePlayerOneLed = false, DefaultLightbar = true };
        t.DefaultLightbarRed = 0x12; t.DefaultLightbarGreen = 0x34; t.DefaultLightbarBlue = 0x56;
        var d = EmptyCommon();
        t.PatchStateBlock(d);
        Assert.Equal(0x12, d[DualSenseProtocol.OffLightbarRed]);
        Assert.Equal(0x34, d[DualSenseProtocol.OffLightbarGreen]);
        Assert.Equal(0x56, d[DualSenseProtocol.OffLightbarBlue]);
        Assert.True((d[DualSenseProtocol.OffValidFlag1] & DualSenseProtocol.Flag1LightbarControl) != 0);
    }

    [Fact]
    public void DefaultLightbar_YieldsToGame()
    {
        // игра выставила флаг и свой цвет — не вмешиваемся
        var t = new FeedbackTranslator { ForcePlayerOneLed = false, DefaultLightbar = true };
        var d = EmptyCommon();
        d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1LightbarControl;
        d[DualSenseProtocol.OffLightbarRed] = 0xAA;
        t.PatchStateBlock(d);
        Assert.Equal(0xAA, d[DualSenseProtocol.OffLightbarRed]);

        // игра прислала цвет без флага (патч совместимости добавит флаг) — тоже не вмешиваемся
        var d2 = EmptyCommon();
        d2[DualSenseProtocol.OffLightbarGreen] = 0xBB;
        t.PatchStateBlock(d2);
        Assert.Equal(0xBB, d2[DualSenseProtocol.OffLightbarGreen]);
        Assert.Equal(0, d2[DualSenseProtocol.OffLightbarRed]); // свой цвет НЕ подставлен
    }

    [Fact]
    public void DefaultLightbar_Disabled_Off()
    {
        var t = new FeedbackTranslator { ForcePlayerOneLed = false, DefaultLightbar = false };
        var d = EmptyCommon();
        t.PatchStateBlock(d);
        Assert.Equal(0, d[DualSenseProtocol.OffLightbarRed]);
        Assert.True((d[DualSenseProtocol.OffValidFlag1] & DualSenseProtocol.Flag1LightbarControl) == 0);
    }
}

// ============================================================== v1.10.0: медиа-тракт (пейсер + перепрошивка seq)

public class MediaPacerTests
{
    private static byte[] Carrier(byte seqNibble = 3, byte typeNibble = 2, byte sample = 0x40)
    {
        var b = new byte[398];
        b[0] = 0x36;
        b[1] = (byte)((seqNibble << 4) | typeNibble); // seq | тип
        b[DualSenseFeedback.CarrierHapticsOffset + 3] = sample;
        b[DualSenseFeedback.CarrierStateOffset + 1] = 0x10;
        return b;
    }

    private static uint StoredCrc(byte[] b) =>
        (uint)(b[DualSenseFeedback.CarrierCrcOffset]
             | (b[DualSenseFeedback.CarrierCrcOffset + 1] << 8)
             | (b[DualSenseFeedback.CarrierCrcOffset + 2] << 16)
             | (b[DualSenseFeedback.CarrierCrcOffset + 3] << 24));

    [Fact]
    public void Resequence_RewritesSeqKeepsType_AndCrcValid()
    {
        var c = Carrier(seqNibble: 3, typeNibble: 2);
        CarrierPacer.Resequence(c, 7);
        Assert.Equal(0x72, c[1]); // seq 7 в старшем ниббле, тип 2 сохранён
        uint crc = Crc32.Update(DualSenseFeedback.CarrierCrcSeed,
            c.AsSpan(0, DualSenseFeedback.CarrierCrcOffset));
        Assert.Equal(crc, StoredCrc(c));
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(15, 0xF0)]
    [InlineData(16, 0x00)]  // кольцо 0..15
    [InlineData(17, 0x10)]
    public void Resequence_SeqWrapsModulo16(int seq, byte highNibble)
    {
        var c = Carrier(seqNibble: 9, typeNibble: 0);
        CarrierPacer.Resequence(c, (byte)seq);
        Assert.Equal(highNibble, (byte)(c[1] & 0xF0));
        uint crc = Crc32.Update(DualSenseFeedback.CarrierCrcSeed,
            c.AsSpan(0, DualSenseFeedback.CarrierCrcOffset));
        Assert.Equal(crc, StoredCrc(c));
    }

    [Fact]
    public void NativeCadence_AndV1101Tuning()
    {
        // Референс hbashton/DS4Windows: «one complete speaker/haptics generation
        // per 10.667 ms». Заряд референса 8; v1.10.1 — 3 (низкая задержка:
        // 8 × 10,667 = 85 мс лишней задержки), запас дают 8 слотов «в полёте».
        Assert.Equal(10.667, CarrierPacer.CadenceMs, 3);
        Assert.Equal(3, CarrierPacer.PrimeCount);
        Assert.Equal(4, CarrierPacer.CatchUpDepth);
        Assert.Equal(250, CarrierPacer.MuteFailsafeAfter.TotalMilliseconds);
    }

    [Fact]
    public void Resequence_DoesNotTouchPayload()
    {
        var c = Carrier();
        c[DualSenseFeedback.CarrierHapticsOffset + 10] = 0x5A;
        c[DualSenseFeedback.CarrierStateOffset + 20] = 0xA5;
        var before = (byte[])c.Clone();
        CarrierPacer.Resequence(c, 12);
        Assert.Equal(before[DualSenseFeedback.CarrierHapticsOffset + 10], c[DualSenseFeedback.CarrierHapticsOffset + 10]);
        Assert.Equal(before[DualSenseFeedback.CarrierStateOffset + 20], c[DualSenseFeedback.CarrierStateOffset + 20]);
    }
}

// ============================================================== v1.12.0: звук динамика (Opus-кодирование мостом)

public class SpeakerOpusTests
{
    private static byte[] AtomicPayload(byte[]? pcm = null, byte speakerSample = 0x77)
    {
        var fb = new byte[474];
        fb[0] = 0; fb[1] = 0; // моторы
        fb[76] = 0x36;        // BT-носитель есть
        fb[76 + DualSenseFeedback.CarrierSpeakerOffset] = 0x93;
        fb[76 + DualSenseFeedback.CarrierSpeakerOffset + 1] = 0; // дорожка ПУСТА (так шлёт viiper)
        var pcmBuf = pcm ?? new byte[1920];
        if (pcm != null) { /* уже готов */ }
        var payload = new byte[2 + 474 + pcmBuf.Length];
        payload[0] = (byte)(474 & 0xFF); payload[1] = (byte)(474 >> 8);
        fb.CopyTo(payload, 2);
        pcmBuf.CopyTo(payload, 2 + 474);
        return payload;
    }

    [Fact]
    public void TryParseAtomic_ExtractsSpeakerPcmTail()
    {
        var pcm = new byte[1920];
        pcm[0] = 0x11; pcm[1] = 0x22;
        var payload = AtomicPayload(pcm);
        Assert.True(DualSenseFeedback.TryParseAtomic(payload, out var fb, out var tail));
        Assert.NotNull(tail);
        Assert.Equal(1920, tail!.Length);
        Assert.Equal(0x11, tail[0]);
        Assert.True(fb.HasCombinedCarrier);
    }

    [Fact]
    public void TryParseAtomic_WithoutTail_ReturnsNullPcm()
    {
        // старый/усечённый кадр — хвоста нет, но обратная связь разбирается
        var payload = new byte[2 + 474];
        payload[0] = (byte)(474 & 0xFF); payload[1] = (byte)(474 >> 8);
        payload[2 + 76] = 0x36;
        Assert.True(DualSenseFeedback.TryParseAtomic(payload, out _, out var tail));
        Assert.Null(tail);
    }

    [Fact]
    public void Encoder_ProducesExact200ByteCbrFrame_InLane()
    {
        // контракт DS4Windows: 480 кадров S16LE → РОВНО 200 Б Opus в дорожке [144..343]
        var enc = new DualSenseBridge.Core.Output.SpeakerOpusEncoder();
        var carrier = new byte[398];
        carrier[0] = 0x36;
        var pcm = new byte[1920];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xFF); // шум
        Assert.True(enc.EncodeInto(carrier, pcm));
        Assert.Equal(0x93, carrier[DualSenseFeedback.CarrierSpeakerOffset]);
        Assert.Equal(200, carrier[DualSenseFeedback.CarrierSpeakerOffset + 1]);
        Assert.Equal(DualSenseBridge.Core.Output.SpeakerOpusEncoder.MediaBufferDepth, carrier[5]);
        Assert.Equal(1, enc.Frames);
        Assert.Equal(0, enc.Rejected);
    }

    [Fact]
    public void Encoder_Silence_AlsoEncodes()
    {
        var enc = new DualSenseBridge.Core.Output.SpeakerOpusEncoder();
        var carrier = new byte[398];
        Assert.True(enc.EncodeInto(carrier, new byte[1920]));
        Assert.Equal(200, carrier[DualSenseFeedback.CarrierSpeakerOffset + 1]);
    }

    [Fact]
    public void Encoder_WrongPcmLength_Rejected()
    {
        var enc = new DualSenseBridge.Core.Output.SpeakerOpusEncoder();
        Assert.False(enc.EncodeInto(new byte[398], new byte[1000]));
        Assert.Equal(0, enc.Frames);
    }
}

// ============================================================== v1.11.0: мост zero-fill хаптик (фикс «жужжания»)

public class RearLaneBridgeTests
{
    private static byte[] Carrier(byte sample = 0x40)
    {
        var b = new byte[398];
        b[0] = 0x36;
        b[1] = 0x20;
        for (int i = DualSenseFeedback.CarrierHapticsOffset;
             i < DualSenseFeedback.CarrierHapticsOffset + DualSenseFeedback.CarrierHapticsSize; i += 2)
            b[i] = sample;
        return b;
    }

    private static byte RearSample(byte[] c) => c[DualSenseFeedback.CarrierHapticsOffset];

    [Fact]
    public void ZeroFill_IsBridged_WithLastRealBlock()
    {
        // viiper: «each V5 media frame consumes one completed rear interval OR
        // ZERO-FILLS that lane» — каждый ~16-й кадр с нулевой дорожкой.
        var br = new RearLaneBridge();
        var t0 = DateTime.UtcNow;
        Assert.False(br.Bridge(Carrier(0x40), t0));                       // реальный блок — запомнен
        var gap = Carrier(0x00);
        Assert.True(br.Bridge(gap, t0.AddMilliseconds(11)));              // zero-fill → мост
        Assert.Equal(0x40, RearSample(gap));                              // подставлен последний реальный
        Assert.Equal(1, br.Bridged);
    }

    [Fact]
    public void EndOfEffect_AfterHoldExpiry_PassesZerosThrough()
    {
        // Нули дольше MaxHold = конец эффекта (игра владеет концом) — не мостится.
        var br = new RearLaneBridge();
        var t0 = DateTime.UtcNow;
        br.Bridge(Carrier(0x40), t0);
        var late = Carrier(0x00);
        Assert.False(br.Bridge(late, t0.AddMilliseconds(200)));
        Assert.Equal(0, RearSample(late));                                // нули прошли как есть
        Assert.Equal(1, br.Released);
    }

    [Fact]
    public void Zeros_WithoutHistory_PassThrough()
    {
        var br = new RearLaneBridge();
        var c = Carrier(0x00);
        Assert.False(br.Bridge(c, DateTime.UtcNow));
        Assert.Equal(0, RearSample(c));
        Assert.Equal(1, br.Released);
    }

    [Fact]
    public void RealBlock_NotModified_AndRefreshesHold()
    {
        var br = new RearLaneBridge();
        var t0 = DateTime.UtcNow;
        var a = Carrier(0x40);
        br.Bridge(a, t0);
        var b = Carrier(0x50);
        Assert.False(br.Bridge(b, t0.AddMilliseconds(30)));               // реальный — не тронут
        Assert.Equal(0x50, RearSample(b));
        var gap = Carrier(0x00);
        Assert.True(br.Bridge(gap, t0.AddMilliseconds(30 + 11)));          // hold от СВЕЖЕГО блока
        Assert.Equal(0x50, RearSample(gap));
    }

    [Fact]
    public void MaxHold_CoversThreeBlocks()
    {
        // Референс (DualSenseRearHapticsUnderrunGate): удержание ~3 блока
        // (3 × 10,667 мс ≈ 32 мс) + запас джиттера.
        Assert.True(RearLaneBridge.MaxHold.TotalMilliseconds is > 31 and <= 40);
    }
}

// ============================================================== v1.10.1: защёлка цвета подсветки (анти-мерцание)

public class LightbarLatchTests
{
    private static byte[] State(byte flag1, byte r, byte g, byte b)
    {
        var d = new byte[DualSenseProtocol.OutputCommonSize];
        d[DualSenseProtocol.OffValidFlag1] = flag1;
        d[DualSenseProtocol.OffLightbarRed] = r;
        d[DualSenseProtocol.OffLightbarGreen] = g;
        d[DualSenseProtocol.OffLightbarBlue] = b;
        return d;
    }

    [Fact]
    public void GameColor_Preserved_AndLatched()
    {
        var t = new FeedbackTranslator { ForcePlayerOneLed = false, DefaultLightbar = true };
        var d = State(DualSenseProtocol.Flag1LightbarControl, 0xAA, 0, 0);
        t.PatchStateBlock(d);
        Assert.Equal(0xAA, d[DualSenseProtocol.OffLightbarRed]); // цвет игры сохранён

        // В пределах окна чёрные кадры (пусть даже с флагом) не подменяются дефолтом
        var black = State(DualSenseProtocol.Flag1LightbarControl, 0, 0, 0);
        t.PatchStateBlock(black);
        Assert.Equal(0, black[DualSenseProtocol.OffLightbarRed]);
    }

    [Fact]
    public void BlackWithFlag_IsNotGameControl_GetsDefault()
    {
        // v1.10.1: RGB #000000 (пусть и с флагом) ≠ «игра управляет»: смеси кадров
        // «флаг есть/нет» при чёрном цвете мерцали подсветкой на каждом кадре.
        var t = new FeedbackTranslator { ForcePlayerOneLed = false, DefaultLightbar = true };
        var d = State(DualSenseProtocol.Flag1LightbarControl, 0, 0, 0);
        t.PatchStateBlock(d);
        Assert.True((d[DualSenseProtocol.OffValidFlag1] & DualSenseProtocol.Flag1LightbarControl) != 0);
        Assert.NotEqual(0, d[DualSenseProtocol.OffLightbarRed] | d[DualSenseProtocol.OffLightbarGreen] | d[DualSenseProtocol.OffLightbarBlue]);
    }

    [Fact]
    public void AfterLatchExpiry_DefaultReturns()
    {
        var t = new FeedbackTranslator { ForcePlayerOneLed = false, DefaultLightbar = true };
        t.PatchStateBlock(State(DualSenseProtocol.Flag1LightbarControl, 0xAA, 0, 0)); // цвет игры
        t.GameColorLatchWindow = TimeSpan.Zero; // окно истекло мгновенно
        var black = State(0, 0, 0, 0);
        t.PatchStateBlock(black);
        Assert.NotEqual(0, black[DualSenseProtocol.OffLightbarBlue]); // дефолт вернулся
    }

    [Fact]
    public void FreshTranslator_BlackGetsDefault()
    {
        var t = new FeedbackTranslator { ForcePlayerOneLed = false, DefaultLightbar = true };
        var d = State(0, 0, 0, 0);
        t.PatchStateBlock(d);
        Assert.NotEqual(0, d[DualSenseProtocol.OffLightbarBlue]);
    }
}

// ============================================================== v1.9.2: нативное перечисление (чистые парсеры)

public class WinHidParserTests
{
    [Fact]
    public void VidPid_ParsedFromPath()
    {
        var r = DualSenseBridge.Core.Hid.WinHid.TryParseVidPid(
            @"\\?\hid#vid_054c&pid_0ce6&col01#8&1de4d0ff&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
        Assert.NotNull(r);
        Assert.Equal(0x054C, r!.Value.vid);
        Assert.Equal(0x0CE6, r.Value.pid);
    }

    [Fact]
    public void VidPid_NullForBroken()
    {
        // чужой VID/PID парсится нормально — фильтрация по Sony выполняется в Enumerate
        var xbox = DualSenseBridge.Core.Hid.WinHid.TryParseVidPid(@"\\?\hid#vid_045e&pid_028e#x#{g}");
        Assert.Equal(0x045E, xbox!.Value.vid);
        Assert.Null(DualSenseBridge.Core.Hid.WinHid.TryParseVidPid(""));
        Assert.Null(DualSenseBridge.Core.Hid.WinHid.TryParseVidPid(@"\\?\hid#no vid here"));
    }

    [Fact]
    public void InstanceId_FromPath()
    {
        string id = DualSenseBridge.Core.Hid.WinHid.InstanceIdFromPath(
            @"\\?\hid#vid_054c&pid_0ce6&col01#8&1de4d0ff&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
        Assert.Equal(@"HID\VID_054C&PID_0CE6&COL01\8&1DE4D0FF&0&0000", id);
    }

    [Theory]
    [InlineData("BTHENUM\\VID_054C&PID_0CE6\\...", DualSenseBridge.Core.Protocol.Transport.Bluetooth, false)]
    [InlineData("BTHLE\\DEV_...", DualSenseBridge.Core.Protocol.Transport.Bluetooth, false)]
    [InlineData("USB\\VID_054C&PID_0CE6\\...", DualSenseBridge.Core.Protocol.Transport.Usb, false)]
    [InlineData("USBIP\\VID_054C&PID_0CE6\\...", DualSenseBridge.Core.Protocol.Transport.Unknown, true)]
    [InlineData(null, DualSenseBridge.Core.Protocol.Transport.Unknown, false)]
    public void Parent_Classified(string? parent, DualSenseBridge.Core.Protocol.Transport expected, bool usbip)
    {
        var (t, isUsbip) = DualSenseBridge.Core.Hid.WinHid.ClassifyParent(parent);
        Assert.Equal(expected, t);
        Assert.Equal(usbip, isUsbip);
    }

    // ============================================================== v1.9.3: паддинг записи до OutputReportByteLength

    [Fact]
    public void WriteBuffer_ExactLength_ReturnedAsIs()
    {
        // носитель 0x36 (398 Б) на BT-коллекции с максимумом 398 — пишется как есть
        var report = new byte[398];
        var buf = DualSenseBridge.Core.Hid.WinHidStream.PrepareWriteBuffer(report, 398);
        Assert.Same(report, buf);
    }

    [Fact]
    public void WriteBuffer_Short_PaddedToCap()
    {
        // отчёт состояния 0x31 (78 Б) на BT-коллекции с максимумом 398 —
        // обязан дополняться нулями до 398, иначе WriteFile отклоняет запись
        var report = new byte[78];
        for (int i = 0; i < report.Length; i++) report[i] = (byte)(i | 0x80);
        var buf = DualSenseBridge.Core.Hid.WinHidStream.PrepareWriteBuffer(report, 398);
        Assert.Equal(398, buf.Length);
        Assert.Equal(report, buf[..78]);               // префикс не тронут
        Assert.All(buf[78..], b => Assert.Equal(0, b)); // хвост добит нулями
    }

    [Fact]
    public void WriteBuffer_LongerThanCap_Throws()
    {
        var ex = Assert.Throws<System.IO.IOException>(
            () => DualSenseBridge.Core.Hid.WinHidStream.PrepareWriteBuffer(new byte[399], 398));
        Assert.Contains("398", ex.Message);
    }

    [Fact]
    public void WriteBuffer_UnknownCap_ReturnedAsIs()
    {
        // caps не прочитались (OutputCap = 0) — пишем как есть, лучшего варианта нет
        var report = new byte[78];
        Assert.Same(report, DualSenseBridge.Core.Hid.WinHidStream.PrepareWriteBuffer(report, 0));
    }

}

// ============================================================== v1.9.4: виртуальные usbip-устройства и транспорт по caps

public class WinHidVirtualDetectionTests
{
    [Theory]
    [InlineData(new string[] { "USB\\VID_054C&PID_0CE6\\5&abc", "USB\\ROOT_HUB&VID_1209&PID_8250&REV_0000\\4&def", "USBIPWIN\\vhci\\0" }, true)]   // WDM vHCI
    [InlineData(new string[] { "USB\\VID_054C&PID_0CE6\\5&abc", "ROOT\\VHCI_UDE\\0" }, true)]                                                    // UDE vHCI (0.9.x)
    [InlineData(new string[] { "USB\\VID_054C&PID_0CE6\\5&abc", "USB\\VID_1209&PID_8250\\1" }, true)]                                            // vhub usbip-win
    [InlineData(new string[] { "USBIP\\VID_054C&PID_0CE6\\1" }, true)]                                                                               // старый формат
    [InlineData(new string[] { "BTHENUM\\VID_054C&PID_0CE6\\8&x", "BTH\\DEV_0011223344CC\\8&y" }, false)]                                        // физический BT
    [InlineData(new string[] { "USB\\VID_054C&PID_0CE6\\5&abc", "USB\\VID_8087&PID_0029\\6&hub", "PCI\\VEN_8086&DEV_AAAA\\3&1" }, false)]    // физический USB
    public void VirtualUsbip_DetectedByAncestorChain(string[] chain, bool expected)
    {
        Assert.Equal(expected, DualSenseBridge.Core.Hid.WinHid.IsVirtualUsbipChain(chain));
    }

    [Fact]
    public void VirtualUsbip_EmptyAndNullChain_NotVirtual()
    {
        Assert.False(DualSenseBridge.Core.Hid.WinHid.IsVirtualUsbipChain(Array.Empty<string>()));
        Assert.False(DualSenseBridge.Core.Hid.WinHid.IsVirtualUsbipChain(new string?[] { null, "" }));
    }

    [Theory]
    [InlineData(78, 398, DualSenseBridge.Core.Protocol.Transport.Bluetooth)] // BT: input 0x31=78, output 0x36=398
    [InlineData(64, 48, DualSenseBridge.Core.Protocol.Transport.Usb)]        // USB: input 0x01=64, output 0x02=48
    [InlineData(0, 0, DualSenseBridge.Core.Protocol.Transport.Unknown)]      // caps не прочитаны
    [InlineData(49, 17, DualSenseBridge.Core.Protocol.Transport.Unknown)]    // чужая коллекция
    public void Transport_ClassifiedByCaps(int inputCap, int outputCap, DualSenseBridge.Core.Protocol.Transport expected)
    {
        Assert.Equal(expected, DualSenseBridge.Core.Hid.WinHid.ClassifyByCaps(inputCap, outputCap));
    }

}

// ============================================================== v1.9.5: фильтр DualSense в списке блокировки HidHide

public class HidHideRescueTests
{
    [Theory]
    [InlineData("HID\\VID_054C&PID_0CE6&COL01\\8&1DE4D0FF&0&0000", true)]   // DualSense BT
    [InlineData("HID\\VID_054C&PID_0CE6&COL02\\8&1DE4D0FF&0&0001", true)]   // вторая коллекция
    [InlineData("hid\\vid_054c&pid_0ce6&col01\\8&xyz&0&0000", true)]        // нижний регистр
    [InlineData("HID\\VID_054C&PID_0DF2&COL01\\8&1DE4D0FF&0&0000", true)]   // DualSense Edge
    [InlineData("HID\\VID_045E&PID_028E\\6&abc&0&0000", false)]             // Xbox — чужой
    [InlineData("HID\\VID_054C&PID_09CC\\7&def&0&0000", false)]             // DS4 — не DualSense
    [InlineData("", false)]
    public void DualSenseInstanceId_Detected(string id, bool expected)
    {
        Assert.Equal(expected, DualSenseBridge.Core.HidHide.HidHideManager.IsDualSenseInstanceId(id));
    }

}

// ============================================================== v1.9.6: отбор главной гейпадной коллекции

public class PrimaryCollectionOrderTests
{
    private static DualSenseBridge.Core.Hid.DualSenseDeviceInfo Dev(
        DualSenseBridge.Core.Protocol.Transport t, int inCap, int outCap, string path = "x") =>
        new() { DevicePath = path, InstanceId = path, Transport = t, VendorId = 0x054C,
                ProductId = 0x0CE6, ProductName = "DualSense",
                MaxInputReportLength = inCap, MaxOutputReportLength = outCap };

    [Fact]
    public void PrimaryCollection_First_EvenIfUsb_AheadOfSecondaryBt()
    {
        // Побочная BT-коллекция (сенсоры) не должна обыгрывать главную гейпадную.
        var list = new List<DualSenseBridge.Core.Hid.DualSenseDeviceInfo>
        {
            Dev(DualSenseBridge.Core.Protocol.Transport.Bluetooth, 21, 17, "col02"),  // побочная BT
            Dev(DualSenseBridge.Core.Protocol.Transport.Usb, 64, 48, "usb-col01"),    // главная USB
            Dev(DualSenseBridge.Core.Protocol.Transport.Bluetooth, 78, 398, "col01"), // главная BT
        };
        var ordered = DualSenseBridge.Core.Hid.WinHid.OrderPhysicalCandidates(list);
        Assert.Equal("col01", ordered[0].DevicePath);      // главная BT — первая
        Assert.Equal("usb-col01", ordered[1].DevicePath);  // главная USB — вторая
        Assert.Equal("col02", ordered[2].DevicePath);      // побочная — последняя
    }

    [Fact]
    public void PrimaryBt_Before_PrimaryUsb()
    {
        var list = new List<DualSenseBridge.Core.Hid.DualSenseDeviceInfo>
        {
            Dev(DualSenseBridge.Core.Protocol.Transport.Usb, 64, 48, "usb"),
            Dev(DualSenseBridge.Core.Protocol.Transport.Bluetooth, 78, 398, "bt"),
        };
        var ordered = DualSenseBridge.Core.Hid.WinHid.OrderPhysicalCandidates(list);
        Assert.Equal("bt", ordered[0].DevicePath);
        Assert.Equal("usb", ordered[1].DevicePath);
    }

    [Fact]
    public void UnknownCaps_Fallback_KeepsBtPriority()
    {
        // caps не прочитались — порядок как в v1.9.4: BT раньше USB.
        var list = new List<DualSenseBridge.Core.Hid.DualSenseDeviceInfo>
        {
            Dev(DualSenseBridge.Core.Protocol.Transport.Usb, 0, 0, "usb"),
            Dev(DualSenseBridge.Core.Protocol.Transport.Bluetooth, 0, 0, "bt"),
        };
        var ordered = DualSenseBridge.Core.Hid.WinHid.OrderPhysicalCandidates(list);
        Assert.Equal("bt", ordered[0].DevicePath);
        Assert.Equal("usb", ordered[1].DevicePath);
    }

}

// ============================================================== v1.9.7: арбитр — caps коллекции, защита от виртуального

public class CapsArbiterTests
{
    // ГЛАВНЫЙ тест v1.9.7: 64-байтовый отчёт 0x01 на BT-коллекции (cap 78) —
    // это минимальный BT-отчёт (или почерк виртуального устройства), но НЕ USB.
    // Раньше он «переворачивал» транспорт — мост замирал.
    [Fact]
    public void Report01_64Bytes_OnBtCollection_IsBluetooth()
    {
        var report = new byte[64]; report[0] = 0x01;
        Assert.Equal(DualSenseBridge.Core.Protocol.Transport.Bluetooth,
            DualSenseBridge.Core.Hid.DualSenseHidDevice.ClassifyFromReport(report, 78));
    }

    [Fact]
    public void Report01_64Bytes_OnUsbCollection_IsUsb()
    {
        var report = new byte[64]; report[0] = 0x01;
        Assert.Equal(DualSenseBridge.Core.Protocol.Transport.Usb,
            DualSenseBridge.Core.Hid.DualSenseHidDevice.ClassifyFromReport(report, 64));
    }

    [Fact]
    public void Report31_IsBluetooth_RegardlessOfCaps()
    {
        var report = new byte[78]; report[0] = 0x31;
        Assert.Equal(DualSenseBridge.Core.Protocol.Transport.Bluetooth,
            DualSenseBridge.Core.Hid.DualSenseHidDevice.ClassifyFromReport(report, 78));
        Assert.Equal(DualSenseBridge.Core.Protocol.Transport.Bluetooth,
            DualSenseBridge.Core.Hid.DualSenseHidDevice.ClassifyFromReport(report, 0));
    }

    [Fact]
    public void Report01_NoCaps_FallbackHeuristics()
    {
        var minimal = new byte[10]; minimal[0] = 0x01;
        Assert.Equal(DualSenseBridge.Core.Protocol.Transport.Bluetooth,
            DualSenseBridge.Core.Hid.DualSenseHidDevice.ClassifyFromReport(minimal, 0));
        var usb = new byte[64]; usb[0] = 0x01;
        Assert.Equal(DualSenseBridge.Core.Protocol.Transport.Usb,
            DualSenseBridge.Core.Hid.DualSenseHidDevice.ClassifyFromReport(usb, 0));
    }

    // Подпись виртуального usbip-устройства: отчёт КОРОЧЕ объявленного максимума.
    [Fact]
    public void ReportShorterThanCap_Inconsistent_VirtualSignature()
    {
        var report = new byte[64]; report[0] = 0x01;
        Assert.False(DualSenseBridge.Core.Hid.DualSenseHidDevice.IsReportConsistentWithCaps(report, 78));
    }

    [Fact]
    public void ReportEqualToCap_Consistent()
    {
        var bt = new byte[78]; bt[0] = 0x31;
        Assert.True(DualSenseBridge.Core.Hid.DualSenseHidDevice.IsReportConsistentWithCaps(bt, 78));
        var usb = new byte[64]; usb[0] = 0x01;
        Assert.True(DualSenseBridge.Core.Hid.DualSenseHidDevice.IsReportConsistentWithCaps(usb, 64));
    }

    [Fact]
    public void UnknownCap_AlwaysConsistent()
    {
        var report = new byte[10]; report[0] = 0x01;
        Assert.True(DualSenseBridge.Core.Hid.DualSenseHidDevice.IsReportConsistentWithCaps(report, 0));
    }
}

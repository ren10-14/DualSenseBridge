using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core.Translation;

/// <summary>Режим трансляции обратной связи виртуального контроллера в команды DualSense.</summary>
public enum TranslationMode
{
    /// <summary>
    /// Нативный режим (по умолчанию): общий блок берётся из USB-отчёта 0x02,
    /// который игра/Steam Input отправили виртуальному DualSense. USB и BT
    /// используют один и тот же 47-байтовый общий блок (проверено по
    /// hid-playstation.c: static_assert(sizeof(dualsense_output_report_common) == 47))
    /// — мы лишь добавляем BT-заголовок (seq + tag 0x10) и CRC32.
    /// </summary>
    Native,

    /// <summary>
    /// Структурированный режим: общий блок собирается только из полей
    /// rumble/RGB/playerLeds/триггеры. Используется для upstream-диалекта VIIPER
    /// (где нативного отчёта нет), для тестов и для явного контроля флагов.
    /// </summary>
    Structured,
}

/// <summary>
/// Транслятор: обратная связь виртуального DualSense -> общий блок output-отчёта
/// физического DualSense.
///
/// Патчи безопасности (всегда):
///  - блокируются отладочные режимы триггеров 0xFC/0xFD/0xFE (ломают привод
///    триггера до сброса — Nielk1, «Debug or Calibration functions»);
///
/// Патчи совместимости (починить эффекты «молчаливых» игр — v1.5.0):
/// контроллер применяет только те секции отчёта, у которых выставлен бит
/// валидности (valid_flag0/1/2 — hid-playstation.c). Часть игр/обёрток пишет
/// сами данные, но не выставляет флаги — и прошивка молча игнорирует эффект.
/// Поэтому если данные есть, а флага нет — флаг добавляется (обратное
/// направление не выполняется: «данных нет, а флаг есть» не трогаем):
///  - моторы ≠ 0 → flag0 |= COMPATIBLE_VIBRATION|HAPTICS_SELECT (ядро Linux
///    ставит ОБА бита: «Select classic rumble style haptics and enable it»);
///  - блок триггера с режимом ≠ 0 → flag0 |= R2/L2_TRIGGER_EFFECT;
///  - RGB ≠ 0 → flag1 |= LIGHTBAR_CONTROL;
///  - player_leds ≠ 0 → flag1 |= PLAYER_INDICATOR_CONTROL.
/// </summary>
public sealed class FeedbackTranslator
{
    /// <summary>Событие: применён патч (для журнала).</summary>
    public event Action<string>? SafetyPatchApplied;

    public TranslationMode Mode { get; set; } = TranslationMode.Native;

    /// <summary>Масштаб интенсивности вибрации, 0..1 (по умолчанию 1.0).</summary>
    public double RumbleScale { get; set; } = 1.0;

    /// <summary>
    /// Усиление хаптик PCM (аудиоканал 0x36): множитель амплитуды сэмплов
    /// S8 дорожки [78..141], 0.25..4.0 (1.0 — как есть). При изменении
    /// пересчитывается CRC носителя.
    /// </summary>
    public double HapticsGain { get; set; } = 1.0;

    /// <summary>
    /// Принудительно показывать LED «игрок 1» (маска 0x04 — одиночный
    /// центральный LED, канон PS5/hid-playstation.c), независимо от того,
    /// какой номер игрока прислала игра/Steam (слот виртуального контроллера
    /// может быть вторым — тогда игра зажигает рисунок игрока 2, 0x0A).
    /// v1.9.9: если игра не управляет player-LED ВООБЩЕ (флаг не выставлен,
    /// данных нет) — зажигаем «игрок 1» сами, с максимальной яркостью
    /// (флаг2 бит 0 + led_brightness = 0, по патчу ядра Linux о яркости).
    /// </summary>
    public bool ForcePlayerOneLed { get; set; } = true;

    /// <summary>
    /// Подсветка по умолчанию (v1.9.9): если игра НЕ управляет подсветкой
    /// (флаг LIGHTBAR_CONTROL не выставлен и RGB нулевой) — зажигаем свой цвет.
    /// Как только игра берёт управление (флаг или ненулевой цвет) — не мешаем.
    /// </summary>
    public bool DefaultLightbar { get; set; } = true;

    /// <summary>Цвет подсветки по умолчанию (мягкий синий, как у PS5).</summary>
    public byte DefaultLightbarRed { get; set; } = 0x00;
    public byte DefaultLightbarGreen { get; set; } = 0x40;
    public byte DefaultLightbarBlue { get; set; } = 0xFF;

    /// <summary>Когда игра последний раз задавала РЕАЛЬНЫЙ цвет подсветки (RGB != 0).</summary>
    private DateTime _lastGameColorUtc = DateTime.MinValue;

    /// <summary>
    /// Окно «игра владеет подсветкой» после задания ненулевого цвета (v1.10.1).
    /// Чёрный цвет (RGB=0), пусть и с флагом управления, владельцем НЕ считается:
    /// смеси кадров «флаг есть/флага нет» при RGB #000000 (видно в журнале
    /// эксплуатации) мерцали подсветкой «дефолт ↔ чёрный» на каждом кадре.
    /// </summary>
    public TimeSpan GameColorLatchWindow { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Транслирует обратную связь в общий блок output-отчёта.
    /// Возвращает null, если отчёт пуст (нечего отправлять).
    /// </summary>
    public OutputCommon? Translate(DualSenseFeedback fb)
    {
        if (Mode == TranslationMode.Native && fb.UsbOutputReport.Length == DualSenseProtocol.OutputReportUsbSize
            && fb.UsbOutputReport[0] == DualSenseProtocol.OutputReportUsb)
        {
            return TranslateNative(fb);
        }
        return TranslateStructured(fb);
    }

    /// <summary>
    /// Обрабатывает УЖЕ СЛИТОЕ состояние (OutputMerger, v1.6.0): применяет те же
    /// патчи, что и нативный путь (санитарная обработка триггеров, флаги
    /// совместимости, «игрок 1», масштаб вибрации). Структурированные поля viiper
    /// не дублируются — слияние уже выполнил вызывающий.
    /// </summary>
    public OutputCommon? TranslateMerged(byte[] merged47)
    {
        if (merged47.Length != DualSenseProtocol.OutputCommonSize) return null;
        var common = OutputCommon.FromRaw(merged47);
        PatchStateBlock(common.Data);
        return common.IsEmpty ? null : common;
    }

private OutputCommon? TranslateNative(DualSenseFeedback fb)
    {
        // [28..75] объекта обратной связи — USB-отчёт 0x02: [0]=ID + 47 байт общего блока.
        var common = OutputCommon.FromRaw(fb.UsbOutputReport.AsSpan(1, DualSenseProtocol.OutputCommonSize));

        // Дополнение из структурированных полей (если нативный отчёт
        // игру не прислала вовсе, а viiper заполнил [0..27] сам).
        MergeStructured(common, fb);

        // Все патчи — РОВНО ОДИН раз (масштаб вибрации не должен применяться дважды).
        PatchStateBlock(common.Data);

        return common.IsEmpty ? null : common;
    }

    /// <summary>
    /// Применяет к общему блоку (47 Б) все патчи: санитарная обработка триггеров,
    /// флаги совместимости, «игрок 1», масштаб вибрации. Используется и для
    /// обычного пути, и для слитого состояния, и для состояния внутри 0x36.
    /// </summary>
    public void PatchStateBlock(byte[] d)
    {
        if (PatchTriggerBlock(d, DualSenseProtocol.OffTriggerRight))
            SafetyPatchApplied?.Invoke("R2: отладочный режим триггера заменён на Off");
        if (PatchTriggerBlock(d, DualSenseProtocol.OffTriggerLeft))
            SafetyPatchApplied?.Invoke("L2: отладочный режим триггера заменён на Off");
        PatchCompatibilityFlags(d);
        PatchPlayerOne(d);
        PatchDefaultLightbar(d);
        ApplyRumbleScale(d);
    }

    /// <summary>
    /// Патчит состояние ВНУТРИ комбинированного BT-отчёта 0x36 (398 Б) и
    /// пересчитывает его CRC32 (seed 0xEADA2D49 по [0..393], LE в [394..397]).
    /// Возвращает тот же массив. Хаптики PCM [78..141] и Opus-динамик не
    /// трогаем — только блок состояния [13..59].
    /// </summary>
    public byte[] PatchCarrier(byte[] carrier)
    {
        if (carrier is null || carrier.Length != 398 || carrier[0] != 0x36) return carrier;

        var state = new byte[DualSenseProtocol.OutputCommonSize];
        Array.Copy(carrier, DualSenseFeedback.CarrierStateOffset, state, 0, DualSenseProtocol.OutputCommonSize);
        PatchStateBlock(state);
        Array.Copy(state, 0, carrier, DualSenseFeedback.CarrierStateOffset, DualSenseProtocol.OutputCommonSize);

        // Усиление хаптик PCM (S8, с клампом): применяется ДО пересчёта CRC.
        if (HapticsGain != 1.0)
        {
            for (int i = DualSenseFeedback.CarrierHapticsOffset;
                 i < DualSenseFeedback.CarrierHapticsOffset + DualSenseFeedback.CarrierHapticsSize; i++)
            {
                int v = (sbyte)carrier[i];
                carrier[i] = (byte)Math.Clamp((int)Math.Round(v * HapticsGain), -128, 127);
            }
        }

        uint crc = Crc32.Update(DualSenseFeedback.CarrierCrcSeed, carrier.AsSpan(0, DualSenseFeedback.CarrierCrcOffset));
        carrier[DualSenseFeedback.CarrierCrcOffset + 0] = (byte)(crc & 0xFF);
        carrier[DualSenseFeedback.CarrierCrcOffset + 1] = (byte)((crc >> 8) & 0xFF);
        carrier[DualSenseFeedback.CarrierCrcOffset + 2] = (byte)((crc >> 16) & 0xFF);
        carrier[DualSenseFeedback.CarrierCrcOffset + 3] = (byte)((crc >> 24) & 0xFF);
        return carrier;
    }

    private OutputCommon? TranslateStructured(DualSenseFeedback fb)
    {
        var common = new OutputCommon();

        if ((fb.RumbleSmall | fb.RumbleLarge) != 0)
        {
            byte right = ScaleByte(fb.RumbleSmall);
            byte left = ScaleByte(fb.RumbleLarge);
            if ((right | left) != 0) common.SetRumble(right, left);
        }

        if ((fb.LedRed | fb.LedGreen | fb.LedBlue) != 0 || fb.PlayerLeds != 0)
        {
            common.SetLightbar(fb.LedRed, fb.LedGreen, fb.LedBlue);
            if (fb.PlayerLeds != 0) common.SetPlayerLeds(fb.PlayerLeds);
        }

        // Нативные блоки триггеров доступны только в диалекте hbashton (474 байта).
        var rightBlock = (byte[])fb.TriggerRight.Clone();
        var leftBlock = (byte[])fb.TriggerLeft.Clone();
        if (TriggerEffect.Sanitize(rightBlock))
            SafetyPatchApplied?.Invoke("R2: отладочный режим триггера заменён на Off");
        if (TriggerEffect.Sanitize(leftBlock))
            SafetyPatchApplied?.Invoke("L2: отладочный режим триггера заменён на Off");
        if (rightBlock[0] != 0) common.SetTriggerRight(rightBlock);
        if (leftBlock[0] != 0) common.SetTriggerLeft(leftBlock);

        PatchPlayerOne(common.Data);
        ApplyRumbleScale(common.Data);
        return common.IsEmpty ? null : common;
    }

    /// <summary>
    /// Добавляет отсутствующие флаги валидности для секций, в которых игра
    /// прислала ненулевые данные (см. комментарий класса).
    /// </summary>
    private void PatchCompatibilityFlags(byte[] d)
    {
        byte motorR = d[DualSenseProtocol.OffMotorRight];
        byte motorL = d[DualSenseProtocol.OffMotorLeft];
        if ((motorR | motorL) != 0)
        {
            byte need = (byte)(DualSenseProtocol.Flag0CompatibleVibration | DualSenseProtocol.Flag0HapticsSelect);
            if ((d[DualSenseProtocol.OffValidFlag0] & need) != need)
            {
                d[DualSenseProtocol.OffValidFlag0] |= need;
                SafetyPatchApplied?.Invoke("вибрация: добавлены флаги COMPATIBLE_VIBRATION|HAPTICS_SELECT (без них прошивка игнорирует моторы)");
            }
        }

        if (d[DualSenseProtocol.OffTriggerRight] != 0 &&
            (d[DualSenseProtocol.OffValidFlag0] & DualSenseProtocol.Flag0RightTriggerEffect) == 0)
        {
            d[DualSenseProtocol.OffValidFlag0] |= DualSenseProtocol.Flag0RightTriggerEffect;
            SafetyPatchApplied?.Invoke("R2: добавлен флаг валидности эффекта триггера");
        }

        if (d[DualSenseProtocol.OffTriggerLeft] != 0 &&
            (d[DualSenseProtocol.OffValidFlag0] & DualSenseProtocol.Flag0LeftTriggerEffect) == 0)
        {
            d[DualSenseProtocol.OffValidFlag0] |= DualSenseProtocol.Flag0LeftTriggerEffect;
            SafetyPatchApplied?.Invoke("L2: добавлен флаг валидности эффекта триггера");
        }

        byte r = d[DualSenseProtocol.OffLightbarRed];
        byte g = d[DualSenseProtocol.OffLightbarGreen];
        byte b = d[DualSenseProtocol.OffLightbarBlue];
        if ((r | g | b) != 0 &&
            (d[DualSenseProtocol.OffValidFlag1] & DualSenseProtocol.Flag1LightbarControl) == 0)
        {
            d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1LightbarControl;
            SafetyPatchApplied?.Invoke("подсветка: добавлен флаг LIGHTBAR_CONTROL (без него RGB не применяется)");
        }

        if (d[DualSenseProtocol.OffPlayerLeds] != 0 &&
            (d[DualSenseProtocol.OffValidFlag1] & DualSenseProtocol.Flag1PlayerIndicatorControl) == 0)
        {
            d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1PlayerIndicatorControl;
            SafetyPatchApplied?.Invoke("player LED: добавлен флаг PLAYER_INDICATOR_CONTROL");
        }
    }

    /// <summary>
    /// Дополняет общий блок структурированными полями viiper [0..27] для секций,
    /// которых в нативном отчёте игры нет (флаг не выставлен И данные нулевые).
    /// viiper заполняет эти поля только из валидных секций отчёта игры.
    /// </summary>
    private void MergeStructured(OutputCommon common, DualSenseFeedback fb)
    {
        var d = common.Data;

        if ((d[DualSenseProtocol.OffMotorRight] | d[DualSenseProtocol.OffMotorLeft]) == 0 &&
            (fb.RumbleSmall | fb.RumbleLarge) != 0)
        {
            d[DualSenseProtocol.OffMotorRight] = fb.RumbleSmall;
            d[DualSenseProtocol.OffMotorLeft] = fb.RumbleLarge;
            d[DualSenseProtocol.OffValidFlag0] |=
                (byte)(DualSenseProtocol.Flag0CompatibleVibration | DualSenseProtocol.Flag0HapticsSelect);
            SafetyPatchApplied?.Invoke("вибрация взята из структурированных полей viiper (в отчёте игры моторы пусты)");
        }

        if (d[DualSenseProtocol.OffTriggerRight] == 0 && fb.TriggerRight[0] != 0)
        {
            fb.TriggerRight.CopyTo(d.AsSpan(DualSenseProtocol.OffTriggerRight, TriggerEffect.BlockSize));
            d[DualSenseProtocol.OffValidFlag0] |= DualSenseProtocol.Flag0RightTriggerEffect;
            SafetyPatchApplied?.Invoke("R2: эффект взят из структурированного блока viiper");
        }

        if (d[DualSenseProtocol.OffTriggerLeft] == 0 && fb.TriggerLeft[0] != 0)
        {
            fb.TriggerLeft.CopyTo(d.AsSpan(DualSenseProtocol.OffTriggerLeft, TriggerEffect.BlockSize));
            d[DualSenseProtocol.OffValidFlag0] |= DualSenseProtocol.Flag0LeftTriggerEffect;
            SafetyPatchApplied?.Invoke("L2: эффект взят из структурированного блока viiper");
        }

        if ((d[DualSenseProtocol.OffLightbarRed] | d[DualSenseProtocol.OffLightbarGreen] | d[DualSenseProtocol.OffLightbarBlue]) == 0 &&
            (fb.LedRed | fb.LedGreen | fb.LedBlue) != 0)
        {
            d[DualSenseProtocol.OffLightbarRed] = fb.LedRed;
            d[DualSenseProtocol.OffLightbarGreen] = fb.LedGreen;
            d[DualSenseProtocol.OffLightbarBlue] = fb.LedBlue;
            d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1LightbarControl;
            SafetyPatchApplied?.Invoke("подсветка взята из структурированных полей viiper");
        }

        if (d[DualSenseProtocol.OffPlayerLeds] == 0 && fb.PlayerLeds != 0)
        {
            d[DualSenseProtocol.OffPlayerLeds] = fb.PlayerLeds;
            d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1PlayerIndicatorControl;
            SafetyPatchApplied?.Invoke("player LED взят из структурированных полей viiper");
        }
    }

    /// <summary>
    /// Принудительный рисунок «игрок 1» (0x04) с максимальной яркостью.
    /// v1.9.9: зажигается ДАЖЕ если игра не управляла player-LED вовсе (флаг не
    /// выставлен, маска пустая) — раньше LED просто оставался тёмным.
    /// Яркость: флаг2 бит 0 (LED_BRIGHTNESS_CONTROL) + led_brightness = 0
    /// (0 = самая яркая) — как в патче ядра Linux о яркости player-LED.
    /// </summary>
    private void PatchPlayerOne(byte[] d)
    {
        if (!ForcePlayerOneLed) return;
        bool gameControls = (d[DualSenseProtocol.OffValidFlag1] & DualSenseProtocol.Flag1PlayerIndicatorControl) != 0;
        if (gameControls && d[DualSenseProtocol.OffPlayerLeds] == DualSenseProtocol.PlayerLedPlayer1 &&
            (d[DualSenseProtocol.OffValidFlag2] & DualSenseProtocol.Flag2LedBrightnessControl) != 0)
            return; // уже «игрок 1» с управлением яркостью

        if (!gameControls && d[DualSenseProtocol.OffPlayerLeds] == 0)
            SafetyPatchApplied?.Invoke("player LED: зажжён «игрок 1» (игра не управляла LED)");
        else if (gameControls && d[DualSenseProtocol.OffPlayerLeds] != DualSenseProtocol.PlayerLedPlayer1)
            SafetyPatchApplied?.Invoke("player LED: принудительно «игрок 1»");

        d[DualSenseProtocol.OffPlayerLeds] = DualSenseProtocol.PlayerLedPlayer1;
        d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1PlayerIndicatorControl;
        d[DualSenseProtocol.OffLedBrightness] = 0; // 0 = максимальная яркость
        d[DualSenseProtocol.OffValidFlag2] |= DualSenseProtocol.Flag2LedBrightnessControl;
    }

    /// <summary>
    /// Подсветка по умолчанию (v1.9.9): после подключения (LIGHT_OUT) полоса
    /// вокруг тачпада гаснет, и если игра подсветкой не управляет — контроллер
    /// остаётся тёмным. Зажигаем мягкий синий, пока игра молчит; как только
    /// игра выставляет флаг LIGHTBAR_CONTROL или шлёт ненулевой цвет — не мешаем.
    /// </summary>
    private void PatchDefaultLightbar(byte[] d)
    {
        if (!DefaultLightbar) return;
        bool gameColor = (d[DualSenseProtocol.OffLightbarRed] | d[DualSenseProtocol.OffLightbarGreen] | d[DualSenseProtocol.OffLightbarBlue]) != 0;
        if (gameColor)
        {
            // Игра задала РЕАЛЬНЫЙ цвет — она владелец подсветки (в пределах окна).
            _lastGameColorUtc = DateTime.UtcNow;
            return;
        }
        // Чёрный (RGB=0), даже с флагом управления, владельцем не считается:
        // иначе смеси кадров «флаг/без флага» при #000000 мерцают (v1.10.1).
        if (_lastGameColorUtc != DateTime.MinValue &&
            DateTime.UtcNow - _lastGameColorUtc < GameColorLatchWindow)
            return; // недавно был настоящий цвет игры — не вмешиваемся
        d[DualSenseProtocol.OffLightbarRed] = DefaultLightbarRed;
        d[DualSenseProtocol.OffLightbarGreen] = DefaultLightbarGreen;
        d[DualSenseProtocol.OffLightbarBlue] = DefaultLightbarBlue;
        d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1LightbarControl;
        SafetyPatchApplied?.Invoke("подсветка: цвет по умолчанию (игра не управляла)");
    }

    private void ApplyRumbleScale(byte[] d)
    {
        if (RumbleScale >= 0.999) return;
        d[DualSenseProtocol.OffMotorRight] = ScaleByte(d[DualSenseProtocol.OffMotorRight]);
        d[DualSenseProtocol.OffMotorLeft] = ScaleByte(d[DualSenseProtocol.OffMotorLeft]);
    }

    private byte ScaleByte(byte v) => (byte)Math.Clamp((int)Math.Round(v * RumbleScale), 0, 255);

    /// <summary>Заменяет опасные режимы триггера на Off. true — если блок изменён.</summary>
    private static bool PatchTriggerBlock(byte[] commonData, int offset)
    {
        var block = commonData.AsSpan(offset, TriggerEffect.BlockSize);
        return TriggerEffect.Sanitize(block);
    }
}

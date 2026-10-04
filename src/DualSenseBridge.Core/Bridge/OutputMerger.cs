using DualSenseBridge.Core.Protocol;

namespace DualSenseBridge.Core.Bridge;

/// <summary>
/// Кумулятивное слияние команд игры в ОДНО согласованное состояние (v1.6.0).
///
/// Зачем: игры часто шлют РАЗДЕЛЬНЫЕ output-отчёты (только триггеры, только
/// вибрация, только подсветка) по несколько за кадр. Писать каждый в Bluetooth
/// по отдельности — переполнить канал (BT ~125 Гц) и потерять часть (эффект
/// «проваливается»). Решение — как у viiper (mergeRawOutputReport) и ядра Linux:
/// накапливать состояние ПО ГРУППАМ ПОЛЕЙ и отправлять один плотный отчёт.
///
/// Семантика (ровно как mergeRawOutputReport в hbashton/VIIPER):
///  - секция обновляется, только если во входящем отчёте выставлен её флаг
///    валидности (вибрация: flag0.0x01 ИЛИ flag2.0x04 «vibration v2»);
///  - секция с флагом и нулевыми данными — ЯВНОЕ выключение (триггер Off);
///  - секция без флага не трогается (отчёт «только вибрация» не сбрасывает триггер).
/// </summary>
public sealed class OutputMerger
{
    private readonly byte[] _state = new byte[DualSenseProtocol.OutputCommonSize];

    /// <summary>Текущее слитое состояние (копия; не изменяйте).</summary>
    public byte[] Current
    {
        get { var copy = new byte[DualSenseProtocol.OutputCommonSize]; lock (_lock) _state.CopyTo(copy, 0); return copy; }
    }

    private readonly object _lock = new();

    /// <summary>Сливает общий блок 47 Б (команда игры) в накопленное состояние.</summary>
    public void Apply(ReadOnlySpan<byte> common47)
    {
        if (common47.Length != DualSenseProtocol.OutputCommonSize) return;
        lock (_lock)
        {
            var d = _state.AsSpan();
            byte f0 = common47[DualSenseProtocol.OffValidFlag0];
            byte f1 = common47[DualSenseProtocol.OffValidFlag1];
            byte f2 = common47[DualSenseProtocol.OffValidFlag2];

            // --- Вибрация: селектор = flag0.0x01|0x02 ИЛИ flag2.0x04 (vibration v2).
            if ((f0 & (DualSenseProtocol.Flag0CompatibleVibration | DualSenseProtocol.Flag0HapticsSelect)) != 0 ||
                (f2 & 0x04) != 0)
            {
                d[DualSenseProtocol.OffValidFlag0] |= (byte)(DualSenseProtocol.Flag0CompatibleVibration | DualSenseProtocol.Flag0HapticsSelect);
                d[DualSenseProtocol.OffMotorRight] = common47[DualSenseProtocol.OffMotorRight];
                d[DualSenseProtocol.OffMotorLeft] = common47[DualSenseProtocol.OffMotorLeft];
            }

            // --- Аудио-регуляторы (по одному байту, каждый под своим флагом;
            //     kernel: SPEAKER_VOLUME_ENABLE=0x20, MIC=0x40, AUDIO_CONTROL=0x80;
            //     viiper: HEADPHONE_VOLUME=0x10; flag1: mute LED=0x01, power_save=0x02,
            //     audio_control2=0x80).
            MergeIf(d, common47, f0, 0x10, DualSenseProtocol.OffHeadphoneVolume);
            MergeIf(d, common47, f0, 0x20, DualSenseProtocol.OffSpeakerVolume);
            MergeIf(d, common47, f0, DualSenseProtocol.Flag0MicVolumeEnable, DualSenseProtocol.OffMicVolume);
            MergeIf(d, common47, f0, DualSenseProtocol.Flag0AudioControlEnable, DualSenseProtocol.OffAudioControl);
            MergeIf(d, common47, f1, DualSenseProtocol.Flag1MicMuteLedControl, DualSenseProtocol.OffMuteButtonLed);
            MergeIf(d, common47, f1, DualSenseProtocol.Flag1PowerSaveControl, DualSenseProtocol.OffPowerSaveControl);
            MergeIf(d, common47, f1, DualSenseProtocol.Flag1AudioControl2, DualSenseProtocol.OffAudioControl2);

            // --- Триггеры: флаг + 11-байтовый блок (включая явный Off — режим 0x00).
            if ((f0 & DualSenseProtocol.Flag0RightTriggerEffect) != 0)
            {
                common47.Slice(DualSenseProtocol.OffTriggerRight, TriggerEffect.BlockSize)
                    .CopyTo(d.Slice(DualSenseProtocol.OffTriggerRight, TriggerEffect.BlockSize));
                d[DualSenseProtocol.OffValidFlag0] |= DualSenseProtocol.Flag0RightTriggerEffect;
            }
            if ((f0 & DualSenseProtocol.Flag0LeftTriggerEffect) != 0)
            {
                common47.Slice(DualSenseProtocol.OffTriggerLeft, TriggerEffect.BlockSize)
                    .CopyTo(d.Slice(DualSenseProtocol.OffTriggerLeft, TriggerEffect.BlockSize));
                d[DualSenseProtocol.OffValidFlag0] |= DualSenseProtocol.Flag0LeftTriggerEffect;
            }

            // --- Player LED (+ яркость) и подсветка RGB.
            if ((f1 & DualSenseProtocol.Flag1PlayerIndicatorControl) != 0)
            {
                d[DualSenseProtocol.OffPlayerLeds] = common47[DualSenseProtocol.OffPlayerLeds];
                d[DualSenseProtocol.OffLedBrightness] = common47[DualSenseProtocol.OffLedBrightness];
                d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1PlayerIndicatorControl;
            }
            if ((f1 & DualSenseProtocol.Flag1LightbarControl) != 0)
            {
                d[DualSenseProtocol.OffLightbarRed] = common47[DualSenseProtocol.OffLightbarRed];
                d[DualSenseProtocol.OffLightbarGreen] = common47[DualSenseProtocol.OffLightbarGreen];
                d[DualSenseProtocol.OffLightbarBlue] = common47[DualSenseProtocol.OffLightbarBlue];
                d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1LightbarControl;
            }

            // --- lightbar_setup (сброс анимации) и «vibration v2».
            if ((f2 & DualSenseProtocol.Flag2LightbarSetupControl) != 0)
            {
                d[DualSenseProtocol.OffLightbarSetup] = common47[DualSenseProtocol.OffLightbarSetup];
                d[DualSenseProtocol.OffValidFlag2] |= DualSenseProtocol.Flag2LightbarSetupControl;
            }
            if ((f2 & 0x04) != 0)
                d[DualSenseProtocol.OffValidFlag2] |= 0x04;

            // RELEASE_LEDS (отдать управление LED) — проходной бит.
            if ((f1 & DualSenseProtocol.Flag1ReleaseLeds) != 0)
                d[DualSenseProtocol.OffValidFlag1] |= DualSenseProtocol.Flag1ReleaseLeds;
        }
    }

    private static void MergeIf(Span<byte> state, ReadOnlySpan<byte> src, byte flags, byte bit, int offset)
    {
        if ((flags & bit) != 0) state[offset] = src[offset];
    }

    /// <summary>Сброс накопленного состояния (при остановке моста/смене игры).</summary>
    public void Reset()
    {
        lock (_lock) Array.Clear(_state, 0, _state.Length);
    }
}

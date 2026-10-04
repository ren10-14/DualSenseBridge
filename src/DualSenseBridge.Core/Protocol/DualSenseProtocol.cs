namespace DualSenseBridge.Core.Protocol;

/// <summary>
/// Подтверждённые константы проводного протокола Sony DualSense (PS5).
/// Все значения выверены по исходникам драйвера Linux hid-playstation.c
/// (автор — Roderick Colenbrander, Sony Interactive Entertainment, GPL-2.0)
/// и перекрёстно по сообществу реверс-инжиниринга (см. docs/RESEARCH.md).
/// </summary>
public static class DualSenseProtocol
{
    // ------------------------------------------------------------------ Идентификация

    public const ushort VendorIdSony = 0x054C;
    public const ushort ProductIdDualSense = 0x0CE6;
    public const ushort ProductIdDualSenseEdge = 0x0DF2;

    public const string ProductName = "DualSense Wireless Controller";

    // ------------------------------------------------------------------ Идентификаторы отчётов

    /// <summary>Input-отчёт USB: 64 байта целиком, включая Report ID.</summary>
    public const byte InputReportUsb = 0x01;
    public const int InputReportUsbSize = 64;

    /// <summary>Input-отчёт Bluetooth: 78 байт, последние 4 — CRC32 (seed 0xA1).</summary>
    public const byte InputReportBt = 0x31;
    public const int InputReportBtSize = 78;

    /// <summary>
    /// Минимальный input-отчёт Bluetooth (0x01, ~10 байт). Контроллер шлёт его,
    /// пока хост не прочитает feature-отчёт калибровки 0x05 — после этого
    /// переключается на полный 0x31. (hid-playstation.c; патч v3 Sony.)
    /// </summary>
    public const byte InputReportBtMinimal = 0x01;

    /// <summary>Output-отчёт USB: 0x02, 48 полезных байт (Report ID + общий блок 47).</summary>
    public const byte OutputReportUsb = 0x02;
    public const int OutputReportUsbSize = 48;

    /// <summary>Output-отчёт Bluetooth: 0x31, 78 байт с CRC32 (seed 0xA2).</summary>
    public const byte OutputReportBt = 0x31;
    public const int OutputReportBtSize = 78;

    /// <summary>Размер общего блока output-отчёта, общего для USB и BT.</summary>
    public const int OutputCommonSize = 47;

    /// <summary>
    /// Магическое значение тега BT output-отчёта (байт 2).
    /// «Tag must be set. Exact meaning is unclear.» — hid-playstation.c
    /// </summary>
    public const byte OutputBtTag = 0x10;

    // ------------------------------------------------------------------ Feature-отчёты

    /// <summary>Калибровка гироскопа/акселерометра; чтение переключает BT в режим 0x31.</summary>
    public const byte FeatureReportCalibration = 0x05;
    public const int FeatureReportCalibrationSize = 41;

    /// <summary>MAC-адрес контроллера (bytes 1..6).</summary>
    public const byte FeatureReportPairingInfo = 0x09;
    public const int FeatureReportPairingInfoSize = 20;

    /// <summary>Версии прошивки: hw LE32 @24, fw LE32 @28, update LE16 @44.</summary>
    public const byte FeatureReportFirmwareInfo = 0x20;
    public const int FeatureReportFirmwareInfoSize = 64;

    // ------------------------------------------------------------------ Флаги output-отчёта (общий блок, смещение +0)

    public const byte Flag0CompatibleVibration = 0x01;  // «классическая» вибрация (DS4-совместимая)
    public const byte Flag0HapticsSelect = 0x02;         // выбор haptic-движков для эмуляции вибрации
    public const byte Flag0RightTriggerEffect = 0x04;    // блок эффекта R2 валиден
    public const byte Flag0LeftTriggerEffect = 0x08;     // блок эффекта L2 валиден
    public const byte Flag0SpeakerVolumeEnable = 0x20;
    public const byte Flag0MicVolumeEnable = 0x40;
    public const byte Flag0AudioControlEnable = 0x80;

    public const byte Flag1MicMuteLedControl = 0x01;     // LED кнопки mute (байт +8)
    public const byte Flag1PowerSaveControl = 0x02;      // power_save_control (байт +9)
    public const byte Flag1LightbarControl = 0x04;       // RGB-подсветка (байты +44..+46)
    public const byte Flag1ReleaseLeds = 0x08;           // освободить управление LED
    public const byte Flag1PlayerIndicatorControl = 0x10;// 5 LED игрока (байт +43)
    public const byte Flag1AudioControl2 = 0x80;         // audio_control2 (байт +37)

    public const byte Flag2LightbarSetupControl = 0x02;  // lightbar_setup (байт +41)
    public const byte Flag2CompatibleVibration2 = 0x04;  // «vibration v2» (новые прошивки)

    /// <summary>
    /// Контроль яркости player-LED (байт +42). Подтверждено патчем ядра Linux
    /// «HID: playstation: Support DualSense player LED brightness control»:
    /// DS_OUTPUT_VALID_FLAG2_LED_BRIGHTNESS_CONTROL_ENABLE = BIT(0);
    /// яркость: 0 = ЯРЧЕ ВСЕГО, 1 = средне, 2 = тускло (инвертировано).
    /// </summary>
    public const byte Flag2LedBrightnessControl = 0x01;

    // lightbar_setup: бит 1 = LIGHT_OUT (плавно погасить; нужен для сброса
    // стартовой анимации подсветки после BT-подключения — hid-playstation.c)
    public const byte LightbarSetupLightOut = 0x02;

    // ------------------------------------------------------------------ Смещения внутри общего блока output (47 байт)

    public const int OffValidFlag0 = 0;
    public const int OffValidFlag1 = 1;
    public const int OffMotorRight = 2;      // высокочастотный/слабый мотор
    public const int OffMotorLeft = 3;       // низкочастотный/сильный мотор
    public const int OffHeadphoneVolume = 4; // 0x00..0x7F
    public const int OffSpeakerVolume = 5;   // 0x00..0xFF (принимается ~0x3D..0x64)
    public const int OffMicVolume = 6;       // 0x00..0x40
    public const int OffAudioControl = 7;
    public const int OffMuteButtonLed = 8;   // 0=выкл, 1=вкл, 2=пульс
    public const int OffPowerSaveControl = 9;// бит 0x10 = отключение микрофона
    public const int OffTriggerRight = 10;   // 11 байт: режим + 10 параметров (R2)
    public const int OffTriggerLeft = 21;    // 11 байт: режим + 10 параметров (L2)
    public const int OffAudioControl2 = 37;
    public const int OffValidFlag2 = 38;
    public const int OffLightbarSetup = 41;
    public const int OffLedBrightness = 42;  // яркость player LED: 0=ярко, 1=средне, 2=тускло (инверсно)
    public const int OffPlayerLeds = 43;     // битовая маска 5 LED (бит 0 — левый)
    public const int OffLightbarRed = 44;
    public const int OffLightbarGreen = 45;
    public const int OffLightbarBlue = 46;

    // ------------------------------------------------------------------ Player LED (маска 5 LED, hid-playstation.c player_ids)

    /// <summary>Канонические рисунки PS5: игрок 1 — одиночный центральный LED (BIT(2)).</summary>
    public const byte PlayerLedPlayer1 = 0x04; // BIT(2)
    public const byte PlayerLedPlayer2 = 0x0A; // BIT(3)|BIT(1)
    public const byte PlayerLedPlayer3 = 0x15; // BIT(4)|BIT(2)|BIT(0)
    public const byte PlayerLedPlayer4 = 0x1B; // BIT(4)|BIT(3)|BIT(1)|BIT(0)
    public const byte PlayerLedPlayer5 = 0x1F; // все 5

    // ------------------------------------------------------------------ Комбинированный BT-отчёт 0x36 (аудиоканал)

    /// <summary>
    /// Комбинированный Bluetooth-отчёт 0x36: состояние + хаптики PCM (64 Б, стерео
    /// S8, 3 кГц) + Opus-динамик. Именно им PS5 передаёт хаптики/звук — «аудиоканал».
    /// Объявлен в BT-дескрипторе DualSense: 398 байт (report-descriptor-bluetooth.txt:
    /// 85 36 09 36 96 8d 01 91 02). viiper доставляет его готовым к записи в поле
    /// [76..473] отдачи V5 (hbashton/VIIPER device/dualsense/bthaptics.go).
    /// </summary>
    public const byte CombinedBtReportId = 0x36;
    public const int CombinedBtReportSize = 398;

    // ------------------------------------------------------------------ Смещения общего блока input (63 байта)

    public const int InOffLx = 0;
    public const int InOffLy = 1;
    public const int InOffRx = 2;
    public const int InOffRy = 3;
    public const int InOffL2 = 4;            // z
    public const int InOffR2 = 5;            // rz
    public const int InOffSeq = 6;
    public const int InOffButtons0 = 7;      // младший ниббл = hat; бит4 Square, бит5 Cross, бит6 Circle, бит7 Triangle
    public const int InOffButtons1 = 8;      // бит0 L1, бит1 R1, бит2 L2, бит3 R2, бит4 Create, бит5 Options, бит6 L3, бит7 R3
    public const int InOffButtons2 = 9;      // бит0 PS, бит1 TouchpadClick, бит2 MicMute; (Edge: биты 4..7)
    public const int InOffButtons3 = 10;
    public const int InOffGyro = 15;         // 3 × int16 LE (после reserved[4] 11..14)
    public const int InOffAccel = 21;        // 3 × int16 LE
    public const int InOffSensorTimestamp = 27; // uint32 LE, тик 0.33 мкс
    public const int InOffTouchPoints = 32;  // 2 × 4 байта (после reserved2 в +31)
    public const int InOffStatus0 = 52;      // батарея: биты 0..3 ёмкость (0..10), биты 4..7 статус заряда (после reserved3[12] 40..51)
    public const int InOffStatus1 = 53;      // бит0 наушники, бит1 микрофон, бит2 mute

    // ------------------------------------------------------------------ Кнопки (маски)

    public const byte Btn0Square = 0x10;
    public const byte Btn0Cross = 0x20;
    public const byte Btn0Circle = 0x40;
    public const byte Btn0Triangle = 0x80;

    public const byte Btn1L1 = 0x01;
    public const byte Btn1R1 = 0x02;
    public const byte Btn1L2 = 0x04;
    public const byte Btn1R2 = 0x08;
    public const byte Btn1Create = 0x10;
    public const byte Btn1Options = 0x20;
    public const byte Btn1L3 = 0x40;
    public const byte Btn1R3 = 0x80;

    public const byte Btn2Ps = 0x01;
    public const byte Btn2Touchpad = 0x02;
    public const byte Btn2MicMute = 0x04;

    // ------------------------------------------------------------------ Сенсоры

    public const double AccelResPerG = 8192.0;      // отсчётов на 1g (hid-playstation.c)
    public const double GyroCountsPerDps = 16.384;  // сырых отсчётов на градус/с (VIIPER const.go)

    public const int TouchpadWidth = 1920;
    public const int TouchpadHeight = 1080;
    public const byte TouchPointInactive = 0x80;    // бит 7 байта contact = точка неактивна
}

/// <summary>Транспорт подключения контроллера.</summary>
public enum Transport
{
    Unknown,
    Bluetooth,
    Usb,
}

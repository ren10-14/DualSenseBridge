# ЭТАП 2 — Архитектура

## Обзор

Решение: .NET 10 (C#), два проекта + тесты:

- **`DualSenseBridge.Core`** (net10.0, кроссплатформенное ядро — тестируется на любой ОС):
  весь протокол, клиенты, трансляция, мост.
- **`DualSenseBridge`** (net10.0-windows, WinForms): CLI, трей/GUI, установка драйверов.
- **`DualSenseBridge.Tests`** (xunit): 36 юнит-тестов с «золотыми» векторами.

Соответствие требуемым модулям:

| Требовалось | Реализовано в |
|---|---|
| DeviceManager | `Core/Hid/HidDeviceMonitor.cs`, `Core/Hid/DualSenseHidDevice.cs` |
| VirtualGamepadManager | `Core/Viiper/ViiperClient.cs` (шайба VIIPER + стрим V5) |
| FeedbackTranslator | `Core/Translation/FeedbackTranslator.cs` (+ `InputMapper.cs`) |
| DualSensePacketBuilder | `Core/Protocol/DualSenseOutputReport.cs` (+ весь `Protocol/`) |
| BluetoothHidWriter | `Core/Output/BluetoothHidWriter.cs` |
| PacketOptimizer | очередь newest-wins + дедупликация + мин. интервал внутри `BluetoothHidWriter` |
| HidHideManager | `Core/HidHide/HidHideManager.cs` |
| DiagnosticsUI | `App/UI/MainForm.cs` (статусы, статистика, журнал, тесты) + `App/Program.cs` (CLI-тесты) |

## Схема данных

```
                    ввод (125 Гц по BT)
DualSense (BT 0x31) ──► DualSenseInputParser ──► DualSenseState ──► InputMapper
                                                                    │ wire 33 Б
                                                                    ▼
                              ViiperDeviceStream.SendInputAsync (кадр 0x01)
                                                                    │ TCP :3242
                                                                    ▼
              игра ◄── USB (usbip-win2) ◄── VIIPER: виртуальный DualSense
                                                                    │
                    отдача (кадр 0x81, 474 Б)                       ▼
              ViiperDeviceStream.FeedbackReceived ──► DualSenseFeedback
                                                                    │
                                  FeedbackTranslator (Native/Structured + патчи)
                                                                    │ OutputCommon (47 Б)
                                                                    ▼
                          BluetoothHidWriter (newest-wins, 4 мс, keep-alive 30 с)
                                                                    │ BT 0x31 (78 Б, seq+tag+CRC)
                                                                    ▼
                                                       DualSense (Bluetooth)
```

## Компоненты ядра

### Protocol/ — истина протокола DualSense
- `DualSenseProtocol.cs` — все константы: размеры отчётов, смещения, флаги, маски кнопок.
- `Crc32.cs` — zlib-совместимый CRC-32 + сидированный вариант Sony (0xA1/0xA2/0xA3),
  каскадируемый `Update` (нужен для кадров V5, где CRC покрывает несмежные участки).
- `TriggerEffect.cs` — фабрики эффектов (Feedback/Weapon/Vibration/MultiZone/Bow),
  проверка диапазонов, `Sanitize` (опасные режимы → Off).
- `DualSenseOutputReport.cs` — `OutputCommon` (47 Б, флаги выставляются автоматически)
  и `DualSenseReportCodec` (обёртка в USB 0x02 / BT 0x31 c seq/tag/CRC).
- `DualSenseState.cs` — разбор input-отчётов (USB 64 Б, BT 78 Б c проверкой CRC,
  минимальный BT 10 Б), батарея, тач, IMU.

### Viiper/ — клиент VIIPER
- `V5Frame.cs` — кодек кадров VPCM (16-байтовый заголовок; CRC покрывает [4..11]+payload).
- `DualSenseWire.cs` — wire-формат ввода (33 Б) и отдачи (474 Б) виртуального DualSense.
- `ViiperClient.cs` — TCP-клиент: ping/шина/add/remove (`dualsensegamepadv5` или `dualsense`),
  `OpenStreamAsync` (handshake в пределах 5 с), `ViiperDeviceStream` с read-loop
  (0x81/0x83/0x85) и отправкой ввода; два диалекта: HbashtonV5 / Upstream.

### Translation/
- `InputMapper.cs` — DualSenseState → wire 33 Б: стики 0..255 → -128..127,
  hat 0..8 → битовая маска D-pad, кнопки → uint32, тач/IMU как есть (единицы совпадают).
- `FeedbackTranslator.cs` — отдача → OutputCommon.
  **Native** (по умолчанию): общий блок берётся из нативного USB-отчёта 0x02 игры
  (байты 28..75 payload 0x81) — сохраняются триггеры/подсветка/LED/аудио.
  **Structured**: сборка из полей rumble/RGB/LED/триггеры (для upstream-диалекта и тестов).
  Патчи безопасности: Sanitize триггеров; HAPTICS_SELECT при моторах; масштаб RumbleScale.

### Hid/
- `DualSenseHidDevice.cs` — перечисление (BT определяется по «bthhid» в пути), открытие,
  инициализация BT (чтение feature 0x05 для полного режима + сброс подсветки),
  чтение (BT 0x31 c CRC / USB 0x01; минимальные 0x01), запись (BT 0x31 / USB 0x02),
  feature-отчёты (MAC 0x09, прошивка 0x20).
- `HidDeviceMonitor.cs` — DeviceList.Local.Changed + антидребезг 500 мс → реконнект моста.

### HidHide/ — `HidHideManager.cs`
Скрытие/раскрытие физического DualSense (instance ID), список исключений приложений
(наш exe добавляется в ApplicationPaths). Написано строго по API 1.12.0, проверенному рефлексией.

### Output/ — `BluetoothHidWriter.cs`
Очередь **newest-wins** (одно значение — проигравшие отбрасываются, потерь нет: физическому
контроллеру важно последнее состояние), дедупликация неизменённых отчётов, минимальный
интервал записи (по умолчанию 4 мс = период опроса BT), keep-alive 30 с (повтор последнего
отчёта), статистика `OutputStats` (байт в/из, подавленные дубликаты, сжатие, задержка).

### Bridge/ — `BridgeService.cs`
Оркестратор: StartAsync (ping → шина → add → стрим → attach устройства), InputLoop
(чтение физического контроллера → парс → мап → отправка в VIIPER), OnFeedback
(трансляция → очередь), реконнекты (монитор устройств, переподключение VIIPER),
тест-команды (вибрация/триггеры/подсветка/LED), `BridgeStatus` для UI.

### App (Windows)
- `Program.cs` — все CLI-флаги, консольный режим, тесты, установка/проверка драйверов.
- `Setup/DriverSetup.cs` — обнаружение usbip-win2 (реестр/сервис usbip2_ude), поиск/запуск
  viiper.exe, установка с повышением прав (/VERYSILENT, /S, интерактивно), HidHide-статус.
- `UI/TrayApplicationContext.cs` + `UI/MainForm.cs` — трей (старт/стоп/тесты/выход),
  окно статуса (VIIPER/виртуальное устройство/физический контроллер/батарея/HidHide),
  кнопки тестов, статистика пакетов, журнал, настройки (режим/диалект/интервал/масштаб).

## Решения и обоснования

1. **Нативный режим трансляции** — USB и BT используют один и тот же 47-байтовый общий блок;
   мы лишь добавляем BT-заголовок и CRC. Ничего из посланного игрой не теряется.
2. **WriteFile (interrupt OUT), не HidD_SetOutputReport** — по BT control-трансфер может зависать.
3. **newest-wins вместо очереди всех пакетов** — обратная связь это «состояние», а не «события»;
   при частоте игр 60–250 Гц и BT-периоде 4 мс отправлять всё нет смысла (и нельзя).
4. **VIIPER отдельным процессом** — GPL-изоляция + стабильность (падение моста не роняет драйвер).
5. **Диалект upstream сохранён** — совместимость с Alia5-версией VIIPER (без триггеров).
6. **Кросс-компиляция**: `EnableWindowsTargeting=true` — сборка win-x64 возможна на любой ОС
   (проверено: EXE собран из Linux).

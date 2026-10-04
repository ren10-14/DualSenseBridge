# ЭТАП 1 — Исследование

Отчёт по исследованию протоколов и инструментов. Все ключевые технические факты проверены
по первоисточникам (ссылки ниже); неподтверждённое помечено `[НЕ ПОДТВЕРЖДЕНО]`.
Дата исследования: октябрь 2026.

## 1. Постановка задачи

Физический DualSense подключён к Windows по Bluetooth. Нужно:

1. скрыть его от игр (чтобы не было двойного ввода) — HidHide;
2. показать игре **виртуальный контроллер типа DualSense** (не Xbox 360!);
3. перехватывать от игры вибрацию/адаптивные триггеры/подсветку и транслировать их
   в корректные HID Output Reports физического DualSense по BT.

Ключевое требование пользователя: **максимально сохранить функционал** — не «всё в базовую вибрацию».

## 2. Выбор виртуального контроллера

### Отвергнуто: ViGEm (Xbox 360 / DS4)

ViGEmBus эмулирует Xbox 360 (XInput) и DualShock 4. Проблемы:

- XInput не передаёт адаптивные триггеры и RGB — обратная отдача DualSense невозможна;
- эмулятор DS4 не даёт игре «увидеть» DualSense: игры будут использовать DS4-профиль
  без триггеров и подсветки нового поколения.

### Принято: VIIPER (форк hbashton) — виртуальный DualSense

- **Alia5/VIIPER** — виртуальные HID-геймпады для Windows (наследник ViGEm, от его автора).
  Стабильный протокол: input 33 байта, обратная связь всего 6 байт (rumble+LED) — **без триггеров**.
- **hbashton/VIIPER** (форк, используется в DS4Windows 5/RC4.6.6, сентябрь 2026) — добавляет
  устройство **`dualsensegamepadv5`** с полным набором: адаптивные триггеры, нативный
  output-отчёт 0x02 от игры, хаптики/аудио. Это единственное найденное решение,
  дающее играм настоящий «DualSense-профиль». Доказательство работоспособности —
  DS4Windows (форвардинг триггеров/хаптики на физический DualSense) и ApexSenseBridge
  (in-process libVIIPER, задержка < 2 мс).

**Лицензионная чистота**: VIIPER — GPL-3.0, но запускается **отдельным процессом**
(viiper.exe) и общается с нашим EXE по TCP — GPL-код не линкуется, наш код может быть
любой лицензии. usbip-win2 — BSD-2, HidHide — MIT, HidSharp — Apache-2.0.

### usbip-win2

VIIPER создаёт виртуальные устройства через usbip (USB/IP): виртуальный USB-геймпад
появляется в Windows как настоящее USB-устройство. usbip-win2 (vadimgrn) — драйвер,
прошедший WHLK/attestation-подпись Microsoft, Windows 10 1903+, установка требует
перезагрузки. VIIPER сам выполняет localhost auto-attach.

## 3. Протокол DualSense (канон: drivers/hid/hid-playstation.c ядра Linux)

### 3.1 Output (хост → контроллер)

Общий блок **47 байт** — одинаков для USB и BT:

| Смещение | Поле | Значение |
|---|---|---|
| +0 | valid_flag0 | 0x01 COMPATIBLE_VIBRATION, 0x02 HAPTICS_SELECT, 0x04 R2-эффект, 0x08 L2-эффект, 0x20/0x40/0x80 аудио |
| +1 | valid_flag1 | 0x01 mute LED, 0x02 power_save, 0x04 RGB-подсветка, 0x10 player LED |
| +2 | motor_right | высокочастотный (слабый) мотор, 0..255 |
| +3 | motor_left | низкочастотный (сильный) мотор, 0..255 |
| +4..+9 | аудио-регуляторы | громкость наушников/динамика/микрофона и т.д. |
| +10..+20 | R2-эффект | 11 Б: режим + 10 параметров |
| +21..+31 | L2-эффект | 11 Б |
| +37 | audio_control2 | |
| +38 | valid_flag2 | 0x02 LIGHT_OUT (сброс стартовой анимации подсветки) |
| +41 | lightbar_setup | 0x02 = fade out |
| +42 | led_brightness | 0 ярко / 1 средне / 2 тускло |
| +43 | player_leds | маска 5 LED (бит 0 — левый) |
| +44..+46 | RGB | красный/зелёный/синий |

Триггерные блоки и флаги — ядро Linux не использует (у него там reserved), смещения
подтверждены независимо: Nielk1 (gist «Factories for all DualSense trigger effects»),
Steamworks (isteamdualsense.h), moonlight-common-c.

**Обёртки:**

- **USB**: report 0x02, всего **48 байт** (ID + 47). По официальному HID-дескриптору
  тело output-отчёта 0x02 = 47 байт; Windows отклоняет запись длиннее, чем объявлено
  в дескрипторе (Linux-драйвер паддит до 63 — это его внутренний буфер, НЕ размер отчёта).
- **Bluetooth**: report 0x31, всего **78 байт**: `[0]=0x31, [1]=seq<<4, [2]=0x10 (магический тег), [3..49]=общий блок, [50..73]=reserved, [74..77]=CRC32 LE`.
  CRC32 — стандартный IEEE (zlib), вычисляется по байтам `[0..73]` с **seed-байтом 0xA2** перед ними
  (`zlib.crc32(b'\xA2' + report[0:74])`). seq — 4-битный счётчик 0..15, увеличивается на каждый отчёт.
  Тег 0x10 обязателен («Tag must be set. Exact meaning is unclear» — hid-playstation.c).

### 3.2 Input (контроллер → хост)

Общий блок **63 байта**:

| Смещение | Поле |
|---|---|
| +0..+3 | LX, LY, RX, RY (0..255, 128 центр) |
| +4..+5 | L2, R2 (0..255) |
| +6 | seq |
| +7..+10 | buttons[4]: [7] hat(4б)+Square/Cross/Circle/Triangle; [8] L1/R1/L2/R2/Create/Options/L3/R3; [9] PS/Touchpad/MicMute |
| +15..+20 | гироскоп 3×int16 LE |
| +21..+26 | акселерометр 3×int16 LE |
| +27..+30 | timestamp сенсоров LE32 (тик 0.33 мкс) |
| +32..+39 | тач-точки 2×4Б: contact (бит7=неактивна), x_lo, xy (мл.ниббл x_hi, ст.ниббл y_lo), y_hi; X 12 бит = x_lo\|(x_hi<<8), Y 12 бит = y_lo\|(y_hi<<4) |
| +52..+54 | status[3]: [52] батарея (мл.ниббл ёмкость 0..10 → ×10+5 %, ст.ниббл 0/1/2=режим, 0xa/0xb/0xf=ошибка), [53] наушники/микрофон/mute |

- **USB**: report 0x01, 64 байта = ID + 63.
- **BT**: report 0x31, 78 байт = ID + reserved + 63 + reserved(9) + CRC32 (seed 0xA1 по [0..73]).
- **BT минимальный** (до чтения feature 0x05): report 0x01, 10 байт:
  `[1..4]=LX,LY,RX,RY; [5]=hat+Square/Cross/Circle/Triangle; [6]=L1..R3; [7]=PS/Touchpad(+vendor); [8]=L2; [9]=R2`.
  Раскладка — по распарсенному официальному дескриптору (nondebug.github.io/dualsense).
  MicMute в минимальном отчёте отсутствует.
- После подключения по BT контроллер шлёт **минимальные** отчёты, пока хост не прочитает
  feature-отчёт 0x05 (калибровка); для полноценного режима и для программирования
  подсветки нужен сброс lightbar (LIGHT_OUT).

### 3.3 Эффекты адаптивных триггеров (11 байт: режим + 10 параметров)

| Режим | Код | Кодировка |
|---|---|---|
| Off | 0x05 | выкл (остальные байты 0) |
| Feedback | 0x21 | [1..2] маска зон LE16 (зоны 0..9), [3..6] упакованные 3-битные силы (LE) |
| Weapon | 0x25 | [1..2] биты start\|end, [3] сила-1 |
| Vibration | 0x26 | как Feedback, [9] частота Гц |

Ограничения: позиции 0..9, сила 0..8 (0 = выкл, как у Sony); Weapon: start 2..7, end в (start..8].
**Запрещены**: 0x01/0x02/0x06 («простые», остаточные), 0x11/0x12 (ограниченный диапазон),
**0xFC/0xFD/0xFE — отладочные/калибровочные: «ломают» привод триггера до сброса** (Nielk1).
Неофициальные: Bow 0x22, Galloping 0x23, Machine 0x27 `[НЕ ПОДТВЕРЖДЕНО: поведение на всех прошивках]`.

### 3.4 Запись в контроллер (Windows)

`WriteFile` (interrupt OUT) предпочтителен; `HidD_SetOutputReport` (control-трансфер)
по Bluetooth может зависать (janaxelson.com, HidSharp/HIDAPI документация).
Размер буфера должен соответствовать дескриптору: USB — 48 Б, BT — 78 Б.

## 4. Протокол VIIPER (TCP :3242, hbashton V5)

Управление: `\0`-terminated текстовые запросы, ответ читается до закрытия соединения:
`ping`, `bus`, `bus/create`, `bus/{id}/add {"type":"dualsensegamepadv5"}`, `bus/{id}/remove`.
После `add` нужно открыть стрим за 5 с (handshake `bus/{busId}/{devId}\0`), иначе устройство удаляется.

Стрим (кадры V5, заголовок 16 Б):

```
[0..3]="VPCM"  [4]=0x05  [5]=тип  [6..7]=len LE16  [8..11]=seq LE32  [12..15]=CRC32 LE
CRC32 покрывает байты [4..11] + payload (поле CRC пропускается!) — zlib-стандарт.
```

- Клиент → VIIPER: **0x01** = состояние (33 Б: LX,LY,RX,RY int8; Buttons uint32 LE;
  DPad uint8 (U1/D2/L4/R8); L2,R2 uint8; touch1/2 (u16,u16,bool); gyro 3×i16; accel 3×i16);
  0x02 = PCM микрофона (1920 Б, S16LE стерео 48 кГц / 10 мс).
- VIIPER → клиент: **0x81** = обратная связь **474 Б**:
  `[0..5]` rumbleSmall, rumbleLarge, LedR, LedG, LedB, PlayerLeds;
  `[6..16]` нативный блок R2 (11 Б); `[17..27]` L2 (11 Б);
  `[28..75]` **нативный USB output-отчёт 0x02 (48 Б) как его отправила игра**;
  `[76..473]` комбинированный BT-носитель 398 Б (аудио/хаптики).
  **0x83** = атомарно обратная связь + PCM динамика (LE16 длина + 474 + 1920);
  **0x85** = событие микрофона.
- Нарушение версии/CRC/seq/типа — сервер разрывает стрим.
- Upstream-диалект (Alia5): тип `dualsense`, сырой стрим без кадров, обратная связь 6 Б
  (только rumble+RGB+playerLeds) — совместимость сохранена в клиенте.

## 5. HidHide (проверено рефлексией по пакету Nefarius.Drivers.HidHide 1.12.0)

API `IHidHideControlService`: IsInstalled, IsActive, IsAppListInverted, BlockedInstanceIds,
ApplicationPaths, AddBlockedInstanceId/RemoveBlockedInstanceId/ClearBlockedInstancesList,
AddApplicationPath(**string** — один аргумент; в документации Nefarius указан второй
параметр, в сборке 1.12.0 его НЕТ)/RemoveApplicationPath/ClearApplicationsList.
Семантика: HidHide блокирует устройство для ВСЕХ процессов; ApplicationPaths — список
исключений (туда добавляется наш exe). Исключения: HidHide*Exception (NativeErrorCode).

## 6. Эталонные («золотые») векторы

Вычислены независимой реализацией (python+zlib) и зафиксированы в юнит-тестах:

- BT output 0x31 (seq=2, flag0=0x03, mR=0x80, mL=0x40):
  `31201003008040…(нули)…9938997f`
- Триггеры: Feedback(3,5)=`21f8030048922400000000`; Weapon(4,7,6)=`2590000500000000000000`;
  Vibration(2,4,30Гц)=`26fc03c0b66d1b00001e00`
- USB output 0x02 (flag0=0x0C, flag1=0x04, RGB 10FF20): `020c04…(нули)…10ff20`
- V5-кадр 0x81 (seq 7, payload DEADBE): `5650434d0581030007000000cf395a5f` + `deadbe`
- BT input 0x31 (hat=ЮЗ+Cross+Options+PS, тач (100,200), батарея 55% заряжается):
  `31008080808000ff2a252001…0164800c80…1501…466ca43a`

## 7. Источники

1. torvalds/linux `drivers/hid/hid-playstation.c` — канон протокола DualSense (структуры, CRC, тег 0x10, батарея).
2. hbashton/VIIPER: docs/devices/dualsense.md, docs/api/overview.md — V5-протокол, 474-байтная отдача, dualsensegamepadv5.
3. Alia5/VIIPER + alia5.github.io/VIIPER — стабильный TCP API, wire-формат 33 Б.
4. Nielk1 (John Klein), gist «DualSense trigger effect factories» (MIT) — кодировка триггеров, опасные режимы.
5. nondebug.github.io/dualsense — официальные HID-дескрипторы DualSense (USB/BT), раскладка минимального BT-отчёта.
6. Ryochan7/DS4Windows (в т.ч. RC4.6.6) — форвардинг триггеров/хаптики через VIIPER доказан.
7. vadimgrn/usbip-win2 — BSD-2, WHLK-подпись, Win10 1903+.
8. Nefarius/HidHide + Nefarius.Drivers.HidHide 1.12.0 — API скрытия устройств.
9. Microsoft Learn (HID WriteFile/SetOutputReport), janaxelson.com (USB Complete), jloehr/HIDAPI — способ записи.
10. Steamworks ISteamInput/SetDualSenseTriggerEffect — подтверждение офиц. режимов триггеров.
11. controllers.fandom.com (Sony DualSense) — размеры отчётов по дескриптору (output 0x02 = 47 Б тела).

### Дополнение (v1.5.0, сверка с первоисточниками)

- hid-playstation.c (torvalds/master, скачан): `static_assert(sizeof(dualsense_output_report_common) == 47)` —
  валид0/1 (+0/+1), моторы (+2/+3), аудио (+4..9), триггеры (+10..31), audio_control2 (+37),
  valid2 (+38), lightbar_setup (+41), led_brightness (+42), player_leds (+43), RGB (+44..46).
  Рисунки игрока: P1=BIT(2)=0x04, P2=BIT(3)|BIT(1)=0x0A, P3=0x15, P4=0x1B, P5=0x1F.
- BT-дескриптор (nondebug.github.io): output 0x31=78 Б, 0x32=142, 0x33=206, 0x34=270, 0x35=334,
  **0x36=398** (аудиоканал), 0x37=462, 0x38=526, 0x39=547 → MaxOutputReportLength BT = 547.
- hbashton/VIIPER (исходники): `mergeOutputReport` — RawOutputReport = точная команда игры
  (плюс clearIncompleteOutputValidity для коротких записей); носитель 0x36 очищается для
  обычного пути (`dualsensegamepadv5`) и заполняется только аудио-сборщиком
  (dualsensecombinedaudioduplexv5, 0x83-кадры). Структура 0x36: [0]=0x36, [1]=seq<<4,
  [2..10] заголовок пакета 0x11 (поток аудио/хаптик, буфер 16), [11..12] 0x90+63,
  [13..75] состояние 63 Б (vDS-дефолт, первые 47 = отчёт игры), [76..77] 0x92+64,
  [78..141] хаптики PCM (S8 стерео 3 кГц), [142..143] 0x93+0, [144..343] Opus-динамик 200 Б,
  [394..397] CRC32 (seed 0xEADA2D49).

## 8. Выводы для архитектуры

- Виртуальное устройство: `dualsensegamepadv5` через VIIPER (единственный вариант с триггерами).
- Трансляция отдачи: **нативный режим** — брать общий блок из USB-отчёта 0x02 (байты 28..75
  payload 0x81) и оборачивать в BT 0x31 (максимальная точность, ничего не теряется);
  структурный режим — резерв/диагностика.
- Патчи безопасности: санитизация триггеров (0xFC–0xFE → Off), добавление бита
  HAPTICS_SELECT при заданных моторах (по hid-playstation.c — rumble всегда ставит 0x01|0x02).
- Оптимизация пакетов: newest-wins очередь + дедупликация + мин. интервал 4 мс —
  без обрезки содержимого протокола.

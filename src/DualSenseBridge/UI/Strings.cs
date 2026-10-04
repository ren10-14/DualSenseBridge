namespace DualSenseBridge.App.UI;

/// <summary>
/// Строки интерфейса (v1.13.0): русский/английский. Выбор — настройка Language
/// (ru/en). Смена языка пересоздаёт главное окно (все строки читаются при
/// построении UI) и обновляет меню трея (событие LanguageChanged).
/// Диагностические сообщения ядра (журнал, статистика) остаются на русском.
/// </summary>
internal static class Strings
{
    private static string _lang = "ru";

    public static string Lang
    {
        get => _lang;
        set
        {
            var v = string.Equals(value, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
            if (_lang == v) return;
            _lang = v;
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Меню трея подписывается и обновляет тексты при смене языка.</summary>
    public static event Action? LanguageChanged;

    public static bool IsEnglish => _lang == "en";

    public static string S(string key) => IsEnglish ? Table[key].en : Table[key].ru;

    public static string S(string key, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture,
            IsEnglish ? Table[key].en : Table[key].ru, args);

    private sealed record Pair(string ru, string en);

    private static readonly Dictionary<string, Pair> Table = new()
    {
        ["Title"] = new("DualSenseBridge v{0} — мост DualSense (Bluetooth → виртуальный DualSense)",
                        "DualSenseBridge v{0} — DualSense bridge (Bluetooth → virtual DualSense)"),
        ["Status"] = new("Статус", "Status"),
        ["ViiperServer"] = new("VIIPER-сервер:", "VIIPER server:"),
        ["VirtualDs"] = new("Виртуальный DualSense:", "Virtual DualSense:"),
        ["Physical"] = new("Физический контроллер:", "Physical controller:"),
        ["Battery"] = new("Батарея:", "Battery:"),
        ["HidHide"] = new("HidHide:", "HidHide:"),
        ["Deps"] = new("Зависимости (usbip+viiper):", "Dependencies (usbip+viiper):"),
        ["Feedback"] = new("Отдача от игры:", "Game feedback:"),

        ["ViiperConnected"] = new("подключён (v{0})", "connected (v{0})"),
        ["ViiperDisconnected"] = new("не подключён", "not connected"),
        ["VirtualCreated"] = new("создан ({0})", "created ({0})"),
        ["VirtualNotCreated"] = new("не создан", "not created"),
        ["BridgeActive"] = new("{0} ({1}) — мост активен", "{0} ({1}) — bridge active"),
        ["BridgeNotRunning"] = new("виден: {0} [{1}] — мост не запущен", "visible: {0} [{1}] — bridge not running"),
        ["NotFoundHint"] = new("не найден (подключите DualSense по Bluetooth или USB)",
                               "not found (connect a DualSense via Bluetooth or USB)"),
        ["HidHideHiddenHint"] = new(
            " — но HidHide сейчас прячет DualSense от этой программы (exe сменил имя?). Меню трея → «Показать контроллер», либо добавьте этот exe в список разрешённых в клиенте HidHide",
            " — but HidHide is currently hiding the DualSense from this program (exe renamed?). Tray menu → “Show controller”, or add this exe to the allow-list in the HidHide client"),
        ["HidHideHidden"] = new("установлен, контроллер скрыт", "installed, controller hidden"),
        ["HidHideInstalled"] = new("установлен", "installed"),
        ["HidHideNotInstalled"] = new("не установлен", "not installed"),

        ["UsbipReady"] = new("готовы (usbip-win2 {0} + viiper.exe)", "ready (usbip-win2 {0} + viiper.exe)"),
        ["UsbipVhciMess"] = new(
            "usbip-win2 {0}, но активных vHci-устройств {1} (остатки старой установки мешают usbip!) — «Установить зависимости» выполнит очистку",
            "usbip-win2 {0}, but {1} active vHci devices (old install leftovers break usbip!) — “Install dependencies” will clean up"),
        ["UsbipWrongVer"] = new("usbip-win2 {0} — viiper требует {1}! Кнопка «Установить зависимости» заменит драйвер",
                                "usbip-win2 {0} — viiper requires {1}! “Install dependencies” will replace the driver"),
        ["UsbipWrongNoViiper"] = new("usbip-win2 {0} (нужно {1}) и viiper.exe не готов",
                                     "usbip-win2 {0} (need {1}) and viiper.exe not ready"),
        ["UsbipOkNoViiper"] = new("usbip-win2 ок, viiper.exe не готов (запустится из встроенных ресурсов)",
                                  "usbip-win2 OK, viiper.exe not ready (will start from bundled resources)"),
        ["UsbipNone"] = new("не установлены — кнопка «Установить зависимости (автоматически)»; тесты эффектов работают уже сейчас",
                            "not installed — use “Install dependencies (automatic)”; effect tests already work"),
        ["ViiperNotReady"] = new("; viiper.exe не готов", "; viiper.exe not ready"),

        ["FbNoData"] = new("нет данных (запустите игру — здесь появится всё, что она посылает)",
                           "no data (launch a game — everything it sends will appear here)"),
        ["FbFrames"] = new("#{0}, {1}", "#{0}, {1}"),
        ["StatsNoData"] = new("нет данных", "no data"),
        ["BridgeNotStartedStats"] = new(
            "мост не запущен — после установки зависимостей (usbip-win2 + viiper.exe) он стартует автоматически",
            "bridge not running — it starts automatically once dependencies are installed (usbip-win2 + viiper.exe)"),
        ["TransportDetecting"] = new("транспорт уточняется", "detecting transport"),

        ["TestsGroup"] = new("Тесты эффектов (отправляются напрямую в физический контроллер)",
                             "Effect tests (sent directly to the physical controller)"),
        ["TestRumble"] = new("Вибрация", "Rumble"),
        ["TestTriggerFeedback"] = new("Триггер R2: сопротивление", "Trigger R2: resistance"),
        ["TestTriggerWeapon"] = new("R2: оружие", "R2: weapon"),
        ["TestTriggerVibro"] = new("L2: вибрация", "L2: vibration"),
        ["TestLightbar"] = new("Подсветка", "Lightbar"),
        ["TestPlayerLeds"] = new("Player LEDs", "Player LEDs"),
        ["InstallDeps"] = new("Установить зависимости (автоматически)", "Install dependencies (automatic)"),
        ["CheckConsole"] = new("Проверить (консоль)", "Check (console)"),
        ["OpenLog"] = new("Открыть журнал", "Open log"),

        ["LogGroup"] = new("Статистика пакетов и журнал", "Packet statistics & log"),
        ["SettingsGroup"] = new("Настройки (применяются сразу, сохраняются автоматически)",
                                "Settings (applied immediately, saved automatically)"),

        ["LangLabel"] = new("Язык:", "Language:"),
        ["LangRu"] = new("Русский", "Русский"),
        ["LangEn"] = new("English", "English"),
        ["ThemeLabel"] = new("Тема:", "Theme:"),
        ["ThemeLight"] = new("Светлая", "Light"),
        ["ThemeDark"] = new("Тёмная", "Dark"),

        ["ModeLabel"] = new("Режим трансляции:", "Translation mode:"),
        ["ModeNative"] = new("Native (нативный отчёт игры)", "Native (game's native report)"),
        ["ModeStructured"] = new("Structured (только rumble/LED/триггеры)", "Structured (rumble/LED/triggers only)"),
        ["DialectLabel"] = new("Диалект VIIPER:", "VIIPER dialect:"),
        ["DialectV5"] = new("hbashton V5 (триггеры)", "hbashton V5 (triggers)"),
        ["DialectUpstream"] = new("upstream (только rumble/LED)", "upstream (rumble/LED only)"),
        ["MinIntervalLabel"] = new("Мин. интервал записи, мс:", "Min write interval, ms:"),
        ["RumbleLabel"] = new("Интенсивность вибрации:", "Rumble intensity:"),
        ["Duplex"] = new("Полный дуплекс (хаптики PCM TLOU и др.)", "Full duplex (PCM haptics, TLOU etc.)"),
        ["PlayerLedAlways"] = new("LED «игрок 1» всегда включён", "Player-1 LED always on"),
        ["PlayerLedChk2"] = new("Player LED: всегда «игрок 1»", "Player LED: always “player 1”"),
        ["DefaultLightbarChk"] = new("Подсветка по умолчанию (пока игра не управляет)",
                                     "Default lightbar (until the game takes over)"),
        ["LightbarColorBtn"] = new("Цвет подсветки…", "Lightbar color…"),
        ["HapticsGainLabel"] = new("Усиление хаптик:", "Haptics gain:"),

        ["TrayStatusSettings"] = new("Статус и настройки", "Status && settings"),
        ["TrayInstallDeps"] = new("Установить зависимости…", "Install dependencies…"),
        ["TrayTestRumble"] = new("Тест: вибрация", "Test: rumble"),
        ["TrayTestTriggers"] = new("Тест: триггеры", "Test: triggers"),
        ["TrayTestLightbar"] = new("Тест: подсветка", "Test: lightbar"),
        ["TrayHide"] = new("Скрыть контроллер (HidHide)", "Hide controller (HidHide)"),
        ["TrayShow"] = new("Показать контроллер", "Show controller"),
        ["TrayExit"] = new("Выход", "Exit"),
    };
}

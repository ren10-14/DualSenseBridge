using DualSenseBridge.App.Setup;
using DualSenseBridge.Core;
using DualSenseBridge.Core.Bridge;
using DualSenseBridge.Core.Hid;
using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Translation;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.App.UI;

/// <summary>
/// Главное окно: статус компонентов, тестовые кнопки (вибрация/триггеры/подсветка),
/// статистика пакетов, журнал, настройки. Физический контроллер ищется независимым
/// сканированием — виден в окне даже без запущенного моста и без зависимостей.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly BridgeService _bridge;
    private readonly AppSettings _settings;

    /// <summary>Версия сборки — видна в заголовке окна (чтобы всегда было понятно, какой EXE запущен).</summary>
    private static string AppVersion =>
        typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?";

    private readonly Label _lblViiper = new();
    private readonly Label _lblVirtual = new();
    private readonly Label _lblPhysical = new();
    private readonly Label _lblBattery = new();
    private readonly Label _lblHidHide = new();
    private readonly Label _lblDeps = new();
    private readonly Label _lblFeedback = new();
    private readonly Label _lblStats = new();
    private readonly CheckBox _chkPlayerOne = new();
    private readonly TextBox _logBox = new();
    private readonly ComboBox _cmbMode = new();
    private readonly ComboBox _cmbDialect = new();
    private readonly NumericUpDown _numInterval = new();
    private readonly TrackBar _trackRumble = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };

    // Независимое сканирование устройств (не зависит от моста).
    private int _tick;
    private volatile bool _scanRunning;
    private IReadOnlyList<DualSenseDeviceInfo>? _lastScan;

    // Кэш статуса зависимостей: usbip.exe запускать на потоке UI НЕЛЬЗЯ —
    // это процесс, который может виснуть (v1.6.0:_refresh только в фоне).
    private volatile DriverSetup.DependencyStatus? _depsCache;
    private volatile bool _depsRefreshing;
    private readonly CheckBox _chkDuplex = new();

    /// <summary>Смена языка: окно закрывается и сразу пересоздаётся (v1.13.0).</summary>
    public bool RequestReopen { get; private set; }

    private Color _okColor = SystemColors.ControlText;
    private Color _warnColor = Color.Firebrick;

    public MainForm(BridgeService bridge, AppSettings settings)
    {
        _bridge = bridge;
        _settings = settings;
        Strings.Lang = _settings.Language; // строки читаются при построении UI
        BuildUi();
        ApplyTheme(_settings.Theme == "dark");
        Log.LineAppended += OnLogLine;
        _uiTimer.Tick += (_, _) => RefreshStatus();
        _uiTimer.Start();
        _logFlushTimer.Tick += (_, _) => FlushLog();
        _logFlushTimer.Start();
        RefreshStatus();
    }

    private void BuildUi()
    {
        Text = Strings.S("Title", AppVersion);
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(800, 700);
        Size = new Size(920, 780);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // панель: язык + тема
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // статус
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // тесты + зависимости
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));  // СТАТИСТИКА (отдельная строка окна)
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));   // журнал
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // настройки

        // --- v1.13.1: панель быстрого доступа — ЯЗЫК и ТЕМА на самом видном месте ---
        var topBar = new Panel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
        var topRight = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        var lblLang = new Label { Text = Strings.S("LangLabel"), AutoSize = true, Padding = new Padding(0, 8, 4, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        var cmbLang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        cmbLang.Items.AddRange(new object[] { Strings.S("LangRu"), Strings.S("LangEn") });
        cmbLang.SelectedIndex = Strings.IsEnglish ? 1 : 0;
        cmbLang.SelectedIndexChanged += (_, _) =>
        {
            _settings.Language = cmbLang.SelectedIndex == 1 ? "en" : "ru";
            _settings.Save();
            Strings.Lang = _settings.Language; // меню трея обновится событием
            RequestReopen = true;              // окно пересоздаётся с новым языком
            Close();
        };
        var lblTheme = new Label { Text = Strings.S("ThemeLabel"), AutoSize = true, Padding = new Padding(16, 8, 4, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        var cmbTheme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        cmbTheme.Items.AddRange(new object[] { Strings.S("ThemeLight"), Strings.S("ThemeDark") });
        cmbTheme.SelectedIndex = _settings.Theme == "dark" ? 1 : 0;
        cmbTheme.SelectedIndexChanged += (_, _) =>
        {
            _settings.Theme = cmbTheme.SelectedIndex == 1 ? "dark" : "light";
            _settings.Save();
            ApplyTheme(_settings.Theme == "dark"); // применяется сразу, без пересоздания
        };
        topRight.Controls.Add(lblLang);
        topRight.Controls.Add(cmbLang);
        topRight.Controls.Add(lblTheme);
        topRight.Controls.Add(cmbTheme);
        topBar.Controls.Add(topRight);
        root.Controls.Add(topBar, 0, 0);

        // --- Статус ---
        var grpStatus = new GroupBox { Text = Strings.S("Status"), Dock = DockStyle.Fill, Padding = new Padding(8) };
        var st = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        st.Controls.Add(new Label { Text = Strings.S("ViiperServer"), AutoSize = true }, 0, 0);
        st.Controls.Add(_lblViiper, 1, 0);
        st.Controls.Add(new Label { Text = Strings.S("VirtualDs"), AutoSize = true }, 0, 1);
        st.Controls.Add(_lblVirtual, 1, 1);
        st.Controls.Add(new Label { Text = Strings.S("Physical"), AutoSize = true }, 0, 2);
        st.Controls.Add(_lblPhysical, 1, 2);
        st.Controls.Add(new Label { Text = Strings.S("Battery"), AutoSize = true }, 0, 3);
        st.Controls.Add(_lblBattery, 1, 3);
        st.Controls.Add(new Label { Text = Strings.S("HidHide"), AutoSize = true }, 0, 4);
        st.Controls.Add(_lblHidHide, 1, 4);
        st.Controls.Add(new Label { Text = Strings.S("Deps"), AutoSize = true }, 0, 5);
        st.Controls.Add(_lblDeps, 1, 5);
        st.Controls.Add(new Label { Text = Strings.S("Feedback"), AutoSize = true }, 0, 6);
        _lblFeedback.Dock = DockStyle.Fill;
        st.Controls.Add(_lblFeedback, 1, 6);
        grpStatus.Controls.Add(st);
        root.Controls.Add(grpStatus, 0, 1);

        // --- Тесты + зависимости ---
        var grpTests = new GroupBox
        {
            Text = Strings.S("TestsGroup"),
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            AutoSize = true,
        };
        var tp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        tp.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // тесты
        tp.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // зависимости

        // Ряд 1: тесты эффектов (с переносом на вторую строку при узком окне).
        var rowTests = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, AutoSize = true,
        };
        AddBtn(rowTests, Strings.S("TestRumble"), async () => await _bridge.TestRumbleAsync());
        AddBtn(rowTests, Strings.S("TestTriggerFeedback"), async () => await _bridge.TestTriggerAsync(false, 0x21));
        AddBtn(rowTests, Strings.S("TestTriggerWeapon"), async () => await _bridge.TestTriggerAsync(false, 0x25));
        AddBtn(rowTests, Strings.S("TestTriggerVibro"), async () => await _bridge.TestTriggerAsync(true, 0x26));
        AddBtn(rowTests, Strings.S("TestLightbar"), async () => await _bridge.TestLightbarAsync(0xFF, 0x40, 0x10));
        AddBtn(rowTests, Strings.S("TestPlayerLeds"), async () => await _bridge.TestPlayerLedsAsync());
        tp.Controls.Add(rowTests, 0, 0);

        // Ряд 2: зависимости — главная кнопка крупнее и заметнее.
        var rowDeps = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, AutoSize = true, Padding = new Padding(0, 8, 0, 0),
        };
        var btnDeps = new Button
        {
            Text = Strings.S("InstallDeps"),
            AutoSize = true,
            Padding = new Padding(10, 6, 10, 6),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        };
        btnDeps.Click += (_, _) =>
        {
            using var dlg = new InstallDialog();
            dlg.ShowDialog(this);
            RefreshStatus();
        };
        rowDeps.Controls.Add(btnDeps);
        var btnCheck = new Button { Text = Strings.S("CheckConsole"), AutoSize = true, Padding = new Padding(4) };
        btnCheck.Click += (_, _) => DriverSetup.OpenCliConsole("--check-deps");
        rowDeps.Controls.Add(btnCheck);
        var btnLog = new Button { Text = Strings.S("OpenLog"), AutoSize = true, Padding = new Padding(4) };
        btnLog.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Log.FilePath}\""); }
            catch (Exception ex) { Log.Error("Не удалось открыть журнал", ex); }
        };
        rowDeps.Controls.Add(btnLog);
        tp.Controls.Add(rowDeps, 0, 1);

        grpTests.Controls.Add(tp);
        root.Controls.Add(grpTests, 0, 2);

        // --- Статистика + журнал ---
        var grpLog = new GroupBox { Text = Strings.S("LogGroup"), Dock = DockStyle.Fill, Padding = new Padding(8) };
        // v1.13.1 — СТАТИСТИКА: отдельная строка КОРНЯ окна (не внутри группы
        // журнала — фикс «пропадания»: в авторастущей строке таблицы над журналом
        // при длинном тексте и переносы строк строка схлопывалась до нуля).
        var statsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 62,
            Padding = new Padding(10, 8, 10, 4),
            BorderStyle = BorderStyle.FixedSingle,
        };
        _lblStats.Dock = DockStyle.Fill;
        _lblStats.AutoSize = false;
        _lblStats.MinimumSize = new Size(0, 48); // страховка от схлопывания
        _lblStats.TextAlign = ContentAlignment.MiddleLeft;
        statsPanel.Controls.Add(_lblStats);
        root.Controls.Add(statsPanel, 0, 3);

        var lp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        lp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Font = new Font("Consolas", 8.5F);
        lp.Controls.Add(_logBox, 0, 0);
        grpLog.Controls.Add(lp);
        root.Controls.Add(grpLog, 0, 4);

        // --- Настройки ---
        var grpSettings = new GroupBox { Text = Strings.S("SettingsGroup"), Dock = DockStyle.Fill, Height = 196, Padding = new Padding(8) };
        var sp = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };

        sp.Controls.Add(new Label { Text = Strings.S("ModeLabel"), AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
        _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMode.Items.AddRange(new object[] { Strings.S("ModeNative"), Strings.S("ModeStructured") });
        _cmbMode.SelectedIndex = _settings.Translation == TranslationMode.Native ? 0 : 1;
        _cmbMode.Width = 250;
        _cmbMode.SelectedIndexChanged += (_, _) =>
        {
            _settings.Translation = _cmbMode.SelectedIndex == 0 ? TranslationMode.Native : TranslationMode.Structured;
            _bridge.TranslationMode = _settings.Translation;
            _settings.Save();
        };
        sp.Controls.Add(_cmbMode);

        sp.Controls.Add(new Label { Text = Strings.S("DialectLabel"), AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
        _cmbDialect.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbDialect.Items.AddRange(new object[] { Strings.S("DialectV5"), Strings.S("DialectUpstream") });
        _cmbDialect.SelectedIndex = _settings.Dialect == ViiperDialect.HbashtonV5 ? 0 : 1;
        _cmbDialect.Width = 190;
        _cmbDialect.SelectedIndexChanged += (_, _) =>
        {
            _settings.Dialect = _cmbDialect.SelectedIndex == 0 ? ViiperDialect.HbashtonV5 : ViiperDialect.Upstream;
            _bridge.Dialect = _settings.Dialect;
            _settings.Save();
        };
        sp.Controls.Add(_cmbDialect);

        sp.Controls.Add(new Label { Text = Strings.S("MinIntervalLabel"), AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
        _numInterval.Minimum = 1;
        _numInterval.Maximum = 100;
        _numInterval.Value = Math.Clamp(_settings.MinWriteIntervalMs, 1, 100);
        _numInterval.Width = 60;
        _numInterval.ValueChanged += (_, _) =>
        {
            _settings.MinWriteIntervalMs = (int)_numInterval.Value;
            _bridge.MinWriteInterval = TimeSpan.FromMilliseconds(_settings.MinWriteIntervalMs);
            _settings.Save();
        };
        sp.Controls.Add(_numInterval);

        sp.Controls.Add(new Label { Text = Strings.S("RumbleLabel"), AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
        _trackRumble.Minimum = 0;
        _trackRumble.Maximum = 100;
        _chkDuplex.Text = Strings.S("Duplex");
        _chkDuplex.AutoSize = true;
        _chkDuplex.Checked = _settings.FullDuplexAudio;
        _chkDuplex.Padding = new Padding(8, 8, 8, 0);
        _chkDuplex.CheckedChanged += (_, _) =>
        {
            _settings.FullDuplexAudio = _chkDuplex.Checked;
            _settings.Save();
            _bridge.FullDuplexAudio = _chkDuplex.Checked;
        };
        sp.Controls.Add(_chkDuplex);
        _bridge.FullDuplexAudio = _settings.FullDuplexAudio;

        // v1.9.9: индикация — LED игрока и подсветка по умолчанию.
        var chkPlayerLed = new CheckBox
        {
            Text = Strings.S("PlayerLedAlways"),
            AutoSize = true,
            Checked = _settings.ForcePlayerOneLed,
            Padding = new Padding(8, 8, 8, 0),
        };
        chkPlayerLed.CheckedChanged += (_, _) =>
        {
            _settings.ForcePlayerOneLed = chkPlayerLed.Checked;
            _settings.Save();
            _bridge.ForcePlayerOneLed = chkPlayerLed.Checked;
        };
        sp.Controls.Add(chkPlayerLed);

        var chkLightbar = new CheckBox
        {
            Text = Strings.S("DefaultLightbarChk"),
            AutoSize = true,
            Checked = _settings.DefaultLightbar,
            Padding = new Padding(8, 8, 8, 0),
        };
        chkLightbar.CheckedChanged += (_, _) =>
        {
            _settings.DefaultLightbar = chkLightbar.Checked;
            _settings.Save();
            _bridge.DefaultLightbar = chkLightbar.Checked;
        };
        sp.Controls.Add(chkLightbar);

        // v1.11.0: выбор цвета подсветки по умолчанию (кнопка + превью-квадрат).
        byte lbR = 0x00, lbG = 0x40, lbB = 0xFF;
        try
        {
            string s = (_settings.DefaultLightbarColor ?? "").Trim().TrimStart('#');
            if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint lv))
            { lbR = (byte)(lv >> 16); lbG = (byte)(lv >> 8); lbB = (byte)lv; }
        }
        catch { }
        var swatch = new Panel { Width = 34, Height = 20, BackColor = Color.FromArgb(lbR, lbG, lbB), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(6, 6, 10, 0), Tag = "swatch" };
        var btnColor = new Button { Text = Strings.S("LightbarColorBtn"), AutoSize = true, Margin = new Padding(8, 2, 0, 0) };
        btnColor.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = swatch.BackColor, FullOpen = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            swatch.BackColor = dlg.Color;
            _settings.DefaultLightbarColor = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            _settings.Save();
            _bridge.DefaultLightbarColor = (dlg.Color.R, dlg.Color.G, dlg.Color.B);
        };
        sp.Controls.Add(btnColor);
        sp.Controls.Add(swatch);
        _bridge.ForcePlayerOneLed = _settings.ForcePlayerOneLed;
        _bridge.DefaultLightbar = _settings.DefaultLightbar;
        _bridge.DefaultLightbarColor = (lbR, lbG, lbB);

        sp.Controls.Add(new Label { Text = Strings.S("HapticsGainLabel"), AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
        var trackHap = new TrackBar();
        trackHap.Minimum = 25;
        trackHap.Maximum = 400;
        trackHap.Value = (int)Math.Clamp(_settings.HapticsGain * 100, 25, 400);
        trackHap.Width = 120;
        trackHap.TickFrequency = 50;
        trackHap.ValueChanged += (_, _) =>
        {
            _settings.HapticsGain = trackHap.Value / 100.0;
            _settings.Save();
            _bridge.HapticsGain = _settings.HapticsGain;
        };
        sp.Controls.Add(trackHap);
        _bridge.HapticsGain = _settings.HapticsGain;

        _chkPlayerOne.Text = Strings.S("PlayerLedChk2");
        _chkPlayerOne.AutoSize = true;
        _chkPlayerOne.Checked = _settings.ForcePlayerOneLed;
        _chkPlayerOne.Padding = new Padding(8, 8, 8, 0);
        _chkPlayerOne.CheckedChanged += (_, _) =>
        {
            _settings.ForcePlayerOneLed = _chkPlayerOne.Checked;
            _settings.Save();
            _bridge.ForcePlayerOneLed = _chkPlayerOne.Checked;
        };
        sp.Controls.Add(_chkPlayerOne);
        _bridge.ForcePlayerOneLed = _settings.ForcePlayerOneLed;

        _trackRumble.Value = (int)(_settings.RumbleScale * 100);
        _trackRumble.Width = 140;
        _trackRumble.ValueChanged += (_, _) =>
        {
            _settings.RumbleScale = _trackRumble.Value / 100.0;
            _bridge.RumbleScale = _settings.RumbleScale;
            _settings.Save();
        };
        sp.Controls.Add(_trackRumble);

        grpSettings.Controls.Add(sp);
        root.Controls.Add(grpSettings, 0, 5);

        Controls.Add(root);

        FormClosed += (_, _) =>
        {
            _uiTimer.Stop();
            _logFlushTimer.Stop();
            Log.LineAppended -= OnLogLine;
        };
    }

    private void ApplyTheme(bool dark)
    {
        UiTheme.Apply(this, dark);
        _okColor = dark ? UiTheme.DarkText : SystemColors.ControlText;
        _warnColor = dark ? Color.OrangeRed : Color.Firebrick;
    }

    private static void AddBtn(FlowLayoutPanel panel, string text, Func<Task> action)
    {
        var btn = new Button { Text = text, AutoSize = true, Padding = new Padding(4) };
        btn.Click += async (_, _) =>
        {
            btn.Enabled = false;
            try { await action(); }
            catch (Exception ex) { Log.Error($"Тест «{text}» не удался", ex); }
            finally { btn.Enabled = true; }
        };
        panel.Controls.Add(btn);
    }

    private void RefreshStatus()
    {
        try
        {
        RefreshStatusCore();
        }
        catch (Exception ex)
        {
            Log.Debug($"RefreshStatus: {ex.Message}");
        }
    }

    private void RefreshStatusCore()
    {
        var s = _bridge.Status;
        _lblViiper.Text = s.ViiperConnected ? Strings.S("ViiperConnected", s.ViiperVersion) : Strings.S("ViiperDisconnected");
        _lblVirtual.Text = s.VirtualDeviceCreated
            ? Strings.S("VirtualCreated", s.VirtualDeviceType.Length > 0 ? s.VirtualDeviceType : "USB DualSense")
            : Strings.S("VirtualNotCreated");

        // Физический контроллер: сначала статус моста; если мост не активен —
        // независимое сканирование (каждые 2 с, вне потока UI).
        if (s.PhysicalDeviceConnected)
        {
            _lblPhysical.Text = Strings.S("BridgeActive", s.PhysicalDeviceName, TransportName(s.PhysicalTransport));
        }
        else
        {
            var scan = _lastScan;
            if (scan is { Count: > 0 })
            {
                var best = scan.OrderBy(d => d.Transport == Transport.Bluetooth ? 0 : 1).First();
                _lblPhysical.Text = Strings.S("BridgeNotRunning", best.ProductName, TransportName(best.Transport));
            }
            else
            {
                // v1.9.5: важный случай — контроллер есть, но HidHide прячет его
                // от ЭТОЙ копии программы (exe сменил имя/путь, а допуск не добавился).
                // Проверяем список блокировки самого драйвера: если там DualSense —
                // говорим прямо, что делать, вместо загадочного «не найден».
                string blockedHint = "";
                try
                {
                    var hh = Core.HidHide.HidHideManager.Shared;
                    if (hh.IsInstalled && hh.IsActive && hh.GetBlockedDualSenseIds().Count > 0)
                        blockedHint = Strings.S("HidHideHiddenHint");
                }
                catch { }
                _lblPhysical.Text = Strings.S("NotFoundHint") + blockedHint;
            }
        }

        _lblBattery.Text = s.BatteryPercent >= 0 ? $"{s.BatteryPercent}%" : "—";
        _lblHidHide.Text = DriverSetup.GetHidHideStatus()
            ? (s.DeviceHidden ? Strings.S("HidHideHidden") : Strings.S("HidHideInstalled"))
            : Strings.S("HidHideNotInstalled");

        // Фоновое обновление кэша не чаще раза в 5 с (и никогда на потоке UI).
        // v1.9.4: прежний флаг _depsRefreshing защищал только от ПАРАЛЛЕЛЬНЫХ
        // обновлений, но не от частоты — проверка порта viiper выполнялась КАЖДУЮ
        // секунду, и viiper заваливался ошибками «api handshake check error=EOF»
        // (пустое TCP-подключение), а наш журнал — их пересылкой.
        if (!_depsRefreshing && (DateTime.UtcNow - _depsLastRefreshUtc).TotalSeconds >= 5)
        {
            _depsRefreshing = true;
            _depsLastRefreshUtc = DateTime.UtcNow;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { _depsCache = DriverSetup.GetDependencyStatus(); }
                catch { }
                finally { _depsRefreshing = false; }
            });
        }
        var deps = _depsCache ?? new DriverSetup.DependencyStatus(
            false, null, false, 0, false, null, false);
        bool viiperFound = deps.ViiperRunning || deps.ViiperPath is not null;
        bool versionOk = deps.UsbipInstalled && deps.UsbipVersion == DriverSetup.RequiredUsbipVersion;
        string usbipText;
        if (deps.UsbipReady)
            usbipText = Strings.S("UsbipReady", deps.UsbipVersion);
        else if (deps.UsbipInstalled && versionOk && deps.VhciCount > 1)
            usbipText = Strings.S("UsbipVhciMess", deps.UsbipVersion, deps.VhciCount);
        else if (deps.UsbipInstalled && !versionOk && viiperFound)
            usbipText = Strings.S("UsbipWrongVer", deps.UsbipVersion ?? "?", DriverSetup.RequiredUsbipVersion);
        else if (deps.UsbipInstalled && !versionOk)
            usbipText = Strings.S("UsbipWrongNoViiper", deps.UsbipVersion ?? "?", DriverSetup.RequiredUsbipVersion);
        else if (versionOk && !viiperFound)
            usbipText = Strings.S("UsbipOkNoViiper");
        else
            usbipText = Strings.S("UsbipNone");
        _lblDeps.Text = viiperFound && deps.UsbipReady ? usbipText : (usbipText + (viiperFound ? "" : Strings.S("ViiperNotReady")));
        _lblDeps.ForeColor = deps.UsbipReady && viiperFound ? _okColor : _warnColor;

        var fb = _bridge.Status;
        _lblFeedback.Text = fb.FeedbackFrames == 0
            ? Strings.S("FbNoData")
            : Strings.S("FbFrames", fb.FeedbackFrames, fb.LastFeedbackSummary);

        var stats = _bridge.OutputStats;
        _lblStats.Text = s.IsRunning
            ? stats?.Describe() ?? Strings.S("StatsNoData")
            : Strings.S("BridgeNotStartedStats");

        // Запуск фонового сканирования устройств каждые 2 тика (2 с).
        if (++_tick % 2 == 0 && !_scanRunning && !s.PhysicalDeviceConnected)
        {
            _scanRunning = true;
            Task.Run(() =>
            {
                try { _lastScan = DualSenseHidDevice.Enumerate(); }
                catch { _lastScan = null; }
                finally { _scanRunning = false; }
            });
        }
    }

    private static string TransportName(Transport t) => t switch
    {
        Transport.Bluetooth => "Bluetooth",
        Transport.Usb => "USB",
        _ => Strings.S("TransportDetecting"),
    };

    // Журнал: строки копятся в очередь, UI забирает пачками (не BeginInvoke
    // на каждую строку — при интенсивном журнале UI-поток захлёбывался).
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logFlushTimer = new() { Interval = 150 };
    private DateTime _depsLastRefreshUtc = DateTime.MinValue;

    private void OnLogLine(string line)
    {
        _logQueue.Enqueue(line);
    }

    private void FlushLog()
    {
        if (IsDisposed) return;
        int appended = 0;
        while (appended < 200 && _logQueue.TryDequeue(out var line))
        {
            _logBox.AppendText(line + Environment.NewLine);
            appended++;
        }
        // Ограничение объёма: держим «хвост» журнала, иначе TextBox растёт
        // до сотен МБ и окно умираает.
        if (_logBox.TextLength > 300_000)
            _logBox.Text = _logBox.Text[^150_000..];
    }
}

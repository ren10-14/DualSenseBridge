using DualSenseBridge.App.Setup;
using DualSenseBridge.Core;
using DualSenseBridge.Core.Bridge;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.App.UI;

/// <summary>
/// Контекст приложения: значок в трее + главное окно.
/// Супервизор каждые 15 с пытается запустить мост (устойчив к тому, что
/// зависимости установлены позже или viiper.exe стартует не мгновенно),
/// а тесты эффектов и индикация физического контроллера работают всегда.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private MainForm? _mainForm;
    private InstallDialog? _installDialog;
    private readonly BridgeService _bridge = new();
    private readonly AppSettings _settings;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _supervisor;
    private System.Threading.Timer? _startupWatchdog;

    /// <summary>Пункты меню трея (для смены языка на лету, v1.13.0).</summary>
    private readonly ToolStripMenuItem?[] _menuItems = new ToolStripMenuItem?[8];
    private int _supervisorFailures;
    private readonly SynchronizationContext _uiContext;

    public TrayApplicationContext(string mode, string dialect)
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = AppSettings.Load();
        Strings.Lang = _settings.Language; // меню трея строится ниже — уже с нужным языком
        if (mode == "structured") _settings.Translation = Core.Translation.TranslationMode.Structured;
        if (dialect == "upstream") _settings.Dialect = ViiperDialect.Upstream;

        _bridge.TranslationMode = _settings.Translation;
        _bridge.Dialect = _settings.Dialect;
        _bridge.RumbleScale = _settings.RumbleScale;
        _bridge.MinWriteInterval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.MinWriteIntervalMs, 1, 100));
        _bridge.FullDuplexAudio = _settings.FullDuplexAudio;
        _bridge.ForcePlayerOneLed = _settings.ForcePlayerOneLed;
        _bridge.HapticsGain = _settings.HapticsGain;
        _bridge.DefaultLightbar = _settings.DefaultLightbar;
        _bridge.DefaultLightbarColor = ParseColor(_settings.DefaultLightbarColor, 0x00, 0x40, 0xFF);
        _bridge.StatusChanged += UpdateTray; // маршалинг внутри UpdateTray

        var menu = new ContextMenuStrip();
        // v1.13.0: тексты меню локализованы; ссылки храним — смена языка
        // обновляет их без пересоздания иконки.
        _menuItems[0] = new ToolStripMenuItem(Strings.S("TrayStatusSettings"));
        _menuItems[0].Click += (_, _) => ShowMainForm();
        menu.Items.Add(_menuItems[0]);
        menu.Items.Add(new ToolStripSeparator());
        _menuItems[1] = new ToolStripMenuItem(Strings.S("TrayInstallDeps"));
        _menuItems[1].Click += (_, _) => ShowInstallDialog();
        menu.Items.Add(_menuItems[1]);
        _menuItems[2] = new ToolStripMenuItem(Strings.S("TrayTestRumble"));
        _menuItems[2].Click += async (_, _) => await SafeTest(() => _bridge.TestRumbleAsync());
        menu.Items.Add(_menuItems[2]);
        _menuItems[3] = new ToolStripMenuItem(Strings.S("TrayTestTriggers"));
        _menuItems[3].Click += async (_, _) =>
        {
            await SafeTest(() => _bridge.TestTriggerAsync(false, 0x21));
            await SafeTest(() => _bridge.TestTriggerAsync(true, 0x25));
        };
        menu.Items.Add(_menuItems[3]);
        _menuItems[4] = new ToolStripMenuItem(Strings.S("TrayTestLightbar"));
        _menuItems[4].Click += async (_, _) => await SafeTest(() => _bridge.TestLightbarAsync(0xFF, 0x40, 0x10));
        menu.Items.Add(_menuItems[4]);
        menu.Items.Add(new ToolStripSeparator());
        _menuItems[5] = new ToolStripMenuItem(Strings.S("TrayHide"));
        _menuItems[5].Click += (_, _) => DriverSetup.TryHideDevices(true);
        menu.Items.Add(_menuItems[5]);
        _menuItems[6] = new ToolStripMenuItem(Strings.S("TrayShow"));
        _menuItems[6].Click += (_, _) => DriverSetup.TryHideDevices(false);
        menu.Items.Add(_menuItems[6]);
        menu.Items.Add(new ToolStripSeparator());
        _menuItems[7] = new ToolStripMenuItem(Strings.S("TrayExit"));
        _menuItems[7].Click += (_, _) => ExitApp();
        menu.Items.Add(_menuItems[7]);
        Strings.LanguageChanged += UpdateMenuTexts;

        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Text = "DualSenseBridge",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMainForm();

        // v1.9.5: ДО запуска таймеров и супервизора гарантируем свой допуск в
        // HidHide (если он установлен). HidHide переживает перезапуск, а список
        // разрешённых хранит ПУТЬ exe — при смене имени файла без этого новый
        // exe НЕ ВИДИТ физический контроллер, пока HidHide активен. Это делается
        // до всего остального: control-устройство HidHide однопользовательское,
        // и статус-таймер не должен спорить с этой операцией.
        Core.HidHide.HidHideManager.Shared.EnsureSelfWhitelisted();

        _statusTimer.Tick += (_, _) => UpdateTray();
        _statusTimer.Start();

        // Супервизор: автозапуск моста с повторами (зависимости могут
        // появиться позже; viiper.exe после старта слушает порт не мгновенно).
        _supervisor = Task.Run(() => SupervisorLoopAsync(_shutdownCts.Token));

        // Вотчдог запуска (v1.9.2): если через 20 с мост так и не работает —
        // показываем окно принудительно (тихий режим не должен выглядеть как
        // «приложение не запустилось»).
        _startupWatchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                if (!_bridge.Status.IsRunning)
                    _uiContext.Post(__ => ShowMainForm(), null);
            }
            catch { }
        }, null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);

        // v1.12.0: окно «Статус и настройки» открывается СРАЗУ при запуске
        // (явное требование пользователя); скрыть — вручную (крестик/трей).
        ShowMainForm();
    }

    /// <summary>«#RRGGBB» → RGB (с запасным значением при кривом формате).</summary>
    private static (byte r, byte g, byte b) ParseColor(string? hex, byte fr, byte fg, byte fb)
    {
        try
        {
            string s = (hex ?? "").Trim().TrimStart('#');
            if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        catch { }
        return (fr, fg, fb);
    }

    /// <summary>
    /// Пытается запустить мост, пока это не удастся. Если зависимости не готовы —
    /// однократно сообщает об этом в журнал и повторяет проверку каждые 15 с.
    /// </summary>
    private async Task SupervisorLoopAsync(CancellationToken ct)
    {
        bool announcedMissing = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_bridge.Status.IsRunning)
                {
                    var deps = DriverSetup.GetDependencyStatus();
                    bool usbipReady = deps.UsbipReady;
                    bool viiperReady = deps.ViiperRunning || DriverSetup.TryStartViiper();
                    if (usbipReady && viiperReady)
                    {
                        // ВАЖНО (v1.9.2): сначала мост, HidHide — потом и best-effort:
                        // любые проблемы HidHide не должны мешать запуску моста.
                        await _bridge.StartAsync(ct);
                        if (_settings.HidePhysicalDevice)
                        {
                            try { DriverSetup.TryHideDevices(true); }
                            catch (Exception hex) { Log.Warn($"HidHide: {hex.Message}"); }
                        }
                        Log.Info("Мост запущен автоматически");
                    }
                    else if (!announcedMissing)
                    {
                        announcedMissing = true;
                        var problems = new List<string>();
                        if (!deps.UsbipInstalled)
                            problems.Add("usbip-win2 не установлен");
                        else if (!usbipReady)
                            problems.Add($"usbip-win2 версии {deps.UsbipVersion ?? "?"} — viiper требует строго {DriverSetup.RequiredUsbipVersion}");
                        if (!viiperReady)
                            problems.Add("viiper.exe не запущен");
                        Log.Warn("Зависимости не готовы: " + string.Join("; ", problems) + ". " +
                                 "Окно программы → кнопка «Установить зависимости (автоматически)» — скачает и поставит нужные версии. " +
                                 "Тесты эффектов на физическом контроллере доступны уже сейчас; " +
                                 "проверка зависимостей повторяется каждые 15 с.");
                    }
                }
                else
                {
                    announcedMissing = false;
                    _supervisorFailures = 0;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Автозапуск моста не удался (повтор через 15 с)", ex);

                // v1.9.1: три неудачи подряд — показываем окно, чтобы «тихий» режим
                // (StartMinimized) не выглядел как «приложение не запустилось».
                if (++_supervisorFailures >= 3)
                {
                    _supervisorFailures = 0;
                    try { _uiContext.Post(_ => ShowMainForm(), null); } catch { }
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SafeTest(Func<Task> test)
    {
        try { await test(); }
        catch (Exception ex) { Log.Error("Тест не удался", ex); }
    }

    private void ShowInstallDialog()
    {
        // Немодально: можно продолжать пользоваться треем/окном во время установки.
        if (_installDialog is null || _installDialog.IsDisposed)
        {
            _installDialog = new InstallDialog();
            _installDialog.Show();
        }
        else
        {
            _installDialog.Activate();
        }
    }

    private void ShowMainForm()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_bridge, _settings);
            _mainForm.FormClosed += (_, _) =>
            {
                bool reopen = _mainForm.RequestReopen; // смена языка — пересоздать окно
                _mainForm = null;
                if (reopen) ShowMainForm();
            };
            _mainForm.Show();
        }
        else
        {
            _mainForm.Activate();
        }
    }

    /// <summary>Обновляет тексты меню трея при смене языка (v1.13.0).</summary>
    private void UpdateMenuTexts()
    {
        var texts = new[]
        {
            Strings.S("TrayStatusSettings"), Strings.S("TrayInstallDeps"), Strings.S("TrayTestRumble"),
            Strings.S("TrayTestTriggers"), Strings.S("TrayTestLightbar"), Strings.S("TrayHide"),
            Strings.S("TrayShow"), Strings.S("TrayExit"),
        };
        for (int i = 0; i < _menuItems.Length; i++)
            if (_menuItems[i] is not null) _menuItems[i].Text = texts[i];
    }

    private void UpdateTray()
    {
        // StatusChanged приходит из фоновых потоков моста — marshal в UI-поток.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            _uiContext.Post(_ => UpdateTray(), null);
            return;
        }
        try
        {
            var s = _bridge.Status;
            string status = s.IsRunning ? "мост запущен" : "мост остановлен";
            string battery = s.BatteryPercent >= 0 ? $", батарея {s.BatteryPercent}%" : "";
            string phys = s.PhysicalDeviceConnected
                ? (s.PhysicalTransport == Core.Protocol.Transport.Bluetooth ? "BT" : "USB") : "нет";
            _tray.Text = $"DualSenseBridge: {status} | контроллер: {phys}{battery}";
        }
        catch (ObjectDisposedException) { }
    }

    private void ExitApp()
    {
        _statusTimer.Stop();
        _shutdownCts.Cancel();
        var stop = Task.Run(async () =>
        {
            try
            {
                await _bridge.StopAsync();
                if (_settings.HidePhysicalDevice)
                    DriverSetup.TryHideDevices(false);
            }
            catch { }
        });
        stop.Wait(TimeSpan.FromSeconds(5));

        _tray.Visible = false;
        _mainForm?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _startupWatchdog?.Dispose();
            _statusTimer.Dispose();
            _tray.Dispose();
            _bridge.Dispose();
            _shutdownCts.Dispose();
        }
        base.Dispose(disposing);
    }
}

using DualSenseBridge.App.Setup;
using DualSenseBridge.Core;

namespace DualSenseBridge.App.UI;

/// <summary>
/// Диалог автоматической установки зависимостей: по одной кнопке скачивает
/// компоненты с официальных GitHub-релизов и устанавливает их.
/// viiper.exe распаковывается рядом с программой (портативно, GPL-3.0 —
/// licenses.txt кладётся рядом); драйверы usbip-win2 и HidHide ставятся их
/// штатными установщиками в тихом режиме (Windows потребует подтвердить UAC).
/// </summary>
internal sealed class InstallDialog : Form
{
    private readonly CheckBox _chkUsbIp;
    private readonly CheckBox _chkViiper;
    private readonly CheckBox _chkHidHide;
    private readonly Label _lblStatus = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        ForeColor = Color.DarkSlateGray,
        Text = "Проверяю текущее состояние компонентов…",
    };
    private readonly Button _btnInstall = new() { Text = "Скачать и установить", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _btnCancel = new() { Text = "Закрыть", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 18 };
    private readonly Label _lblStage = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DarkSlateGray };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 8.5F),
        Dock = DockStyle.Fill,
    };

    private CancellationTokenSource? _cts;
    private bool _running;

    public InstallDialog()
    {
        Text = $"Установка зависимостей — DualSenseBridge v{typeof(InstallDialog).Assembly.GetName().Version?.ToString(3) ?? "?"}";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        MinimumSize = new Size(680, 560);
        Size = new Size(720, 600);

        string baseDir = DependencyInstaller.GetWritableBaseDirectory();

        _chkUsbIp = NewChk("usbip-win2 — драйвер виртуальной USB-шины (обязателен для виртуального контроллера; UAC, возможна перезагрузка)", true);
        _chkViiper = NewChk("viiper.exe — сервер виртуальных DualSense, GPL-3.0 (портативный: кладётся в папку программы)", true);
        _chkHidHide = NewChk("HidHide — скрытие физического контроллера от игр, опционально (UAC)", true);

        var intro = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            Text =
                "Нажмите «Скачать и установить» — компоненты будут скачаны с официальных GitHub-релизов " +
                "(hbashton/VIIPER, vadimgrn/usbip-win2, nefarius/HidHide) по HTTPS.\n" +
                $"Файлы: viiper.exe — в папку программы; установщики — в «{baseDir}\\deps».\n" +
                "Драйверы (usbip-win2, HidHide) невозможно «распаковать» — их установщики запустятся в тихом режиме, " +
                "Windows попросит подтвердить UAC (это требование системы). Нужен интернет.",
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // описание
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // текущий статус
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // чекбоксы
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // кнопки
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // прогресс
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // журнал

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(_lblStatus, 0, 1);

        var checks = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        checks.Controls.Add(_chkUsbIp, 0, 0);
        checks.Controls.Add(_chkViiper, 0, 1);
        checks.Controls.Add(_chkHidHide, 0, 2);
        root.Controls.Add(checks, 0, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _btnInstall.Click += async (_, _) => await RunInstallAsync();
        _btnCancel.Click += (_, _) =>
        {
            if (_running) _cts?.Cancel();
            else Close();
        };
        buttons.Controls.Add(_btnInstall);
        buttons.Controls.Add(_btnCancel);
        root.Controls.Add(buttons, 0, 3);

        var progressPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        progressPanel.Controls.Add(_progress, 0, 0);
        progressPanel.Controls.Add(_lblStage, 0, 1);
        root.Controls.Add(progressPanel, 0, 4);

        root.Controls.Add(_log, 0, 5);

        Controls.Add(root);
        FormClosing += (_, e) =>
        {
            if (_running) { _cts?.Cancel(); e.Cancel = true; } // сначала прерываем установку
        };
        UpdateComponentStatuses();
    }

    /// <summary>Текущее состояние компонентов — чтобы сразу было видно, что уже установлено.</summary>
    private void UpdateComponentStatuses()
    {
        try
        {
            var deps = DriverSetup.GetDependencyStatus();

            string usb = !deps.UsbipInstalled ? "НЕ установлен"
                : deps.UsbipReady ? $"{deps.UsbipVersion} (как требуется)"
                : $"{deps.UsbipVersion} — ТРЕБУЕТСЯ {DriverSetup.RequiredUsbipVersion}!";
            string vip = deps.ViiperRunning ? "запущен"
                : deps.ViiperPath is not null ? $"найден, не запущен ({deps.ViiperPath})" : "встроен в приложение (будет распакован)";
            string hh = deps.HidHideInstalled ? "установлен" : "не установлен (опция)";
            _lblStatus.Text = $"Сейчас:  usbip-win2 — {usb};   viiper.exe — {vip};   HidHide — {hh}.";

            // Уже готовое можно не ставить повторно.
            if (deps.UsbipReady) _chkUsbIp.Checked = false;
            if (deps.ViiperRunning) _chkViiper.Checked = false;
            if (deps.HidHideInstalled) _chkHidHide.Checked = false;
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"Не удалось определить состояние компонентов: {ex.Message}";
        }
    }

    private static CheckBox NewChk(string text, bool @checked) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = @checked,
        Padding = new Padding(0, 3, 0, 3),
    };

    private async Task RunInstallAsync()
    {
        if (_running) return;
        if (!_chkUsbIp.Checked && !_chkViiper.Checked && !_chkHidHide.Checked)
        {
            _lblStage.Text = "Отметьте хотя бы один компонент.";
            return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        _btnInstall.Enabled = false;
        _btnCancel.Text = "Прервать";
        foreach (var c in new[] { _chkUsbIp, _chkViiper, _chkHidHide }) c.Enabled = false;
        _progress.Value = 0;
        AppendLog("Начинаю установку. При появлении запросов UAC — подтвердите их.");

        var progress = new Progress<InstallProgress>(p =>
        {
            _lblStage.Text = $"{p.Component} — {p.Stage}{(p.Percent > 0 ? $" {p.Percent}%" : "")}" +
                             (p.Detail.Length > 0 ? $" — {p.Detail}" : "");
            if (p.Percent > 0) _progress.Value = Math.Min(p.Percent, 100);
            AppendLog($"  {p.Component}: {p.Stage} {(p.Percent > 0 ? p.Percent + "%" : "")} {p.Detail}".TrimEnd());
        });

        List<ComponentResult> results;
        try
        {
            results = await DependencyInstaller.InstallAllAsync(
                _chkUsbIp.Checked, _chkViiper.Checked, _chkHidHide.Checked, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            results = new List<ComponentResult> { new("установка", false, "прервана пользователем") };
        }
        catch (Exception ex)
        {
            Log.Error("Установка зависимостей не удалась", ex);
            results = new List<ComponentResult> { new("установка", false, ex.Message) };
        }

        AppendLog("");
        foreach (var r in results)
            AppendLog($"{(r.Success ? "[ OK ]    " : "[ОШИБКА] ")}{r.Component}: {r.Detail}");

        int failed = results.Count(r => !r.Success);
        _lblStage.Text = failed == 0
            ? "Готово! Мост запустится автоматически (в течение ~15 с)."
            : $"Завершено, проблем: {failed} — подробности выше и в журнале.";
        if (failed == 0) _progress.Value = 100;

        _running = false;
        _cts.Dispose();
        _cts = null;
        _btnInstall.Enabled = true;
        _btnCancel.Text = "Закрыть";
        foreach (var c in new[] { _chkUsbIp, _chkViiper, _chkHidHide }) c.Enabled = true;
        UpdateComponentStatuses();
    }

    private void AppendLog(string line)
    {
        if (IsDisposed) return;
        _log.AppendText(line + Environment.NewLine);
    }
}

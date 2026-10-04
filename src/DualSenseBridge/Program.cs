using System.Runtime.InteropServices;
using DualSenseBridge.App.UI;
using DualSenseBridge.App.Setup;
using DualSenseBridge.Core;
using DualSenseBridge.Core.Hid;

namespace DualSenseBridge.App;

/// <summary>
/// Точка входа DualSenseBridge.
///
/// CLI-флаги:
///   --verbose                 подробный журнал (DEBUG-уровень)
///   --log-file &lt;path&gt;          файл журнала (по умолчанию %APPDATA%\DualSenseBridge\DualSenseBridge.log)
///   --list-devices            показать найденные контроллеры и выйти
///   --test-rumble             тест вибрации (нужен подключённый контроллер)
///   --test-triggers           тест адаптивных триггеров L2/R2
///   --test-led                тест подсветки
///   --hide-device             скрыть физический DualSense через HidHide
///   --unhide-device           показать физический DualSense обратно
///   --install-deps            мастер: скачивает usbip-win2 + viiper.exe (+HidHide) с официальных
///                              GitHub-релизов и устанавливает (драйверы — с UAC; viiper — рядом с EXE)
///   --check-deps              проверить состояние зависимостей
///   --console                 консольный режим (без GUI): мост работает, вывод в терминал
///   --mode native|structured  режим трансляции обратной связи (по умолчанию native)
///   --dialect hb|upstream     диалект VIIPER (по умолчанию hb = dualsensegamepadv5)
///   --version                 версия
/// Без флагов — запуск GUI (трей).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // WinExe-приложение не получает консоль само по себе: если запущены из
        // cmd/PowerShell с CLI-флагами — подключаемся к консоли родителя,
        // иначе вывод (--list-devices, --check-deps, тесты) был бы невидим.
        if (args.Length > 0)
            ConsoleAttacher.TryAttach();

        string? logFile = null;
        bool verbose = false, console = false;
        var tests = new List<string>();
        bool listDevices = false, hide = false, unhide = false, installDeps = false, checkDeps = false, showVersion = false, diagnose = false;
        string mode = "native", dialect = "hb";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--verbose": case "-v": verbose = true; break;
                case "--log-file": logFile = Arg(args, ref i); break;
                case "--console": console = true; break;
                case "--list-devices": listDevices = true; break;
                case "--diagnose": diagnose = true; break;
                case "--test-rumble": tests.Add("rumble"); break;
                case "--test-triggers": tests.Add("triggers"); break;
                case "--test-led": tests.Add("led"); break;
                case "--hide-device": hide = true; break;
                case "--unhide-device": unhide = true; break;
                case "--install-deps": installDeps = true; break;
                case "--check-deps": checkDeps = true; break;
                case "--mode": mode = Arg(args, ref i); break;
                case "--dialect": dialect = Arg(args, ref i); break;
                case "--version": showVersion = true; break;
                case "--help": case "-h": case "/?":
                    Console.WriteLine("DualSenseBridge — см. README.md для полного списка флагов.");
                    return 0;
                default:
                    Console.Error.WriteLine($"Неизвестный аргумент: {args[i]}");
                    return 2;
            }
        }

        // Один экземпляр: если уже запущен (в т.ч. зависший) — не стартуем второй,
        // а сообщаем (зависший надо снять в диспетчере задач — он держит контроллер).
        using var singleInstance = new System.Threading.Mutex(
            true, "Local\\DualSenseBridge.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            System.Windows.Forms.MessageBox.Show(
                "DualSenseBridge уже запущен (возможно, зависший экземпляр в фоне — он держит\n" +
                "контроллер и viiper). Откройте окно из трея или завершите процесс\n" +
                "DualSenseBridge-v*.exe в диспетчере задач и запустите снова.",
                "DualSenseBridge", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return 0;
        }
        System.Threading.Thread.Sleep(150); // GC.KeepAlive-страховка не нужна: using держит мьютекс

        logFile ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DualSenseBridge", "DualSenseBridge.log");
        Log.Init(logFile, verbose);
        Log.Info($"DualSenseBridge v{AppVersionString} запущен");

        if (showVersion)
        {
            Console.WriteLine($"DualSenseBridge {AppVersionString}");
            return 0;
        }

        if (listDevices)
        {
            return RunListDevices();
        }

        if (diagnose)
        {
            return RunDiagnose();
        }

        if (checkDeps)
        {
            return DriverSetup.CheckDeps();
        }

        if (installDeps)
        {
            // Мастер: скачивает всё с официальных GitHub-релизов и устанавливает.
            ApplicationConfiguration.Initialize();
            using var dlg = new InstallDialog();
            Application.Run(dlg);
            return 0;
        }

        if (hide || unhide)
        {
            return DriverSetup.RunHideUnhide(hide);
        }

        if (tests.Count > 0)
        {
            return RunTestsAsync(tests).GetAwaiter().GetResult();
        }

        if (console)
        {
            return RunConsoleAsync(mode, dialect).GetAwaiter().GetResult();
        }

        // GUI
        ApplicationConfiguration.Initialize();

        // Глобальные обработчики (v1.6.0): любое необработанное исключение пишем в
        // журнал и НЕ даём приложению молча вылететь; UI-исключения — продолжаем работу.
        System.Windows.Forms.Application.ThreadException += (_, e) =>
            Log.Error("Необработанное исключение интерфейса (работа продолжена)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Log.Error("Необработанное исключение домена", ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Необработанное исключение фоновой задачи", e.Exception);
            e.SetObserved();
        };

        using var ctx = new TrayApplicationContext(mode, dialect);
        Application.Run(ctx);
        return 0;
    }

    private static string AppVersionString =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    private static string Arg(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Для {args[i]} требуется значение");
        return args[++i];
    }

    // ------------------------------------------------------------------ CLI-команды

    /// <summary>
    /// Полная диагностика (v1.9.7): все DualSense-коллекции с размерами, цепочками
    /// предков и вердиктом «физический/виртуальный»; HidHide; viiper; живые отчёты
    /// первого физического кандидата. Один запуск отвечает на все вопросы.
    /// </summary>
    private static int RunDiagnose()
    {
        Console.WriteLine("=== ДИАГНОСТИКА DualSenseBridge ===");
        Console.WriteLine($"Версия: {typeof(Program).Assembly.GetName().Version}");
        Console.WriteLine($"Путь exe: {Environment.ProcessPath}");
        Console.WriteLine();

        Console.WriteLine("--- DualSense-коллекции ---");
        var (physical, virtualDevices) = Core.Hid.WinHid.EnumerateWithVirtuals();
        void Dump(DualSenseBridge.Core.Hid.DualSenseDeviceInfo info, string verdict)
        {
            Console.WriteLine($"[{verdict}] {info.InstanceId}");
            Console.WriteLine($"    путь: {info.DevicePath}");
            Console.WriteLine($"    caps: input {info.MaxInputReportLength} Б / output {info.MaxOutputReportLength} Б");
            var chain = Core.Hid.WinHid.GetAncestorDeviceIds(info.InstanceId);
            Console.WriteLine($"    родители ({chain.Count}): {string.Join(" <- ", chain)}");
        }
        foreach (var v in virtualDevices) Dump(v, "ВИРТУАЛЬНЫЙ");
        foreach (var p in physical) Dump(p, "ФИЗИЧЕСКИЙ");
        if (physical.Count == 0 && virtualDevices.Count == 0)
            Console.WriteLine("DualSense-коллекций не найдено вообще (см. --list-devices для всех HID-устройств).");

        Console.WriteLine();
        Console.WriteLine("--- HidHide ---");
        try
        {
            var hh = Core.HidHide.HidHideManager.Shared;
            Console.WriteLine($"установлен: {hh.IsInstalled}; активен: {hh.IsActive}");
            if (hh.IsInstalled)
            {
                Console.WriteLine($"разрешённые приложения: {string.Join("; ", hh.GetApplicationPaths())}");
                var blocked = hh.HiddenDevices;
                Console.WriteLine(blocked.Count == 0
                    ? "список блокировки: пуст"
                    : "список блокировки: " + string.Join("; ", blocked));
            }
        }
        catch (Exception ex) { Console.WriteLine($"ошибка: {ex.Message}"); }

        Console.WriteLine();
        Console.WriteLine("--- VIIPER ---");
        try
        {
            var viiper = new Core.Viiper.ViiperClient();
            var version = viiper.PingAsync().GetAwaiter().GetResult();
            Console.WriteLine(version is null
                ? "НЕ запущен (127.0.0.1:3242)"
                : $"запущен, версия {version}");
        }
        catch (Exception ex) { Console.WriteLine($"ошибка: {ex.Message}"); }

        if (physical.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("--- Живые отчёты первого физического кандидата ---");
            var pick = Core.Hid.WinHid.OrderPhysicalCandidates(physical)[0];
            Console.WriteLine($"кандидат: {pick.InstanceId} (caps input {pick.MaxInputReportLength} Б)");
            try
            {
                using var dev = new Core.Hid.WinHidStream(pick.DevicePath);
                for (int i = 0; i < 10; i++)
                {
                    var r = dev.Read(300);
                    if (r is null) { Console.WriteLine("  (таймаут — отчётов нет)"); continue; }
                    Console.WriteLine($"  отчёт: id=0x{r[0]:X2}, длина {r.Length} Б " +
                                      $"(от коллекции ожидалось: {dev.InputCap} Б) " +
                                      $"согласован: {DualSenseHidDevice.IsReportConsistentWithCaps(r, dev.InputCap)}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"ошибка открытия/чтения: {ex.Message}"); }
        }

        Console.WriteLine();
        Console.WriteLine("Если физическая коллекция найдена, но отчёты «не согласованы» или их нет — " +
                          "пришлите весь этот вывод целиком.");
        return physical.Count > 0 ? 0 : 1;
    }

    private static int RunListDevices()
    {
        // Основной перечень — как в v1.9.1: то, что видит и открывает мост (HidSharp).
        var devices = DualSenseHidDevice.Enumerate().ToList();
        var (_, virtualDevices) = Core.Hid.WinHid.EnumerateWithVirtuals();
        Console.WriteLine($"Найдено ФИЗИЧЕСКИХ контроллеров DualSense: {devices.Count}" +
                          (virtualDevices.Count > 0 ? $" (+ {virtualDevices.Count} виртуальных usbip, мост их не открывает)" : ""));
        foreach (var info in devices)
        {
            Console.WriteLine($"  - {info}");
            Console.WriteLine($"    instance: {info.InstanceId}");
            Console.WriteLine($"    путь: {info.DevicePath}");
            Console.WriteLine($"    caps: input {info.MaxInputReportLength} Б / output {info.MaxOutputReportLength} Б");
        }
        if (virtualDevices.Count > 0)
        {
            Console.WriteLine("Виртуальные (usbip) DualSense — мост их не трогает:");
            foreach (var info in virtualDevices)
                Console.WriteLine($"  - {info.DevicePath}");
        }
        Console.WriteLine("Транспорт уточняется при подключении: BT = input 0x31 (78 Б) или 0x01 (10 Б), USB = 0x01 (64 Б).");

        if (devices.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Физический DualSense не найден. Все HID-устройства системы:");
            try
            {
                foreach (var path in Core.Hid.WinHid.ListHidInterfacePaths())
                {
                    var vp = Core.Hid.WinHid.TryParseVidPid(path);
                    Console.WriteLine(vp is { } v
                        ? $"  - VID={v.vid:X4} PID={v.pid:X4}  {path}"
                        : $"  - {path}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (не удалось перечислить: {ex.Message})");
            }
            Console.WriteLine();
            Console.WriteLine("Возможные причины:");
            Console.WriteLine("  • контроллер не подключён/не сопряжен (Параметры → Bluetooth → DualSense Wireless Controller);");
            Console.WriteLine("  • контроллер скрыт через HidHide, а ЭТОТ exe не в списке разрешённых —");
            Console.WriteLine("    запустите DualSenseBridge.exe --unhide-device или добавьте текущий exe в список разрешённых HidHide;");
            Console.WriteLine("  • контроллер занят другим приложением с эксклюзивным доступом.");
        }

        var viiper = new Core.Viiper.ViiperClient();
        var version = viiper.PingAsync().GetAwaiter().GetResult();
        Console.WriteLine(version is null
            ? "VIIPER: НЕ запущен (127.0.0.1:3242)"
            : $"VIIPER: запущен, версия {version}");

        var hh = DriverSetup.GetHidHideStatus();
        Console.WriteLine($"HidHide: {(hh ? "установлен" : "не найден")}");
        return devices.Count > 0 ? 0 : 1;
    }

    private static async Task<int> RunTestsAsync(List<string> tests)
    {
        // Предпочтителен Bluetooth (задача проекта), но тесты работают и по USB.
        var devices = DualSenseHidDevice.Enumerate()
            .OrderBy(d => d.Transport == Core.Protocol.Transport.Bluetooth ? 0 : 1)
            .ToList();

        if (devices.Count == 0)
        {
            Console.Error.WriteLine("Контроллер DualSense не найден.");
            return 1;
        }

        var info = devices[0];
        Console.WriteLine($"Используется: {info}");
        if (info.HidDevice is null)
        {
            Console.Error.WriteLine("Устройство найдено, но HidSharp-объект недоступен.");
            return 1;
        }
        using var device = new DualSenseHidDevice(info.HidDevice, info);
        device.Open();
        device.Initialize();
        Console.WriteLine($"Транспорт после подключения: {device.Transport}");

        foreach (var test in tests)
        {
            Console.WriteLine($"Тест: {test} ...");
            switch (test)
            {
                case "rumble":
                    device.WriteCommon(new Core.Protocol.OutputCommon().SetRumble(0xC0, 0xFF));
                    await Task.Delay(400);
                    device.WriteCommon(new Core.Protocol.OutputCommon().SetRumble(0, 0));
                    break;
                case "triggers":
                {
                    var block = new byte[Core.Protocol.TriggerEffect.BlockSize];
                    Core.Protocol.TriggerEffect.Feedback(block, 3, 7);
                    device.WriteCommon(new Core.Protocol.OutputCommon().SetTriggerRight(block));
                    Core.Protocol.TriggerEffect.Weapon(block, 3, 7, 7);
                    device.WriteCommon(new Core.Protocol.OutputCommon().SetTriggerLeft(block));
                    await Task.Delay(2500);
                    device.WriteCommon(new Core.Protocol.OutputCommon()
                        .SetTriggerRight(Core.Protocol.TriggerEffect.Off())
                        .SetTriggerLeft(Core.Protocol.TriggerEffect.Off()));
                    break;
                }
                case "led":
                    device.WriteCommon(new Core.Protocol.OutputCommon().SetLightbar(0xFF, 0x20, 0x20));
                    await Task.Delay(700);
                    device.WriteCommon(new Core.Protocol.OutputCommon().SetLightbar(0x20, 0xFF, 0x20));
                    await Task.Delay(700);
                    device.WriteCommon(new Core.Protocol.OutputCommon().SetLightbar(0x20, 0x20, 0xFF));
                    await Task.Delay(700);
                    device.WriteCommon(new Core.Protocol.OutputCommon().SetLightbar(0, 0, 0x40));
                    break;
            }
            Console.WriteLine("  выполнено.");
        }

        Console.WriteLine($"Журнал: {Log.FilePath}");
        return 0;
    }

    private static async Task<int> RunConsoleAsync(string mode, string dialect)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var settings = AppSettings.Load();
        if (mode == "structured") settings.Translation = Core.Translation.TranslationMode.Structured;
        if (dialect == "upstream") settings.Dialect = Core.Viiper.ViiperDialect.Upstream;

        using var bridge = new Core.Bridge.BridgeService
        {
            TranslationMode = settings.Translation,
            Dialect = settings.Dialect,
            RumbleScale = settings.RumbleScale,
            MinWriteInterval = TimeSpan.FromMilliseconds(Math.Clamp(settings.MinWriteIntervalMs, 1, 100)),
        };

        Console.WriteLine("DualSenseBridge (консольный режим). Ctrl+C — выход.");
        try
        {
            if (settings.HidePhysicalDevice)
                DriverSetup.TryHideDevices(exclusiveToBridge: true);
            await bridge.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось запустить мост", ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var statsTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (await statsTimer.WaitForNextTickAsync(cts.Token))
                {
                    var st = bridge.OutputStats;
                    if (st is not null) Console.WriteLine(st.Describe());
                }
            }
        }
        catch (OperationCanceledException) { }

        await bridge.StopAsync();
        DriverSetup.TryHideDevices(exclusiveToBridge: false);
        return 0;
    }
}


/// <summary>Подключение к консоли родительского процесса (cmd/PowerShell).</summary>
internal static class ConsoleAttacher
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void TryAttach()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        catch
        {
            // не критично: запущены не из консоли
        }
    }
}

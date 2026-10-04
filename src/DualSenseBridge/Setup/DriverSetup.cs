using System.Diagnostics;
using System.Runtime.InteropServices;
using DualSenseBridge.Core;
using DualSenseBridge.Core.Hid;
using DualSenseBridge.Core.HidHide;
using Microsoft.Win32;

namespace DualSenseBridge.App.Setup;

/// <summary>
/// Проверка и установка зависимостей:
///  1) usbip-win2 (BSD-2, драйвер подписан Microsoft attestation / WHLK) —
///     виртуальная USB-шина для VIIPER; требуется одна установка + перезагрузка;
///  2) viiper.exe (GPL-3.0) — userspace-сервер виртуальных устройств;
///  3) HidHide (MIT) — опционально, для скрытия физического контроллера.
/// Ни один компонент не подменяет системные драйверы и не требует отключения
/// проверки подписи (test mode не нужен — usbip-win2 распространяется подписанным).
/// </summary>
public static class DriverSetup
{
    private const string UsbipServiceName = "usbip2_ude";
    private const string UsbipReleasesUrl = "https://github.com/vadimgrn/usbip-win2/releases";

    /// <summary>
    /// Точная версия usbip-win2, которую требует viiper (проверено по бинарникам
    /// hbashton/VIIPER v0.1.2 и v0.1.9-rc4.6.6: строки «usbip-win2 0.9.7.7 …»,
    /// «Refusing to start VIIPER with an incompatible USB/IP runtime» при других версиях).
    /// </summary>
    public const string RequiredUsbipVersion = "0.9.7.7";
    private const string ViiperHbReleasesUrl = "https://github.com/hbashton/VIIPER/releases";
    private const string ViiperUpstreamReleasesUrl = "https://github.com/Alia5/VIIPER/releases";
    private const string HidHideUrl = "https://github.com/nefarius/HidHide/releases";

    // ------------------------------------------------------------------ Проверки

    /// <summary>Драйвер usbip-win2 установлен (сервис usbip2_ude присутствует).</summary>
    public static bool IsUsbIpInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{UsbipServiceName}");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ vHci (виртуальный USB-хост-контроллер)

    /// <summary>GUID интерфейса vHci usbip-win2 — тот же, по которому ищет usbip.exe
    /// (libusbip, userspace/libusbip/src/vhci.cpp; подтверждено разбором проблемы
    /// «Multiple instances of vHci device interface found»).</summary>
    private static readonly Guid VhciInterfaceGuid = new("B4030C06-DC5F-4FCC-87EB-E5515A0935C0");

    private const uint CmGetDeviceInterfaceListPresent = 0; // CM_GET_DEVICE_INTERFACE_LIST_PRESENT

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint ulLen, ref Guid interfaceClassGuid, IntPtr pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, IntPtr pDeviceID, [Out] char[] buffer, uint ulLen, uint ulFlags);

    /// <summary>
    /// Instance ID АКТИВНЫХ (present) vHci-устройств usbip-win2. Если их больше одного —
    /// каждый вызов usbip.exe падает: «Multiple instances of vHci device interface found»
    /// (остаточное «ghost»-устройство от предыдущей установки драйвера). Именно так
    /// usbip.exe определяет ошибку (CM_GET_DEVICE_INTERFACE_LIST_PRESENT).
    /// </summary>
    public static List<string> GetVhciDeviceInstanceIds()
    {
        var result = new List<string>();
        try
        {
            var guid = VhciInterfaceGuid;
            int cr = CM_Get_Device_Interface_List_SizeW(out uint size, ref guid, IntPtr.Zero, CmGetDeviceInterfaceListPresent);
            if (cr != 0 || size == 0) return result;

            var buf = new char[size];
            cr = CM_Get_Device_Interface_ListW(ref guid, IntPtr.Zero, buf, size, CmGetDeviceInterfaceListPresent);
            if (cr != 0) return result;

            foreach (var seg in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // Символическая ссылка вида \\?\root#usbip#0000#{GUID} -> ROOT\USBIP\0000
                string link = seg.StartsWith(@"\\?\") ? seg[4..] : seg;
                int brace = link.IndexOf('{');
                if (brace > 1) link = link[..(brace - 1)];
                string instance = link.Replace('#', '\\');
                if (instance.Length > 0) result.Add(instance);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Перечисление vHci-интерфейсов не удалось: {ex.Message}");
        }
        return result;
    }

    /// <summary>Число активных vHci-устройств (&gt;1 — остатки старой установки, usbip.exe сломан).</summary>
    public static int CountVhciInterfaces() => GetVhciDeviceInstanceIds().Count;

    /// <summary>
    /// Удаляет vHci-устройства (elevated pnputil /remove-device, один UAC на всё).
    /// Вызывается при чистке «ghost»-устройств после замены драйвера usbip-win2.
    /// </summary>
    public static bool RemoveVhciDevices(IEnumerable<string> instanceIds)
    {
        var ids = instanceIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return true;
        try
        {
            var cmd = string.Join(" & ", ids.Select(i => $"pnputil /remove-device \"{i}\""));
            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
            {
                Verb = "runas",
                UseShellExecute = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(120_000);
            Log.Info("Удалены остаточные vHci-устройства: " + string.Join(", ", ids));
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn("Очистка vHci отменена пользователем (UAC)");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось удалить остаточные vHci-устройства", ex);
            return false;
        }
    }

    /// <summary>
    /// Версия установленного usbip-win2 (запускает usbip.exe --version из канонического
    /// пути C:\Program Files\USBip — именно так проверяет и сам viiper).
    /// null — не удалось определить.
    /// </summary>
    public static string? GetInstalledUsbipVersion()
    {
        try
        {
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string exe = Path.Combine(Environment.GetFolderPath(root), "USBip", "usbip.exe");
                if (!File.Exists(exe)) continue;
                var psi = new ProcessStartInfo(exe, "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p is null) continue;

                // v1.9.1: чтение вывода — АСИНХРОННО. ReadToEnd ДО WaitForExit блокируется
                // НАВСЕГДА, если usbip.exe завис (например, драйвер занят потоком игры) —
                // это зависало весь запуск приложения.
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    continue;
                }
                if (!Task.WhenAll(stdoutTask, stderrTask).Wait(2000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    continue;
                }
                string output = stdoutTask.Result + stderrTask.Result;
                var m = System.Text.RegularExpressions.Regex.Match(output, @"(\d+\.\d+\.\d+(?:\.\d+)?)");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// usbip-win2 полностью готов для viiper: версия совпадает с требуемой И нет
    /// дубликатов vHci (&gt;1 активного vHci ломает usbip.exe — «ghost» от старой установки).
    /// </summary>
    public static bool IsUsbipReady()
    {
        var v = GetInstalledUsbipVersion();
        return IsUsbIpInstalled() && v == RequiredUsbipVersion && CountVhciInterfaces() <= 1;
    }

    /// <summary>Сводное состояние зависимостей (для UI, консоли и супервизора).</summary>
    public sealed record DependencyStatus(
        bool UsbipInstalled, string? UsbipVersion, bool UsbipReady, int VhciCount,
        bool ViiperRunning, string? ViiperPath,
        bool HidHideInstalled);

    public static DependencyStatus GetDependencyStatus()
    {
        string? ver = GetInstalledUsbipVersion();
        bool installed = IsUsbIpInstalled() || ver is not null;
        int vhci = CountVhciInterfaces();
        return new DependencyStatus(
            installed, ver, installed && ver == RequiredUsbipVersion && vhci <= 1, vhci,
            IsViiperRunning(), FindViiper(),
            GetHidHideStatus());
    }

    /// <summary>Известные пути поиска viiper.exe.</summary>
    public static IEnumerable<string> GetViiperCandidatePaths()
    {
        var settings = AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(settings.ViiperPath))
            yield return settings.ViiperPath!;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIIPER", "viiper.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DS4Windows", "VIIPER", "viiper.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseBridge", "viiper.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "viiper.exe");
    }

    public static string? FindViiper()
    {
        TryExtractEmbeddedViiper(); // viiper вшит в EXE — распаковываем при необходимости
        return GetViiperCandidatePaths().FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Распаковывает вшитый в приложение viiper.exe (hbashton/VIIPER v0.1.2, GPL-3.0)
    /// в %LOCALAPPDATA%\DualSenseBridge\viiper.exe (+ лицензия рядом). Идемпотентно.
    /// </summary>
    public static bool TryExtractEmbeddedViiper()
    {
        try
        {
            string targetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseBridge");
            string target = Path.Combine(targetDir, "viiper.exe");

            // Уже распакован и не пустой — не трогаем.
            if (File.Exists(target) && new FileInfo(target).Length > 1_000_000)
                return true;

            var asm = typeof(DriverSetup).Assembly;
            string[] names = asm.GetManifestResourceNames();

            string? exeName = names.FirstOrDefault(n => n.EndsWith("viiper.exe", StringComparison.OrdinalIgnoreCase));
            if (exeName is null)
                return false; // сборка без вшитого ресурса (не должно происходить)

            Directory.CreateDirectory(targetDir);
            using (var s = asm.GetManifestResourceStream(exeName))
            using (var f = File.Create(target))
                s!.CopyTo(f);

            string? licName = names.FirstOrDefault(n => n.EndsWith("viiper.licenses.txt", StringComparison.OrdinalIgnoreCase));
            if (licName is not null)
            {
                using var s = asm.GetManifestResourceStream(licName);
                using var f = File.Create(Path.Combine(targetDir, "viiper-licenses.txt"));
                s!.CopyTo(f);
            }

            Log.Info($"viiper.exe распакован из приложения: {target}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось распаковать вшитый viiper.exe", ex);
            return false;
        }
    }

    public static bool IsViiperRunning()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", 3242);
            if (!connect.Wait(700))
                return false;
            // v1.9.4: посылаем КОРРЕКТНЫЙ запрос «ping\0» — пустое подключение
            // (как раньше) заставляло viiper логировать «api handshake check
            // error=EOF» при каждой проверке. viiper отвечает и закрывает
            // соединение сам (один запрос на соединение — internal/server/api).
            tcp.GetStream().Write("ping\0"u8);
            tcp.GetStream().Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool GetHidHideStatus()
    {
        // v1.9.5: общий экземпляр + кэш 5 с — control-устройство HidHide допускает
        // только одно подключение одновременно, и дёргать его каждую секунду
        // (статус-таймер) значило спорить с собственными операциями списка.
        try
        {
            if ((DateTime.UtcNow - _hidHideStatusUtc).TotalSeconds < 5) return _hidHideStatusCache;
            _hidHideStatusCache = HidHideManager.Shared.IsInstalled;
            _hidHideStatusUtc = DateTime.UtcNow;
            return _hidHideStatusCache;
        }
        catch { return false; }
    }
    private static bool _hidHideStatusCache;
    private static DateTime _hidHideStatusUtc = DateTime.MinValue;

    // ------------------------------------------------------------------ Установка

    private static Process? _viiperProcess;
    private static readonly object ViiperStartLock = new();

    /// <summary>
    /// Запускает viiper.exe (если найден и не запущен).
    /// ВАЖНО (проверено на реальном бинарнике v0.1.2): без аргументов viiper.exe
    /// печатает справку и СРАЗУ ЗАВЕРШАЕТСЯ («окно открывается на секунду») —
    /// сервер запускается подкомандой «server»: API на :3242, USBIP на :3241,
    /// авто-аттач виртуальных устройств к localhost на Windows — нативным IOCTL
    /// (usbip.exe в PATH не обязателен; требуется только драйвер usbip-win2).
    /// </summary>
    public static bool TryStartViiper()
    {
        if (IsViiperRunning()) return true;
        var path = FindViiper();
        if (path is null)
            return false; // супервизор сам сообщает об отсутствии зависимостей (без спама каждые 15 с)

        try
        {
            lock (ViiperStartLock)
            {
                // Не запускаем второй экземпляр, если предыдущий ещё жив.
                if (_viiperProcess is { HasExited: false })
                    return true;

                var psi = new ProcessStartInfo(path, "server")
                {
                    WorkingDirectory = Path.GetDirectoryName(path)!,
                    UseShellExecute = false,       // без всплывающего чёрного окна
                    CreateNoWindow = true,
                    RedirectStandardOutput = true, // вывод сервера — в наш журнал
                    RedirectStandardError = true,
                };

                // Фолбэк viiper — usbip.exe из PATH (нативный IOCTL обычно работает сам,
                // но добавим каталог usbip-win2 на случай отличий конфигурации).
                try
                {
                    var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    var usbipDir = Path.Combine(pf, "USBip");
                    if (Directory.Exists(usbipDir))
                        psi.Environment["PATH"] = usbipDir + Path.PathSeparator
                            + (Environment.GetEnvironmentVariable("PATH") ?? "");
                }
                catch { /* вне Windows — не актуально */ }

                var proc = Process.Start(psi);
                if (proc is null)
                {
                    Log.Error("Не удалось запустить процесс viiper.exe (Process.Start вернул null)");
                    return false;
                }
                _viiperProcess = proc;
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    try { Log.Warn($"[viiper] процесс завершён, код выхода {proc.ExitCode}"); }
                    catch { Log.Warn("[viiper] процесс завершён"); }
                };

                // Откачиваем вывод в фоне — иначе буфер пайпа переполнится и сервер
                // заблокируется. ANSI-коды цвета вырезаются.
                _ = Task.Run(() => PumpViiperOutput(proc.StandardOutput));
                _ = Task.Run(() => PumpViiperOutput(proc.StandardError));

                Log.Info($"viiper.exe server запущен: {path} (PID {proc.Id})");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось запустить viiper.exe (server)", ex);
            return false;
        }
    }

    private static void PumpViiperOutput(StreamReader reader)
    {
        try
        {
            while (!reader.EndOfStream)
            {
                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                line = System.Text.RegularExpressions.Regex.Replace(line, @"\x1B\[[0-9;]*[A-Za-z]", "");
                if (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("WARN", StringComparison.Ordinal))
                    Log.Warn($"[viiper] {line}");
                else
                    Log.Info($"[viiper] {line}"); // вывод сервера виден всегда — это главная диагностика
            }
        }
        catch
        {
            // процесс завершён — нормальный конец чтения
        }
    }

    /// <summary>
    /// Запускает CLI-команду этого EXE в отдельном консольном окне
    /// (WinExe-приложение собственной консоли не имеет).
    /// </summary>
    public static void OpenCliConsole(string arguments)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                Log.Warn("Не удалось определить путь к EXE для запуска консольной команды");
                return;
            }
            // cmd /c ""...\DualSenseBridge.exe" --install-deps & pause"
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" {arguments} & pause\"")
            {
                UseShellExecute = true,
            });
            Log.Info($"Запущена команда в консоли: {arguments}");
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось запустить консольную команду {arguments}", ex);
        }
    }

    /// <summary>
    /// Запускает установщик с повышением прав (UAC). Сначала пробует тихую установку
    /// (Inno Setup: /VERYSILENT /NORESTART; NSIS: /S), при неудаче — интерактивно.
    /// НЕ отключает проверку подписей и не устанавливает неподписанные драйверы.
    /// </summary>
    public static bool RunInstallerElevated(string installerPath, params string[] argSets)
    {
        if (argSets is null || argSets.Length == 0)
            argSets = new[] { "/VERYSILENT /NORESTART", "/S", "" }; // Inno Setup, NSIS, интерактивно
        foreach (var args in argSets)
        {
            try
            {
                var psi = new ProcessStartInfo(installerPath)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    Arguments = args,
                };
                using var proc = Process.Start(psi);
                if (proc is null) continue;
                proc.WaitForExit(10 * 60 * 1000);
                if (proc.HasExited && proc.ExitCode == 0)
                {
                    Log.Info($"Установщик выполнен: {Path.GetFileName(installerPath)} {args}");
                    return true;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log.Warn("Установка отменена пользователем (UAC)");
                return false;
            }
            catch
            {
                // пробуем следующий набор аргументов
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ CLI-обёртки

    /// <summary>Печать состояния зависимостей. Возвращает код ошибки (0 = всё ок).</summary>
    public static int CheckDeps()
    {
        Console.WriteLine("=== Состояние зависимостей DualSenseBridge ===");
        bool usbip = IsUsbIpInstalled();
        bool viiperRunning = IsViiperRunning();
        string? viiperPath = FindViiper();
        bool hidhide = GetHidHideStatus();

        string? usbipVer = GetInstalledUsbipVersion();
        string usbipState = !usbip ? "НЕТ"
            : usbipVer == RequiredUsbipVersion ? "OK"
            : "ВЕРСИЯ";
        int vhci = CountVhciInterfaces();
        Console.WriteLine($"[{usbipState}] usbip-win2 (сервис {UsbipServiceName}): установлен {(usbipVer is null ? "версия неизвестна" : usbipVer)}" +
                          (usbip && usbipVer != RequiredUsbipVersion ? $" — viiper требует СТРОГО {RequiredUsbipVersion}!" : ""));
        Console.WriteLine($"      активных vHci-устройств: {vhci}" + (vhci > 1 ? " — БОЛЬШЕ ОДНОГО: usbip.exe не работает (ghost от старой установки); «Установить зависимости» выполнит очистку" : ""));
        Console.WriteLine($"[{(viiperPath is not null ? "OK" : "НЕТ")}] viiper.exe ({(viiperPath ?? "не найден")})");
        Console.WriteLine($"[{(viiperRunning ? "OK" : "-- ")}] VIIPER-сервер 127.0.0.1:3242 {(viiperRunning ? "отвечает" : "не запущен")}");
        Console.WriteLine($"[{(hidhide ? "OK" : "НЕТ")}] HidHide (опционально)");

        if (!usbip || viiperPath is null || !hidhide)
        {
            Console.WriteLine();
            Console.WriteLine("Автоматическая установка: DualSenseBridge.exe --install-deps");
            Console.WriteLine("(или кнопка «Установить зависимости…» в окне программы) — скачает и установит всё с официальных релизов:");
            if (!usbip) Console.WriteLine($"  • usbip-win2: {UsbipReleasesUrl}");
            if (viiperPath is null) Console.WriteLine($"  • viiper.exe: {ViiperHbReleasesUrl} (адаптивные триггеры; альтернатива без триггеров: {ViiperUpstreamReleasesUrl})");
            if (!hidhide) Console.WriteLine($"  • HidHide (опционально): {HidHideUrl}");
        }

        bool usbipReady = usbip && usbipVer == RequiredUsbipVersion;
        return usbipReady && (viiperPath is not null || viiperRunning) ? 0 : 1;
    }

    /// <summary>CLI: скрыть/показать контроллер через HidHide.</summary>
    public static int RunHideUnhide(bool hide)
    {
        var manager = HidHideManager.Shared;
        var ids = HidHideManager.CollectDualSenseInstanceIds();
        if (ids.Count == 0 && !hide)
        {
            // v1.9.5: если контроллер скрыт от НАС, показать его можно только
            // по списку блокировки самого драйвера.
            ids = manager.GetBlockedDualSenseIds();
            if (ids.Count > 0)
                Console.WriteLine("Контроллер не виден перечислению (скрыт HidHide) — использую список блокировки драйвера.");
        }
        if (ids.Count == 0)
        {
            Console.Error.WriteLine("Контроллер DualSense не найден.");
            return 1;
        }

        try
        {
            if (!manager.IsInstalled)
            {
                Console.Error.WriteLine("HidHide не установлен. Скачать: " + HidHideUrl);
                return 1;
            }

            if (hide)
            {
                manager.HideDevices(ids);
                Console.WriteLine("Физический DualSense скрыт от других приложений (HidHide активен).");
                Console.WriteLine("Список скрытых: " + string.Join(", ", manager.HiddenDevices));
            }
            else
            {
                manager.UnhideDevices(ids);
                Console.WriteLine("Физический DualSense снова виден системе.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка HidHide: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Скрытие/показ из кода моста (используется при запуске/остановке).</summary>
    public static void TryHideDevices(bool exclusiveToBridge)
    {
        try
        {
            var manager = HidHideManager.Shared;
            if (!manager.IsInstalled) return;

            if (exclusiveToBridge)
            {
                var ids = HidHideManager.CollectDualSenseInstanceIds();
                if (ids.Count == 0) return;
                manager.HideDevices(ids);

                // v1.9.5, страховка: после скрытия МЫ обязаны по-прежнему видеть
                // физический контроллер (только что добавили себя в разрешённые).
                // Если вдруг нет — немедленно откатываем скрытие: лучше «двойной
                // ввод», чем мост, потерявший контроллер.
                if (DualSenseHidDevice.Enumerate().Count == 0)
                {
                    Log.Error("HidHide: после скрытия физический контроллер перестал быть виден программе — " +
                              "откатываю скрытие (мост не должен терять контроллер)");
                    manager.UnhideDevices(ids);
                }
            }
            else
            {
                // Показ: если перечисление ничего не видит (контроллер скрыт от
                // НАС), список для показа берём из САМОГО ДРАЙВЕРА — это единственный
                // работающий путь из «слепоты» (v1.9.5; раньше кнопка молча
                // ничего не делала).
                var ids = HidHideManager.CollectDualSenseInstanceIds();
                if (ids.Count == 0)
                {
                    ids = manager.GetBlockedDualSenseIds();
                    if (ids.Count > 0)
                        Log.Info($"HidHide: контроллер не виден перечислению — показываю по списку драйвера ({ids.Count} записей)");
                }
                if (ids.Count == 0) return;
                manager.UnhideDevices(ids);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"HidHide: {ex.Message}");
        }
    }
}

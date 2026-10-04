using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using DualSenseBridge.Core;

namespace DualSenseBridge.App.Setup;

/// <summary>Прогресс установки одного компонента (для UI).</summary>
public sealed class InstallProgress
{
    public required string Component { get; init; }
    public required string Stage { get; init; }   // «скачивание», «распаковка», «установка», «проверка»
    public int Percent { get; init; }              // 0..100 в пределах компонента
    public string Detail { get; init; } = "";
}

/// <summary>Итог установки одного компонента.</summary>
public sealed record ComponentResult(string Component, bool Success, string Detail);

/// <summary>
/// Автоматическая установка зависимостей. Всё скачивается ТОЛЬКО с официальных
/// GitHub-релизов по HTTPS:
///   • viiper.exe — https://github.com/hbashton/VIIPER (GPL-3.0, отдельный процесс;
///     архив viiper-windows-amd64.zip распаковывается рядом с программой — портативно);
///   • usbip-win2 — https://github.com/vadimgrn/usbip-win2 (BSD-2, драйвер подписан
///     Microsoft; установщик Inno Setup, тихий режим /VERYSILENT /NORESTART, нужен UAC);
///   • HidHide — https://github.com/nefarius/HidHide (MIT; MSI-стиль, /qn /norestart, UAC).
/// Драйверы невозможно «распаковать» — Windows требует их штатной установки
/// (это системное ограничение, а не наше решение). Никаких сторонних зеркал и подмен файлов.
/// </summary>
public static class DependencyInstaller
{
    // Резервные прямые ссылки на проверенные версии (2026-10-02);
    // используются, если GitHub API недоступен (лимит запросов и т.п.).
    private const string ViiperFallbackUrl =
        "https://github.com/hbashton/VIIPER/releases/download/v0.1.2/viiper-windows-amd64.zip";
    // РОВНО та версия, что нужна viiper (проверено по его бинарнику), — не «последняя»!
    private const string UsbipFallbackUrlX64 =
        "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.7/USBip-0.9.7.7-x64.exe";
    private const string HidHideFallbackUrl =
        "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe";

    /// <summary>
    /// User-Agent для GitHub API/релизов. ВАЖНО: только ASCII — не-ASCII символы в
    /// HTTP-заголовках .NET отвергает с ошибкой «Request headers must contain only
    /// ASCII characters» (такой баг был в v1.1.x — русское слово в UA ломало
    /// ВСЕ запросы, включая скачивание). Поэтому строка формируется здесь
    /// с гарантией ASCII.
    /// </summary>
    public static readonly string UserAgent =
        $"DualSenseBridge/{typeof(DependencyInstaller).Assembly.GetName().Version?.ToString(3) ?? "1.0"} (Windows; local app)";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        // GitHub API требует User-Agent; страховка от не-ASCII (см. комментарий выше).
        string ua = UserAgent.All(c => c is >= ' ' and <= '~') ? UserAgent : "DualSenseBridge";
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        return client;
    }

    // ------------------------------------------------------------------ Общий поток

    /// <summary>
    /// Скачивает и устанавливает выбранные компоненты. Ошибка одного не мешает остальным.
    /// </summary>
    public static async Task<List<ComponentResult>> InstallAllAsync(
        bool installUsbIp, bool installViiper, bool installHidHide,
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var results = new List<ComponentResult>();
        string baseDir = GetWritableBaseDirectory();
        string depsDir = Path.Combine(baseDir, "deps");

        if (installViiper)
            results.Add(await InstallViiperAsync(baseDir, depsDir, progress, ct));
        if (installUsbIp)
            results.Add(await InstallUsbIpAsync(depsDir, progress, ct));
        if (installHidHide)
            results.Add(await InstallHidHideAsync(depsDir, progress, ct));

        return results;
    }

    /// <summary>
    /// Каталог для скачиваемых файлов: рядом с EXE; если записи нет (например,
    /// программа в Program Files) — %LOCALAPPDATA%\DualSenseBridge.
    /// </summary>
    public static string GetWritableBaseDirectory()
    {
        try
        {
            string appDir = AppContext.BaseDirectory;
            string probe = Path.Combine(appDir, ".write-probe");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return appDir;
        }
        catch
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseBridge");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ------------------------------------------------------------------ Компоненты

    private static async Task<ComponentResult> InstallViiperAsync(
        string baseDir, string depsDir, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        const string component = "viiper.exe";
        try
        {
            if (DriverSetup.IsViiperRunning())
                return new ComponentResult(component, true, "уже запущен (127.0.0.1:3242 отвечает)");

            // Основной путь: viiper ВШИТ в этот EXE — просто распаковываем и запускаем.
            Append(progress, component, "распаковка", 10, "из встроенных ресурсов приложения");
            if (DriverSetup.TryExtractEmbeddedViiper() && DriverSetup.FindViiper() is { } embedded)
            {
                var settings = AppSettings.Load();
                settings.ViiperPath = embedded;
                settings.Save();

                Append(progress, component, "запуск", 50, embedded);
                DriverSetup.TryStartViiper();
                for (int i = 0; i < 12 && !DriverSetup.IsViiperRunning(); i++)
                    await Task.Delay(500, ct);

                if (DriverSetup.IsViiperRunning())
                    return new ComponentResult(component, true, $"распакован из приложения и запущен: {embedded}");

                // Не поднялся (обычно — не та версия usbip-win2): сообщение об этом
                // даст компонент usbip-win2 ниже; viiper будет запущен супервизором позже.
                return new ComponentResult(component, true,
                    $"распакован: {embedded}; сервер не поднялся — проверьте версию usbip-win2 (нужна {DriverSetup.RequiredUsbipVersion})");
            }
            Append(progress, component, "скачивание", 0, "вшитый ресурс недоступен — скачиваю с GitHub");

            string zipPath = Path.Combine(depsDir, "viiper-windows-amd64.zip");
            if (File.Exists(zipPath))
            {
                Append(progress, component, "скачивание", 100, "архив уже скачан — используем существующий");
            }
            else
            {
                string? url = await FindLatestAssetUrlAsync(
                    "hbashton/VIIPER",
                    n => n.Equals("viiper-windows-amd64.zip", StringComparison.OrdinalIgnoreCase),
                    ViiperFallbackUrl, component, progress, ct);
                if (url is null)
                    return new ComponentResult(component, false,
                        "в последнем релизе hbashton/VIIPER нет файла viiper-windows-amd64.zip — скачайте вручную (см. README)");
                zipPath = await DownloadAsync(url, depsDir, "viiper-windows-amd64.zip", component, progress, ct);
            }

            // Распаковка рядом с программой (портативно, без установки).
            Append(progress, component, "распаковка", 0, baseDir);
            string exePath = Path.Combine(baseDir, "viiper.exe");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                                e.Name.Equals("viiper.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("в архиве нет viiper.exe");
                entry.ExtractToFile(exePath, overwrite: true);

                // Лицензии (GPL-3.0) — обязательно рядом с бинарником.
                foreach (var lic in zip.Entries.Where(e =>
                             e.Name.Equals("licenses.txt", StringComparison.OrdinalIgnoreCase)))
                    lic.ExtractToFile(Path.Combine(baseDir, "licenses.txt"), overwrite: true);
            }

            // Запоминаем путь в настройках и запускаем сервер.
            var savedSettings = AppSettings.Load();
            savedSettings.ViiperPath = exePath;
            savedSettings.Save();

            Append(progress, component, "запуск", 50, exePath);
            DriverSetup.TryStartViiper();
            for (int i = 0; i < 12 && !DriverSetup.IsViiperRunning(); i++)
                await Task.Delay(500, ct);

            return DriverSetup.IsViiperRunning()
                ? new ComponentResult(component, true, $"установлен и запущен: {exePath}")
                : new ComponentResult(component, true,
                    $"распакован в {exePath}, но порт 3242 пока не отвечает — сервер, возможно, стартует дольше; мост подключится сам");
        }
        catch (OperationCanceledException)
        {
            return new ComponentResult(component, false, "отменено пользователем");
        }
        catch (Exception ex)
        {
            Log.Error("Установка viiper.exe не удалась", ex);
            return new ComponentResult(component, false, Describe(ex));
        }
    }

    private static async Task<ComponentResult> InstallUsbIpAsync(
        string depsDir, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        const string component = "usbip-win2";
        try
        {
            // ВАЖНО: viiper (hbashton) требует СТРОГО usbip-win2 0.9.7.7 — при другой версии
            // он отказывается стартовать («Refusing to start VIIPER with an incompatible
            // USB/IP runtime»). Пропускаем установку ТОЛЬКО если стоит именно требуемая версия;
            // любую другую заменяем (удаляем + ставим 0.9.7.7).
            string? installed = DriverSetup.GetInstalledUsbipVersion();
            var vhci = DriverSetup.GetVhciDeviceInstanceIds();
            bool versionOk = installed == DriverSetup.RequiredUsbipVersion;
            bool vhciOk = vhci.Count <= 1;
            if (versionOk && vhciOk)
                return new ComponentResult(component, true,
                    $"уже установлена требуемая версия {DriverSetup.RequiredUsbipVersion} (vHci: {vhci.Count})");

            // Сценарий A: версия неверна — заменяем драйвер.
            if (!versionOk)
            {
                Append(progress, component, "замена", 5,
                    $"установлена {installed ?? "?"}, viiper требует {DriverSetup.RequiredUsbipVersion} — заменяю");

                // Тихо удаляем существующую версию штатным деинсталлятором (Inno Setup).
                Append(progress, component, "замена", 15, "удаление старой версии — подтвердите UAC");
                string? unins = FindUsbipUninstaller();
                if (unins is not null)
                    await Task.Run(() => DriverSetup.RunInstallerElevated(unins, "/VERYSILENT /NORESTART", ""));
                else
                    Log.Warn("Деинсталлятор usbip-win2 не найден — ставлю поверх (Inno заменит файлы)");
            }

            // Сценарий B (в т.ч. после замены): «ghost»-устройства vHci.
            // Если активных vHci больше одного, ЛЮБОЙ вызов usbip.exe падает с
            // «Multiple instances of vHci device interface found» (остатки старой
            // установки; подтверждено разбором проблемы). Удаляем ВСЕ vHci —
            // установщик/перезагрузка создаст единственный свежий.
            vhci = DriverSetup.GetVhciDeviceInstanceIds();
            if (vhci.Count > 0)
            {
                Append(progress, component, "очистка", 40,
                    $"найдено vHci-устройств: {vhci.Count} (лишние мешают usbip) — удаление, подтвердите UAC");
                await Task.Run(() => DriverSetup.RemoveVhciDevices(vhci));
            }

            string fileName = "usbip-0.9.7.7-x64.exe";
            string path = Path.Combine(depsDir, fileName);
            if (!File.Exists(path))
            {
                string? url = await FindAssetByTagAsync(
                    "vadimgrn/usbip-win2", "v.0.9.7.7",
                    n => n.Equals("USBip-0.9.7.7-x64.exe", StringComparison.OrdinalIgnoreCase),
                    UsbipFallbackUrlX64,
                    component, progress, ct);
                if (url is null)
                    return new ComponentResult(component, false,
                        $"не найден установщик usbip-win2 {DriverSetup.RequiredUsbipVersion} — скачайте вручную: {UsbipFallbackUrlX64}");
                path = await DownloadAsync(url, depsDir, fileName, component, progress, ct);
            }
            else
            {
                Append(progress, component, "скачивание", 100, "установщик уже скачан — используем существующий");
            }

            // Установщик Inno Setup: тихий режим, но Windows потребует подтвердить UAC.
            Append(progress, component, "установка", 90, "запуск установщика — подтвердите запрос UAC");
            bool ran = await Task.Run(() =>
                DriverSetup.RunInstallerElevated(path, "/VERYSILENT /NORESTART", "/SILENT /NORESTART", ""));

            if (DriverSetup.IsUsbIpInstalled())
            {
                string? ver = DriverSetup.GetInstalledUsbipVersion();
                int vhciNow = DriverSetup.CountVhciInterfaces();
                if (ver == DriverSetup.RequiredUsbipVersion && vhciNow <= 1)
                    return new ComponentResult(component, true,
                        $"установлена требуемая версия {ver}, vHci: {vhciNow}. ОБЯЗАТЕЛЬНО ПЕРЕЗАГРУЗИТЕ ПК — без этого драйвер в ядре остаётся старым");
                if (ver != DriverSetup.RequiredUsbipVersion)
                    return new ComponentResult(component, false,
                        $"после установки обнаружена версия {ver} вместо {DriverSetup.RequiredUsbipVersion} — перезагрузите ПК и повторите");
                return new ComponentResult(component, false,
                    $"версия {ver} ок, но активных vHci по-прежнему {vhciNow} — перезагрузите ПК и повторите установку");
            }
            return new ComponentResult(component, false,
                ran
                    ? "установщик завершился, но сервис usbip2_ude не обнаружен — вероятно, нужна перезагрузка"
                    : "установщик не завершился успешно (отменён UAC?) — попробуйте ещё раз или установите вручную");
        }
        catch (OperationCanceledException)
        {
            return new ComponentResult(component, false, "отменено пользователем");
        }
        catch (Exception ex)
        {
            Log.Error("Установка usbip-win2 не удалась", ex);
            return new ComponentResult(component, false, Describe(ex));
        }
    }

    private static async Task<ComponentResult> InstallHidHideAsync(
        string depsDir, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        const string component = "HidHide";
        try
        {
            if (DriverSetup.GetHidHideStatus())
                return new ComponentResult(component, true, "уже установлен");

            string fileName = "HidHide-setup-x64.exe";
            string path = Path.Combine(depsDir, fileName);
            if (!File.Exists(path))
            {
                string? url = await FindLatestAssetUrlAsync(
                    "nefarius/HidHide",
                    n => n.StartsWith("HidHide_", StringComparison.OrdinalIgnoreCase) &&
                         n.EndsWith("_x64.exe", StringComparison.OrdinalIgnoreCase),
                    HidHideFallbackUrl, component, progress, ct);
                if (url is null)
                    return new ComponentResult(component, false,
                        "в последнем релизе nefarius/HidHide нет установщика x64 — скачайте вручную (см. README)");
                path = await DownloadAsync(url, depsDir, fileName, component, progress, ct);
            }
            else
            {
                Append(progress, component, "скачивание", 100, "установщик уже скачан — используем существующий");
            }

            // Установщик MSI-стиля: тихий режим /qn /norestart, подтверждение UAC.
            Append(progress, component, "установка", 90, "запуск установщика — подтвердите запрос UAC");
            bool ran = await Task.Run(() =>
                DriverSetup.RunInstallerElevated(path, "/qn /norestart", "/passive", ""));

            return DriverSetup.GetHidHideStatus()
                ? new ComponentResult(component, true, "установлен")
                : new ComponentResult(component, false,
                    ran
                        ? "установщик завершился, но HidHide не обнаружен — проверьте вручную"
                        : "установщик не завершился успешно (отменён UAC?) — HidHide можно пропустить, это опция");
        }
        catch (OperationCanceledException)
        {
            return new ComponentResult(component, false, "отменено пользователем");
        }
        catch (Exception ex)
        {
            Log.Error("Установка HidHide не удалась", ex);
            return new ComponentResult(component, false, Describe(ex));
        }
    }

    // ------------------------------------------------------------------ Скачивание

    /// <summary>Ищет URL файла в последнем релизе через GitHub API; при сбое — резервная ссылка.</summary>
    private static Task<string?> FindLatestAssetUrlAsync(
        string repo, Func<string, bool> assetMatch, string fallbackUrl,
        string component, IProgress<InstallProgress> progress, CancellationToken ct)
        => FindAssetByTagAsync(repo, null, assetMatch, fallbackUrl, component, progress, ct);

    /// <summary>Ищет URL файла в релизе с заданным тегом (null = последний); при сбое — резервная ссылка.</summary>
    private static async Task<string?> FindAssetByTagAsync(
        string repo, string? tag, Func<string, bool> assetMatch, string fallbackUrl,
        string component, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        try
        {
            string releasesPath = tag is null ? "releases/latest" : $"releases/tags/{tag}";
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/{releasesPath}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is not null && assetMatch(name) &&
                    asset.TryGetProperty("browser_download_url", out var u) &&
                    u.GetString() is { Length: > 0 } url)
                    return url;
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"GitHub API недоступен ({repo}): {ex.Message} — используется проверенная резервная ссылка");
            Append(progress, component, "поиск", 0, "GitHub API недоступен — используется проверенная версия");
            return fallbackUrl;
        }
    }

    /// <summary>Путь к штатному деинсталлятору usbip-win2 (unins000.exe), если есть.</summary>
    private static string? FindUsbipUninstaller()
    {
        try
        {
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string dir = Path.Combine(Environment.GetFolderPath(root), "USBip");
                var unins = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "unins*.exe").FirstOrDefault()
                    : null;
                if (unins is not null) return unins;
            }
        }
        catch { }
        return null;
    }

    private static async Task<string> DownloadAsync(
        string url, string destDir, string fileName, string component,
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        string path = Path.Combine(destDir, fileName);
        Append(progress, component, "скачивание", 0, url);

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buf = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
                Append(progress, component, "скачивание", (int)(read * 100 / total),
                    $"{read / 1048576.0:F1} / {total / 1048576.0:F1} МБ");
        }
        return path;
    }


    /// <summary>Человекопонятное описание сбоя (сетевые ошибки — с подсказкой).</summary>
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => $"сеть/скачивание не удалось — проверьте подключение к интернету ({ex.Message})",
        TaskCanceledException => "превышено время ожидания сети (15 мин) — проверьте интернет и повторите",
        UriFormatException => $"некорректная ссылка загрузки ({ex.Message})",
        _ => ex.Message,
    };

    private static void Append(IProgress<InstallProgress> p, string component, string stage, int percent, string detail) =>
        p.Report(new InstallProgress { Component = component, Stage = stage, Percent = percent, Detail = detail });
}

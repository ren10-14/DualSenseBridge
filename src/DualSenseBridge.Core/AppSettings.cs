using System.Text.Json;
using System.Text.Json.Serialization;
using DualSenseBridge.Core.Translation;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core;

/// <summary>Настройки приложения (сохраняются в %APPDATA%\DualSenseBridge\settings.json).</summary>
public sealed class AppSettings
{
    /// <summary>Диалект VIIPER: hbashtonV5 (триггеры, рекомендуется) или upstream.</summary>
    public ViiperDialect Dialect { get; set; } = ViiperDialect.HbashtonV5;

    /// <summary>Режим трансляции обратной связи.</summary>
    public TranslationMode Translation { get; set; } = TranslationMode.Native;

    /// <summary>Минимальный интервал записи в контроллер, мс (по умолчанию 4 = период опроса BT).</summary>
    public int MinWriteIntervalMs { get; set; } = 4;

    /// <summary>Масштаб интенсивности вибрации 0..1.</summary>
    public double RumbleScale { get; set; } = 1.0;

    /// <summary>Скрывать физический контроллер через HidHide при запуске моста.</summary>
    public bool HidePhysicalDevice { get; set; } = true;

    /// <summary>Принудительно зажигать LED «игрок 1» (слот виртуального контроллера может быть вторым).</summary>
    public bool ForcePlayerOneLed { get; set; } = true;

    /// <summary>
    /// Полный дуплекс: виртуальный DualSense с аудио-интерфейсами — игры с
    /// PCM-хаптиками (TLOU и др. PS-порты) получают полноценный путь до контроллера.
    /// </summary>
    public bool FullDuplexAudio { get; set; } = true;

    /// <summary>Усиление хаптик PCM (аудиоканал), 0.25..4.0.</summary>
    public double HapticsGain { get; set; } = 1.0;

    /// <summary>Запускать свернутым в трей.</summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>Путь к viiper.exe (если не задан — поиск в стандартных местах).</summary>
    public string? ViiperPath { get; set; }

    /// <summary>Цвет подсветки по умолчанию (после тестов).</summary>
    public string IdleLightbarColor { get; set; } = "#000040";

    /// <summary>
    /// Подсветка по умолчанию (v1.9.9): зажигать полосу вокруг тачпада, пока
    /// игра не управляет ею (мягкий синий, как у PS5).
    /// </summary>
    public bool DefaultLightbar { get; set; } = true;

    /// <summary>Цвет подсветки по умолчанию «#RRGGBB» (v1.9.9).</summary>
    public string DefaultLightbarColor { get; set; } = "#0040FF";

    /// <summary>Тема интерфейса: light или dark (v1.13.0).</summary>
    public string Theme { get; set; } = "light";

    /// <summary>Язык интерфейса: ru или en (v1.13.0).</summary>
    public string Language { get; set; } = "ru";

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DualSenseBridge");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать настройки: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось сохранить настройки: {ex.Message}");
        }
    }
}

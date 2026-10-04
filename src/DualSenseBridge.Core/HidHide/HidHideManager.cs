using DualSenseBridge.Core.Hid;
using DualSenseBridge.Core.Protocol;
using Nefarius.Drivers.HidHide;

namespace DualSenseBridge.Core.HidHide;

/// <summary>
/// Управление драйвером HidHide (MIT, nefarius): скрывает физический DualSense
/// от игр и других приложений, чтобы исключить «двойной ввод», при этом
/// DualSenseBridge добавляет сам себя в список разрешённых приложений и
/// продолжает видеть контроллер.
///
/// Семантика HidHide: список приложений по умолчанию работает как «блокировать
/// всех, кроме перечисленных»; устройства из BlockedInstanceIds невидимы
/// для всех, кроме разрешённых приложений.
///
/// ВАЖНО (документация HidHide API): control-устройство драйвера допускает
/// только ОДНО подключение одновременно. Все обращения сериализуются через
/// статическую блокировку, а весь код приложения использует один общий
/// экземпляр (Shared) — иначе параллельные операции (статус-таймер + добавление
/// себя в белый список) получают «Access Denied» и попытка молча проваливается
/// (v1.9.4: из-за этого программа могла «ослепнуть» — не увидеть физический
/// контроллер на всю сессию, если HidHide остался активным, а exe сменил имя).
/// </summary>
public sealed class HidHideManager
{
    /// <summary>Единственный экземпляр для всего приложения.</summary>
    public static readonly HidHideManager Shared = new();

    /// <summary>Сериализация всех обращений к control-устройству HidHide.</summary>
    private static readonly object GlobalLock = new();

    private readonly IHidHideControlService _api;

    private HidHideManager()
    {
        _api = new HidHideControlService();
    }

    public bool IsInstalled
    {
        get
        {
            try { lock (GlobalLock) return _api.IsInstalled; }
            catch { return false; }
        }
    }

    public bool IsActive
    {
        get
        {
            try { lock (GlobalLock) return _api.IsActive; }
            catch { return false; }
        }
    }

    /// <summary>Скрыть физический DualSense: разрешаем себе, блокируем устройства, активируем.</summary>
    public void HideDevices(IEnumerable<string> instanceIds, string? selfPath = null)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("HidHide не установлен");

        lock (GlobalLock)
        {
            // 1. Разрешаем текущий процесс (иначе скроем контроллер от самих себя).
            selfPath ??= Environment.ProcessPath;
            if (selfPath is not null && !_api.ApplicationPaths.Contains(selfPath, StringComparer.OrdinalIgnoreCase))
            {
                _api.AddApplicationPath(selfPath);
                Log.Info($"HidHide: процесс {Path.GetFileName(selfPath)} добавлен в список разрешённых");
            }

            // 2. Блокируем все коллекции HID-устройства (COL01, COL02, ...).
            foreach (var id in instanceIds)
            {
                var target = Normalize(id);
                if (!_api.BlockedInstanceIds.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    _api.AddBlockedInstanceId(target);
                    Log.Info($"HidHide: устройство скрыто: {target}");
                }
            }

            // 3. Активируем глобально.
            _api.IsActive = true;
        }
    }

    /// <summary>
    /// Гарантирует, что текущий процесс есть в списке разрешённых приложений HidHide
    /// (выполняется при каждом запуске, пока HidHide установлен — активен он или нет:
    /// скрыть могут и позже). ВЫЗЫВАТЬ ДО ПОДКЛЮЧЕНИЯ ФИЗИЧЕСКОГО КОНТРОЛЛЕРА:
    /// HidHide переживает перезапуск приложения, а список разрешённых хранит ПУТЬ exe —
    /// при переименовании (DualSenseBridge-v1.9.1.exe → …-v1.9.5.exe) новый exe
    /// без этого НЕ ВИДИТ контроллер (v1.9.2–1.9.4: из-за этого мост оставался
    /// без физического контроллера, пока HidHide был активен).
    /// Возвращает true, если допуск гарантирован.
    /// </summary>
    public bool EnsureSelfWhitelisted(string? selfPath = null)
    {
        selfPath ??= Environment.ProcessPath;
        if (selfPath is null) return true; // путей нет — и прятать не от кого
        try
        {
            if (!IsInstalled) return true;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    lock (GlobalLock)
                    {
                        if (_api.ApplicationPaths.Contains(selfPath, StringComparer.OrdinalIgnoreCase))
                            return true; // уже в списке
                        _api.AddApplicationPath(selfPath);
                        // Проверяем перечитыванием: операция могла «молча» не примениться.
                        if (_api.ApplicationPaths.Contains(selfPath, StringComparer.OrdinalIgnoreCase))
                        {
                            Log.Info($"HidHide: {Path.GetFileName(selfPath)} добавлен в список разрешённых " +
                                     "(HidHide переживает перезапуск, а exe сменил имя/путь)");
                            return true;
                        }
                    }
                }
                catch when (attempt <= 3)
                {
                    // Control-устройство HidHide допускает только ОДНО подключение
                    // одновременно: чужой клиент (или наше же параллельное обращение)
                    // мог занять его — ждём и пробуем снова.
                    Thread.Sleep(300 * attempt);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"HidHide: НЕ УДАЛОСЬ добавить себя в список разрешённых ({ex.Message}). " +
                      "Если контроллер не виден — он скрыт этим драйвером от текущей копии программы. " +
                      "Закройте клиент HidHide и перезапустите программу, либо добавьте " +
                      $"{Path.GetFileName(selfPath)} в список разрешённых приложений в клиенте HidHide.", ex);
            return false;
        }
    }

    /// <summary>Показать физический DualSense обратно (убрать из списка блокировки).</summary>
    public void UnhideDevices(IEnumerable<string> instanceIds)
    {
        if (!IsInstalled) return;
        lock (GlobalLock)
        {
            foreach (var id in instanceIds)
            {
                var target = Normalize(id);
                if (_api.BlockedInstanceIds.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    _api.RemoveBlockedInstanceId(target);
                    Log.Info($"HidHide: устройство снова видно: {target}");
                }
            }
        }
    }

    /// <summary>
    /// СПАСАТЕЛЬНЫЙ ТРОС (v1.9.5): DualSense-устройства в СПИСКЕ БЛОКИРОВКИ самого
    /// драйвера. Используется, когда перечисление ничего не видит (HidHide прячет
    /// контроллер от нас) — показать («unhide») можно только по списку драйвера.
    /// </summary>
    public List<string> GetBlockedDualSenseIds()
    {
        var result = new List<string>();
        if (!IsInstalled) return result;
        try
        {
            lock (GlobalLock)
            {
                foreach (var id in _api.BlockedInstanceIds)
                    if (IsDualSenseInstanceId(id))
                        result.Add(id);
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Чистая проверка: instance ID выглядит как DualSense/DualSense Edge
    /// (содержит VID_054C&PID_0CE6 или PID_0DF2 в любом регистре).
    /// </summary>
    public static bool IsDualSenseInstanceId(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return false;
        string s = instanceId.ToUpperInvariant();
        return s.Contains("VID_054C&PID_0CE6") || s.Contains("VID_054C&PID_0DF2");
    }

    /// <summary>Полностью очистить список скрытых устройств (кнопка «показать всё»).</summary>
    public void ClearHidden()
    {
        if (!IsInstalled) return;
        lock (GlobalLock) _api.ClearBlockedInstancesList();
        Log.Info("HidHide: список скрытых устройств очищен");
    }

    /// <summary>Скрытые в данный момент устройства.</summary>
    public IReadOnlyList<string> HiddenDevices
    {
        get
        {
            try { lock (GlobalLock) return IsInstalled ? _api.BlockedInstanceIds : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }
    }

    /// <summary>Разрешённые (белый список) приложения (для диагностики).</summary>
    public IReadOnlyList<string> GetApplicationPaths()
    {
        try { lock (GlobalLock) return IsInstalled ? _api.ApplicationPaths : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string Normalize(string instanceId) => instanceId.TrimStart('\\', '?').ToUpperInvariant();

    /// <summary>Собирает Instance ID всех HID-коллекций всех подключённых DualSense.</summary>
    public static List<string> CollectDualSenseInstanceIds()
    {
        var ids = new List<string>();
        foreach (var info in DualSenseHidDevice.Enumerate())
            ids.Add(info.InstanceId);
        return ids;
    }
}

using HidSharp;

namespace DualSenseBridge.Core.Hid;

/// <summary>
/// Мониторинг подключения/отключения физических DualSense — событие HidSharp
/// DeviceList.Changed, как в v1.9.1 (полный откат v1.9.8).
/// Страховка: даже если событие не сработает, InputLoop моста переподключает
/// контроллер автоматически (не реже раза в 5 с).
/// </summary>
public sealed class HidDeviceMonitor : IDisposable
{
    private readonly Action _callback;
    private bool _disposed;

    public HidDeviceMonitor(Action changedCallback)
    {
        _callback = changedCallback;
        DeviceList.Local.Changed += OnDeviceListChanged;
    }

    private void OnDeviceListChanged(object? sender, EventArgs e)
    {
        try { _callback(); }
        catch { /* обратный вызов не должен рвать нить событий */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeviceList.Local.Changed -= OnDeviceListChanged;
    }
}

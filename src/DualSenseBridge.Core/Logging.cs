namespace DualSenseBridge.Core;

/// <summary>
/// Простой потокобезопасный журнал: файл + консоль + подписчики (UI).
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _filePath;
    private static bool _verbose;
    private static StreamWriter? _writer;

    public static event Action<string>? LineAppended;

    public static void Init(string? filePath, bool verbose)
    {
        lock (Gate)
        {
            _verbose = verbose;
            if (filePath is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
                _writer = new StreamWriter(filePath, append: true) { AutoFlush = true };
                _filePath = filePath;
                _writer.WriteLine();
                _writer.WriteLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} DualSenseBridge =====");
            }
            catch
            {
                _writer = null;
            }
        }
    }

    public static string? FilePath => _filePath;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Debug(string message)
    {
        if (_verbose) Write("DEBUG", message);
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            _writer?.WriteLine(line);
        }
        LineAppended?.Invoke(line);
    }
}

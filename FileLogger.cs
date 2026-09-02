namespace VaultLatch;

internal sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly string _logPath;

    public FileLogger()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VaultLatch");
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, "VaultLatch.log");
    }

    public string LogPath => _logPath;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}: {exception}");
    }

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
        }
    }

    public void Dispose()
    {
    }
}

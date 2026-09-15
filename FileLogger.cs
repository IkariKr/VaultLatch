namespace VaultLatch;

internal sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly string? _logPath;

    public FileLogger()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.GetTempPath();
            }

            var directory = Path.Combine(root, "VaultLatch");
            Directory.CreateDirectory(directory);
            _logPath = Path.Combine(directory, "VaultLatch.log");
        }
        catch
        {
            _logPath = null;
        }
    }

    public string LogPath => _logPath ?? string.Empty;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}: {exception}");
    }

    private void Write(string level, string message)
    {
        if (_logPath is null)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never propagate a removable-disk or file-lock failure.
        }
    }

    public void Dispose()
    {
    }
}

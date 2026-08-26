using System.Globalization;
using System.Text;

namespace ResourceGrab.Core.Logging;

/// <summary>
/// 滚动文件日志：按天一个文件（app-yyyyMMdd.log），追加写入，线程安全，
/// 启动时清理超过保留天数的旧文件。注意：日志内容不得包含密码 / API Key 等敏感信息。
/// </summary>
public sealed class FileLogger : ILogger, IDisposable
{
    private const string FileNamePrefix = "app-";
    private const string FileNameSuffix = ".log";

    private readonly object _lock = new();
    private readonly string _logDirectory;
    private readonly int _keepDays;
    private readonly Func<DateTime> _now;
    private StreamWriter? _writer;
    private string _currentDateKey = "";

    public FileLogger(string logDirectory, int keepDays = 7, Func<DateTime>? now = null)
    {
        _logDirectory = logDirectory;
        _keepDays = Math.Max(1, keepDays);
        _now = now ?? (() => DateTime.Now);
        Directory.CreateDirectory(_logDirectory);
        CleanupOldFiles();
    }

    public string LogDirectory => _logDirectory;

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        lock (_lock)
        {
            try
            {
                var dateKey = _now().ToString("yyyyMMdd");
                if (_writer is null || !string.Equals(dateKey, _currentDateKey, StringComparison.Ordinal))
                {
                    RollFile(dateKey);
                }

                _writer!.WriteLine($"{_now():yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
                if (exception is not null)
                {
                    _writer.WriteLine(exception);
                }
                _writer.Flush();
            }
            catch
            {
                // 日志失败不应影响业务逻辑
            }
        }
    }

    private void RollFile(string dateKey)
    {
        _writer?.Flush();
        _writer?.Dispose();
        _currentDateKey = dateKey;
        _writer = new StreamWriter(
            new FileStream(Path.Combine(_logDirectory, $"{FileNamePrefix}{dateKey}{FileNameSuffix}"),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
    }

    private void CleanupOldFiles()
    {
        try
        {
            var cutoff = _now().Date.AddDays(-_keepDays);
            foreach (var file in Directory.EnumerateFiles(_logDirectory, $"{FileNamePrefix}*{FileNameSuffix}"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(FileNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                var dateText = name[FileNamePrefix.Length..];
                if (DateTime.TryParseExact(dateText, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) && date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败忽略，不影响日志写入
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}


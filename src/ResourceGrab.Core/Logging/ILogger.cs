namespace ResourceGrab.Core.Logging;

/// <summary>应用日志抽象（与具体实现解耦，便于测试与替换）。</summary>
public interface ILogger
{
    /// <summary>日志目录（已创建）。</summary>
    string LogDirectory { get; }

    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

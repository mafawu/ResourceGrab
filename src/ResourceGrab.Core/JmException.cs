namespace ResourceGrab.Core;

/// <summary>禁漫 API 调用相关的业务异常。</summary>
public class JmException : Exception
{
    public JmException(string message) : base(message) { }
    public JmException(string message, Exception inner) : base(message, inner) { }
}

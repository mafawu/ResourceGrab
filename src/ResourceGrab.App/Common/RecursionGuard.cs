using System.Diagnostics;

namespace ResourceGrab.App.Common;

/// <summary>
/// 临时诊断工具：用于定位 StackOverflowException（无限递归）。
/// 真正的 StackOverflowException 不可被任何 catch 捕获、也不会写日志，进程直接终止，
/// 因此这里在疑似递归的「同步」调用入口包裹 using (RecursionGuard.Enter(tag))，
/// 当同一线程的调用深度超过阈值时抛出「可捕获」的 InvalidOperationException 并附带完整调用栈，
/// 从而把真实递归栈暴露到日志里，并提前终止递归、避免进程崩溃。
/// 仅用于排查，定位后可移除。
/// </summary>
public sealed class RecursionGuard : IDisposable
{
    private const int Limit = 40;
    [ThreadStatic] private static int _depth;
    private readonly string _tag;

    private RecursionGuard(string tag) => _tag = tag;

    public static RecursionGuard Enter(string tag)
    {
        _depth++;
        if (_depth > Limit)
        {
            var at = _depth;
            var stack = new StackTrace(true).ToString();
            // 把计数复位为 Limit，使异常展开（Limit 次 using 析构）后归零，避免线程复用导致计数漂移。
            _depth = Limit;
            throw new InvalidOperationException(
                $"[RecursionGuard] 检测到疑似无限递归（同线程深度 {at}）于 {tag}。\n{stack}");
        }

        return new RecursionGuard(tag);
    }

    public void Dispose() => _depth--;
}

using System.Net;
using System.Net.Sockets;

namespace ResourceGrab.Core.Errors;

/// <summary>错误类别：把底层异常映射为面向用户的类别。</summary>
public enum JmErrorType
{
    /// <summary>本子/章节不存在。</summary>
    NotFound,

    /// <summary>网络类失败（超时/连接/SSL/域名失效等）。</summary>
    Network,

    /// <summary>登录/认证失败。</summary>
    Auth,

    /// <summary>请求被拒绝（如 403 风控）。</summary>
    Permission,

    /// <summary>下载不完整（部分图片/章节失败）。</summary>
    DownloadFailed,

    /// <summary>无法归类的其他错误（保留原始消息）。</summary>
    Unknown,
}

/// <summary>分类结果：类别 + 用户友好提示 + 原始详情。</summary>
public sealed class JmErrorInfo
{
    public JmErrorType Type { get; }

    /// <summary>面向用户的友好提示（Unknown 时为原始消息）。</summary>
    public string Message { get; }

    /// <summary>原始异常消息（含内部异常，便于排查）。</summary>
    public string Detail { get; }

    internal JmErrorInfo(JmErrorType type, string message, string detail)
    {
        Type = type;
        Message = message;
        Detail = detail;
    }
}

/// <summary>
/// 统一错误分类器：把底层异常映射为用户可读、可操作的提示。
/// 参考 astrbot_plugin_jm_cosmos 的 core/errors.py 设计——
/// 先按异常类型精确归类，再用消息关键词兜底。
/// </summary>
public static class JmErrorClassifier
{
    // 网络类关键词（无法精确归类时的兜底判断）
    private static readonly string[] NetworkKeywords =
    {
        "timeout", "timed out", "超时",
        "connect", "connection", "无法连接", "拒绝连接", "refused",
        "network", "网络",
        "ssl", "tls",
        "proxy", "代理",
        "max retries", "重试",
        "dns", "解析",
        "unreachable", "reset",
    };

    private static readonly string[] NotFoundKeywords =
    {
        "404", "not found", "未找到", "没有找到", "不存在",
    };

    private static readonly string[] AuthKeywords =
    {
        "401", "unauthorized", "未授权", "登录失败", "账号", "密码", "login",
    };

    private static readonly string[] PermissionKeywords =
    {
        "403", "forbidden", "禁止访问", "被禁止",
    };

    private static readonly string[] DownloadFailedKeywords =
    {
        "只下载了", "部分图片", "下载图片",
    };

    /// <summary>分类异常（遍历整个异常链，类型优先、关键词兜底）。</summary>
    public static JmErrorInfo Classify(Exception? ex)
    {
        if (ex is null)
        {
            return Unknown("发生未知错误", "");
        }

        var detail = BuildDetail(ex);
        var chain = new List<Exception>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            chain.Add(current);
        }

        // 1. 基于异常类型的精确分类（网络错误通常被包装在 HttpRequestException 链中）
        foreach (var current in chain)
        {
            if (current is HttpRequestException httpEx)
            {
                if (httpEx.StatusCode == HttpStatusCode.NotFound)
                {
                    return NotFound(detail);
                }
                if (httpEx.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return Auth(detail);
                }
                if (httpEx.StatusCode == HttpStatusCode.Forbidden)
                {
                    return Permission(detail);
                }
                return Network(detail, httpEx.StatusCode);
            }

            if (current is SocketException)
            {
                return Network(detail, null);
            }

            if (current is TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                // HttpClient 超时表现为未取消的 TaskCanceledException
                return new JmErrorInfo(
                    JmErrorType.Network, "请求超时，可能是网络或域名问题，请稍后重试", detail);
            }
        }

        // 2. 消息关键词兜底
        return Classify(detail);
    }

    /// <summary>分类已有消息文本（下载引擎等不经过异常的场景）。</summary>
    public static JmErrorInfo Classify(string message)
    {
        var text = message ?? "";
        if (ContainsAny(text, NotFoundKeywords))
        {
            return NotFound(text);
        }
        if (ContainsAny(text, AuthKeywords))
        {
            return Auth(text);
        }
        if (ContainsAny(text, PermissionKeywords))
        {
            return Permission(text);
        }
        if (ContainsAny(text, NetworkKeywords))
        {
            return Network(text, null);
        }
        if (ContainsAny(text, DownloadFailedKeywords))
        {
            return new JmErrorInfo(
                JmErrorType.DownloadFailed, "部分图片下载失败，请重试对应章节", text);
        }
        return Unknown(text, text);
    }

    /// <summary>仅取用户友好提示（UI 快捷入口）。</summary>
    public static string Message(Exception ex) => Classify(ex).Message;

    private static JmErrorInfo NotFound(string detail) => new(
        JmErrorType.NotFound, "未找到该本子或章节，请检查 ID 是否正确", detail);

    private static JmErrorInfo Auth(string detail) => new(
        JmErrorType.Auth, "登录失败，请检查账号和密码是否正确", detail);

    private static JmErrorInfo Permission(string detail) => new(
        JmErrorType.Permission, "请求被拒绝（可能因 IP 风控被禁止访问），请稍后重试或更换网络", detail);

    private static JmErrorInfo Network(string detail, HttpStatusCode? statusCode)
    {
        var hint = statusCode is null
            ? "网络连接失败，已自动尝试其他可用接口域名；可稍后重试或在设置中更换域名列表"
            : $"网络请求失败（状态码 {(int)statusCode}），已自动尝试其他可用接口域名；可稍后重试或在设置中更换域名列表";
        return new JmErrorInfo(JmErrorType.Network, hint, detail);
    }

    private static JmErrorInfo Unknown(string message, string detail)
        => new(JmErrorType.Unknown, message, detail);

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>拼接完整消息（含内部异常），作为 Detail 保留原始信息。</summary>
    private static string BuildDetail(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }
        return string.Join(" -> ", parts);
    }
}

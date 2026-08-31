using System.Net;

namespace ResourceGrab.Core.Services.VideoScrape.Fetching;

// ---------------------------------------------------------------------------
// 阶段0: 拦截页识别 — 从 MissAvSource.IsCloudflareBlock 提升为公共组件
// 供多级抓取栈（MultiTierFetcher）与各刮削源统一判定"被反爬拦截"。
// 命中后应返回 VideoSourceOutcome.Blocked，让上层降级/换镜像，而不是把
// 挑战页当正常页面解析出 0 条结果。
// ---------------------------------------------------------------------------

public static class BlockDetector
{
    /// <summary>综合状态码与响应体特征判定是否被站点防护（Cloudflare 等）拦截。</summary>
    public static bool IsBlocked(int? statusCode, string? body)
    {
        if (statusCode is 403 or 429 or 503)
            return true;

        if (string.IsNullOrEmpty(body) || body.Length < 100)
            return false;

        // 挑战页 body 特征（只看头部即可，整页扫描代价高且无必要）
        var head = body.AsSpan(0, Math.Min(body.Length, 3000));
        return head.Contains("just a moment", StringComparison.OrdinalIgnoreCase)
            || head.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
            || head.Contains("cf_chl_", StringComparison.OrdinalIgnoreCase)
            || head.Contains("attention required", StringComparison.OrdinalIgnoreCase);
    }
}

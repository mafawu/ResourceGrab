using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Services.ShortDrama;
using ResourceGrab.Core.Sources.ShortDrama;

namespace ResourceGrab.App.ViewModels;

/// <summary>
/// 短剧卡片适配器：把在线摘要（<see cref="ShortDramaSummary"/>）或本地已下载剧集
/// 统一成 <see cref="MediaCardViewModel"/>，由 <c>MediaCard</c> 用 DramaCover 模板渲染。
/// <para>
/// 角标与「本地」互斥：已下载的剧不再显示"新上/热播"，因为用户此刻关心的是"我有没有存下来"。
/// </para>
/// </summary>
public sealed class ShortDramaCardAdapter : MediaCardViewModel
{
    /// <summary>在线卡片。</summary>
    public ShortDramaCardAdapter(
        ShortDramaSummary summary,
        Action<ShortDramaSummary>? open = null,
        Action<ShortDramaSummary>? play = null,
        Action<ShortDramaSummary>? download = null)
    {
        Summary = summary;
        CardId = summary.Id;
        Title = summary.Title;
        CoverUrl = "";
        ScoreText = summary.ScoreText;
        BadgeText = NormalizeBadge(summary.Badge);
        EpisodeText = summary.EpisodeText;
        Subtitle = BuildSubtitle(summary.ChannelName, summary.Tags);
        IsLocal = false;
        CoverPlaceholderBrush = DramaCoverPlaceholder.Build(summary.Id + summary.Title);
        CoverFallbackText = DramaCoverPlaceholder.FirstChar(summary.Title);

        OpenCommand = new RelayCommand(_ => open?.Invoke(summary));
        PrimaryCommand = new RelayCommand(_ => (play ?? open)?.Invoke(summary));
        DownloadCommand = download is null ? null : new RelayCommand(_ => download(summary));
    }

    /// <summary>本地卡片（离线库）：封面走本地文件，角标显示"本地"。</summary>
    public ShortDramaCardAdapter(LocalDrama local, Action<LocalDrama>? open = null)
    {
        Local = local;
        var meta = local.Metadata;
        CardId = meta.Id;
        Title = meta.Title;
        CoverUrl = local.CoverPath ?? "";
        ScoreText = meta.ScoreText;
        BadgeText = "";
        EpisodeText = $"{local.DownloadedCount} / {Math.Max(local.TotalEpisodes, local.DownloadedCount)} 集";
        Subtitle = BuildLocalSubtitle(local);
        IsLocal = true;
        CoverPlaceholderBrush = DramaCoverPlaceholder.Build(meta.Id + meta.Title);
        CoverFallbackText = DramaCoverPlaceholder.FirstChar(meta.Title);

        OpenCommand = new RelayCommand(_ => open?.Invoke(local));
        PrimaryCommand = open is null ? null : new RelayCommand(_ => open(local));
    }

    /// <summary>封面解密完成后由页面回填（触发一次属性通知，图片层就能显示）。</summary>
    public void SetCoverUrl(string? path)
    {
        var next = path ?? "";
        if (next == CoverUrl) return;
        CoverUrl = next;
        OnPropertyChanged(nameof(CoverUrl));
    }

    public ShortDramaSummary? Summary { get; }

    public LocalDrama? Local { get; }

    public override string CardId { get; }

    public override string Title { get; }

    public override MediaVisualKind VisualKind => MediaVisualKind.DramaCover;

    /// <summary>
    /// 封面：**本地解密后的缓存文件路径**（优先），否则空串走渐变兜底。
    /// 站点的封面 URL 拿到的是密文，直接绑上去只会解码失败，所以这里不放远程地址。
    /// </summary>
    public string CoverUrl { get; private set; }

    /// <summary>右上角评分（"8.4"）；无评分时为空，模板据此隐藏角标。</summary>
    public string ScoreText { get; }

    /// <summary>左上角标（"新上"/"热播"/"高分"/"原创"）。</summary>
    public string BadgeText { get; }

    /// <summary>是否显示普通角标（本地徽章优先，二者互斥）。</summary>
    public bool ShowBadge => !IsLocal && BadgeText.Length > 0;

    /// <summary>是否为本地已下载剧集。</summary>
    public bool IsLocal { get; }

    /// <summary>右下角集数（在线为"更新至6集"，本地为"4 / 6 集"）。</summary>
    public string EpisodeText { get; }

    /// <summary>
    /// 封面兜底底色。站点封面 CDN（pic.drleao.cn）目前返回的是加密字节（ETag 与响应体 md5 一致，
    /// 说明缓存里存的就不是图片），WPF 解不出来 → 图片层会保持空白。
    /// 这里按剧集 id 生成一个确定性的渐变色，让封面墙仍然是"一面墙"而不是一排空盒子。
    /// 图源修好后，图片层会自然盖上去，不需要改这里。
    /// </summary>
    public Brush CoverPlaceholderBrush { get; }

    /// <summary>兜底时显示的首字（重写基类的同名属性，供卡片模板绑定）。</summary>
    public override string? CoverFallbackText { get; set; }

    /// <summary>站点给的角标里，「高分新上」这类复合词压成短标签，避免撑破 20px 高的胶囊。</summary>
    private static string NormalizeBadge(string raw)
    {
        var b = (raw ?? "").Trim();
        if (b.Length == 0) return "";
        if (b.Contains("高分", StringComparison.Ordinal)) return "高分";
        if (b.Contains("热播", StringComparison.Ordinal) || b.Contains("热门", StringComparison.Ordinal)) return "热播";
        if (b.Contains("原创", StringComparison.Ordinal)) return "原创";
        if (b.Contains("新", StringComparison.Ordinal)) return "新上";
        return b.Length > 4 ? b[..4] : b;
    }

    private static string BuildSubtitle(string channelName, IReadOnlyList<string> tags)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(channelName)) parts.Add(channelName);
        if (tags.Count > 0 && (parts.Count == 0 || tags[0] != parts[0])) parts.Add(tags[0]);
        return string.Join(" · ", parts);
    }

    private static string BuildLocalSubtitle(LocalDrama local)    {
        var size = local.TotalBytes;
        var sizeText = size switch
        {
            <= 0 => "0 MB",
            < 1024L * 1024 * 1024 => $"{size / 1024.0 / 1024.0:F0} MB",
            _ => $"{size / 1024.0 / 1024.0 / 1024.0:F2} GB",
        };
        var when = local.LastDownloadedAt.LocalDateTime;
        var ago = DateTime.Now - when;
        var agoText = ago.TotalDays switch
        {
            >= 30 => $"{ago.TotalDays / 30:F0} 个月前下载",
            >= 7 => $"{ago.TotalDays / 7:F0} 周前下载",
            >= 1 => $"{ago.TotalDays:F0} 天前下载",
            _ => $"{Math.Max(1, ago.TotalHours):F0} 小时前下载",
        };
        return $"{sizeText} · {agoText}";
    }
}

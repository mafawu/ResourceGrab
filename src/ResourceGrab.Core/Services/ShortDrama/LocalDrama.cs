using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Services.ShortDrama;

/// <summary>
/// 本地短剧元数据（落盘为 <c>drama.json</c>）。**本地库的唯一真相来源**：
/// 离线可读、可迁移、删掉整个文件夹即"从库中移除"，不需要任何额外索引文件。
/// </summary>
public sealed class DramaMetadata
{
    /// <summary>schema 版本，便于日后迁移。</summary>
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;

    /// <summary>站点侧剧集 id。</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>封面文件名（相对本目录）。封面在下载时抓一次，规避签名地址过期。</summary>
    [JsonPropertyName("coverFile")] public string CoverFile { get; set; } = "poster.jpg";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("scoreText")] public string ScoreText { get; set; } = "";

    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];

    [JsonPropertyName("channelName")] public string ChannelName { get; set; } = "";

    [JsonPropertyName("totalEpisodes")] public int TotalEpisodes { get; set; }

    /// <summary>"更新至6集" / "全6集"。</summary>
    [JsonPropertyName("episodeText")] public string EpisodeText { get; set; } = "";

    [JsonPropertyName("releaseText")] public string ReleaseText { get; set; } = "";

    [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "xigua";

    [JsonPropertyName("detailUrl")] public string DetailUrl { get; set; } = "";

    /// <summary>元数据抓取时间（用于"多久没更新了"的展示与追更判断）。</summary>
    [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("episodes")] public List<DramaEpisodeMeta> Episodes { get; set; } = [];
    /// <summary>从本地既有目录导入时记录的原始路径（自己下载的剧为 null）。</summary>
    [JsonPropertyName("importedFrom")] public string? ImportedFrom { get; set; }

    /// <summary>已落盘的集数。</summary>
    [JsonIgnore] public int DownloadedCount => Episodes.Count(e => e.IsDownloaded);

    [JsonIgnore] public long TotalBytes => Episodes.Sum(e => e.SizeBytes);
}

/// <summary>单集元数据。没有 <see cref="File"/> 即表示未下载。</summary>
public sealed class DramaEpisodeMeta
{
    [JsonPropertyName("number")] public int Number { get; set; }

    /// <summary>展示用时长（"4:21"）。</summary>
    [JsonPropertyName("durationText")] public string DurationText { get; set; } = "";

    /// <summary>文件名（相对本目录）；null/空 = 未下载。</summary>
    [JsonPropertyName("file")] public string? File { get; set; }

    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }

    [JsonPropertyName("downloadedAt")] public DateTimeOffset? DownloadedAt { get; set; }

    /// <summary>文件存在但时长校验不通过（残缺），UI 上要区别于"完整已下载"。</summary>
    [JsonPropertyName("partial")] public bool Partial { get; set; }

    [JsonIgnore] public bool IsDownloaded => !string.IsNullOrWhiteSpace(File);
}

/// <summary>本地库条目：元数据 + 所在文件夹（便于打开目录 / 解析封面 / 播放）。</summary>
public sealed class LocalDrama
{
    public required string FolderPath { get; init; }

    public required DramaMetadata Metadata { get; init; }

    public string Id => Metadata.Id;

    public string Title => Metadata.Title;

    public int DownloadedCount => Metadata.DownloadedCount;

    public int TotalEpisodes => Metadata.Episodes.Count > 0 ? Metadata.Episodes.Count : Metadata.TotalEpisodes;

    public long TotalBytes => Metadata.TotalBytes;

    /// <summary>封面绝对路径；文件不存在返回 null（卡片走首字占位）。</summary>
    public string? CoverPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Metadata.CoverFile)) return null;
            var path = Path.Combine(FolderPath, Metadata.CoverFile);
            return System.IO.File.Exists(path) ? path : null;
        }
    }

    public string? ResolveEpisodeFile(int number)
    {
        var ep = Metadata.Episodes.FirstOrDefault(e => e.Number == number);
        if (ep?.File is not { Length: > 0 } file) return null;
        var path = Path.Combine(FolderPath, file);
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>库里最新一次下载时间（列表排序用）。</summary>
    public DateTimeOffset LastDownloadedAt =>
        Metadata.Episodes.Where(e => e.DownloadedAt.HasValue)
            .Select(e => e.DownloadedAt!.Value)
            .DefaultIfEmpty(Metadata.FetchedAt)
            .Max();
}

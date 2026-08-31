using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

/// <summary>
/// GFriends — GitHub 开源头像库（github.com/gfriends/gfriends）纯头像源。
/// 索引：仓库根 Filetree.json（约 6MB），键 = 演员名/别名（含日文名与罗马字，天然做了别名归一），
///       值 = 实际图片文件名；图片按 Content/&lt;公司&gt;/&lt;文件名&gt; 组织。
/// 流程：索引本地缓存（TTL 30 天，过期自动重下）→ 名字精确查索引 → 下载图片
///       → 落盘到 avatars 目录 → 返回本地路径（离线复用）。
/// 参考 amane（github.com/sqzw-x/amane）crawlers/actor/sites/gfriends.py 同源设计重写。
/// </summary>
public sealed class GFriendsSource : IVideoActorSource
{
    private static readonly string[] IndexUrls =
    [
        "https://raw.githubusercontent.com/gfriends/gfriends/master/Filetree.json",
        "https://cdn.jsdelivr.net/gh/xinxin8816/gfriends@master/Filetree.json",
        "https://cdn.jsdelivr.net/gh/gfriends/gfriends@master/Filetree.json",
    ];

    private const string ImageUrlTemplate =
        "https://raw.githubusercontent.com/gfriends/gfriends/master/Content/{0}/{1}";

    private static readonly TimeSpan IndexTtl = TimeSpan.FromDays(30);

    private readonly HttpClient _http;
    private readonly string _indexPath;
    private readonly string _avatarDir;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private Dictionary<string, (string Company, string File)>? _index;

    public GFriendsSource(HttpClient http, string indexPath, string avatarDir, ILogger? logger = null)
    {
        _http = http;
        _indexPath = indexPath;
        _avatarDir = avatarDir;
        _logger = logger;
    }

    public string Id => "gfriends";
    public string DisplayName => "GFriends";

    public async ValueTask<ActorSourceFetchResult> FetchAsync(string actorName, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var name = actorName.Trim();
        if (name.Length == 0) return ActorSourceFetchResult.NotFound(Id);

        try
        {
            var index = await EnsureIndexAsync(ct);
            if (index is null)
                return ActorSourceFetchResult.Failure(Id, ActorSourceOutcome.HttpError, "索引不可用且下载失败", (int)sw.ElapsedMilliseconds);
            if (!index.TryGetValue(name, out var seg))
                return ActorSourceFetchResult.NotFound(Id, (int)sw.ElapsedMilliseconds);

            // 头像落盘后离线复用，不再重复下载
            var localPath = Path.Combine(_avatarDir, HashName(name) + ".jpg");
            if (!File.Exists(localPath))
            {
                var relative = $"Content/{Uri.EscapeDataString(seg.Company)}/{Uri.EscapeDataString(seg.File)}";
                var bytes = await DownloadFirstAsync(relative, ct);
                if (bytes is null)
                    return ActorSourceFetchResult.Failure(Id, ActorSourceOutcome.HttpError, "头像下载失败（所有候选地址）", (int)sw.ElapsedMilliseconds);
                Directory.CreateDirectory(_avatarDir);
                await File.WriteAllBytesAsync(localPath, bytes, ct);
            }

            var metadata = new VideoActorMetadata { Name = name };
            metadata.ImageUrls.Add(localPath);
            metadata.SourceUrls["gfriends"] = "https://github.com/gfriends/gfriends";
            return ActorSourceFetchResult.Success(Id, metadata, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return ActorSourceFetchResult.Failure(Id, ActorSourceOutcome.Cancelled, "Cancelled", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[GFriends] {actorName} 获取失败: {ex.Message}");
            return ActorSourceFetchResult.Failure(Id, ActorSourceOutcome.HttpError, ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>加载索引：内存缓存 → 本地文件（未过期）→ 联网下载候选地址。</summary>
    private async Task<Dictionary<string, (string Company, string File)>?> EnsureIndexAsync(CancellationToken ct)
    {
        if (_index is not null) return _index;
        await _indexGate.WaitAsync(ct);
        try
        {
            if (_index is not null) return _index;

            if (!File.Exists(_indexPath) || DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_indexPath) > IndexTtl)
            {
                var downloaded = await DownloadIndexAsync(ct);
                if (downloaded is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
                    var tmp = _indexPath + ".tmp";
                    await File.WriteAllTextAsync(tmp, downloaded, ct);
                    File.Move(tmp, _indexPath, overwrite: true);
                }
            }

            if (File.Exists(_indexPath))
            {
                var json = await File.ReadAllTextAsync(_indexPath, ct);
                _index = ParseIndex(json);
                _logger?.Info($"[GFriends] 索引就绪: {_index.Count} 个名字");
            }
            return _index;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<string?> DownloadIndexAsync(CancellationToken ct)
    {
        foreach (var url in IndexUrls)
        {
            try
            {
                _logger?.Info($"[GFriends] 下载索引: {url}");
                var json = await _http.GetStringAsync(url, ct);
                if (json.Length > 1000) return json;
                _logger?.Warn($"[GFriends] 索引响应过小({json.Length})，换下一个地址");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn($"[GFriends] 索引下载失败，换下一个地址: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>解析 Filetree.json：{"Content": {"公司": {"名字.jpg": "实际文件.jpg?t=...", ...}}}</summary>
    internal static Dictionary<string, (string Company, string File)> ParseIndex(string json)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Content", out var content)) return result;

        foreach (var company in content.EnumerateObject())
        {
            foreach (var entry in company.Value.EnumerateObject())
            {
                var name = entry.Name;
                foreach (var ext in new[] { ".jpg", ".png", ".webp", ".jpeg" })
                {
                    if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        name = name[..^ext.Length];
                        break;
                    }
                }
                if (name.Length == 0) continue;

                // 值 = 实际文件名（可带 ?t= 缓存戳）；别名键映射到同一实际文件
                var file = entry.Value.GetString() ?? "";
                var q = file.IndexOf('?');
                if (q >= 0) file = file[..q];
                if (file.Length == 0) continue;

                result[name] = (company.Name, file);
            }
        }
        return result;
    }

    private async Task<byte[]?> DownloadFirstAsync(string relativePath, CancellationToken ct)
    {
        var url = string.Format(ImageUrlTemplate, relativePath);
        try
        {
            return await _http.GetByteArrayAsync(url, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Warn($"[GFriends] 头像下载失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>头像本地文件名（SHA256 前 16 位，规避演员名里的非法文件名字符）。</summary>
    public static string AvatarFileName(string name)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string HashName(string name) => AvatarFileName(name);
}

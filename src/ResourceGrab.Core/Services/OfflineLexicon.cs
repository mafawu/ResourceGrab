using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 离线词典（随包内嵌，无网络可用，首次使用时惰性加载一次）：
/// - mapping_actor.xml：演员别名（日文原名/繁/简）→ 简体名。该 mapping 表源自
///   AVDC / Movie_Data_Capture 生态（经 amane 项目分发，GPL-3.0），仅数据文件。
/// - mapping_info.xml：标签归一化映射；输出"删除"表示该标签应丢弃（如"10枚組"类噪声）。
/// - zhcdict.json：繁→简词典（zh2Hans 字/词映射 + zh2CN 地区词），取自 zhconv 项目。
/// 加载失败时全部方法原样返回，绝不阻断刮削主流程。
/// </summary>
public static class OfflineLexicon
{
    private const string DropMark = "删除";
    private const string ResourcePrefix = "ResourceGrab.Core.Resources.";

    private static readonly Lazy<bool> _loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Dictionary<string, string> _actorMap = new(StringComparer.Ordinal);   // 演员别名 → 简体名
    private static Dictionary<string, string> _tagMap = new(StringComparer.Ordinal);      // 标签关键词 → 归一词（DropMark=丢弃）
    private static Dictionary<string, string> _zh2Hans = new(StringComparer.Ordinal);     // 繁→简 字/词映射
    private static List<KeyValuePair<string, string>> _zhPhrases = [];                    // 多字词组（长词优先）

    private static bool Load()
    {
        try
        {
            var asm = typeof(OfflineLexicon).Assembly;
            LoadActorMap(asm);
            LoadInfoMap(asm);
            LoadZhDict(asm);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Stream? OpenResource(Assembly asm, string name) =>
        asm.GetManifestResourceStream(ResourcePrefix + name);

    private static void LoadActorMap(Assembly asm)
    {
        using var stream = OpenResource(asm, "mapping_actor.xml");
        if (stream is null) return;
        var doc = XDocument.Load(stream);
        foreach (var e in doc.Root?.Elements("a") ?? [])
        {
            var zh = ((string?)e.Attribute("zh_cn"))?.Trim();
            // "错误"/"删除"是表内标注的无效条目，不作为译名输出
            if (string.IsNullOrWhiteSpace(zh) || zh is DropMark or "错误" or "刪除") continue;
            _actorMap.TryAdd(zh, zh);
            var keywords = ((string?)e.Attribute("keyword"))?.Split(',') ?? [];
            foreach (var alias in keywords)
            {
                var name = alias.Trim();
                if (name.Length > 0 && name != zh) _actorMap.TryAdd(name, zh);
            }
        }
    }

    private static void LoadInfoMap(Assembly asm)
    {
        using var stream = OpenResource(asm, "mapping_info.xml");
        if (stream is null) return;
        var doc = XDocument.Load(stream);
        foreach (var e in doc.Root?.Elements("a") ?? [])
        {
            var zh = ((string?)e.Attribute("zh_cn"))?.Trim();
            if (string.IsNullOrWhiteSpace(zh)) continue;
            var keywords = ((string?)e.Attribute("keyword"))?.Split(',') ?? [];
            foreach (var alias in keywords)
            {
                var name = alias.Trim();
                if (name.Length > 0) _tagMap.TryAdd(name, zh);
            }
        }
    }

    private static void LoadZhDict(Assembly asm)
    {
        using var stream = OpenResource(asm, "zhcdict.json");
        if (stream is null) return;
        using var doc = JsonDocument.Parse(stream);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            if (p.Name is not ("zh2Hans" or "zh2CN")) continue;
            foreach (var e in p.Value.EnumerateObject())
            {
                if (e.Value.ValueKind != JsonValueKind.String) continue;
                _zh2Hans.TryAdd(e.Name, e.Value.GetString() ?? "");
            }
        }
        // 多字词组按长度降序整体替换，长词优先；单字走字典
        _zhPhrases = _zh2Hans
            .Where(kv => kv.Key.Length > 1)
            .OrderByDescending(kv => kv.Key.Length)
            .ToList();
    }

    /// <summary>演员名翻译：命中 mapping_actor 别名表输出简体名，未命中原样返回。</summary>
    public static string TranslateActor(string? name)
    {
        _ = _loaded.Value;
        var n = name?.Trim();
        return n is null || n.Length == 0 || !_loaded.Value
            ? n ?? ""
            : _actorMap.TryGetValue(n, out var zh) ? zh : n;
    }

    /// <summary>
    /// 标签归一化：命中 mapping_info 的输出映射词（"删除"标记返回 null 丢弃），
    /// 未命中的做繁转简。
    /// </summary>
    public static string? NormalizeTag(string? tag)
    {
        _ = _loaded.Value;
        var t = tag?.Trim();
        if (t is null || t.Length == 0 || !_loaded.Value) return t;
        return _tagMap.TryGetValue(t, out var mapped)
            ? mapped == DropMark ? null : mapped
            : ToSimplified(t);
    }

    /// <summary>批量归一化标签：丢弃"删除"项、去空、按出现顺序去重。</summary>
    public static List<string> NormalizeTags(IEnumerable<string?> tags)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var normalized = NormalizeTag(tag);
            if (normalized is not null && seen.Add(normalized)) result.Add(normalized);
        }
        return result;
    }

    /// <summary>繁→简转换：先整体替换多字词组（长词优先），再逐字映射。简体/西文原样返回。</summary>
    public static string ToSimplified(string? text)
    {
        _ = _loaded.Value;
        if (string.IsNullOrEmpty(text) || !_loaded.Value) return text ?? "";
        var s = text;
        foreach (var kv in _zhPhrases)
        {
            if (s.Contains(kv.Key, StringComparison.Ordinal)) s = s.Replace(kv.Key, kv.Value, StringComparison.Ordinal);
        }

        var sb = new StringBuilder(s.Length);
        var changed = false;
        foreach (var ch in s)
        {
            if (_zh2Hans.TryGetValue(ch.ToString(), out var mapped) && mapped.Length == 1)
            {
                sb.Append(mapped);
                changed = true;
            }
            else
            {
                sb.Append(ch);
            }
        }
        return changed ? sb.ToString() : s;
    }

    /// <summary>
    /// 刮削元数据落地前的离线词典后处理（两套刮削引擎共用）：标题/简介繁转简、
    /// 演员译名、标签归一化。OriginalTitle 保留原样。
    /// </summary>
    public static void ProcessMetadata(VideoScrapeMetadata meta)
    {
        if (meta is null) return;
        try
        {
            meta.Title = ToSimplified(meta.Title);
            meta.Description = ToSimplified(meta.Description);
            if (meta.Actors.Count > 0)
            {
                meta.Actors = meta.Actors.Select(TranslateActor)
                    .Where(a => a.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            if (meta.Tags.Count > 0) meta.Tags = NormalizeTags(meta.Tags);
        }
        catch
        {
            // 词典处理失败不影响刮削结果
        }
    }
}

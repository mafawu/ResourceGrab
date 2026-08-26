using ResourceGrab.Core.Models;
using System.Text.Json;

namespace ResourceGrab.Core.Services;

public class NovelIndexService
{
    private NovelIndexFile? _index;
    private List<NovelResource> _resources = new();
    private Dictionary<string,int> _tagCounts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string,int> _primaryCounts = new(StringComparer.OrdinalIgnoreCase);

    public NovelIndexFile? Index => _index;
    public IReadOnlyList<NovelResource> Resources => _resources;
    public IReadOnlyDictionary<string,int> TagCounts => _tagCounts;
    public IReadOnlyDictionary<string,int> PrimaryCounts => _primaryCounts;
    public string? IndexPath { get; private set; }
    public string? Root => _index?.Root;
    public string? EffectiveRoot { get; private set; }
    public string? LastError { get; private set; }

    public async Task<NovelIndexFile?> LoadAsync(string indexPath)
    {
        LastError = null;
        try
        {
            if (!File.Exists(indexPath))
            {
                LastError = $"索引文件不存在: {indexPath}";
                return null;
            }
            IndexPath = indexPath;
            string json;
            try { json = await File.ReadAllTextAsync(indexPath); }
            catch (Exception ex) { LastError = $"读取索引失败: {ex.Message}"; return null; }

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            try { _index = JsonSerializer.Deserialize<NovelIndexFile>(json, opts); }
            catch (Exception ex) { LastError = $"解析索引 JSON 失败: {ex.Message}"; return null; }

            if (_index == null) { LastError = "索引为空"; return null; }
            BuildResources();
            return _index;
        }
        catch (Exception ex)
        {
            LastError = $"未知错误: {ex.Message}";
            return null;
        }
    }

    private void BuildResources()
    {
        if (_index == null) return;
        var declaredRoot = _index.Root ?? "";
        string effectiveRoot = declaredRoot;
        // 兼容：JSON 里 root 可能是旧路径（如 E:\备份\小说_已分类），实际目录是 索引文件所在目录
        if (IndexPath != null)
        {
            var indexDir = Path.GetDirectoryName(IndexPath) ?? "";
            if (string.IsNullOrWhiteSpace(effectiveRoot) || !Directory.Exists(effectiveRoot))
            {
                if (Directory.Exists(indexDir)) effectiveRoot = indexDir;
            }
        }
        if (string.IsNullOrWhiteSpace(effectiveRoot) || !Directory.Exists(effectiveRoot))
        {
            // 最后兜底：尝试 E:\备份\小说
            if (Directory.Exists(@"E:\备份\小说")) effectiveRoot = @"E:\备份\小说";
        }
        EffectiveRoot = effectiveRoot;

        _resources = new List<NovelResource>();
        foreach (var f in _index.Files)
        {
            try
            {
                var rel = (f.RelativePath ?? "").Replace("\\","/").TrimStart('/','\\');
                // 防止 // 
                while (rel.Contains("//")) rel = rel.Replace("//","/");
                var full = string.IsNullOrWhiteSpace(effectiveRoot) ? rel : Path.Combine(effectiveRoot, rel.Replace("/","\\"));
                var primary = (f.PrimaryTag ?? "").Replace("\\","/").Trim();
                while (primary.Contains("//")) primary = primary.Replace("//","/");
                var tags = (f.Tags ?? new List<string>())
                    .Select(t => (t ?? "").Replace("\\","/").Trim())
                    .Select(t => { while(t.Contains("//")) t=t.Replace("//","/"); return t; })
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _resources.Add(new NovelResource
                {
                    Name = f.Name ?? "",
                    RelativePath = rel,
                    FullPath = full,
                    PrimaryTag = primary,
                    Tags = tags,
                    Size = f.Size
                });
            }
            catch { }
        }

        _tagCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        _primaryCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _resources)
        {
            foreach (var t in r.Tags)
            {
                if (_tagCounts.ContainsKey(t)) _tagCounts[t]++; else _tagCounts[t]=1;
            }
            var p = r.PrimaryTag;
            if (!string.IsNullOrWhiteSpace(p))
            {
                if (_primaryCounts.ContainsKey(p)) _primaryCounts[p]++; else _primaryCounts[p]=1;
            }
        }
    }

    public IReadOnlyList<string> GetStructure() => _index?.Structure.Select(s => (s??"").Replace("\\","/").Trim()).Where(s=>!string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();

    public Dictionary<string, List<string>> GetStructureTree()
    {
        var tree = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in GetStructure())
        {
            var clean = item.Replace("\\","/").Trim();
            while (clean.Contains("//")) clean = clean.Replace("//","/");
            var parts = clean.Split("/", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length==0) continue;
            var top = parts[0];
            if (!tree.ContainsKey(top)) tree[top] = new List<string>();
            if (parts.Length > 1) tree[top].Add(clean);
        }
        foreach (var k in tree.Keys.ToList()) tree[k] = tree[k].Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s=>s).ToList();
        return tree;
    }
}

// 批量拉取 gfriends 演员头像的独立小工具（与 App 内 GFriendsSource 同一套解析/哈希/存储逻辑）。
// 用法: dotnet run --project scripts/AvatarFetcher -c Release
using System.Security.Cryptography;
using System.Text.Json;

var proxy = "http://127.0.0.1:10809";
var baseDir = @"F:\ProcessProject\C#\ResourceGrab\bin\Release\config";
var libPath = Path.Combine(baseDir, "video-library.json");
var indexPath = Path.Combine(baseDir, "gfriends", "Filetree.json");
var avatarDir = Path.Combine(baseDir, "avatars");
Directory.CreateDirectory(avatarDir);

var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy(proxy), UseProxy = true };
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

// ── 1. 库内全部演员名 ──
var lib = JsonDocument.Parse(File.ReadAllText(libPath));
var actors = new HashSet<string>(StringComparer.Ordinal);
foreach (var item in lib.RootElement.EnumerateArray())
{
    if (!item.TryGetProperty("Actors", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
    foreach (var a in arr.EnumerateArray())
    {
        var n = a.GetString();
        if (!string.IsNullOrWhiteSpace(n)) actors.Add(n);
    }
}
Console.WriteLine($"演员总数: {actors.Count}");

// ── 2. gfriends 索引（大小写敏感解析，PS5.1 的 ConvertFrom-Json 会因重名键失败）──
var index = new Dictionary<string, (string Company, string File)>(StringComparer.Ordinal);
using (var tree = JsonDocument.Parse(File.ReadAllText(indexPath)))
{
    if (tree.RootElement.TryGetProperty("Content", out var content))
    {
        foreach (var comp in content.EnumerateObject())
        {
            foreach (var e in comp.Value.EnumerateObject())
            {
                var name = e.Name;
                foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp" })
                    if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
                if (name.Length == 0) continue;
                var file = e.Value.GetString() ?? "";
                var q = file.IndexOf('?');
                if (q >= 0) file = file[..q];
                if (file.Length == 0 || index.ContainsKey(name)) continue;
                index[name] = (comp.Name, file);
            }
        }
    }
}
Console.WriteLine($"索引名字数: {index.Count}");

// ── 3. 下载缺失头像 ──
int done = 0, hit = 0, miss = 0, fail = 0, skip = 0;
foreach (var name in actors)
{
    done++;
    var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();
    var outPath = Path.Combine(avatarDir, hash + ".jpg");
    if (File.Exists(outPath)) { skip++; continue; }
    if (!index.TryGetValue(name, out var seg)) { miss++; continue; }
    var url = $"https://raw.githubusercontent.com/gfriends/gfriends/master/Content/{Uri.EscapeDataString(seg.Company)}/{Uri.EscapeDataString(seg.File)}";
    try
    {
        var bytes = await http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(outPath, bytes);
        hit++;
    }
    catch { fail++; }
    if (done % 25 == 0)
        Console.WriteLine($"进度 {done}/{actors.Count} 命中{hit} 缺索引{miss} 失败{fail} 已有{skip}");
}
Console.WriteLine($"完成: 总{actors.Count} 新下载{hit} 缺索引{miss} 失败{fail} 已有{skip}");

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 漫画标题中文名生成：
/// 1) 先从标题中提取已有的中文片段（离线、零成本）；
/// 2) 提取不到时，若配置了 titleTranslate，调用 OpenAI 兼容 Chat Completions 接口翻译。
/// </summary>
public class TitleTranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim Gate = new(3, 3);
    private static readonly Regex BracketRegex = new(@"\[[^\]]*\]|\([^)]*\)|（[^）]*）|【[^】]*】", RegexOptions.Compiled);
    private static readonly Regex HanRegex = new(@"[\u4e00-\u9fff]{2,}", RegexOptions.Compiled);
    private static readonly Regex KanaRegex = new(@"[\p{IsHiragana}\p{IsKatakana}]", RegexOptions.Compiled);
    private static readonly Regex BracketsLeftRegex = new(@"[\[\]【】（）()]", RegexOptions.Compiled);

    /// <summary>从标题中提取已有中文片段（去掉方括号/圆括号标签块后合并连续汉字）；无中文时返回空字符串。</summary>
    public static string ExtractChineseName(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        var withoutBrackets = BracketRegex.Replace(title, " ");
        var parts = HanRegex.Matches(withoutBrackets).Select(m => m.Value).ToList();
        if (parts.Count == 0)
        {
            return "";
        }

        var joined = string.Concat(parts);
        return joined.Length < 2 ? "" : joined;
    }

    /// <summary>判断中文名是否为「未完成的翻译」：残留日文假名或括号（社团/作者名未剥离），需要重新翻译。</summary>
    public static bool LooksUnfinished(string? nameCn)
    {
        if (string.IsNullOrWhiteSpace(nameCn))
        {
            return false;
        }

        return KanaRegex.IsMatch(nameCn) || BracketsLeftRegex.IsMatch(nameCn);
    }

    /// <summary>调用 OpenAI 兼容接口把标题翻译为简体中文；未配置、失败或结果不合格时返回 null。</summary>
    public async Task<string?> TranslateAsync(string title, TitleTranslateOptions options, CancellationToken ct = default)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var baseUrl = options.BaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = "https://api.openai.com/v1";
        }
        var model = string.IsNullOrWhiteSpace(options.Model) ? "gpt-4o-mini" : options.Model;

        var payload = new
        {
            model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是漫画标题翻译器。输入是本地下载的漫画文件夹名，通常形如 [汉化组](展会)[社团 (作者)] 标题 [语言标签]。只翻译「标题主体」为简体中文，并遵守规则：1) 不要保留或翻译方括号/圆括号/【】内的社团名、作者名、汉化组、展会名、语言标签；2) 只输出翻译后的标题，不要输出方括号、引号、前后缀或任何解释；3) 若标题主体本身已是中文，直接原样输出标题主体。",
                },
                new { role = "user", content = title },
            },
            temperature = 0.2,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {options.ApiKey}");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        await Gate.WaitAsync(ct);
        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            var cleaned = Clean(content);
            if (string.IsNullOrEmpty(cleaned) || LooksUnfinished(cleaned))
            {
                return null;
            }
            return cleaned;
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var result = text.Trim();
        // 去掉“翻译：/译文：”等前缀
        result = Regex.Replace(result, @"^(?:翻译|译文|中文|简体|简中|中译)\s*[:：]\s*", "");
        // 去掉方括号/圆括号/【】标签块（社团、作者、展会、语言标签等）
        result = BracketRegex.Replace(result, " ");
        // 去掉引号与常见标点
        result = result.Trim().Trim('"', '\'', '“', '”', '「', '」', '『', '』', '、', '。', '：', ' ', '\t');
        // 合并多余空白
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        return result;
    }
}

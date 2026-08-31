using System.Text;
using System.Text.RegularExpressions;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services;

public enum VideoNumberKind { Unknown = 0, Normal, Fc2, Uncensored }

public record VideoNumberResult(string Number, VideoNumberKind Kind, string Part, double Confidence);

public static partial class VideoNumberParser
{
    [GeneratedRegex(@"(?:^|[^A-Za-z0-9])([A-Za-z]{2,8})[-_ ]?(\d{2,6})(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandardNumber();

    [GeneratedRegex(@"[A-Za-z0-9._+-]+\.(?:com|net|org|vip|xyz|cc|me|tv|io|co|jp|us|top|club|shop|site|online|icu|info|biz|live|app|link|pro|art|fun|space|store|tech|website|pw|la|moe)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DomainNoise();

    [GeneratedRegex(@"^[A-Za-z]{2,8}[-_]\d{2,6}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberShapedToken();

    public static VideoNumberResult Parse(string filePath)
        => Parse(filePath, null);

    public static VideoNumberResult Parse(string filePath, ILogger? logger)
    {
        var result = ParseCore(filePath);
        logger?.Info($"[VideoNumberParser] \"{Path.GetFileName(filePath)}\" -> " +
            $"{(result.Number.Length > 0 ? result.Number : "(未识别)")} kind={result.Kind} confidence={result.Confidence:0.00}");
        return result;
    }

    private static VideoNumberResult ParseCore(string filePath)
    {
        var text = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(text)) return new("", VideoNumberKind.Unknown, "", 0);
        text = DomainNoise().Replace(text, " ");
        text = Regex.Replace(text, @"@([^@\s]+)", static match =>
            NumberShapedToken().IsMatch(match.Groups[1].Value)
                ? $" {match.Groups[1].Value}"
                : " ",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(1080p|720p|2160p|4k|2k|hevc|h\.?26[45]|x264|x265|uncensored|censored|chinese|ch|cd\s*(\d+))", " ", RegexOptions.IgnoreCase);
        var partMatch = Regex.Match(Path.GetFileNameWithoutExtension(filePath), @"\bCD\s*(\d+)\b", RegexOptions.IgnoreCase);
        var part = partMatch.Success ? $"CD{partMatch.Groups[1].Value}" : "";

        var fc2 = Regex.Match(text, @"(?:FC2|FPPV)[-_ ]?(?:PPV[-_ ]?)?(\d{5,8})", RegexOptions.IgnoreCase);
        if (fc2.Success) return new($"FC2-{fc2.Groups[1].Value}", VideoNumberKind.Fc2, part, 0.95);

        var best = StandardNumber().Matches(text).Cast<Match>()
            .Where(match => match.Groups[1].Value.Length >= 2 && match.Groups[1].Value.Length <= 5)
            .Select(match => new
            {
                Number = $"{match.Groups[1].Value.ToUpperInvariant()}-{match.Groups[2].Value}",
                Confidence = match.Value.Contains('-') ? 0.92 : match.Groups[1].Value.Length <= 5 ? 0.72 : 0.48,
            })
            .OrderByDescending(candidate => candidate.Confidence)
            .FirstOrDefault();
        if (best is not null)
            return new(best.Number, VideoNumberKind.Normal, part, best.Confidence);

        var uncensored = Regex.Match(text, @"(?:^|\s)(\d{6})-(\d{3})(?:$|\s)");
        if (uncensored.Success) return new($"{uncensored.Groups[1].Value}-{uncensored.Groups[2].Value}", VideoNumberKind.Uncensored, part, 0.85);

        uncensored = Regex.Match(text, @"(?:^|\s)(\d{3,4})[_-](\d{3})(?:$|\s)");
        if (uncensored.Success) return new($"{uncensored.Groups[1].Value}-{uncensored.Groups[2].Value}", VideoNumberKind.Uncensored, part, 0.7);

        // 严格模式：只认文件名本身的番号，不做父目录兜底——
        // 否则文件夹内的广告/预告/任意命名杂片会借用文件夹番号入库被误刮削。
        return new("", VideoNumberKind.Unknown, part, 0);
    }
}

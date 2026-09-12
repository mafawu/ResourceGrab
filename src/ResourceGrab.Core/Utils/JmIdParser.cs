using System.Text.RegularExpressions;

namespace ResourceGrab.Core.Utils;

/// <summary>
/// 禁漫 JM 号解析：JM123456 / jm123456 / JM-123456 / JM 123456 / 【JM123456】/ 纯数字。
/// 命中时调用方可直达详情（GetComicAsync），失败再回退关键词搜索。
/// </summary>
public static partial class JmIdParser
{
    /// <summary>JM 专辑号位数范围（过短的纯数字更可能是普通关键词，回退搜索更稳妥）。</summary>
    private const int MinDigits = 4;
    private const int MaxDigits = 9;

    [GeneratedRegex(@"^(?:jm)?\s*[-_#\s]*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex JmIdRegex();

    public static bool TryParse(string? input, out long albumId)
    {
        albumId = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();
        // 【JM123456】这类括号包裹：先剥一层括号再解析
        if (text.Length >= 2 && ((text.StartsWith("【") && text.EndsWith("】"))
            || (text.StartsWith("[") && text.EndsWith("]"))))
        {
            text = text[1..^1].Trim();
        }
        var m = JmIdRegex().Match(text);
        if (!m.Success) return false;
        var digits = m.Groups[1].Value;
        if (digits.Length < MinDigits || digits.Length > MaxDigits) return false;
        if (!long.TryParse(digits, out albumId) || albumId <= 0) return false;
        return true;
    }
}

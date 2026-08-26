using System.Text;

namespace ResourceGrab.Core.Utils;

/// <summary>
/// 将字符串中的非法文件名字符替换为安全字符（与原 Rust 实现一致）。
/// </summary>
public static class FilenameFilter
{
    public static string Filter(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            sb.Append(c switch
            {
                '\\' or '/' => ' ',
                ':' => '：',
                '*' => '⭐',
                '?' => '？',
                '"' => '\'',
                '<' => '《',
                '>' => '》',
                '|' => '丨',
                '.' => '·',
                _ => c,
            });
        }
        return sb.ToString().Trim();
    }
}

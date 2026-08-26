using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ResourceGrab.Core.Utils;

/// <summary>
/// 评论内容 HTML 转纯文本：剥离标签、br 转换行、解码实体。
/// </summary>
public static class HtmlText
{
    private static readonly HtmlParser Parser = new();

    public static string StripToText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        var sb = new StringBuilder();
        var document = Parser.ParseDocument(html);
        var body = document.Body;
        if (body is not null)
        {
            foreach (var node in body.ChildNodes)
            {
                AppendNodeText(node, sb);
            }
        }
        return sb.ToString().Trim();
    }

    private static void AppendNodeText(INode node, StringBuilder sb)
    {
        switch (node)
        {
            case IText text:
                sb.Append(text.Text);
                break;
            case IElement { TagName: "STYLE" or "SCRIPT" }:
                return;
            case IElement { TagName: "BR" }:
                sb.Append('\n');
                break;
            case IElement element:
                foreach (var child in element.ChildNodes)
                {
                    AppendNodeText(child, sb);
                }
                break;
        }
    }
}

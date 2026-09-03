using ResourceGrab.Core.Models;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Utils;

namespace ResourceGrab.App.ViewModels;

/// <summary>漫画评论显示模型：兼容在线评论（ComicComment）与本地保存的原始评论（ForumCommentRespData）。</summary>
public sealed class CommentViewModel
{
    public string AuthorLine { get; }
    public string Content { get; }
    public string LikesText { get; }
    public string TimeText { get; }
    public List<CommentViewModel> Replies { get; }

    public CommentViewModel(ComicComment c)
    {
        AuthorLine = DisplayName(c.Nickname, c.Username);
        Content = c.Content;
        LikesText = c.Likes?.ToString("N0") ?? "0";
        TimeText = FormatTime(c.CreatedAt);
        Replies = c.Replies.Select(r => new CommentViewModel(r)).ToList();
    }

    public CommentViewModel(ForumCommentRespData c)
    {
        AuthorLine = DisplayName(c.Nickname, c.Username);
        Content = HtmlText.StripToText(c.Content);
        LikesText = c.Likes is { Length: > 0 } s ? s : "0";
        TimeText = FormatTime(c.Addtime);
        Replies = (c.Replies ?? new()).Select(r => new CommentViewModel(r)).ToList();
    }

    private static string DisplayName(string? nickname, string? username)
        => !string.IsNullOrWhiteSpace(nickname) ? nickname
         : !string.IsNullOrWhiteSpace(username) ? username
         : "匿名";

    private static string FormatTime(string? raw)
    {
        if (long.TryParse(raw, out var ts))
        {
            return DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("MM-dd HH:mm");
        }
        return raw ?? "";
    }

    /// <summary>导出为可复制的纯文本（作者 / 时间 / 内容，回复缩进跟随）。</summary>
    public string ToPlainText(string indent = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(indent).Append(AuthorLine);
        if (!string.IsNullOrEmpty(TimeText)) sb.Append(' ').Append(TimeText);
        sb.AppendLine();
        sb.AppendLine(Content);
        foreach (var reply in Replies)
        {
            sb.Append(reply.ToPlainText(indent + "  "));
        }
        return sb.ToString().TrimEnd();
    }
}

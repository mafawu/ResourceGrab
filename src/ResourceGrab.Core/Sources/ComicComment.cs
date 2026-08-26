namespace ResourceGrab.Core.Sources;

/// <summary>漫画评论（含嵌套回评），与具体站点解耦的通用模型。</summary>
public class ComicComment
{
    public string CommentId { get; init; } = "";
    public string? AlbumId { get; init; }
    public string? UserId { get; init; }
    public string? ParentCommentId { get; init; }
    public string Content { get; init; } = "";
    public string? Username { get; init; }
    public string? Nickname { get; init; }
    public bool IsSpoiler { get; init; }
    public string? CreatedAt { get; init; }
    public long? Likes { get; init; }
    public List<ComicComment> Replies { get; init; } = new();
}

/// <summary>评论分页。</summary>
public class CommentPage
{
    public List<ComicComment> Items { get; init; } = new();

    /// <summary>全部分页的主评论总数；接口未提供时（如网页端）为 null。</summary>
    public long? Total { get; init; }

    public int Page { get; init; }

    public int PageSize => 10;

    public int? PageCount => Total is { } total ? (int)((total + PageSize - 1) / PageSize) : null;
}
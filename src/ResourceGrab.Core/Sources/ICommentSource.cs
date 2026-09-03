namespace ResourceGrab.Core.Sources;

/// <summary>支持漫画评论查询的源能力（与 ICategorySource / IRankSource 同类的可选能力声明）。</summary>
public interface ICommentSource
{
    /// <summary>获取指定漫画的评论分页（第 1 页起，每页 10 条）。</summary>
    Task<CommentPage> GetCommentsAsync(string comicId, int page, CancellationToken ct = default);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

/// <summary>禁漫 API 统一响应包装：code==200 时 data 为加密字符串。</summary>
public class JmResp
{
    [JsonPropertyName("code")] public long Code { get; set; }
    [JsonPropertyName("data")] public JsonElement Data { get; set; }
    [JsonPropertyName("error_msg")] public string ErrorMsg { get; set; } = "";
}

public class UserProfileRespData
{
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("emailverified")] public string EmailVerified { get; set; } = "";
    [JsonPropertyName("photo")] public string Photo { get; set; } = "";
    [JsonPropertyName("fname")] public string Fname { get; set; } = "";
    [JsonPropertyName("gender")] public string Gender { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("coin")] public long Coin { get; set; }
    [JsonPropertyName("album_favorites")] public long AlbumFavorites { get; set; }
    [JsonPropertyName("s")] public string S { get; set; } = "";
    [JsonPropertyName("level_name")] public string LevelName { get; set; } = "";
    [JsonPropertyName("level")] public long Level { get; set; }
    [JsonPropertyName("nextLevelExp")] public long NextLevelExp { get; set; }
    [JsonPropertyName("exp")] public string Exp { get; set; } = "";
    [JsonPropertyName("expPercent")] public double ExpPercent { get; set; }
    [JsonPropertyName("album_favorites_max")] public long AlbumFavoritesMax { get; set; }
    [JsonPropertyName("ad_free")] public bool AdFree { get; set; }
    [JsonPropertyName("charge")] public string Charge { get; set; } = "";
    [JsonPropertyName("jar")] public string Jar { get; set; } = "";
    [JsonPropertyName("invitation_qrcode")] public string InvitationQrcode { get; set; } = "";
    [JsonPropertyName("invitation_url")] public string InvitationUrl { get; set; } = "";
    [JsonPropertyName("invited_cnt")] public string InvitedCnt { get; set; } = "";
}

public class CategoryRespData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
}

public class CategorySubRespData
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}

public class AlbumInSearchRespData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("image")] public string Image { get; set; } = "";
    [JsonPropertyName("category")] public CategoryRespData Category { get; set; } = new();
    [JsonPropertyName("category_sub")] public CategorySubRespData CategorySub { get; set; } = new();
    [JsonPropertyName("liked")] public bool Liked { get; set; }
    [JsonPropertyName("is_favorite")] public bool IsFavorite { get; set; }
    [JsonPropertyName("update_at")] public long UpdateAt { get; set; }
}

public class SearchRespData
{
    [JsonPropertyName("search_query")] public string SearchQuery { get; set; } = "";
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("content")] public List<AlbumInSearchRespData> Content { get; set; } = new();
}

/// <summary>搜索命中唯一漫画时返回的重定向结构。</summary>
public class RedirectRespData
{
    [JsonPropertyName("search_query")] public string SearchQuery { get; set; } = "";
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("redirect_aid")] public string RedirectAid { get; set; } = "";
}

public class AlbumRespData
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("addtime")] public string Addtime { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("total_views")] public string TotalViews { get; set; } = "";
    [JsonPropertyName("likes")] public string Likes { get; set; } = "";
    [JsonPropertyName("series")] public List<SeriesRespData> Series { get; set; } = new();
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("comment_total")] public string CommentTotal { get; set; } = "";
    [JsonPropertyName("author")] public List<string> Author { get; set; } = new();
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("works")] public List<string> Works { get; set; } = new();
    [JsonPropertyName("actors")] public List<string> Actors { get; set; } = new();
    [JsonPropertyName("related_list")] public List<RelatedListRespData> RelatedList { get; set; } = new();
    [JsonPropertyName("liked")] public bool Liked { get; set; }
    [JsonPropertyName("is_favorite")] public bool IsFavorite { get; set; }
    [JsonPropertyName("is_aids")] public bool IsAids { get; set; }
}

public class SeriesRespData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sort")] public string Sort { get; set; } = "";
}

public class RelatedListRespData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("image")] public string Image { get; set; } = "";
}

public class ChapterRespData
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("series")] public List<SeriesRespData> Series { get; set; } = new();
    [JsonPropertyName("tags")] public string Tags { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("images")] public List<string> Images { get; set; } = new();
    [JsonPropertyName("addtime")] public string Addtime { get; set; } = "";
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("is_favorite")] public bool IsFavorite { get; set; }
    [JsonPropertyName("liked")] public bool Liked { get; set; }
}

public class FavoriteRespData
{
    [JsonPropertyName("list")] public List<AlbumInFavoriteRespData> List { get; set; } = new();
    [JsonPropertyName("folder_list")] public List<FavoriteFolderRespData> FolderList { get; set; } = new();
    [JsonPropertyName("total")] public string Total { get; set; } = "";
    [JsonPropertyName("count")] public long Count { get; set; }
}

public class AlbumInFavoriteRespData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("latest_ep")] public string? LatestEp { get; set; }
    [JsonPropertyName("latest_ep_aid")] public string? LatestEpAid { get; set; }
    [JsonPropertyName("image")] public string Image { get; set; } = "";
    [JsonPropertyName("category")] public CategoryRespData Category { get; set; } = new();
    [JsonPropertyName("category_sub")] public CategorySubRespData CategorySub { get; set; } = new();
}

public class FavoriteFolderRespData
{
    [JsonPropertyName("FID")] public string Fid { get; set; } = "";
    [JsonPropertyName("UID")] public string Uid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class ToggleFavoriteResp
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("msg")] public string Msg { get; set; } = "";
    [JsonPropertyName("type")] public ToggleType ToggleType { get; set; }
}

public class GetWeeklyInfoRespData
{
    [JsonPropertyName("categories")] public List<CategoryInWeeklyInfo> Categories { get; set; } = new();
    [JsonPropertyName("type")] public List<WeeklyType> Types { get; set; } = new();
}

public class CategoryInWeeklyInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
}

public class WeeklyType
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
}

public class GetWeeklyRespData
{
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("list")] public List<ComicInWeeklyRespData> List { get; set; } = new();
}

public class ComicInWeeklyRespData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("image")] public string Image { get; set; } = "";
    [JsonPropertyName("category")] public CategoryRespData Category { get; set; } = new();
    [JsonPropertyName("category_sub")] public CategorySubRespData CategorySub { get; set; } = new();
    [JsonPropertyName("liked")] public bool Liked { get; set; }
    [JsonPropertyName("is_favorite")] public bool IsFavorite { get; set; }
    [JsonPropertyName("update_at")] public long UpdateAt { get; set; }
}

/// <summary>禁漫评论分页响应（/forum?mode=all）。</summary>
public class ForumRespData
{
    [JsonPropertyName("list")] public List<ForumCommentRespData> List { get; set; } = new();
    [JsonPropertyName("total")] public string Total { get; set; } = "";
}

/// <summary>禁漫评论条目（replys 为嵌套回评，结构相同）。</summary>
public class ForumCommentRespData
{
    [JsonPropertyName("CID")] public string Cid { get; set; } = "";
    [JsonPropertyName("AID")] public string? Aid { get; set; }
    [JsonPropertyName("UID")] public string? Uid { get; set; }
    [JsonPropertyName("parent_CID")] public string? ParentCid { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("spoiler")] public string? Spoiler { get; set; }
    [JsonPropertyName("is_spoiler")] public JsonElement? IsSpoiler { get; set; }
    [JsonPropertyName("addtime")] public string? Addtime { get; set; }
    [JsonPropertyName("likes")] public string? Likes { get; set; }
    [JsonPropertyName("replys")] public List<ForumCommentRespData> Replies { get; set; } = new();
}

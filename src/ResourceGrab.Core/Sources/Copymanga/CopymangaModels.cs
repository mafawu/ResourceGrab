using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Sources.Copymanga;

// ============================================================
// 拷贝漫画（copymanga）JSON API 响应模型
// 对齐 copymanga-downloader（lanyeeee）源码中的 serde 结构：
//  - 详情用 /api/v3/comic2/ 返回 results.comic + results.groups
//  - 章节用 /group/{gw}/chapters 分页拉取，章节图片需 Authorization token
// ============================================================

public class CopyApiResponse<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("results")] public T? Results { get; set; }
}

/// <summary>通用分页包裹（搜索 / 章节列表共用）。</summary>
public class CopyPagination<T>
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("list")] public List<T> List { get; set; } = new();
}

// ====================== 搜索 ======================

/// <summary>搜索结果：results 直接是 Pagination{ComicInSearch}。</summary>
public class CopySearchResults : CopyPagination<CopyComicInSearch>
{
}

public class CopyComicInSearch
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    [JsonPropertyName("path_word")] public string PathWord { get; set; } = "";
    [JsonPropertyName("cover")] public string Cover { get; set; } = "";
    [JsonPropertyName("ban")] public int Ban { get; set; }
    [JsonPropertyName("author")] public List<CopyAuthor> Author { get; set; } = new();
    [JsonPropertyName("popular")] public long Popular { get; set; }
}

public class CopyAuthor
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path_word")] public string? PathWord { get; set; }
}

// ====================== 详情（comic2） ======================

/// <summary>详情响应 results：comic 元信息 + groups 字典（name → 分组）。</summary>
public class CopyComicDetail
{
    [JsonPropertyName("comic")] public CopyComicMeta Comic { get; set; } = new();

    /// <summary>分组字典：key 为分组 name（如 "正序"/"番外"），value 含 path_word 与名称。</summary>
    [JsonPropertyName("groups")] public Dictionary<string, CopyGroupMeta> Groups { get; set; } = new();
}

public class CopyComicMeta
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path_word")] public string PathWord { get; set; } = "";
    [JsonPropertyName("author")] public List<CopyAuthor> Author { get; set; } = new();
    [JsonPropertyName("cover")] public string Cover { get; set; } = "";
    [JsonPropertyName("brief")] public string Brief { get; set; } = "";
    [JsonPropertyName("theme")] public List<CopyTheme> Theme { get; set; } = new();
    [JsonPropertyName("datetime_updated")] public string DateTimeUpdated { get; set; } = "";
}

public class CopyTheme
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path_word")] public string? PathWord { get; set; }
}

public class CopyGroupMeta
{
    [JsonPropertyName("path_word")] public string PathWord { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

// ====================== 章节列表 ======================

/// <summary>某分组下的章节分页结果。</summary>
public class CopyChaptersResult : CopyPagination<CopyChapterMeta>
{
}

public class CopyChapterMeta
{
    [JsonPropertyName("index")] public long Index { get; set; }
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ordered")] public long Ordered { get; set; }
    [JsonPropertyName("comic_path_word")] public string ComicPathWord { get; set; } = "";
    [JsonPropertyName("group_path_word")] public string GroupPathWord { get; set; } = "";
    [JsonPropertyName("group_id")] public string? GroupId { get; set; }
    [JsonPropertyName("type")] public long Type { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("count")] public long Count { get; set; }
}

// ====================== 章节详情（图片） ======================

/// <summary>章节图片响应：contents 为图片 URL 列表，words 为排序索引（打乱）。</summary>
public class CopyChapterDetail
{
    [JsonPropertyName("chapter")] public CopyChapterData Chapter { get; set; } = new();
}

public class CopyChapterData
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("contents")] public List<CopyContent> Contents { get; set; } = new();
    /// <summary>图片真实顺序索引：contents[i] 的最终位置是 words[i]。</summary>
    [JsonPropertyName("words")] public List<long> Words { get; set; } = new();
}

public class CopyContent
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

// ====================== 登录 ======================

public class CopyLoginResult
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
}

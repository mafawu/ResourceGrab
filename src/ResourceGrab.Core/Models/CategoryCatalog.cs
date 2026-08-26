namespace ResourceGrab.Core.Models;

/// <summary>分类节点：站点「成人A漫」分类 + 子分类（Slug 对应接口路径段）。</summary>
public class CategoryNode
{
    public string Name { get; init; } = "";

    /// <summary>接口路径段，如 "doujin"、"doujin/sub/CG"；空串表示全部。</summary>
    public string Slug { get; init; } = "";

    /// <summary>该分类下的热门标签快捷入口。</summary>
    public List<string> HotTags { get; init; } = new();

    public List<CategoryNode> Children { get; init; } = new();
}

/// <summary>主题分区条目：分类或标签。分类可携带子分类（对应 /theme/ 页「類別」分区）。</summary>
public class ThemeEntry
{
    public string Name { get; init; } = "";

    /// <summary>是否为分类条目；false 表示标签（按关键词搜索）。</summary>
    public bool IsCategory { get; init; }

    /// <summary>分类接口路径段，如 "doujin"、"doujin/sub/CG"；空串表示全部。</summary>
    public string Slug { get; init; } = "";

    /// <summary>子分类（仅分类条目使用，对应「類別」分区下的子分类）。</summary>
    public List<ThemeEntry> Children { get; init; } = new();

    /// <summary>首次进入时默认选中（「全部」分类）。</summary>
    public bool IsDefault { get; init; }
}

/// <summary>主题分区（对应 18comic /theme/ 页左侧分区）。</summary>
public class ThemeSection
{
    public string Title { get; init; } = "";
    public List<ThemeEntry> Entries { get; init; } = new();
}

/// <summary>站点主题页目录（/theme/：熱門成人主題 / 類別 / 主題A漫 / 角色扮演 / 特殊PLAY / 其他）。</summary>
public static class ThemeCatalog
{
    private static ThemeEntry Tag(string name) => new() { Name = name };

    private static ThemeEntry Cat(CategoryNode node) => new()
    {
        Name = node.Name,
        Slug = node.Slug,
        IsCategory = true,
        IsDefault = string.IsNullOrEmpty(node.Slug),
        Children = node.Children.Select(Cat).ToList(),
    };

    public static List<ThemeSection> Sections { get; } = new()
    {
        new()
        {
            Title = "熱門成人主題",
            Entries =
            {
                Tag("劇情向"), Tag("運動褲"), Tag("觀淫"), Tag("多作者"), Tag("全彩"),
                Tag("無修正"), Tag("完結"), Tag("巨乳"), Tag("NTR"), Tag("純愛"),
            },
        },
        new()
        {
            Title = "類別",
            Entries = CategoryCatalog.Roots.Select(Cat).ToList(),
        },
        new()
        {
            Title = "主題A漫",
            Entries =
            {
                Tag("無修正"), Tag("劇情向"), Tag("青年漫"), Tag("校服"), Tag("純愛"), Tag("人妻"),
                Tag("教師"), Tag("百合"), Tag("Yaoi"), Tag("性轉"), Tag("NTR"), Tag("女裝"),
                Tag("癡女"), Tag("全彩"), Tag("女性向"), Tag("完結"), Tag("禁漫漢化組"),
            },
        },
        new()
        {
            Title = "角色 / 扮演",
            Entries =
            {
                Tag("御姐"), Tag("熟女"), Tag("巨乳"), Tag("貧乳"), Tag("女性支配"), Tag("教師"),
                Tag("女僕"), Tag("護士"), Tag("泳裝"), Tag("眼鏡"), Tag("連褲襪"), Tag("其他制服"),
                Tag("兔女郎"),
            },
        },
        new()
        {
            Title = "特殊PLAY",
            Entries =
            {
                Tag("群交"), Tag("足交"), Tag("束縛"), Tag("肛交"), Tag("阿黑顏"), Tag("藥物"),
                Tag("扶他"), Tag("調教"), Tag("野外露出"), Tag("催眠"), Tag("自慰"), Tag("觸手"),
                Tag("獸交"), Tag("亞人"), Tag("怪物女孩"), Tag("皮物"), Tag("ryona"), Tag("騎大車"),
            },
        },
        new()
        {
            Title = "其他",
            Entries =
            {
                Tag("CG"), Tag("重口"), Tag("獵奇"), Tag("非H"), Tag("血腥暴力"), Tag("站長推薦"),
            },
        },
    };
}

/// <summary>
/// 站点分类目录（实测自 18comic /albums 分类页）。
/// </summary>
public static class CategoryCatalog
{
    public static List<CategoryNode> Roots { get; } = new()
    {
        new()
        {
            Name = "全部",
            Slug = "",
            HotTags = { "站長推薦", "巨乳", "口交", "無修正", "人妻", "NTR", "純愛", "百合", "熟女", "御姐", "全彩", "足交", "女僕", "催眠", "束縛", "調教" },
        },
        new()
        {
            Name = "單人",
            Slug = "single",
            HotTags = { "完結", "青年漫", "劇情向", "巨乳", "NTR", "純愛", "中文" },
        },
        new()
        {
            Name = "短篇",
            Slug = "short",
            HotTags = { "全彩", "無修正", "巨乳", "口交", "足交", "中文" },
        },
        new()
        {
            Name = "韓漫",
            Slug = "hanman",
            HotTags = { "韓漫", "全彩", "巨乳", "純愛", "NTR", "完結", "中文" },
        },
        new()
        {
            Name = "同人",
            Slug = "doujin",
            HotTags = { "CG集", "NTR", "全彩", "中文", "原神", "艦娘", "碧藍航線" },
            Children =
            {
                new() { Name = "CG圖集", Slug = "doujin/sub/CG", HotTags = { "CG集", "NTR", "全彩" } },
            },
        },
        new()
        {
            Name = "美漫",
            Slug = "meiman",
            HotTags = { "comic", "manhwa", "webtoon", "English" },
            Children =
            {
                new() { Name = "Comic", Slug = "meiman/sub/comic" },
                new() { Name = "Manhwa", Slug = "meiman/sub/manhwa" },
                new() { Name = "Other", Slug = "meiman/sub/other" },
                new() { Name = "18Scan", Slug = "meiman/sub/18scan" },
            },
        },
        new()
        {
            Name = "其他類",
            Slug = "another",
            HotTags = { "COS", "3D", "其他" },
            Children =
            {
                new() { Name = "Cosplay", Slug = "another/sub/cosplay" },
            },
        },
    };
}

namespace ResourceGrab.Core.Sources.Hitomi;

/// <summary>hitomi.la 站点常量（与 lanyeeee/hitomi-downloader 的 common.rs/search.rs 保持一致）。</summary>
public static class HitomiConstants
{
    /// <summary>API 与图床域名。</summary>
    public const string Domain = "ltn.gold-usergeneratedcontent.net";

    public const string Protocol = "https:";

    /// <summary>nozomi 列表文件扩展名（BigEndian Int32 序列）。</summary>
    public const string NozomiExtension = ".nozomi";

    /// <summary>nozomi 文件前缀目录。</summary>
    public const string CompressedNozomiPrefix = "n";

    /// <summary>galleriesindex 目录（版本号 / B-tree 索引）。</summary>
    public const string GalleryIndexDir = "galleriesindex";

    /// <summary>tagindex 目录（标签索引，当前只读版本号，实际搜索只用 galleriesindex）。</summary>
    public const string TagIndexDir = "tagindex";

    /// <summary>搜索建议域名（未使用，保留常量说明）。</summary>
    public const string TagIndexDomain = "tagindex.hitomi.la";

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

    /// <summary>图片防盗链 Referer。</summary>
    public const string Referer = "https://hitomi.la/";

    /// <summary>每页条数（与原版 PAGE_SIZE 一致）。</summary>
    public const int PageSize = 25;

    /// <summary>B-tree 节点字节数（MAX_NODE_SIZE）。</summary>
    public const long MaxNodeSize = 464;

    /// <summary>B-tree 子节点数上限。</summary>
    public const int B = 16;
}

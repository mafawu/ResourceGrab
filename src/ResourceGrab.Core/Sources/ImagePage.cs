namespace ResourceGrab.Core.Sources;

/// <summary>一张待下载的图片：URL + 请求所需 headers + 分块重组块数。</summary>
public class ImagePage
{
    public string Url { get; init; } = "";

    /// <summary>请求该图片时必须附加的 headers（User-Agent / Referer / Cookie 等），站点专属。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>图片被站点切成几块乱序存放，下载后需按此块数重组；0 表示未分块、原样保存。</summary>
    public uint BlockNum { get; init; }
}

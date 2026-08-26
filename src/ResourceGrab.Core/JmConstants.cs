namespace ResourceGrab.Core;

public static class JmConstants
{
    public const string AppTokenSecret = "18comicAPP";
    public const string AppTokenSecret2 = "18comicAPPContent";
    public const string AppDataSecret = "185Hcomic3PAPP7R";
    public const string AppVersion = "1.7.3";
    public const string ApiDomain = "www.cdngwc.cc";

    /// <summary>接口域名默认列表：请求失败时自动轮换到下一个可用域名。</summary>
    public static readonly string[] ApiDomains =
    {
        "www.cdngwc.cc",
        "www.cdngwc.net",
        "www.cdnhth.club",
        "www.cdngwc.club",
        "www.cdn-mspjmapiproxy.xyz",
    };

    public const string ImageDomain = "cdn-msp2.jmapiproxy2.cc";
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
}
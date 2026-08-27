namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M5/M6: 共享 HTTP 客户端工厂接口
// ---------------------------------------------------------------------------

public interface IVideoHttpClientFactory
{
    HttpClient Create(string sourceId);
}

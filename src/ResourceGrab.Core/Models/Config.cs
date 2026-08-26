using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

public class Config
{
    [JsonPropertyName("apiDomain")] public string ApiDomain { get; set; } = "";

    [JsonPropertyName("apiDomains")] public List<string> ApiDomains { get; set; } = new();
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("downloadDir")] public string DownloadDir { get; set; } = "";
    [JsonPropertyName("downloadFormat")] public DownloadFormat DownloadFormat { get; set; } = DownloadFormat.Jpeg;
    [JsonPropertyName("localDirs")] public List<string> LocalDirs { get; set; } = new();
    [JsonPropertyName("titleTranslate")] public TitleTranslateOptions TitleTranslate { get; set; } = new();
    [JsonPropertyName("readerScrollSpeed")] public double ReaderScrollSpeed { get; set; } = 1.0;
    [JsonPropertyName("videoScraping")] public VideoScrapeSettings? VideoScraping { get; set; }
}

public class TitleTranslateOptions
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "gpt-4o-mini";
}

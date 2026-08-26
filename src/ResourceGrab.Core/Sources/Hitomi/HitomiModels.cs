using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Sources.Hitomi;

/// <summary>画廊信息（对应原版 common.rs 的 GalleryInfo，字段名与站点返回一致）。</summary>
public class GalleryInfo
{
    /// <summary>站点返回的 id 可能是数字或字符串，宽容转换。</summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("japanese_title")] public string? JapaneseTitle { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("language_localname")] public string? LanguageLocalname { get; set; }
    [JsonPropertyName("type")] public string TypeField { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("artists")] public List<HitomiArtist>? Artists { get; set; }
    [JsonPropertyName("groups")] public List<HitomiGroup>? Groups { get; set; }
    [JsonPropertyName("parodys")] public List<HitomiParody>? Parodys { get; set; }
    [JsonPropertyName("tags")] public List<HitomiTag>? Tags { get; set; }
    [JsonPropertyName("related")] public List<int> Related { get; set; } = new();
    [JsonPropertyName("characters")] public List<HitomiCharacter>? Characters { get; set; }
    [JsonPropertyName("scene_indexes")] public List<int> SceneIndexes { get; set; } = new();
    [JsonPropertyName("files")] public List<GalleryFile> Files { get; set; } = new();
}

public class GalleryFile
{
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("haswebp")] public int HasWebp { get; set; }
    [JsonPropertyName("hasavif")] public int HasAvif { get; set; }
    [JsonPropertyName("hasjxl")] public int HasJxl { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("height")] public int Height { get; set; }
}

public class HitomiArtist
{
    [JsonPropertyName("artist")] public string Artist { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public class HitomiGroup
{
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public class HitomiParody
{
    [JsonPropertyName("parody")] public string Parody { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public class HitomiCharacter
{
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public class HitomiTag
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("female")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int Female { get; set; }

    [JsonPropertyName("male")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int Male { get; set; }
}
/// <summary>宽容整数转换：站点字段可能是数字或字符串（如 "id":"4116540"）。</summary>
public sealed class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var number) => number,
            JsonTokenType.String when int.TryParse(reader.GetString(), out var parsed) => parsed,
            _ => 0,
        };

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

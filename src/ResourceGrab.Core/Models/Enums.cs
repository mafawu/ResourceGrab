using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SearchSort
{
    Latest,
    View,
    Picture,
    Like,
}

public static class SearchSortExtensions
{
    public static string ToQueryString(this SearchSort sort) => sort switch
    {
        SearchSort.Latest => "mr",
        SearchSort.View => "mv",
        SearchSort.Picture => "mp",
        SearchSort.Like => "tf",
        _ => "mr",
    };
}

/// <summary>排行/浏览的周期维度（对应接口 o 参数后缀 t/w/m）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RankPeriod
{
    All,
    Today,
    Week,
    Month,
}

public static class RankPeriodExtensions
{
    public static string ToQueryString(this RankPeriod period) => period switch
    {
        RankPeriod.All => "",
        RankPeriod.Today => "t",
        RankPeriod.Week => "w",
        RankPeriod.Month => "m",
        _ => "",
    };

    /// <summary>排序 + 周期组合：o=mv（總）、mv_t（天）、mv_w（周）、mv_m（月）。</summary>
    public static string Combine(this SearchSort sort, RankPeriod period)
    {
        var periodCode = period.ToQueryString();
        return string.IsNullOrEmpty(periodCode) ? sort.ToQueryString() : $"{sort.ToQueryString()}_{periodCode}";
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FavoriteSort
{
    FavoriteTime,
    UpdateTime,
}

public static class FavoriteSortExtensions
{
    public static string ToQueryString(this FavoriteSort sort) => sort switch
    {
        FavoriteSort.FavoriteTime => "mr",
        FavoriteSort.UpdateTime => "mp",
        _ => "mr",
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadFormat
{
    Jpeg,
    Png,
    Webp,
}

public static class DownloadFormatExtensions
{
    public static string Extension(this DownloadFormat format) => format switch
    {
        DownloadFormat.Jpeg => "jpg",
        DownloadFormat.Png => "png",
        DownloadFormat.Webp => "webp",
        _ => "jpg",
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToggleType
{
    Add,
    Remove,
}

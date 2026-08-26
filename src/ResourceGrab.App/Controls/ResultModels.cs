using System.Windows.Input;
using ResourceGrab.App.Filtering;
namespace ResourceGrab.App.Controls;

/// <summary>列表页的标准状态。</summary>
public enum ResultStatus
{
    Idle,
    Loading,
    Ready,
    Empty,
    Error
}

/// <summary>携带数据的泛型状态容器。所有异步列表页必须使用此模型。</summary>
public sealed record ResultState<T>(
    ResultStatus Status,
    IReadOnlyList<T>? Items = null,
    string? EmptyTitle = null,
    string? EmptyMessage = null,
    string? ErrorMessage = null,
    ICommand? RetryCommand = null)
{
    public static ResultState<T> Idle() => new(ResultStatus.Idle);
    public static ResultState<T> Loading() => new(ResultStatus.Loading);
    public static ResultState<T> Ready(IReadOnlyList<T> items) => new(ResultStatus.Ready, Items: items);
    public static ResultState<T> Empty(string title = "暂无数据", string message = "")
        => new(ResultStatus.Empty, EmptyTitle: title, EmptyMessage: message);
    public static ResultState<T> Error(string message = "加载失败", ICommand? retry = null)
        => new(ResultStatus.Error, ErrorMessage: message, RetryCommand: retry);
}

/// <summary>分页请求模型。</summary>
public sealed record PageRequest(
    string Keyword = "",
    FilterSelection? Filters = null,
    int PageIndex = 0,
    int PageSize = 50);

/// <summary>分页结果模型。</summary>
public sealed record PageResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageIndex,
    int PageSize)
{
    public bool HasMore => (PageIndex + 1) * PageSize < TotalCount;
}


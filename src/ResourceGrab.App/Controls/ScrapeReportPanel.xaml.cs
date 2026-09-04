using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Services.VideoScrape;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 视频刮削报告面板：展示字段溯源和来源执行记录。
/// 嵌入 VideoView 的本地详情面板中。
/// </summary>
public partial class ScrapeReportPanel : UserControl
{
    private readonly ScrapeReportService _reportService;

    public ScrapeReportPanel()
    {
        InitializeComponent();
        _reportService = App.Services.GetRequiredService<ScrapeReportService>();
    }

    /// <summary>加载并显示指定 VideoItem 的刮削报告。</summary>
    public void LoadReport(VideoItem item)
    {
        Visibility = Visibility.Visible;
        DetailBorder.Visibility = Visibility.Collapsed;
        ExpandArrow.Text = "▸";

        if (string.IsNullOrEmpty(item.Id))
        {
            ShowEmpty("暂无刮削报告");
            return;
        }

        var report = _reportService.GetReport(item.Id);
        if (report is null)
        {
            ShowEmpty("暂无刮削报告");
            return;
        }

        // Summary
        var successCount = report.Attempts.Count(a => a.Outcome == VideoSourceOutcome.Success);
        var totalAttempts = report.Attempts.Count;
        var cacheHits = report.Attempts.Count(a => a.Outcome == VideoSourceOutcome.CacheHit);
        SummaryText.Text = $"来源 {successCount}/{totalAttempts} 成功" + (cacheHits > 0 ? $" · {cacheHits} 缓存命中" : "");

        // Field sources
        FieldSourceList.ItemsSource = report.FieldSources.Select(kv => new FieldSourceItem
        {
            FieldName = GetFieldDisplayName(kv.Key),
            SourceDisplay = GetSourceDisplayName(kv.Value),
        }).ToList();

        // Attempts
        AttemptList.ItemsSource = report.Attempts.Select(a => new AttemptItem
        {
            StatusIcon = a.Outcome switch
            {
                VideoSourceOutcome.Success => "✓",
                VideoSourceOutcome.CacheHit => "◆",
                VideoSourceOutcome.NoMatch => "○",
                VideoSourceOutcome.Blocked => "✗",
                VideoSourceOutcome.HttpError => "✗",
                VideoSourceOutcome.ParseError => "✗",
                VideoSourceOutcome.Disabled => "—",
                VideoSourceOutcome.Cancelled => "○",
                _ => "?"
            },
            SourceId = a.SourceId,
            OutcomeText = a.Outcome switch
            {
                VideoSourceOutcome.Success => "成功",
                VideoSourceOutcome.CacheHit => "缓存",
                VideoSourceOutcome.NoMatch => "未匹配",
                VideoSourceOutcome.Blocked => "被拦截",
                VideoSourceOutcome.HttpError => "网络错误",
                VideoSourceOutcome.ParseError => "解析失败",
                VideoSourceOutcome.Disabled => "已禁用",
                VideoSourceOutcome.Cancelled => "已取消",
                _ => a.Outcome.ToString()
            },
            OutcomeBrush = a.Outcome switch
            {
                VideoSourceOutcome.Success or VideoSourceOutcome.CacheHit
                    => (Brush)Application.Current.FindResource("PrimaryBrush"),
                VideoSourceOutcome.NoMatch or VideoSourceOutcome.Disabled or VideoSourceOutcome.Cancelled
                    => (Brush)Application.Current.FindResource("TextDisabledBrush"),
                _ => (Brush)Application.Current.FindResource("DangerBrush"),
            },
            StatusBrush = a.Outcome switch
            {
                VideoSourceOutcome.Success or VideoSourceOutcome.CacheHit
                    => (Brush)Application.Current.FindResource("PrimaryBrush"),
                VideoSourceOutcome.NoMatch
                    => (Brush)Application.Current.FindResource("TextSecondaryBrush"),
                _ => (Brush)Application.Current.FindResource("DangerBrush"),
            },
            Reason = a.Reason ?? "",
            ElapsedText = a.ElapsedMs > 0 ? $"{a.ElapsedMs}ms" : "",
        }).ToList();
    }

    public void HideReport()
    {
        Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty(string message)
    {
        FieldSourceList.ItemsSource = null;
        AttemptList.ItemsSource = null;
        SummaryText.Text = message;
    }

    private void ToggleExpand_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var isExpanded = DetailBorder.Visibility == Visibility.Visible;
        DetailBorder.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandArrow.Text = isExpanded ? "▸" : "▾";
    }

    private static string GetFieldDisplayName(string field) => field switch
    {
        "title" => "标题",
        "originalTitle" => "原题",
        "description" => "简介",
        "actors" => "演员",
        "tags" => "标签",
        "coverUrl" => "封面",
        "score" => "评分",
        "series" => "系列",
        "studio" => "制作商",
        "releaseDate" => "发行日期",
        _ => field,
    };

    private static string GetSourceDisplayName(string sourceIds)
    {
        var ids = sourceIds.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var names = ids.Select(id => id switch
        {
            "javbus" => "JavBus",
            "javdb" => "JavDB",
            "airav" => "AirAv",
            "dmm" => "DMM",
            "iqqtv" => "IQQTV",
            _ => id.ToUpperInvariant(),
        });
        return string.Join("+", names);
    }
}

/// <summary>字段溯源 UI 模型。</summary>
public sealed class FieldSourceItem
{
    public string FieldName { get; set; } = "";
    public string SourceDisplay { get; set; } = "";
}

/// <summary>来源执行记录 UI 模型。</summary>
public sealed class AttemptItem
{
    public string StatusIcon { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string OutcomeText { get; set; } = "";
    public Brush OutcomeBrush { get; set; } = Brushes.Gray;
    public Brush StatusBrush { get; set; } = Brushes.Gray;
    public string Reason { get; set; } = "";
    public string ElapsedText { get; set; } = "";
}

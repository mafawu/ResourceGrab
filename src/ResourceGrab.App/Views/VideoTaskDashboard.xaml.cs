using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Services.VideoScrape;

namespace ResourceGrab.App.Views;

/// <summary>
/// 视频刮削任务监控面板。
/// 展示当前任务队列的状态、每个任务的来源执行详情，支持取消/重试。
/// </summary>
public partial class VideoTaskDashboard : UserControl
{
    private VideoScrapeTaskQueue? _taskQueue;
    private VideoSourceHealthService? _health;
    private readonly ILogger? _logger;
    private System.Threading.Timer? _refreshTimer;
    /// <summary>用户手动收起明细的任务 Id；默认展开。</summary>
    private readonly HashSet<string> _collapsedRows = new(StringComparer.OrdinalIgnoreCase);

    public VideoTaskDashboard()
    {
        InitializeComponent();
        try { _logger = App.Services.GetRequiredService<ILogger>(); } catch { }
        try { _health = App.Services.GetRequiredService<VideoSourceHealthService>(); } catch { }
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 尝试获取任务队列
        try { _taskQueue = App.Services.GetRequiredService<VideoScrapeTaskQueue>(); } catch { }

        // 队列事件即时刷新；计时器仅兜底刷新运行耗时。
        if (_taskQueue != null) _taskQueue.ProgressChanged += OnQueueProgressChanged;
        _refreshTimer = new System.Threading.Timer(_ => Dispatcher.BeginInvoke(Refresh), null,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(800));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_taskQueue != null) _taskQueue.ProgressChanged -= OnQueueProgressChanged;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    private void OnQueueProgressChanged(VideoTaskProgress _) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        if (_taskQueue is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            TaskListPanel.Visibility = Visibility.Collapsed;
            TotalCountText.Text = "0";
            RunningText.Text = "0";
            PendingText.Text = "0";
            CompletedText.Text = "0";
            FailedText.Text = "0";
            SkippedText.Text = "0";
            return;
        }

        var progress = _taskQueue.GetProgress();
        var allTasks = _taskQueue.GetAllTasks().ToList();

        // Update stats
        TotalCountText.Text = allTasks.Count.ToString();
        RunningText.Text = progress.Running.ToString();
        PendingText.Text = progress.Pending.ToString();
        CompletedText.Text = progress.Completed.ToString();
        FailedText.Text = progress.Failed.ToString();
        SkippedText.Text = progress.Skipped.ToString();

        // Show/hide empty state
        EmptyState.Visibility = allTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskListPanel.Visibility = allTasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshHealth();

        // 行与任务按 Id 严格对应：集合变化才整列重建，否则原地更新。
        var ordered = allTasks.OrderByDescending(t => t.CreatedAt).ToList();
        var currentIds = TaskListPanel.Children.OfType<FrameworkElement>()
            .Select(e => e.Tag as string ?? "")
            .ToList();
        var targetIds = ordered.Select(t => t.Id).ToList();
        if (!currentIds.SequenceEqual(targetIds))
        {
            TaskListPanel.Children.Clear();
            foreach (var task in ordered)
            {
                TaskListPanel.Children.Add(BuildTaskRow(task));
            }
        }
        else
        {
            for (var i = 0; i < ordered.Count && i < TaskListPanel.Children.Count; i++)
            {
                UpdateTaskRow((FrameworkElement)TaskListPanel.Children[i], ordered[i]);
            }
        }
    }

    /// <summary>来源健康摘要：每源一行"成功/未匹配/被拦/网络错"，Blocked 占比高时提示需要代理或指纹。</summary>
    private void RefreshHealth()
    {
        var snapshot = _health?.GetSnapshot();
        if (snapshot is null || snapshot.Count == 0)
        {
            HealthPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var lines = snapshot.Take(8).Select(s =>
        {
            var blockedRatio = s.Total > 0 ? (double)s.Blocked / s.Total : 0;
            var hint = blockedRatio > 0.5 ? " ⚠ 建议配置代理/TLS指纹" : "";
            return $"{s.SourceId}: 成功{s.Success} 未匹配{s.NoMatch} 被拦{s.Blocked} 网络错{s.HttpError}{hint}";
        });
        HealthText.Text = string.Join("   |   ", lines);
        HealthPanel.Visibility = Visibility.Visible;
    }

    private FrameworkElement BuildTaskRow(VideoScrapeTask task)
    {
        // 外层容器：卡片样式
        var border = new Border
        {
            Background = (Brush)FindResource("HoverBgBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 4),
        };

        var outerStack = new StackPanel();

        // 第一行：状态指示 + 番号 + 类型标签 + 操作按钮
        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });  // 状态圆点
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); // 间距
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 番号+类型
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) }); // 操作按钮

        // 状态圆点
        var statusDot = new Border
        {
            Width = 8, Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 番号 + 类型
        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var numberText = new TextBlock
        {
            FontSize = 14, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var metaPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var typeText = new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var timeText = new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)FindResource("TextDisabledBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        metaPanel.Children.Add(typeText);
        metaPanel.Children.Add(timeText);
        infoPanel.Children.Add(numberText);
        infoPanel.Children.Add(metaPanel);

        // 操作按钮：handler 只在 BuildTaskRow 挂一次，动作按 Tag 里绑定的任务动态决定，
        // 避免 UpdateTaskRow 每次刷新重复订阅。
        var actionButton = new Button
        {
            Style = (Style)FindResource("GhostButtonStyle"),
            FontSize = 11, Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        actionButton.Click += (_, _) =>
        {
            if (actionButton.Tag is not VideoScrapeTask t) return;
            if (t.Status is VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry or VideoTaskStatus.Running)
                _taskQueue?.Cancel(t.Id);
            else if (t.Status == VideoTaskStatus.Failed)
                RetryTask(t);
        };

        Grid.SetColumn(statusDot, 0);
        Grid.SetColumn(infoPanel, 2);
        Grid.SetColumn(actionButton, 3);
        row1.Children.Add(statusDot);
        row1.Children.Add(infoPanel);
        row1.Children.Add(actionButton);

        outerStack.Children.Add(row1);

        // 第二行：批内进度与计数
        var progressRow = new Grid { Margin = new Thickness(20, 8, 0, 0) };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var progressBar = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 100,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var progressText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(progressBar, 0);
        Grid.SetColumn(progressText, 2);
        progressRow.Children.Add(progressBar);
        progressRow.Children.Add(progressText);
        outerStack.Children.Add(progressRow);

        // 第二行：错误信息或源尝试详情（可选）
        var detailText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 4, 0, 0),
            MaxWidth = 500,
        };
        outerStack.Children.Add(detailText);

        // 第三行：批内条目明细开关 + 虚拟化列表（最新在前）
        var detailToggle = new Button
        {
            Style = (Style)FindResource("GhostButtonStyle"),
            Content = "收起明细 ▴",
            FontSize = 11,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(20, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        detailToggle.Click += (_, _) =>
        {
            if (border.Tag is not string id) return;
            if (!_collapsedRows.Add(id))
            {
                _collapsedRows.Remove(id);
            }
            Refresh();
        };
        outerStack.Children.Add(detailToggle);

        var itemsList = new ListBox
        {
            MaxHeight = 180,
            FontSize = 11,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Margin = new Thickness(20, 2, 0, 0),
            Visibility = Visibility.Collapsed,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        System.Windows.Automation.AutomationProperties.SetName(itemsList, "TaskItemResults");
        ScrollViewer.SetVerticalScrollBarVisibility(itemsList, ScrollBarVisibility.Auto);
        outerStack.Children.Add(itemsList);

        border.Child = outerStack;
        border.Tag = task.Id;

        UpdateTaskRow(border, task);
        return border;
    }

    private void UpdateTaskRow(FrameworkElement row, VideoScrapeTask task)
    {
        if (row is not Border border || border.Child is not StackPanel outerStack) return;
        if (outerStack.Children.Count < 1) return;

        var row1 = outerStack.Children[0] as Grid;
        var progressRow = outerStack.Children.Count > 1 ? outerStack.Children[1] as Grid : null;
        var detailText = outerStack.Children.Count > 2 ? outerStack.Children[2] as TextBlock : null;
        var detailToggle = outerStack.Children.Count > 3 ? outerStack.Children[3] as Button : null;
        var itemsList = outerStack.Children.Count > 4 ? outerStack.Children[4] as ListBox : null;
        if (row1 is null) return;

        var statusDot = row1.Children[0] as Border;
        var infoPanel = row1.Children[1] as StackPanel;
        var actionButton = row1.Children[2] as Button;
        if (statusDot is null || infoPanel is null || actionButton is null) return;
        if (infoPanel.Children.Count < 2) return;
        var numberText = infoPanel.Children[0] as TextBlock;
        var metaPanel = infoPanel.Children[1] as StackPanel;
        if (numberText is null || metaPanel is null || metaPanel.Children.Count < 2) return;
        var typeText = metaPanel.Children[0] as TextBlock;
        var timeText = metaPanel.Children[1] as TextBlock;
        if (typeText is null || timeText is null) return;

        // 番号
        numberText.Text = task.Number;

        // 类型
        typeText.Text = task.Type switch
        {
            VideoTaskType.ScrapeVideo => "刮削",
            VideoTaskType.RescrapeVideo => "重刮削",
            VideoTaskType.ActorScrape => "演员",
            _ => task.Type.ToString(),
        };

        // 状态颜色和文字
        var (dotBrush, statusLabel) = task.Status switch
        {
            VideoTaskStatus.Pending => ((Brush)FindResource("TextDisabledBrush"), "等待中"),
            VideoTaskStatus.Running => ((Brush)FindResource("PrimaryBrush"), "刮削中"),
            VideoTaskStatus.WaitingRetry => ((Brush)FindResource("WarningBrush"), "重试中"),
            VideoTaskStatus.Completed => ((Brush)FindResource("SuccessBrush"), "完成"),
            VideoTaskStatus.Failed => ((Brush)FindResource("DangerBrush"), "失败"),
            VideoTaskStatus.Cancelled => ((Brush)FindResource("TextDisabledBrush"), "已取消"),
            VideoTaskStatus.Skipped => ((Brush)FindResource("TextDisabledBrush"), "跳过"),
            _ => ((Brush)FindResource("TextSecondaryBrush"), "未知"),
        };
        statusDot.Background = dotBrush;

        // 时间信息
        if (task.Status == VideoTaskStatus.Running && task.StartedAt.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - task.StartedAt.Value;
            timeText.Text = $"· {statusLabel} · {elapsed.TotalSeconds:F0}s";
            timeText.Foreground = (Brush)FindResource("PrimaryBrush");
        }
        else if (task.Status == VideoTaskStatus.Completed && task.CompletedAt.HasValue)
        {
            timeText.Text = $"· {statusLabel} · {task.CompletedAt.Value.ToLocalTime():HH:mm:ss}";
            timeText.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else if (task.Status == VideoTaskStatus.Failed)
        {
            timeText.Text = $"· {statusLabel} · 已尝试 {task.Attempt} 次";
            timeText.Foreground = (Brush)FindResource("DangerBrush");
        }
        else
        {
            timeText.Text = $"· {statusLabel}";
            timeText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }

        // 批内进度
        if (progressRow != null && progressRow.Children.Count >= 2)
        {
            if (progressRow.Children[0] is ProgressBar progressBar)
            {
                progressBar.Visibility = task.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
                progressBar.Value = task.Total > 0 ? task.Completed * 100.0 / task.Total : 0;
            }
            if (progressRow.Children[1] is TextBlock progressText)
            {
                progressText.Text = task.Total > 0
                    ? $"{task.Completed}/{task.Total} · 成功{task.SuccessCount} 未匹配{task.NoMatchCount} 失败{task.FailedCount} 跳过{task.SkippedCount}"
                    : "等待执行";
            }
        }

        // 详情行：错误信息或源尝试
        if (detailText is not null)
        {
            if (!string.IsNullOrEmpty(task.Error))
            {
                detailText.Text = task.Error;
                detailText.Foreground = (Brush)FindResource("DangerBrush");
                detailText.Visibility = Visibility.Visible;
            }
            else if (task.Attempts.Count > 0)
            {
                var attemptDetails = task.Attempts.TakeLast(3)
                    .Select(a => $"{a.SourceId}({a.Outcome}) {a.ElapsedMs}ms")
                    .ToList();
                detailText.Text = string.Join("  →  ", attemptDetails);
                detailText.Foreground = (Brush)FindResource("TextSecondaryBrush");
                detailText.Visibility = Visibility.Visible;
            }
            else if (task.Logs.Count > 0)
            {
                detailText.Text = string.Join("  ·  ", task.Logs.Take(3));
                detailText.Foreground = (Brush)FindResource("TextSecondaryBrush");
                detailText.Visibility = Visibility.Visible;
            }
            else
            {
                detailText.Visibility = Visibility.Collapsed;
            }
        }

        // 操作按钮：文字/可见性随状态变化，动作读 Tag 中的任务。
        actionButton.Tag = task;
        actionButton.Content = task.Status switch
        {
            VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry or VideoTaskStatus.Running => "取消",
            VideoTaskStatus.Failed => "重试",
            _ => "",
        };
        actionButton.Visibility = task.Status is VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry
            or VideoTaskStatus.Running or VideoTaskStatus.Failed
            ? Visibility.Visible : Visibility.Collapsed;

        // 条目明细：批内每条的命中来源/标题/耗时；展开状态记忆在 _collapsedRows。
        var itemResults = task.ItemResults;
        if (detailToggle is not null)
        {
            var expanded = itemResults.Count > 0 && !_collapsedRows.Contains(task.Id);
            detailToggle.Visibility = itemResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            detailToggle.Content = expanded ? "收起明细 ▴" : "展开明细 ▾";
        }
        if (itemsList is ListBox list)
        {
            var expanded = itemResults.Count > 0 && !_collapsedRows.Contains(task.Id);
            list.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (expanded)
            {
                // 仅当最新一条变化时重建，避免每次刷新重置滚动位置。
                var newest = itemResults[0];
                if (!ReferenceEquals(list.Tag, newest))
                {
                    list.Tag = newest;
                    list.ItemsSource = itemResults.Take(200).Select(FormatItemResult).ToList();
                    if (list.Items.Count > 0) list.ScrollIntoView(list.Items[0]);
                }
            }
            else
            {
                list.Tag = null;
            }
        }
    }

    private static string FormatItemResult(VideoTaskItemResult r)
    {
        var icon = r.Outcome switch { "成功" => "✓", "未匹配" => "△", "失败" => "✗", _ => "－" };
        var label = r.Number.Length > 0 ? r.Number : r.FileName;
        var title = r.Title.Length > 46 ? r.Title[..46] + "…" : r.Title;
        var body = r.Outcome switch
        {
            "成功" => string.Join(" · ", new[] { r.Sources, title, r.Detail, $"{r.ElapsedMs}ms" }.Where(s => !string.IsNullOrEmpty(s))),
            "跳过" => r.Detail,
            _ => string.IsNullOrEmpty(r.Detail) ? r.Outcome : $"{r.Outcome}：{r.Detail}",
        };
        return $"{r.At.ToLocalTime():HH:mm:ss}  {icon} {label}  {body}";
    }

    private void RetryTask(VideoScrapeTask task)
    {
        if (_taskQueue is null) return;
        if (task.ItemIds is { Count: > 0 } ids)
            _taskQueue.EnqueueBatchTask(ids, task.Type, task.Number);
        else
            _taskQueue.Enqueue(task.Number, task.Type, task.VideoItemId);
    }
    private void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (_taskQueue is null) return;
        var failed = _taskQueue.GetAllTasks()
            .Where(t => t.Status == VideoTaskStatus.Failed)
            .ToList();
        foreach (var task in failed)
            RetryTask(task);
        ToastService.Show($"已重新入队 {failed.Count} 个失败任务", ToastKind.Info);
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        _taskQueue?.CancelAll();
        ToastService.Show("已取消所有任务", ToastKind.Info);
    }
}

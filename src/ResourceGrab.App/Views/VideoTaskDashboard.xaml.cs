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
    private readonly ILogger? _logger;
    private System.Threading.Timer? _refreshTimer;

    public VideoTaskDashboard()
    {
        InitializeComponent();
        try { _logger = App.Services.GetRequiredService<ILogger>(); } catch { }
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 尝试获取任务队列
        try { _taskQueue = App.Services.GetRequiredService<VideoScrapeTaskQueue>(); } catch { }

        // 启动定时刷新
        _refreshTimer = new System.Threading.Timer(_ => Dispatcher.BeginInvoke(Refresh), null,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(800));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

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

        // Rebuild task list only if count changed
        if (TaskListPanel.Children.Count != allTasks.Count)
        {
            TaskListPanel.Children.Clear();
            foreach (var task in allTasks.OrderByDescending(t => t.CreatedAt))
            {
                TaskListPanel.Children.Add(BuildTaskRow(task));
            }
        }
        else
        {
            // Update existing rows
            var ordered = allTasks.OrderByDescending(t => t.CreatedAt).ToList();
            for (var i = 0; i < ordered.Count && i < TaskListPanel.Children.Count; i++)
            {
                UpdateTaskRow((FrameworkElement)TaskListPanel.Children[i], ordered[i]);
            }
        }
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

        // 操作按钮
        var actionButton = new Button
        {
            Style = (Style)FindResource("GhostButtonStyle"),
            FontSize = 11, Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        Grid.SetColumn(statusDot, 0);
        Grid.SetColumn(infoPanel, 2);
        Grid.SetColumn(actionButton, 3);
        row1.Children.Add(statusDot);
        row1.Children.Add(infoPanel);
        row1.Children.Add(actionButton);

        outerStack.Children.Add(row1);

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
        var detailText = outerStack.Children.Count > 1 ? outerStack.Children[1] as TextBlock : null;
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
            else
            {
                detailText.Visibility = Visibility.Collapsed;
            }
        }

        // 操作按钮
        actionButton.Content = task.Status switch
        {
            VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry or VideoTaskStatus.Running => "取消",
            VideoTaskStatus.Failed => "重试",
            _ => "",
        };
        actionButton.Visibility = task.Status is VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry
            or VideoTaskStatus.Running or VideoTaskStatus.Failed
            ? Visibility.Visible : Visibility.Collapsed;

        // 避免重复注册
        actionButton.Click -= OnTaskAction;
        if (task.Status is VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry or VideoTaskStatus.Running)
            actionButton.Click += (_, _) => _taskQueue?.Cancel(task.Id);
        else if (task.Status == VideoTaskStatus.Failed)
            actionButton.Click += (_, _) => _taskQueue?.Enqueue(task.Number, task.Type, task.VideoItemId);
    }

    private void OnTaskAction(object sender, RoutedEventArgs e) { }
    private void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (_taskQueue is null) return;
        var failed = _taskQueue.GetAllTasks()
            .Where(t => t.Status == VideoTaskStatus.Failed)
            .ToList();
        foreach (var task in failed)
        {
            _taskQueue.Enqueue(task.Number, task.Type, task.VideoItemId);
        }
        ToastService.Show($"已重新入队 {failed.Count} 个失败任务", ToastKind.Info);
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        _taskQueue?.CancelAll();
        ToastService.Show("已取消所有任务", ToastKind.Info);
    }
}
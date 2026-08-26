using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ResourceGrab.App.Common;

/// <summary>
/// TextBlock 附加属性：把文本限制在指定行数内并显示省略号。
/// WPF 的 TextTrimming 在 TextWrapping="Wrap" 时对高度溢出（MaxHeight）不生效——
/// 文本会被硬截断且不显示省略号；这里改用 FormattedText 实测换行高度，
/// 二分查找能放入 N 行的最长前缀并补 "…"。
/// 用法：TextWrapping="Wrap" common:TextBlockExt.MaxLines="2"（不要再设 MaxHeight / TextTrimming）。
/// 通过 SetCurrentValue 写入，不破坏原有数据绑定；绑定源变化或尺寸变化时会自动重算。
/// </summary>
public static class TextBlockExt
{
    /// <summary>允许显示的最大行数（&lt;=0 表示不限制）。</summary>
    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.RegisterAttached(
        "MaxLines", typeof(int), typeof(TextBlockExt),
        new PropertyMetadata(0, OnMaxLinesChanged));

    public static void SetMaxLines(DependencyObject element, int value) => element.SetValue(MaxLinesProperty, value);

    public static int GetMaxLines(DependencyObject element) => (int)element.GetValue(MaxLinesProperty);

    private sealed class State
    {
        public string Original = "";
        public string? LastSet;
        public double LastWidth = -1;
    }

    private static readonly ConditionalWeakTable<TextBlock, State> States = new();

    private static void OnMaxLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb)
        {
            return;
        }
        if ((int)e.OldValue > 0)
        {
            tb.LayoutUpdated -= OnLayoutUpdated;
            tb.Loaded -= OnLoaded;
            tb.Unloaded -= OnUnloaded;
            States.Remove(tb);
        }
        if ((int)e.NewValue > 0)
        {
            tb.Loaded += OnLoaded;
            tb.Unloaded += OnUnloaded;
            if (tb.IsLoaded)
            {
                tb.LayoutUpdated += OnLayoutUpdated;
            }
            States.Remove(tb); // 清掉旧状态，重新按当前文本/尺寸计算
        }
    }

    /// <summary>元素进入视觉树后订阅 LayoutUpdated（虚拟化回收再挂载时也能重新生效）。</summary>
    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb)
        {
            tb.LayoutUpdated += OnLayoutUpdated;
        }
    }

    /// <summary>元素脱离视觉树（虚拟化回收/模板销毁）时退订，避免 WPF 布局更新事件列表堆积。</summary>
    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb)
        {
            tb.LayoutUpdated -= OnLayoutUpdated;
            States.Remove(tb);
        }
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        // WPF 在元素脱离视觉树（如虚拟化回收容器、模板切换销毁）时，
        // 可能以 null 作为 sender 回调 LayoutUpdated，必须防御
        if (sender is not TextBlock tb)
        {
            return;
        }
        var maxLines = GetMaxLines(tb);
        if (maxLines <= 0 || tb.ActualWidth <= 0)
        {
            return;
        }

        var state = States.GetOrCreateValue(tb);

        // 快速路径：尺寸与当前显示文本都没变，无需重算
        if (state.LastWidth == tb.ActualWidth && state.LastSet is { } last && tb.Text == last)
        {
            return;
        }

        // Text 被外部（绑定/代码）重新赋值 → 以当前文本为原始文本重新截断
        if (tb.Text != state.LastSet)
        {
            state.Original = tb.Text;
        }

        var width = Math.Max(1, tb.ActualWidth - 2);
        var lineHeight = MeasureLineHeight(tb);
        if (lineHeight <= 0)
        {
            return;
        }
        var maxHeight = lineHeight * maxLines;

        string target;
        if (Fits(tb, state.Original, width, maxHeight))
        {
            target = state.Original;
        }
        else
        {
            target = Truncate(tb, state.Original, width, maxHeight);
        }

        if (tb.Text != target)
        {
            // SetCurrentValue：不破坏绑定，绑定源后续更新仍能覆盖我们写入的截断值
            tb.SetCurrentValue(TextBlock.TextProperty, target);
        }
        state.LastSet = target;
        state.LastWidth = tb.ActualWidth;
    }

    private static double MeasureLineHeight(TextBlock tb)
        => Build(tb, "Ag", 0).Height;

    private static bool Fits(TextBlock tb, string text, double width, double maxHeight)
        => Build(tb, text, width).Height <= maxHeight + 0.5;

    private static string Truncate(TextBlock tb, string text, double width, double maxHeight)
    {
        const string ellipsis = "…";
        if (text.Length <= 1)
        {
            return ellipsis;
        }

        // 二分查找「前缀 + 省略号」能放入 N 行的最长前缀
        int lo = 1, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Fits(tb, Prefix(text, mid) + ellipsis, width, maxHeight))
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return Prefix(text, lo) + ellipsis;
    }

    /// <summary>取前缀并避免截断代理对（emoji 等）。</summary>
    private static string Prefix(string text, int length)
    {
        length = Math.Min(length, text.Length);
        if (length > 0 && char.IsLowSurrogate(text[length - 1]))
        {
            length--;
        }
        return text.Substring(0, length);
    }

    private static FormattedText Build(TextBlock tb, string text, double width)
    {
        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            tb.FlowDirection,
            typeface,
            tb.FontSize,
            tb.Foreground ?? Brushes.Black,
            VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        if (width > 0)
        {
            ft.MaxTextWidth = width;
        }
        return ft;
    }
}

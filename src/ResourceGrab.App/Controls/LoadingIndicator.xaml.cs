using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 全应用统一等待样式：旋转环（品牌主色）+ 主文本 + 可选副文本。
/// 替换：States 骨架屏、手写"加载中…"、播放器加载条、在线搜索不定条（确定性进度条不动）。
/// </summary>
public partial class LoadingIndicator : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LoadingIndicator),
        new PropertyMetadata("加载中…"));

    public static readonly DependencyProperty SubTextProperty = DependencyProperty.Register(
        nameof(SubText), typeof(string), typeof(LoadingIndicator),
        new PropertyMetadata(""));

    public static readonly DependencyProperty RingSizeProperty = DependencyProperty.Register(
        nameof(RingSize), typeof(double), typeof(LoadingIndicator),
        new PropertyMetadata(36.0));

    /// <summary>主文本（代码可改，如"搜索中…"）。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>副文本（为空自动隐藏，如"正在同步收藏夹，稍候片刻"）。</summary>
    public string SubText
    {
        get => (string)GetValue(SubTextProperty);
        set => SetValue(SubTextProperty, value);
    }

    /// <summary>旋转环直径。</summary>
    public double RingSize
    {
        get => (double)GetValue(RingSizeProperty);
        set => SetValue(RingSizeProperty, value);
    }

    public LoadingIndicator()
    {
        InitializeComponent();
        // 文本默认跟随次级文本色，深色遮罩上由调用方显式覆盖 Foreground
        if (ReadLocalValue(ForegroundProperty) == DependencyProperty.UnsetValue)
            SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
    }
}

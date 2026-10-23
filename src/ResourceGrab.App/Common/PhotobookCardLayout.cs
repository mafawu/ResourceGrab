using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.Controls;

namespace ResourceGrab.App.Common;

/// <summary>
/// 写真图集卡片的自适应尺寸计算。
/// <para>
/// 卡片网格用 <see cref="WrapPanel"/> 承载（两处写真页都是），这里按容器可用宽度算出列数与
/// 卡片宽度，使每行正好铺满可用宽度，窗口拉伸/收缩时卡片同比放大缩小。
/// </para>
/// <para>
/// 尺寸策略与漫画/视频侧的卡片刻意不同：那边是「多而小」的缩略图墙（最小宽 90、最多 8 列），
/// 写真是一张一册的竖版海报，按「少而大」取舍 —— 最小宽 160、上限 260，
/// 宽屏时优先增加列数而不是把单张继续拉大。
/// </para>
/// </summary>
internal static class PhotobookCardLayout
{
    /// <summary>卡片最小宽度。比全局网格的 90 大得多，否则默认窗口下会挤成缩略图。</summary>
    public const double MinCardWidth = 160;

    /// <summary>卡片最大宽度。超过它就改为增加列数，避免宽屏下单张海报大得占满整屏。</summary>
    public const double MaxCardWidth = 260;

    /// <summary>布局完成前的初始卡片宽度（约等于默认窗口下的实际宽度）。</summary>
    public const double DefaultCardWidth = 200;

    /// <summary>
    /// 封面框高宽比 = 高 / 宽。竖版海报实测高宽比中位 1.5（3:2 竖幅），另有 4:3 竖幅。
    /// <para>
    /// 封面用 <c>Stretch=UniformToFill</c> 铺满这个框（超出裁切），所以这个比例决定了
    /// 「一张标准竖版海报被完整显示」的基准：等于 1.5 的封面零裁切，
    /// 4:3 竖幅（1.333）裁掉约 11% 宽度，横幅则裁得更多。
    /// </para>
    /// <para>
    /// 注意不能用 <c>Uniform</c>：写真封面宽高比并不统一（实测分布 1.33~1.6，还有竖向拼图这类
    /// 极端比例），Uniform 会把偏高的封面缩成窄条居中，卡片两侧留下大片空白（10-07 实际踩到）。
    /// </para>
    /// </summary>
    public const double CoverRatio = 1.5;

    /// <summary>
    /// 铺满一侧的安全余量。<see cref="WrapPanel"/> 的换行条件是「已排宽度 + 本张卡片宽 &gt;
    /// 可用宽度」，只要我们对可用宽度的估算比真实值大哪怕半个像素，每行就会少排一张卡片。
    /// </summary>
    public const double FitSafetyMargin = 1;

    /// <summary>网格横向间距，沿用全局定义。</summary>
    private const double Spacing = GridCellSizer.Spacing;

    /// <summary>
    /// 卡片容器（直接承载卡片的那层 <see cref="WrapPanel"/>）当前的可用横向宽度；尚未完成布局时返回 0。
    /// <para>
    /// <b>必须量容器自身的 ActualWidth，不能拿外层 <c>ScrollViewer.ActualWidth</c> 去减一个
    /// 固定滚动条宽度。</b>那样得到的值总比真实可用宽度大一点（滚动条占位会随主题/样式/是否显示而变化，
    /// 实测本机与预期能差出 7~10px），而 <see cref="WrapPanel"/> 是「超出即换行」的严格布局：
    /// 差一点点就会让每行都放不下第二张卡，整片网格退化成单列、右侧留下大片空白
    /// （10-07 的实际现象，已用离屏布局探针复现）。
    /// </para>
    /// <para>
    /// 这层 WrapPanel 被 ScrollViewer 的视口左右拉伸，它的 ActualWidth 就是真实视口宽度
    /// （已自动扣掉滚动条占位），且与卡片尺寸无关，不会形成「卡片变大 → 容器变大」的反馈环。
    /// </para>
    /// </summary>
    public static double AvailableWidth(FrameworkElement cardsHost)
    {
        var width = cardsHost.ActualWidth;
        if (width <= 0)
        {
            return 0;
        }

        var available = width - FitSafetyMargin;
        return available > 0 ? available : width;
    }

    /// <summary>封面高度。</summary>
    public static double CoverHeight(double cardWidth) => cardWidth * CoverRatio;

    /// <summary>按可用宽度算出列数与卡片宽度（各列正好铺满可用宽度）——写真图集卡片尺寸。</summary>
    public static (int Columns, double CardWidth) Compute(double availableWidth)
        => Compute(availableWidth, MinCardWidth, MaxCardWidth, DefaultCardWidth);

    /// <summary>
    /// 按可用宽度与给定的宽度区间算出列数与单元宽度（各列正好铺满可用宽度）。
    /// <para>
    /// 在线阅读器的「画墙」模式复用了这里的同一套数学：它的贴片比图集卡片小得多，
    /// 但「量容器自身宽度 + 末尾安全余量 + 反算后向下取整 0.5px」这几点完全一致，
    /// 换成各写一份迟早会踩到 WrapPanel 少排一列的坑。
    /// </para>
    /// </summary>
    /// <param name="availableWidth">容器可用横向宽度，须由 <see cref="AvailableWidth"/> 取得。</param>
    /// <param name="minWidth">单元最小宽度。</param>
    /// <param name="maxWidth">单元最大宽度；超过它就改为增加列数。</param>
    /// <param name="fallbackWidth">宽度尚未就绪时的兜底宽度。</param>
    public static (int Columns, double CardWidth) Compute(
        double availableWidth, double minWidth, double maxWidth, double fallbackWidth)
    {
        if (availableWidth <= 0 || maxWidth <= 0 || minWidth <= 0)
        {
            return (GridCellSizer.DefaultColumns, fallbackWidth);
        }

        // 列数越少，单张越大。取「不超过最大宽度」所需的最少列数（单元尽量大），
        // 同时不超过「不小于最小宽度」允许的列数上限。
        var columns = (int)Math.Ceiling((availableWidth + Spacing) / (maxWidth + Spacing));
        var fit = (int)Math.Floor((availableWidth + Spacing) / (minWidth + Spacing));
        columns = fit >= 1 ? Math.Clamp(columns, 1, fit) : Math.Max(1, columns);

        // 由「列数 × 单元宽 + (列数 - 1) × 间距 = 可用宽度」反算单元宽。
        // 向下取整到 0.5 像素，避免亚像素累计导致末列被挤到下一行。
        var cardWidth = Math.Floor((availableWidth - Spacing * (columns - 1)) / columns * 2) / 2;

        // 由上面的列数约束可推得结果必落在 [minWidth, maxWidth] 内，此处仅兜底
        return (columns, Math.Clamp(cardWidth, minWidth, maxWidth));
    }

    /// <summary>
    /// 把网格里已有的卡片就地重排到新尺寸（只改尺寸，不重新加载封面）。
    /// 返回本次使用的卡片宽度。
    /// </summary>
    public static double Apply(WrapPanel host, double availableWidth)
    {
        var (columns, cardWidth) = Compute(availableWidth);
        var coverHeight = CoverHeight(cardWidth);

        for (var i = 0; i < host.Children.Count; i++)
        {
            if (host.Children[i] is not Border { Child: StackPanel stack } card)
            {
                continue;
            }

            // 末列去掉右间距，否则整行会比可用宽度多出一个间距而被迫换行
            var isRowEnd = (i + 1) % columns == 0;
            card.Width = cardWidth;
            card.Margin = new Thickness(0, 0, isRowEnd ? 0 : Spacing, Spacing);

            if (stack.Children.Count > 0
                && stack.Children[0] is Grid { Tag: "PhotobookCoverHost" } coverHost)
            {
                coverHost.Width = cardWidth;
                coverHost.Height = coverHeight;
            }
        }

        return cardWidth;
    }
}

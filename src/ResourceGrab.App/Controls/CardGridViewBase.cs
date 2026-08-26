using System.Windows;
using System.Windows.Controls;

namespace ResourceGrab.App.Controls;

public class CardGridViewBase : UserControl
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CardGridViewBase),
        new PropertyMetadata(GridCellSizer.DefaultColumns, (d, _) => ((CardGridViewBase)d).UpdateCellSize()));

    [Obsolete("Use Columns instead")]
    public static readonly DependencyProperty CellScaleProperty = DependencyProperty.Register(
        nameof(CellScale), typeof(double), typeof(CardGridViewBase),
        new PropertyMetadata(1.0, (d, e) =>
        {
            var self = (CardGridViewBase)d;
            var scale = (double)e.NewValue;
            var cols = (int)Math.Round(GridCellSizer.DefaultColumns / scale);
            cols = Math.Clamp(cols, GridCellSizer.MinColumns, GridCellSizer.MaxColumns);
            if (self.Columns != cols) self.Columns = cols;
            else self.UpdateCellSize();
        }));

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(CardGridViewBase), new PropertyMetadata(168.0));

    public static readonly DependencyProperty SlotWidthProperty = DependencyProperty.Register(
        nameof(SlotWidth), typeof(double), typeof(CardGridViewBase), new PropertyMetadata(182.0));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    [Obsolete("Use Columns instead")]
    public double CellScale
    {
        get => (double)GetValue(CellScaleProperty);
        set => SetValue(CellScaleProperty, value);
    }

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    public double SlotWidth
    {
        get => (double)GetValue(SlotWidthProperty);
        set => SetValue(SlotWidthProperty, value);
    }

    protected CardGridViewBase()
    {
        SizeChanged += (_, _) => UpdateCellSize();
        Loaded += (_, _) => UpdateCellSize();
    }

    protected virtual void UpdateCellSize()
    {
        if (ActualWidth <= 0)
            return;
        const double scrollbarReserve = 16;
        var available = ActualWidth > scrollbarReserve ? ActualWidth - scrollbarReserve : ActualWidth;
        var (cardWidth, slotWidth) = GridCellSizer.ComputeByColumns(Columns, available);
        CardWidth = cardWidth;
        SlotWidth = slotWidth;
    }
}
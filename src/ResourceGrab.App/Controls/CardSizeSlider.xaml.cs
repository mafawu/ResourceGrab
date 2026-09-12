using System.Windows;
using System.Windows.Controls;

namespace ResourceGrab.App.Controls;

public partial class CardSizeSlider : UserControl
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CardSizeSlider),
        new FrameworkPropertyMetadata(GridCellSizer.DefaultColumns, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((CardSizeSlider)d).OnColumnsChanged()));

    [Obsolete("Use Columns instead")]
    public static readonly DependencyProperty CellScaleProperty = DependencyProperty.Register(
        nameof(CellScale), typeof(double), typeof(CardSizeSlider),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) =>
            {
                var self = (CardSizeSlider)d;
                var scale = (double)e.NewValue;
                var cols = (int)Math.Round(GridCellSizer.DefaultColumns / scale);
                cols = Math.Clamp(cols, GridCellSizer.MinColumns, GridCellSizer.MaxColumns);
                if (self.Columns != cols) self.Columns = cols;
            }));

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

    private bool _syncing;
    public event Action<int>? ColumnsChanged;

    public CardSizeSlider()
    {
        InitializeComponent();
    }

    private void OnColumnsChanged()
    {
        if (_syncing) return;
        // 越界值（如未绑定时的 0）钳回有效区间，避免显示"0个/行"之类的无效态
        var clamped = Math.Clamp(Columns, GridCellSizer.MinColumns, GridCellSizer.MaxColumns);
        if (clamped != Columns)
        {
            Columns = clamped;
            return;
        }
        _syncing = true;
        if (ScaleSlider is not null)
            ScaleSlider.Value = Columns;
        if (ScaleText is not null)
            ScaleText.Text = $"{Columns} 个/行";
        _syncing = false;
        try { ColumnsChanged?.Invoke(Columns); } catch {}
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        Columns = (int)Math.Round(ScaleSlider.Value);
    }
}

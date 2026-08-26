using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ResourceGrab.App.Views;

public partial class NovelSearchPanel : UserControl
{
    private Dictionary<string, List<string>> _tree = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string,int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _included = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string, IReadOnlyCollection<string>, IReadOnlyCollection<string>>? FilterChanged;
    public NovelSearchPanel() { InitializeComponent(); UpdateUi(); }

    public void SetData(Dictionary<string, List<string>> tree, IReadOnlyDictionary<string,int> counts)
    {
        try
        {
            _tree = new Dictionary<string, List<string>>(tree ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
            _counts = new Dictionary<string,int>(counts ?? new Dictionary<string,int>(), StringComparer.OrdinalIgnoreCase);
            Rebuild();
        }
        catch { }
    }

    private void Rebuild()
    {
        try
        {
            TreeHost.Children.Clear();
            if (_tree.Count==0)
            {
                TreeHost.Children.Add(new TextBlock{ Text="暂无标签", Foreground=TryFindBrush("TextDisabledBrush")});
                return;
            }
            foreach (var kv in _tree.OrderBy(k=>k.Key))
            {
                var top = kv.Key;
                var subs = kv.Value ?? new List<string>();
                var header = new Grid { Margin = new Thickness(0,0,0,6) };
                header.ColumnDefinitions.Add(new ColumnDefinition{ Width = GridLength.Auto});
                header.ColumnDefinitions.Add(new ColumnDefinition{ Width = new GridLength(1, GridUnitType.Star)});
                var title = new TextBlock { Text = top, FontSize=11, FontWeight=FontWeights.SemiBold, Foreground=TryFindBrush("TextSecondaryBrush"), VerticalAlignment=VerticalAlignment.Center };
                Grid.SetColumn(title,0); header.Children.Add(title);
                if (_counts.TryGetValue(top, out var c)) {
                    var cnt = new TextBlock { Text = $"({c})", FontSize=10.5, Foreground=TryFindBrush("TextDisabledBrush"), Margin=new Thickness(6,0,0,0), VerticalAlignment=VerticalAlignment.Center };
                    Grid.SetColumn(cnt,1); header.Children.Add(cnt);
                }
                TreeHost.Children.Add(header);
                var wrap = new WrapPanel();
                wrap.Children.Add(MakeButton(top));
                foreach (var sub in subs.OrderBy(s=>s))
                {
                    var name = sub.Split("/").Last();
                    wrap.Children.Add(MakeButton(sub, name));
                }
                TreeHost.Children.Add(wrap);
                TreeHost.Children.Add(new Border{ Height=1, Background=TryFindBrush("DividerBrush"), Margin=new Thickness(0,10,0,10)});
            }
        }
        catch { }
    }

    private Brush TryFindBrush(string key)
    {
        try { if (TryFindResource(key) is Brush b) return b; } catch { }
        return Brushes.Gray;
    }

    private ToggleButton MakeButton(string fullTag, string? display=null)
    {
        var name = display ?? fullTag.Split("/").Last();
        var count = _counts.TryGetValue(fullTag, out var v) ? v : 0;
        var isInc = _included.Contains(fullTag);
        var isExc = _excluded.Contains(fullTag);
        Style? style = null;
        try { style = TryFindResource("LocalTagToggleStyle") as Style; } catch { }
        var btn = new ToggleButton
        {
            Content = $"{(isInc?"✓ ":isExc?"∅ ":"")}{name} {(count>0?$"({count})":"")}",
            IsChecked = isInc,
            Tag = isExc?"excluded":null,
            Style = style,
            Margin=new Thickness(0,0,6,6),
            ToolTip = fullTag
        };
        if (isExc) btn.IsChecked=false;
        btn.Click += (_,_)=>{
            try{
                if (_excluded.Contains(fullTag)) _excluded.Remove(fullTag);
                if (_included.Contains(fullTag)) _included.Remove(fullTag); else _included.Add(fullTag);
                Rebuild(); Notify();
            }catch{}
        };
        btn.MouseRightButtonUp += (_,e)=>{
            try{
                if (_included.Contains(fullTag)) _included.Remove(fullTag);
                if (_excluded.Contains(fullTag)) _excluded.Remove(fullTag); else _excluded.Add(fullTag);
                Rebuild(); Notify(); e.Handled=true;
            }catch{}
        };
        return btn;
    }

    private void KeywordBox_TextChanged(object sender, TextChangedEventArgs e){ UpdateUi(); Notify(); }
    private void ClearButton_Click(object sender, RoutedEventArgs e){ try{ KeywordBox.Clear(); }catch{} _included.Clear(); _excluded.Clear(); Rebuild(); UpdateUi(); Notify(); }
    private void KeywordBox_GotFocus(object sender, RoutedEventArgs e){ UpdateUi(); }
    private void KeywordBox_LostFocus(object sender, RoutedEventArgs e)=> UpdateUi();
    private void UpdateUi(){
        try{
            var hasText = !string.IsNullOrEmpty(KeywordBox.Text);
            PlaceholderText.Visibility = hasText || KeywordBox.IsKeyboardFocused ? Visibility.Collapsed : Visibility.Visible;
            var hasFilter = _included.Count>0 || _excluded.Count>0;
            ClearButton.Visibility = hasText || hasFilter ? Visibility.Visible : Visibility.Collapsed;
        }catch{}
    }
    private void Notify(){
        try{ FilterChanged?.Invoke(KeywordBox.Text?.Trim() ?? "", _included.ToList(), _excluded.ToList()); }catch{}
    }
}

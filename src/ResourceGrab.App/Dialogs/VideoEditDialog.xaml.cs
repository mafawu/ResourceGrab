using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ResourceGrab.App.Controls;
using ResourceGrab.Core.Models;

namespace ResourceGrab.App.Dialogs;

/// <summary>
/// 视频元数据编辑表单：名称/描述/系列/导演/标签/演员/声优/制作组/评分/备注。
/// ShowDialog 返回 true 表示已就地更新 folder 对象（持久化由调用方负责）。
/// </summary>
public partial class VideoEditDialog : Window
{
    private readonly VideoFolder _folder;
    public VideoFolder Folder => _folder;
    private int _rating;

    public VideoEditDialog(VideoFolder folder)
    {
        InitializeComponent();
        _folder = folder;
        LoadForm(folder);
    }

    private void LoadForm(VideoFolder folder)
    {
        HeaderText.Text = $"编辑视频信息 · {folder.Name}";
        NameBox.Text = folder.Name;
        SeriesBox.Text = folder.Series;
        DirectorBox.Text = folder.Director;
        DescriptionBox.Text = folder.Description;
        NotesBox.Text = folder.Notes;

        TagsEditor.Tags = new ObservableCollection<string>(folder.Tags);
        ActorsEditor.Tags = new ObservableCollection<string>(folder.Actors);
        VoiceActorsEditor.Tags = new ObservableCollection<string>(folder.VoiceActors);
        ProductionTeamEditor.Tags = new ObservableCollection<string>(folder.ProductionTeam);

        SetRating(folder.Rating);
    }

    // ====================== 评分 ======================

    private void SetRating(int rating)
    {
        _rating = Math.Clamp(rating, 0, 5);
        RatingStars.Children.Clear();
        for (var i = 1; i <= 5; i++)
        {
            var star = new TextBlock
            {
                Text = i <= _rating ? "★" : "☆",
                FontSize = 20,
                Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = i <= _rating
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00))
                    : (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 0, 3, 0),
            };
            var value = i;
            star.MouseLeftButtonUp += (_, _) => SetRating(value == _rating ? value - 1 : value);
            RatingStars.Children.Add(star);
        }
    }

    private void RatingClear_Click(object sender, MouseButtonEventArgs e) => SetRating(0);

    // ====================== 保存 / 关闭 ======================

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        _folder.Name = name.Length > 0 ? name : "(未命名)";
        _folder.Series = SeriesBox.Text.Trim();
        _folder.Director = DirectorBox.Text.Trim();
        _folder.Description = DescriptionBox.Text.Trim();
        _folder.Notes = NotesBox.Text.Trim();
        _folder.Tags = TagsEditor.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _folder.Actors = ActorsEditor.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _folder.VoiceActors = VoiceActorsEditor.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _folder.ProductionTeam = ProductionTeamEditor.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _folder.Rating = _rating;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.ButtonState == MouseButtonState.Pressed)
        {
            // 避免在文本框内拖拽误触发移动窗口
            if (e.OriginalSource is DependencyObject d && IsWithinInput(d)) return;
            DragMove();
        }

        static bool IsWithinInput(DependencyObject node)
        {
            while (node is not null)
            {
                if (node is TextBox) return true;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return false;
        }
    }
}

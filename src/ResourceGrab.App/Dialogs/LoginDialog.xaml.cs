using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Errors;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Dialogs;

/// <summary>模态登录对话框：账号 + 密码，成功后关闭。</summary>
public partial class LoginDialog : Window
{
    private readonly SessionService _session;

    public LoginDialog()
    {
        InitializeComponent();
        _session = App.Services.GetRequiredService<SessionService>();
        Loaded += (_, _) => UsernameBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmWindowCorner.Apply(this);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = TryLoginAsync();
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) => await TryLoginAsync();

    private async Task TryLoginAsync()
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            ShowError("请输入账号和密码");
            return;
        }

        SetBusy(true);
        ErrorCapsule.Visibility = Visibility.Collapsed;
        try
        {
            await _session.LoginAsync(username, password);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(JmErrorClassifier.Classify(ex).Message);
            SetBusy(false);
        }
    }

    /// <summary>切换登录忙碌态：仅在此状态下启动/停止旋转动画，避免对话框闲置时无限循环动画空转。</summary>
    private void SetBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        BusyIcon.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        NormalText.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (busy)
        {
            BusySpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            // 传 null 停止动画，Angle 恢复默认值
            BusySpin.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorCapsule.Visibility = Visibility.Visible;
    }
}


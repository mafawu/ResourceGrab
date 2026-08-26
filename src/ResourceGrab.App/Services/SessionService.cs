using System.Windows;
using System.Windows.Threading;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Http;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Services;

/// <summary>登录会话：登录/登出/启动时静默恢复，均回到 UI 线程通知。</summary>
public class SessionService : ObservableObject
{
    private readonly JmHttpClient _client;
    private readonly ConfigService _config;
    private readonly Dispatcher _dispatcher;

    public SessionService(JmHttpClient client, ConfigService config)
    {
        _client = client;
        _config = config;
        _dispatcher = Application.Current.Dispatcher;
    }

    public bool IsLoggedIn { get; private set; }

    public string? Username { get; private set; }

    /// <summary>账号密码登录；成功后保存凭据到配置并通知 UI。</summary>
    public async Task<bool> LoginAsync(string username, string password)
    {
        var profile = await _client.LoginAsync(username, password);
        _dispatcher.Invoke(() =>
        {
            IsLoggedIn = true;
            Username = string.IsNullOrEmpty(profile.Username) ? username : profile.Username;
            OnPropertyChanged(nameof(IsLoggedIn));
            OnPropertyChanged(nameof(Username));
        });
        _config.Current.Username = username;
        _config.Current.Password = password;
        _config.Save();
        return true;
    }

    /// <summary>用配置中保存的凭据静默恢复登录。</summary>
    public async Task<bool> TryRestoreAsync()
    {
        var config = _config.Current;
        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
        {
            return false;
        }
        try
        {
            return await LoginAsync(config.Username, config.Password);
        }
        catch
        {
            return false;
        }
    }

    public void Logout()
    {
        IsLoggedIn = false;
        Username = null;
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(Username));
    }
}

using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Market;

namespace SkinManager.Desktop;

public interface IMarketAccountAuthProvider : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<string?> ReadCookieHeaderAsync(CancellationToken cancellationToken = default);
    Task OpenAsync(Window owner, Uri startUri, CancellationToken cancellationToken = default);
    Task ClearCookiesAsync(CancellationToken cancellationToken = default);
}

internal sealed class WebView2MarketAccountAuthProvider : IMarketAccountAuthProvider
{
    private static readonly Uri SiteOrigin = new("https://blog.ancimen.top/");
    private readonly WebView2 _cookieBridge;
    private readonly string _userDataFolder;
    private CoreWebView2Environment? _environment;
    private Task? _initializeTask;
    private MarketAccountWindow? _activeWindow;
    private bool _disposed;

    internal WebView2MarketAccountAuthProvider(WebView2 cookieBridge, string? userDataFolder = null)
    {
        _cookieBridge = cookieBridge ?? throw new ArgumentNullException(nameof(cookieBridge));
        _userDataFolder = Path.GetFullPath(userDataFolder ?? GetDefaultUserDataFolder());
    }

    internal static string GetDefaultUserDataFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkinManager",
        "MarketAccount");

    internal static string? BuildCookieHeader(IEnumerable<(string Name, string Value)> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        var safeCookies = cookies.Where(cookie =>
                !string.IsNullOrWhiteSpace(cookie.Name) &&
                cookie.Name.All(character => char.IsAsciiLetterOrDigit(character) || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~') &&
                cookie.Value.IndexOfAny([';', '\r', '\n']) < 0)
            .Select(cookie => $"{cookie.Name}={cookie.Value}")
            .ToArray();
        return safeCookies.Length == 0 ? null : string.Join("; ", safeCookies);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_cookieBridge.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("账号 WebView2 只能在窗口 UI 线程初始化。");
        }

        _initializeTask ??= InitializeCoreAsync();
        return _initializeTask.WaitAsync(cancellationToken);
    }

    public async Task<string?> ReadCookieHeaderAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var core = _cookieBridge.CoreWebView2
            ?? throw new InvalidOperationException("账号 Cookie 桥接尚未初始化。");
        var cookies = await core.CookieManager.GetCookiesAsync(SiteOrigin.AbsoluteUri);
        cancellationToken.ThrowIfCancellationRequested();
        return BuildCookieHeader(cookies.Select(cookie => (cookie.Name, cookie.Value)));
    }

    public async Task OpenAsync(Window owner, Uri startUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(startUri);
        await InitializeAsync(cancellationToken);
        if (!MarketAccountNavigationPolicy.IsAllowed(startUri, SiteOrigin))
        {
            throw new InvalidOperationException("账号窗口只允许打开本站登录或注册页面。");
        }

        if (!owner.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("账号窗口只能在窗口 UI 线程打开。");
        }

        var window = new MarketAccountWindow(
            _environment ?? throw new InvalidOperationException("账号 WebView2 环境尚未初始化。"),
            startUri)
        {
            Owner = owner
        };
        _activeWindow = window;
        window.Show();
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (!window.Dispatcher.HasShutdownStarted)
            {
                _ = window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (window.IsVisible)
                    {
                        window.Close();
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        });
        try
        {
            await window.Completion.WaitAsync(cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(_activeWindow, window))
            {
                _activeWindow = null;
            }
        }
    }

    public async Task ClearCookiesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (_activeWindow is { IsVisible: true } window)
        {
            await window.Dispatcher.InvokeAsync(window.Close, System.Windows.Threading.DispatcherPriority.Background);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _cookieBridge.CoreWebView2?.CookieManager.DeleteAllCookies();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_activeWindow is { } window && !window.Dispatcher.HasShutdownStarted)
        {
            try
            {
                await window.Dispatcher.InvokeAsync(() =>
                {
                    if (window.IsVisible)
                    {
                        window.Close();
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (_initializeTask is not null)
        {
            try
            {
                await _initializeTask;
            }
            catch (Exception)
            {
            }
        }

        if (!_cookieBridge.Dispatcher.HasShutdownStarted)
        {
            try
            {
                await _cookieBridge.Dispatcher.InvokeAsync(
                    _cookieBridge.Dispose,
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private async Task InitializeCoreAsync()
    {
        Directory.CreateDirectory(_userDataFolder);
        _environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: _userDataFolder);
        await _cookieBridge.EnsureCoreWebView2Async(_environment);
        var core = _cookieBridge.CoreWebView2
            ?? throw new InvalidOperationException("账号 Cookie 桥接 WebView2 初始化失败。");
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        if (string.IsNullOrWhiteSpace(core.Source) || core.Source == "about:blank")
        {
            core.Navigate("about:blank");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WebView2MarketAccountAuthProvider));
        }
    }
}

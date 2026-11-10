using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace SkinManager.Desktop;

internal partial class MarketAccountWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly Uri _startUri;
    private readonly Uri _siteOrigin;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _returnedToMarket;
    private bool _isInitializing;
    private bool _isClosed;

    internal MarketAccountWindow(CoreWebView2Environment environment, Uri startUri)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _startUri = startUri ?? throw new ArgumentNullException(nameof(startUri));
        _siteOrigin = new Uri(startUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        if (!MarketAccountNavigationPolicy.IsAllowed(_startUri, _siteOrigin))
        {
            throw new InvalidOperationException("账号窗口只允许打开本站登录、注册与账号返回页。");
        }

        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: false, animateClose: false);
        Loaded += MarketAccountWindow_Loaded;
        Closing += (_, _) => _completion.TrySetResult(_returnedToMarket);
        Closed += MarketAccountWindow_Closed;
    }

    internal Task<bool> Completion => _completion.Task;

    private async void MarketAccountWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _isInitializing = true;
        try
        {
            await AccountBrowser.EnsureCoreWebView2Async(_environment);
            if (_isClosed || !IsVisible)
            {
                return;
            }

            var core = AccountBrowser.CoreWebView2
                ?? throw new InvalidOperationException("账号页面没有完成初始化。");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += AccountBrowser_NavigationStarting;
            core.NewWindowRequested += AccountBrowser_NewWindowRequested;
            core.ProcessFailed += AccountBrowser_ProcessFailed;
            AccountStatusText.Text = "请在本站完成登录或注册；登录后会自动返回皮肤市场。";
            core.Navigate(_startUri.AbsoluteUri);
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                AccountStatusText.Text = $"账号页面无法打开：{exception.Message}";
                AccountStatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorText");
            }
        }
    }

    private void AccountBrowser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            SetStatus("本站账号链接无效，已阻止导航。", isError: true);
            return;
        }

        if (MarketAccountNavigationPolicy.IsReturnToMarket(uri, _siteOrigin))
        {
            e.Cancel = true;
            _returnedToMarket = true;
            AccountStatusText.Text = "已返回皮肤市场，正在验证账号…";
            _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        if (!MarketAccountNavigationPolicy.IsAllowed(uri, _siteOrigin))
        {
            e.Cancel = true;
            SetStatus("为保护账号信息，只能在本站登录或注册。", isError: true);
        }
    }

    private void AccountBrowser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            SetStatus("本站账号链接无效，已阻止打开。", isError: true);
            return;
        }

        if (MarketAccountNavigationPolicy.IsReturnToMarket(uri, _siteOrigin))
        {
            _returnedToMarket = true;
            AccountStatusText.Text = "已返回皮肤市场，正在验证账号…";
            _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        if (MarketAccountNavigationPolicy.IsAllowed(uri, _siteOrigin))
        {
            AccountBrowser.CoreWebView2?.Navigate(uri.AbsoluteUri);
            return;
        }

        SetStatus("只允许本站登录与注册页面，已阻止外部窗口。", isError: true);
    }

    private void AccountBrowser_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) =>
        SetStatus("账号页面意外关闭，请返回市场后重试。", isError: true);

    private void ReturnButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MarketAccountWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _completion.TrySetResult(_returnedToMarket);
        if (AccountBrowser.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= AccountBrowser_NavigationStarting;
            core.NewWindowRequested -= AccountBrowser_NewWindowRequested;
            core.ProcessFailed -= AccountBrowser_ProcessFailed;
        }

        AccountBrowser.Dispose();
    }

    private void SetStatus(string message, bool isError)
    {
        if (_isClosed)
        {
            return;
        }

        AccountStatusText.Text = message;
        AccountStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "ErrorText" : "SecondaryText");
    }
}

internal static class MarketAccountNavigationPolicy
{
    internal static bool IsAllowed(Uri target, Uri siteOrigin)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(siteOrigin);
        if (!IsSameOrigin(target, siteOrigin))
        {
            return false;
        }

        return target.AbsolutePath.Equals("/wp-login.php", StringComparison.OrdinalIgnoreCase) ||
               IsReturnToMarket(target, siteOrigin);
    }

    internal static bool IsReturnToMarket(Uri target, Uri siteOrigin)
    {
        if (!IsSameOrigin(target, siteOrigin) || !target.AbsolutePath.Equals("/", StringComparison.Ordinal))
        {
            return false;
        }

        return target.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(parts => Uri.UnescapeDataString(parts[0]).Equals("page_id", StringComparison.OrdinalIgnoreCase) &&
                          parts.Length > 1 && Uri.UnescapeDataString(parts[1]) == "39");
    }

    private static bool IsSameOrigin(Uri target, Uri siteOrigin) =>
        target.IsAbsoluteUri && target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        siteOrigin.IsAbsoluteUri && siteOrigin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        target.IdnHost.Equals(siteOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        target.Port == siteOrigin.Port && string.IsNullOrEmpty(target.UserInfo);
}

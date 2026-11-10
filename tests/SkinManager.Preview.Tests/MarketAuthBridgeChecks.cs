using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Desktop;

namespace SkinManager.Preview.Tests;

internal static class MarketAuthBridgeChecks
{
    private static readonly Uri SiteOrigin = new("https://blog.ancimen.top/");
    private static readonly Uri LoginUri = new("https://blog.ancimen.top/wp-login.php");
    private static readonly Uri MarketReturnUri = new("https://blog.ancimen.top/?page_id=39");
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

    internal static async Task RunAsync()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var testRoot = Path.GetFullPath(Path.Combine(temporaryRoot, $"SkinManager-market-auth-bridge-{Guid.NewGuid():N}"));
        EnsureWithinTemporaryRoot(testRoot, temporaryRoot);
        Directory.CreateDirectory(testRoot);

        var providerProfile = Path.Combine(testRoot, "provider-profile");
        var isolatedProfile = Path.Combine(testRoot, "isolated-profile");
        Window? providerHost = null;
        WebView2? providerBridge = null;
        WebView2MarketAccountAuthProvider? provider = null;
        Window? isolatedHost = null;
        WebView2? isolatedBridge = null;
        MarketAccountWindow? accountWindow = null;
        try
        {
            VerifyNavigationPolicy();

            providerBridge = CreateHiddenBridge();
            providerHost = CreateHiddenHost(providerBridge);
            providerHost.Show();
            provider = new WebView2MarketAccountAuthProvider(providerBridge, providerProfile);
            await provider.InitializeAsync().WaitAsync(OperationTimeout);

            var providerCore = providerBridge.CoreWebView2
                ?? throw new InvalidOperationException("Cookie 桥接 WebView2 初始化后没有 CoreWebView2。");
            await WaitUntilAsync(() => string.Equals(providerCore.Source, "about:blank", StringComparison.OrdinalIgnoreCase),
                "Cookie 桥接停留在 about:blank");
            Require(IsWithin(testRoot, providerProfile) && IsWithin(temporaryRoot, providerProfile),
                "Cookie 桥接使用系统临时目录内的 GUID 隔离配置目录");

            var providerCookieManager = providerCore.CookieManager;
            AddFixtureCookie(providerCookieManager, "sm_fixture_root", "root-only-fixture", "/");
            AddFixtureCookie(providerCookieManager, "sm_fixture_login", "login-path-fixture", "/wp-login.php");

            var rootCookieHeader = await provider.ReadCookieHeaderAsync().WaitAsync(OperationTimeout);
            var rootHeaderEntries = rootCookieHeader?.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim()).ToArray() ?? [];
            Require(rootHeaderEntries.Contains("sm_fixture_root=root-only-fixture", StringComparer.Ordinal) &&
                    !rootHeaderEntries.Any(entry => entry.StartsWith("sm_fixture_login=", StringComparison.Ordinal)),
                "生产 Cookie 桥只读取根 URI 匹配的 Cookie，不包含登录路径 Cookie");

            var loginPathCookies = await GetCookiesAsync(providerCookieManager, LoginUri);
            Require(loginPathCookies.Any(cookie => cookie.Name == "sm_fixture_root") &&
                    loginPathCookies.Any(cookie => cookie.Name == "sm_fixture_login"),
                "登录路径可匹配根路径与 /wp-login.php fixture Cookie");

            isolatedBridge = CreateHiddenBridge();
            isolatedHost = CreateHiddenHost(isolatedBridge);
            isolatedHost.Show();
            var isolatedEnvironment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: isolatedProfile)
                .WaitAsync(OperationTimeout);
            await isolatedBridge.EnsureCoreWebView2Async(isolatedEnvironment).WaitAsync(OperationTimeout);
            var isolatedCore = isolatedBridge.CoreWebView2
                ?? throw new InvalidOperationException("隔离配置目录的 WebView2 初始化失败。");
            AddFixtureCookie(isolatedCore.CookieManager, "sm_fixture_isolated", "other-profile-fixture", "/");
            await WaitUntilAsync(async () =>
            {
                var cookies = await GetCookiesAsync(isolatedCore.CookieManager, SiteOrigin);
                return cookies.Any(cookie => cookie.Name == "sm_fixture_isolated");
            }, "隔离配置目录接受 fixture Cookie");

            await provider.ClearCookiesAsync().WaitAsync(OperationTimeout);
            var rootCookiesAfterClear = await GetCookiesAsync(providerCookieManager, SiteOrigin);
            var loginCookiesAfterClear = await GetCookiesAsync(providerCookieManager, LoginUri);
            Require(rootCookiesAfterClear.Count == 0 && loginCookiesAfterClear.Count == 0,
                "清理操作删除桥接配置目录内所有路径的 Cookie");
            var isolatedCookiesAfterClear = await GetCookiesAsync(isolatedCore.CookieManager, SiteOrigin);
            Require(isolatedCookiesAfterClear.Any(cookie =>
                    cookie.Name == "sm_fixture_isolated" && cookie.Value == "other-profile-fixture"),
                "清理桥接配置目录不会影响另一个 fixture 配置目录");

            var providerEnvironment = GetProviderEnvironment(provider);
            accountWindow = new MarketAccountWindow(providerEnvironment, MarketReturnUri)
            {
                Owner = providerHost,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -2_000,
                Top = -2_000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            var accountBrowser = accountWindow.FindName("AccountBrowser") as WebView2
                ?? throw new InvalidOperationException("账号窗口没有名为 AccountBrowser 的 WebView2。");
            var safetyCancellationReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var returnNavigationWasCancelled = false;
            accountBrowser.CoreWebView2InitializationCompleted += (_, args) =>
            {
                if (!args.IsSuccess || accountBrowser.CoreWebView2 is not { } accountCore)
                {
                    safetyCancellationReady.TrySetException(
                        new InvalidOperationException("账号窗口的 fixture WebView2 初始化失败。"));
                    return;
                }

                accountCore.NavigationStarting += (_, navigation) =>
                {
                    if (!Uri.TryCreate(navigation.Uri, UriKind.Absolute, out var target) ||
                        !target.IdnHost.Equals(SiteOrigin.IdnHost, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    navigation.Cancel = true;
                    if (MarketAccountNavigationPolicy.IsReturnToMarket(target, SiteOrigin))
                    {
                        returnNavigationWasCancelled = true;
                    }
                };
                safetyCancellationReady.TrySetResult(true);
            };

            accountWindow.Show();
            await safetyCancellationReady.Task.WaitAsync(OperationTimeout);
            var returnedToMarket = await accountWindow.Completion.WaitAsync(OperationTimeout);
            await WaitUntilAsync(() => !accountWindow.IsVisible, "账号窗口收到市场返回 URI 后自动关闭");
            Require(returnNavigationWasCancelled && returnedToMarket,
                "真实 NavigationStarting 先取消返回 URI，再让账号窗口完成自动关闭");

            Console.WriteLine("通过：真实 WebView2 Cookie 桥接、临时配置目录隔离与账号返回拦截");
        }
        finally
        {
            try
            {
                if (accountWindow?.IsVisible == true)
                {
                    accountWindow.Close();
                }

                if (provider is not null)
                {
                    await provider.DisposeAsync();
                }
                else
                {
                    providerBridge?.Dispose();
                }

                if (isolatedBridge is not null)
                {
                    isolatedBridge.Dispose();
                }

                if (isolatedHost?.IsVisible == true)
                {
                    isolatedHost.Close();
                }

                if (providerHost?.IsVisible == true)
                {
                    providerHost.Close();
                }
            }
            finally
            {
                TryDeleteTemporaryDirectory(testRoot, temporaryRoot);
            }
        }
    }

    private static void VerifyNavigationPolicy()
    {
        Require(MarketAccountNavigationPolicy.IsAllowed(LoginUri, SiteOrigin) &&
                MarketAccountNavigationPolicy.IsAllowed(
                    new Uri("https://blog.ancimen.top/wp-login.php?action=register"), SiteOrigin) &&
                MarketAccountNavigationPolicy.IsAllowed(MarketReturnUri, SiteOrigin),
            "账号导航策略允许本站登录、注册和市场返回 URI");
        Require(!MarketAccountNavigationPolicy.IsAllowed(new Uri("https://evil.example/wp-login.php"), SiteOrigin) &&
                !MarketAccountNavigationPolicy.IsAllowed(new Uri("https://blog.ancimen.top/skins/ayanami"), SiteOrigin) &&
                !MarketAccountNavigationPolicy.IsAllowed(new Uri("https://blog.ancimen.top@evil.example/wp-login.php"), SiteOrigin),
            "账号导航策略拒绝外站、同站非账号页和嵌入用户名的伪装 URI");
    }

    private static WebView2 CreateHiddenBridge() => new()
    {
        Width = 1,
        Height = 1,
        Visibility = Visibility.Hidden
    };

    private static Window CreateHiddenHost(WebView2 bridge)
    {
        var content = new Grid();
        content.Children.Add(bridge);
        return new Window
        {
            Title = "Market auth fixture",
            Width = 100,
            Height = 80,
            Content = content,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize
        };
    }

    private static void AddFixtureCookie(CoreWebView2CookieManager manager, string name, string value, string path)
    {
        var cookie = manager.CreateCookie(name, value, SiteOrigin.IdnHost, path);
        cookie.IsSecure = true;
        manager.AddOrUpdateCookie(cookie);
    }

    private static async Task<IReadOnlyList<CoreWebView2Cookie>> GetCookiesAsync(
        CoreWebView2CookieManager manager,
        Uri uri) => await manager.GetCookiesAsync(uri.AbsoluteUri).WaitAsync(OperationTimeout);

    private static CoreWebView2Environment GetProviderEnvironment(WebView2MarketAccountAuthProvider provider) =>
        typeof(WebView2MarketAccountAuthProvider)
            .GetField("_environment", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(provider) as CoreWebView2Environment
        ?? throw new InvalidOperationException("账号 Cookie 桥接没有保留已初始化的 fixture WebView2 环境。");

    private static async Task WaitUntilAsync(Func<bool> condition, string label) =>
        await WaitUntilAsync(() => Task.FromResult(condition()), label);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string label)
    {
        var deadline = DateTime.UtcNow + OperationTimeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"市场账号桥接检查失败：{label}超时。");
            }

            await Task.Delay(25);
        }
    }

    private static bool IsWithin(string parent, string child)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullChild = Path.GetFullPath(child);
        return fullChild.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureWithinTemporaryRoot(string path, string temporaryRoot)
    {
        if (!IsWithin(temporaryRoot, path))
        {
            throw new InvalidOperationException("市场账号桥接检查目录超出系统临时目录。");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"市场账号桥接检查失败：{message}。");
        }

        Console.WriteLine($"通过：{message}");
    }

    private static void TryDeleteTemporaryDirectory(string path, string temporaryRoot)
    {
        try
        {
            EnsureWithinTemporaryRoot(path, temporaryRoot);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Market;

namespace SkinManager.Preview.Tests;

internal static class FeedbackUiChecks
{
    private const string TestCookie = "feedback-user=offline-fixture";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SkinManagerFeedbackUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var screenshotDirectory = Program.FindTestOutputDirectory();
        Directory.CreateDirectory(screenshotDirectory);
        AppLocalization.Initialize(Path.Combine(testRoot, "language.json"));
        AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
        var originalTheme = AppThemeService.Mode;
        AppThemeService.SetMode(AppThemeMode.Night);

        FeedbackWindow? feedbackWindow = null;
        FeedbackWindow? cancellationWindow = null;
        SkinMarketWindow? marketWindow = null;
        FakeAuthProvider? authProvider = null;
        FeedbackHandler? handler = null;
        MarketAccountSession? session = null;
        try
        {
            authProvider = new FakeAuthProvider();
            handler = new FeedbackHandler();
            var client = new SkinMarketClient(handler);
            session = new MarketAccountSession(client, authProvider, ownsClient: true, ownsAuthProvider: true);
            feedbackWindow = CreateFeedbackWindow(session);
            feedbackWindow.Show();
            await WaitUntilAsync(
                () => feedbackWindow.IsLoaded && feedbackWindow.LoginButtonForTests.IsVisible &&
                      !feedbackWindow.SubmitButtonForTests.IsEnabled &&
                      feedbackWindow.AccountStatusTextForTests.Text.Contains("尚未登录", StringComparison.Ordinal),
                "匿名反馈表单加载完成");
            Check(feedbackWindow.Title == "发送反馈" &&
                  ((TextBlock)feedbackWindow.FindName("FeedbackHeading")!).Text == "发送反馈" &&
                  feedbackWindow.TitleBoxForTests.MaxLength >= SkinMarketFeedbackRules.MaxTitleCharacters &&
                  feedbackWindow.MessageBoxForTests.AcceptsReturn &&
                  feedbackWindow.LoginButtonForTests.IsVisible,
                "匿名用户打开原生反馈表单，保留标题、正文和登录入口");
            await Program.SaveWindowScreenshotAsync(
                feedbackWindow,
                Path.Combine(screenshotDirectory, "反馈-夜间-未登录.png"));

            const string draftTitle = "临时标题🙂";
            const string draftMessage = "这是尚未发送的反馈内容。";
            feedbackWindow.TitleBoxForTests.Text = draftTitle;
            feedbackWindow.MessageBoxForTests.Text = draftMessage;
            authProvider.CookieAfterNextOpen = TestCookie;
            feedbackWindow.LoginButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(
                () => session.Account is not null && feedbackWindow.SubmitButtonForTests.IsEnabled &&
                      !authProvider.IsOpening,
                "登录后返回反馈表单");
            Check(feedbackWindow.TitleBoxForTests.Text == draftTitle &&
                  feedbackWindow.MessageBoxForTests.Text == draftMessage &&
                  authProvider.LastStartUri is { Host: "blog.ancimen.top", AbsolutePath: "/wp-login.php" },
                "反馈登录复用市场认证并保留用户已填写的内容");
            AppThemeService.SetMode(AppThemeMode.Day);
            await feedbackWindow.Dispatcher.InvokeAsync(feedbackWindow.UpdateLayout, DispatcherPriority.Render);
            await Program.SaveWindowScreenshotAsync(
                feedbackWindow,
                Path.Combine(screenshotDirectory, "反馈-白昼-已登录.png"));

            AppLocalization.SetLanguage(AppLanguage.English);
            await feedbackWindow.Dispatcher.InvokeAsync(feedbackWindow.UpdateLayout, DispatcherPriority.Render);
            Check(feedbackWindow.Title == "Send feedback" &&
                  ((TextBlock)feedbackWindow.FindName("FeedbackHeading")!).Text == "Send feedback" &&
                  feedbackWindow.TitleCharacterCount.Text == "5/120 characters" &&
                  AutomationProperties.GetName(feedbackWindow.LoginButtonForTests) == "Log in to the skin market",
                "反馈窗口标题、计数器与登录辅助标签即时切换为英文");
            await Program.SaveWindowScreenshotAsync(
                feedbackWindow,
                Path.Combine(screenshotDirectory, "Feedback-English-day.png"));

            AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);

            feedbackWindow.Width = 560;
            feedbackWindow.Height = 540;
            handler.SetNextFeedbackStatus(HttpStatusCode.Forbidden);
            feedbackWindow.SubmitButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(
                () => handler.FeedbackRequestCount == 1 && session.Account is null &&
                      feedbackWindow.LoginButtonForTests.IsVisible && !feedbackWindow.SubmitButtonForTests.IsEnabled,
                "认证失效后表单回到待登录状态");
            Check(feedbackWindow.TitleBoxForTests.Text == draftTitle &&
                  feedbackWindow.MessageBoxForTests.Text == draftMessage &&
                  feedbackWindow.OperationStatusTextForTests.Text.Contains("重新登录", StringComparison.Ordinal),
                "会话失效错误保留标题和正文并提示重新登录");
            await feedbackWindow.Dispatcher.InvokeAsync(feedbackWindow.UpdateLayout, DispatcherPriority.Render);
            Check(feedbackWindow.Width == 560 && feedbackWindow.Height == 540 &&
                  feedbackWindow.OperationStatusTextForTests.IsVisible &&
                  feedbackWindow.LoginButtonForTests.IsVisible &&
                  feedbackWindow.CloseButtonForTests.IsVisible &&
                  feedbackWindow.SubmitButtonForTests.IsVisible &&
                  feedbackWindow.CloseButtonForTests.ActualHeight > 0 &&
                  feedbackWindow.SubmitButtonForTests.ActualHeight > 0,
                "最小尺寸错误状态仍显示账号提示与底部操作按钮");
            await Program.SaveWindowScreenshotAsync(
                feedbackWindow,
                Path.Combine(screenshotDirectory, "反馈-最小尺寸.png"));

            feedbackWindow.Width = 640;
            feedbackWindow.Height = 620;
            authProvider.CookieAfterNextOpen = TestCookie;
            feedbackWindow.LoginButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(
                () => session.Account is not null && feedbackWindow.SubmitButtonForTests.IsEnabled &&
                      !authProvider.IsOpening,
                "失效会话重新登录后返回原表单");
            Check(feedbackWindow.TitleBoxForTests.Text == draftTitle &&
                  feedbackWindow.MessageBoxForTests.Text == draftMessage,
                "重新登录没有清空反馈草稿");

            handler.BlockNextFeedback(HttpStatusCode.ServiceUnavailable);
            feedbackWindow.SubmitButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await handler.WaitForBlockedFeedbackAsync();
            feedbackWindow.SubmitButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(handler.FeedbackRequestCount == 2 &&
                  !feedbackWindow.SubmitButtonForTests.IsEnabled &&
                  !feedbackWindow.TitleBoxForTests.IsEnabled &&
                  feedbackWindow.IsSendingForTests,
                "发送中禁用重复提交并暂时锁定表单字段");
            handler.ReleaseBlockedFeedback();
            await WaitUntilAsync(
                () => !feedbackWindow.IsSendingForTests &&
                      feedbackWindow.OperationStatusTextForTests.Text.Contains("暂时无法发送", StringComparison.Ordinal),
                "模拟服务器暂时不可用后的失败状态");
            Check(feedbackWindow.TitleBoxForTests.Text == draftTitle &&
                  feedbackWindow.MessageBoxForTests.Text == draftMessage &&
                  feedbackWindow.SubmitButtonForTests.IsEnabled,
                "发送失败后保留草稿并允许直接重试");

            feedbackWindow.SubmitButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(
                () => handler.FeedbackRequestCount == 3 &&
                      feedbackWindow.SubmitButtonForTests.Content?.ToString() == "已发送",
                "重试成功后进入明确完成状态");
            Check(feedbackWindow.TitleBoxForTests.IsReadOnly &&
                  feedbackWindow.MessageBoxForTests.IsReadOnly &&
                  feedbackWindow.TitleBoxForTests.IsEnabled &&
                  feedbackWindow.MessageBoxForTests.IsEnabled &&
                  feedbackWindow.CloseButtonForTests.IsEnabled,
                "反馈发送成功后锁定已发送内容并提供关闭入口");
            Check(handler.LastFeedbackPayload is { } payload &&
                  payload.Title == draftTitle && payload.Message == draftMessage &&
                  payload.Source == "skinmanager" &&
                  payload.AppVersion == UpdateCoordinator.ReadCurrentVersionTag(),
                "UI 提交原文、固定来源和程序集公开版本");

            marketWindow = new SkinMarketWindow(
                client,
                new Version(1, 1, 4),
                App.ReadEmbeddedBasePackage(),
                Path.Combine(testRoot, "unused-profile"),
                _ => Task.FromResult(true),
                () => new Dictionary<string, string>(),
                useDefaultAccountAuth: false,
                accountSession: session)
            {
                Width = 880,
                Height = 620,
                MinWidth = 880,
                MinHeight = 620,
                Left = -2_000,
                Top = -2_000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            marketWindow.Show();
            await WaitUntilAsync(
                () => marketWindow.IsLoaded && marketWindow.LogoutButtonForTests.IsVisible &&
                      marketWindow.AccountDisplayTextForTests.Text == "反馈测试用户",
                "共享市场窗口读取已登录会话");
            marketWindow.LogoutButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(
                () => session.Account is null && feedbackWindow.LoginButtonForTests.IsVisible &&
                      !feedbackWindow.SubmitButtonForTests.IsEnabled,
                "市场注销同步撤销反馈表单认证");
            Check(handler.FeedbackRequestCount == 3,
                "市场注销后反馈表单不能沿用过期会话发送");

            authProvider.CookieAfterNextOpen = TestCookie;
            feedbackWindow.LoginButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(
                () => session.Account is not null && marketWindow.LogoutButtonForTests.IsVisible &&
                      !feedbackWindow.LoginButtonForTests.IsVisible,
                "反馈窗口登录同步更新市场账号状态");

            cancellationWindow = CreateFeedbackWindow(session);
            cancellationWindow.Show();
            await WaitUntilAsync(
                () => cancellationWindow.IsLoaded && !cancellationWindow.LoginButtonForTests.IsVisible,
                "第二个反馈窗口复用已登录会话");
            cancellationWindow.TitleBoxForTests.Text = "取消请求";
            cancellationWindow.MessageBoxForTests.Text = "关闭窗口时应停止等待中的提交。";
            Check(cancellationWindow.SubmitButtonForTests.IsEnabled, "已登录用户可直接开始提交反馈");
            handler.HoldNextFeedbackUntilCanceled();
            cancellationWindow.SubmitButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await handler.WaitForBlockedFeedbackAsync();
            cancellationWindow.Close();
            await WaitUntilAsync(
                () => handler.CancellationObserved && !session.IsBusy,
                "关闭反馈窗口取消在途请求");
            Check(handler.FeedbackRequestCount == 4,
                "关闭时取消请求，没有触发额外提交");

            Console.WriteLine($"反馈表单截图已输出：{screenshotDirectory}");
        }
        finally
        {
            if (cancellationWindow?.IsVisible == true)
            {
                cancellationWindow.Close();
            }

            if (marketWindow?.IsVisible == true)
            {
                marketWindow.Close();
            }

            if (feedbackWindow?.IsVisible == true)
            {
                feedbackWindow.Close();
            }

            if (session is not null)
            {
                await session.DisposeAsync();
            }

            AppThemeService.SetMode(originalTheme);
            AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
            TryDeleteTemporaryDirectory(testRoot);
        }
    }

    private static FeedbackWindow CreateFeedbackWindow(MarketAccountSession session) => new(session)
    {
        Width = 640,
        Height = 620,
        MinWidth = 560,
        MinHeight = 540,
        Left = -2_000,
        Top = -2_000,
        WindowStartupLocation = WindowStartupLocation.Manual,
        ShowActivated = false,
        ShowInTaskbar = false
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"反馈 UI 检查超时：{label}。");
            }

            await Application.Current.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            await Task.Delay(20);
        }

        await Application.Current.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
        Console.WriteLine($"通过：{label}");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{label}失败。");
        }

        Console.WriteLine($"通过：{label}");
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
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

    private sealed class FakeAuthProvider : IMarketAccountAuthProvider
    {
        public string? CookieHeader { get; private set; }
        public string? CookieAfterNextOpen { get; set; }
        public Uri? LastStartUri { get; private set; }
        public bool IsOpening { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string?> ReadCookieHeaderAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CookieHeader);
        }

        public Task OpenAsync(Window owner, Uri startUri, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(owner);
            cancellationToken.ThrowIfCancellationRequested();
            IsOpening = true;
            LastStartUri = startUri;
            if (CookieAfterNextOpen is not null)
            {
                CookieHeader = CookieAfterNextOpen;
                CookieAfterNextOpen = null;
            }

            IsOpening = false;
            return Task.CompletedTask;
        }

        public Task ClearCookiesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CookieHeader = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            CookieHeader = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FeedbackHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private int _feedbackRequestCount;
        private HttpStatusCode? _nextFeedbackStatus;
        private TaskCompletionSource? _blockedRequestStarted;
        private TaskCompletionSource? _releaseBlockedRequest;
        private bool _holdUntilCanceled;

        public int FeedbackRequestCount => Volatile.Read(ref _feedbackRequestCount);
        public bool CancellationObserved { get; private set; }
        public FeedbackPayload? LastFeedbackPayload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = DecodeQuery(request.RequestUri);
            if (query.Contains("page_id=39", StringComparison.Ordinal))
            {
                var cookie = request.Headers.TryGetValues("Cookie", out var values)
                    ? values.SingleOrDefault()
                    : null;
                return HtmlResponse(BootstrapHtml(cookie?.Contains("feedback-user", StringComparison.Ordinal) == true));
            }

            if (query.Contains("/wp/v2/users/me", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("{\"id\":42,\"name\":\"反馈测试用户\"}");
            }

            if (query.Contains("rest_route=/skin-manager/v1/skins", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {"formatVersion":1,"schemaVersion":1,"items":[],
                     "pagination":{"page":1,"perPage":12,"total":0,"totalPages":0},"availableTags":[]}
                    """);
            }

            if (request.Method == HttpMethod.Post &&
                query.Contains("rest_route=/skin-manager/v1/feedback", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _feedbackRequestCount);
                var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                using (var document = JsonDocument.Parse(body))
                {
                    var root = document.RootElement;
                    LastFeedbackPayload = new FeedbackPayload(
                        root.GetProperty("title").GetString() ?? string.Empty,
                        root.GetProperty("message").GetString() ?? string.Empty,
                        root.GetProperty("source").GetString() ?? string.Empty,
                        root.GetProperty("appVersion").GetString() ?? string.Empty);
                }
                HttpStatusCode? nextStatus;
                TaskCompletionSource? started;
                TaskCompletionSource? release;
                bool holdUntilCanceled;
                lock (_gate)
                {
                    nextStatus = _nextFeedbackStatus;
                    _nextFeedbackStatus = null;
                    started = _blockedRequestStarted;
                    release = _releaseBlockedRequest;
                    holdUntilCanceled = _holdUntilCanceled;
                    _holdUntilCanceled = false;
                }

                if (started is not null)
                {
                    started.TrySetResult();
                }

                if (holdUntilCanceled)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        CancellationObserved = true;
                        throw;
                    }
                }

                if (release is not null)
                {
                    await release.Task.WaitAsync(cancellationToken);
                }

                if (nextStatus is { } status)
                {
                    return ErrorResponse(status, status == HttpStatusCode.ServiceUnavailable
                        ? "服务器暂时无法发送反馈，请稍后重试。"
                        : "市场登录状态或安全验证已失效，请重新登录后重试。");
                }

                return JsonResponse("{\"success\":true,\"message\":\"反馈已发送\"}");
            }

            throw new InvalidDataException($"未预期的离线 API 请求：{request.Method} {query}");
        }

        internal void SetNextFeedbackStatus(HttpStatusCode status)
        {
            lock (_gate)
            {
                _nextFeedbackStatus = status;
            }
        }

        internal void BlockNextFeedback(HttpStatusCode status)
        {
            lock (_gate)
            {
                _nextFeedbackStatus = status;
                _blockedRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _releaseBlockedRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal void HoldNextFeedbackUntilCanceled()
        {
            lock (_gate)
            {
                _blockedRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _releaseBlockedRequest = null;
                _holdUntilCanceled = true;
            }
        }

        internal async Task WaitForBlockedFeedbackAsync()
        {
            TaskCompletionSource? started;
            lock (_gate)
            {
                started = _blockedRequestStarted;
            }

            if (started is not null)
            {
                await started.Task.WaitAsync(WaitLimit);
            }
        }

        internal void ReleaseBlockedFeedback()
        {
            TaskCompletionSource? release;
            lock (_gate)
            {
                release = _releaseBlockedRequest;
            }

            release?.TrySetResult();
        }

        private static string DecodeQuery(Uri? uri) =>
            Uri.UnescapeDataString((uri?.Query ?? string.Empty).TrimStart('?').Replace('+', ' '));

        private static string BootstrapHtml(bool loggedIn)
        {
            var config = JsonSerializer.Serialize(new
            {
                restBase = "/?rest_route=/skin-manager/v1/",
                nonce = loggedIn ? "feedback-nonce" : string.Empty,
                isLoggedIn = loggedIn,
                userId = loggedIn ? 42 : 0,
                canSubmit = false,
                loginUrl = "https://blog.ancimen.top/wp-login.php?redirect_to=https%3A%2F%2Fblog.ancimen.top%2F%3Fpage_id%3D39",
                marketUrl = "https://blog.ancimen.top/?page_id=39"
            });
            return $"<section data-smm-config=\"{WebUtility.HtmlEncode(config)}\"></section>";
        }

        private static HttpResponseMessage HtmlResponse(string html) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string message) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                code = status == HttpStatusCode.ServiceUnavailable
                    ? "smm_feedback_unavailable"
                    : "smm_authentication_required",
                message,
                data = new { status = (int)status }
            }), Encoding.UTF8, "application/json")
        };
    }

    private sealed record FeedbackPayload(string Title, string Message, string Source, string AppVersion);
}

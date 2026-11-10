using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Market;

namespace SkinManager.Preview.Tests;

internal static class MarketCommunityUiChecks
{
    private const string SubmitCookie = "market-submit=offline-fixture";
    private const string ReadOnlyCookie = "market-readonly=offline-fixture";
    private const string RestNonce = "market-ui-nonce-fixture";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    public static async Task RunAsync()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SkinManagerMarketCommunityUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var screenshotDirectory = Program.FindTestOutputDirectory();
        Directory.CreateDirectory(screenshotDirectory);
        var originalTheme = AppThemeService.Mode;
        AppThemeService.SetMode(AppThemeMode.Night);

        SkinMarketWindow? window = null;
        SkinMarketClient? client = null;
        FakeMarketAuthProvider? authProvider = null;
        string? originalZipPath = null;
        try
        {
            var basePackage = App.ReadEmbeddedBasePackage();
            var ayanamiBytes = ReadEmbeddedPackage("Ayanami");
            var sayaBytes = ReadEmbeddedPackage("Saya");
            var ayanami = SkinPackageReader.ReadZipBytes(ayanamiBytes);
            var saya = SkinPackageReader.ReadZipBytes(sayaBytes);

            var store = new SkinStore(Path.Combine(testRoot, "skin-store"));
            store.Initialize();
            store.Save(ayanami, replaceExisting: false);
            store.Save(saya, replaceExisting: false);
            Check(store.GetCatalog().Select(entry => entry.Id).Order(StringComparer.Ordinal).SequenceEqual(["ayanami", "saya"]),
                "UI 市场用例使用隔离皮肤库，内含 Ayanami 与 Saya");

            originalZipPath = Path.Combine(testRoot, "original-ayanami.zip");
            await File.WriteAllBytesAsync(originalZipPath, ayanamiBytes);
            var server = new FakeMarketHandler([ayanami, saya], [ayanamiBytes, sayaBytes]);
            authProvider = new FakeMarketAuthProvider();
            client = new SkinMarketClient(server);
            window = new SkinMarketWindow(
                client,
                new Version(1, 1, 0),
                basePackage,
                Path.Combine(testRoot, "unused-profile"),
                _ => Task.FromResult(true),
                () => store.GetCatalog().ToDictionary(entry => entry.Id, entry => entry.Version, StringComparer.OrdinalIgnoreCase),
                accountAuthProvider: authProvider,
                useDefaultAccountAuth: false,
                accountProfileDirectory: Path.Combine(testRoot, "fake-account-profile"),
                readLocalSkinCatalog: store.GetCatalog,
                exportLocalSkinPackage: (id, path) => store.ExportZip(id, path, overwriteExisting: false))
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

            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await WaitUntilAsync(() => window.IsLoaded && !window.IsLoading && window.Rows.Count == 2,
                "离线目录与窗口加载完成");
            await WaitUntilAsync(() => window.Rows.All(row => row.HasThumbnail || row.HasThumbnailError),
                "Ayanami 与 Saya 封面读取完成");
            await WaitUntilAsync(() => authProvider.InitializeCount > 0 && client.Account is null &&
                !IsAccountBusy(window) && window.LoginButtonForTests.IsEnabled,
                "匿名账号状态恢复完成");
            Check(window.IsCatalogVisible && window.LoginButtonForTests.Visibility == Visibility.Visible &&
                  window.RegisterButtonForTests.Visibility == Visibility.Visible &&
                  window.LogoutButtonForTests.Visibility == Visibility.Collapsed,
                "匿名用户可浏览目录并看到登录与注册入口");
            Check(server.WriteRequestCount == 0 && server.PackageDownloadCount == 0,
                "匿名目录浏览没有点赞、评论、投稿或下载 ZIP 写请求");
            await SaveScreenshotAsync(window, Path.Combine(screenshotDirectory, "市场社区-夜间-匿名目录-880x620.png"));

            var ayanamiRow = window.Rows.Single(row => row.Id == "ayanami");
            window.OpenDetails(ayanamiRow);
            await WaitUntilAsync(() => window.IsDetailsVisible && window.CommentsListForTests.Items.Count == 2,
                "匿名详情加载公开示例评论");
            var loginOpens = authProvider.OpenCount;
            Click(window.DetailsLikeButtonForTests, "匿名用户打开点赞登录流程");
            await WaitUntilAsync(() => authProvider.OpenCount == loginOpens + 1 && !IsAccountBusy(window),
                "匿名点赞打开 fake 登录流程");
            Check(server.WriteRequestCount == 0 && authProvider.LastStartUri == client.LoginUri &&
                  authProvider.LastStartUri is { Host: "blog.ancimen.top", AbsolutePath: "/wp-login.php" },
                "匿名点赞不发 POST，登录链接交给 fake provider");

            loginOpens = authProvider.OpenCount;
            Click(FindNamed<Button>(window, "LoginToCommentButton"), "匿名用户打开评论登录流程");
            await WaitUntilAsync(() => authProvider.OpenCount == loginOpens + 1 && !IsAccountBusy(window),
                "匿名评论打开 fake 登录流程");
            Check(server.WriteRequestCount == 0, "匿名评论登录入口不发 POST");

            Click(window.OpenUploadButtonForTests, "匿名用户打开投稿页");
            await WaitUntilAsync(() => window.IsUploadVisibleForTests, "匿名投稿页显示权限提示");
            loginOpens = authProvider.OpenCount;
            Click(FindNamed<Button>(window, "UploadLoginButton"), "匿名用户从投稿提示打开登录流程");
            await WaitUntilAsync(() => authProvider.OpenCount == loginOpens + 1 && !IsAccountBusy(window),
                "匿名投稿登录入口打开 fake provider");
            Check(server.WriteRequestCount == 0, "匿名上传登录入口不发 POST");
            Click(FindNamed<Button>(window, "BackFromUploadButton"), "返回详情");
            await WaitUntilAsync(() => window.IsDetailsVisible, "回到匿名详情");

            loginOpens = authProvider.OpenCount;
            Click(window.RegisterButtonForTests, "打开注册入口");
            await WaitUntilAsync(() => authProvider.OpenCount == loginOpens + 1 && !IsAccountBusy(window),
                "注册 fake 流程结束");
            Check(authProvider.LastStartUri is { Host: "blog.ancimen.top" } registerUri &&
                  registerUri.AbsolutePath == "/wp-login.php" && registerUri.Query.Contains("action=register", StringComparison.Ordinal),
                "注册入口使用本站注册页面，未打开真实网站");
            Check(server.WriteRequestCount == 0 && client.Account is null,
                "匿名登录与注册对话流程结束后仍没有写请求");

            authProvider.CookieAfterNextOpen = ReadOnlyCookie;
            loginOpens = authProvider.OpenCount;
            Click(window.LoginButtonForTests, "以只读账号登录");
            await WaitUntilAsync(() => authProvider.OpenCount == loginOpens + 1 && client.Account?.UserId == 43 &&
                window.LogoutButtonForTests.Visibility == Visibility.Visible,
                "fake 登录恢复只读账号状态");
            Check(window.LoginButtonForTests.Visibility == Visibility.Collapsed && window.RegisterButtonForTests.Visibility == Visibility.Collapsed,
                "登录后切换为账号与退出状态");
            Click(window.OpenUploadButtonForTests, "只读账号打开投稿页");
            await WaitUntilAsync(() => window.IsUploadVisibleForTests, "只读账号投稿页加载");
            Check(!window.SubmitUploadButtonForTests.IsEnabled && !window.PickUploadZipButtonForTests.IsEnabled &&
                  !window.ExportLocalSkinButtonForTests.IsEnabled &&
                  FindNamed<TextBlock>(window, "UploadAccessMessage").Text.Contains("没有投稿权限", StringComparison.Ordinal),
                "CanSubmit=false 时显示权限提示并禁用投稿");
            Click(FindNamed<Button>(window, "BackFromUploadButton"), "只读账号返回详情");
            Click(window.LogoutButtonForTests, "退出只读账号");
            await WaitUntilAsync(() => client.Account is null && authProvider.CookieHeader is null &&
                window.LoginButtonForTests.Visibility == Visibility.Visible &&
                window.LogoutButtonForTests.Visibility == Visibility.Collapsed,
                "退出后清除客户端账号并恢复登录入口");

            authProvider.CookieAfterNextOpen = SubmitCookie;
            loginOpens = authProvider.OpenCount;
            Click(window.LoginButtonForTests, "以可投稿账号登录");
            await WaitUntilAsync(() => authProvider.OpenCount == loginOpens + 1 && client.Account is { UserId: 42, CanSubmit: true } &&
                window.LogoutButtonForTests.Visibility == Visibility.Visible,
                "fake 登录恢复可投稿账号");
            await WaitUntilAsync(() => window.Rows.Single(row => row.Id == "ayanami").LikedByMe,
                "登录目录按服务器状态同步已点赞标记");

            window.OpenDetails(ayanamiRow);
            await WaitUntilAsync(() => window.CommentsListForTests.Items.Count == 2 &&
                FindNamed<TextBlock>(window, "CommentsPaginationText").Text.Contains("第 1/2 页", StringComparison.Ordinal),
                "登录后详情加载示例评论与分页");
            await SetWindowSizeAsync(window, 1060, 740);
            await SaveScreenshotAsync(window, Path.Combine(screenshotDirectory, "市场社区-夜间-登录详情与示例评论.png"));

            Check(window.NextCommentsButtonForTests.IsEnabled, "有第二页评论时启用下一页");
            Click(window.NextCommentsButtonForTests, "加载第二页评论");
            await WaitUntilAsync(() => window.CommentsListForTests.Items.Count == 1 &&
                FindNamed<TextBlock>(window, "CommentsPaginationText").Text.Contains("第 2/2 页", StringComparison.Ordinal),
                "详情评论分页切换到第二页");
            Click(FindNamed<Button>(window, "PreviousCommentsButton"), "返回第一页评论");
            await WaitUntilAsync(() => window.CommentsListForTests.Items.Count == 2 &&
                FindNamed<TextBlock>(window, "CommentsPaginationText").Text.Contains("第 1/2 页", StringComparison.Ordinal),
                "详情评论分页返回第一页");

            var likeCountBefore = server.LikePostCount;
            Click(window.DetailsLikeButtonForTests, "取消当前点赞");
            await WaitUntilAsync(() => !ayanamiRow.LikedByMe && server.LikePostCount == likeCountBefore + 1,
                "取消点赞按服务器状态更新详情");
            Click(window.DetailsLikeButtonForTests, "重新点赞");
            await WaitUntilAsync(() => ayanamiRow.LikedByMe && server.LikePostCount == likeCountBefore + 2,
                "点赞按服务器状态更新详情");

            window.CommentInputForTests.Text = "这是一条等待审核的离线测试评论。";
            Check(window.SendCommentButtonForTests.IsEnabled, "输入评论后启用评论提交按钮");
            Click(window.SendCommentButtonForTests, "提交待审核评论");
            await WaitUntilAsync(() => server.CommentPostCount == 1 &&
                FindNamed<TextBlock>(window, "DetailsCommunityStatus").Text.Contains("审核", StringComparison.Ordinal) &&
                window.CommentInputForTests.Text.Length == 0,
                "评论成功后清空输入并显示待审核状态");

            server.FailNextComment();
            const string retainedErrorComment = "服务端失败后仍应保留这段评论。";
            window.CommentInputForTests.Text = retainedErrorComment;
            Check(window.SendCommentButtonForTests.IsEnabled, "错误用例输入后启用评论按钮");
            Click(window.SendCommentButtonForTests, "触发 fake 评论服务错误");
            await WaitUntilAsync(() => window.CommentInputForTests.Text == retainedErrorComment &&
                FindNamed<TextBlock>(window, "DetailsCommunityStatus").Text.Contains("输入内容已保留", StringComparison.Ordinal),
                "评论请求失败时保留输入和错误提示");

            server.ExpireNextCommentNonce();
            const string retainedNonceComment = "nonce 失效后也要保留评论。";
            window.CommentInputForTests.Text = retainedNonceComment;
            Check(window.SendCommentButtonForTests.IsEnabled, "nonce 用例输入后启用评论按钮");
            Click(window.SendCommentButtonForTests, "触发 fake nonce 失效");
            await WaitUntilAsync(() => client.Account is null &&
                window.LoginButtonForTests.Visibility == Visibility.Visible &&
                window.CommentInputForTests.Text == retainedNonceComment &&
                FindNamed<TextBlock>(window, "MarketAccountStatusText").Text.Contains("过期", StringComparison.Ordinal),
                "nonce 失效清除账号、显示重新登录并保留评论输入");

            authProvider.CookieAfterNextOpen = SubmitCookie;
            loginOpens = authProvider.OpenCount;
            Click(window.LoginButtonForTests, "nonce 失效后重新登录");
            await WaitUntilAsync(() => authProvider.OpenCount == loginOpens + 1 && client.Account is { CanSubmit: true },
                "重新登录恢复投稿权限");

            AppThemeService.SetMode(AppThemeMode.Day);
            Click(window.OpenUploadButtonForTests, "打开投稿页");
            await WaitUntilAsync(() => window.IsUploadVisibleForTests && window.ExportLocalSkinButtonForTests.IsEnabled,
                "投稿页载入隔离本地皮肤库");
            Check(window.LocalSkinBoxForTests.Items.Count == 2,
                "投稿皮肤选择器只显示 Ayanami 与 Saya");
            window.LocalSkinBoxForTests.SelectedIndex = 0;
            var selectedLocalSkin = window.LocalSkinBoxForTests.SelectedItem as MarketLocalSkinOption;
            Check(selectedLocalSkin?.Entry.Id.Equals("ayanami", StringComparison.OrdinalIgnoreCase) == true,
                "本地皮肤目录可选择 Ayanami");
            await window.Dispatcher.InvokeAsync(() => window.LocalSkinBoxForTests.UpdateLayout(), DispatcherPriority.Render);
            var localSkinTextBlocks = FindVisualDescendants<TextBlock>(window.LocalSkinBoxForTests)
                .Select(textBlock => textBlock.Text.Trim()).ToArray();
            var expectedLocalSkinLabel = selectedLocalSkin!.DisplayLabel;
            Check(expectedLocalSkinLabel.Contains(selectedLocalSkin.Entry.Name, StringComparison.Ordinal) &&
                  expectedLocalSkinLabel.Contains("ayanami", StringComparison.OrdinalIgnoreCase) &&
                  expectedLocalSkinLabel.Contains(selectedLocalSkin.Entry.Version, StringComparison.Ordinal) &&
                  localSkinTextBlocks.Any(text => text.Equals(expectedLocalSkinLabel, StringComparison.Ordinal)) &&
                  !localSkinTextBlocks.Any(text => text.Contains(nameof(MarketLocalSkinOption), StringComparison.Ordinal) ||
                                                   text.Contains(nameof(SkinCatalogEntry), StringComparison.Ordinal)),
                "投稿页本地皮肤选择器可见 TextBlock 显示 Ayanami/ayanami/版本友好名称，没有原始记录字段");
            Click(window.ExportLocalSkinButtonForTests, "从本地皮肤库导出 Ayanami 投稿包");
            await WaitUntilAsync(() => window.UploadPackagePathForTests is { } path && File.Exists(path),
                "本地 Ayanami 临时 ZIP 导出完成");
            var temporaryPackagePath = window.UploadPackagePathForTests!;
            Check(SkinPackageReader.ReadZipFile(temporaryPackagePath).Id == "ayanami",
                "SkinStore.ExportZip 生成有效 Ayanami 投稿 ZIP");

            FillUploadForm(window, "本地皮肤库投稿：等待管理员审核。", "轻量,浅色");
            await SaveScreenshotAsync(window, Path.Combine(screenshotDirectory, "市场社区-白昼-投稿完整表单.png"));
            Check(window.SubmitUploadButtonForTests.IsEnabled, "选包并填写表单后启用投稿按钮");
            Click(window.SubmitUploadButtonForTests, "提交本地库临时 ZIP");
            await WaitUntilAsync(() => server.SubmissionPostCount == 1 &&
                window.UploadStatusTextForTests.Text.Contains("等待管理员审核", StringComparison.Ordinal),
                "投稿成功显示服务器 pending 审核状态");
            await WaitUntilAsync(() => !File.Exists(temporaryPackagePath), "成功提交后清理客户端拥有的临时 ZIP");
            await SaveScreenshotAsync(window, Path.Combine(screenshotDirectory, "市场社区-白昼-投稿待审核结果.png"));

            await window.LoadUploadPackageForTestsAsync(originalZipPath);
            FillUploadForm(window, "用户原始 ZIP 成功提交后必须保留。", "浅色");
            Click(window.SubmitUploadButtonForTests, "提交用户原始 ZIP");
            await WaitUntilAsync(() => server.SubmissionPostCount == 2 &&
                window.UploadStatusTextForTests.Text.Contains("等待管理员审核", StringComparison.Ordinal),
                "用户原始 ZIP 投稿成功并得到 pending 状态");
            Check(File.Exists(originalZipPath), "原始 ZIP 成功投稿后仍存在");

            await window.LoadUploadPackageForTestsAsync(originalZipPath);
            FillUploadForm(window, "nonce 过期时保留投稿说明。", "浅色");
            server.ExpireNextSubmissionNonce();
            Click(window.SubmitUploadButtonForTests, "触发投稿 nonce 失效");
            await WaitUntilAsync(() => client.Account is null &&
                window.LoginButtonForTests.Visibility == Visibility.Visible &&
                window.UploadDescriptionBoxForTests.Text == "nonce 过期时保留投稿说明。" &&
                window.UploadPackagePathForTests == Path.GetFullPath(originalZipPath) &&
                FindNamed<TextBlock>(window, "MarketAccountStatusText").Text.Contains("过期", StringComparison.Ordinal),
                "投稿 nonce 失效清账号、显示重新登录并保留说明和原始包");
            Check(File.Exists(originalZipPath), "投稿 nonce 失效后原始 ZIP 仍存在");

            authProvider.CookieAfterNextOpen = SubmitCookie;
            loginOpens = authProvider.OpenCount;
            Click(FindNamed<Button>(window, "UploadLoginButton"), "投稿 nonce 失效后重新登录");
            await WaitUntilAsync(() => authProvider.OpenCount == loginOpens + 1 && client.Account is { CanSubmit: true } &&
                window.SubmitUploadButtonForTests.IsEnabled,
                "重新登录后投稿表单、说明与按钮恢复");

            await window.LoadUploadPackageForTestsAsync(originalZipPath);
            FillUploadForm(window, "服务端错误时保留投稿说明。", "浅色");
            server.FailNextSubmission();
            Click(window.SubmitUploadButtonForTests, "触发 fake 投稿服务错误");
            await WaitUntilAsync(() => window.UploadStatusTextForTests.Text.Contains("表单与包仍保留", StringComparison.Ordinal),
                "投稿错误显示输入与包保留提示");
            Check(window.UploadDescriptionBoxForTests.Text == "服务端错误时保留投稿说明。" && File.Exists(originalZipPath),
                "服务端投稿错误后保留说明和原始 ZIP");

            await window.LoadUploadPackageForTestsAsync(originalZipPath);
            FillUploadForm(window, "取消投稿后保留说明。", "浅色");
            var submissionStarted = server.BlockNextSubmission();
            Click(window.SubmitUploadButtonForTests, "开始可取消的 fake 投稿");
            await submissionStarted.Task.WaitAsync(WaitLimit);
            await WaitUntilAsync(() => window.IsSubmittingForTests && window.CancelSubmissionButtonForTests.IsEnabled,
                "显示投稿取消按钮");
            Click(window.CancelSubmissionButtonForTests, "取消投稿");
            await WaitUntilAsync(() => !window.IsSubmittingForTests &&
                window.UploadStatusTextForTests.Text.Contains("已取消", StringComparison.Ordinal),
                "取消投稿后显示取消状态");
            Check(window.UploadDescriptionBoxForTests.Text == "取消投稿后保留说明。" && File.Exists(originalZipPath),
                "取消投稿后保留说明和原始 ZIP");

            Check(server.AllWriteRequestsAuthenticated,
                "客户端所有点赞、评论与投稿写请求都来自明确登录状态并带认证头");
            Check(server.PackageDownloadCount == 0,
                "市场社区浏览没有触发目录皮肤包下载");

            window.Close();
            await closed.Task.WaitAsync(WaitLimit);
            await Task.Delay(100);
            Check(File.Exists(originalZipPath), "关闭市场窗口后仍保留用户原始 ZIP");
            Console.WriteLine($"市场社区 UI 截图已保存：{screenshotDirectory}");
        }
        finally
        {
            if (window is { IsVisible: true })
            {
                window.Close();
            }
            else if (client is not null)
            {
                client.Dispose();
            }

            if (authProvider is not null)
            {
                await authProvider.DisposeAsync();
            }

            AppThemeService.SetMode(originalTheme);
            var tempPath = Path.GetFullPath(testRoot);
            var tempBase = Path.GetFullPath(Path.GetTempPath());
            if (Directory.Exists(tempPath) && tempPath.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }

    private static void FillUploadForm(SkinMarketWindow window, string description, string tags)
    {
        window.UploadDescriptionBoxForTests.Text = description;
        window.UploadAuthorBoxForTests.Text = "Ancimen";
        window.UploadLicenseBoxForTests.Text = "保留所有权利。";
        window.UploadMinimumVersionBoxForTests.Text = "1.0.0";
        window.UploadTagsBoxForTests.Text = tags;
    }

    private static bool IsAccountBusy(SkinMarketWindow window) =>
        FindNamed<TextBlock>(window, "MarketAccountStatusText").Text.StartsWith("正在", StringComparison.Ordinal);

    private static void Click(Button button, string label)
    {
        Check(button.IsEnabled && button.Visibility == Visibility.Visible,
            $"{label}按钮已启用且可见");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static T FindNamed<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"找不到市场控件 {name}。");

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string label)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                Console.WriteLine($"通过：{label}");
                return;
            }

            await Task.Delay(40);
        }

        throw new TimeoutException($"等待超时：{label}");
    }

    private static async Task SetWindowSizeAsync(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        await WaitUntilAsync(() => Math.Abs(window.ActualWidth - width) < 2 && Math.Abs(window.ActualHeight - height) < 2,
            $"市场窗口调整到 {width:0}×{height:0}");
        await window.Dispatcher.InvokeAsync(() =>
        {
            window.UpdateLayout();
            if (window.Content is FrameworkElement content)
            {
                content.UpdateLayout();
            }
        }, DispatcherPriority.Render);
    }

    private static async Task SaveScreenshotAsync(Window window, string path)
    {
        await window.Dispatcher.InvokeAsync(() =>
        {
            window.UpdateLayout();
            if (window.Content is FrameworkElement content)
            {
                content.UpdateLayout();
            }
        }, DispatcherPriority.Render);
        await Program.SaveWindowScreenshotAsync(window, path);
    }

    private static byte[] ReadEmbeddedPackage(string name)
    {
        var resourceName = $"SkinManager.Preview.Tests.Resources.{name}.zip";
        using var source = typeof(MarketCommunityUiChecks).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到嵌入的 {name} 样例包。");
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{label}失败。");
        }

        Console.WriteLine($"通过：{label}");
    }

    private sealed class FakeMarketAuthProvider : IMarketAccountAuthProvider
    {
        public string? CookieHeader { get; private set; }
        public string? CookieAfterNextOpen { get; set; }
        public Uri? LastStartUri { get; private set; }
        public int OpenCount { get; private set; }
        public int InitializeCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCount++;
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
            LastStartUri = startUri;
            OpenCount++;
            if (CookieAfterNextOpen is not null)
            {
                CookieHeader = CookieAfterNextOpen;
                CookieAfterNextOpen = null;
            }

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

    private sealed class FakeMarketHandler : HttpMessageHandler
    {
        private enum ReplyKind
        {
            Success,
            Fail,
            ExpiredNonce,
            Block
        }

        private readonly IReadOnlyDictionary<string, SkinPackage> _packages;
        private readonly IReadOnlyDictionary<string, byte[]> _packageBytes;
        private TaskCompletionSource? _submissionStarted;
        private int _nextCommentReply;
        private int _nextSubmissionReply;
        private int _likePostCount;
        private int _commentPostCount;
        private int _submissionPostCount;
        private int _writeRequestCount;
        private int _packageDownloadCount;
        private volatile bool _isLiked = true;
        private byte[]? _lastSubmissionBody;

        public FakeMarketHandler(IReadOnlyList<SkinPackage> packages, IReadOnlyList<byte[]> packageBytes)
        {
            if (packages.Count != packageBytes.Count)
            {
                throw new ArgumentException("市场测试包与 ZIP 字节数量不一致。");
            }

            _packages = packages.ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
            _packageBytes = packages.Select((package, index) => (package.Id, Bytes: packageBytes[index]))
                .ToDictionary(pair => pair.Id, pair => pair.Bytes, StringComparer.OrdinalIgnoreCase);
        }

        public int LikePostCount => Volatile.Read(ref _likePostCount);
        public int CommentPostCount => Volatile.Read(ref _commentPostCount);
        public int SubmissionPostCount => Volatile.Read(ref _submissionPostCount);
        public int WriteRequestCount => Volatile.Read(ref _writeRequestCount);
        public int PackageDownloadCount => Volatile.Read(ref _packageDownloadCount);
        public byte[]? LastSubmissionBody => Volatile.Read(ref _lastSubmissionBody);

        private int _invalidWriteAuthentication;
        public bool AllWriteRequestsAuthenticated => Volatile.Read(ref _invalidWriteAuthentication) == 0;

        public void FailNextComment() => Interlocked.Exchange(ref _nextCommentReply, (int)ReplyKind.Fail);
        public void ExpireNextCommentNonce() => Interlocked.Exchange(ref _nextCommentReply, (int)ReplyKind.ExpiredNonce);
        public void ExpireNextSubmissionNonce() => Interlocked.Exchange(ref _nextSubmissionReply, (int)ReplyKind.ExpiredNonce);
        public void FailNextSubmission() => Interlocked.Exchange(ref _nextSubmissionReply, (int)ReplyKind.Fail);

        public TaskCompletionSource BlockNextSubmission()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _submissionStarted, gate);
            Interlocked.Exchange(ref _nextSubmissionReply, (int)ReplyKind.Block);
            return gate;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidDataException("Fake 市场收到没有 URI 的请求。");
            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !uri.Host.Equals("blog.ancimen.top", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Fake 市场拒绝本站以外的 HTTP 请求。");
            }

            var route = QueryValue(uri, "rest_route");
            if (uri.Query.Contains("page_id=39", StringComparison.Ordinal))
            {
                var cookie = Header(request, "Cookie");
                var loggedIn = IsLoginCookie(cookie);
                var userId = cookie?.Contains("market-readonly", StringComparison.Ordinal) == true ? 43 : 42;
                return Html(BootstrapHtml(loggedIn, userId, canSubmit: loggedIn && userId == 42));
            }

            if (route == "/wp/v2/users/me")
            {
                var userId = Header(request, "Cookie")?.Contains("market-readonly", StringComparison.Ordinal) == true ? 43 : 42;
                return Json(new { id = userId, name = userId == 42 ? "离线投稿用户" : "离线只读用户" });
            }

            if (route == "/skin-manager/v1/skins")
            {
                return Catalog(uri, request);
            }

            if (route is not null && route.StartsWith("/skin-manager/v1/skins/", StringComparison.Ordinal) &&
                route.EndsWith("/comments", StringComparison.Ordinal))
            {
                var prefix = "/skin-manager/v1/skins/";
                var skinId = route.Substring(prefix.Length, route.Length - prefix.Length - "/comments".Length);
                if (request.Method == HttpMethod.Get)
                {
                    return Comments(uri, skinId);
                }

                return await CreateCommentAsync(request, cancellationToken).ConfigureAwait(false);
            }

            if (route is not null && route.StartsWith("/skin-manager/v1/skins/", StringComparison.Ordinal) &&
                route.EndsWith("/like", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return await SetLikeAsync(request, cancellationToken).ConfigureAwait(false);
            }

            if (route == "/skin-manager/v1/submissions" && request.Method == HttpMethod.Post)
            {
                return await SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            }

            if (uri.AbsolutePath.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase))
            {
                return Thumbnail(uri);
            }

            if (route is not null && route.EndsWith("/download", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _packageDownloadCount);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return JsonError(HttpStatusCode.NotFound, "offline_route_not_found", "Fake 市场没有此路由。");
        }

        private HttpResponseMessage Catalog(Uri uri, HttpRequestMessage request)
        {
            var page = Math.Max(1, int.TryParse(QueryValue(uri, "page"), out var parsedPage) ? parsedPage : 1);
            var perPage = Math.Max(1, int.TryParse(QueryValue(uri, "per_page"), out var parsedPerPage) ? parsedPerPage : 12);
            var cookie = Header(request, "Cookie");
            var loggedIn = IsLoginCookie(cookie);
            var items = _packages.Values
                .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .Select(package => new
                {
                    id = package.Id,
                    name = package.Name,
                    version = package.Version,
                    appearance = Appearance(package.Appearance),
                    description = package.Id == "saya" ? "深色海夜测试皮肤。" : "浅色测试皮肤。",
                    fullDescription = package.Id == "saya" ? "Saya 详情说明与夜色背景示例。" : "Ayanami 详情说明与海色背景示例。",
                    author = package.Definition.Author ?? "Ancimen",
                    submitter = "Ancimen",
                    licenseNotes = "保留所有权利。",
                    previewUrl = ThumbnailUrl(package.Id),
                    thumbnailUrl = ThumbnailUrl(package.Id),
                    detailUrl = $"https://blog.ancimen.top/skins/{package.Id}",
                    tags = new[] { new { id = package.Id == "saya" ? 18 : 12, name = package.Id == "saya" ? "深色" : "浅色" } },
                    likeCount = package.Id == "ayanami" ? 11 + (_isLiked ? 1 : 0) : 6,
                    commentCount = package.Id == "ayanami" ? 22 : 3,
                    likedByMe = loggedIn && package.Id == "ayanami" && _isLiked,
                    downloadUrl = $"https://blog.ancimen.top/?rest_route=%2Fskin-manager%2Fv1%2Fskins%2F{package.Id}%2Fdownload",
                    sha256 = Convert.ToHexString(SHA256.HashData(_packageBytes[package.Id])).ToLowerInvariant(),
                    size = (long)_packageBytes[package.Id].Length,
                    skinFormatVersion = package.Definition.FormatVersion,
                    minManagerVersion = "1.0.0",
                    updatedAt = "2026-11-10T00:00:00Z"
                }).ToArray();
            var start = Math.Min((page - 1) * perPage, items.Length);
            var pageItems = items.Skip(start).Take(perPage).ToArray();
            var response = new
            {
                formatVersion = 1,
                schemaVersion = 1,
                items = pageItems,
                pagination = new { page, perPage, total = items.Length, totalPages = (int)Math.Ceiling(items.Length / (double)perPage) },
                availableTags = new[] { new { id = 12, name = "浅色" }, new { id = 18, name = "深色" } }
            };

            return Json(response);
        }

        private static HttpResponseMessage Comments(Uri uri, string skinId)
        {
            var page = Math.Max(1, int.TryParse(QueryValue(uri, "page"), out var parsed) ? parsed : 1);
            var items = page == 1
                ? new object[]
                {
                    new { id = 101, author = "离线体验用户", content = $"{skinId} 的示例评论，适合预览详情排版。", date = "2026-11-09T10:30:00" },
                    new { id = 102, author = "海色收藏者", content = "背景图与浅色界面搭配自然。", date = "2026-11-08T15:20:00" }
                }
                : [new { id = 201, author = "分页用户", content = "第二页评论仍由 fake REST 返回。", date = "2026-11-07T09:10:00" }];
            return Json(new
            {
                items,
                pagination = new { page, perPage = 20, total = 21, totalPages = 2 }
            });
        }

        private async Task<HttpResponseMessage> SetLikeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RecordWriteAuthentication(request);
            var payload = await ReadBodyTextAsync(request, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            _isLiked = document.RootElement.GetProperty("liked").GetBoolean();
            Interlocked.Increment(ref _likePostCount);
            return Json(new { liked = _isLiked, likeCount = 11 + (_isLiked ? 1 : 0) });
        }

        private async Task<HttpResponseMessage> CreateCommentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RecordWriteAuthentication(request);
            _ = await ReadBodyTextAsync(request, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _commentPostCount);
            var outcome = (ReplyKind)Interlocked.Exchange(ref _nextCommentReply, (int)ReplyKind.Success);
            if (outcome == ReplyKind.ExpiredNonce)
            {
                return JsonError(HttpStatusCode.Forbidden, "rest_cookie_invalid_nonce", "市场登录状态已过期。");
            }

            if (outcome == ReplyKind.Fail)
            {
                return JsonError(HttpStatusCode.InternalServerError, "offline_comment_failed", "Fake 评论服务暂时不可用。");
            }

            return Json(HttpStatusCode.Created, new
            {
                comment = new { id = 301, author = "离线投稿用户", content = "这是一条等待审核的离线测试评论。", date = "2026-11-10T12:00:00" },
                pending = true,
                message = "评论已提交，审核后显示"
            });
        }

        private async Task<HttpResponseMessage> SubmitAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RecordWriteAuthentication(request);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastSubmissionBody, body);
            Interlocked.Increment(ref _submissionPostCount);
            var outcome = (ReplyKind)Interlocked.Exchange(ref _nextSubmissionReply, (int)ReplyKind.Success);
            if (outcome == ReplyKind.ExpiredNonce)
            {
                return JsonError(HttpStatusCode.Forbidden, "rest_cookie_invalid_nonce", "市场登录状态已过期。");
            }

            if (outcome == ReplyKind.Fail)
            {
                return JsonError(HttpStatusCode.InternalServerError, "offline_submission_failed", "Fake 投稿服务暂时不可用。");
            }

            if (outcome == ReplyKind.Block)
            {
                Volatile.Read(ref _submissionStarted)?.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return Json(new
            {
                item = new
                {
                    postId = 900 + SubmissionPostCount,
                    status = "pending",
                    id = "ayanami",
                    name = "Ayanami"
                }
            });
        }

        private HttpResponseMessage Thumbnail(Uri uri)
        {
            var id = uri.AbsolutePath.Contains("saya", StringComparison.OrdinalIgnoreCase) ? "saya" : "ayanami";
            var bytes = _packages[id].ImageBytes ?? throw new InvalidDataException($"{id} 样例缺少图片资源。");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("image/png") }
                }
            };
        }

        private void RecordWriteAuthentication(HttpRequestMessage request)
        {
            Interlocked.Increment(ref _writeRequestCount);
            var cookie = Header(request, "Cookie");
            var nonce = Header(request, "X-WP-Nonce");
            if (!IsLoginCookie(cookie) || nonce != RestNonce)
            {
                Interlocked.Exchange(ref _invalidWriteAuthentication, 1);
            }
        }

        private static async Task<string> ReadBodyTextAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };

        private static HttpResponseMessage Json(object value) => Json(HttpStatusCode.OK, value);

        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage JsonError(HttpStatusCode status, string code, string message) => Json(status, new
        {
            code,
            message,
            data = new { status = (int)status }
        });

        private static string BootstrapHtml(bool loggedIn, int userId, bool canSubmit)
        {
            var config = JsonSerializer.Serialize(new
            {
                restBase = "/?rest_route=/skin-manager/v1/",
                nonce = loggedIn ? RestNonce : string.Empty,
                isLoggedIn = loggedIn,
                userId = loggedIn ? userId : 0,
                canSubmit,
                loginUrl = "https://blog.ancimen.top/wp-login.php?redirect_to=https%3A%2F%2Fblog.ancimen.top%2F%3Fpage_id%3D39",
                marketUrl = "https://blog.ancimen.top/?page_id=39"
            });
            return $"<section data-smm-config=\"{WebUtility.HtmlEncode(config)}\"></section>";
        }

        private static bool IsLoginCookie(string? cookie) =>
            cookie?.Contains("market-submit", StringComparison.Ordinal) == true ||
            cookie?.Contains("market-readonly", StringComparison.Ordinal) == true;

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

        private static string? QueryValue(Uri uri, string name)
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=');
                var key = separator < 0 ? part : part[..separator];
                if (!Uri.UnescapeDataString(key).Equals(name, StringComparison.Ordinal))
                {
                    continue;
                }

                var value = separator < 0 ? string.Empty : part[(separator + 1)..];
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }

            return null;
        }

        private static string ThumbnailUrl(string skinId) =>
            $"https://blog.ancimen.top/wp-content/uploads/market-{skinId}.png";

        private static string Appearance(SkinAppearance appearance) => appearance switch
        {
            SkinAppearance.Light => "light",
            SkinAppearance.Dark => "dark",
            _ => "system"
        };
    }
}

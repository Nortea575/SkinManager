using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Market;

namespace SkinManager.Preview.Tests;

internal static class MarketUiChecks
{
    public static Func<SkinMarketClient> CreateReviewClientFactory(byte[] originalPackageBytes)
    {
        ArgumentNullException.ThrowIfNull(originalPackageBytes);
        var source = new MarketFixtureSource([CreateSkinFixture(originalPackageBytes)]);
        return () => new SkinMarketClient(new FixtureHandler(source));
    }

    public static Task RunAsync(
        string testRoot,
        BasePackage basePackage,
        byte[] originalPackageBytes,
        string screenshotDirectory) =>
        RunAsync(testRoot, basePackage, originalPackageBytes, null, screenshotDirectory);

    public static async Task RunAsync(
        string testRoot,
        BasePackage basePackage,
        byte[] ayanamiPackageBytes,
        byte[]? sayaPackageBytes,
        string screenshotDirectory)
    {
        var store = new SkinStore(Path.Combine(testRoot, "skin-market-empty-library"));
        store.Initialize();
        Check(store.GetCatalog().Count == 0, "市场界面测试从空皮肤库开始");

        var fixtures = new List<MarketSkinFixture> { CreateSkinFixture(ayanamiPackageBytes) };
        if (sayaPackageBytes is not null)
        {
            fixtures.Add(CreateSkinFixture(sayaPackageBytes));
        }

        var source = new MarketFixtureSource(fixtures);
        var originalThemeMode = AppThemeService.Mode;
        var mainWindow = new MainWindow(
            store,
            basePackage,
            previewProfileDirectory: Path.Combine(testRoot, "skin-market-unused-profile"),
            readRuntimeStatusOnLoad: false,
            closeAfterSuccessfulInstall: false,
            importReplacePrompt: _ => MessageBoxResult.No,
            skinMarketClientFactory: () => new SkinMarketClient(new FixtureHandler(source)))
        {
            Width = 960,
            Height = 640,
            Left = -2_000,
            Top = -2_000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            ShowInTaskbar = false
        };

        SkinMarketWindow? marketWindow = null;
        try
        {
            mainWindow.Show();
            await WaitUntilAsync(() => mainWindow.IsVisible && mainWindow.MarketEntryButton.IsEnabled,
                "空库窗口的皮肤市场入口可用");
            mainWindow.MarketEntryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => mainWindow.MarketWindow is { IsVisible: true }, "从主窗口打开市场");
            marketWindow = mainWindow.MarketWindow;
            if (marketWindow is null)
            {
                throw new InvalidDataException("主窗口没有创建市场窗口。");
            }

            await WaitUntilAsync(() => marketWindow.IsLoaded && marketWindow.Opacity >= 0.999,
                "市场窗口淡入完成后再捕获客户区");
            await WaitUntilAsync(() => marketWindow.Rows.Count == fixtures.Count,
                "Ayanami 与 Saya 皮肤条目加载完成");
            await WaitUntilAsync(() => marketWindow.Rows.All(row => row.HasThumbnail || row.HasThumbnailError),
                "列表封面缩略图读取完成");
            Check(marketWindow.Rows.Any(row => row.Id == "ayanami") && marketWindow.Rows.Any(row => row.Id == "saya") == (sayaPackageBytes is not null),
                "市场显示所选 Ayanami / Saya 样例");
            Check(marketWindow.Rows.All(row => row.Item.Author == "Ancimen"), "市场卡片展示皮肤作者");
            Check(marketWindow.Rows.All(row => row.SummaryLabel.Contains(row.Item.Version, StringComparison.Ordinal) &&
                  row.InstallStatus == "未安装" && row.AuthorAndSocialLabel.Contains("赞 ", StringComparison.Ordinal) &&
                  row.AuthorAndSocialLabel.Contains("评论 ", StringComparison.Ordinal)),
                "卡片展示版本、安装状态、点赞数和评论数");
            Check(marketWindow.IsCatalogVisible && !marketWindow.IsDetailsVisible && !marketWindow.IsListView,
                "默认进入网格目录视图");
            Check(source.ZipRequestCount == 0, "打开目录和下载封面缩略图不下载任何 ZIP 包");
            Check(marketWindow.AccountCookieBridgeForTests.Visibility == Visibility.Hidden &&
                  !FindVisualChildren<Microsoft.Web.WebView2.Wpf.WebView2>(marketWindow).Any(browser => browser.IsVisible),
                "市场目录只保留隐藏账号 Cookie 桥接，不创建效果预览视图");
            Check(FindMarketButton(marketWindow, marketWindow.Rows[0], "下载并安装皮肤").IsEnabled,
                "兼容且未安装的皮肤可以下载");

            source.SetAvailableTags(false);
            await marketWindow.RefreshAsync(resetPage: true);
            Check(marketWindow.TagBoxForTests.Visibility == Visibility.Collapsed,
                "服务端可用标签为空时隐藏标签筛选");
            source.SetAvailableTags(true);
            await marketWindow.RefreshAsync(resetPage: true);
            Check(marketWindow.TagBoxForTests.Visibility == Visibility.Visible,
                "服务端返回可用标签后显示标签筛选");

            marketWindow.ListViewButtonForTests.IsChecked = true;
            await WaitUntilAsync(() => marketWindow.IsListView, "切换到列表视图");
            Check(marketWindow.Rows.Count == fixtures.Count && source.ZipRequestCount == 0,
                "列表视图切换保留目录并且不下载 ZIP");
            marketWindow.GridViewButtonForTests.IsChecked = true;
            await WaitUntilAsync(() => !marketWindow.IsListView, "切回网格视图");

            var sayaRow = marketWindow.Rows.FirstOrDefault(row => row.Id == "saya") ?? marketWindow.Rows[0];
            var initialZipRequestCount = source.ZipRequestCount;
            marketWindow.OpenDetails(sayaRow);
            Check(marketWindow.IsDetailsVisible && !marketWindow.IsCatalogVisible, "打开市场原生详情页");
            Check(!marketWindow.DetailsDownloadButtonForTests.IsEnabled &&
                  sayaRow.CompatibilityLabel.Contains("需要管理器 99.0.0", StringComparison.Ordinal),
                "详情页标出不兼容最低版本并禁用安装按钮");
            Check(sayaRow.Item.EffectiveDescription.Contains("完整说明", StringComparison.Ordinal),
                "详情页数据包含完整说明");
            Check(source.ZipRequestCount == initialZipRequestCount, "打开皮肤详情不下载 ZIP");
            Check(marketWindow.AccountCookieBridgeForTests.Visibility == Visibility.Hidden &&
                  !FindVisualChildren<Microsoft.Web.WebView2.Wpf.WebView2>(marketWindow).Any(browser => browser.IsVisible),
                "原生皮肤详情不创建可见效果预览视图");
            await SaveWindowScreenshotAsync(
                marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-夜间-详情.png"));

            AppThemeService.SetMode(AppThemeMode.Day);
            await marketWindow.Dispatcher.InvokeAsync(marketWindow.UpdateLayout);
            await SaveWindowScreenshotAsync(
                marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-白昼-详情.png"));

            marketWindow.BackToCatalog();
            await WaitUntilAsync(() => marketWindow.IsCatalogVisible, "从详情返回市场目录");
            await WaitUntilAsync(() => Math.Abs(marketWindow.CatalogScrollOffset) < 1.0,
                "返回目录保留原滚动位置");
            await SaveWindowScreenshotAsync(
                marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-白昼-网格.png"));
            AppThemeService.SetMode(originalThemeMode);
            await marketWindow.Dispatcher.InvokeAsync(marketWindow.UpdateLayout);

            if (sayaPackageBytes is not null)
            {
                marketWindow.TagBoxForTests.SelectedValue = "12";
                await WaitUntilAsync(() => marketWindow.Rows.Count == 1 && marketWindow.Rows[0].Id == "ayanami",
                    "标签筛选更新目录");
                Check(source.LastCatalogQuery.Contains("tag=12", StringComparison.Ordinal), "目录请求发送选中的标签 ID");
                marketWindow.TagBoxForTests.SelectedIndex = 0;
                await WaitUntilAsync(() => marketWindow.Rows.Count == 2, "清除标签筛选恢复完整目录");

                marketWindow.SortBoxForTests.SelectedValue = "likes";
                await WaitUntilAsync(() => source.LastCatalogQuery.Contains("sort=likes", StringComparison.Ordinal) &&
                    marketWindow.Rows.Count == 2 && marketWindow.Rows[0].Id == "saya",
                    "最多点赞排序更新目录");
                Check(marketWindow.Rows[0].Id == "saya", "最多点赞排序按点赞数排列条目");
                marketWindow.SortBoxForTests.SelectedValue = "latest";
                await WaitUntilAsync(() => source.LastCatalogQuery.Contains("sort=latest", StringComparison.Ordinal) &&
                    marketWindow.Rows.Count == 2 && marketWindow.Rows[0].Id == "ayanami",
                    "最近更新排序恢复目录");

                var staleGate = source.BlockNextCatalogRequest();
                marketWindow.SearchBoxForTests.Text = "ayanami";
                var staleRequest = marketWindow.RefreshAsync(resetPage: true);
                await staleGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                marketWindow.SearchBoxForTests.Text = "saya";
                await marketWindow.RefreshAsync(resetPage: true);
                await staleRequest;
                Check(marketWindow.Rows.Count == 1 && marketWindow.Rows[0].Id == "saya" &&
                      source.LastCatalogQuery.Contains("q=saya", StringComparison.Ordinal),
                    "新搜索取消旧目录请求并只显示最新结果");
                marketWindow.SearchBoxForTests.Text = string.Empty;
                await marketWindow.RefreshAsync(resetPage: true);
                await WaitUntilAsync(() => marketWindow.Rows.Count == 2, "清除搜索恢复完整目录");

                marketWindow.DarkFilterButtonForTests.IsChecked = true;
                await WaitUntilAsync(() => marketWindow.Rows.Count == 1 && marketWindow.Rows[0].Id == "saya",
                    "深色外观筛选只显示 Saya");
                marketWindow.FilterAllButtonForTests.IsChecked = true;
                await WaitUntilAsync(() => marketWindow.Rows.Count == 2, "恢复全部外观目录");
            }

            await SaveWindowScreenshotAsync(
                marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-夜间-网格.png"));
            await WaitUntilAsync(() => !marketWindow.IsLoading, "市场目录请求完成后稳定滚动状态");
            await marketWindow.Dispatcher.InvokeAsync(
                marketWindow.UpdateLayout,
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await ResizeWindowForCaptureAsync(
                marketWindow, marketWindow.MinWidth, marketWindow.MinHeight, "市场窗口缩至最小尺寸");
            var catalogScrollViewer = marketWindow.CatalogScrollViewerForTests;
            catalogScrollViewer.ScrollToEnd();
            await WaitUntilAsync(
                () => catalogScrollViewer.ScrollableHeight <= 1 ||
                      catalogScrollViewer.VerticalOffset >= catalogScrollViewer.ScrollableHeight - 1,
                "最小窗口市场目录滚动到底部");
            await marketWindow.Dispatcher.InvokeAsync(marketWindow.UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
            var ayanamiDownloadButton = FindMarketButton(
                marketWindow, marketWindow.Rows.First(row => row.Id == "ayanami"), "下载并安装皮肤");
            var sayaDownloadButton = FindMarketButton(
                marketWindow, marketWindow.Rows.First(row => row.Id == "saya"), "下载并安装皮肤");
            Check(IsFullyVisibleInScrollViewer(ayanamiDownloadButton, catalogScrollViewer) &&
                  IsFullyVisibleInScrollViewer(sayaDownloadButton, catalogScrollViewer),
                "最小窗口可滚到目录底部并完整看到安装按钮");
            await SaveWindowScreenshotAsync(
                marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-夜间-最小窗口.png"));
            catalogScrollViewer.ScrollToTop();
            await ResizeWindowForCaptureAsync(marketWindow, 1060, 740, "市场窗口恢复常规尺寸");

            var ayanamiRow = marketWindow.Rows.First(row => row.Id == "ayanami");
            FindMarketButton(marketWindow, ayanamiRow, "下载并安装皮肤")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => store.GetCatalog().Any(entry => entry.Id == "ayanami"), "显式选择下载后安装到空皮肤库");
            await WaitUntilAsync(() => ayanamiRow.OperationStatus.Contains("加入本地皮肤库", StringComparison.Ordinal),
                "下载成功状态明确表示皮肤已加入皮肤库");
            Check(source.ZipRequestCount == 1, "只有用户点击安装按钮后才读取皮肤 ZIP 包");
            Check(store.LoadSkin("ayanami").Version == fixtures.First().Package.Version, "市场包按原 manifest 版本保存");
            Check(mainWindow.MarketEntryButton.IsEnabled, "下载后主窗口仍保持正常交互");

            var updatedBytes = RewritePackageVersion(ayanamiPackageBytes, "99.0.0");
            var updatedPackage = SkinPackageReader.ReadZipBytes(updatedBytes);
            source.ReplaceSkin(CreateSkinFixture(updatedBytes, Encoding.UTF8.GetBytes("invalid thumbnail image")));
            await marketWindow.RefreshAsync(resetPage: true);
            await WaitUntilAsync(() => marketWindow.Rows.First(row => row.Id == "ayanami").HasThumbnailError,
                "封面缩略图解码失败时显示重试状态");
            ayanamiRow = marketWindow.Rows.First(row => row.Id == "ayanami");
            Check(ayanamiRow.Item.Version == updatedPackage.Version, "刷新目录后显示市场更新版本");
            Check(FindMarketButton(marketWindow, ayanamiRow, "下载并安装皮肤").IsEnabled,
                "已安装旧版本的皮肤可以更新");

            FindMarketButton(marketWindow, ayanamiRow, "下载并安装皮肤")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => ayanamiRow.OperationStatus.Contains("已取消替换", StringComparison.Ordinal),
                "重复 ID 的替换取消状态可测试");
            Check(store.LoadSkin("ayanami").Version == fixtures.First().Package.Version,
                "取消替换后原有皮肤版本保持不变");
            Check(!ayanamiRow.OperationStatus.Contains("加入本地皮肤库", StringComparison.Ordinal),
                "取消替换没有显示安装成功");
            Check(!mainWindow.RuntimeStatusMessage.Text.Contains("正在连接并应用", StringComparison.Ordinal),
                "市场安装不会自动连接或注入皮肤");
            Console.WriteLine("通过：目录筛选、native 详情、缩略图和显式下载 UI 流程");
        }
        finally
        {
            AppThemeService.SetMode(originalThemeMode);
            if (marketWindow is { IsVisible: true })
            {
                marketWindow.Close();
            }

            if (mainWindow.IsVisible)
            {
                mainWindow.Close();
            }
        }
    }

    private static MarketSkinFixture CreateSkinFixture(byte[] packageBytes, byte[]? thumbnailBytes = null)
    {
        var package = SkinPackageReader.ReadZipBytes(packageBytes);
        thumbnailBytes ??= package.ImageBytes ?? Encoding.UTF8.GetBytes("skin has no cover image");
        return new MarketSkinFixture(package, packageBytes, thumbnailBytes);
    }

    private static byte[] RewritePackageVersion(byte[] packageBytes, string version)
    {
        using var sourceStream = new MemoryStream(packageBytes, writable: false);
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: true);
        using var outputStream = new MemoryStream();
        using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open();
                using var entryBytes = new MemoryStream();
                input.CopyTo(entryBytes);
                var bytes = entryBytes.ToArray();
                if (Path.GetFileName(entry.FullName).Equals("skin.json", StringComparison.OrdinalIgnoreCase))
                {
                    var manifest = JsonNode.Parse(bytes) as JsonObject
                        ?? throw new InvalidDataException("测试皮肤 manifest 不是 JSON 对象。");
                    manifest["version"] = version;
                    bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
                }

                var rewritten = output.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var target = rewritten.Open();
                target.Write(bytes, 0, bytes.Length);
            }
        }

        return outputStream.ToArray();
    }

    private static string CreateCatalogJson(
        IReadOnlyList<MarketSkinFixture> skins,
        int page,
        int perPage,
        int total,
        int totalPages,
        bool includeAvailableTags) =>
        JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            schemaVersion = 1,
            availableTags = includeAvailableTags ? new object[]
            {
                new { id = 12, name = "浅色" },
                new { id = 18, name = "深色" }
            } : Array.Empty<object>(),
            items = skins.Select(skin =>
            {
                var package = skin.Package;
                var isSaya = package.Id.Equals("saya", StringComparison.OrdinalIgnoreCase);
                return new
                {
                    id = package.Id,
                    name = package.Name,
                    version = package.Version,
                    appearance = package.Appearance switch
                    {
                        SkinAppearance.Light => "light",
                        SkinAppearance.Dark => "dark",
                        _ => "system"
                    },
                    description = isSaya
                        ? "深色主题搭配夜色背景，营造安静沉稳的界面。"
                        : "浅色主题搭配海色背景，适合清爽明亮的界面。",
                    fullDescription = isSaya
                        ? "Saya 完整说明：深色界面与夜色背景，展示详细市场资料。"
                        : "Ayanami 完整说明：浅色界面与海色背景，展示详细市场资料。",
                    author = "Ancimen",
                    submitter = "Ancimen",
                    licenseNotes = "保留所有权利。",
                    previewUrl = $"https://blog.ancimen.top/wp-content/uploads/legacy-{package.Id}.png",
                    thumbnailUrl = $"https://blog.ancimen.top/wp-content/uploads/market-ui-{package.Id}.png",
                    detailUrl = $"https://blog.ancimen.top/skins/{package.Id}",
                    tags = new[] { new { id = isSaya ? 18 : 12, name = isSaya ? "深色" : "浅色" } },
                    likeCount = isSaya ? 23 : 11,
                    commentCount = isSaya ? 5 : 2,
                    likedByMe = false,
                    downloadUrl = $"https://blog.ancimen.top/?rest_route=/skin-manager/v1/skins/{package.Id}/download",
                    sha256 = Convert.ToHexString(SHA256.HashData(skin.PackageBytes)).ToLowerInvariant(),
                    size = skin.PackageBytes.LongLength,
                    skinFormatVersion = package.Definition.FormatVersion,
                    minManagerVersion = isSaya ? "99.0.0" : "1.0.0",
                    updatedAt = isSaya ? "2026-11-05T00:00:00Z" : "2026-11-03T00:00:00Z"
                };
            }).ToArray(),
            pagination = new { page, perPage, total, totalPages }
        });

    private static HttpResponseMessage ContentResponse(byte[] bytes, string contentType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
        }
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                Console.WriteLine($"通过：{label}");
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"等待超时：{label}");
    }

    private static async Task SaveWindowScreenshotAsync(Window window, string path)
        => await Program.SaveWindowScreenshotAsync(window, path);

    private static async Task ResizeWindowForCaptureAsync(Window window, double width, double height, string label)
    {
        window.Width = width;
        window.Height = height;
        await WaitUntilAsync(
            () => Math.Abs(window.ActualWidth - width) < 2 && Math.Abs(window.ActualHeight - height) < 2,
            label);
        await window.Dispatcher.InvokeAsync(() =>
        {
            window.UpdateLayout();
            if (window.Content is FrameworkElement content)
            {
                content.UpdateLayout();
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private static bool IsFullyVisibleInScrollViewer(Button button, ScrollViewer scrollViewer)
    {
        var bounds = button.TransformToAncestor(scrollViewer)
            .TransformBounds(new Rect(new Point(), button.RenderSize));
        return new Rect(0, 0, scrollViewer.ViewportWidth, scrollViewer.ViewportHeight).Contains(bounds);
    }

    private static Button FindMarketButton(SkinMarketWindow window, SkinMarketRow row, string automationName)
    {
        window.UpdateLayout();
        if (window.Content is FrameworkElement content)
        {
            content.UpdateLayout();
        }

        return FindVisualChildren<Button>(window)
            .FirstOrDefault(button => button.IsVisible && ReferenceEquals(button.DataContext, row) &&
                string.Equals(AutomationProperties.GetName(button), automationName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"找不到市场卡片按钮：{automationName}。");
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{label}失败。");
        }
    }

    private sealed record MarketSkinFixture(SkinPackage Package, byte[] PackageBytes, byte[] ThumbnailBytes);

    private sealed class MarketFixtureSource(List<MarketSkinFixture> initialSkins)
    {
        private readonly object _sync = new();
        private List<MarketSkinFixture> _skins = initialSkins;
        private int _zipRequestCount;
        private string _lastCatalogQuery = string.Empty;
        private bool _includeAvailableTags = true;
        private CatalogGate? _nextCatalogGate;

        public int ZipRequestCount => Volatile.Read(ref _zipRequestCount);
        public string LastCatalogQuery => Volatile.Read(ref _lastCatalogQuery);
        public bool IncludeAvailableTags
        {
            get
            {
                lock (_sync)
                {
                    return _includeAvailableTags;
                }
            }
        }

        public IReadOnlyList<MarketSkinFixture> Snapshot()
        {
            lock (_sync)
            {
                return _skins.ToArray();
            }
        }

        public void RecordCatalogQuery(string query) => Volatile.Write(ref _lastCatalogQuery, query);
        public void RecordZipRequest() => Interlocked.Increment(ref _zipRequestCount);
        public void SetAvailableTags(bool available)
        {
            lock (_sync)
            {
                _includeAvailableTags = available;
            }
        }

        public CatalogGate BlockNextCatalogRequest()
        {
            var gate = new CatalogGate();
            lock (_sync)
            {
                _nextCatalogGate = gate;
            }

            return gate;
        }

        public async Task WaitAtCatalogGateAsync(CancellationToken cancellationToken)
        {
            CatalogGate? gate;
            lock (_sync)
            {
                gate = _nextCatalogGate;
                _nextCatalogGate = null;
            }

            if (gate is null)
            {
                return;
            }

            gate.Started.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
        }

        public void ReplaceSkin(MarketSkinFixture fixture)
        {
            lock (_sync)
            {
                _skins = _skins.Select(existing => existing.Package.Id.Equals(fixture.Package.Id, StringComparison.OrdinalIgnoreCase)
                    ? fixture
                    : existing).ToList();
            }
        }

        public sealed class CatalogGate
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class FixtureHandler(MarketFixtureSource source) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidDataException("测试 HTTP 请求缺少 URI。");
            var route = Uri.UnescapeDataString(uri.Query).Replace('+', ' ');
            var skins = source.Snapshot();
            if (route.Contains("/skin-manager/v1/skins/", StringComparison.Ordinal) && route.Contains("/download", StringComparison.Ordinal))
            {
                var id = route.Split("/skin-manager/v1/skins/", StringSplitOptions.None).Last()
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
                var skin = skins.FirstOrDefault(candidate => candidate.Package.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (skin is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                source.RecordZipRequest();
                return ContentResponse(skin.PackageBytes, "application/zip");
            }

            if (route.Contains("/skin-manager/v1/skins/", StringComparison.Ordinal) && route.Contains("/comments", StringComparison.Ordinal))
            {
                var query = ParseQuery(uri.Query);
                var page = query.TryGetValue("page", out var pageText) && int.TryParse(pageText, out var parsedPage)
                    ? parsedPage
                    : 1;
                return ContentResponse(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        items = Array.Empty<object>(),
                        pagination = new { page, perPage = 20, total = 0, totalPages = 0 }
                    })),
                    "application/json");
            }

            if (route.Contains("/skin-manager/v1/skins", StringComparison.Ordinal))
            {
                source.RecordCatalogQuery(uri.Query);
                await source.WaitAtCatalogGateAsync(cancellationToken);
                var query = ParseQuery(uri.Query);
                IEnumerable<MarketSkinFixture> filtered = skins;
                if (query.TryGetValue("appearance", out var appearance) && !string.IsNullOrWhiteSpace(appearance))
                {
                    filtered = filtered.Where(skin => skin.Package.Appearance.ToString().Equals(appearance, StringComparison.OrdinalIgnoreCase));
                }

                if (query.TryGetValue("tag", out var tag))
                {
                    filtered = filtered.Where(skin => (skin.Package.Id.Equals("saya", StringComparison.OrdinalIgnoreCase) ? "18" : "12") == tag);
                }

                if (query.TryGetValue("q", out var search) && !string.IsNullOrWhiteSpace(search))
                {
                    filtered = filtered.Where(skin => skin.Package.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        skin.Package.Id.Contains(search, StringComparison.OrdinalIgnoreCase));
                }

                var result = filtered.ToArray();
                if (query.TryGetValue("sort", out var sort) && sort == "likes")
                {
                    result = result.OrderByDescending(skin => skin.Package.Id.Equals("saya", StringComparison.OrdinalIgnoreCase) ? 23 : 11).ToArray();
                }

                return ContentResponse(
                    Encoding.UTF8.GetBytes(CreateCatalogJson(
                        result, 1, 12, result.Length, result.Length == 0 ? 0 : 1, source.IncludeAvailableTags)),
                    "application/json");
            }

            if (uri.AbsolutePath.StartsWith("/wp-content/uploads/market-ui-", StringComparison.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(uri.AbsolutePath).Replace("market-ui-", string.Empty, StringComparison.OrdinalIgnoreCase);
                var skin = skins.FirstOrDefault(candidate => candidate.Package.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                return skin is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : ContentResponse(skin.ThumbnailBytes, "image/png");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part.Length > 1 ? part[1].Replace('+', ' ') : string.Empty),
                StringComparer.Ordinal);
    }
}

using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Market;

namespace SkinManager.Preview.Tests;

internal static class MarketUiPreviewChecks
{
    public static async Task RunAsync(
        string testRoot,
        BasePackage basePackage,
        byte[] ayanamiBytes,
        byte[] sayaBytes,
        string screenshotDirectory)
    {
        Directory.CreateDirectory(testRoot);
        Directory.CreateDirectory(screenshotDirectory);
        var ayanamiPackage = SkinPackageReader.ReadZipBytes(ayanamiBytes);
        var sayaPackage = SkinPackageReader.ReadZipBytes(sayaBytes);
        var source = new MarketFixtureSource(
        [
            new FixturePackage(ayanamiPackage, ayanamiBytes, ayanamiPackage.ImageBytes ?? throw new InvalidDataException("Ayanami 缺少背景图片。")),
            new FixturePackage(sayaPackage, sayaBytes, sayaPackage.ImageBytes ?? throw new InvalidDataException("Saya 缺少背景图片。"))
        ]);
        source.FailNextPreview("ayanami");

        var store = new SkinStore(Path.Combine(testRoot, "skin-market-preview-library"));
        store.Initialize();
        Check(store.GetCatalog().Count == 0, "真实预览专测从空皮肤库开始");

        var mainWindow = new MainWindow(
            store,
            basePackage,
            previewProfileDirectory: Path.Combine(testRoot, "skin-market-preview-browser"),
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
                "空库窗口的市场入口可用");
            mainWindow.MarketEntryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => mainWindow.MarketWindow is { IsVisible: true }, "打开真实双皮肤市场目录");
            marketWindow = mainWindow.MarketWindow
                ?? throw new InvalidDataException("主窗口没有创建市场窗口。");

            await WaitUntilAsync(() => marketWindow.Rows.Count == 2, "Ayanami 与 Saya 两张真实皮肤卡片加载");
            var ayanamiRow = marketWindow.Rows.Single(row => row.Id == ayanamiPackage.Id);
            var sayaRow = marketWindow.Rows.Single(row => row.Id == sayaPackage.Id);
            await WaitUntilAsync(() => ayanamiRow.HasPreviewError && sayaRow.HasThumbnail,
                "Ayanami 首次图片解码失败且 Saya 图片成功显示");
            Check(!string.IsNullOrWhiteSpace(ayanamiRow.PreviewError) &&
                  ayanamiRow.PreviewError.Contains("图片", StringComparison.Ordinal),
                "图片失败提供具体重试提示，不影响其他卡片");
            var retryImageButton = FindCardButton(marketWindow, ayanamiRow, "重试预览图片");
            Check(retryImageButton.IsEnabled && Equals(retryImageButton.ToolTip, ayanamiRow.PreviewError),
                "图片失败时单张重试按钮可用");
            Check(FindCardButton(marketWindow, ayanamiRow, "打开皮肤效果预览").IsEnabled &&
                  FindCardButton(marketWindow, ayanamiRow, "下载并安装皮肤").IsEnabled,
                "图片失败不会禁用皮肤效果预览和下载安装");
            await SaveWindowScreenshotAsync(marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-图片失败可重试-1000x690.png"));

            retryImageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => ayanamiRow.HasThumbnail, "点击单张重试后读取到真实 Ayanami 背景图");
            Check(source.PreviewRequestCount("ayanami") >= 2, "单张重试重新请求图片地址");
            await WaitUntilAsync(() => sayaRow.HasThumbnail, "真实 Saya 背景图仍可显示");

            marketWindow.Width = 1000;
            marketWindow.Height = 690;
            await WaitForLayoutAsync(marketWindow);
            await SaveWindowScreenshotAsync(marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-双真实背景-1000x690.png"));

            marketWindow.Width = marketWindow.MinWidth;
            marketWindow.Height = marketWindow.MinHeight;
            await WaitForLayoutAsync(marketWindow);
            await SaveWindowScreenshotAsync(marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-双真实背景-880x560.png"));

            var scrollViewer = marketWindow.CatalogScrollViewerForTests;
            var ayanamiDetails = FindVisualChildren<Expander>(marketWindow)
                .First(expander => ReferenceEquals(expander.DataContext, ayanamiRow));
            ayanamiDetails.IsExpanded = true;
            await WaitForLayoutAsync(marketWindow);
            Check(scrollViewer.ScrollableHeight > 0, "展开详情后市场卡片列表可纵向滚动");
            var expectedOffset = Math.Min(72, scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(expectedOffset);
            await WaitUntilAsync(() => Math.Abs(scrollViewer.VerticalOffset - expectedOffset) < 1,
                "市场卡片滚动到非零位置");
            var savedOffset = scrollViewer.VerticalOffset;

            await OpenPreviewThroughCardAsync(marketWindow, sayaRow);
            await WaitUntilAsync(() => marketWindow.PreviewRenderer?.CurrentResolvedSkin is not null,
                "点击 Saya 大图后静态皮肤渲染完成");
            await VerifySayaPreviewAsync(marketWindow, sayaPackage);
            Check(marketWindow.PreviewPackage?.Id == sayaPackage.Id, "效果预览使用已校验且仅驻留内存的 Saya 包");
            Check(store.GetCatalog().Count == 0, "仅预览不会写入本地皮肤库");
            Check(marketWindow.PreviewInstallButtonIsEnabledForTests,
                "预览包校验后仍可从效果预览页下载并安装");

            await SavePreviewScreenshotAsync(marketWindow,
                Path.Combine(screenshotDirectory, "皮肤效果预览-Saya-880x560.png"));
            marketWindow.Width = 1000;
            marketWindow.Height = 690;
            await WaitForLayoutAsync(marketWindow);
            await SavePreviewScreenshotAsync(marketWindow,
                Path.Combine(screenshotDirectory, "皮肤效果预览-Saya-1000x690.png"));

            FindWindowButton(marketWindow, "返回皮肤市场").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !marketWindow.IsPreviewPageVisible && marketWindow.PreviewRenderer is null &&
                marketWindow.PreviewBrowserHostForTests.Content is null,
                "返回市场时释放 WebView2 与渲染器");
            await WaitUntilAsync(() => Math.Abs(scrollViewer.VerticalOffset - savedOffset) < 1,
                "返回后恢复原市场滚动位置");
            Check(marketWindow.PreviewPackage is null && store.GetCatalog().Count == 0,
                "返回时丢弃内存预览包且不写入本地皮肤库");

            marketWindow.DarkFilterButtonForTests.IsChecked = true;
            await WaitUntilAsync(() => marketWindow.Rows.Count == 1 && marketWindow.Rows[0].Id == sayaPackage.Id,
                "深色筛选只显示 Saya");
            await OpenPreviewThroughCardAsync(marketWindow, marketWindow.Rows[0]);
            await WaitUntilAsync(() => marketWindow.PreviewRenderer?.CurrentResolvedSkin is not null,
                "深色筛选状态下仍可打开效果预览");
            FindWindowButton(marketWindow, "返回皮肤市场").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !marketWindow.IsPreviewPageVisible && marketWindow.Rows.Count == 1,
                "返回后保留深色筛选结果");
            Check(marketWindow.DarkFilterButtonForTests.IsChecked == true && marketWindow.Rows[0].Id == sayaPackage.Id,
                "预览往返保留外观筛选");
            Check(store.GetCatalog().Count == 0, "第二次预览往返仍未写入本地皮肤库");

            marketWindow.DarkFilterButtonForTests.IsChecked = false;
            marketWindow.FilterAllButtonForTests.IsChecked = true;
            await WaitUntilAsync(() => marketWindow.Rows.Count == 2, "恢复全部外观筛选");
            ayanamiRow = marketWindow.Rows.Single(row => row.Id == ayanamiPackage.Id);
            sayaRow = marketWindow.Rows.Single(row => row.Id == sayaPackage.Id);
            source.DelayNextDownload("ayanami");
            var delayedPreview = marketWindow.OpenPreviewAsync(ayanamiRow);
            await source.WaitForDelayedDownloadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            marketWindow.BackToCatalog();
            await delayedPreview;
            await Task.Delay(250);
            Check(!marketWindow.IsPreviewPageVisible && marketWindow.PreviewRenderer is null &&
                  marketWindow.PreviewBrowserHostForTests.Content is null,
                "快速返回取消未完成的包读取，迟到结果不回闪预览页");
            Check(store.GetCatalog().Count == 0, "快速返回没有写入任何皮肤");

            var sayaDownloadButton = FindCardButton(marketWindow, sayaRow, "下载并安装皮肤");
            Check(sayaDownloadButton.IsEnabled, "返回市场后下载安装按钮仍可用");
            sayaDownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => store.GetCatalog().Any(entry => entry.Id == sayaPackage.Id),
                "返回市场后仍可下载并安装 Saya");
            Check(store.LoadSkin(sayaPackage.Id).Version == sayaPackage.Version,
                "市场下载保存原始 manifest 版本");
            Check(!mainWindow.RuntimeStatusMessage.Text.Contains("正在连接并应用", StringComparison.Ordinal),
                "市场预览和安装不会连接或注入皮肤");
            Console.WriteLine("通过：真实双背景卡片、图片单张重试、静态预览、往返状态、取消与下载");
        }
        finally
        {
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

    private static async Task OpenPreviewThroughCardAsync(SkinMarketWindow window, SkinMarketRow row)
    {
        await WaitForLayoutAsync(window);
        var button = FindCardButton(window, row, "打开皮肤效果预览");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.IsPreviewPageVisible && !window.IsPackagePreviewLoading &&
            window.PreviewRenderer?.CurrentResolvedSkin is not null,
            $"打开“{row.Name}”静态皮肤效果预览");
    }

    private static async Task VerifySayaPreviewAsync(SkinMarketWindow window, SkinPackage package)
    {
        var renderer = window.PreviewRenderer ?? throw new InvalidDataException("市场预览没有创建渲染器。");
        var resolved = renderer.CurrentResolvedSkin ?? throw new InvalidDataException("市场预览没有 resolved skin。");
        var expected = SkinResolver.Resolve(package, SkinAppearance.Dark, SkinScene.Home);
        Check(resolved.Appearance == SkinAppearance.Dark && resolved.Scene == SkinScene.Home,
            "Saya 包按深色首页场景解析");
        Check(resolved.Colors.Count == expected.Colors.Count && resolved.Colors.All(pair =>
                expected.Colors.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "静态效果预览使用 Saya manifest 的颜色令牌");
        Check(resolved.Background?.ImageBytes is { Length: > 0 } backgroundBytes &&
              package.ImageBytes is { Length: > 0 } packageBytes && backgroundBytes.AsSpan().SequenceEqual(packageBytes),
            "静态预览背景资源与 Saya 包内图片一致");
        var domColorJson = await renderer.ExecuteScriptAsync(
            "(() => { const target=document.querySelector('[data-skin-part=\"window\"]'); return target ? getComputedStyle(target).color : ''; })()");
        var domColor = JsonSerializer.Deserialize<string>(domColorJson)
            ?? throw new InvalidDataException("静态预览 DOM 未返回窗口文字颜色。");
        Check(domColor == CssColorToBrowserValue(expected.Colors["color.text.primary"]),
            "静态预览 DOM 实际颜色与 Saya manifest 令牌一致");
        var layoutProbe = await renderer.ExecuteScriptAsync("""
            (() => {
              const welcome = Array.from(document.querySelectorAll("h1"))
                .find(element => element.textContent?.trim() === "Welcome back");
              const header = document.querySelector('[data-skin-part="header"]');
              const welcomeScroller = welcome?.closest('[class*="overflow-y-auto"]');
              const welcomeRect = welcome?.getBoundingClientRect();
              const headerRect = header?.getBoundingClientRect();
              return {
                viewportHeight: window.innerHeight,
                headerBottom: headerRect?.bottom ?? -1,
                welcomeTop: welcomeRect?.top ?? -1,
                welcomeBottom: welcomeRect?.bottom ?? -1,
                scrollHeight: welcomeScroller?.scrollHeight ?? 0,
                clientHeight: welcomeScroller?.clientHeight ?? 0,
                scrollTop: welcomeScroller?.scrollTop ?? -1,
                overflowY: welcomeScroller ? getComputedStyle(welcomeScroller).overflowY : "missing",
              };
            })()
            """);
        using var layout = JsonDocument.Parse(layoutProbe);
        var metrics = layout.RootElement;
        var viewportHeight = metrics.GetProperty("viewportHeight").GetDouble();
        var headerBottom = metrics.GetProperty("headerBottom").GetDouble();
        var welcomeTop = metrics.GetProperty("welcomeTop").GetDouble();
        var welcomeBottom = metrics.GetProperty("welcomeBottom").GetDouble();
        var scrollHeight = metrics.GetProperty("scrollHeight").GetInt32();
        var clientHeight = metrics.GetProperty("clientHeight").GetInt32();
        Check(welcomeTop >= headerBottom - 0.5 && welcomeBottom <= viewportHeight &&
              metrics.GetProperty("overflowY").GetString() == "auto" && scrollHeight > clientHeight,
            "最小预览的 Welcome back 完整位于顶栏下方，首页内容可独立滚动");
        Console.WriteLine($"最小预览 DOM：viewport={viewportHeight:0}，header.bottom={headerBottom:0.##}，Welcome={welcomeTop:0.##}–{welcomeBottom:0.##}，scroll={clientHeight}/{scrollHeight}。");
    }

    private static string CssColorToBrowserValue(string value)
    {
        if (!CssColorParser.TryParse(value, out var color))
        {
            throw new InvalidDataException($"无法解析皮肤测试颜色 {value}。");
        }

        return color.Alpha == byte.MaxValue
            ? $"rgb({color.Red}, {color.Green}, {color.Blue})"
            : $"rgba({color.Red}, {color.Green}, {color.Blue}, {(color.Alpha / 255d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
    }

    private static async Task SaveWindowScreenshotAsync(Window window, string path)
    {
        var content = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("市场窗口没有客户区内容。");
        await WaitForLayoutAsync(window);
        var margin = content.Margin;
        var clientWidth = content.ActualWidth + margin.Left + margin.Right;
        var clientHeight = content.ActualHeight + margin.Top + margin.Bottom;
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(clientWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(clientHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var drawingVisual = new DrawingVisual();
        using (var drawing = drawingVisual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, clientWidth, clientHeight));
            drawing.DrawRectangle(new VisualBrush(content), null,
                new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }

        bitmap.Render(drawingVisual);
        await WritePngAsync(bitmap, path);
    }

    private static async Task SavePreviewScreenshotAsync(SkinMarketWindow window, string path)
    {
        var content = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("市场窗口没有客户区内容。");
        var browser = window.PreviewBrowserHostForTests.Content as WebView2
            ?? throw new InvalidOperationException("市场静态预览 WebView2 不存在。");
        var core = browser.CoreWebView2
            ?? throw new InvalidOperationException("市场静态预览 WebView2 尚未初始化。");
        await WaitForLayoutAsync(window);

        var dpi = VisualTreeHelper.GetDpi(window);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var windowBitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        windowBitmap.Render(window);
        windowBitmap.Freeze();

        await using var previewStream = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, previewStream);
        previewStream.Position = 0;
        var previewBitmap = BitmapFrame.Create(previewStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        previewBitmap.Freeze();

        var browserBounds = browser.TransformToAncestor(window)
            .TransformBounds(new Rect(new Point(0, 0), browser.RenderSize));
        var composition = new DrawingVisual();
        using (var drawing = composition.RenderOpen())
        {
            drawing.DrawImage(windowBitmap, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            drawing.DrawImage(previewBitmap, browserBounds);
        }

        var composedBitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        composedBitmap.Render(composition);
        composedBitmap.Freeze();

        var margin = content.Margin;
        var clientWidth = content.ActualWidth + margin.Left + margin.Right;
        var clientHeight = content.ActualHeight + margin.Top + margin.Bottom;
        var cropWidth = Math.Max(1, (int)Math.Ceiling(clientWidth * dpi.DpiScaleX));
        var cropHeight = Math.Max(1, (int)Math.Ceiling(clientHeight * dpi.DpiScaleY));
        if (cropWidth > pixelWidth || cropHeight > pixelHeight)
        {
            throw new InvalidOperationException("市场预览客户区裁切超出窗口截图范围。");
        }

        var clientBitmap = new CroppedBitmap(composedBitmap, new Int32Rect(0, 0, cropWidth, cropHeight));
        clientBitmap.Freeze();
        await WritePngAsync(clientBitmap, path);
        Console.WriteLine($"市场预览截图 {Path.GetFileName(path)}：窗口 {window.ActualWidth:0.#}×{window.ActualHeight:0.#}，WebView {browserBounds.X:0.#},{browserBounds.Y:0.#} {browserBounds.Width:0.#}×{browserBounds.Height:0.#}，客户区裁切 {cropWidth}×{cropHeight}。" );
    }

    private static async Task WritePngAsync(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        await output.FlushAsync();
        Check(output.Length > 1_000, $"输出 {Path.GetFileName(path)} 截图");
    }

    private static async Task WaitForLayoutAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() =>
        {
            window.UpdateLayout();
            if (window.Content is FrameworkElement content)
            {
                content.UpdateLayout();
            }
        }, DispatcherPriority.Loaded);
        await Task.Delay(150);
    }

    private static Button FindCardButton(SkinMarketWindow window, SkinMarketRow row, string automationName) =>
        FindVisualChildren<Button>(window).FirstOrDefault(button => ReferenceEquals(button.DataContext, row) &&
            AutomationProperties.GetName(button).Equals(automationName, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"找不到市场卡片按钮：{automationName}。");

    private static Button FindWindowButton(SkinMarketWindow window, string automationName) =>
        FindVisualChildren<Button>(window).FirstOrDefault(button =>
            AutomationProperties.GetName(button).Equals(automationName, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"找不到市场按钮：{automationName}。");

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

    private static async Task WaitUntilAsync(Func<bool> predicate, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
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

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{label}失败。");
        }

        Console.WriteLine($"通过：{label}");
    }

    private sealed record FixturePackage(SkinPackage Package, byte[] PackageBytes, byte[] PreviewBytes);

    private sealed class MarketFixtureSource(IReadOnlyList<FixturePackage> packages)
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, int> _previewRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _invalidPreviewCount = new(StringComparer.OrdinalIgnoreCase);
        private string? _delayedDownloadId;
        private TaskCompletionSource _delayedDownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PreviewRequestCount(string id)
        {
            lock (_sync)
            {
                return _previewRequests.GetValueOrDefault(id);
            }
        }

        public void FailNextPreview(string id)
        {
            lock (_sync)
            {
                _invalidPreviewCount[id] = 1;
            }
        }

        public void DelayNextDownload(string id)
        {
            lock (_sync)
            {
                _delayedDownloadId = id;
                _delayedDownloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task WaitForDelayedDownloadAsync()
        {
            lock (_sync)
            {
                return _delayedDownloadStarted.Task;
            }
        }

        public bool TryStartDelayedDownload(string id, out TaskCompletionSource started)
        {
            lock (_sync)
            {
                if (_delayedDownloadId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
                {
                    _delayedDownloadId = null;
                    started = _delayedDownloadStarted;
                    return true;
                }

                started = null!;
                return false;
            }
        }

        public void CountPreview(string id)
        {
            lock (_sync)
            {
                _previewRequests[id] = _previewRequests.GetValueOrDefault(id) + 1;
            }
        }

        public byte[] GetPreview(string id)
        {
            lock (_sync)
            {
                if (_invalidPreviewCount.GetValueOrDefault(id) > 0)
                {
                    _invalidPreviewCount[id]--;
                    return Encoding.UTF8.GetBytes("invalid image fixture");
                }
            }

            return packages.First(package => package.Package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).PreviewBytes;
        }

        public FixturePackage GetPackage(string id) => packages.First(package =>
            package.Package.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<FixturePackage> Filter(string query, string appearance)
        {
            IEnumerable<FixturePackage> filtered = packages;
            if (!string.IsNullOrWhiteSpace(appearance))
            {
                filtered = filtered.Where(package => package.Package.Appearance.ToString()
                    .Equals(appearance, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = filtered.Where(package => package.Package.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    package.Package.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            return filtered.ToArray();
        }
    }

    private sealed class FixtureHandler(MarketFixtureSource source) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidDataException("测试 HTTP 请求缺少 URI。");
            var route = Uri.UnescapeDataString(uri.Query).Replace('+', ' ');
            if (route.Contains("/skin-manager/v1/skins/", StringComparison.Ordinal) &&
                route.Contains("/download", StringComparison.Ordinal))
            {
                var id = route.Split("/skins/", StringSplitOptions.None)[1].Split('/', StringSplitOptions.None)[0];
                if (source.TryStartDelayedDownload(id, out var started))
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return ContentResponse(source.GetPackage(id).PackageBytes, "application/zip");
            }

            if (route.Contains("/skin-manager/v1/skins", StringComparison.Ordinal))
            {
                var values = ParseQuery(uri.Query);
                var appearance = values.GetValueOrDefault("appearance", string.Empty);
                var query = values.GetValueOrDefault("q", string.Empty);
                var filtered = source.Filter(query, appearance);
                return ContentResponse(Encoding.UTF8.GetBytes(CreateCatalogJson(filtered)), "application/json");
            }

            if (uri.AbsolutePath.StartsWith("/wp-content/uploads/market-", StringComparison.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(uri.AbsolutePath)["market-".Length..];
                source.CountPreview(id);
                return ContentResponse(source.GetPreview(id), "image/png");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string CreateCatalogJson(IReadOnlyList<FixturePackage> fixtures) => JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            schemaVersion = 1,
            items = fixtures.Select(fixture => new
            {
                id = fixture.Package.Id,
                name = fixture.Package.Name,
                version = fixture.Package.Version,
                appearance = fixture.Package.Appearance switch
                {
                    SkinAppearance.Light => "light",
                    SkinAppearance.Dark => "dark",
                    _ => "system"
                },
                description = fixture.Package.Id.Equals("ayanami", StringComparison.OrdinalIgnoreCase)
                    ? "浅色主题搭配包内背景，呈现柔和的海色与清晰文字。"
                    : "深色主题适配夜色背景，可在市场中预览静态聊天界面。",
                author = "Ancimen",
                licenseNotes = "保留所有权利。",
                previewUrl = $"https://blog.ancimen.top/wp-content/uploads/market-{fixture.Package.Id}.png",
                downloadUrl = $"https://blog.ancimen.top/?rest_route=/skin-manager/v1/skins/{fixture.Package.Id}/download",
                sha256 = Convert.ToHexString(SHA256.HashData(fixture.PackageBytes)).ToLowerInvariant(),
                size = fixture.PackageBytes.LongLength,
                skinFormatVersion = fixture.Package.Definition.FormatVersion,
                minManagerVersion = "1.0.0",
                updatedAt = "2026-11-04T00:00:00Z"
            }),
            pagination = new { page = 1, perPage = 12, total = fixtures.Count, totalPages = fixtures.Count == 0 ? 0 : 1 }
        });

        private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty,
                StringComparer.OrdinalIgnoreCase);

        private static HttpResponseMessage ContentResponse(byte[] bytes, string contentType) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
            }
        };
    }
}

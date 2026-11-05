using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Market;

namespace SkinManager.Preview.Tests;

internal static class MarketUiChecks
{
    public static async Task RunLivePreviewFetchAsync()
    {
        using var client = new SkinMarketClient();
        var failures = new List<Exception>();
        for (var round = 1; round <= 3; round++)
        {
            var page = await client.GetPageAsync();
            Console.WriteLine($"[market-live] 第 {round}/3 轮：同一客户端读取目录 {page.Items.Count} 款；随后并发取图。");
            var results = await Task.WhenAll(page.Items.Select(async item =>
            {
                try
                {
                    var bytes = await client.GetPreviewImageAsync(item);
                    if (bytes is null || bytes.Length == 0)
                    {
                        throw new InvalidDataException($"{item.Id} 没有返回预览图片字节。");
                    }

                    var image = SkinMarketWindow.DecodePreview(bytes);
                    return (Item: item, Bytes: bytes.Length, Width: image.PixelWidth, Height: image.PixelHeight, Error: (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (Item: item, Bytes: 0, Width: 0, Height: 0, Error: (Exception?)exception);
                }
            }));

            foreach (var result in results)
            {
                if (result.Error is { } exception)
                {
                    failures.Add(exception);
                    Console.Error.WriteLine($"[market-live] {result.Item.Id} 失败：{exception}");
                }
                else
                {
                    Console.WriteLine($"[market-live] {result.Item.Id} 成功：{result.Bytes} bytes，WPF {result.Width}×{result.Height}。");
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("生产市场客户端的线上预览图验证未通过。", failures);
        }

        Console.WriteLine("[market-live] 三轮目录读取、并发线上取图与 WPF 解码均通过；本验证不会下载皮肤包、写入用户库或连接 Codex。");
    }

    public static Func<SkinMarketClient> CreateReviewClientFactory(byte[] originalPackageBytes)
    {
        ArgumentNullException.ThrowIfNull(originalPackageBytes);
        var originalPackage = SkinPackageReader.ReadZipBytes(originalPackageBytes);
        var source = new MarketFixtureSource(
            originalPackageBytes,
            originalPackage.ImageBytes ?? throw new InvalidDataException("样例皮肤没有背景预览图。"));
        return () => new SkinMarketClient(new FixtureHandler(source));
    }

    public static async Task RunAsync(
        string testRoot,
        BasePackage basePackage,
        byte[] originalPackageBytes,
        string screenshotDirectory)
    {
        var libraryDirectory = Path.Combine(testRoot, "skin-market-empty-library");
        var store = new SkinStore(libraryDirectory);
        store.Initialize();
        Check(store.GetCatalog().Count == 0, "市场界面测试从空皮肤库开始");

        var originalPackage = SkinPackageReader.ReadZipBytes(originalPackageBytes);
        var previewBytes = originalPackage.ImageBytes ?? throw new InvalidDataException("样例皮肤没有背景预览图。");
        var source = new MarketFixtureSource(originalPackageBytes, previewBytes);
        var mainWindow = new MainWindow(
            store,
            basePackage,
            previewProfileDirectory: Path.Combine(testRoot, "skin-market-browser"),
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

            await WaitUntilAsync(() => marketWindow.Rows.Count == 1,
                "离线目录加载并显示皮肤卡片");
            await WaitUntilAsync(() => marketWindow.Rows[0].HasThumbnail, "市场预览图成功解码");
            Check(marketWindow.Rows[0].Item.Id == originalPackage.Id, "市场条目显示 manifest ID");
            Check(FindMarketButton(marketWindow, marketWindow.Rows[0], "下载并安装皮肤").IsEnabled,
                "兼容且未安装的皮肤可以下载");

            await SaveWindowScreenshotAsync(
                marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-默认窗口-1000x690.png"));
            marketWindow.Width = marketWindow.MinWidth;
            marketWindow.Height = marketWindow.MinHeight;
            await Task.Delay(150);
            await SaveWindowScreenshotAsync(
                marketWindow,
                Path.Combine(screenshotDirectory, "皮肤市场-最小窗口-880x560.png"));
            marketWindow.Width = 1000;
            marketWindow.Height = 690;
            await Task.Delay(150);

            FindMarketButton(marketWindow, marketWindow.Rows[0], "下载并安装皮肤")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => store.GetCatalog().Any(entry => entry.Id == originalPackage.Id), "市场下载写入空皮肤库");
            await WaitUntilAsync(() => marketWindow.Rows[0].OperationStatus.Contains("加入本地皮肤库", StringComparison.Ordinal),
                "下载成功状态明确表示皮肤已加入皮肤库");
            Check(store.LoadSkin(originalPackage.Id).Version == originalPackage.Version, "市场包按原 manifest 版本保存");
            Check(mainWindow.MarketEntryButton.IsEnabled, "下载后主窗口仍保持正常交互");

            var updateBytes = RewritePackageVersion(originalPackageBytes, "99.0.0");
            var updatePackage = SkinPackageReader.ReadZipBytes(updateBytes);
            source.ReplacePackage(updateBytes, updatePackage, Encoding.UTF8.GetBytes("invalid preview image"));
            await marketWindow.RefreshAsync(resetPage: true);
            await WaitUntilAsync(() => marketWindow.Rows.Count == 1 && marketWindow.Rows[0].HasPreviewError,
                "单张预览解码失败显示占位并保留目录条目");
            Check(marketWindow.Rows[0].Item.Version == updatePackage.Version, "刷新目录后显示市场更新版本");
            Check(FindMarketButton(marketWindow, marketWindow.Rows[0], "下载并安装皮肤").IsEnabled,
                "已安装旧版本的皮肤可更新");

            FindMarketButton(marketWindow, marketWindow.Rows[0], "下载并安装皮肤")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => marketWindow.Rows[0].OperationStatus.Contains("已取消替换", StringComparison.Ordinal),
                "重复 ID 的替换取消状态可测试");
            Check(store.LoadSkin(originalPackage.Id).Version == originalPackage.Version,
                "取消替换后原有皮肤版本保持不变");
            Check(!marketWindow.Rows[0].OperationStatus.Contains("加入本地皮肤库", StringComparison.Ordinal),
                "取消替换没有显示安装成功");
            Check(!mainWindow.RuntimeStatusMessage.Text.Contains("正在连接并应用", StringComparison.Ordinal),
                "市场安装不会自动连接或注入皮肤");
            Console.WriteLine("通过：市场空库安装、预览和替换取消 UI 流程");
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

    private static string CreateCatalogJson(SkinPackage package, byte[] zipBytes, string previewUrl) =>
        JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            schemaVersion = 1,
            items = new[]
            {
                new
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
                    description = package.Id.Equals("ayanami", StringComparison.OrdinalIgnoreCase)
                        ? "浅色主题搭配海色背景，适合清爽明亮的界面。"
                        : "深色主题搭配夜色背景，营造安静沉稳的界面。",
                    author = "Ancimen",
                    licenseNotes = "保留所有权利。",
                    previewUrl,
                    downloadUrl = $"https://blog.ancimen.top/?rest_route=/skin-manager/v1/skins/{package.Id}/download",
                    sha256 = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant(),
                    size = zipBytes.LongLength,
                    skinFormatVersion = package.Definition.FormatVersion,
                    minManagerVersion = "1.0.0",
                    updatedAt = "2026-11-03T00:00:00Z"
                }
            },
            pagination = new { page = 1, perPage = 12, total = 1, totalPages = 1 }
        });

    private static HttpResponseMessage ContentResponse(byte[] bytes, string contentType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } }
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
    {
        if (window.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("市场窗口没有可捕获的客户区内容。");
        }

        window.UpdateLayout();
        content.UpdateLayout();
        var margin = content.Margin;
        var clientWidth = content.ActualWidth + margin.Left + margin.Right;
        var clientHeight = content.ActualHeight + margin.Top + margin.Bottom;
        var dpi = VisualTreeHelper.GetDpi(content);
        var image = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(clientWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(clientHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var clientCanvas = new DrawingVisual();
        using (var drawing = clientCanvas.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, clientWidth, clientHeight));
            drawing.DrawRectangle(
                new VisualBrush(content),
                null,
                new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }

        image.Render(clientCanvas);
        image.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        await output.FlushAsync();
        Check(output.Length > 1_000, $"输出市场窗口截图 {Path.GetFileName(path)}");
    }

    private static Button FindMarketButton(SkinMarketWindow window, SkinMarketRow row, string automationName)
    {
        window.UpdateLayout();
        if (window.Content is FrameworkElement content)
        {
            content.UpdateLayout();
        }

        return FindVisualChildren<Button>(window)
            .FirstOrDefault(button => ReferenceEquals(button.DataContext, row) &&
                AutomationProperties.GetName(button).Equals(automationName, StringComparison.Ordinal))
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

    private sealed class MarketFixtureSource(byte[] packageBytes, byte[] previewBytes)
    {
        private readonly object _sync = new();
        private byte[] _packageBytes = packageBytes;
        private byte[] _previewBytes = previewBytes;
        private SkinPackage _package = SkinPackageReader.ReadZipBytes(packageBytes);

        public (SkinPackage Package, byte[] PackageBytes, byte[] PreviewBytes) Snapshot()
        {
            lock (_sync)
            {
                return (_package, _packageBytes, _previewBytes);
            }
        }

        public void ReplacePackage(byte[] packageBytes, SkinPackage package, byte[] previewBytes)
        {
            lock (_sync)
            {
                _packageBytes = packageBytes;
                _package = package;
                _previewBytes = previewBytes;
            }
        }
    }

    private sealed class FixtureHandler(MarketFixtureSource source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidDataException("测试 HTTP 请求缺少 URI。");
            var route = Uri.UnescapeDataString(uri.Query).Replace('+', ' ');
            var snapshot = source.Snapshot();
            if (route.Contains("/skin-manager/v1/skins/", StringComparison.Ordinal) && route.Contains("/download", StringComparison.Ordinal))
            {
                return Task.FromResult(ContentResponse(snapshot.PackageBytes, "application/zip"));
            }

            if (route.Contains("/skin-manager/v1/skins", StringComparison.Ordinal))
            {
                const string previewUrl = "https://blog.ancimen.top/wp-content/uploads/market-ui-preview.png";
                return Task.FromResult(ContentResponse(
                    Encoding.UTF8.GetBytes(CreateCatalogJson(snapshot.Package, snapshot.PackageBytes, previewUrl)),
                    "application/json"));
            }

            if (uri.AbsolutePath.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ContentResponse(snapshot.PreviewBytes, "image/png"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Core;
using SkinManager.Desktop;

namespace SkinManager.Preview.Tests;

internal static class GifBackgroundChecks
{
    public static async Task RunAsync()
    {
        var gifPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "images", "transparent-animation.gif");
        var gifBytes = File.ReadAllBytes(gifPath);
        var info = SkinPackageReader.ReadGifInfo(gifBytes);
        Require(info.Width == 16 && info.Height == 16 && info.FirstFrameWidth == 12 && info.FirstFrameHeight == 12,
            "GIF thumbnail dimensions come from the logical screen and include a local first frame.");

        var opaqueLocalFrame = ClearFirstFrameTransparency(gifBytes);
        var opaqueInfo = SkinPackageReader.ReadGifInfo(opaqueLocalFrame);
        Require(!opaqueInfo.FirstFrameHasTransparency,
            "Opaque local-frame fixture disables the transparent-index flag.");
        var thumbnail = PreviewImageCropper.CreateThumbnail(opaqueLocalFrame, 32, 32, 1, 1, 0.5, 0.5);
        var thumbnailPixels = ReadPixels(thumbnail);
        Require(thumbnail.PixelWidth == 16 && thumbnail.PixelHeight == 16 &&
                IsOpaqueBlack(thumbnailPixels, thumbnail.PixelWidth, 0, 0),
            "A local first frame without a transparency flag fills untouched logical-screen pixels with the GIF background color.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "SkinManagerGifBackground", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Window? window = null;
        ThemePreviewRenderer? renderer = null;
        try
        {
            var first = await CreatePreviewAsync(gifBytes, tempRoot, "first");
            window = first.Window;
            renderer = first.Renderer;
            var package = first.Package;
            var options = new PreviewOptions(Appearance: "light", Scene: "home");
            Require(await renderer.DisplayPackageAsync(package, options),
                "GIF background package loads in the offline preview renderer.");

            await renderer.ExecuteScriptAsync("""
                (() => {
                  const image = document.querySelector("#skin-manager-background .skin-manager-image");
                  window.__gifBackgroundImage = image;
                  window.__gifBackgroundUrl = image ? getComputedStyle(image).backgroundImage : "";
                })();
                """);
            var recolored = CreatePackage(gifBytes, "#4E6174");
            Require(await renderer.UpdatePackageStylesAsync(recolored, options),
                "Changing background color applies without replacing the GIF document.");
            var elementProbe = await renderer.ExecuteScriptAsync("""
                (() => {
                  const image = document.querySelector("#skin-manager-background .skin-manager-image");
                  return {
                    sameElement: !!image && image === window.__gifBackgroundImage,
                    sameUrl: !!image && getComputedStyle(image).backgroundImage === window.__gifBackgroundUrl,
                  };
                })()
                """);
            using (var probe = JsonDocument.Parse(elementProbe))
            {
                Require(probe.RootElement.GetProperty("sameElement").GetBoolean() &&
                        probe.RootElement.GetProperty("sameUrl").GetBoolean(),
                    "Color-only preview updates keep the GIF image element and source URL stable.");
            }
            var backgroundAnimationCapture = await CaptureAnimationAsync(renderer);
            Require(backgroundAnimationCapture.FrameChanged,
                "The package background remains animated after a color-only style update.");

            Require(await renderer.DisplayOriginalImageAsync(package.ImagePath!),
                "Original GIF opens through the existing WebView2 renderer.");
            await WaitForOriginalImageAsync(renderer);
            Require(renderer.ServedSkinResourcePaths.Contains(package.ImagePath!, StringComparer.OrdinalIgnoreCase),
                "Original GIF is fetched from the skin package route with the existing image/gif response path.");
            var animationCapture = await CaptureAnimationAsync(renderer);
            Require(animationCapture.FrameChanged,
                "WebView2 captures at least two different rendered GIF frames.");
            var evidenceDirectory = Program.FindTestOutputDirectory();
            Directory.CreateDirectory(evidenceDirectory);
            var screenshotPath = Path.Combine(evidenceDirectory, "gif-background-original.png");
            await File.WriteAllBytesAsync(screenshotPath, animationCapture.LastFrame);
            Console.WriteLine($"GIF 动画预览截图：{screenshotPath}");

            Require(await renderer.DisplayPackageAsync(recolored, options),
                "Returning from the original image restores the package preview.");
            var originalViewCleared = await renderer.ExecuteScriptAsync(
                "!document.getElementById('skin-manager-original-image') && !!document.querySelector('#skin-manager-background .skin-manager-image')");
            Require(JsonSerializer.Deserialize<bool>(originalViewCleared),
                "Returning to the package removes the original-image view.");

            renderer.Dispose();
            renderer = null;
            window.Close();
            window = null;

            var reopened = await CreatePreviewAsync(gifBytes, tempRoot, "reopened");
            window = reopened.Window;
            renderer = reopened.Renderer;
            Require(await renderer.DisplayPackageAsync(reopened.Package, options) &&
                    await renderer.DisplayOriginalImageAsync(reopened.Package.ImagePath!),
                "A released preview WebView can be recreated and display the selected GIF again.");
            await WaitForOriginalImageAsync(renderer);
            var reopenedCapture = await CaptureAnimationAsync(renderer);
            Require(reopenedCapture.FrameChanged,
                "GIF animation continues after the preview has been released and reopened.");

            renderer.Dispose();
            renderer = null;
            window.Close();
            window = null;
            await VerifyMainWindowOriginalFlowAsync(gifBytes, tempRoot);
        }
        finally
        {
            renderer?.Dispose();
            window?.Close();
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
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

    private static async Task<(Window Window, ThemePreviewRenderer Renderer, SkinPackage Package)> CreatePreviewAsync(
        byte[] gifBytes,
        string tempRoot,
        string name)
    {
        var browser = new WebView2
        {
            Width = 720,
            Height = 480,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var window = new Window
        {
            Title = "GIF preview check",
            Width = 720,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = browser
        };
        window.Show();
        var renderer = new ThemePreviewRenderer(
            browser,
            Path.Combine(tempRoot, $"browser-{name}"),
            App.ReadEmbeddedBasePackage());
        try
        {
            await renderer.InitializeAsync();
            return (window, renderer, CreatePackage(gifBytes, "#18202A"));
        }
        catch
        {
            renderer.Dispose();
            window.Close();
            throw;
        }
    }

    private static SkinPackage CreatePackage(byte[] gifBytes, string backgroundColor)
    {
        var manifest = new JsonObject
        {
            ["formatVersion"] = 1,
            ["id"] = "gif-background-check",
            ["name"] = "GIF background check",
            ["version"] = "1.0.0",
            ["appearance"] = "light",
            ["background"] = new JsonObject
            {
                ["image"] = "assets/transparent-animation.gif",
                ["fit"] = "contain",
                ["focusX"] = 0.5,
                ["focusY"] = 0.5,
                ["color"] = backgroundColor
            }
        };
        return SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
        {
            ["skin.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            ["assets/transparent-animation.gif"] = gifBytes
        });
    }

    private static async Task VerifyMainWindowOriginalFlowAsync(byte[] gifBytes, string tempRoot)
    {
        var store = new SkinStore(Path.Combine(tempRoot, "manager-skin-store"));
        store.Initialize();
        store.Save(CreatePackage(gifBytes, "#18202A"), replaceExisting: false);
        var mainWindow = new MainWindow(
            store,
            App.ReadEmbeddedBasePackage(),
            previewProfileDirectory: Path.Combine(tempRoot, "manager-preview"),
            readRuntimeStatusOnLoad: false)
        {
            ShowInTaskbar = false
        };

        try
        {
            mainWindow.Show();
            await WaitForMainPreviewAsync(mainWindow);
            var originalButton = Find<Button>(mainWindow, "OriginalButton");
            originalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var currentRenderer = GetRenderer(mainWindow);
            await WaitForOriginalImageAsync(currentRenderer);
            await WaitUntilAsync(() => ReadPrivate<bool>(mainWindow, "_showingOriginalGif"), "show the main window GIF original");
            Require(originalButton.Content?.ToString() == "返回聊天预览" &&
                    Find<UIElement>(mainWindow, "OriginalImageLayer").Visibility == Visibility.Collapsed &&
                    currentRenderer.IsShowingOriginalImage,
                "The main window's original-image action shows the animated GIF in its preview WebView.");

            var oldRenderer = GetRenderer(mainWindow);
            Find<Button>(mainWindow, "PreviewPowerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => ReadPrivate<bool>(mainWindow, "_previewClosed"), "close the GIF preview browser");
            var rendererField = mainWindow.GetType().GetField("_previewRenderer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GIF check cannot find _previewRenderer.");
            Require(rendererField.GetValue(mainWindow) is null,
                "Closing the main window preview releases its WebView renderer.");

            var displayGate = ReadPrivate<SemaphoreSlim>(mainWindow, "_previewDisplayGate");
            await displayGate.WaitAsync();
            ThemePreviewRenderer newRenderer;
            try
            {
                Find<Button>(mainWindow, "PreviewPowerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForMainPreviewAsync(mainWindow);
                newRenderer = GetRenderer(mainWindow);
                var selectedPackage = ReadPrivateReference<SkinPackage>(mainWindow, "_selectedPackage");
                Require(!ReferenceEquals(oldRenderer, newRenderer),
                    "Reopening the preview creates a new renderer after release.");
                Require(selectedPackage is not null && !newRenderer.IsCurrentPackageReady(selectedPackage) &&
                        !ReadPrivate<Task>(mainWindow, "_previewReadyTask").IsCompleted,
                    "The test reaches the actual document-loading window before the reopened preview is ready.");
                Find<Button>(mainWindow, "OriginalButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!newRenderer.IsShowingOriginalImage,
                    "The original-image action waits for the reopened package document to finish loading.");
            }
            finally
            {
                displayGate.Release();
            }

            await WaitForOriginalImageAsync(newRenderer);
            await WaitUntilAsync(() => ReadPrivate<bool>(mainWindow, "_showingOriginalGif"), "reopen the main window GIF original");
            Require(ReadPrivate<bool>(mainWindow, "_showingOriginalGif") &&
                    newRenderer.IsShowingOriginalImage,
                "The main window can play the selected GIF again after closing and reopening preview.");
        }
        finally
        {
            mainWindow.Close();
        }
    }

    private static async Task WaitForMainPreviewAsync(MainWindow window)
    {
        await WaitUntilAsync(
            () => !ReadPrivate<bool>(window, "_previewClosed") &&
                  ReadPrivate<bool>(window, "_previewInitialized") &&
                  ReadPrivateReference<SkinPackage>(window, "_selectedPackage")?.ImagePath is { } path &&
                  Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase) &&
                  Find<Button>(window, "OriginalButton").IsEnabled,
            "initialize the main window preview for the GIF skin");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string operation)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out while trying to {operation}.");
    }

    private static T Find<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"GIF check cannot find {name}.");

    private static T ReadPrivate<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new InvalidOperationException($"GIF check cannot read {name}."));

    private static T? ReadPrivateReference<T>(object target, string name) where T : class =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(target) as T;

    private static ThemePreviewRenderer GetRenderer(MainWindow window) => ReadPrivate<ThemePreviewRenderer>(window, "_previewRenderer");

    private static byte[] ClearFirstFrameTransparency(byte[] source)
    {
        var result = source.ToArray();
        for (var index = 0; index + 7 < result.Length; index++)
        {
            if (result[index] == 0x21 && result[index + 1] == 0xf9 && result[index + 2] == 0x04)
            {
                result[index + 3] &= 0xfe;
                return result;
            }
        }

        throw new InvalidDataException("GIF 测试素材没有图形控制扩展块。");
    }

    private static async Task WaitForOriginalImageAsync(ThemePreviewRenderer renderer)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var result = await renderer.ExecuteScriptAsync("(() => { const image = document.getElementById('skin-manager-original-image'); return !!image && image.complete && image.naturalWidth > 0; })()");
            if (JsonSerializer.Deserialize<bool>(result))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("WebView2 未能加载 GIF 原图。");
    }

    private static async Task<(bool FrameChanged, byte[] LastFrame)> CaptureAnimationAsync(ThemePreviewRenderer renderer)
    {
        string? firstHash = null;
        byte[]? lastFrame = null;
        for (var attempt = 0; attempt < 14; attempt++)
        {
            using var capture = new MemoryStream();
            await renderer.CapturePreviewAsync(capture);
            lastFrame = capture.ToArray();
            var pixelHash = HashPixels(lastFrame);
            if (firstHash is not null && pixelHash != firstHash)
            {
                return (true, lastFrame);
            }

            firstHash ??= pixelHash;
            await Task.Delay(75);
        }

        return (false, lastFrame ?? Array.Empty<byte>());
    }

    private static string HashPixels(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = ReadPixels(converted);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static byte[] ReadPixels(BitmapSource source)
    {
        var pixels = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }

    private static bool IsOpaqueBlack(byte[] pixels, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return pixels[offset] == 0 && pixels[offset + 1] == 0 && pixels[offset + 2] == 0 && pixels[offset + 3] == byte.MaxValue;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL GIF background checks: {message}");
        }
    }
}

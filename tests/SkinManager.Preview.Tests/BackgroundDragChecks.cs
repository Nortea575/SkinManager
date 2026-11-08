using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Core;
using SkinManager.Desktop;

namespace SkinManager.Preview.Tests;

internal static class BackgroundDragChecks
{
    public static async Task RunAsync(WebView2 browser, ThemePreviewRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(renderer);
        var core = browser.CoreWebView2 ?? throw new InvalidOperationException("背景拖动检查需要已初始化的 WebView2。");
        var viewportJson = await core.ExecuteScriptAsync("JSON.stringify({ width: innerWidth, height: innerHeight });");
        var viewport = JsonDocument.Parse(JsonSerializer.Deserialize<string>(viewportJson)!);
        var viewportWidth = viewport.RootElement.GetProperty("width").GetDouble();
        var viewportHeight = viewport.RootElement.GetProperty("height").GetDouble();
        Assert(viewportWidth > 0 && viewportHeight > 0, "预览视口具有有效尺寸");

        var imageWidth = 400;
        var imageHeight = Math.Max(1, (int)Math.Round(200 / (viewportWidth / viewportHeight)));
        var package = CreateContainPackage(imageWidth, imageHeight);
        Assert(await renderer.DisplayPackageAsync(package, "light"), "加载包含横图背景的焦点测试皮肤");
        Assert(renderer.CurrentResolvedSkin?.Background?.ImagePath == "assets/background.png",
            "测试皮肤的背景图片进入预览");

        var moved = new TaskCompletionSource<PreviewBackgroundFocusEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<PreviewBackgroundFocusEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var phases = new List<string>();
        var focusEvents = new List<string>();
        void OnBackgroundFocusChanged(object? sender, PreviewBackgroundFocusEventArgs args)
        {
            phases.Add(args.Phase);
            focusEvents.Add($"{args.Phase}({args.FocusX:F4},{args.FocusY:F4})");
            if (args.Phase == "move" && args.FocusY > 0.5)
            {
                moved.TrySetResult(args);
            }
            else if (args.Phase == "end")
            {
                ended.TrySetResult(args);
            }
        }

        renderer.BackgroundFocusChanged += OnBackgroundFocusChanged;
        try
        {
            await core.ExecuteScriptAsync("""
                (() => {
                  const key = "__SKIN_MANAGER_BACKGROUND_DRAG_CHECKS__";
                  const records = [];
                  const handlers = {};
                  for (const type of ["pointerdown", "pointermove", "pointerup", "pointercancel", "lostpointercapture"]) {
                    handlers[type] = (event) => records.push({
                      type,
                      targetId: event.target?.id || "",
                      targetTag: event.target?.tagName || "",
                      pointerId: event.pointerId,
                      isPrimary: event.isPrimary,
                      button: event.button,
                      buttons: event.buttons,
                    });
                    document.addEventListener(type, handlers[type], true);
                  }
                  window[key] = { records, handlers };
                })();
                """);
            await renderer.SetBackgroundPositionModeAsync(true);
            await core.ExecuteScriptAsync("""
                (() => {
                  const mode = window["__SKIN_MANAGER_BACKGROUND_POSITION__"];
                  const measure = mode.measure;
                  mode.measure = async (...args) => {
                    await new Promise(resolve => setTimeout(resolve, 100));
                    return measure(...args);
                  };
                })();
                """);
            var layerResult = await core.ExecuteScriptAsync(
                "document.elementFromPoint(Math.floor(innerWidth / 2), Math.floor(innerHeight / 2))?.id || ''; ");
            var layerId = JsonSerializer.Deserialize<string>(layerResult);
            Assert(layerId == "skin-manager-background-position-layer",
                "elementFromPoint 命中透明焦点拖动层，而不是被其下方内容遮挡");

            var geometryJson = await core.ExecuteScriptAsync("""
                (() => {
                  const image = document.querySelector("#skin-manager-background .skin-manager-image");
                  if (!image) return { width: innerWidth, height: innerHeight, imageWidth: 0, imageHeight: 0, fit: "" };
                  const bounds = image.getBoundingClientRect();
                  return {
                    width: innerWidth,
                    height: innerHeight,
                    imageWidth: bounds.width,
                    imageHeight: bounds.height,
                    fit: getComputedStyle(image).backgroundSize,
                  };
                })();
                """);
            var geometry = JsonDocument.Parse(geometryJson);
            var viewportImageWidth = geometry.RootElement.GetProperty("imageWidth").GetDouble();
            var viewportImageHeight = geometry.RootElement.GetProperty("imageHeight").GetDouble();
            var fit = geometry.RootElement.GetProperty("fit").GetString();
            Assert(fit == "contain", "预览背景使用 contain 尺寸");
            var containScale = Math.Min(viewportImageWidth / imageWidth, viewportImageHeight / imageHeight);
            var spanY = viewportImageHeight - imageHeight * containScale;
            Assert(spanY > 1, "contain 横图在竖直方向留白，允许背景上下移动");

            var testViewportWidth = geometry.RootElement.GetProperty("width").GetDouble();
            var testViewportHeight = geometry.RootElement.GetProperty("height").GetDouble();
            var centerX = Math.Clamp((int)Math.Round(testViewportWidth / 2), 1, Math.Max(1, (int)testViewportWidth - 1));
            var centerY = Math.Clamp((int)Math.Round(testViewportHeight / 2), 1, Math.Max(1, (int)testViewportHeight - 21));
            await DispatchMouseAsync(core, "mousePressed", centerX, centerY, buttons: 1, button: "left");
            await DispatchMouseAsync(core, "mouseMoved", centerX, centerY + 20, buttons: 1, button: "none");
            await DispatchMouseAsync(core, "mouseReleased", centerX, centerY + 20, buttons: 0, button: "left");
            await DispatchMouseAsync(core, "mouseMoved", centerX, centerY + 60, buttons: 0, button: "none");

            PreviewBackgroundFocusEventArgs moveArgs;
            try
            {
                moveArgs = await moved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException exception)
            {
                var diagnosticJson = await core.ExecuteScriptAsync($$"""
                    (() => {
                      const mode = window["__SKIN_MANAGER_BACKGROUND_POSITION__"];
                      const probe = window["__SKIN_MANAGER_BACKGROUND_DRAG_CHECKS__"];
                      return {
                        hit: document.elementFromPoint({{centerX}}, {{centerY}})?.id || document.elementFromPoint({{centerX}}, {{centerY}})?.tagName || "",
                        modeEnabled: mode?.enabled ?? false,
                        active: mode?.active ? {
                          pointerId: mode.active.pointerId,
                          focusX: mode.active.focusX,
                          focusY: mode.active.focusY,
                          geometry: mode.active.geometry,
                          pendingFinish: mode.active.pendingFinish,
                          lastX: mode.active.lastX,
                          lastY: mode.active.lastY,
                        } : null,
                        pointerEvents: probe?.records || [],
                      };
                    })();
                    """);
                throw new TimeoutException(
                    $"拖动未产生目标 move。焦点事件={string.Join(',', focusEvents)}；DOM诊断={diagnosticJson}",
                    exception);
            }
            var endArgs = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(phases.Contains("begin") && phases.Contains("move") && phases.Contains("end"),
                "真实指针输入经过拖动层并收到 begin/move/end 消息");
            var expectedFocusY = Math.Clamp(0.5 + 20 / spanY, 0, 1);
            Assert(Math.Abs(moveArgs.FocusY - expectedFocusY) < 0.0001 && Math.Abs(moveArgs.FocusX - 0.5) < 0.0001 &&
                   Math.Abs(endArgs.FocusY - moveArgs.FocusY) < 0.0001,
                "contain 垂直焦点只累计释放前 20px，释放后额外指针移动不改变 focusY");
        }
        finally
        {
            await renderer.SetBackgroundPositionModeAsync(false);
            await core.ExecuteScriptAsync("""
                (() => {
                  const probe = window["__SKIN_MANAGER_BACKGROUND_DRAG_CHECKS__"];
                  if (!probe) return;
                  for (const [type, handler] of Object.entries(probe.handlers)) {
                    document.removeEventListener(type, handler, true);
                  }
                  delete window["__SKIN_MANAGER_BACKGROUND_DRAG_CHECKS__"];
                })();
                """);
            renderer.BackgroundFocusChanged -= OnBackgroundFocusChanged;
        }
    }

    private static SkinPackage CreateContainPackage(int width, int height)
    {
        var manifest = """
            {
              "formatVersion": 1,
              "id": "background-drag-check",
              "name": "Background drag check",
              "version": "1.0.0",
              "appearance": "light",
              "background": {
                "image": "assets/background.png",
                "fit": "contain",
                "focusX": 0.5,
                "focusY": 0.5
              }
            }
            """;
        return SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
        {
            ["skin.json"] = Encoding.UTF8.GetBytes(manifest),
            ["assets/background.png"] = CreatePng(width, height)
        });
    }

    private static byte[] CreatePng(int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[checked(width * height * 4)];
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = byte.MaxValue;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static Task<string> DispatchMouseAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        string type,
        int x,
        int y,
        int buttons,
        string button) => core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new { type, x, y, buttons, button, clickCount = 1 }));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL 背景拖动检查：{message}。");
        }
    }
}

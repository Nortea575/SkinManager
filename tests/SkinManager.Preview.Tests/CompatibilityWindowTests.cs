using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkinManager.Desktop;
using SkinManager.Runtime;

namespace SkinManager.Preview.Tests;

internal static class CompatibilityWindowTests
{
    public static async Task RunAsync()
    {
        await VerifiesCompactLayoutAndFailureFeedbackAsync();
        await VerifiesCloseAndEscapeAsync();
    }

    private static async Task VerifiesCompactLayoutAndFailureFeedbackAsync()
    {
        var executablePath = @"C:\Example\" +
            string.Join(@"\", Enumerable.Repeat("MagpieFolder_" + new string('x', 34), 12)) +
            @"\Magpie.exe";
        var longEvidence = string.Join(" ", Enumerable.Repeat(
            "示例检测信息用于检查窄窗口中的换行、滚动和按钮可用性。", 32));
        var result = new MagpieCompatibilityResult(
            IsInstalled: true,
            IsRunning: false,
            ExecutablePath: executablePath,
            ProductVersion: "0.1.3237",
            Support: MagpieCompatibilitySupport.Unsupported,
            CanLaunch: true,
            Evidence: longEvidence,
            Message: "已核实 Magpie 自行重新打开 Codex 时不附带远程调试参数。网关模型配置与皮肤启动参数相互独立。");
        var inspectCalls = 0;
        var magpieLaunchCalls = 0;
        var debugLaunchCalls = 0;
        var launchError = "模拟调试版启动失败：" + string.Join(" ", Enumerable.Repeat("请检查实例状态与端口。", 20));
        var window = CreateWindow(
            () =>
            {
                debugLaunchCalls++;
                return Task.FromResult(new RuntimeStatus(RuntimeStatusState.Error, null, null, launchError, false));
            },
            _ =>
            {
                inspectCalls++;
                return result;
            },
            (_, _) =>
            {
                magpieLaunchCalls++;
                return Task.CompletedTask;
            });

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded && Find<TextBlock>(window, "SupportTitle").Text.StartsWith("已核实", StringComparison.Ordinal),
                "兼容页完成fake检测");
            window.Width = 620;
            window.Height = 440;
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            content.UpdateLayout();

            var supportTitle = Find<TextBlock>(window, "SupportTitle");
            var compatibilityMessage = Find<TextBlock>(window, "CompatibilityMessageText");
            var pathInput = Find<TextBox>(window, "ExecutablePathInput");
            var pathText = Find<TextBlock>(window, "ExecutablePathText");
            var evidenceText = Find<TextBlock>(window, "EvidenceText");
            var actionStatus = Find<TextBlock>(window, "ActionStatusText");
            var launchMagpie = Find<Button>(window, "LaunchMagpieButton");
            var launchCodex = Find<Button>(window, "LaunchDebugCodexButton");
            var closeButton = Find<Button>(window, "CloseButton");
            var scrollViewer = FindVisualChildren<ScrollViewer>(window).FirstOrDefault(viewer => viewer.ScrollableHeight > 0);

            Require(window.Width == 620 && window.Height == 440 && inspectCalls == 1,
                "兼容页可在620×440逻辑尺寸显示，检测使用fake服务");
            Require(supportTitle.Text == "已核实：启动不附带调试参数" &&
                    compatibilityMessage.Text.Contains("调试参数", StringComparison.Ordinal) &&
                    compatibilityMessage.Text.Contains("网关模型", StringComparison.Ordinal) &&
                    pathInput.Text == executablePath && pathText.Text.Contains(executablePath, StringComparison.Ordinal) &&
                    evidenceText.Text == longEvidence,
                "兼容页说明启动参数限制并完整显示模拟路径和检测证据");
            Require(scrollViewer is not null,
                "长路径和长检测信息在较小窗口内可通过内容区滚动查看");

            var buttons = new[] { launchMagpie, launchCodex, closeButton };
            var contentBounds = new Rect(new Point(0, 0), content.RenderSize);
            var buttonBounds = buttons.Select(button => GetBounds(content, button)).ToArray();
            var statusBounds = GetBounds(content, actionStatus);
            Require(buttons.All(button => button.IsVisible && button.IsEnabled) &&
                    buttonBounds.All(bounds => IsWithinBounds(bounds, contentBounds)) &&
                    buttonBounds.All(bounds => !bounds.IntersectsWith(statusBounds)),
                "长内容下启动与关闭按钮仍完整可见、可用且不被状态文字覆盖");

            launchCodex.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => debugLaunchCalls == 1 &&
                actionStatus.Text.StartsWith("启动未完成：", StringComparison.Ordinal), "兼容页显示fake调试启动失败");
            Require(actionStatus.Text.Contains("模拟调试版启动失败", StringComparison.Ordinal) &&
                    !actionStatus.Text.Contains("已提交启动请求", StringComparison.Ordinal) && magpieLaunchCalls == 0,
                "启动回调返回Error时明确显示失败，且未调用真实Magpie启动");
            content.UpdateLayout();
            var afterFailureBounds = buttons.Select(button => GetBounds(content, button)).ToArray();
            Require(buttons.All(button => button.IsVisible && button.IsEnabled) &&
                    afterFailureBounds.All(bounds => IsWithinBounds(bounds, new Rect(new Point(0, 0), content.RenderSize))) &&
                    afterFailureBounds.All(bounds => !bounds.IntersectsWith(GetBounds(content, actionStatus))),
                "较长失败说明不会挤出兼容页底部按钮");

            await SaveScreenshotAsync(window, Path.Combine(Program.FindTestOutputDirectory(), "兼容页-最小窗口-620x440.png"));
            closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !window.IsVisible, "兼容页关闭按钮关闭窗口");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static async Task VerifiesCloseAndEscapeAsync()
    {
        var window = CreateWindow(
            () => Task.FromResult(new RuntimeStatus(RuntimeStatusState.Error, null, null, "fake", false)),
            _ => new MagpieCompatibilityResult(
                true, false, @"C:\Example\Magpie.exe", "0.1.3237", MagpieCompatibilitySupport.Unsupported,
                true, "fake evidence", "fake message"),
            (_, _) => Task.CompletedTask);
        window.Left = -2_000;
        window.Top = -2_000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded, "兼容页完成加载后响应Escape");
            var source = PresentationSource.FromVisual(window) ??
                throw new InvalidOperationException("兼容页尚未连接到WPF窗口消息源。");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            await WaitUntilAsync(() => !window.IsVisible, "按Escape关闭兼容页");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static CompatibilityWindow CreateWindow(
        Func<Task<RuntimeStatus>> launchDebugCodex,
        Func<string?, MagpieCompatibilityResult> inspect,
        Func<string?, CancellationToken, Task> launchMagpie) =>
        new(launchDebugCodex, inspect, launchMagpie)
        {
            Width = 620,
            Height = 440,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false
        };

    private static T Find<T>(Window window, string name) where T : FrameworkElement =>
        window.FindName(name) is T element
            ? element
            : throw new InvalidOperationException($"兼容页缺少控件 {name}。");

    private static Rect GetBounds(FrameworkElement ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

    private static bool IsWithinBounds(Rect bounds, Rect viewport) =>
        bounds.Left >= viewport.Left - 1 && bounds.Top >= viewport.Top - 1 &&
        bounds.Right <= viewport.Right + 1 && bounds.Bottom <= viewport.Bottom + 1;

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

    private static async Task SaveScreenshotAsync(Window window, string path)
    {
        if (window.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("兼容页没有可捕获的内容区。");
        }

        window.UpdateLayout();
        content.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(content);
        var image = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var capture = new DrawingVisual();
        using (var drawing = capture.RenderOpen())
        {
            drawing.DrawRectangle(new VisualBrush(content), null,
                new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        image.Render(capture);
        image.Freeze();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        await output.FlushAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"兼容页回归失败：{label}超时。");
            }

            await Task.Delay(25);
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"兼容页回归失败：{label}");
        }

        Console.WriteLine($"通过：{label}");
    }
}

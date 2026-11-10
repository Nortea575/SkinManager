using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
        await VerifiesSavedPreferenceAndPendingTargetAsync();
        await VerifiesStorageFailuresDoNotClaimEnabledAsync();
        await VerifiesMagpieActionsAreIndependentAsync();
        await VerifiesCompactLayoutAndFailureFeedbackAsync();
        await VerifiesCloseAndEscapeAsync();
    }

    private static async Task VerifiesSavedPreferenceAndPendingTargetAsync()
    {
        var writes = 0;
        var configureCalls = 0;
        var current = new RuntimeCompatibilityOptions(true);
        var window = CreateWindow(
            options: current,
            lastRuntimeResult: new RuntimeCompatibilityResult(
                RuntimeCompatibilityState.Applied,
                "http://127.0.0.1:9335",
                "Applied",
                true),
            saveAndConfigure: (options, _) =>
            {
                writes++;
                current = options;
                configureCalls++;
                return Task.FromResult<RuntimeCompatibilityResult?>(new RuntimeCompatibilityResult(
                    RuntimeCompatibilityState.Disabled,
                    "http://127.0.0.1:9335",
                    "宿主确认固定语言已关闭。",
                    true));
            });
        window.Width = 760;
        window.Height = 570;
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded, "兼容设置页完成加载");
            await WaitUntilAsync(() => window.Opacity >= 0.999, "默认兼容设置页淡入完成");
            var toggle = Find<CheckBox>(window, "ForceSimplifiedChineseCheckBox");
            var saved = Find<TextBlock>(window, "LanguageSavedStatusText");
            var runtime = Find<TextBlock>(window, "LanguageRuntimeStatusText");
            Require(!Find<Expander>(window, "MagpieSettingsExpander").IsExpanded &&
                    Find<Button>(window, "LaunchDebugCodexButton").IsVisible &&
                    Find<Button>(window, "CloseButton").IsVisible,
                "不展开Magpie条目也能找到调试版启动入口与固定关闭按钮");
            Require(toggle.IsChecked == true && saved.Text.Contains("已保存", StringComparison.Ordinal) &&
                    runtime.Text.StartsWith("已生效", StringComparison.Ordinal) && writes == 0 && configureCalls == 0,
                "重新打开时读取保存值与最近结果，初始化不保存、不连接");
            await SaveScreenshotAsync(window, Path.Combine(Program.FindTestOutputDirectory(), "兼容设置-默认条目.png"));

            Toggle(toggle);
            await WaitUntilAsync(() => writes == 1 && toggle.IsEnabled, "关闭语言开关后保存");
            Require(current == new RuntimeCompatibilityOptions(false) &&
                    saved.Text.Contains("已保存：关闭", StringComparison.Ordinal) &&
                    runtime.Text.StartsWith("已关闭：宿主确认", StringComparison.Ordinal) &&
                    !runtime.Text.Contains("原始语言", StringComparison.Ordinal) && configureCalls == 1,
                "关闭开关后显示后端真实状态，不推断用户原始语言");
        }
        finally
        {
            window.Close();
        }

        var reopenedWindow = CreateWindow(options: current);
        reopenedWindow.Show();
        try
        {
            await WaitUntilAsync(() => reopenedWindow.IsLoaded, "重新打开兼容设置页");
            Require(Find<CheckBox>(reopenedWindow, "ForceSimplifiedChineseCheckBox").IsChecked == false &&
                    Find<TextBlock>(reopenedWindow, "LanguageSavedStatusText").Text.Contains("已保存：关闭", StringComparison.Ordinal),
                "重新打开后保留上一次保存的开关值");
        }
        finally
        {
            reopenedWindow.Close();
        }

        var pendingWindow = CreateWindow(
            saveAndConfigure: (options, _) => Task.FromResult<RuntimeCompatibilityResult?>(null));
        pendingWindow.Show();
        try
        {
            await WaitUntilAsync(() => pendingWindow.IsLoaded, "未连接兼容页完成加载");
            var toggle = Find<CheckBox>(pendingWindow, "ForceSimplifiedChineseCheckBox");
            Toggle(toggle);
            await WaitUntilAsync(() =>
                Find<TextBlock>(pendingWindow, "LanguageSavedStatusText").Text.Contains("已保存", StringComparison.Ordinal) &&
                Find<TextBlock>(pendingWindow, "LanguageRuntimeStatusText").Text.StartsWith("待连接", StringComparison.Ordinal),
                "无目标时显示待连接");
            Require(toggle.IsChecked == true, "无连接目标时仍可保存语言偏好，且不显示已生效");
            Toggle(toggle);
            await WaitUntilAsync(() =>
                Find<TextBlock>(pendingWindow, "LanguageSavedStatusText").Text.Contains("已保存：关闭", StringComparison.Ordinal),
                "关闭二态开关");
            Require(!toggle.IsThreeState && toggle.IsChecked == false,
                "用户切换开关只在保存的开与关之间切换");
        }
        finally
        {
            pendingWindow.Close();
        }
    }

    private static async Task VerifiesStorageFailuresDoNotClaimEnabledAsync()
    {
        var saves = 0;
        var window = CreateWindow(
            saveAndConfigure: (_, _) =>
            {
                saves++;
                return Task.FromException<RuntimeCompatibilityResult?>(new IOException("模拟设置存储失败"));
            });
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded, "设置失败用例完成加载");
            var toggle = Find<CheckBox>(window, "ForceSimplifiedChineseCheckBox");
            var saved = Find<TextBlock>(window, "LanguageSavedStatusText");
            Toggle(toggle);
            await WaitUntilAsync(() => saves == 1 && toggle.IsEnabled, "设置写入错误反馈");
            Require(toggle.IsChecked == false && saved.Text.Contains("未能保存", StringComparison.Ordinal) &&
                    !Find<TextBlock>(window, "LanguageRuntimeStatusText").Text.StartsWith("已生效", StringComparison.Ordinal),
                "存储失败会恢复上次开关值，不显示已保存或已生效");
        }
        finally
        {
            window.Close();
        }

        var loadAttempts = 0;
        var reloadedWindow = CreateWindow(
            settingsLoaded: false,
            settingsLoadError: "模拟首次读取失败",
            reloadSettings: () =>
            {
                loadAttempts++;
                if (loadAttempts == 1)
                {
                    throw new IOException("模拟重试读取失败");
                }

                return new RuntimeCompatibilityOptions(true);
            });
        reloadedWindow.Show();
        try
        {
            await WaitUntilAsync(() => reloadedWindow.IsLoaded, "设置读取失败用例完成加载");
            var toggle = Find<CheckBox>(reloadedWindow, "ForceSimplifiedChineseCheckBox");
            var retry = Find<Button>(reloadedWindow, "RetrySettingsLoadButton");
            Require(toggle.IsChecked is null && !toggle.IsEnabled && retry.IsVisible,
                "读取失败时开关显示未知并禁用，不暗示已关闭");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!toggle.IsEnabled && loadAttempts == 1,
                "读取重试失败仍保留未知状态");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(toggle.IsChecked == true && toggle.IsEnabled && loadAttempts == 2,
                "重新读取成功后恢复真实保存值");
        }
        finally
        {
            reloadedWindow.Close();
        }
    }

    private static async Task VerifiesMagpieActionsAreIndependentAsync()
    {
        var options = new RuntimeCompatibilityOptions(false);
        var optionWrites = 0;
        var configureCalls = 0;
        var inspectCalls = 0;
        var magpieLaunchCalls = 0;
        var debugLaunchCalls = 0;
        var fakeResult = new MagpieCompatibilityResult(
            true,
            false,
            @"C:\Example\Magpie.exe",
            "0.1.3237",
            MagpieCompatibilitySupport.Unsupported,
            true,
            "Magpie 自身重开 Codex 不附带调试参数。",
            "网关模型配置独立于皮肤启动参数。");
        var window = CreateWindow(
            saveAndConfigure: (value, _) =>
            {
                optionWrites++;
                options = value;
                configureCalls++;
                return Task.FromResult<RuntimeCompatibilityResult?>(new RuntimeCompatibilityResult(
                    RuntimeCompatibilityState.Applied,
                    "http://127.0.0.1:9335",
                    "已写入",
                    true));
            },
            launchDebugCodex: () =>
            {
                debugLaunchCalls++;
                return Task.FromResult(new RuntimeStatus(RuntimeStatusState.Connecting, null,
                    "http://127.0.0.1:9335", "fake", true));
            },
            inspectMagpie: _ =>
            {
                inspectCalls++;
                return fakeResult;
            },
            launchMagpie: (_, _) =>
            {
                magpieLaunchCalls++;
                return Task.CompletedTask;
            });
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded, "独立设置页完成加载");
            var toggle = Find<CheckBox>(window, "ForceSimplifiedChineseCheckBox");
            Toggle(toggle);
            await WaitUntilAsync(() => optionWrites == 1 && toggle.IsEnabled, "保存语言偏好");
            Require(inspectCalls == 0 && magpieLaunchCalls == 0 && options.ForceSimplifiedChinese &&
                    configureCalls == 1 && Find<TextBlock>(window, "LanguageRuntimeStatusText").Text.StartsWith("已生效", StringComparison.Ordinal),
                "语言开关只保存并配置语言，不触发 Magpie 检测或启动");

            var expander = Find<Expander>(window, "MagpieSettingsExpander");
            expander.IsExpanded = true;
            await WaitUntilAsync(() => inspectCalls == 1, "展开 Magpie 后执行一次检测");
            Require(Find<TextBlock>(window, "SupportTitle").Text.Contains("启动", StringComparison.Ordinal),
                "Magpie 区域只显示启动参数状态");

            Find<Button>(window, "LaunchMagpieButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => magpieLaunchCalls == 1 && inspectCalls >= 2,
                "用户点击后启动 Magpie 并刷新只读检测");
            Require(optionWrites == 1 && configureCalls == 1 && options.ForceSimplifiedChinese,
                "Magpie 启动与语言偏好相互独立");

            Find<Button>(window, "LaunchDebugCodexButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => debugLaunchCalls == 1 &&
                Find<TextBlock>(window, "WindowStatusText").Text.Contains("已请求启动", StringComparison.Ordinal),
                "调试目标尚未就绪时显示等待状态");
            Require(optionWrites == 1 && configureCalls == 1,
                "启动调试版 Codex 不虚称应用语言或皮肤");
        }
        finally
        {
            window.Close();
        }
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
            launchDebugCodex: () =>
            {
                debugLaunchCalls++;
                return Task.FromResult(new RuntimeStatus(RuntimeStatusState.Error, null, null, launchError, false));
            },
            inspectMagpie: _ =>
            {
                inspectCalls++;
                return result;
            },
            launchMagpie: (_, _) =>
            {
                magpieLaunchCalls++;
                return Task.CompletedTask;
            });

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded, "窄窗口兼容设置页加载");
            await WaitUntilAsync(() => window.Opacity >= 0.999, "兼容设置页淡入完成");
            window.Width = 620;
            window.Height = 440;
            window.UpdateLayout();
            var expander = Find<Expander>(window, "MagpieSettingsExpander");
            expander.IsExpanded = true;
            await WaitUntilAsync(() => inspectCalls == 1, "窄窗口展开 Magpie 检测内容");
            await Task.Delay(250);
            window.UpdateLayout();

            var pathInput = Find<TextBox>(window, "ExecutablePathInput");
            var pathText = Find<TextBlock>(window, "ExecutablePathText");
            var evidenceText = Find<TextBlock>(window, "EvidenceText");
            var launchDebug = Find<Button>(window, "LaunchDebugCodexButton");
            var closeButton = Find<Button>(window, "CloseButton");
            var scrollViewer = Find<ScrollViewer>(window, "SettingsScrollViewer");
            Require(window.Width == 620 && window.Height == 440 && inspectCalls == 1,
                "兼容设置页在620×440逻辑尺寸工作，检测由fake服务完成");
            Require(launchDebug.Content?.ToString() == "启动 / 连接 Codex",
                "兼容设置页明确支持连接已有调试目标或启动新的调试版");
            Require(Find<TextBlock>(window, "SupportTitle").Text == "已核实：启动不附带调试参数" &&
                    Find<TextBlock>(window, "CompatibilityMessageText").Text.Contains("网关模型", StringComparison.Ordinal) &&
                    pathInput.Text == executablePath && pathText.Text.Contains(executablePath, StringComparison.Ordinal) &&
                    evidenceText.Text == longEvidence && scrollViewer.ScrollableHeight > 0,
                "Magpie 长路径与检测信息可滚动查看，文案限定为启动参数事实");

            Find<Button>(window, "LaunchDebugCodexButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => debugLaunchCalls == 1 &&
                Find<TextBlock>(window, "WindowStatusText").Text.StartsWith("启动未完成：", StringComparison.Ordinal),
                "兼容设置页显示fake启动失败");
            var status = Find<TextBlock>(window, "WindowStatusText");
            Require(status.Text.Contains("模拟调试版启动失败", StringComparison.Ordinal) &&
                    !status.Text.Contains("已请求启动", StringComparison.Ordinal) && magpieLaunchCalls == 0,
                "启动失败显示真实原因，且没有调用Magpie启动");

            window.UpdateLayout();
            var bounds = GetBounds(window, closeButton);
            var launchBounds = GetBounds(window, launchDebug);
            Require(closeButton.IsVisible && closeButton.IsEnabled &&
                    launchDebug.IsVisible && launchDebug.IsEnabled &&
                    IsWithinBounds(bounds, new Rect(0, 0, window.ActualWidth, window.ActualHeight)) &&
                    IsWithinBounds(launchBounds, new Rect(0, 0, window.ActualWidth, window.ActualHeight)) &&
                    !bounds.IntersectsWith(launchBounds),
                "长说明与错误下底部启动、关闭操作保持完整可见且互不遮挡");
            var detailsScrollViewer = Find<ScrollViewer>(window, "MagpieDetailsScrollViewer");
            detailsScrollViewer.BringIntoView();
            await Task.Delay(50);
            detailsScrollViewer.ScrollToVerticalOffset(detailsScrollViewer.ScrollableHeight);
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout());
            var detailsBounds = GetBounds(window, detailsScrollViewer);
            var settingsBounds = GetBounds(window, scrollViewer);
            Require(detailsBounds.IntersectsWith(settingsBounds) &&
                    IsWithinBounds(GetBounds(window, closeButton), new Rect(0, 0, window.ActualWidth, window.ActualHeight)) &&
                    IsWithinBounds(GetBounds(window, launchDebug), new Rect(0, 0, window.ActualWidth, window.ActualHeight)),
                "滚动长检测详情时底栏启动与关闭按钮仍固定可见");
            await SaveScreenshotAsync(window, Path.Combine(Program.FindTestOutputDirectory(), "兼容设置-最小窗口-620x440.png"));
            closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !window.IsVisible, "兼容设置页关闭按钮关闭窗口");
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
        var window = CreateWindow();
        window.Left = -2_000;
        window.Top = -2_000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded, "兼容设置页响应Escape");
            var source = PresentationSource.FromVisual(window) ??
                throw new InvalidOperationException("兼容设置页尚未连接到WPF窗口消息源。");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            await WaitUntilAsync(() => !window.IsVisible, "按Escape关闭兼容设置页");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static SettingsWindow CreateWindow(
        RuntimeCompatibilityOptions? options = null,
        bool settingsLoaded = true,
        string? settingsLoadError = null,
        RuntimeCompatibilityResult? lastRuntimeResult = null,
        Func<RuntimeCompatibilityOptions, CancellationToken, Task<RuntimeCompatibilityResult?>>? saveAndConfigure = null,
        Func<RuntimeCompatibilityOptions>? reloadSettings = null,
        Func<Task<RuntimeStatus>>? launchDebugCodex = null,
        Func<string?, MagpieCompatibilityResult>? inspectMagpie = null,
        Func<string?, CancellationToken, Task>? launchMagpie = null) =>
        new(
            options ?? new RuntimeCompatibilityOptions(),
            settingsLoaded,
            settingsLoadError,
            lastRuntimeResult,
            saveAndConfigure ?? ((value, _) => Task.FromResult<RuntimeCompatibilityResult?>(null)),
            reloadSettings ?? (() => new RuntimeCompatibilityOptions()),
            launchDebugCodex ?? (() => Task.FromResult(new RuntimeStatus(RuntimeStatusState.Connecting, null, null, "fake", true))),
            inspectMagpie ?? (_ => new MagpieCompatibilityResult(
                false, false, null, null, MagpieCompatibilitySupport.NotInstalled, false, "fake evidence", "未检测到")),
            launchMagpie ?? ((_, _) => Task.CompletedTask))
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
            : throw new InvalidOperationException($"兼容设置页缺少控件 {name}。");

    private static void Toggle(CheckBox checkBox)
    {
        checkBox.Focus();
        var peer = new CheckBoxAutomationPeer(checkBox);
        var toggle = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider ??
            throw new InvalidOperationException("简体中文开关没有Toggle自动化操作。");
        toggle.Toggle();
    }

    private static Rect GetBounds(FrameworkElement ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

    private static bool IsWithinBounds(Rect bounds, Rect viewport) =>
        bounds.Left >= viewport.Left - 1 && bounds.Top >= viewport.Top - 1 &&
        bounds.Right <= viewport.Right + 1 && bounds.Bottom <= viewport.Bottom + 1;

    private static async Task SaveScreenshotAsync(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        image.Render(window);
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
                throw new InvalidOperationException($"兼容设置回归失败：{label}超时。");
            }

            await Task.Delay(25);
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"兼容设置回归失败：{label}");
        }

        Console.WriteLine($"通过：{label}");
    }
}

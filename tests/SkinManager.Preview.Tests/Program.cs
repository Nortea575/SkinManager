using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using System.Windows.Markup;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Runtime;
using SkinManager.Updating;

namespace SkinManager.Preview.Tests;

internal static class Program
{
    private static int _exitCode = 1;
    private static string? _cleanupRoot;

    private const string ChatInkFixtureCss = """
        :where(:root,[data-theme]) {
          --color-text: var(--app-color-text-foreground);
          --color-text-prose: var(--color-text);
          --color-text-emphasis: var(--app-color-text-foreground,var(--color-text));
          --color-text-secondary: var(--app-color-text-secondary,var(--app-color-text-foreground));
          --color-text-tertiary: var(--app-color-text-foreground-tertiary);
        }
        /* The local foreground is the measured runtime value, not the shipped light fallback (#1a1c1f). */
        .chat-local-scope {
          --app-color-text-foreground: #201a18;
          --app-color-text-foreground-secondary: color-mix(in srgb,#201a18 65%,transparent);
          --app-color-text-foreground-tertiary: color-mix(in srgb,#201a18 45%,transparent);
          --color-text: #201a18;
          --color-text-prose: #201a18;
          --color-text-emphasis: #201a18;
          --color-text-primary: #201a18;
          --color-text-secondary: color-mix(in srgb,#201a18 65%,transparent);
          --color-text-tertiary: color-mix(in srgb,#201a18 45%,transparent);
        }
        /* Minimal source-derived RichText rules from d926df36a3a1.css. */
        ._TextBase_1ccqq_1 { display: var(--text-base-display,inline); color: var(--text-base-color,var(--color-text-prose)); }
        ._Text_18604_1 { --text-base-color: var(--color-text-prose); --text-base-display: block; }
        ._Caption_sef0d_1 { --text-base-color: var(--color-text-secondary); --text-base-display: block; }
        ._Title_o98eq_1 { --text-base-color: var(--color-text-prose); --text-base-display: block; }
        ._List_1xyux_1 { color: var(--color-text-prose); }
        /* Measured local TextBase overrides, kept on real text nodes without data-theme. */
        .chat-local-scope [class*="_TextBase_"] {
          --app-color-text-foreground: #201a18;
          --app-color-text-foreground-secondary: color-mix(in srgb,#201a18 65%,transparent);
          --app-color-text-foreground-tertiary: color-mix(in srgb,#201a18 45%,transparent);
          --color-text: #201a18;
          --color-text-prose: #201a18;
          --color-text-emphasis: #201a18;
          --color-text-primary: #201a18;
          --color-text-secondary: color-mix(in srgb,#201a18 65%,transparent);
          --color-text-tertiary: color-mix(in srgb,#201a18 45%,transparent);
        }
        ._MarkdownRoot_xjxw3_2[data-markdown-text-tone=inherit] ._ExternalMarkdownLink_xjxw3_2 {
          color: var(--markdown-link-color,currentColor);
        }
        body { margin: 0; background: #13202b; font: 16px/1.5 system-ui,sans-serif; }
        #chat-ink-card { box-sizing: border-box; min-height: 100vh; padding: 28px 36px; background: #13202b; color: #e5efe9; }
        #chat-ink-step { margin: 0 0 22px; color: #afc6c0; font-size: 14px; font-weight: 600; }
        #chat-assistant { padding: 22px 26px; border: 1px solid #405d62; border-radius: 14px; background: #1b2c36; }
        #chat-user { margin-top: 18px; padding: 10px 18px; color: #afc6c0; }
        #chat-heading { margin: 18px 0 8px; }
        #chat-list { margin: 8px 0; padding-left: 24px; }
        #chat-secondary { margin-top: 14px; }
        #chat-link { margin-left: 14px; }
        #chat-code { margin-top: 12px; }
        """;

    private const string ChatInkFixtureHtml = """
        <main id="chat-ink-card">
          <h1 id="chat-ink-step">2.3.4 root-only aliases:正文仍为 #201a18</h1>
          <article id="chat-assistant" data-skin-part="message.assistant">
            <div id="chat-markdown-root" class="_MarkdownRoot_xjxw3_2" data-markdown-text-style="assistant-message" data-markdown-text-tone="inherit" style="--markdown-link-color:#1878d4">
              <section id="chat-local-scope" class="chat-local-scope">
                <p id="chat-paragraph" class="_TextBase_1ccqq_1 _Text_18604_1">Assistant reply paragraph</p>
                <h2 id="chat-heading" class="_TextBase_1ccqq_1 _Title_o98eq_1">A response heading</h2>
                <ul id="chat-list" class="_List_1xyux_1"><li><span id="chat-list-body" class="_TextBase_1ccqq_1 _Text_18604_1">A list item body</span></li></ul>
                <span id="chat-secondary" class="_TextBase_1ccqq_1 _Caption_sef0d_1">Secondary status</span>
                <a id="chat-link" data-d-component="link" class="_ExternalMarkdownLink_xjxw3_2" href="#offline-link"><span id="chat-link-text" class="_TextBase_1ccqq_1 _Text_18604_1" style="--color-text-prose:var(--markdown-link-color)">Explicit markdown link</span></a>
                <pre id="chat-code"><code><span id="chat-code-token" class="_TextBase_1ccqq_1 _Text_18604_1" style="--color-text-prose:rgb(203,85,20)">const item = 1;</span></code></pre>
              </section>
            </div>
          </article>
          <article id="chat-user" data-skin-part="message.user">
            <div class="_MarkdownRoot_xjxw3_2" data-markdown-text-style="user-message" data-markdown-text-tone="inherit">
              <section class="chat-local-scope"><p id="chat-user-text" class="_TextBase_1ccqq_1 _Text_18604_1">User message stays independent</p></section>
            </div>
          </article>
        </main>
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        var reviewUi = args.Contains("--review-ui", StringComparer.OrdinalIgnoreCase);
        var reviewCapture = args.Contains("--review-capture", StringComparer.OrdinalIgnoreCase);
        var marketUiOnly = args.Contains("--market-ui-only", StringComparer.OrdinalIgnoreCase);
        var marketLivePreviewImages = args.Contains("--market-live-preview-images", StringComparer.OrdinalIgnoreCase);
        var managerInstanceOnly = args.Contains("--manager-instance-only", StringComparer.OrdinalIgnoreCase);
        var managerInstancePostedOpen = args.Contains("--manager-instance-posted-open", StringComparer.OrdinalIgnoreCase);
        var managerInstanceDelay = ReadManagerInstanceDelay(args);
        var motionOnly = args.Contains("--motion-only", StringComparer.OrdinalIgnoreCase);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources = LoadOfflineApplicationResources();
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (marketLivePreviewImages)
                {
                    await MarketUiChecks.RunLivePreviewFetchAsync();
                }
                else if (marketUiOnly)
                {
                    await RunMarketUiOnlyAsync();
                }
                else if (reviewUi || reviewCapture)
                {
                    await RunReviewUiAsync(reviewCapture);
                }
                else if (managerInstanceOnly)
                {
                    await RunManagerInstanceOnlyAsync(managerInstanceDelay, managerInstancePostedOpen);
                }
                else if (motionOnly)
                {
                    await MotionBehaviorChecks.RunAsync();
                }
                else
                {
                    await RunAsync();
                }
                _exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"预览验收失败：{exception}");
                _exitCode = 1;
            }
            finally
            {
                application.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
            }
        }), System.Windows.Threading.DispatcherPriority.Normal);

        System.Windows.Threading.Dispatcher.Run();
        if (_cleanupRoot is not null)
        {
            TryDeleteDirectory(_cleanupRoot);
        }

        return _exitCode;
    }

    private static async Task RunReviewUiAsync(bool captureScreenshots)
    {
        Check(UpdateCoordinator.ReadCurrentVersion(typeof(MainWindow).Assembly) == new Version(1, 0, 0),
            "更新比较读取公开软件版本 1.0.0");
        var reviewRoot = Path.Combine(Path.GetTempPath(), "SkinManagerPreviewReview", Guid.NewGuid().ToString("N"));
        _cleanupRoot = reviewRoot;
        Directory.CreateDirectory(reviewRoot);
        var store = new SkinStore(Path.Combine(reviewRoot, "skins"));
        store.Initialize();
        var ayanamiBytes = ReadEmbeddedPackage("Ayanami");
        store.Save(SkinPackageReader.ReadZipBytes(ayanamiBytes), replaceExisting: false);
        store.Save(SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Saya")), replaceExisting: false);

        var updateClient = new FakeUpdateClient(Path.Combine(reviewRoot, "downloads"), new Version(1, 0, 0));
        var updateCoordinator = new UpdateCoordinator(
            updateClient,
            Path.Combine(reviewRoot, "updates"),
            new Version(1, 0, 0));
        updateCoordinator.AutomaticCheckAndDownload = false;
        var fakeInstances = new FakeCodexInstanceService(
        [
            CreateFakeInstance(7901, 9335, CodexInstanceKind.Debug, canConnect: true, "离线审阅用虚拟实例"),
            CreateFakeInstance(7902, null, CodexInstanceKind.Normal, canConnect: false, "离线审阅普通实例")
        ]);
        var window = new MainWindow(
            store,
            App.ReadEmbeddedBasePackage(),
            previewProfileDirectory: Path.Combine(reviewRoot, "browser-profile"),
            readRuntimeStatusOnLoad: true,
            updateCoordinator: updateCoordinator,
            installerLauncher: new FakeInstallerLauncher { ShouldStart = false },
            closeAfterSuccessfulInstall: false,
            codexInstanceService: fakeInstances,
            runtimeStatusReader: _ => Task.FromResult(
                new RuntimeStatus(RuntimeStatusState.Stopped, null, null, null, null)),
            skinMarketClientFactory: MarketUiChecks.CreateReviewClientFactory(ayanamiBytes))
        {
            Width = 1240,
            Height = 840,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        if (captureScreenshots)
        {
            try
            {
                await CaptureReviewScreenshotsAsync(window, ayanamiBytes);
            }
            finally
            {
                AllowTestWindowClose(window);
                window.Close();
            }

            return;
        }

        Console.WriteLine("离线审阅窗口已打开：只使用临时皮肤库、虚拟实例、fixture 市场和 fake 更新服务。关闭主窗口后会清理临时数据。");
        await closed.Task;
    }

    private static async Task RunMarketUiOnlyAsync()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SkinManagerMarketUi", Guid.NewGuid().ToString("N"));
        _cleanupRoot = testRoot;
        Directory.CreateDirectory(testRoot);
        var screenshotDirectory = FindTestOutputDirectory();
        Directory.CreateDirectory(screenshotDirectory);
        var basePackage = App.ReadEmbeddedBasePackage();
        var ayanamiBytes = ReadEmbeddedPackage("Ayanami");
        await MarketUiPreviewChecks.RunAsync(
            testRoot,
            basePackage,
            ayanamiBytes,
            ReadEmbeddedPackage("Saya"),
            screenshotDirectory);
        await MarketUiChecks.RunAsync(testRoot, basePackage, ayanamiBytes, screenshotDirectory);
    }

    private static async Task RunManagerInstanceOnlyAsync(int autoSelectDelayMilliseconds, bool postOpen)
    {
        var previousAnimationOverride = WindowMotion.AnimationEnabledOverrideForTests;
        WindowMotion.AnimationEnabledOverrideForTests = true;
        var testRoot = Path.Combine(Path.GetTempPath(), "SkinManagerManagerInstanceOnly", Guid.NewGuid().ToString("N"));
        _cleanupRoot = testRoot;
        Directory.CreateDirectory(testRoot);
        var ayanamiBytes = ReadEmbeddedPackage("Ayanami");
        var ayanami = SkinPackageReader.ReadZipBytes(ayanamiBytes);
        var basePackage = App.ReadEmbeddedBasePackage();
        var store = new SkinStore(Path.Combine(testRoot, "manager-data"));
        store.Initialize();
        store.Save(ayanami, replaceExisting: false);

        var connectedDebug = CreateFakeInstance(7411, 9335, CodexInstanceKind.Debug, canConnect: true, "Codex 当前连接实例");
        var selectedDebug = CreateFakeInstance(7412, 9336, CodexInstanceKind.Debug, canConnect: true, "Codex 可选调试实例");
        var unavailableDebug = CreateFakeInstance(7413, 9444, CodexInstanceKind.Debug, canConnect: false, "Codex 未就绪调试实例");
        var ordinary = CreateFakeInstance(7414, null, CodexInstanceKind.Normal, canConnect: false, "Codex 普通实例");
        var fakeInstances = new FakeCodexInstanceService([connectedDebug, selectedDebug, unavailableDebug, ordinary]);
        var fakeStatusReadCount = 0;
        var fakeConnectedStatus = new RuntimeStatus(
            RuntimeStatusState.Connected,
            ayanami.Id,
            "http://127.0.0.1:9335",
            "fake connected",
            true,
            1,
            connectedDebug.Identity);
        var fakeStoppedStatus = new RuntimeStatus(RuntimeStatusState.Stopped, null, null, "fake closed", null);

        var window = new MainWindow(
            store,
            basePackage,
            Path.Combine(testRoot, "manager-preview-cache"),
            readRuntimeStatusOnLoad: true,
            codexInstanceService: fakeInstances,
            confirmInstanceClose: _ => true,
            confirmInstanceForceClose: _ => true,
            runtimeStatusReader: _ =>
            {
                fakeStatusReadCount++;
                return Task.FromResult(fakeInstances.Instances.Any(instance => instance.Identity == connectedDebug.Identity)
                    ? fakeConnectedStatus
                    : fakeStoppedStatus);
            })
        {
            Width = 960,
            Height = 640,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };

        var deadline = DateTime.UtcNow.AddSeconds(15);
        DateTime? dialogLoadedAt = null;
        var dialogSeen = false;
        var selectionAttempted = false;
        var timeoutReached = false;
        bool? dialogResultOnClose = null;
        var returnedDialog = default(InstancesWindow);
        var runtimePortInput = FindNamed<TextBox>(window, "RuntimePortInput");
        var runtimeTargetText = FindNamed<TextBlock>(window, "RuntimeTargetText");
        var manageInstancesButton = FindNamed<Button>(window, "ManageInstancesButton");
        var selectionTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        selectionTimer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<InstancesWindow>().FirstOrDefault();
            if (dialog is not null && !dialogSeen)
            {
                dialogSeen = true;
                dialogLoadedAt = DateTime.UtcNow;
                var handle = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
                var opacitySource = DependencyPropertyHelper.GetValueSource(dialog, Window.OpacityProperty);
                Console.WriteLine($"[manager-instance-only] 窗口已出现 hwnd=0x{handle.ToInt64():X}, IsLoaded={dialog.IsLoaded}, IsVisible={dialog.IsVisible}, Opacity={dialog.Opacity:0.###}, BaseOpacity={dialog.GetAnimationBaseValue(Window.OpacityProperty):0.###}, OpacityAnimated={opacitySource.IsAnimated}, AnimationsEnabled={WindowMotion.AnimationsEnabled}, Instances.Count={dialog.Instances.Count}, autoSelectDelay={autoSelectDelayMilliseconds}ms");
                dialog.Closing += (_, args) =>
                {
                    Console.WriteLine($"[manager-instance-only] Closing: Cancel={args.Cancel}, DialogResult={TryReadDialogResult(dialog)?.ToString() ?? "null"}, Opacity={dialog.Opacity:0.###}");
                };
                dialog.Closed += (_, _) =>
                {
                    dialogResultOnClose = TryReadDialogResult(dialog);
                    Console.WriteLine($"[manager-instance-only] Closed: DialogResult={dialogResultOnClose?.ToString() ?? "null"}, Opacity={dialog.Opacity:0.###}");
                };
                dialog.SourceInitialized += (_, _) =>
                {
                    var initializedHandle = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
                    Console.WriteLine($"[manager-instance-only] SourceInitialized hwnd=0x{initializedHandle.ToInt64():X}");
                };

                if (WindowMotion.AnimationsEnabled)
                {
                    dialog.BeginAnimation(Window.OpacityProperty, null);
                    dialog.Opacity = 0;
                    dialogLoadedAt = DateTime.UtcNow;
                    WindowMotion.AnimateOpacity(dialog, 1, 165);
                    Console.WriteLine("[manager-instance-only] 为确定性复现，测试端重启当前 HWND 的 165ms 淡入，再按指定时点选择。");
                }
            }

            if (dialog is { IsLoaded: true, IsVisible: true } && !selectionAttempted && dialog.Instances.Count == 4 &&
                dialogLoadedAt is DateTime loadedAt &&
                DateTime.UtcNow - loadedAt >= TimeSpan.FromMilliseconds(autoSelectDelayMilliseconds))
            {
                selectionAttempted = true;
                var handle = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
                var dialogList = FindNamed<ListView>(dialog, "InstanceList");
                var targetRow = FindInstanceRow(dialogList, selectedDebug.Identity);
                var connectedText = RowProperty(FindInstanceRow(dialogList, connectedDebug.Identity), "ConnectionText");
                var targetText = RowProperty(targetRow, "ConnectionText");
                dialogList.SelectedItem = targetRow;
                var useButton = FindNamed<Button>(dialog, "UseInstanceButton");
                returnedDialog = dialog;
                var opacitySource = DependencyPropertyHelper.GetValueSource(dialog, Window.OpacityProperty);
                Console.WriteLine($"[manager-instance-only] 状态已就绪 hwnd=0x{handle.ToInt64():X}, elapsedSinceLoadedObservation={(DateTime.UtcNow - loadedAt).TotalMilliseconds:0}ms, IsLoaded={dialog.IsLoaded}, Opacity={dialog.Opacity:0.###}, BaseOpacity={dialog.GetAnimationBaseValue(Window.OpacityProperty):0.###}, OpacityAnimated={opacitySource.IsAnimated}, Instances.Count={dialog.Instances.Count}, connected='{connectedText}', target='{targetText}', UseEnabled={useButton.IsEnabled}");
                if (useButton.IsEnabled)
                {
                    useButton.Click += (_, _) => Console.WriteLine($"[manager-instance-only] 选择Click返回: SelectedForConnection={dialog.SelectedForConnection?.Identity.ProcessId.ToString() ?? "null"}, DialogResult={TryReadDialogResult(dialog)?.ToString() ?? "null"}, IsVisible={dialog.IsVisible}");
                    useButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Console.WriteLine($"[manager-instance-only] RaiseEvent返回: SelectedForConnection={dialog.SelectedForConnection?.Identity.ProcessId.ToString() ?? "null"}, DialogResult={TryReadDialogResult(dialog)?.ToString() ?? "null"}, IsVisible={dialog.IsVisible}, Opacity={dialog.Opacity:0.###}");
                }
                else
                {
                    Console.WriteLine("[manager-instance-only] 连接选择按钮已禁用，未派发选择事件。");
                    dialog.Close();
                }
            }

            if (DateTime.UtcNow >= deadline && !timeoutReached)
            {
                timeoutReached = true;
                Console.WriteLine($"[manager-instance-only] 超时：main IsLoaded={window.IsLoaded}, IsVisible={window.IsVisible}, Opacity={window.Opacity:0.###}, statusReads={fakeStatusReadCount}, dialogSeen={dialogSeen}, selectionAttempted={selectionAttempted}");
                WindowMotion.AnimationEnabledOverrideForTests = false;
                if (dialog is { IsVisible: true })
                {
                    try { dialog.DialogResult = false; } catch (InvalidOperationException) { dialog.Close(); }
                }
                else
                {
                    Application.Current.Shutdown(1);
                }
            }
        };

        window.Show();
        Console.WriteLine($"[manager-instance-only] 主窗已 Show，等待 fake RuntimeStatus 和模态实例窗口；deadline={deadline:O}");
        try
        {
            await WaitUntilAsync(() => Task.FromResult(window.IsLoaded && fakeStatusReadCount > 0 &&
                FindNamed<Button>(window, "ManageInstancesButton").IsEnabled), "主窗fake连接状态可打开实例管理");
            Console.WriteLine($"[manager-instance-only] MainWindow就绪: hwnd=0x{new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64():X}, status={FindNamed<TextBlock>(window, "RuntimeStatusText").Text}, ManageEnabled={manageInstancesButton.IsEnabled}");

            var browser = FindNamed<WebView2>(window, "PreviewBrowser");
            var skinList = FindNamed<ListBox>(window, "SkinList");
            await WaitUntilAsync(() => Task.FromResult(skinList.Items.Cast<object>().Any(row => GetRowId(row) == ayanami.Id)),
                "fake manager skin list loaded before modal open");
            skinList.SelectedItem = skinList.Items.Cast<object>().Single(row => GetRowId(row) == ayanami.Id);
            var renderer = GetWindowRenderer(window);
            await WaitUntilAsync(async () => browser.CoreWebView2 is not null && renderer.CurrentResolvedSkin is not null &&
                await ReadWindowPageAsync(browser) == "package:home", "fake manager preview reached Ayanami home before modal open");
            var webViewProbe = await ReadObjectAsync(renderer, "({ ready: document.readyState, page: document.documentElement.getAttribute('data-page') || '' })");
            Console.WriteLine($"[manager-instance-only] WebView2 script await returned: {JsonSerializer.Serialize(webViewProbe)}; openMode={(postOpen ? "Dispatcher" : "direct")}");

            selectionTimer.Start();
            if (postOpen)
            {
                await window.Dispatcher.InvokeAsync(
                    () => manageInstancesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                manageInstancesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            selectionTimer.Stop();

            var accepted = returnedDialog is not null && dialogResultOnClose == true &&
                returnedDialog.SelectedForConnection?.Identity == selectedDebug.Identity &&
                runtimePortInput.Text == selectedDebug.DebugPort?.ToString() &&
                runtimeTargetText.Text.Contains($"PID {selectedDebug.Identity.ProcessId}", StringComparison.Ordinal);
            Console.WriteLine($"[manager-instance-only] ShowDialog返回: accepted={dialogResultOnClose == true}, dialogResult={dialogResultOnClose?.ToString() ?? "null"}, SelectedForConnection={returnedDialog?.SelectedForConnection?.Identity.ProcessId.ToString() ?? "null"}, port={runtimePortInput.Text}, target='{runtimeTargetText.Text}', timedOut={timeoutReached}");
            Check(!timeoutReached && accepted, "主窗实例选择模态链路返回true并设置fake实例端口");
        }
        finally
        {
            selectionTimer.Stop();
            AllowTestWindowClose(window);
            if (returnedDialog is { IsVisible: true })
            {
                WindowMotion.AnimationEnabledOverrideForTests = false;
                returnedDialog.DialogResult = false;
            }
            window.Close();
            WindowMotion.AnimationEnabledOverrideForTests = previousAnimationOverride;
        }
    }

    private static bool? TryReadDialogResult(Window window)
    {
        try
        {
            return window.DialogResult;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int ReadManagerInstanceDelay(string[] args)
    {
        const string prefix = "--manager-instance-delay=";
        var value = args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (value is null)
        {
            return 60;
        }

        if (!int.TryParse(value[prefix.Length..], out var delay) || delay is < 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "--manager-instance-delay 必须介于 0 和 1000 毫秒之间。");
        }

        return delay;
    }

    private static async Task CaptureReviewScreenshotsAsync(MainWindow window, byte[] ayanamiBytes)
    {
        var screenshots = FindTestOutputDirectory();
        Directory.CreateDirectory(screenshots);
        await WaitUntilAsync(() => Task.FromResult(window.IsLoaded && window.Opacity >= 0.999),
            "离线审阅主窗完成加载");

        var browser = FindNamed<WebView2>(window, "PreviewBrowser");
        var list = FindNamed<ListBox>(window, "SkinList");
        await WaitUntilAsync(() => Task.FromResult(list.Items.Count >= 2), "离线审阅皮肤列表完成加载");
        list.SelectedItem = list.Items.Cast<object>().Single(row => GetRowId(row) == "ayanami");
        var renderer = GetWindowRenderer(window);
        await WaitUntilAsync(async () => browser.CoreWebView2 is not null && renderer.CurrentResolvedSkin is not null &&
            await ReadWindowPageAsync(browser) == "package:home", "离线审阅预览显示 Ayanami 首页");
        await PrepareNeutralCaptureAsync(renderer, browser);

        window.Width = 1240;
        window.Height = 840;
        window.UpdateLayout();
        await Task.Delay(190);
        await SaveManagerWindowScreenshotAsync(window, browser, Path.Combine(screenshots, "管理器-常规窗口.png"));

        window.Width = 960;
        window.Height = 640;
        window.UpdateLayout();
        await Task.Delay(190);
        await SaveManagerWindowScreenshotAsync(window, browser, Path.Combine(screenshots, "管理器-最小窗口-960x640.png"));

        FindNamed<Button>(window, "EditSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = FindNamed<SkinEditorView>(window, "SkinEditor");
        await WaitUntilAsync(() => Task.FromResult(editor.Visibility == Visibility.Visible && editor.Opacity >= 0.999),
            "离线审阅制作器完成显示");
        await Task.Delay(400);
        await SaveManagerWindowScreenshotAsync(window, browser, Path.Combine(screenshots, "制作器-最小窗口-960x640.png"));

        window.Width = 1240;
        window.Height = 840;
        window.UpdateLayout();
        await Task.Delay(180);
        await SaveManagerWindowScreenshotAsync(window, browser, Path.Combine(screenshots, "制作器-常规窗口.png"));
        Console.WriteLine($"已更新主窗与制作器截图：{screenshots}");
    }

    private static async Task RunAsync()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SkinManagerPreviewTests", Guid.NewGuid().ToString("N"));
        _cleanupRoot = testRoot;
        Directory.CreateDirectory(testRoot);
        await MotionBehaviorChecks.RunAsync();
        var screenshotDirectory = FindTestOutputDirectory();
        Directory.CreateDirectory(screenshotDirectory);

        var basePackage = App.ReadEmbeddedBasePackage();
        var ayanamiBytes = ReadEmbeddedPackage("Ayanami");
        var nativeBytes = ReadEmbeddedPackage("Native");
        var sayaBytes = ReadEmbeddedPackage("Saya");
        var ayanami = SkinPackageReader.ReadZipBytes(ayanamiBytes);
        var native = SkinPackageReader.ReadZipBytes(nativeBytes);
        var saya = SkinPackageReader.ReadZipBytes(sayaBytes);
        var stateFixture = CreateStateFixture(Path.Combine(testRoot, "state-fixture"));
        var noneFixture = CreateStateFixture(Path.Combine(testRoot, "none-fixture"), "preview-none-fixture", noneAssistant: true);
        var assistantColorFixture = CreateStateFixture(
            Path.Combine(testRoot, "assistant-color-fixture"),
            "preview-assistant-color-fixture",
            assistantColor: "#8d2b9d");
        var declaredDark = CreateDeclaredDarkFixture(Path.Combine(testRoot, "declared-dark"));
        var systemFixture = CreateSystemAppearanceFixture(Path.Combine(testRoot, "system-appearance"));

        Check(ayanami.Id == "ayanami" && ayanami.Definition.FormatVersion == 1, "Ayanami 样例使用 skin.json 格式 v1");
        Check(ayanami.ImageBytes is { Length: > 0 }, "Ayanami 样例包含可读的背景资源");
        var thumbnail = PreviewImageCropper.CreateThumbnail(
            ayanami.ImageBytes!, 76, 58, 1, 1, ayanami.FocusX ?? 0.5, ayanami.FocusY ?? 0.5);
        Check(thumbnail is WriteableBitmap && thumbnail.IsFrozen && thumbnail.PixelWidth <= 76 && thumbnail.PixelHeight <= 58,
            "皮肤卡片使用按显示尺寸缩小且与原始大图分离的缩略图");
        var highDpiThumbnail = PreviewImageCropper.CreateThumbnail(
            ayanami.ImageBytes!, 76, 58, 2, 2, ayanami.FocusX ?? 0.5, ayanami.FocusY ?? 0.5);
        Check(highDpiThumbnail.PixelWidth <= 152 && highDpiThumbnail.PixelHeight <= 116,
            "缩略图像素上限随显示 DPI 缩放");
        var focusSource = new WriteableBitmap(200, 100, 96, 96, PixelFormats.Bgra32, null);
        var focusPixels = new byte[200 * 100 * 4];
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 200; x++)
            {
                var offset = (y * 200 + x) * 4;
                focusPixels[offset + (x < 100 ? 2 : 0)] = byte.MaxValue;
                focusPixels[offset + 3] = byte.MaxValue;
            }
        }
        focusSource.WritePixels(new Int32Rect(0, 0, 200, 100), focusPixels, 200 * 4, 0);
        focusSource.Freeze();
        var leftFocusCrop = PreviewImageCropper.CropToRatio(focusSource, 1, 0.25, 0.5);
        var rightFocusCrop = PreviewImageCropper.CropToRatio(focusSource, 1, 0.75, 0.5);
        var leftFocusPixels = new byte[leftFocusCrop.PixelWidth * leftFocusCrop.PixelHeight * 4];
        var rightFocusPixels = new byte[rightFocusCrop.PixelWidth * rightFocusCrop.PixelHeight * 4];
        leftFocusCrop.CopyPixels(leftFocusPixels, leftFocusCrop.PixelWidth * 4, 0);
        rightFocusCrop.CopyPixels(rightFocusPixels, rightFocusCrop.PixelWidth * 4, 0);
        var sampleOffset = (leftFocusCrop.PixelHeight / 2 * leftFocusCrop.PixelWidth + leftFocusCrop.PixelWidth / 2) * 4;
        Check(leftFocusCrop is WriteableBitmap && rightFocusCrop is WriteableBitmap &&
              leftFocusPixels[sampleOffset + 2] == byte.MaxValue && rightFocusPixels[sampleOffset] == byte.MaxValue,
            "缩略图独立保存裁切结果并保留左右焦点位置");
        Check(native.Definition.Tokens.Count == 0 && native.Definition.Components.Count == 0 && native.Definition.Background is null,
            "Native 样例不声明覆盖，保持原版外观");
        Check(native.ImageBytes is null, "无图皮肤没有背景图资源");
        Check(saya.Id == "saya" && saya.ImageBytes is { Length: > 0 } &&
              saya.Appearance == SkinAppearance.Dark,
            "Saya 深色示例读取出声明的深色外观与背景资源");

        await MarketUiChecks.RunAsync(testRoot, basePackage, ayanamiBytes, screenshotDirectory);

        var sayaLight = SkinResolver.Resolve(saya, SkinAppearance.Light, SkinScene.Home);
        var sayaDark = SkinResolver.Resolve(saya, SkinAppearance.Dark, SkinScene.Home);
        Check(sayaLight.Colors["color.surface.window"] != sayaDark.Colors["color.surface.window"] &&
              sayaDark.Background?.ImageBytes is { Length: > 0 },
            "Saya 浅色变体与深色背景均能解析");
        Check(ContrastRatio(sayaDark.Colors["color.text.primary"], sayaDark.Colors["color.surface.window"]) >= 4.5 &&
              ContrastRatio(sayaDark.Colors["color.text.secondary"], sayaDark.Colors["color.surface.window"]) >= 4.5,
            "Saya 深色主次文字在窗口表面上保持 WCAG AA 对比度");

        var ayanamiLight = SkinResolver.Resolve(ayanami, SkinAppearance.Light, SkinScene.Home);
        var ayanamiDark = SkinResolver.Resolve(ayanami, SkinAppearance.Dark, SkinScene.Home);
        Check(ayanamiLight.Colors.Count > 0 && ayanamiDark.Colors.Count > 0, "Ayanami light / dark 均解析出有效色卡");
        Check(ayanamiLight.Colors.Any(pair => !ayanamiDark.Colors.TryGetValue(pair.Key, out var value) || value != pair.Value),
            "Ayanami 的浅色与深色令牌存在实际差异");

        var browser = new WebView2
        {
            Width = 960,
            Height = 540,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var rendererWindow = CreatePreviewWindow(browser, 960, 540);
        var footerTestBrowser = new WebView2
        {
            Width = 960,
            Height = 540,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var footerTestWindow = CreatePreviewWindow(footerTestBrowser, 960, 540);
        var renderer = new ThemePreviewRenderer(browser, Path.Combine(testRoot, "renderer-data", "PreviewBrowser"), basePackage);
        var requestedWebHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backgroundResponses = new List<string>();
        EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs>? requestAudit = null;
        EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2WebResourceResponseReceivedEventArgs>? responseAudit = null;

        rendererWindow.Show();
        try
        {
            await renderer.InitializeAsync();
            var core = browser.CoreWebView2 ?? throw new InvalidOperationException("WebView2 初始化后仍不可用。");
            requestAudit = (_, args) =>
            {
                if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requestUri) &&
                    requestUri.Scheme is "http" or "https")
                {
                    requestedWebHosts.Add(requestUri.Host);
                }
            };
            core.WebResourceRequested += requestAudit;
            responseAudit = (_, args) =>
            {
                var uri = args.Request.Uri;
                if (uri.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    uri.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    uri.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    backgroundResponses.Add($"{args.Response.StatusCode} {new Uri(uri).AbsolutePath}");
                }
            };
            core.WebResourceResponseReceived += responseAudit;

            await RenderBaseAsync(renderer, new PreviewOptions("home", "light"));
            await WaitForPageAsync(renderer, "base", "home");
            var baseProbe = await ReadPreviewProbeAsync(renderer);
            await PrepareNeutralCaptureAsync(renderer, browser);
            await SaveScreenshotAsync(renderer, Path.Combine(screenshotDirectory, "native-home-light.png"));
            Console.WriteLine($"DOM 探針：base/home composerCount={baseProbe["composerCount"]}, rows={baseProbe["composerRows"]}");
            Check(baseProbe["composerCount"] == "1", "原生首页只有一个活动 composer");
            await LogVisualEvidenceAsync(renderer, "Native base/home");

            await RenderPackageAsync(renderer, native, new PreviewOptions("home", "light"));
            await WaitForPageAsync(renderer, "package", "home");
            var nativeProbe = await ReadPreviewProbeAsync(renderer);
            Check(nativeProbe["composerCount"] == "1", "Native 皮肤首页只有一个活动 composer");
            await PrepareNeutralCaptureAsync(renderer, browser);
            await SaveScreenshotAsync(renderer, Path.Combine(screenshotDirectory, "native-package-light.png"));
            Check(PngPixelsMatch(
                    Path.Combine(screenshotDirectory, "native-home-light.png"),
                    Path.Combine(screenshotDirectory, "native-package-light.png")),
                "空配置 Native 包与原版基础页像素一致");
            await LogVisualEvidenceAsync(renderer, "Native package/home");

            await RenderPackageAsync(renderer, ayanami, new PreviewOptions("home", "light"));
            await WaitForPageAsync(renderer, "package", "home");
            CheckResolvedSkin(renderer, SkinAppearance.Light, SkinScene.Home, ayanami);
            var imageDecode = await DecodeBackgroundImageAsync(renderer);
            Check(imageDecode["state"] == "decoded" && imageDecode["width"] == "1672" && imageDecode["height"] == "941",
                "Ayanami 1672 × 941 背景图通过浏览器解码");
            Console.WriteLine($"背景资源HTTP响应：{string.Join(" | ", backgroundResponses)}");
            var imageSourceKind = imageDecode["source"].StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? "data URI"
                : imageDecode["source"].StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ? "blob URL" : "other";
            Console.WriteLine($"背景图解码：{JsonSerializer.Serialize(new { state = imageDecode["state"], width = imageDecode["width"], height = imageDecode["height"], imageSourceKind })}");
            await PrepareNeutralCaptureAsync(renderer, browser);
            await SaveScreenshotAsync(renderer, Path.Combine(screenshotDirectory, "ayanami-home-light.png"));
            await LogVisualEvidenceAsync(renderer, "Ayanami light/home");
            var lightLayers = await ReadBackgroundProbeAsync(renderer);

            await RenderPackageAsync(renderer, ayanami, new PreviewOptions("home", "dark"));
            await WaitForPageAsync(renderer, "package", "home");
            CheckResolvedSkin(renderer, SkinAppearance.Dark, SkinScene.Home, ayanami);
            await PrepareNeutralCaptureAsync(renderer, browser);
            await SaveScreenshotAsync(renderer, Path.Combine(screenshotDirectory, "ayanami-home-dark.png"));

            await RenderPackageAsync(renderer, saya, new PreviewOptions("home", "dark"));
            await WaitForPageAsync(renderer, "package", "home");
            CheckResolvedSkin(renderer, SkinAppearance.Dark, SkinScene.Home, saya);
            var sayaHomeProbe = await ReadPreviewProbeAsync(renderer);
            var sayaHomeBackground = await ReadBackgroundProbeAsync(renderer);
            var sayaHomeImage = await DecodeBackgroundImageAsync(renderer);
            var sayaHomeText = await ReadObjectAsync(renderer, "(() => { const target=document.querySelector('[data-skin-part=\\\"window\\\"]'); return { color: target ? getComputedStyle(target).color : '' }; })()");
            Check(sayaHomeProbe["composerCount"] == "1" &&
                  sayaHomeBackground["imageLayer"] == "present" &&
                  sayaHomeImage["state"] == "decoded" &&
                  sayaHomeText["color"] == CssColorToBrowserValue(sayaDark.Colors["color.text.primary"]),
                "Saya 首页深色预览显示背景图、正常布局与可读文字颜色");
            await PrepareNeutralCaptureAsync(renderer, browser);
            await SaveScreenshotAsync(renderer, Path.Combine(screenshotDirectory, "saya-home-dark.png"));

            await RenderPackageAsync(renderer, saya, new PreviewOptions("conversation", "dark"));
            await WaitForPageAsync(renderer, "package", "conversation");
            CheckResolvedSkin(renderer, SkinAppearance.Dark, SkinScene.Conversation, saya);
            var sayaConversationProbe = await ReadPreviewProbeAsync(renderer);
            var sayaConversationBackground = await ReadBackgroundProbeAsync(renderer);
            Check(sayaConversationProbe["composerCount"] == "1" &&
                  sayaConversationProbe["activePage"] == "task" &&
                  sayaConversationBackground["imageLayer"] == "present",
                "Saya 对话页深色预览保留背景图和单一活动 composer");
            await PrepareNeutralCaptureAsync(renderer, browser);
            await SaveScreenshotAsync(renderer, Path.Combine(screenshotDirectory, "saya-conversation-dark.png"));
            await LogVisualEvidenceAsync(renderer, "Saya dark/conversation");

            await RenderPackageAsync(renderer, ayanami, new PreviewOptions("conversation", "light"));
            await WaitForPageAsync(renderer, "package", "conversation");
            CheckResolvedSkin(renderer, SkinAppearance.Light, SkinScene.Conversation, ayanami);
            var conversationProbe = await ReadPreviewProbeAsync(renderer);
            Check(conversationProbe["composerCount"] == "1", "对话页只保留一个活动 composer");
            Check(conversationProbe["activePage"] == "task", "conversation 场景路由到原生对话布局");
            await PrepareNeutralCaptureAsync(renderer, browser);
            await SaveScreenshotAsync(renderer, Path.Combine(screenshotDirectory, "ayanami-conversation-light.png"));
            Console.WriteLine($"DOM 探針：conversation composerRows={conversationProbe["composerRows"]}, tracks={conversationProbe["composerRowsText"]}, display={conversationProbe["toolbarDisplay"]}");
            Check(conversationProbe["composerRows"] == "2", "composer 使用两行工具栏网格");
            await LogVisualEvidenceAsync(renderer, "Ayanami light/conversation");
            await RenderBaseAsync(renderer, new PreviewOptions("conversation", "light"));
            await WaitForPageAsync(renderer, "base", "conversation");
            await LogVisualEvidenceAsync(renderer, "Native base/conversation");

            var stateHome = SkinResolver.Resolve(stateFixture, SkinAppearance.Light, SkinScene.Home);
            await RenderPackageAsync(renderer, stateFixture, new PreviewOptions("home", "light"));
            await WaitForPageAsync(renderer, "package", "home");
            CheckResolvedSkin(renderer, SkinAppearance.Light, SkinScene.Home, stateFixture);
            rendererWindow.Activate();
            browser.Focus();
            await Task.Delay(100);
            await VerifyFocusedComposerAsync(renderer, stateHome);
            await VerifyButtonStatesAsync(browser, renderer, stateHome);

            var homeSurface = await ReadComposerBodySurfaceAsync(renderer);
            await RenderPackageAsync(renderer, stateFixture, new PreviewOptions("conversation", "light"));
            await WaitForPageAsync(renderer, "package", "conversation");
            var conversationSurface = await ReadComposerSurfaceAsync(renderer);
            Check(homeSurface != conversationSurface, "controlled fixture 的 home / conversation 样式差异实际生效");
            CheckResolvedSkin(renderer, SkinAppearance.Light, SkinScene.Conversation, stateFixture);
            var tokenSurfaces = await ReadTokenSurfaceProbeAsync(renderer);
            Check(tokenSurfaces["window"] != tokenSurfaces["assistant"] &&
                  tokenSurfaces["window"] != "rgba(0, 0, 0, 0)" && tokenSurfaces["assistant"] != "rgba(0, 0, 0, 0)",
                "window 与 assistantMessage 颜色分别投影到各自的实际表面");
            await PrepareNeutralCaptureAsync(renderer, browser);
            await SaveScreenshotAsync(renderer, Path.Combine(screenshotDirectory, "solid-no-image-conversation.png"));
            Check(renderer.CurrentResolvedSkin?.Background?.ImageBytes is null, "纯色无图皮肤解析后仍没有图片资源");
            var solidProbe = await ReadBackgroundProbeAsync(renderer);
            Check(solidProbe["imageLayer"] == "none", "纯色无图预览没有损坏图片层");
            Check(solidProbe["surface"] is not ("transparent" or "rgba(0, 0, 0, 0)"),
                "无图皮肤仍使用定义的纯色背景表面");

            await RenderPackageAsync(renderer, noneFixture, new PreviewOptions("conversation", "light"));
            await WaitForPageAsync(renderer, "package", "conversation");
            var noneProbe = await ReadObjectAsync(renderer, """
                (() => {
                  const surface = document.querySelector('[data-skin-part="message.assistant"] > div');
                  const style = getComputedStyle(surface);
                  return { background: style.backgroundColor, backgroundImage: style.backgroundImage, shadow: style.boxShadow };
                })()
                """);
            Check(noneProbe["background"] is "transparent" or "rgba(0, 0, 0, 0)",
                "component.background=none 实际输出 transparent 并移除背景图");
            Check(noneProbe["backgroundImage"] == "none", "component.background=none 清除实际元素背景图");
            Check(noneProbe["shadow"] == "none", "component.shadow=none 实际清除阴影");

            await RenderPackageAsync(renderer, ayanami, new PreviewOptions("conversation", "light", UserTransparency: 10));
            CheckPanelOpacity(renderer, ayanami, 0.90);
            var transparencyDocument = browser.Source;
            var lowTransparencyLayers = await ReadBackgroundProbeAsync(renderer);
            var lowTransparencyPanel = await ReadComposerSurfaceAsync(renderer);
            await RenderPackageAsync(renderer, ayanami, new PreviewOptions("conversation", "light", UserTransparency: 90));
            Check(browser.Source == transparencyDocument, "相同皮肤和场景只改透明度时复用当前预览文档");
            CheckPanelOpacity(renderer, ayanami, 0.10);
            var highTransparencyLayers = await ReadBackgroundProbeAsync(renderer);
            var highTransparencyPanel = await ReadComposerSurfaceAsync(renderer);
            Check(lowTransparencyPanel != highTransparencyPanel, "面板透明度控件改变了 composer 面板 alpha");
            Check(lowTransparencyLayers["imageOpacity"] == highTransparencyLayers["imageOpacity"] &&
                  lowTransparencyLayers["overlayOpacity"] == highTransparencyLayers["overlayOpacity"] &&
                  lowTransparencyLayers["imageLayer"] == highTransparencyLayers["imageLayer"],
                "面板透明度不改变壁纸图片、图片 alpha 或遮罩 alpha");
            CheckBackgroundUnchanged(renderer.CurrentResolvedSkin, ayanami);

            await RenderPackageAsync(renderer, ayanami, new PreviewOptions("home", "light", ShowOriginalBase: true));
            await WaitForPageAsync(renderer, "base", "home");
            await PrepareNeutralCaptureAsync(renderer, browser);
            await SaveScreenshotAsync(renderer, Path.Combine(screenshotDirectory, "ayanami-original-base.png"));
            await LogVisualEvidenceAsync(renderer, "Ayanami ShowOriginalBase/home");
            Check(PngPixelsMatch(
                    Path.Combine(screenshotDirectory, "native-home-light.png"),
                    Path.Combine(screenshotDirectory, "ayanami-original-base.png")),
                "原版对照恢复相同的基础画面");
            await RenderPackageAsync(renderer, ayanami, new PreviewOptions("home", "light"));
            await WaitForPageAsync(renderer, "package", "home");
            CheckResolvedSkin(renderer, SkinAppearance.Light, SkinScene.Home, ayanami);

            var oldRender = renderer.DisplayPackageAsync(ayanami, new PreviewOptions("conversation", "dark"));
            var latestRender = renderer.DisplayPackageAsync(native, new PreviewOptions("home", "light"));
            await Task.WhenAll(oldRender, latestRender);
            await WaitForPageAsync(renderer, "package", "home");
            var finalProbe = await ReadPreviewProbeAsync(renderer);
            Check(finalProbe["activePage"] == "home" && renderer.CurrentResolvedSkin?.Scene == SkinScene.Home,
                "快速切換完成後保留最後一次選擇的頁面與 resolved skin");

            await VerifyRuntimeBrowserPortalAsync(renderer, browser, ayanami, stateFixture);
            await VerifyRuntimeAppearanceAsync(renderer, browser, saya, ayanami, systemFixture);
            await VerifyChatAssistantInkAsync(
                renderer,
                ayanami,
                saya,
                native,
                stateFixture,
                assistantColorFixture,
                screenshotDirectory);

            Check(basePackage.Styles.All(path => renderer.ServedBaseResourcePaths.Contains(path, StringComparer.OrdinalIgnoreCase)),
                "基礎包登記的 CSS 資源均由本地預覽路由提供");
            Check(requestedWebHosts.Count == 1 && requestedWebHosts.Single().EndsWith(".invalid", StringComparison.OrdinalIgnoreCase),
                "WebView2 僅請求本地離線預覽源，沒有外部網路來源");
            Console.WriteLine($"背景資源響應：{string.Join(" | ", backgroundResponses)}");

            footerTestWindow.Show();
            var footerEnvironment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(testRoot, "footer-browser-data"));
            await footerTestBrowser.EnsureCoreWebView2Async(footerEnvironment);
            await FooterBackdropLayoutTests.VerifyAsync(footerTestBrowser.CoreWebView2!, ayanami);
        }
        finally
        {
            if (requestAudit is not null && browser.CoreWebView2 is { } core)
            {
                core.WebResourceRequested -= requestAudit;
            }
            if (responseAudit is not null && browser.CoreWebView2 is { } responseCore)
            {
                responseCore.WebResourceResponseReceived -= responseAudit;
            }

            renderer.Dispose();
            rendererWindow.Content = null;
            rendererWindow.Close();
            footerTestWindow.Content = null;
            footerTestBrowser.Dispose();
            footerTestWindow.Close();
        }

        await VerifyUpdatesAndInstallHandoffAsync(testRoot, basePackage, screenshotDirectory);
        await VerifyInstanceManagementAsync(screenshotDirectory);
        await VerifyManagerControlsAsync(testRoot, basePackage, ayanamiBytes, nativeBytes, sayaBytes, ayanami, saya, declaredDark);
    }

    private static async Task VerifyInstanceManagementAsync(string screenshotDirectory)
    {
        var ordinary = CreateFakeInstance(7310, null, CodexInstanceKind.Normal, canConnect: false, "Codex 普通会话");
        var connectedDebug = CreateFakeInstance(7311, 9335, CodexInstanceKind.Debug, canConnect: true, "Codex 调试 A");
        var otherDebug = CreateFakeInstance(7312, 9336, CodexInstanceKind.Debug, canConnect: true, "Codex 调试 B");
        var unavailableDebug = CreateFakeInstance(7313, 9444, CodexInstanceKind.Debug, canConnect: false, "Codex 调试端口未就绪");
        var fake = new FakeCodexInstanceService([ordinary, connectedDebug, otherDebug, unavailableDebug]);
        var confirmClose = false;
        var forceConfirmationCount = 0;
        fake.CloseResults.Enqueue(CodexCloseResult.StillRunning);
        fake.CloseResults.Enqueue(CodexCloseResult.Closed);

        var window = CreateInstancesTestWindow(fake, connectedDebug.Identity, _ => confirmClose, _ =>
        {
            forceConfirmationCount++;
            return true;
        });
        window.Show();
        try
        {
            await WaitUntilAsync(() => Task.FromResult(window.IsLoaded && window.Opacity >= 0.999),
                "实例管理窗口淡入完成后再捕获客户区");
            await WaitUntilAsync(() => Task.FromResult(window.Instances.Count == 4), "fake 实例服务显示普通和调试 Codex 主实例");
            var list = FindNamed<ListView>(window, "InstanceList");
            var ordinaryRow = FindInstanceRow(list, ordinary.Identity);
            var connectedRow = FindInstanceRow(list, connectedDebug.Identity);
            var otherDebugRow = FindInstanceRow(list, otherDebug.Identity);
            var unavailableRow = FindInstanceRow(list, unavailableDebug.Identity);
            Check(RowProperty(connectedRow, "ConnectionText") == "当前连接" &&
                  RowProperty(otherDebugRow, "ConnectionText") == "—",
                "实例列表仅按RuntimeStatus中的身份标记当前连接，不按端口或本地选择推断");

            list.SelectedItem = ordinaryRow;
            Check(!FindNamed<Button>(window, "UseInstanceButton").IsEnabled &&
                  FindNamed<Button>(window, "CloseInstanceButton").IsEnabled,
                "普通实例可以定位或关闭，但不能作为皮肤连接目标");
            list.SelectedItem = unavailableRow;
            Check(RowProperty(unavailableRow, "KindText") == "调试版" &&
                  !FindNamed<Button>(window, "UseInstanceButton").IsEnabled,
                "调试实例即使监听器尚未就绪仍显示调试类型并禁用连接");

            list.SelectedItem = otherDebugRow;
            FindNamed<Button>(window, "LocateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(fake.LocateCalls.Count == 1), "定位请求到达fake实例服务");
            Check(fake.LocateCalls.Single().Identity == otherDebug.Identity,
                "定位操作只使用当前选中实例快照");

            list.SelectedItem = connectedRow;
            FindNamed<Button>(window, "CloseInstanceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(FindNamed<TextBlock>(window, "ActionStatus").Text.Contains("取消", StringComparison.Ordinal)),
                "取消实例关闭确认");
            Check(fake.CloseCalls.Count == 0, "取消关闭确认时不调用进程服务");

            confirmClose = true;
            FindNamed<Button>(window, "CloseInstanceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(fake.CloseCalls.Count == 1 &&
                FindNamed<Button>(window, "ForceCloseButton").Visibility == Visibility.Visible),
                "普通关闭仍运行时单独显示强制结束选项");
            Check(fake.CloseCalls[0].Instance.Identity == connectedDebug.Identity && !fake.CloseCalls[0].Force,
                "普通关闭只请求关闭所选身份且force=false");
            await SaveWindowScreenshotAsync(window, Path.Combine(screenshotDirectory, "实例管理-最小窗口-760x460.png"));

            FindNamed<Button>(window, "ForceCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(fake.CloseCalls.Count == 2 &&
                window.LastClosedIdentity == connectedDebug.Identity), "二次确认后fake强制结束所选实例");
            Check(fake.CloseCalls[1].Instance.Identity == connectedDebug.Identity && fake.CloseCalls[1].Force &&
                  forceConfirmationCount == 1,
                "强制结束必须由单独二次确认触发，并继续使用原选中身份");
        }
        finally
        {
            window.Close();
        }

        var switchFake = new FakeCodexInstanceService([connectedDebug, otherDebug, ordinary]);
        switchFake.CloseResults.Enqueue(CodexCloseResult.StillRunning);
        switchFake.CloseResults.Enqueue(CodexCloseResult.StillRunning);
        switchFake.CloseResults.Enqueue(CodexCloseResult.Closed);
        var switchWindow = CreateInstancesTestWindow(switchFake, connectedDebug.Identity, _ => true, _ => true);
        switchWindow.Show();
        try
        {
            await WaitUntilAsync(() => Task.FromResult(switchWindow.Instances.Count == 3), "切换选择测试装载fake实例");
            var list = FindNamed<ListView>(switchWindow, "InstanceList");
            list.SelectedItem = FindInstanceRow(list, connectedDebug.Identity);
            FindNamed<Button>(switchWindow, "CloseInstanceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(switchFake.CloseCalls.Count == 1 &&
                FindNamed<Button>(switchWindow, "ForceCloseButton").Visibility == Visibility.Visible),
                "切换选择前出现force资格");
            list.SelectedItem = FindInstanceRow(list, otherDebug.Identity);
            Check(FindNamed<Button>(switchWindow, "ForceCloseButton").Visibility == Visibility.Collapsed,
                "切换所选实例立即清除force资格");
            FindNamed<Button>(switchWindow, "ForceCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(switchFake.CloseCalls.Count == 1, "切换目标后不能对旧实例或新实例沿用force资格");

            FindNamed<Button>(switchWindow, "CloseInstanceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(switchFake.CloseCalls.Count == 2 &&
                FindNamed<Button>(switchWindow, "ForceCloseButton").Visibility == Visibility.Visible),
                "新选中目标重新经过普通关闭确认和结果检查");
            FindNamed<Button>(switchWindow, "ForceCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(switchFake.CloseCalls.Count == 3 &&
                switchWindow.LastClosedIdentity == otherDebug.Identity), "新目标重新单独确认后才可force");
            Check(switchFake.CloseCalls[1].Instance.Identity == otherDebug.Identity && !switchFake.CloseCalls[1].Force &&
                  switchFake.CloseCalls[2].Instance.Identity == otherDebug.Identity && switchFake.CloseCalls[2].Force,
                "force调用始终绑定其对应的正常关闭目标");
        }
        finally
        {
            switchWindow.Close();
        }

        var refreshFake = new FakeCodexInstanceService([connectedDebug, otherDebug]);
        refreshFake.CloseResults.Enqueue(CodexCloseResult.StillRunning);
        var refreshWindow = CreateInstancesTestWindow(refreshFake, connectedDebug.Identity, _ => true, _ => true);
        refreshWindow.Show();
        try
        {
            await WaitUntilAsync(() => Task.FromResult(refreshWindow.Instances.Count == 2), "刷新资格测试装载fake实例");
            var list = FindNamed<ListView>(refreshWindow, "InstanceList");
            list.SelectedItem = FindInstanceRow(list, connectedDebug.Identity);
            FindNamed<Button>(refreshWindow, "CloseInstanceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(refreshFake.CloseCalls.Count == 1 &&
                FindNamed<Button>(refreshWindow, "ForceCloseButton").Visibility == Visibility.Visible),
                "刷新前出现force资格");
            FindNamed<Button>(refreshWindow, "RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(FindNamed<Button>(refreshWindow, "ForceCloseButton").Visibility == Visibility.Collapsed),
                "刷新实例列表清除force资格");
            FindNamed<Button>(refreshWindow, "ForceCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(refreshFake.CloseCalls.Count == 1, "刷新后不得复用旧force许可");
        }
        finally
        {
            refreshWindow.Close();
        }

        var noWindowFake = new FakeCodexInstanceService([ordinary]);
        noWindowFake.CloseResults.Enqueue(CodexCloseResult.NativeQuitUnavailable);
        var noWindow = CreateInstancesTestWindow(noWindowFake, null, _ => true, _ => true);
        noWindow.Show();
        try
        {
            await WaitUntilAsync(() => Task.FromResult(noWindow.Instances.Count == 1), "无窗口关闭测试装载fake实例");
            FindNamed<ListView>(noWindow, "InstanceList").SelectedItem = FindInstanceRow(
                FindNamed<ListView>(noWindow, "InstanceList"), ordinary.Identity);
            FindNamed<Button>(noWindow, "CloseInstanceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(FindNamed<TextBlock>(noWindow, "ActionStatus").Text.Contains("原生 Quit", StringComparison.Ordinal)),
                "原生 Quit 不可用时保留退出结果");
            Check(noWindowFake.CloseCalls.Count == 1 && !noWindowFake.CloseCalls[0].Force &&
                  FindNamed<Button>(noWindow, "ForceCloseButton").Visibility == Visibility.Visible,
                "原生 Quit 不可用时不自动强制结束，并提供单独确认入口");
        }
        finally
        {
            noWindow.Close();
        }

        var staleSnapshot = CreateFakeInstance(7314, 9555, CodexInstanceKind.Debug, canConnect: true, "Codex 即将退出");
        var restartedIdentity = staleSnapshot.Identity with { StartedAtUtc = staleSnapshot.Identity.StartedAtUtc.AddMinutes(1) };
        var restartedInstance = staleSnapshot with { Identity = restartedIdentity, WindowTitle = "Codex 重启后实例" };
        var staleFake = new FakeCodexInstanceService([staleSnapshot])
        {
            InstancesAfterNextCloseFailure = [restartedInstance],
            NextCloseException = new InvalidOperationException("所选 Codex 实例已退出或身份已变化。请刷新实例列表后重试。")
        };
        var staleWindow = CreateInstancesTestWindow(staleFake, null, _ => true, _ => true);
        staleWindow.Show();
        try
        {
            await WaitUntilAsync(() => Task.FromResult(staleWindow.Instances.Count == 1), "陈旧身份测试装载旧fake快照");
            var list = FindNamed<ListView>(staleWindow, "InstanceList");
            list.SelectedItem = FindInstanceRow(list, staleSnapshot.Identity);
            FindNamed<Button>(staleWindow, "CloseInstanceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(
                FindNamed<TextBlock>(staleWindow, "ActionStatus").Text.Contains("身份已变化", StringComparison.Ordinal) &&
                staleWindow.Instances.Single().Identity == restartedIdentity),
                "陈旧PID身份错误清除force资格并刷新到新进程身份");
            Check(staleFake.CloseCalls.Count == 1 && !staleFake.CloseCalls[0].Force &&
                  FindNamed<Button>(staleWindow, "ForceCloseButton").Visibility == Visibility.Collapsed &&
                  list.SelectedItem is null,
                "身份变化后不复用旧选中快照或force许可");
        }
        finally
        {
            staleWindow.Close();
        }
    }

    private static InstancesWindow CreateInstancesTestWindow(
        ICodexInstanceService service,
        CodexProcessIdentity? connectedIdentity,
        Func<CodexInstance, bool> confirmClose,
        Func<CodexInstance, bool> confirmForceClose) => new(service, connectedIdentity, confirmClose, confirmForceClose)
    {
        Width = 760,
        Height = 460,
        Left = -2_000,
        Top = -2_000,
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.SingleBorderWindow,
        ResizeMode = ResizeMode.CanResizeWithGrip
    };

    private static object FindInstanceRow(ListView list, CodexProcessIdentity identity) =>
        list.Items.Cast<object>().Single(row =>
            row.GetType().GetProperty("Instance", BindingFlags.Instance | BindingFlags.Public)?.GetValue(row) is CodexInstance instance &&
            instance.Identity == identity);

    private static string RowProperty(object row, string propertyName) =>
        row.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(row)?.ToString() ?? string.Empty;

    private static async Task VerifyRuntimeBrowserPortalAsync(
        ThemePreviewRenderer renderer,
        WebView2 browser,
        SkinPackage ayanami,
        SkinPackage updatePackage)
    {
        await RenderBaseAsync(renderer, new PreviewOptions("home", "light"));
        await WaitForPageAsync(renderer, "base", "home");
        await InstallBrowserPortalFixtureAsync(renderer);

        var portalBefore = await ReadObjectAsync(renderer, BrowserPortalProbeScript);
        Check(portalBefore["portalIsBodySibling"] == "true", "离线浏览器 portal 与 Codex shell 是 body 同级节点");
        Check(portalBefore["portalZIndex"] == "auto" && portalBefore["portalPointerEvents"] == "auto" &&
              portalBefore["portalVisibility"] == "visible",
            "离线浏览器 portal 初始保留原生 auto 层级及可交互状态");

        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(ayanami));
        await WaitUntilAsync(async () =>
            await renderer.ExecuteScriptAsync("window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'light:home'") == "true",
            "live Runtime 应用 Ayanami 首页皮肤");
        var applied = await ReadObjectAsync(renderer, BrowserPortalProbeScript);
        Check(applied["portalSnapshot"] == portalBefore["portalSnapshot"], "live Runtime 未改写浏览器 portal 的 position、z-index、clip 或 pointer-events");
        Check(applied["runtimeImagePresent"] == "true", "live Runtime 仍创建 Ayanami 背景图层");

        const string legacyStackingRule = "@layer skin-manager-native { [data-app-shell-frame] { position:relative !important; z-index:1 !important; } }";
        var legacyStyleScript = "(() => { const style = document.createElement('style'); style.id = 'skin-manager-legacy-shell-stack'; style.textContent = " +
            JsonSerializer.Serialize(legacyStackingRule) + "; document.head.appendChild(style); return true; })()";
        await renderer.ExecuteScriptAsync(legacyStyleScript);

        var legacy = await ReadObjectAsync(renderer, BrowserPortalProbeScript);
        var legacyPixel = await CapturePixelAtCssPointAsync(renderer, browser, 700, 400);
        Check(legacy["shellZIndex"] == "1", "旧 2.1.0 层级规则把 shell 提升到 z-index 1");
        Check(legacy["browserHit"] == "false", "旧 2.1.0 层级规则使浏览器 portal 的页面命中被 shell 截获");
        Check(legacyPixel != "45,115,172,255", "旧 2.1.0 层级规则下截图采样显示 shell 内容而非假网页底色");
        Console.WriteLine($"浏览器 portal 层级回归：旧规则 hit={legacy["browserHit"]}, z={legacy["shellZIndex"]}, 像素={legacyPixel}");

        await renderer.ExecuteScriptAsync("document.getElementById('skin-manager-legacy-shell-stack')?.remove(); true;");
        await WaitUntilAsync(async () =>
            (await ReadObjectAsync(renderer, BrowserPortalProbeScript))["browserHit"] == "true",
            "2.1.1 原生 shell 层级下命中浏览器假网页");
        var fixedProbe = await ReadObjectAsync(renderer, BrowserPortalProbeScript);
        var fixedPixel = await CapturePixelAtCssPointAsync(renderer, browser, 700, 400);
        Check(fixedProbe["shellPosition"] == portalBefore["shellPosition"] && fixedProbe["shellZIndex"] == portalBefore["shellZIndex"],
            "2.1.1 保留 Codex 原生 shell position 与 z-index");
        Check(fixedProbe["portalSnapshot"] == portalBefore["portalSnapshot"], "2.1.1 不改写原生 browser portal 层级和裁切属性");
        Check(fixedPixel == "45,115,172,255", "2.1.1 截图显示浏览器假网页的蓝色底层");
        Console.WriteLine($"浏览器 portal 层级回归：2.1.1 hit={fixedProbe["browserHit"]}, z={fixedProbe["shellZIndex"]}, 像素={fixedPixel}");

        var inputBounds = await ReadObjectAsync(renderer, """
            (() => {
              const rect = document.getElementById('offline-github-login').getBoundingClientRect();
              return { x: String(rect.x + rect.width / 2), y: String(rect.y + rect.height / 2) };
            })()
            """);
        var inputX = double.Parse(inputBounds["x"], System.Globalization.CultureInfo.InvariantCulture);
        var inputY = double.Parse(inputBounds["y"], System.Globalization.CultureInfo.InvariantCulture);
        await browser.CoreWebView2!.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new { type = "mousePressed", x = inputX, y = inputY, button = "left", clickCount = 1 }));
        await browser.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new { type = "mouseReleased", x = inputX, y = inputY, button = "left", clickCount = 1 }));
        Check(await renderer.ExecuteScriptAsync("document.activeElement?.id === 'offline-github-login'") == "true",
            "浏览器 portal 可命中并聚焦本地假登录输入框");

        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(updatePackage));
        var updatedColor = CssColorToBrowserValue("#f7f3ee");
        await WaitUntilAsync(async () =>
            (await ReadObjectAsync(renderer, BrowserPortalProbeScript))["runtimeBackgroundColor"] == updatedColor,
            "live Runtime 热更新皮肤背景");
        var hotUpdated = await ReadObjectAsync(renderer, BrowserPortalProbeScript);
        Check(hotUpdated["browserHit"] == "true" && hotUpdated["portalSnapshot"] == portalBefore["portalSnapshot"],
            "皮肤热更新后浏览器 portal 仍可见且原生样式未变");

        await renderer.ExecuteScriptAsync("document.getElementById('offline-embedded-frame-host-root')?.remove(); true;");
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "!document.getElementById('offline-embedded-frame-host-root') && !!document.querySelector('.skin-manager-runtime-background')") == "true",
            "关闭浏览器 portal 不会移除仍启用的皮肤背景");
        await InstallBrowserPortalFixtureAsync(renderer);
        var reopened = await ReadObjectAsync(renderer, BrowserPortalProbeScript);
        Check(reopened["browserHit"] == "true" && reopened["portalSnapshot"] == portalBefore["portalSnapshot"],
            "重新打开离线浏览器 portal 后可见且属性保持原样");

        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRemoveScript());
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "!window.__SKIN_MANAGER_RUNTIME__ && !document.querySelector('.skin-manager-runtime-background') && !document.getElementById('skin-manager-runtime-style')") == "true",
            "恢复原生时清理 live Runtime 背景与样式");
        var restored = await ReadObjectAsync(renderer, BrowserPortalProbeScript);
        Check(restored["browserHit"] == "true" && restored["portalSnapshot"] == portalBefore["portalSnapshot"],
            "恢复原生后假网页仍可见，原生 portal 样式未被改写");
    }

    private static async Task VerifyRuntimeAppearanceAsync(
        ThemePreviewRenderer renderer,
        WebView2 browser,
        SkinPackage saya,
        SkinPackage ayanami,
        SkinPackage systemFixture)
    {
        await RenderBaseAsync(renderer, new PreviewOptions("home", "light"));
        await WaitForPageAsync(renderer, "base", "home");
        await renderer.ExecuteScriptAsync("""
            (() => {
              const root = document.documentElement;
              root.classList.add('host-preserve-class');
              root.style.setProperty('--host-preserve-style', 'kept');
              window.__skinRuntimeLegacyCalls = { apply: 0, remove: 0 };
              window.__SKIN_MANAGER_RUNTIME__ = {
                apply() { window.__skinRuntimeLegacyCalls.apply++; },
                remove() { window.__skinRuntimeLegacyCalls.remove++; }
              };
              return true;
            })()
            """);

        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(saya));
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'dark:home' &&
            document.documentElement.getAttribute('data-theme') === 'dark' &&
            getComputedStyle(document.documentElement).colorScheme === 'dark'
            """) == "true", "浅色 host 应用 Saya 后原生 UI 与皮肤均为深色");
        var sayaDark = await ReadObjectAsync(renderer, """
            (() => {
              const root = document.documentElement;
              const style = getComputedStyle(root);
              return {
                activeKey: String(window.__SKIN_MANAGER_RUNTIME__?.activeKey || ''),
                appearanceVersion: String(window.__SKIN_MANAGER_RUNTIME__?.appearanceVersion || ''),
                theme: root.getAttribute('data-theme') || '',
                colorScheme: style.colorScheme,
                themeVariant: style.getPropertyValue('--theme-variant').trim(),
                legacyApply: String(window.__skinRuntimeLegacyCalls.apply),
                legacyRemove: String(window.__skinRuntimeLegacyCalls.remove),
                rootClass: root.className,
                preserveStyle: style.getPropertyValue('--host-preserve-style').trim()
              };
            })()
            """);
        Check(sayaDark["activeKey"] == "dark:home" && sayaDark["theme"] == "dark" &&
              sayaDark["colorScheme"] == "dark" && sayaDark["themeVariant"] == "dark",
            "Saya 的 fixed dark appearance 驱动真实离线基础 CSS 的 data-theme、color-scheme 与 dark variant");
        Check(sayaDark["appearanceVersion"] == "4" && sayaDark["legacyApply"] == "0" && sayaDark["legacyRemove"] == "1",
            "旧 runtime 仅被移除，新 version 4 runtime 负责应用皮肤");
        Check(sayaDark["rootClass"] == "host-preserve-class" && sayaDark["preserveStyle"] == "kept",
            "固定外观只修改根 data-theme，保留宿主 class 与 style");
        await PrepareNeutralCaptureAsync(renderer, browser);
        await SaveScreenshotAsync(renderer, Path.Combine(FindTestOutputDirectory(), "saya-runtime-dark.png"));

        await renderer.ExecuteScriptAsync("document.documentElement.setAttribute('data-theme', 'dark'); true;");
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.rootThemeOriginal?.present === true &&
            window.__SKIN_MANAGER_RUNTIME__?.rootThemeOriginal?.value === 'dark'
            """) == "true", "固定深色期间捕获宿主写入的相同 data-theme 值");
        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(saya));
        Check(await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'dark:home' &&
            window.__SKIN_MANAGER_RUNTIME__?.rootThemeOriginal?.value === 'dark'
            """) == "true", "重复应用皮肤保留最新宿主 appearance 基线");
        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(systemFixture));
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'dark:home' &&
            document.documentElement.getAttribute('data-theme') === 'dark' &&
            window.__SKIN_MANAGER_RUNTIME__?.rootThemeOriginal === null
            """) == "true", "从 fixed dark 切换到 system 时及时恢复最新宿主深色外观");

        await renderer.ExecuteScriptAsync("document.documentElement.setAttribute('data-theme', 'light'); true;");
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'light:home' &&
            getComputedStyle(document.documentElement).colorScheme === 'light'
            """) == "true", "system skin 跟随宿主异步切换到浅色");

        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(saya));
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'dark:home' &&
            window.__SKIN_MANAGER_RUNTIME__?.rootThemeOriginal?.value === 'light'
            """) == "true", "fixed dark 在异步宿主写入后仍维持皮肤外观并记录浅色基线");
        await renderer.ExecuteScriptAsync("document.documentElement.setAttribute('data-theme', 'light'); true;");
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync("""
            document.documentElement.getAttribute('data-theme') === 'dark' &&
            window.__SKIN_MANAGER_RUNTIME__?.rootThemeOriginal?.value === 'light'
            """) == "true", "异步宿主浅色写入被固定深色重新覆盖且保留为恢复值");
        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(systemFixture));
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync("""
            document.documentElement.getAttribute('data-theme') === 'light' &&
            window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'light:home'
            """) == "true", "fixed-to-system 恢复异步写入的浅色宿主外观");

        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRemoveScript());
        await renderer.ExecuteScriptAsync("document.documentElement.removeAttribute('data-theme'); true;");
        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(saya));
        Check(await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.rootThemeOriginal?.present === false &&
            document.documentElement.getAttribute('data-theme') === 'dark'
            """) == "true", "固定外观支持原生 data-theme 缺失的宿主");
        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRemoveScript());
        Check(await renderer.ExecuteScriptAsync("!document.documentElement.hasAttribute('data-theme')") == "true",
            "移除固定外观时恢复根 data-theme 原本缺失状态");

        await renderer.ExecuteScriptAsync("document.documentElement.setAttribute('data-theme', 'system'); true;");
        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(saya));
        Check(await renderer.ExecuteScriptAsync("document.documentElement.getAttribute('data-theme') === 'dark'") == "true",
            "固定外观能覆盖无效但原本存在的宿主 theme marker");
        var immediateDifferentWrite = await renderer.ExecuteScriptAsync("""
            (() => {
              const root = document.documentElement;
              root.setAttribute('data-theme', 'light');
              window.__SKIN_MANAGER_RUNTIME__?.remove?.();
              return root.getAttribute('data-theme') === 'light';
            })()
            """);
        Check(immediateDifferentWrite == "true", "host 在 observer 回调前写浅色并立即移除时保留新值");

        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(saya));
        var immediateSameWrite = await renderer.ExecuteScriptAsync("""
            (() => {
              const root = document.documentElement;
              root.setAttribute('data-theme', 'dark');
              window.__SKIN_MANAGER_RUNTIME__?.remove?.();
              return root.getAttribute('data-theme') === 'dark';
            })()
            """);
        Check(immediateSameWrite == "true", "host 在 fixed dark 期间写入相同值并立即移除时保留该值");

        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(ayanami));
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'light:home' &&
            document.documentElement.getAttribute('data-theme') === 'light' &&
            getComputedStyle(document.documentElement).colorScheme === 'light'
            """) == "true", "深色 host 应用 Ayanami 后原生 UI 与皮肤均为浅色");
        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRemoveScript());
        Check(await renderer.ExecuteScriptAsync("""
            document.documentElement.getAttribute('data-theme') === 'dark' &&
            document.documentElement.classList.contains('host-preserve-class') &&
            getComputedStyle(document.documentElement).getPropertyValue('--host-preserve-style').trim() === 'kept'
            """) == "true", "移除 Ayanami 后恢复深色 host 并保留 class/style");

        await renderer.ExecuteScriptAsync("""
            (() => {
              const root = document.documentElement;
              root.removeAttribute('data-theme');
              window.__skinRuntimeOriginalMatchMedia = window.matchMedia;
              window.__skinRuntimeAppearanceMedia = {
                matches: false,
                listener: null,
                addEventListener(_type, listener) { this.listener = listener; },
                removeEventListener(_type, listener) { if (this.listener === listener) this.listener = null; }
              };
              window.matchMedia = () => window.__skinRuntimeAppearanceMedia;
              return true;
            })()
            """);
        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRuntimeScript(systemFixture));
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'light:home' &&
            !document.documentElement.hasAttribute('data-theme')
            """) == "true", "system appearance 在无宿主 marker 时使用系统媒体回退");
        await renderer.ExecuteScriptAsync("""
            (() => {
              const media = window.__skinRuntimeAppearanceMedia;
              media.matches = true;
              media.listener?.({ matches: true });
              return true;
            })()
            """);
        Check(await renderer.ExecuteScriptAsync("""
            window.__SKIN_MANAGER_RUNTIME__?.activeKey === 'dark:home' &&
            !document.documentElement.hasAttribute('data-theme')
            """) == "true", "system skin 跟随 prefers-color-scheme 回退且不写根 marker");
        await renderer.ExecuteScriptAsync(SkinRuntimeCompiler.BuildRemoveScript());
        await renderer.ExecuteScriptAsync("""
            (() => {
              window.matchMedia = window.__skinRuntimeOriginalMatchMedia;
              delete window.__skinRuntimeOriginalMatchMedia;
              return true;
            })()
            """);
        var preservedHostAttributes = await ReadObjectAsync(renderer, """
            (() => ({
              className: document.documentElement.className,
              customStyle: getComputedStyle(document.documentElement).getPropertyValue('--host-preserve-style').trim(),
              hasTheme: String(document.documentElement.hasAttribute('data-theme'))
            }))()
            """);
        Check(preservedHostAttributes["className"] == "host-preserve-class" &&
              preservedHostAttributes["customStyle"] == "kept" && preservedHostAttributes["hasTheme"] == "false",
            "完整移除后保留宿主 class、style 和缺失的 data-theme");
    }

    private static async Task VerifyChatAssistantInkAsync(
        ThemePreviewRenderer renderer,
        SkinPackage ayanami,
        SkinPackage saya,
        SkinPackage native,
        SkinPackage stateFixture,
        SkinPackage assistantColorFixture,
        string screenshotDirectory)
    {
        await RenderBaseAsync(renderer, new PreviewOptions("conversation", "dark"));
        await WaitForPageAsync(renderer, "base", "conversation");
        var installFixture = """
            (() => {
              const style = document.createElement('style');
              style.id = 'chat-ink-offline-fixture-style';
              style.textContent = __FIXTURE_CSS__;
              document.head.appendChild(style);
              document.body.innerHTML = __FIXTURE_HTML__;
              document.documentElement.setAttribute('data-theme', 'dark');
              return true;
            })()
            """.Replace("__FIXTURE_CSS__", JsonSerializer.Serialize(ChatInkFixtureCss), StringComparison.Ordinal)
            .Replace("__FIXTURE_HTML__", JsonSerializer.Serialize(ChatInkFixtureHtml), StringComparison.Ordinal);
        await renderer.ExecuteScriptAsync(installFixture);

        var sayaDark = SkinResolver.Resolve(saya, SkinAppearance.Dark, SkinScene.Conversation);
        var sayaPrimary = CssColorToBrowserValue(sayaDark.Colors["color.text.primary"]);
        var sayaSecondary = CssColorToBrowserValue(sayaDark.Colors["color.text.secondary"]);

        // Freeze the 2.3.4 root-only aliases: they update the root variables, but leave
        // the locally redefined TextBase values inside the Chat fixture untouched.
        var legacyRules = $$"""
            @layer skin-manager-native {
              :root[data-theme] {
                --app-color-text-foreground:{{sayaDark.Colors["color.text.primary"]}} !important;
                --color-text-primary:{{sayaDark.Colors["color.text.primary"]}} !important;
                --color-text:{{sayaDark.Colors["color.text.primary"]}} !important;
                --text-primary:{{sayaDark.Colors["color.text.primary"]}} !important;
                --app-color-text-foreground-secondary:{{sayaDark.Colors["color.text.secondary"]}} !important;
                --app-color-text-secondary:{{sayaDark.Colors["color.text.secondary"]}} !important;
                --color-text-secondary:{{sayaDark.Colors["color.text.secondary"]}} !important;
                --text-secondary:{{sayaDark.Colors["color.text.secondary"]}} !important;
              }
            }
            """;
        var legacyStyleScript = """
            (() => {
              const style = document.createElement('style');
              style.id = 'skin-manager-native-style';
              style.textContent = __LEGACY_CSS__;
              document.head.appendChild(style);
              return true;
            })()
            """.Replace("__LEGACY_CSS__", JsonSerializer.Serialize(legacyRules), StringComparison.Ordinal);
        await renderer.ExecuteScriptAsync(legacyStyleScript);

        var oldProbe = await ReadObjectAsync(renderer, ChatAssistantInkProbeScript);
        Check(oldProbe["rootPrimary"] == sayaDark.Colors["color.text.primary"] && oldProbe["paragraph"] == "rgb(32, 26, 24)" &&
              oldProbe["heading"] == "rgb(32, 26, 24)" && oldProbe["listBody"] == "rgb(32, 26, 24)",
            "冻结的 2.3.4 root-only 颜色别名保留根色，但 Chat 正文、标题和列表仍复现局部黑字");
        Check(oldProbe["caption"] != sayaSecondary,
            "旧 root-only 颜色别名未覆盖 Chat TextBase 的 Caption 局部变量");

        await renderer.ExecuteScriptAsync("document.getElementById('chat-ink-step').textContent = '2.3.4 root-only aliases：正文仍为 #201a18'; true;");
        var oldScreenshot = await CapturePreviewBytesAsync(renderer);

        await renderer.ExecuteScriptAsync(NativeSkinStyleBuilder.BuildApplyScript(BuildChatInkPayload(saya, SkinAppearance.Dark)));
        var sayaProbe = await ReadObjectAsync(renderer, ChatAssistantInkProbeScript);
        Check(sayaProbe["paragraph"] == sayaPrimary && sayaProbe["heading"] == sayaPrimary &&
              sayaProbe["listBody"] == sayaPrimary,
            "Saya 深色的正文、标题与列表文本跟随 Chat 局部 TextBase 变量桥接");
        Check(sayaProbe["caption"] == sayaSecondary,
            "Saya 深色 secondary token 更新 Chat Caption 文本");
        Check(sayaProbe["markdownLink"] == "rgb(24, 120, 212)" &&
              sayaProbe["codeToken"] == "rgb(203, 85, 20)" &&
              sayaProbe["userText"] == "rgb(32, 26, 24)",
            "Chat 文字桥接保留显式 Markdown 链接色、代码语法色和同级用户消息局部色");
        Check(sayaProbe["localScopeHasTheme"] == "false",
            "Chat 离线结构的 TextBase 局部作用域不依赖 data-theme 属性");

        await renderer.ExecuteScriptAsync("document.getElementById('chat-ink-step').textContent = '2.3.5：助手回复正文跟随 Saya 浅色字'; true;");
        var updatedScreenshot = await CapturePreviewBytesAsync(renderer);
        Directory.CreateDirectory(screenshotDirectory);
        await SaveChatInkComparisonAsync(
            oldScreenshot,
            updatedScreenshot,
            Path.Combine(screenshotDirectory, "chat-ink-fix-comparison.png"));

        var streamedProbe = await ReadObjectAsync(renderer, """
            (() => {
              const paragraph = document.createElement('p');
              paragraph.id = 'chat-streamed-text';
              paragraph.className = '_TextBase_1ccqq_1 _Text_18604_1 streamed';
              paragraph.textContent = 'Streamed response text';
              document.getElementById('chat-local-scope').appendChild(paragraph);
              return { color: getComputedStyle(paragraph).color };
            })()
            """);
        Check(streamedProbe["color"] == sayaPrimary,
            "Chat 文字桥接覆盖皮肤应用后流式追加的 TextBase 节点");

        var ayanamiLight = SkinResolver.Resolve(ayanami, SkinAppearance.Light, SkinScene.Conversation);
        var ayanamiPrimary = CssColorToBrowserValue(ayanamiLight.Colors["color.text.primary"]);
        var ayanamiSecondary = CssColorToBrowserValue(ayanamiLight.Colors["color.text.secondary"]);
        await renderer.ExecuteScriptAsync("document.documentElement.setAttribute('data-theme', 'light'); true;");
        await renderer.ExecuteScriptAsync(NativeSkinStyleBuilder.BuildApplyScript(BuildChatInkPayload(ayanami, SkinAppearance.Light)));
        var ayanamiProbe = await ReadObjectAsync(renderer, ChatAssistantInkProbeScript);
        Check(ayanamiProbe["paragraph"] == ayanamiPrimary && ayanamiProbe["caption"] == ayanamiSecondary,
            "热更新到 Ayanami 浅色后 Chat 正文与 secondary Caption 同步更新");

        await renderer.ExecuteScriptAsync("document.documentElement.setAttribute('data-theme', 'dark'); true;");
        await renderer.ExecuteScriptAsync(NativeSkinStyleBuilder.BuildApplyScript(BuildChatInkPayload(saya, SkinAppearance.Dark)));
        var sayaReapplied = await ReadObjectAsync(renderer, ChatAssistantInkProbeScript);
        Check(sayaReapplied["paragraph"] == sayaPrimary && sayaReapplied["caption"] == sayaSecondary,
            "再次应用 Saya 后 Chat 正文颜色恢复为新值");

        await renderer.ExecuteScriptAsync("document.documentElement.setAttribute('data-theme', 'light'); true;");
        await renderer.ExecuteScriptAsync(NativeSkinStyleBuilder.BuildApplyScript(BuildChatInkPayload(stateFixture, SkinAppearance.Light)));
        var stateLinkProbe = await ReadObjectAsync(renderer, ChatAssistantInkProbeScript);
        Check(stateLinkProbe["markdownLink"] == "rgb(24, 120, 212)" &&
              stateLinkProbe["codeToken"] == "rgb(203, 85, 20)",
            "普通皮肤的显式链接色和高亮代码色不受正文 primary 别名影响");

        await renderer.ExecuteScriptAsync(NativeSkinStyleBuilder.BuildApplyScript(BuildChatInkPayload(assistantColorFixture, SkinAppearance.Light)));
        var assistantColorProbe = await ReadObjectAsync(renderer, ChatAssistantInkProbeScript);
        Check(assistantColorProbe["paragraph"] == CssColorToBrowserValue("#8d2b9d") &&
              assistantColorProbe["userText"] == "rgb(32, 26, 24)",
            "message.assistant 显式 color 优先于全局 primary，且不影响用户消息颜色");

        await renderer.ExecuteScriptAsync("document.documentElement.setAttribute('data-theme', 'dark'); true;");
        await renderer.ExecuteScriptAsync(NativeSkinStyleBuilder.BuildApplyScript(BuildChatInkPayload(native, SkinAppearance.Dark)));
        var nativeProbe = await ReadObjectAsync(renderer, ChatAssistantInkProbeScript);
        Check(nativeProbe["paragraph"] == "rgb(32, 26, 24)",
            "应用空 Native 配置后 Chat 正文恢复原生局部颜色");
        await renderer.ExecuteScriptAsync(NativeSkinStyleBuilder.BuildApplyScript(BuildChatInkPayload(saya, SkinAppearance.Dark)));
        var activeBeforeRemoval = await ReadObjectAsync(renderer, ChatAssistantInkProbeScript);
        Check(activeBeforeRemoval["paragraph"] == sayaPrimary,
            "移除样式前确认活动 Saya 规则已将 Chat 正文改为浅色");
        await renderer.ExecuteScriptAsync("document.getElementById('skin-manager-native-style')?.remove(); true;");
        var removedProbe = await ReadObjectAsync(renderer, ChatAssistantInkProbeScript);
        Check(removedProbe["paragraph"] == "rgb(32, 26, 24)",
            "移除 Native 样式节点后 Chat 正文恢复原生局部颜色");
    }

    private static NativeSkinStylePayload BuildChatInkPayload(SkinPackage package, SkinAppearance appearance)
    {
        var payload = NativeSkinStyleBuilder.Build(
            SkinResolver.Resolve(package, appearance, SkinScene.Conversation),
            imageUrl: null);
        return payload with
        {
            FillOpacity = new Dictionary<string, NativeFillOpacity>(),
            Background = null
        };
    }

    private const string ChatAssistantInkProbeScript = """
        (() => {
          const color = (id) => {
            const element = document.getElementById(id);
            return element ? getComputedStyle(element).color : '';
          };
          const localScope = document.getElementById('chat-local-scope');
          return {
            rootPrimary: getComputedStyle(document.documentElement).getPropertyValue('--app-color-text-foreground').trim(),
            paragraph: color('chat-paragraph'),
            heading: color('chat-heading'),
            listBody: color('chat-list-body'),
            caption: color('chat-secondary'),
            markdownLink: color('chat-link-text'),
            codeToken: color('chat-code-token'),
            userText: color('chat-user-text'),
            localScopeHasTheme: String(localScope?.hasAttribute('data-theme') ?? false)
          };
        })()
        """;

    private static async Task<byte[]> CapturePreviewBytesAsync(ThemePreviewRenderer renderer)
    {
        await using var stream = new MemoryStream();
        await renderer.CapturePreviewAsync(stream);
        return stream.ToArray();
    }

    private static async Task SaveChatInkComparisonAsync(byte[] beforeBytes, byte[] afterBytes, string path)
    {
        using var beforeStream = new MemoryStream(beforeBytes);
        using var afterStream = new MemoryStream(afterBytes);
        var before = BitmapFrame.Create(beforeStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var after = BitmapFrame.Create(afterStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        before.Freeze();
        after.Freeze();
        Check(before.PixelWidth == after.PixelWidth && before.PixelHeight == after.PixelHeight,
            "Chat 修复前后离线截图尺寸一致");

        var composition = new DrawingVisual();
        using (var drawing = composition.RenderOpen())
        {
            drawing.DrawImage(before, new Rect(0, 0, before.PixelWidth, before.PixelHeight));
            drawing.DrawImage(after, new Rect(before.PixelWidth, 0, after.PixelWidth, after.PixelHeight));
        }

        var image = new RenderTargetBitmap(
            before.PixelWidth + after.PixelWidth,
            Math.Max(before.PixelHeight, after.PixelHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        image.Render(composition);
        image.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        await output.FlushAsync();
        Check(output.Length > 10_000, "輸出 Chat 黑字修复前后离线对照图");
    }

    private static async Task InstallBrowserPortalFixtureAsync(ThemePreviewRenderer renderer)
    {
        await renderer.ExecuteScriptAsync("""
            (() => {
              document.getElementById('offline-embedded-frame-host-root')?.remove();
              const host = document.createElement('div');
              host.id = 'offline-embedded-frame-host-root';
              host.dataset.embeddedFrameHostRoot = 'true';
              host.className = 'pointer-events-none fixed inset-0 overflow-visible';
              host.style.cssText = 'position:fixed;inset:0;pointer-events:none;overflow:visible;';

              const browserRoot = document.createElement('div');
              browserRoot.dataset.browserHostRoot = 'true';
              browserRoot.className = 'pointer-events-none fixed inset-0 [contain:paint]';
              browserRoot.style.cssText = 'position:fixed;inset:0;contain:paint;pointer-events:none;';

              const container = document.createElement('div');
              container.dataset.browserSidebarWebview = 'true';
              container.style.cssText = 'position:fixed;inset:0;pointer-events:auto;visibility:visible;z-index:auto;clip:auto;clip-path:none;overflow:hidden;';

              const webview = document.createElement('webview');
              webview.dataset.browserSidebarConversationId = 'offline-fixture';
              webview.dataset.browserSidebarBrowserTabId = 'offline-fixture';
              webview.style.cssText = 'display:block;position:fixed;inset:0;width:100%;height:100%;pointer-events:auto;visibility:visible;z-index:auto;clip:auto;clip-path:none;border:0;';

              const page = document.createElement('div');
              page.id = 'offline-github-page';
              page.style.cssText = 'position:absolute;inset:0;background:#2d73ac;color:#fff;font:16px sans-serif;';
              const card = document.createElement('div');
              card.style.cssText = 'position:absolute;left:70px;top:55px;width:360px;height:240px;box-sizing:border-box;padding:24px;background:#fff;color:#24292f;border:1px solid #d0d7de;border-radius:8px;';
              const title = document.createElement('div');
              title.textContent = 'GitHub sign in (offline fixture)';
              title.style.cssText = 'font-weight:600;margin-bottom:22px;';
              const input = document.createElement('input');
              input.id = 'offline-github-login';
              input.type = 'text';
              input.autocomplete = 'off';
              input.setAttribute('aria-label', 'Offline test username');
              input.style.cssText = 'width:270px;height:36px;box-sizing:border-box;border:1px solid #8c959f;border-radius:6px;padding:6px 8px;';
              card.append(title, input);
              page.appendChild(card);
              webview.appendChild(page);
              container.appendChild(webview);
              browserRoot.appendChild(container);
              host.appendChild(browserRoot);
              document.body.appendChild(host);
              return host.parentElement === document.querySelector('[data-app-shell-frame]')?.parentElement;
            })()
            """);
    }

    private const string BrowserPortalProbeScript = """
        (() => {
          const shell = document.querySelector('[data-app-shell-frame]');
          const host = document.getElementById('offline-embedded-frame-host-root');
          const browserRoot = host?.querySelector('[data-browser-host-root]');
          const container = host?.querySelector('[data-browser-sidebar-webview]');
          const webview = host?.querySelector('[data-browser-sidebar-conversation-id]');
          const hit = document.elementFromPoint(700, 400);
          const image = document.querySelector('.skin-manager-image');
          const background = document.querySelector('.skin-manager-runtime-background');
          const portalSnapshot = [host, browserRoot, container, webview].map((element) => {
            if (!element) return 'missing';
            const style = getComputedStyle(element);
            return [style.position, style.zIndex, style.clip, style.clipPath, style.pointerEvents,
              style.visibility, style.overflow, style.contain, style.inset, style.opacity].join('|');
          }).join(';');
          return {
            portalIsBodySibling: String(Boolean(host && shell && host.parentElement === shell.parentElement)),
            portalSnapshot,
            portalZIndex: container ? getComputedStyle(container).zIndex : '',
            portalPointerEvents: container ? getComputedStyle(container).pointerEvents : '',
            portalVisibility: container ? getComputedStyle(container).visibility : '',
            browserHit: String(Boolean(hit?.closest('[data-browser-sidebar-webview]'))),
            shellPosition: shell ? getComputedStyle(shell).position : '',
            shellZIndex: shell ? getComputedStyle(shell).zIndex : '',
            runtimeImagePresent: String(Boolean(image && getComputedStyle(image).backgroundImage !== 'none')),
            runtimeBackgroundColor: background ? getComputedStyle(background).backgroundColor : ''
          };
        })()
        """;

    private static async Task<string> CapturePixelAtCssPointAsync(
        ThemePreviewRenderer renderer,
        WebView2 browser,
        double cssX,
        double cssY)
    {
        await using var stream = new MemoryStream();
        await renderer.CapturePreviewAsync(stream);
        stream.Position = 0;
        var source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var viewport = await ReadObjectAsync(renderer, "({ width: String(innerWidth), height: String(innerHeight) })");
        var viewportWidth = double.Parse(viewport["width"], System.Globalization.CultureInfo.InvariantCulture);
        var viewportHeight = double.Parse(viewport["height"], System.Globalization.CultureInfo.InvariantCulture);
        var pixelX = Math.Clamp((int)Math.Floor(cssX * converted.PixelWidth / viewportWidth), 0, converted.PixelWidth - 1);
        var pixelY = Math.Clamp((int)Math.Floor(cssY * converted.PixelHeight / viewportHeight), 0, converted.PixelHeight - 1);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return PixelToString(pixels, (pixelY * converted.PixelWidth + pixelX) * 4);
    }

    private static async Task VerifyUpdatesAndInstallHandoffAsync(
        string testRoot,
        BasePackage basePackage,
        string screenshotDirectory)
    {
        var updatesDirectory = Path.Combine(testRoot, "updates-defaults");
        var updateClient = new FakeUpdateClient(Path.Combine(testRoot, "fake-downloads"));
        using var coordinator = new UpdateCoordinator(updateClient, updatesDirectory, new Version(2, 2, 0));
        Check(coordinator.AutomaticCheckAndDownload, "自动检查和下载默认开启");
        coordinator.StartAutomatically();
        await WaitUntilAsync(() => Task.FromResult(coordinator.HasDownloadedUpdate), "启动时自动检查并下载可用更新");
        Check(updateClient.CheckCount == 1 && updateClient.DownloadCount == 1 &&
              coordinator.StatusMessage.Contains("可以安装", StringComparison.Ordinal),
            "启动自动更新完成后仅就绪提示，不弹窗打断");

        var updateWindow = new UpdatesWindow(coordinator) { Width = 500, Height = 520 };
        updateWindow.Show();
        await WaitUntilAsync(() => Task.FromResult(updateWindow.IsLoaded && updateWindow.Opacity >= 0.999),
            "更新窗口淡入完成后再捕获客户区");
        await updateWindow.Dispatcher.InvokeAsync(() => updateWindow.UpdateLayout());
        await Task.Delay(80);
        var releaseNotes = FindNamed<TextBox>(updateWindow, "ReleaseNotesText");
        releaseNotes.ApplyTemplate();
        releaseNotes.UpdateLayout();
        var notesScroll = FindVisualChildren<ScrollViewer>(releaseNotes).FirstOrDefault();
        var installButton = FindNamed<Button>(updateWindow, "InstallButton");
        Check(FindNamed<CheckBox>(updateWindow, "AutomaticCheckBox").IsChecked == true &&
              installButton.Visibility == Visibility.Visible && installButton.IsVisible && installButton.IsEnabled &&
              releaseNotes.Text == updateClient.Release.Notes && notesScroll?.ScrollableHeight > 0 &&
              FindNamed<TextBlock>(updateWindow, "ReleaseVersionText").Text == "可用版本：2.3.0",
            "500×520 更新窗限制长发布说明滚动，安装按钮可见可用且版本只显示一次");
        await SaveWindowScreenshotAsync(updateWindow, Path.Combine(screenshotDirectory, "版本与更新窗口.png"));
        FindNamed<CheckBox>(updateWindow, "AutomaticCheckBox").IsChecked = false;
        Check(!coordinator.AutomaticCheckAndDownload, "更新窗口切换自动检查偏好");
        updateWindow.Close();

        using (var reloadedPreferences = new UpdateCoordinator(
                   new FakeUpdateClient(Path.Combine(testRoot, "preference-reload")),
                   updatesDirectory,
                   new Version(2, 2, 0)))
        {
            Check(!reloadedPreferences.AutomaticCheckAndDownload, "自动检查偏好跨启动持久化");
        }

        var blockedUpdatesDirectory = Path.Combine(testRoot, "updates-path-occupied");
        File.WriteAllText(blockedUpdatesDirectory, "not a directory");
        var blockedClient = new FakeUpdateClient(Path.Combine(blockedUpdatesDirectory, "cache"));
        using (var blockedCoordinator = new UpdateCoordinator(
                   blockedClient,
                   blockedUpdatesDirectory,
                   new Version(2, 2, 0)))
        {
            var blockedStore = new SkinStore(Path.Combine(testRoot, "blocked-updates-manager-skins"));
            blockedStore.Initialize();
            var blockedManager = new MainWindow(
                blockedStore,
                basePackage,
                Path.Combine(testRoot, "blocked-updates-preview"),
                readRuntimeStatusOnLoad: false,
                updateCoordinator: blockedCoordinator,
                installerLauncher: new FakeInstallerLauncher());
            blockedCoordinator.StartAutomatically();
            await WaitUntilAsync(() => Task.FromResult(blockedCoordinator.StatusMessage.Contains("下载失败", StringComparison.Ordinal)),
                "更新缓存路径冲突时保留管理器并显示下载失败");
            var blockedWindow = new UpdatesWindow(blockedCoordinator);
            Check(FindNamed<TextBlock>(blockedWindow, "StatusText").Text.Contains("下载失败", StringComparison.Ordinal),
                "无法创建更新缓存时错误可在更新窗口中读取和重试");
            blockedManager.Close();
            await blockedManager.Dispatcher.InvokeAsync(() => { });
        }

        updateClient.CheckResult = new UpdateCheckResult(UpdateCheckState.NotPublished);
        await coordinator.CheckAsync();
        Check(coordinator.StatusMessage == "尚未发布可用更新" && !coordinator.HasAvailableUpdate,
            "未发布更新显示正常状态而不误称已是最新版");
        updateClient.CheckResult = new UpdateCheckResult(UpdateCheckState.PackageNotReady);
        await coordinator.CheckAsync();
        Check(coordinator.StatusMessage.Contains("安装包尚未准备好", StringComparison.Ordinal),
            "新版本发布但安装包未就绪时显示单独状态");

        updateClient.CheckResult = new UpdateCheckResult(UpdateCheckState.Available, updateClient.Release);
        await coordinator.CheckAsync();
        updateClient.BlockNextDownload = true;
        var cancellationTask = coordinator.DownloadAsync();
        await updateClient.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.CancelDownload();
        await cancellationTask;
        Check(!coordinator.IsDownloading && coordinator.DownloadedUpdate is null &&
              coordinator.StatusMessage.Contains("已取消", StringComparison.Ordinal),
            "下载可以取消且保留可重试状态");
        updateClient.BlockNextDownload = false;
        await coordinator.DownloadAsync();
        Check(coordinator.HasDownloadedUpdate, "取消后可以重新下载并恢复安装动作");

        var handoffDirectory = Path.Combine(testRoot, "updates-handoff");
        var handoffClient = new FakeUpdateClient(Path.Combine(testRoot, "handoff-downloads"));
        using var handoffCoordinator = new UpdateCoordinator(handoffClient, handoffDirectory, new Version(2, 2, 0));
        handoffCoordinator.AutomaticCheckAndDownload = false;
        await handoffCoordinator.CheckAsync();
        await handoffCoordinator.DownloadAsync();
        var launcher = new FakeInstallerLauncher();
        var promptAnswer = MessageBoxResult.Cancel;
        var store = new SkinStore(Path.Combine(testRoot, "update-handoff-skins"));
        store.Initialize();
        var manager = new MainWindow(
            store,
            basePackage,
            Path.Combine(testRoot, "update-handoff-preview"),
            readRuntimeStatusOnLoad: false,
            updateCoordinator: handoffCoordinator,
            installerLauncher: launcher,
            editorClosePrompt: _ => promptAnswer,
            closeAfterSuccessfulInstall: false)
        {
            Width = 960,
            Height = 640,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        manager.Show();
        await manager.Dispatcher.InvokeAsync(() => manager.UpdateLayout());
        FindNamed<Button>(manager, "NewSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = FindNamed<SkinEditorView>(manager, "SkinEditor");
        FindNamed<TextBox>(editor, "NameInput").Text = "待安装交接草稿";
        Check(editor.IsDirty, "安装交接测试准备未保存草稿");

        Check(!await manager.InstallUpdateAsync() && launcher.LaunchCount == 0 && editor.IsDirty &&
              handoffCoordinator.HasDownloadedUpdate,
            "取消未保存编辑时不启动安装器且保留草稿和更新包");

        promptAnswer = MessageBoxResult.Yes;
        FindNamed<TextBox>(editor, "IdInput").Text = "invalid id";
        Check(!await manager.InstallUpdateAsync() && launcher.LaunchCount == 0 && editor.IsDirty &&
              handoffCoordinator.HasDownloadedUpdate,
            "保存失败时不启动安装器且保留窗口和更新包");

        promptAnswer = MessageBoxResult.No;
        Check(await manager.InstallUpdateAsync() && launcher.LaunchCount == 1 && manager.IsVisible &&
              handoffCoordinator.HasDownloadedUpdate && launcher.InstallDirectory == AppContext.BaseDirectory,
            "确认放弃草稿后只启动一次fake安装器并保留管理器测试窗口");
        manager.Close();
        await WaitUntilAsync(() => Task.FromResult(!manager.IsVisible), "未保存编辑处理后真正关闭管理器窗口");

        var closingCoordinator = new UpdateCoordinator(
            new FakeUpdateClient(Path.Combine(testRoot, "closing-downloads")),
            Path.Combine(testRoot, "updates-successful-close"),
            new Version(2, 2, 0));
        closingCoordinator.AutomaticCheckAndDownload = false;
        await closingCoordinator.CheckAsync();
        await closingCoordinator.DownloadAsync();
        var closingStore = new SkinStore(Path.Combine(testRoot, "update-successful-close-skins"));
        closingStore.Initialize();
        var closingLauncher = new FakeInstallerLauncher();
        var closingManager = new MainWindow(
            closingStore,
            basePackage,
            Path.Combine(testRoot, "update-successful-close-preview"),
            readRuntimeStatusOnLoad: false,
            updateCoordinator: closingCoordinator,
            installerLauncher: closingLauncher)
        {
            Width = 960,
            Height = 640,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        var closingManagerWasClosed = false;
        closingManager.Closed += (_, _) => closingManagerWasClosed = true;
        closingManager.Show();
        await closingManager.Dispatcher.InvokeAsync(() => closingManager.UpdateLayout());
        Check(await closingManager.InstallUpdateAsync() && closingLauncher.LaunchCount == 1 &&
              closingManagerWasClosed && !closingManager.IsVisible &&
              Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown,
            "fake安装器成功后真实MainWindow关闭且Preview测试应用继续运行");
    }

    private static async Task VerifyManagerControlsAsync(
        string testRoot,
        BasePackage basePackage,
        byte[] ayanamiBytes,
        byte[] nativeBytes,
        byte[] sayaBytes,
        SkinPackage ayanami,
        SkinPackage saya,
        SkinPackage declaredDark)
    {
        var screenshotDirectory = FindTestOutputDirectory();
        var store = new SkinStore(Path.Combine(testRoot, "manager-data"));
        store.Initialize();
        store.Save(ayanami, replaceExisting: false);
        store.Save(SkinPackageReader.ReadZipBytes(nativeBytes), replaceExisting: false);
        store.Save(SkinPackageReader.ReadZipBytes(sayaBytes), replaceExisting: false);
        store.Save(declaredDark, replaceExisting: false);
        Check(store.GetSkins().Count == 4, "管理器测试库保存 Native、Ayanami、Saya 和控制皮肤");

        var emptyStore = new SkinStore(Path.Combine(testRoot, "empty-library"));
        emptyStore.Initialize();
        var emptyWindow = new MainWindow(
            emptyStore,
            basePackage,
            Path.Combine(testRoot, "empty-preview-cache"),
            readRuntimeStatusOnLoad: false,
            codexInstanceService: new FakeCodexInstanceService([]))
        {
            Width = 960,
            Height = 640,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        Check(FindNamed<StackPanel>(emptyWindow, "ListEmptyState").Visibility == Visibility.Visible &&
              FindNamed<Border>(emptyWindow, "DetailsEmptyState").Visibility == Visibility.Visible &&
              FindNamed<Button>(emptyWindow, "ListEmptyImportButton").IsEnabled,
            "空库时侧栏与详情区显示可继续导入的空状态");
        emptyWindow.Show();
        await WaitUntilAsync(() => Task.FromResult(emptyWindow.IsLoaded && emptyWindow.Opacity >= 0.999),
            "空库管理器淡入完成后再捕获客户区");
        await SaveWindowScreenshotAsync(emptyWindow, Path.Combine(screenshotDirectory, "管理器-空库-最小窗口-960x640.png"));
        emptyWindow.Close();

        var connectedDebug = CreateFakeInstance(7411, 9335, CodexInstanceKind.Debug, canConnect: true, "Codex 当前连接实例");
        var selectedDebug = CreateFakeInstance(7412, 9336, CodexInstanceKind.Debug, canConnect: true, "Codex 可选调试实例");
        var unavailableDebug = CreateFakeInstance(7413, 9444, CodexInstanceKind.Debug, canConnect: false, "Codex 未就绪调试实例");
        var ordinary = CreateFakeInstance(7414, null, CodexInstanceKind.Normal, canConnect: false, "Codex 普通实例");
        var fakeInstances = new FakeCodexInstanceService([connectedDebug, selectedDebug, unavailableDebug, ordinary]);
        var fakeStatusReadCount = 0;
        var fakeConnectedStatus = new RuntimeStatus(
            RuntimeStatusState.Connected,
            ayanami.Id,
            "http://127.0.0.1:9335",
            "fake connected",
            true,
            1,
            connectedDebug.Identity);
        var fakeStoppedStatus = new RuntimeStatus(RuntimeStatusState.Stopped, null, null, "fake closed", null);

        var window = new MainWindow(
            store,
            basePackage,
            Path.Combine(testRoot, "manager-preview-cache"),
            readRuntimeStatusOnLoad: true,
            codexInstanceService: fakeInstances,
            confirmInstanceClose: _ => true,
            confirmInstanceForceClose: _ => true,
            runtimeStatusReader: _ =>
            {
                fakeStatusReadCount++;
                return Task.FromResult(fakeInstances.Instances.Any(instance => instance.Identity == connectedDebug.Identity)
                    ? fakeConnectedStatus
                    : fakeStoppedStatus);
            })
        {
            Width = 960,
            Height = 640,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        window.Show();
        try
        {
            Check(window.Width == 960 && window.Height == 640 &&
                  window.MinWidth == 960 && window.MinHeight == 640,
                "管理器在960×640 WPF逻辑尺寸下运行并保留最小窗口约束");
            var browser = FindNamed<WebView2>(window, "PreviewBrowser");
            var list = FindNamed<ListBox>(window, "SkinList");
            var sceneBox = FindNamed<ComboBox>(window, "PreviewSceneBox");
            sceneBox.ApplyTemplate();
            var scenePopup = sceneBox.Template.FindName("PART_Popup", sceneBox) as Popup;
            var sceneToggle = sceneBox.Template.FindName("DropDownToggle", sceneBox) as ToggleButton;
            var transparencyInput = FindNamed<TextBox>(window, "PreviewTransparencyInput");
            Check(sceneBox.MinHeight >= 32 &&
                  FindNamed<ComboBox>(window, "PreviewAppearanceBox").MinHeight >= 32 &&
                  scenePopup is not null && sceneToggle is not null &&
                  BindingOperations.GetBinding(scenePopup, Popup.IsOpenProperty)?.Mode == BindingMode.TwoWay &&
                  BindingOperations.GetBinding(sceneToggle, ToggleButton.IsCheckedProperty)?.Mode == BindingMode.TwoWay &&
                  FindNamed<CheckBox>(window, "PreviewSidebarCheckBox").Template is not null &&
                  transparencyInput.TextAlignment == TextAlignment.Right && !transparencyInput.IsEnabled,
                "圆角选择器保留双向下拉部件，数字透明度输入沿用统一文本框样式并在跟随主题时禁用");
            var ayanamiRow = list.Items.Cast<object>().Single(item => GetRowId(item) == ayanami.Id);
            list.SelectedItem = ayanamiRow;
            await WaitUntilAsync(() =>
            {
                list.UpdateLayout();
                var container = list.ItemContainerGenerator.ContainerFromItem(ayanamiRow);
                var image = container is null ? null : FindNamedVisual<Image>(container, "ThumbnailImage");
                var placeholder = container is null ? null : FindNamedVisual<TextBlock>(container, "ThumbnailPlaceholder");
                var rowAppearance = container is null ? null : FindNamedVisual<TextBlock>(container, "RowAppearanceLabel");
                return Task.FromResult(image?.Source is not null && image.Visibility == Visibility.Visible &&
                    placeholder?.Visibility == Visibility.Collapsed &&
                    rowAppearance?.Text.Contains("浅色", StringComparison.Ordinal) == true);
            }, "皮肤列表显示原图缩略图与浅色默认外观");
            await WaitUntilAsync(async () => browser.CoreWebView2 is not null && await ReadWindowPageAsync(browser) == "package:home",
                "管理器加载本地皮肤预览");

            var colors = FindNamed<ItemsControl>(window, "ColorItems");
            var activeRenderer = GetWindowRenderer(window);
            await WaitUntilAsync(() => Task.FromResult(activeRenderer.CurrentResolvedSkin is not null && colors.Items.Count > 0 &&
                FindNamed<Button>(window, "OriginalButton").IsEnabled),
                "管理器解析当前皮肤并生成色卡");
            await WaitUntilAsync(() => Task.FromResult(transparencyInput.Text == "75" &&
                !transparencyInput.IsEnabled &&
                FindNamed<TextBlock>(window, "PreviewTransparencyValue").Text == "主题默认"),
                "跟随主题时显示当前皮肤解析出的 75% 默认面板透明度");
            var defaultAppearanceProbe = await ReadObjectAsync(activeRenderer, """
                (() => {
                  const composer = document.querySelector('[data-skin-part="composer"] [data-composer-body]');
                  const background = composer ? getComputedStyle(composer).backgroundColor : '';
                  const alpha = background.startsWith('rgba(')
                    ? Number(background.slice(background.lastIndexOf(',') + 1, -1).trim())
                    : 1;
                  return {
                    appearance: document.documentElement.getAttribute('data-theme') || '',
                    composerAlpha: String(alpha)
                  };
                })()
                """);
            Check(defaultAppearanceProbe["appearance"] == "light" && defaultAppearanceProbe["composerAlpha"] == "0.25",
                $"Ayanami 皮肤默认实际以浅色页面和 25% composer 背景 alpha 渲染：{JsonSerializer.Serialize(defaultAppearanceProbe)}");
            Check(FindNamed<FrameworkElement>(window, "ColorSection").Visibility == Visibility.Visible,
                "有令牌的皮肤显示配色分组");
            await WaitUntilAsync(() => Task.FromResult(FindNamed<TextBlock>(window, "PreviewStatus").Visibility == Visibility.Collapsed),
                "预览更新后相邻状态提示收起");
            await WaitUntilAsync(() => Task.FromResult(
                FindNamed<TextBlock>(window, "RuntimeConnectedTargetText").Text == $"PID {connectedDebug.Identity.ProcessId}"),
                "fake RuntimeStatus 注入当前连接PID");
            Check(FindNamed<TextBox>(window, "RuntimePortInput").Text == "9335" &&
                  FindNamed<TextBlock>(window, "RuntimeStatusText").Text == "已应用" &&
                  FindNamed<TextBlock>(window, "RuntimeTargetText").Text == "手动端口 · 9335" &&
                  FindNamed<Button>(window, "LaunchDebugButton").IsEnabled &&
                  FindNamed<Button>(window, "ManageInstancesButton").IsEnabled,
                "离线测试通过注入的fake RuntimeStatus显示真实连接身份，未访问实际RuntimeController");

            var connectedMarker = string.Empty;
            var targetMarker = string.Empty;
            string? instanceSelectionError = null;
            var previousAnimationOverride = WindowMotion.AnimationEnabledOverrideForTests;
            var selectionDeadline = DateTime.UtcNow.AddSeconds(12);
            var selectionDispatched = false;
            var dialogStatesLogged = false;
            var selectionTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            selectionTimer.Tick += (_, _) =>
            {
                var allDialogs = Application.Current.Windows.OfType<InstancesWindow>().ToArray();
                var dialog = allDialogs.FirstOrDefault(candidate => candidate.IsVisible);
                if (!dialogStatesLogged && allDialogs.Length > 0)
                {
                    dialogStatesLogged = true;
                    var state = string.Join(" | ", allDialogs.Select(candidate =>
                    {
                        var handle = new System.Windows.Interop.WindowInteropHelper(candidate).Handle;
                        return $"hwnd=0x{handle.ToInt64():X}, visible={candidate.IsVisible}, loaded={candidate.IsLoaded}, opacity={candidate.Opacity:0.###}, count={candidate.Instances.Count}";
                    }));
                    Console.WriteLine($"[manager-instances] InstancesWindow首次观察: {state}");
                    if (dialog is not null)
                    {
                        dialog.Closing += (_, args) => Console.WriteLine(
                            $"[manager-instances] Closing hwnd=0x{new System.Windows.Interop.WindowInteropHelper(dialog).Handle.ToInt64():X}, Cancel={args.Cancel}, DialogResult={TryReadDialogResult(dialog)?.ToString() ?? "null"}, opacity={dialog.Opacity:0.###}");
                        dialog.Closed += (_, _) => Console.WriteLine(
                            $"[manager-instances] Closed hwnd=0x{new System.Windows.Interop.WindowInteropHelper(dialog).Handle.ToInt64():X}, DialogResult={TryReadDialogResult(dialog)?.ToString() ?? "null"}, opacity={dialog.Opacity:0.###}");
                    }
                }

                if (dialog is not null && !selectionDispatched && dialog.Instances.Count == 4)
                {
                    selectionDispatched = true;
                    try
                    {
                        var dialogList = FindNamed<ListView>(dialog, "InstanceList");
                        connectedMarker = RowProperty(FindInstanceRow(dialogList, connectedDebug.Identity), "ConnectionText");
                        targetMarker = RowProperty(FindInstanceRow(dialogList, selectedDebug.Identity), "ConnectionText");
                        dialogList.SelectedItem = FindInstanceRow(dialogList, selectedDebug.Identity);
                        if (!FindNamed<Button>(dialog, "UseInstanceButton").IsEnabled)
                        {
                            instanceSelectionError = "可连接的调试实例按钮仍处于禁用状态。";
                            dialog.Close();
                        }
                        else
                        {
                            var useButton = FindNamed<Button>(dialog, "UseInstanceButton");
                            useButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Console.WriteLine($"[manager-instances] 已派发选择 hwnd=0x{new System.Windows.Interop.WindowInteropHelper(dialog).Handle.ToInt64():X}, SelectedForConnection={dialog.SelectedForConnection?.Identity.ProcessId.ToString() ?? "null"}, DialogResult={TryReadDialogResult(dialog)?.ToString() ?? "null"}, visible={dialog.IsVisible}, opacity={dialog.Opacity:0.###}");
                        }
                    }
                    catch (Exception exception)
                    {
                        instanceSelectionError = exception.Message;
                        dialog.Close();
                    }
                }
                else if (DateTime.UtcNow >= selectionDeadline)
                {
                    instanceSelectionError = selectionDispatched
                        ? "实例已选择，但模态窗口未在期限内完成淡出和关闭。"
                        : "实例对话框在期限内没有加载fake实例。";
                    Console.WriteLine($"[manager-instances] 12秒看门超时: dialogs={allDialogs.Length}, visible={dialog is not null}, loaded={dialog?.IsLoaded}, opacity={dialog?.Opacity:0.###}, count={dialog?.Instances.Count}, selected={selectionDispatched}");
                    WindowMotion.AnimationEnabledOverrideForTests = false;
                    if (dialog is { IsVisible: true })
                    {
                        try { dialog.DialogResult = false; } catch (InvalidOperationException) { dialog.Close(); }
                    }
                    else
                    {
                        Application.Current.Shutdown(1);
                    }
                    selectionTimer.Stop();
                }
            };
            selectionTimer.Start();
            var openInstancesOperation = window.Dispatcher.InvokeAsync(
                () => FindNamed<Button>(window, "ManageInstancesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                System.Windows.Threading.DispatcherPriority.Background);
            await openInstancesOperation.Task;
            selectionTimer.Stop();
            WindowMotion.AnimationEnabledOverrideForTests = previousAnimationOverride;
            Check(instanceSelectionError is null && connectedMarker == "当前连接" && targetMarker == "—",
                $"实例对话框依据真实连接身份显示标识：{instanceSelectionError ?? $"connected={connectedMarker}, target={targetMarker}"}");
            Check(FindNamed<TextBox>(window, "RuntimePortInput").Text == selectedDebug.DebugPort?.ToString() &&
                  FindNamed<TextBlock>(window, "RuntimeTargetText").Text.Contains($"PID {selectedDebug.Identity.ProcessId}", StringComparison.Ordinal) &&
                  FindNamed<TextBlock>(window, "RuntimeConnectedTargetText").Text == $"PID {connectedDebug.Identity.ProcessId}",
                "使用调试实例只填入连接目标和端口，不自动连接，当前连接身份保持独立");
            var buildRequest = (RuntimeApplyRequest)(typeof(MainWindow)
                .GetMethod("BuildRuntimeApplyRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(window, [ayanami, selectedDebug.DebugPort!.Value, null])
                ?? throw new MissingMethodException(typeof(MainWindow).FullName, "BuildRuntimeApplyRequest"));
            Check(buildRequest.Endpoint.EndsWith($":{selectedDebug.DebugPort}", StringComparison.Ordinal) &&
                  buildRequest.ExpectedInstance == selectedDebug.Identity,
                "所选调试目标将其身份写入ExpectedInstance，端口写入连接端点");

            FindNamed<TextBox>(window, "RuntimePortInput").Text = "9337";
            Check(FindNamed<TextBlock>(window, "RuntimeTargetText").Text == "手动端口 · 9337",
                "手动修改端口会清除之前选中的实例目标");
            var manualRequest = (RuntimeApplyRequest)(typeof(MainWindow)
                .GetMethod("BuildRuntimeApplyRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(window, [ayanami, 9337, null])
                ?? throw new MissingMethodException(typeof(MainWindow).FullName, "BuildRuntimeApplyRequest"));
            Check(manualRequest.ExpectedInstance is null && manualRequest.Endpoint.EndsWith(":9337", StringComparison.Ordinal),
                "清除所选实例后手动端口请求不携带过期ExpectedInstance");

            FindNamed<Button>(window, "LocateConnectedWindowButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(fakeInstances.LocateCalls.Count == 1), "主窗口定位请求完成");
            Check(fakeInstances.LocateCalls.Single().Identity == connectedDebug.Identity &&
                  FindNamed<TextBlock>(window, "RuntimeConnectedTargetText").Text == $"PID {connectedDebug.Identity.ProcessId}",
                "定位已连接窗口按RuntimeStatus身份找确切行，不使用新选择或同端口实例");

            var closeDialogError = (string?)null;
            var closeDialogRequested = false;
            var closeDialogDeadline = DateTime.UtcNow.AddSeconds(8);
            var closeDialogTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            closeDialogTimer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<InstancesWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (dialog is not null && !closeDialogRequested && dialog.Instances.Count == fakeInstances.Instances.Count)
                {
                    var dialogList = FindNamed<ListView>(dialog, "InstanceList");
                    dialogList.SelectedItem = FindInstanceRow(dialogList, connectedDebug.Identity);
                    FindNamed<Button>(dialog, "CloseInstanceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    closeDialogRequested = true;
                }
                else if (dialog is not null && closeDialogRequested && dialog.LastClosedIdentity == connectedDebug.Identity)
                {
                    FindNamed<Button>(dialog, "DoneButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    closeDialogTimer.Stop();
                }
                else if (DateTime.UtcNow >= closeDialogDeadline)
                {
                    closeDialogError = "未能在实例对话框中完成fake普通关闭。";
                    dialog?.Close();
                    closeDialogTimer.Stop();
                }
            };
            closeDialogTimer.Start();
            FindNamed<Button>(window, "ManageInstancesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            closeDialogTimer.Stop();
            await WaitUntilAsync(() => Task.FromResult(
                FindNamed<TextBlock>(window, "RuntimeConnectedTargetText").Text == "未连接" &&
                FindNamed<TextBlock>(window, "RuntimeStatusText").Text == "未连接"),
                "关闭当前连接实例后通过fake RuntimeStatus刷新为未连接");
            Check(closeDialogError is null &&
                  fakeInstances.CloseCalls.Any(call => call.Instance.Identity == connectedDebug.Identity && !call.Force) &&
                  fakeStatusReadCount >= 2 &&
                  !fakeInstances.Instances.Any(instance => instance.Identity == connectedDebug.Identity),
                $"关闭当前连接后只发普通关闭、移出实例列表并读取新状态：{closeDialogError ?? $"reads={fakeStatusReadCount}"}");

            FindNamed<ComboBox>(window, "PreviewAppearanceBox").SelectedIndex = 1;
            Check(browser.Visibility == Visibility.Visible, "切换预览明暗时保留当前 WebView 视口连续显示");
            await WaitUntilAsync(() => Task.FromResult(activeRenderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Light &&
                TryGetColorValue(colors, "color.surface.window") ==
                SkinResolver.Resolve(ayanami, SkinAppearance.Light, SkinScene.Home).Colors["color.surface.window"]),
                "管理器色卡切换到浅色 resolved colors");
            var lightValue = GetColorValue(colors, "color.surface.window");
            FindNamed<ComboBox>(window, "PreviewAppearanceBox").SelectedIndex = 2;
            await WaitUntilAsync(() => Task.FromResult(activeRenderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Dark &&
                TryGetColorValue(colors, "color.surface.window") ==
                SkinResolver.Resolve(ayanami, SkinAppearance.Dark, SkinScene.Home).Colors["color.surface.window"]),
                "管理器色卡切换到深色 resolved colors");
            Check(lightValue != GetColorValue(colors, "color.surface.window"), "管理器浅深色切换更新色卡值");
            Check(FindNamed<TextBlock>(window, "SelectedId").Text.Contains(ayanami.Version, StringComparison.Ordinal) &&
                  FindNamed<TextBlock>(window, "SelectedAppearance").Text.Contains("包格式：v1", StringComparison.Ordinal),
                "皮肤版本与包格式在详情中可见");

            FindNamed<ComboBox>(window, "PreviewAppearanceBox").SelectedIndex = 0;
            var darkRow = list.Items.Cast<object>().Single(item => GetRowId(item) == declaredDark.Id);
            list.SelectedItem = darkRow;
            var declaredDarkColor = SkinResolver.Resolve(declaredDark, SkinAppearance.Dark, SkinScene.Home).Colors["color.surface.window"];
            await WaitUntilAsync(() => Task.FromResult(activeRenderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Dark &&
                TryGetColorValue(colors, "color.surface.window") == declaredDarkColor),
                "跟随皮肤模式遵循包声明的 dark 外观");
            Check(GetColorValue(colors, "color.surface.window") == declaredDarkColor,
                "声明 dark 的皮肤在跟随皮肤模式显示其深色令牌");

            list.SelectedItem = ayanamiRow;
            await WaitUntilAsync(async () => await ReadWindowPageAsync(browser) == "package:home", "返回 Ayanami 皮肤预览");
            FindNamed<ComboBox>(window, "PreviewAppearanceBox").SelectedIndex = 0;
            await WaitUntilAsync(() =>
            {
                var value = activeRenderer.CurrentResolvedSkin?.Colors.GetValueOrDefault("color.surface.window");
                return Task.FromResult(activeRenderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Light &&
                    value == SkinResolver.Resolve(ayanami, SkinAppearance.Light, SkinScene.Home).Colors["color.surface.window"] &&
                    TryGetColorValue(colors, "color.surface.window") == value && FindNamed<Button>(window, "OriginalButton").IsEnabled);
            },
                "Ayanami 跟随皮肤预览以浅色与对应色卡完成刷新");

            var transparencyFollow = FindNamed<CheckBox>(window, "PreviewTransparencyFollowCheckBox");
            var transparencyError = FindNamed<TextBlock>(window, "PreviewTransparencyError");
            Check(transparencyInput.Text == "75" && !transparencyInput.IsEnabled &&
                  FindNamed<TextBlock>(window, "PreviewTransparencyValue").Text == "主题默认",
                "跟随主题时显示 75% 主题默认值并禁用数值输入");
            transparencyFollow.IsChecked = false;
            await WaitUntilAsync(() => Task.FromResult(transparencyInput.IsEnabled && transparencyInput.Text == "30" &&
                activeRenderer.CurrentResolvedSkin?.PanelOpacityOverride is double initialPanelOpacity &&
                Math.Abs(initialPanelOpacity - 0.70) < 0.0001),
                "关闭跟随主题后恢复原有30%手动值并应用对应面板 alpha");

            var beforeEdit = activeRenderer.CurrentResolvedSkin;
            transparencyInput.Text = "0";
            Check(ReferenceEquals(activeRenderer.CurrentResolvedSkin, beforeEdit),
                "编辑中的透明度文本不会立即重建预览");
            RaiseKeyDown(transparencyInput, Key.Enter);
            await WaitUntilAsync(() => Task.FromResult(activeRenderer.CurrentResolvedSkin?.PanelOpacityOverride is double opaqueOpacity &&
                Math.Abs(opaqueOpacity - 1d) < 0.0001), "回车提交0%并得到不透明面板");
            CheckBackgroundUnchanged(activeRenderer.CurrentResolvedSkin, ayanami);

            transparencyInput.Text = "100";
            RaiseLostKeyboardFocus(transparencyInput, window);
            await WaitUntilAsync(() => Task.FromResult(activeRenderer.CurrentResolvedSkin?.PanelOpacityOverride is double clearOpacity &&
                Math.Abs(clearOpacity) < 0.0001), "失焦提交100%并得到全透明面板");
            CheckBackgroundUnchanged(activeRenderer.CurrentResolvedSkin, ayanami);

            transparencyInput.Text = "42";
            RaiseKeyDown(transparencyInput, Key.Enter);
            await WaitUntilAsync(() => Task.FromResult(activeRenderer.CurrentResolvedSkin?.PanelOpacityOverride is double ordinaryOpacity &&
                Math.Abs(ordinaryOpacity - 0.58) < 0.0001), "回车提交普通整数42%并更新 resolved skin alpha");
            CheckBackgroundUnchanged(activeRenderer.CurrentResolvedSkin, ayanami);

            foreach (var invalidValue in new[] { "", "abc", "-1", "101", "12.5", new string('9', 32) })
            {
                var lastValidPreview = activeRenderer.CurrentResolvedSkin;
                transparencyInput.Text = invalidValue;
                var invalidLabel = invalidValue.Length == 0 ? "空值" : $"“{invalidValue}”";
                RaiseKeyDown(transparencyInput, Key.Enter);
                await WaitUntilAsync(() => Task.FromResult(
                    ReferenceEquals(activeRenderer.CurrentResolvedSkin, lastValidPreview) &&
                    transparencyError.Visibility == Visibility.Visible),
                    $"无效粘贴/输入{invalidLabel}显示提示且保留上一有效预览");
                Check(transparencyError.Text.Contains("0 到 100 的整数", StringComparison.Ordinal),
                    "透明度校验提示给出允许范围和整数要求");
            }

            transparencyFollow.IsChecked = true;
            await WaitUntilAsync(() => Task.FromResult(!transparencyInput.IsEnabled &&
                transparencyInput.Text == "75" && transparencyError.Visibility == Visibility.Collapsed &&
                activeRenderer.CurrentResolvedSkin?.PanelOpacityOverride is null),
                "非法输入后切到跟随主题时显示75并清除错误");
            transparencyFollow.IsChecked = false;
            await WaitUntilAsync(() => Task.FromResult(transparencyInput.IsEnabled && transparencyInput.Text == "42" &&
                Math.Abs((activeRenderer.CurrentResolvedSkin?.PanelOpacityOverride ?? double.NaN) - 0.58) < 0.0001),
                "取消跟随后恢复42且预览alpha仍与上次有效值一致");

            transparencyInput.Text = "73";
            RaiseKeyDown(transparencyInput, Key.Escape);
            Check(transparencyInput.Text == "42" && transparencyError.Visibility == Visibility.Collapsed &&
                  Math.Abs((activeRenderer.CurrentResolvedSkin?.PanelOpacityOverride ?? double.NaN) - 0.58) < 0.0001,
                "Escape取消编辑并恢复最后一次有效值");

            InvokeWindowMethod(window, "SetBusy", true);
            Check(!transparencyInput.IsEnabled, "管理器忙碌时禁用透明度输入");
            InvokeWindowMethod(window, "SetBusy", false);
            Check(transparencyInput.IsEnabled, "管理器空闲后恢复透明度输入");
            transparencyFollow.IsChecked = true;
            await WaitUntilAsync(() => Task.FromResult(!transparencyInput.IsEnabled &&
                activeRenderer.CurrentResolvedSkin?.PanelOpacityOverride is null),
                "测试结束后恢复跟随主题默认状态");

            var previewViewport = FindNamed<Grid>(window, "PreviewViewport");
            await WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                return Task.FromResult(Math.Abs(browser.Width - previewViewport.ActualWidth) < 1 &&
                    Math.Abs(browser.Height - previewViewport.ActualHeight) < 1);
            }, "预览浏览器尺寸匹配最小窗口中的实际可用视口");
            var compactViewport = new Size(previewViewport.ActualWidth, previewViewport.ActualHeight);
            Check(compactViewport.Width > 400 && compactViewport.Height >= 150,
                $"最小窗口仍保留可用预览视口：{compactViewport.Width:0} × {compactViewport.Height:0}");
            window.Width = 1240;
            window.Height = 840;
            window.UpdateLayout();
            await WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                return Task.FromResult(previewViewport.ActualWidth > compactViewport.Width + 100 &&
                    previewViewport.ActualHeight > compactViewport.Height + 100 &&
                    Math.Abs(browser.Width - previewViewport.ActualWidth) < 1 &&
                    Math.Abs(browser.Height - previewViewport.ActualHeight) < 1);
            }, "放大窗口时预览浏览器跟随真实 viewport 宽高增长");
            window.Width = 960;
            window.Height = 640;
            window.UpdateLayout();
            await WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                return Task.FromResult(Math.Abs(browser.Width - previewViewport.ActualWidth) < 1 &&
                    Math.Abs(browser.Height - previewViewport.ActualHeight) < 1);
            }, "缩回最小窗口时预览浏览器重新匹配 viewport");
            var colorItemsScroll = FindNamed<ScrollViewer>(window, "ColorItemsScrollViewer");
            var accentItem = colors.Items.Cast<object>().Single(item =>
                item.GetType().GetProperty("RawName")?.GetValue(item) as string == "color.accent");
            var accentCard = FindVisualChildren<Border>(colors).Single(card =>
                card.Width == 140 && ReferenceEquals(card.DataContext, accentItem));
            await WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                colorItemsScroll.UpdateLayout();
                return Task.FromResult(colorItemsScroll.ViewportWidth > 0 && colorItemsScroll.ScrollableWidth > 0);
            }, "最小窗口配色区域获得有限宽度并启用横向滚动");
            Check(colorItemsScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto &&
                  colorItemsScroll.ScrollableWidth > 0,
                $"最小窗配色栏可横向浏览全部色卡：viewport={colorItemsScroll.ViewportWidth:0.#}, scrollable={colorItemsScroll.ScrollableWidth:0.#}");
            colorItemsScroll.ScrollToHorizontalOffset(colorItemsScroll.ScrollableWidth);
            await WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                colorItemsScroll.UpdateLayout();
                var accentBounds = accentCard.TransformToAncestor(colorItemsScroll)
                    .TransformBounds(new Rect(new Point(0, 0), accentCard.RenderSize));
                return Task.FromResult(accentCard.IsVisible && accentBounds.Left >= -1 &&
                    accentBounds.Right <= colorItemsScroll.ViewportWidth + 1);
            }, "横向滚到末尾后最后的强调色卡完整可见");
            var originalButton = FindNamed<Button>(window, "OriginalButton");
            originalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Task.FromResult(FindNamed<Grid>(window, "OriginalImageLayer").Visibility == Visibility.Visible),
                "查看原图覆盖在预览视口");
            originalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(async () =>
                    FindNamed<Grid>(window, "OriginalImageLayer").Visibility == Visibility.Collapsed &&
                    browser.Visibility == Visibility.Visible && await ReadWindowPageAsync(browser) == "package:home",
                "关闭原图后返回同一预览视口");

            var originalBase = FindNamed<CheckBox>(window, "PreviewOriginalBaseCheckBox");
            originalBase.IsChecked = true;
            await WaitUntilAsync(async () => await ReadWindowPageAsync(browser) == "base:home", "管理器原版對照顯示基礎頁");
            originalBase.IsChecked = false;
            await WaitUntilAsync(async () => await ReadWindowPageAsync(browser) == "package:home", "管理器關閉原版對照後恢復皮膚");
            await WaitUntilAsync(() => Task.FromResult(activeRenderer.CurrentResolvedSkin is not null), "恢復皮膚后 resolved skin 更新");

        var nativeRow = list.Items.Cast<object>().Single(item => GetRowId(item) == "native");
            list.SelectedItem = nativeRow;
            await WaitUntilAsync(() => Task.FromResult(
                FindNamed<TextBlock>(window, "SelectedImageDimensions").Text == "无背景图" &&
                !originalButton.IsEnabled), "無圖包顯示純色占位並停用查看原圖");
            await WaitUntilAsync(() => Task.FromResult(colors.Items.Count == 0 &&
                FindNamed<FrameworkElement>(window, "ColorSection").Visibility == Visibility.Collapsed),
                "无颜色令牌的原生皮肤收起空配色分组");
            await WaitUntilAsync(async () => await ReadWindowPageAsync(browser) == "package:home", "無圖包正常進入預覽而不報圖片錯誤");
            Check(!FindNamed<Border>(window, "LibraryWarning").IsVisible, "無圖包不觸發圖片損壞警告");
            var nativeContainer = list.ItemContainerGenerator.ContainerFromItem(nativeRow);
            var nativeThumbnail = nativeContainer is null ? null : FindNamedVisual<Image>(nativeContainer, "ThumbnailImage");
            var nativePlaceholder = nativeContainer is null ? null : FindNamedVisual<TextBlock>(nativeContainer, "ThumbnailPlaceholder");
            Check(nativeThumbnail?.Visibility == Visibility.Collapsed &&
                  nativePlaceholder?.Visibility == Visibility.Visible && nativePlaceholder.Text == "无背景图",
                "原生无图皮肤在侧栏显示明确的中性缩略图占位");

            store.Delete(declaredDark.Id);
            InvokeWindowMethod(window, "RefreshList", new object?[] { null });
            Check(store.GetSkins().Count == 3, "Saya 与 Native、Ayanami 共存于管理器预览库");
            FindNamed<Grid>(window, "DetailsScroll").UpdateLayout();
            var sayaRow = list.Items.Cast<object>().Single(item => GetRowId(item) == saya.Id);
            list.SelectedItem = sayaRow;
            await WaitUntilAsync(async () =>
                {
                    if (await ReadWindowPageAsync(browser) != "package:home" ||
                        !FindNamed<TextBlock>(window, "SelectedId").Text.Contains("ID  saya", StringComparison.Ordinal) ||
                        activeRenderer.CurrentResolvedSkin is not
                        {
                            Appearance: SkinAppearance.Dark,
                            Scene: SkinScene.Home,
                            Background: { ImageBytes: { Length: > 0 } }
                        })
                    {
                        return false;
                    }

                    var background = await ReadBackgroundProbeAsync(activeRenderer);
                    return background["imageLayer"] == "present";
                }, "Saya 在管理器跟随皮肤模式下完成深色首页和背景图渲染");
            CheckResolvedSkin(activeRenderer, SkinAppearance.Dark, SkinScene.Home, saya);
            var managerSayaBackground = await ReadBackgroundProbeAsync(activeRenderer);
            Check(managerSayaBackground["imageLayer"] == "present" &&
                  activeRenderer.CurrentResolvedSkin?.Background?.ImageBytes is { Length: > 0 },
                "管理器Saya预览保留背景图层与图像资源");
            await PrepareNeutralCaptureAsync(activeRenderer, browser);
            window.Width = 1240;
            window.Height = 840;
            window.UpdateLayout();
            await Task.Delay(180);
            await SaveManagerWindowScreenshotAsync(window, browser, Path.Combine(screenshotDirectory, "ManagerSaya.png"));

            var finalAyanamiRow = list.Items.Cast<object>().Single(item => GetRowId(item) == ayanami.Id);
            list.SelectedItem = finalAyanamiRow;
            await WaitUntilAsync(async () =>
                    await ReadWindowPageAsync(browser) == "package:home" &&
                    FindNamed<TextBlock>(window, "SelectedId").Text.Contains("ID  ayanami", StringComparison.Ordinal) &&
                    FindNamed<ItemsControl>(window, "ColorItems").Items.Count > 0 &&
                    FindNamed<TextBlock>(window, "PreviewStatus").Visibility == Visibility.Collapsed,
                "生成管理器截图前保留 Native、Ayanami 与 Saya 样例");
            await VerifyPreviewSuspendResumeAsync(window, browser, activeRenderer);
            await PrepareNeutralCaptureAsync(activeRenderer, browser);
            window.Width = 1240;
            window.Height = 840;
            window.UpdateLayout();
            await Task.Delay(200);
            await SaveManagerWindowScreenshotAsync(window, browser, Path.Combine(screenshotDirectory, "管理器-常规窗口.png"));
            window.Width = 960;
            window.Height = 640;
            window.UpdateLayout();
            await Task.Delay(200);
            await SaveManagerWindowScreenshotAsync(window, browser, Path.Combine(screenshotDirectory, "管理器-最小窗口-960x640.png"));
            await VerifyEditorWorkflowAsync(window, store, testRoot, screenshotDirectory);
            await VerifyPreviewReleaseAndReopenAsync(window);
        }
        finally
        {
            AllowTestWindowClose(window);
            window.Close();
        }
    }

    private static void AllowTestWindowClose(MainWindow window)
    {
        typeof(MainWindow).GetField("_allowWindowClose", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(window, true);
    }

    private static async Task VerifyFocusedComposerAsync(ThemePreviewRenderer renderer, ResolvedSkin expected)
    {
        var before = await ReadPreviewProbeAsync(renderer);
        var beforeBodySurface = await ReadComposerBodySurfaceAsync(renderer);
        await renderer.ExecuteScriptAsync("""
            (() => {
              window.__skinManagerSchedulerProbe = { raf: false, microtask: false, timer: false, visibility: document.visibilityState };
              requestAnimationFrame(() => { window.__skinManagerSchedulerProbe.raf = true; });
              queueMicrotask(() => { window.__skinManagerSchedulerProbe.microtask = true; });
              setTimeout(() => { window.__skinManagerSchedulerProbe.timer = true; }, 0);
              return true;
            })()
            """);
        await Task.Delay(100);
        var scheduler = await ReadObjectAsync(renderer, "({ raf: String(window.__skinManagerSchedulerProbe?.raf), microtask: String(window.__skinManagerSchedulerProbe?.microtask), timer: String(window.__skinManagerSchedulerProbe?.timer), visibility: String(window.__skinManagerSchedulerProbe?.visibility) })");
        var activeBefore = await ReadObjectAsync(renderer, "(() => { const editor=document.querySelector('[data-skin-part=\"composer.editor\"][contenteditable=\"true\"]'); return { active: String(document.activeElement===editor) }; })()");
        Console.WriteLine($"Scheduler probe: RAF={scheduler["raf"]}, microtask={scheduler["microtask"]}, setTimeout(0)={scheduler["timer"]}, visibility={scheduler["visibility"]}; active editor before focus={activeBefore["active"]}");
        await renderer.ExecuteScriptAsync("""
            (() => {
              window.__skinManagerFocusFrameReady = false;
              const editor = document.querySelector('[data-skin-part="composer.editor"][contenteditable="true"]');
              const owner = document.querySelector('[data-skin-part="composer"]');
              window.__skinManagerFocusEvents = [];
              for (const eventName of ['focus', 'focusin', 'blur', 'focusout']) {
                owner.addEventListener(eventName, event => window.__skinManagerFocusEvents.push(`${event.type}:${event.target?.getAttribute?.('data-skin-part') || event.target?.tagName || ''}`), true);
              }
              editor.focus();
              requestAnimationFrame(() => requestAnimationFrame(() => { window.__skinManagerFocusFrameReady = true; }));
              return true;
            })()
            """);
        await WaitUntilAsync(async () =>
        {
            var frame = await ReadObjectAsync(renderer, "({ ready: String(window.__skinManagerFocusFrameReady === true) })");
            return frame["ready"] == "true";
        }, "等待 composer focused 填充更新帧");
        var after = await ReadObjectAsync(renderer, """
            (() => {
              const composer = document.querySelector('[data-skin-part="composer"]');
              const body = composer.querySelector('[data-composer-body]');
              const editor = document.querySelector('[data-skin-part="composer.editor"][contenteditable="true"]');
              const manager = window.__SKIN_MANAGER_NATIVE_FILL__;
              const binding = manager?.bindings?.get(body);
              return {
                editable: String(editor.isContentEditable),
                focused: String(document.activeElement === editor),
                documentHasFocus: String(document.hasFocus()),
                focusWithin: String(composer.matches(':focus-within')),
                ownerContainsEditor: String(binding?.owner?.contains(editor) ?? false),
                ownerActive: String(binding?.owner?.matches(':active') ?? false),
                ownerDisabled: String(binding?.owner?.matches(':disabled, [aria-disabled="true"]') ?? false),
                ownerHover: String(binding?.owner?.matches(':hover') ?? false),
                focusEvents: JSON.stringify(window.__skinManagerFocusEvents || []),
                border: getComputedStyle(composer).borderTopColor,
                bodySurface: getComputedStyle(body).backgroundColor,
                bodyBorder: getComputedStyle(body).borderTopColor,
                bodyOpacity: getComputedStyle(body).opacity,
                composerBackground: getComputedStyle(composer).backgroundColor,
                fillConfig: JSON.stringify(manager?.payload?.fillOpacity?.composer ?? null),
                bodyManaged: String(manager?.elements?.has(body) ?? false),
                binding: binding ? `${binding.part}:${binding.owner === composer}:${binding.owner?.dataset?.skinPart || ''}` : '',
                ownerFocusWithin: String(binding?.owner?.matches(':focus-within') ?? false),
                inlineBackground: body.style.getPropertyValue('background-color'),
                inlinePriority: body.style.getPropertyPriority('background-color')
              };
            })()
            """);
        Check(after["editable"] == "true" && after["focused"] == "true" && after["focusWithin"] == "true",
            "實際 contenteditable 聚焦使 composer focused 狀態命中");
        Console.WriteLine($"Focus probe: root border {before["composerBorder"]} -> {after["border"]}; Body fill {beforeBodySurface} -> {after["bodySurface"]}; Body border={after["bodyBorder"]}, opacity={after["bodyOpacity"]}; composer fill={after["composerBackground"]}; manager.fillOpacity.composer={after["fillConfig"]}; managed={after["bodyManaged"]}; binding={after["binding"]}; documentHasFocus={after["documentHasFocus"]}; ownerContainsEditor={after["ownerContainsEditor"]}; owner states active={after["ownerActive"]},disabled={after["ownerDisabled"]},hover={after["ownerHover"]},focus-within={after["ownerFocusWithin"]}; events={after["focusEvents"]}; inline={after["inlineBackground"]}!{after["inlinePriority"]}");
        Check(before["composerBodyBorder"] != after["bodyBorder"], $"聚焦後 composer Body 邊框樣式發生變化：{before["composerBodyBorder"]} -> {after["bodyBorder"]}");
        var focusedBorder = expected.Components["composer"].States["focused"]["borderColor"].Text
            ?? throw new InvalidDataException("focused 樣式沒有 borderColor。");
        Check(after["bodyBorder"] == CssColorToBrowserValue(focusedBorder), "focused composer 實際套用 resolved borderColor");
        Check(beforeBodySurface != after["bodySurface"], "editor focus 更新 composer Body 的 background 與 fillOpacity");
        Check(expected.Components["composer"].States.ContainsKey("focused"), "controlled fixture 聲明 focused 樣式");
    }

    private static async Task VerifyButtonStatesAsync(WebView2 browser, ThemePreviewRenderer renderer, ResolvedSkin expected)
    {
        var bounds = await ReadObjectAsync(renderer, """
            (() => {
              const button = document.querySelector('button[data-skin-part="button.primary"]');
              button.disabled = false;
              const rect = button.getBoundingClientRect();
              return { x: String(rect.x + rect.width / 2), y: String(rect.y + rect.height / 2) };
            })()
            """);
        await browser.CoreWebView2!.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new
            {
                type = "mouseMoved",
                x = double.Parse(bounds["x"], System.Globalization.CultureInfo.InvariantCulture),
                y = double.Parse(bounds["y"], System.Globalization.CultureInfo.InvariantCulture)
            }));

        var hover = await ReadObjectAsync(renderer, """
            (() => {
              const button = document.querySelector('button[data-skin-part="button.primary"]');
              return {
                hovered: String(button.matches(':hover')),
                shadow: getComputedStyle(button).boxShadow,
                background: getComputedStyle(button).backgroundColor,
                color: getComputedStyle(button).color
              };
            })()
            """);
        Check(hover["hovered"] == "true" && hover["shadow"] != "none", "primary 按鈕 hover 樣式命中實際 DOM");

        var disabled = await ReadObjectAsync(renderer, """
            (() => {
              const button = document.querySelector('button[data-skin-part="button.primary"]');
              button.disabled = true;
              const cssRules = rules => Array.from(rules).flatMap(rule => [
                rule,
                ...(rule.cssRules ? cssRules(rule.cssRules) : [])
              ]);
              const buttonRules = cssRules(Array.from(document.styleSheets).flatMap(sheet => {
                try { return Array.from(sheet.cssRules); } catch { return []; }
              })).filter(rule => rule.selectorText?.includes('button.primary') && rule.selectorText?.includes(':disabled'))
                .map(rule => ({ selector: rule.selectorText, cssText: rule.cssText }));
              const inner = button.querySelector('span');
              return {
                disabled: String(button.matches(':disabled')),
                background: getComputedStyle(button).backgroundColor,
                color: getComputedStyle(button).color,
                opacity: getComputedStyle(button).opacity,
                filter: getComputedStyle(button).filter,
                innerColor: inner ? getComputedStyle(inner).color : '',
                innerOpacity: inner ? getComputedStyle(inner).opacity : '',
                inlineColor: button.style.getPropertyValue('color'),
                inlinePriority: button.style.getPropertyPriority('color'),
                outerHTML: button.outerHTML,
                rules: JSON.stringify(buttonRules)
              };
            })()
            """);
        Check(disabled["disabled"] == "true", "disabled 樣式對應實際 button:disabled 元素");
        var expectedDisabledBackground = expected.Components["button.primary"].States["disabled"]["background"].Text
            ?? throw new InvalidDataException("disabled 樣式沒有 background。");
        var expectedDisabledColor = expected.Components["button.primary"].States["disabled"]["color"].Text
            ?? throw new InvalidDataException("disabled 樣式沒有 color。");
        var expectedDisabledBackgroundCss = CssColorToBrowserValue(expectedDisabledBackground);
        var expectedDisabledColorCss = CssColorToBrowserValue(expectedDisabledColor);
        await WaitUntilAsync(async () =>
        {
            var current = await ReadObjectAsync(renderer, """
                (() => {
                  const button = document.querySelector('button[data-skin-part="button.primary"]');
                  return { background: getComputedStyle(button).backgroundColor, color: getComputedStyle(button).color };
                })()
                """);
            return current["background"] == expectedDisabledBackgroundCss && current["color"] == expectedDisabledColorCss;
        }, "等待 disabled 按鈕顏色 transition 完成");
        disabled = await ReadObjectAsync(renderer, """
            (() => {
              const button = document.querySelector('button[data-skin-part="button.primary"]');
              const cssRules = rules => Array.from(rules).flatMap(rule => [rule, ...(rule.cssRules ? cssRules(rule.cssRules) : [])]);
              const buttonRules = cssRules(Array.from(document.styleSheets).flatMap(sheet => {
                try { return Array.from(sheet.cssRules); } catch { return []; }
              })).filter(rule => rule.selectorText?.includes('button.primary') && rule.selectorText?.includes(':disabled'))
                .map(rule => ({ selector: rule.selectorText, cssText: rule.cssText }));
              const inner = button.querySelector('span');
              return {
                disabled: String(button.matches(':disabled')),
                background: getComputedStyle(button).backgroundColor,
                color: getComputedStyle(button).color,
                opacity: getComputedStyle(button).opacity,
                filter: getComputedStyle(button).filter,
                innerColor: inner ? getComputedStyle(inner).color : '',
                innerOpacity: inner ? getComputedStyle(inner).opacity : '',
                inlineColor: button.style.getPropertyValue('color'),
                inlinePriority: button.style.getPropertyPriority('color'),
                outerHTML: button.outerHTML,
                rules: JSON.stringify(buttonRules)
              };
            })()
            """);
        Check(hover["shadow"] != "none" && expected.Components["button.primary"].States.ContainsKey("disabled"),
            "controlled fixture 為真實按鈕配置 disabled 狀態");
        Console.WriteLine($"Disabled button probe: actual background={disabled["background"]}, color={disabled["color"]}, opacity={disabled["opacity"]}, filter={disabled["filter"]}, innerColor={disabled["innerColor"]}, innerOpacity={disabled["innerOpacity"]}, inlineColor={disabled["inlineColor"]}!{disabled["inlinePriority"]}; expected background={CssColorToBrowserValue(expectedDisabledBackground)}, color={CssColorToBrowserValue(expectedDisabledColor)}; outerHTML={disabled["outerHTML"]}; rules={disabled["rules"]}");
        Check(disabled["background"] == CssColorToBrowserValue(expectedDisabledBackground) &&
              disabled["color"] == CssColorToBrowserValue(expectedDisabledColor),
            "disabled button 實際套用 resolved disabled 顏色");
    }

    private static async Task RenderBaseAsync(ThemePreviewRenderer renderer, PreviewOptions options)
    {
        Check(await renderer.DisplayBaseAsync(options), "原生基礎頁導航完成");
    }

    private static async Task RenderPackageAsync(ThemePreviewRenderer renderer, SkinPackage package, PreviewOptions options)
    {
        Check(await renderer.DisplayPackageAsync(package, options), $"皮膚包 {package.Id} 預覽導航完成");
    }

    private static async Task WaitForPageAsync(ThemePreviewRenderer renderer, string mode, string scene)
    {
        var page = scene == "conversation" ? "task" : "home";
        await WaitUntilAsync(async () =>
        {
            var probe = await ReadPreviewProbeAsync(renderer);
            return probe["mode"] == mode && probe["activePage"] == page;
        }, $"預覽切換到 {mode}/{scene}");
    }

    private static async Task<Dictionary<string, string>> ReadPreviewProbeAsync(ThemePreviewRenderer renderer) =>
        await ReadObjectAsync(renderer, """
            (() => {
              const composer = document.querySelector('[data-skin-part="composer"]');
              const toolbar = composer?.querySelector('[data-skin-part="composer.toolbar"]');
              const body = composer?.querySelector('[data-composer-body]');
              const toolbarStyle = toolbar ? getComputedStyle(toolbar) : null;
              const rowsText = toolbarStyle?.gridTemplateRows || 'none';
              const rows = rowsText.split(/\s+/).filter(Boolean);
              return {
                mode: document.documentElement.getAttribute('data-skin-preview-mode') || '',
                activePage: document.querySelector('[data-fixture-page="task"]') ? 'task' :
                            document.querySelector('[data-fixture-page="home"]') ? 'home' : 'none',
                composerCount: String(document.querySelectorAll('[data-skin-part="composer"]').length),
                composerRows: String(rows.length),
                composerRowsText: rowsText,
                toolbarDisplay: toolbarStyle?.display || '',
                composerBorder: composer ? getComputedStyle(composer).borderTopColor : '',
                composerBodyBorder: body ? getComputedStyle(body).borderTopColor : ''
              };
            })()
            """);

    private static async Task<Dictionary<string, string>> ReadBackgroundProbeAsync(ThemePreviewRenderer renderer) =>
        await ReadObjectAsync(renderer, """
            (() => {
              const image = document.querySelector('.skin-manager-image');
              const overlay = document.querySelector('.skin-manager-overlay');
              const canvas = document.querySelector('#skin-manager-background');
              const imageStyle = image ? getComputedStyle(image) : null;
              const rawImageBackground = imageStyle?.backgroundImage || '';
              const imageBackground = rawImageBackground.startsWith('url("data:')
                ? 'url(data image; payload omitted)'
                : rawImageBackground.slice(0, 160);
              return {
                imageLayer: imageStyle && imageStyle.backgroundImage !== 'none' ? 'present' : 'none',
                imageOpacity: imageStyle ? imageStyle.opacity : '',
                overlayOpacity: overlay ? getComputedStyle(overlay).opacity : '',
                surface: canvas ? getComputedStyle(canvas).backgroundColor : ''
              };
            })()
            """);

    private static async Task<string> ReadComposerSurfaceAsync(ThemePreviewRenderer renderer)
    {
        var probe = await ReadObjectAsync(renderer, """
            (() => {
              const composer = document.querySelector('[data-skin-part="composer"]');
              return { surface: composer ? getComputedStyle(composer).backgroundColor : '' };
            })()
            """);
        return probe["surface"];
    }

    private static async Task<string> ReadComposerBodySurfaceAsync(ThemePreviewRenderer renderer)
    {
        var probe = await ReadObjectAsync(renderer, """
            (() => {
              const body = document.querySelector('[data-skin-part="composer"] [data-composer-body]');
              return { surface: body ? getComputedStyle(body).backgroundColor : '' };
            })()
            """);
        return probe["surface"];
    }

    private static Task<Dictionary<string, string>> ReadTokenSurfaceProbeAsync(ThemePreviewRenderer renderer) =>
        ReadObjectAsync(renderer, """
            (() => {
              const windowSurface = document.querySelector('[data-skin-part="window"]');
              const assistantSurface = document.querySelector('[data-skin-part="message.assistant"] > div');
              return {
                window: windowSurface ? getComputedStyle(windowSurface).backgroundColor : '',
                assistant: assistantSurface ? getComputedStyle(assistantSurface).backgroundColor : ''
              };
            })()
            """);

    private static async Task<Dictionary<string, string>> ReadObjectAsync(ThemePreviewRenderer renderer, string script)
    {
        var json = await renderer.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidDataException("預覽頁未返回 DOM 探針結果。");
    }

    private static async Task LogVisualEvidenceAsync(ThemePreviewRenderer renderer, string label)
    {
        var dom = await ReadObjectAsync(renderer, """
            (() => {
              const selectors = [
                ['window', '[data-skin-part="window"]'],
                ['content', '[data-skin-part="content"]'],
                ['sidebar', '[data-skin-part="sidebar"]'],
                ['home', '[data-skin-part="home"]'],
                ['home.hero', '[data-skin-part="home.hero"]'],
                ['projectList', '[data-skin-part="projectList"]'],
                ['composer', '[data-skin-part="composer"]'],
                ['message.user > div', '[data-skin-part="message.user"] > div'],
                ['message.assistant > div', '[data-skin-part="message.assistant"] > div'],
                ['codeBlock', '[data-skin-part="codeBlock"]'],
                ['header', '[data-skin-part="header"]'],
                ['conversation', '[data-skin-part="conversation"]'],
                ['dialog', '[data-skin-part="dialog"]']
              ];
              const parts = selectors.map(([name, selector]) => {
                const element = document.querySelector(selector);
                if (!element) return { name, found: false };
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return {
                  name, found: true, background: style.backgroundColor, color: style.color,
                  backgroundImage: style.backgroundImage,
                  opacity: style.opacity, zIndex: style.zIndex,
                  x: rect.x, y: rect.y, width: rect.width, height: rect.height
                };
              });
              const canvas = document.querySelector('#skin-manager-background');
              const image = document.querySelector('.skin-manager-image');
              const overlay = document.querySelector('.skin-manager-overlay');
              const composer = document.querySelector('[data-skin-part="composer"]');
              const composerStyle = composer ? getComputedStyle(composer) : null;
              const composerRect = composer?.getBoundingClientRect();
              const body = composer?.querySelector('[data-composer-body]');
              const flattenCssRules = ruleList => Array.from(ruleList).flatMap(rule => [rule, ...(rule.cssRules ? flattenCssRules(rule.cssRules) : [])]);
              const styleRules = flattenCssRules(Array.from(document.styleSheets).flatMap(sheet => {
                try { return Array.from(sheet.cssRules); } catch { return []; }
              }));
              const matchingBoxRules = element => styleRules.filter(rule => {
                if (!rule.selectorText || !/padding|spacing|margin|box-sizing/i.test(rule.style?.cssText || '')) return false;
                try { return element.matches(rule.selectorText); } catch { return false; }
              }).map(rule => ({ selector: rule.selectorText, cssText: rule.cssText }));
              const imageStyle = image ? getComputedStyle(image) : null;
              const imageRect = image?.getBoundingClientRect();
              const canvasRect = canvas?.getBoundingClientRect();
              const bodyStyle = body ? getComputedStyle(body) : null;
              const bodyRect = body?.getBoundingClientRect();
              const rawImageBackground = imageStyle?.backgroundImage || '';
              const imageBackground = rawImageBackground.startsWith('url("data:')
                ? 'url(data image; payload omitted)'
                : rawImageBackground.slice(0, 160);
              const stackAt = (x, y) => document.elementsFromPoint(x, y).slice(0, 5).map(element => {
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return {
                  tag: element.tagName, className: String(element.className || ''),
                  skinPart: element.getAttribute('data-skin-part') || '',
                  fixturePage: element.getAttribute('data-fixture-page') || '',
                  background: style.backgroundColor, opacity: style.opacity, zIndex: style.zIndex,
                  position: style.position, x: rect.x, y: rect.y, width: rect.width, height: rect.height
                };
              });
              return {
                parts: JSON.stringify(parts),
                stackCenter: JSON.stringify(stackAt(600, 300)),
                stackSidebar: JSON.stringify(stackAt(100, 300)),
                canvas: canvas ? JSON.stringify({
                  rect: { x: canvasRect.x, y: canvasRect.y, width: canvasRect.width, height: canvasRect.height },
                  background: getComputedStyle(canvas).backgroundColor,
                  zIndex: getComputedStyle(canvas).zIndex,
                  imageBackground,
                  imagePosition: imageStyle?.backgroundPosition || '',
                  imageSize: imageStyle?.backgroundSize || '',
                  imageOpacity: imageStyle?.opacity || '',
                  imageZIndex: imageStyle?.zIndex || '',
                  imageRect: imageRect ? { x: imageRect.x, y: imageRect.y, width: imageRect.width, height: imageRect.height } : null,
                  overlayOpacity: overlay ? getComputedStyle(overlay).opacity : '',
                  overlayZIndex: overlay ? getComputedStyle(overlay).zIndex : '',
                  composerMetrics: composer ? {
                    rect: { x: composerRect.x, y: composerRect.y, width: composerRect.width, height: composerRect.height },
                    clientWidth: composer.clientWidth,
                    scrollWidth: composer.scrollWidth,
                    boxSizing: composerStyle.boxSizing,
                    borderLeftWidth: composerStyle.borderLeftWidth,
                    borderRightWidth: composerStyle.borderRightWidth,
                    paddingLeft: composerStyle.paddingLeft,
                    paddingRight: composerStyle.paddingRight,
                    overflowX: composerStyle.overflowX,
                    directChildren: Array.from(composer.children).map(child => {
                      const childStyle = getComputedStyle(child);
                      const childRect = child.getBoundingClientRect();
                      return {
                        tag: child.tagName,
                        className: String(child.className || ''),
                        clientWidth: child.clientWidth,
                        scrollWidth: child.scrollWidth,
                        rect: { x: childRect.x, y: childRect.y, width: childRect.width, height: childRect.height },
                        boxSizing: childStyle.boxSizing,
                        borderLeftWidth: childStyle.borderLeftWidth,
                        borderRightWidth: childStyle.borderRightWidth,
                        paddingLeft: childStyle.paddingLeft,
                        paddingRight: childStyle.paddingRight,
                        minWidth: childStyle.minWidth,
                        width: childStyle.width,
                        marginLeft: childStyle.marginLeft,
                        marginRight: childStyle.marginRight,
                        overflowX: childStyle.overflowX
                      };
                    })
                  } : null,
                  bodyMetrics: body ? {
                    clientWidth: body.clientWidth,
                    scrollWidth: body.scrollWidth,
                    rect: bodyRect ? { x: bodyRect.x, y: bodyRect.y, width: bodyRect.width, height: bodyRect.height } : null,
                    boxSizing: bodyStyle.boxSizing,
                    borderLeftWidth: bodyStyle.borderLeftWidth,
                    borderRightWidth: bodyStyle.borderRightWidth,
                    paddingLeft: bodyStyle.paddingLeft,
                    paddingRight: bodyStyle.paddingRight,
                    spacing: bodyStyle.getPropertyValue('--spacing').trim(),
                    overflowX: bodyStyle.overflowX,
                    overflowDescendants: Array.from(body.querySelectorAll('*')).filter(element => {
                      const rect = element.getBoundingClientRect();
                      return rect.right > bodyRect.right + 0.5 || element.scrollWidth > element.clientWidth + 1;
                    }).map(element => {
                      const style = getComputedStyle(element);
                      const rect = element.getBoundingClientRect();
                      return {
                        tag: element.tagName,
                        className: String(element.className || ''),
                        skinPart: element.getAttribute('data-skin-part') || '',
                        gridTemplateColumns: style.gridTemplateColumns,
                        width: style.width,
                        minWidth: style.minWidth,
                        marginLeft: style.marginLeft,
                        marginRight: style.marginRight,
                        paddingLeft: style.paddingLeft,
                        paddingRight: style.paddingRight,
                        paddingInlineStart: style.paddingInlineStart,
                        paddingInlineEnd: style.paddingInlineEnd,
                        spacing: style.getPropertyValue('--spacing').trim(),
                        boxSizing: style.boxSizing,
                        rect: { x: rect.x, y: rect.y, width: rect.width, height: rect.height, right: rect.right },
                        clientWidth: element.clientWidth,
                        scrollWidth: element.scrollWidth,
                        offsetWidth: element.offsetWidth,
                        matchedBoxRules: /ComposerLayoutFooter|AdaptiveFooterInput/.test(String(element.className || ''))
                          ? JSON.stringify(matchingBoxRules(element))
                          : ''
                      };
                    })
                  } : null,
                  bodyBackground: bodyStyle?.backgroundColor || '',
                  bodyBorder: bodyStyle?.borderTopColor || '',
                  bodyRect: bodyRect ? { x: bodyRect.x, y: bodyRect.y, width: bodyRect.width, height: bodyRect.height } : null
                }) : '{}'
              };
            })()
            """);
        var resolved = renderer.CurrentResolvedSkin;
        var backgrounds = new
        {
            ImagePath = resolved?.Background?.ImagePath,
            Fit = resolved?.Background?.Fit,
            FocusX = resolved?.Background?.FocusX,
            FocusY = resolved?.Background?.FocusY,
            Opacity = resolved?.Background?.Opacity,
            OverlayColor = resolved?.Background?.OverlayColor,
            OverlayOpacity = resolved?.Background?.OverlayOpacity,
            ImageBytesLength = resolved?.Background?.ImageBytes?.Length
        };
        var components = resolved is null
            ? new Dictionary<string, object?>()
            : new[] { "window", "content", "sidebar", "home", "projectList", "composer", "composer.editor", "message.user", "message.assistant", "codeBlock", "header", "conversation", "dialog" }
                .Where(name => resolved.Components.ContainsKey(name))
                .ToDictionary(name => name, name => (object?)ReadResolvedComponentEvidence(resolved.Components[name]), StringComparer.Ordinal);
        Console.WriteLine($"視覺探針 {label}: {JsonSerializer.Serialize(new { dom, backgrounds, components })}");
    }

    private static object ReadResolvedComponentEvidence(ResolvedComponentStyle style) => new
    {
        Properties = style.Properties.ToDictionary(
            pair => pair.Key,
            pair => new { pair.Value.Kind, pair.Value.Text, pair.Value.Number, pair.Value.Shadow },
            StringComparer.Ordinal),
        States = style.States.ToDictionary(
            state => state.Key,
            state => state.Value.ToDictionary(
                pair => pair.Key,
                pair => new { pair.Value.Kind, pair.Value.Text, pair.Value.Number, pair.Value.Shadow },
                StringComparer.Ordinal),
            StringComparer.Ordinal)
    };

    private static async Task<Dictionary<string, string>> DecodeBackgroundImageAsync(ThemePreviewRenderer renderer)
    {
        await renderer.ExecuteScriptAsync("""
            (() => {
              const imageLayer = document.querySelector('.skin-manager-image');
              const imageBackground = imageLayer ? getComputedStyle(imageLayer).backgroundImage : 'none';
              const match = imageBackground.match(/^url\((?:"|')?(.*?)(?:"|')?\)$/);
              const source = match ? match[1] : '';
              globalThis.__skinManagerImageDecode = { state: source ? 'loading' : 'missing', width: '0', height: '0', source };
              if (!source) return true;
              const probe = new Image();
              probe.onload = () => probe.decode().then(() => {
                globalThis.__skinManagerImageDecode = {
                  state: 'decoded', width: String(probe.naturalWidth), height: String(probe.naturalHeight), source
                };
              }).catch(() => {
                globalThis.__skinManagerImageDecode = {
                  state: 'decode-error', width: String(probe.naturalWidth), height: String(probe.naturalHeight), source
                };
              });
              probe.onerror = () => {
                globalThis.__skinManagerImageDecode = {
                  state: 'load-error', width: String(probe.naturalWidth), height: String(probe.naturalHeight), source
                };
              };
              probe.src = source;
              return true;
            })()
            """);
        Dictionary<string, string>? result = null;
        await WaitUntilAsync(async () =>
        {
            result = await ReadObjectAsync(renderer, "globalThis.__skinManagerImageDecode || { state: 'pending', width: '0', height: '0', source: '' }");
            return result["state"] is "decoded" or "decode-error" or "load-error" or "missing";
        }, "等待浏览器解码背景图");
        return result!;
    }

    private static void CheckResolvedSkin(ThemePreviewRenderer renderer, SkinAppearance appearance, SkinScene scene, SkinPackage package)
    {
        var actual = renderer.CurrentResolvedSkin ?? throw new InvalidOperationException("Renderer 未提供 resolved skin。");
        var expected = SkinResolver.Resolve(package, appearance, scene);
        Check(actual.Appearance == appearance && actual.Scene == scene, $"Renderer resolved {appearance}/{scene}");
        Check(actual.Colors.Count == expected.Colors.Count && actual.Colors.All(pair => expected.Colors.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "renderer 色卡与 Core typed resolution 一致");
    }

    private static void CheckPanelOpacity(ThemePreviewRenderer renderer, SkinPackage package, double expectedOpacity)
    {
        var resolved = renderer.CurrentResolvedSkin ?? throw new InvalidOperationException("Renderer 未提供面板透明度解析结果。");
        Near(expectedOpacity, resolved.PanelOpacityOverride ?? double.NaN, 0.0001, "面板透明度传入 Core override");
        CheckBackgroundUnchanged(resolved, package);
    }

    private static void CheckBackgroundUnchanged(ResolvedSkin? actual, SkinPackage package)
    {
        var resolved = actual?.Background ?? throw new InvalidOperationException("Renderer 未保留皮肤背景定义。");
        var baseline = SkinResolver.Resolve(package, actual.Appearance, actual.Scene).Background
            ?? throw new InvalidOperationException("Core 基線解析沒有背景定義。");
        Check(resolved.ImageBytes is { Length: > 0 } && baseline.ImageBytes is { Length: > 0 } &&
              resolved.ImageBytes.AsSpan().SequenceEqual(baseline.ImageBytes), "手動面板透明度保留 resolved 壁紙原圖字節");
        Check(resolved.Opacity == baseline.Opacity, "手動面板透明度不覆蓋壁紙自身 alpha");
        Check(resolved.OverlayOpacity == baseline.OverlayOpacity, "手動面板透明度不覆蓋遮罩 alpha");
    }

    private static async Task SaveScreenshotAsync(ThemePreviewRenderer renderer, string path)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        await renderer.CapturePreviewAsync(output);
        await output.FlushAsync();
        Check(output.Length > 1_000, $"輸出預覽截圖 {Path.GetFileName(path)}");
        output.Position = 0;
        var signature = new byte[8];
        await output.ReadExactlyAsync(signature);
        Check(signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "預覽截圖是有效 PNG");
    }

    private static async Task SaveManagerWindowScreenshotAsync(MainWindow window, WebView2 browser, string path)
    {
        if (window.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("管理器窗口没有可捕获的客户区内容。");
        }

        window.UpdateLayout();
        content.UpdateLayout();
        var margin = content.Margin;
        var dpi = VisualTreeHelper.GetDpi(window);
        var windowWidth = window.ActualWidth;
        var windowHeight = window.ActualHeight;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(windowWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(windowHeight * dpi.DpiScaleY));
        var windowImage = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        windowImage.Render(window);
        windowImage.Freeze();

        await using var previewStream = new MemoryStream();
        var core = browser.CoreWebView2 ?? throw new InvalidOperationException("管理器預覽 WebView2 尚未初始化。");
        await core.CapturePreviewAsync(
            Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,
            previewStream);
        previewStream.Position = 0;
        var previewImage = BitmapFrame.Create(previewStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        previewImage.Freeze();

        var composition = new DrawingVisual();
        Rect browserBounds;
        using (var drawing = composition.RenderOpen())
        {
            drawing.DrawImage(windowImage, new Rect(0, 0, windowWidth, windowHeight));
            browserBounds = browser.TransformToAncestor(window)
                .TransformBounds(new Rect(new Point(0, 0), browser.RenderSize));
            drawing.DrawImage(previewImage, browserBounds);
        }

        var composedImage = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        composedImage.Render(composition);
        composedImage.Freeze();

        var clientWidth = content.ActualWidth + margin.Left + margin.Right;
        var clientHeight = content.ActualHeight + margin.Top + margin.Bottom;
        var cropWidth = Math.Max(1, (int)Math.Ceiling(clientWidth * dpi.DpiScaleX));
        var cropHeight = Math.Max(1, (int)Math.Ceiling(clientHeight * dpi.DpiScaleY));
        if (cropWidth > pixelWidth || cropHeight > pixelHeight)
        {
            throw new InvalidOperationException("管理器客户区裁切尺寸超出窗口捕获范围。");
        }
        var clientImage = new CroppedBitmap(composedImage, new Int32Rect(0, 0, cropWidth, cropHeight));
        clientImage.Freeze();
        Console.WriteLine($"管理器客户区截图：Window={windowWidth:0.#}×{windowHeight:0.#}，Content={content.ActualWidth:0.#}×{content.ActualHeight:0.#}，Browser={browserBounds.X:0.#},{browserBounds.Y:0.#} {browserBounds.Width:0.#}×{browserBounds.Height:0.#}，裁切={cropWidth}×{cropHeight}");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(clientImage));
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        await output.FlushAsync();
        Check(output.Length > 10_000, $"輸出管理器窗口截圖 {Path.GetFileName(path)}");
    }

    private static async Task SaveWindowScreenshotAsync(Window window, string path)
    {
        if (window.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("窗口没有可捕获的客户区内容。");
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
        Check(output.Length > 1_000, $"输出窗口客户区截图 {Path.GetFileName(path)}");
    }

    private static async Task VerifyPreviewSuspendResumeAsync(MainWindow window, WebView2 browser, ThemePreviewRenderer renderer)
    {
        var documentUri = browser.Source;
        var resolvedSkin = renderer.CurrentResolvedSkin;
        window.WindowState = WindowState.Minimized;
        await WaitUntilAsync(() => Task.FromResult(!GetPreviewActivityState(window)),
            "窗口最小化时停用预览活动状态");
        window.WindowState = WindowState.Normal;
        await WaitUntilAsync(() => Task.FromResult(
                GetPreviewActivityState(window) &&
                browser.Source == documentUri &&
                ReferenceEquals(renderer.CurrentResolvedSkin, resolvedSkin)),
            "恢复窗口后保留同一预览文档和解析状态");
    }

    private static async Task VerifyEditorWorkflowAsync(
        MainWindow window,
        SkinStore store,
        string testRoot,
        string screenshotDirectory)
    {
        FindNamed<Button>(window, "EditSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = FindNamed<SkinEditorView>(window, "SkinEditor");
        await WaitUntilAsync(() => Task.FromResult(editor.Visibility == Visibility.Visible), "打开可视化制作器");

        var motionTabs = FindNamed<TabControl>(editor, "EditorTabs");
        var backgroundTab = motionTabs.Items[1] as TabItem ??
            throw new InvalidDataException("找不到制作器背景页。");
        motionTabs.SelectedItem = backgroundTab;
        await WaitUntilAsync(() => Task.FromResult(backgroundTab.IsLoaded), "真实制作器背景页完成加载");
        await editor.Dispatcher.InvokeAsync(() => editor.UpdateLayout());
        var backgroundScroll = FindVisualChildren<ScrollViewer>(editor)
            .FirstOrDefault(viewer => viewer.IsVisible && viewer.ScrollableHeight > 0) ??
            throw new InvalidDataException("背景页没有创建滚动视口。");
        await WaitUntilAsync(() => Task.FromResult(backgroundScroll.ScrollableHeight > 0 &&
            SmoothScrollBehavior.IsAttached(backgroundScroll)), "背景页滚动视口接入平滑滚动");
        backgroundScroll.ScrollToVerticalOffset(0);
        var editorWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        };
        backgroundScroll.RaiseEvent(editorWheel);
        Check(editorWheel.Handled && SmoothScrollBehavior.IsAnimating(backgroundScroll),
            "真实制作器背景页的 PreviewMouseWheel 事件驱动平滑滚动");
        await WaitUntilAsync(() => Task.FromResult(!SmoothScrollBehavior.IsAnimating(backgroundScroll)),
            "制作器背景页滚动动画有界结束");

        var componentTab = motionTabs.Items[2] as TabItem ??
            throw new InvalidDataException("找不到制作器组件页。");
        motionTabs.SelectedItem = componentTab;
        await WaitUntilAsync(() => Task.FromResult(componentTab.IsLoaded), "真实制作器组件页完成加载");
        await editor.Dispatcher.InvokeAsync(() => editor.UpdateLayout());
        var componentScroll = FindVisualChildren<ScrollViewer>(editor)
            .FirstOrDefault(viewer => viewer.IsVisible && viewer.ScrollableHeight > 0) ??
            throw new InvalidDataException("组件页没有创建滚动视口。");
        await WaitUntilAsync(() => Task.FromResult(componentScroll.ScrollableHeight > 0 &&
            SmoothScrollBehavior.IsAttached(componentScroll)), "组件页滚动视口接入平滑滚动");
        Check(SmoothScrollBehavior.IsAttached(componentScroll), "真实制作器组件页已接入平滑滚动");

        motionTabs.SelectedIndex = 0;
        var metadataExpander = FindNamed<Expander>(editor, "MetadataExpander");
        await WaitUntilAsync(() => Task.FromResult(ExpanderMotionBehavior.IsAttached(metadataExpander)),
            "真实制作器皮肤信息折叠面板接入展开动画");
        metadataExpander.IsExpanded = true;
        await WaitUntilAsync(() => Task.FromResult(metadataExpander.IsExpanded &&
            metadataExpander.Content is UIElement { Opacity: 1 }), "皮肤信息面板已展开");
        var header = FindVisualChildren<ToggleButton>(metadataExpander).FirstOrDefault() ??
            throw new InvalidDataException("制作器皮肤信息面板没有生成可操作标题。");
        Check(PresentationSource.FromVisual(header) is not null, "制作器折叠标题已连接到主窗口");
        var collapseEvent = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            Source = header
        };
        metadataExpander.RaiseEvent(collapseEvent);
        Check(collapseEvent.Handled, "真实制作器皮肤信息面板处理标题折叠输入");
        await WaitUntilAsync(() => Task.FromResult(!metadataExpander.IsExpanded),
            "真实制作器皮肤信息折叠动画完成收起");

        var host = FindNamed<Grid>(window, "PreviewBrowserHost");
        var browser = FindNamed<WebView2>(window, "PreviewBrowser");
        Check(FindVisualChildren<WebView2>(host).Count() == 1,
            "制作器和管理器共用同一个 WebView2 预览控件");

        var renderer = GetWindowRenderer(window);
        await WaitUntilAsync(async () =>
            await renderer.ExecuteScriptAsync("window.__SKIN_MANAGER_COMPONENT_PICKER__?.enabled === true;") == "true",
            "本地预览进入组件选择模式");
        await renderer.ExecuteScriptAsync("document.querySelector('[data-skin-part=\"window\"]')?.click(); true;");
        await WaitUntilAsync(() => Task.FromResult(
            (FindNamed<ComboBox>(editor, "ComponentInput").SelectedValue as string) == "window"),
            "点击本地预览组件后切换组件编辑目标");

        var scopeInput = FindNamed<ComboBox>(editor, "ScopeInput");
        SelectEditorScope(scopeInput, "dark");
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Dark &&
            renderer.CurrentResolvedSkin?.Colors.TryGetValue("color.accent", out var accent) == true &&
            accent.Equals("#a0b7df", StringComparison.OrdinalIgnoreCase)),
            "切换深色变体后预览显示 Ayanami 深色覆盖");
        var darkAccentField = FindEditorField(FindNamed<ItemsControl>(editor, "TokenFields"), "color.accent");
        Check(darkAccentField.Value.Equals("#a0b7df", StringComparison.OrdinalIgnoreCase),
            "深色变体字段读取 variants.dark.tokens 的现有覆盖");
        darkAccentField.Value = string.Empty;
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Colors.TryGetValue("color.accent", out var inheritedAccent) == true &&
            inheritedAccent.Equals("#5875a7", StringComparison.OrdinalIgnoreCase)),
            "清除深色覆盖后预览恢复继承基础强调色");
        Check(((editor.Draft.Manifest["variants"] as JsonObject)?["dark"] as JsonObject)?["tokens"]?["color.accent"] is null,
            "清空字段会删除深色 patch 项而非写入空值");
        darkAccentField.Value = "#A0B7DF";
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Colors.TryGetValue("color.accent", out var restoredAccent) == true &&
            restoredAccent.Equals("#a0b7df", StringComparison.OrdinalIgnoreCase)),
            "恢復 Ayanami 深色強調色覆蓋");

        SelectEditorScope(scopeInput, "conversation");
        await WaitUntilAsync(async () =>
            renderer.CurrentResolvedSkin?.Scene == SkinScene.Conversation &&
            await ReadWindowPageAsync(browser) == "package:conversation",
            "切换对话场景范围后预览显示对话页面");
        var conversationAccentField = FindEditorField(FindNamed<ItemsControl>(editor, "TokenFields"), "color.accent");
        Check(conversationAccentField.Value.Length == 0, "未定义的对话场景强调色保持继承状态");
        conversationAccentField.Value = "#2CA38E";
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Scene == SkinScene.Conversation &&
            renderer.CurrentResolvedSkin?.Colors.TryGetValue("color.accent", out var sceneAccent) == true &&
            sceneAccent.Equals("#2CA38E", StringComparison.OrdinalIgnoreCase)),
            "对话场景输入写入 scenes.conversation 并更新预览");
        Check((((editor.Draft.Manifest["scenes"] as JsonObject)?["conversation"] as JsonObject)?["tokens"] as JsonObject)?["color.accent"]?.GetValue<string>() == "#2CA38E",
            "对话场景色值只写入 conversation patch");
        conversationAccentField.Value = string.Empty;
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Scene == SkinScene.Conversation &&
            renderer.CurrentResolvedSkin?.Colors.TryGetValue("color.accent", out var sceneInheritedAccent) == true &&
            sceneInheritedAccent.Equals("#5875a7", StringComparison.OrdinalIgnoreCase)),
            "清除对话场景色值后继续继承基础强调色");

        SelectEditorScope(scopeInput, "home");
        await WaitUntilAsync(async () =>
            renderer.CurrentResolvedSkin?.Scene == SkinScene.Home &&
            await ReadWindowPageAsync(browser) == "package:home",
            "切换首页场景范围后预览显示首页");
        SelectEditorScope(scopeInput, "light");
        await WaitUntilAsync(async () =>
            renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Light &&
            await ReadWindowPageAsync(browser) == "package:home",
            "切换浅色变体范围后预览回到浅色首页");
        var lightAccentField = FindEditorField(FindNamed<ItemsControl>(editor, "TokenFields"), "color.accent");
        Check(lightAccentField.Value.Length == 0 &&
              ((editor.Draft.Manifest["variants"] as JsonObject)?["light"] as JsonObject)?["tokens"]?["color.accent"] is null,
            "未定义的浅色项保持空白并继续继承基础值");
        SelectEditorScope(scopeInput, "base");
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Light &&
            renderer.CurrentResolvedSkin?.Scene == SkinScene.Home),
            "切回基础范围后预览恢复浅色首页");

        var tokenFields = FindNamed<ItemsControl>(editor, "TokenFields");
        var inheritedSurfaceField = FindEditorField(tokenFields, "color.surface.elevated");
        Check(inheritedSurfaceField.Value.Length == 0 &&
              ((editor.Draft.Manifest["tokens"] as JsonObject)?["color.surface.elevated"] is null),
            "基础范围缺失令牌以空值展示并继续继承，不把默认值写入清单");
        inheritedSurfaceField.Value = "#FFFFFF";
        Check((editor.Draft.Manifest["tokens"]?["color.surface.elevated"]?.GetValue<string>() == "#FFFFFF"),
            "对空白继承字段输入颜色后写入显式覆盖");
        inheritedSurfaceField.Value = string.Empty;
        Check((editor.Draft.Manifest["tokens"]?["color.surface.elevated"] is null),
            "清空继承字段会删除显式覆盖");

        var componentInput = FindNamed<ComboBox>(editor, "ComponentInput");
        componentInput.SelectedValue = "composer";
        var composerFields = FindNamed<ItemsControl>(editor, "ComponentFields");
        var composerRadiusField = FindEditorField(composerFields, "radius");
        var composerFillField = FindEditorField(composerFields, "fillOpacity");
        Check(composerRadiusField.Value == "{radius.control}" && composerFillField.Value == "{material.fillOpacity}",
            "Ayanami 组件圆角与填充透明度的 token 引用保留在编辑字段中");
        composerRadiusField.Value = "18";
        Check((editor.Draft.Manifest["components"]?["composer"]?["radius"] is JsonValue numericRadius &&
              numericRadius.TryGetValue<double>(out var storedRadius) && Math.Abs(storedRadius - 18) < 0.001),
            "把组件圆角 token 引用改成数字后写入 JSON number");
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Components.TryGetValue("composer", out var resolvedComposer) == true &&
            resolvedComposer.Properties.TryGetValue("radius", out var resolvedRadius) &&
            resolvedRadius.Number is { } radius && Math.Abs(radius - 18) < 0.001),
            "组件圆角数字类型正确解析并更新预览");
        composerRadiusField.Value = "{radius.control}";
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Components.TryGetValue("composer", out var restoredComposer) == true &&
            restoredComposer.Properties.TryGetValue("radius", out var restoredRadius) &&
            restoredRadius.Number is { } radius && Math.Abs(radius - 12) < 0.001),
            "组件圆角再次输入 token 引用后恢复令牌解析");
        composerFillField.Value = "60";
        Check((editor.Draft.Manifest["components"]?["composer"]?["fillOpacity"] is JsonValue numericFill &&
              numericFill.TryGetValue<double>(out var storedFill) && Math.Abs(storedFill - 0.4) < 0.001),
            "把组件填充透明度 token 引用改成 60% 后写入 0.4 alpha 数字");
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Components.TryGetValue("composer", out var filledComposer) == true &&
            filledComposer.Properties.TryGetValue("fillOpacity", out var filledOpacity) &&
            filledOpacity.Number is { } opacity && Math.Abs(opacity - 0.4) < 0.001),
            "组件 60% 透明度在预览中解析为 0.4 alpha");
        composerFillField.Value = "{material.fillOpacity}";
        composerRadiusField.Value = "invalid-number";
        var colorField = FindEditorField(tokenFields, "color.accent");
        colorField.Value = "#ABCDEF";
        Check((editor.Draft.Manifest["components"]?["composer"]?["radius"]?.GetValue<string>() == "invalid-number"),
            "编辑其它字段后仍保留无效数字文本，避免静默回退到旧值");
        FindNamed<Button>(editor, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(
            editor.Visibility == Visibility.Visible &&
            FindNamed<TextBlock>(editor, "ValidationErrorText").Visibility == Visibility.Visible &&
            store.LoadSkin("ayanami").Colors.TryGetValue("color.accent", out var unchangedAccent) &&
            unchangedAccent.Equals("#5875a7", StringComparison.OrdinalIgnoreCase)),
            "无效数字阻止保存且不改动皮肤库原包");
        composerRadiusField.Value = "{radius.control}";
        colorField.Value = "#5875A7";
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Colors.TryGetValue("color.accent", out var fixedAccent) == true &&
            fixedAccent.Equals("#5875a7", StringComparison.OrdinalIgnoreCase) &&
            FindNamed<TextBlock>(window, "PreviewStatus").Text == "预览草稿 · 点击组件可切换编辑目标"),
            "修正无效数字后恢复基础强调色和草稿预览");
        componentInput.SelectedValue = "window";

        var editorTabs = FindNamed<TabControl>(editor, "EditorTabs");
        editorTabs.SelectedIndex = 1;
        SelectEditorScope(scopeInput, "light");
        await WaitUntilAsync(() => Task.FromResult(renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Light),
            "背景透明度测试切换到浅色变体范围");
        var imageTransparencyField = FindEditorField(FindNamed<ItemsControl>(editor, "BackgroundFields"), "opacity");
        var overlayTransparencyField = FindEditorField(FindNamed<ItemsControl>(editor, "BackgroundFields"), "overlayOpacity");
        Check(imageTransparencyField.Value == "0" && overlayTransparencyField.Value == "92",
            "浅色变体的背景图片与覆盖层透明度以 0–100% 显示");
        imageTransparencyField.Value = "75";
        await WaitUntilAsync(async () =>
        {
            var background = await ReadBackgroundProbeAsync(renderer);
            return Math.Abs((renderer.CurrentResolvedSkin?.Background?.Opacity ?? double.NaN) - 0.25) < 0.001 &&
                   Math.Abs(double.Parse(background["imageOpacity"], System.Globalization.CultureInfo.InvariantCulture) - 0.25) < 0.01 &&
                   (((editor.Draft.Manifest["variants"] as JsonObject)?["light"] as JsonObject)?["background"] as JsonObject)?["opacity"]?.GetValue<double>() == 0.25;
        }, "浅色变体背景图片输入 75% 透明后写入 alpha 0.25 并更新预览");
        overlayTransparencyField.Value = "75";
        await WaitUntilAsync(async () =>
        {
            var background = await ReadBackgroundProbeAsync(renderer);
            return Math.Abs((renderer.CurrentResolvedSkin?.Background?.OverlayOpacity ?? double.NaN) - 0.25) < 0.001 &&
                   Math.Abs(double.Parse(background["overlayOpacity"], System.Globalization.CultureInfo.InvariantCulture) - 0.25) < 0.01 &&
                   (((editor.Draft.Manifest["variants"] as JsonObject)?["light"] as JsonObject)?["background"] as JsonObject)?["overlayOpacity"]?.GetValue<double>() == 0.25;
        }, "浅色变体覆盖层输入 75% 透明后写入 alpha 0.25 并更新预览");
        imageTransparencyField.Value = "0";
        overlayTransparencyField.Value = "92";
        await WaitUntilAsync(() => Task.FromResult(
            Math.Abs((renderer.CurrentResolvedSkin?.Background?.Opacity ?? double.NaN) - 1) < 0.001 &&
            Math.Abs((renderer.CurrentResolvedSkin?.Background?.OverlayOpacity ?? double.NaN) - 0.08) < 0.001),
            "恢复 Ayanami 原始背景透明度后预览恢复原样");
        SelectEditorScope(scopeInput, "base");
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Light &&
            renderer.CurrentResolvedSkin?.Scene == SkinScene.Home),
            "背景透明度验证结束后切回基础范围");

        editorTabs.SelectedIndex = 0;
        var pageBeforeEdit = browser.Source?.ToString();
        colorField = FindEditorField(FindNamed<ItemsControl>(editor, "TokenFields"), "color.accent");
        var alphaField = FindEditorField(FindNamed<ItemsControl>(editor, "TokenFields"), "material.fillOpacity");
        colorField.Value = "#D02090";
        alphaField.Value = "80";
        alphaField.Value = "75";

        await WaitUntilAsync(async () =>
        {
            var resolved = renderer.CurrentResolvedSkin;
            if (resolved is null || !resolved.Colors.TryGetValue("color.accent", out var accent) ||
                !accent.Equals("#D02090", StringComparison.OrdinalIgnoreCase) ||
                !resolved.Tokens.TryGetValue("material.fillOpacity", out var fillOpacity) ||
                fillOpacity.Number is not { } alpha || Math.Abs(alpha - 0.25) > 0.001)
            {
                return false;
            }

            var probe = await ReadObjectAsync(renderer, """
                (() => {
                  const composer = document.querySelector('[data-skin-part="composer"] [data-composer-body]');
                  const background = composer ? getComputedStyle(composer).backgroundColor : '';
                  const alpha = background.startsWith('rgba(')
                    ? Number(background.slice(background.lastIndexOf(',') + 1, -1).trim())
                    : 1;
                  return { background, alpha: String(alpha) };
                })()
                """);
            return Math.Abs(double.Parse(probe["alpha"], System.Globalization.CultureInfo.InvariantCulture) - 0.25) < 0.01;
        }, "制作器输入 75% 透明后，预览颜色与面板 alpha 按 0.25 实时更新");
        Check(browser.Source?.ToString() == pageBeforeEdit,
            "颜色和透明度编辑通过增量样式更新，未重新导航预览页面");

        FindNamed<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Tokens.TryGetValue("material.fillOpacity", out var undone) == true &&
            undone.Number is { } undoneAlpha && Math.Abs(undoneAlpha - 0.2) < 0.001),
            "撤销恢复面板 alpha 与对应预览");
        FindNamed<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Tokens.TryGetValue("material.fillOpacity", out var redone) == true &&
            redone.Number is { } redoneAlpha && Math.Abs(redoneAlpha - 0.25) < 0.001),
            "重做恢复面板 alpha 与对应预览");

        FindNamed<Button>(editor, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(
            editor.Visibility == Visibility.Collapsed &&
            FindNamed<TextBlock>(window, "PreviewStatus").Visibility == Visibility.Collapsed &&
            renderer.CurrentResolvedSkin?.Background?.ImageBytes is { Length: > 0 } &&
            store.LoadSkin("ayanami").Colors.TryGetValue("color.accent", out var savedAccent) &&
            savedAccent.Equals("#D02090", StringComparison.OrdinalIgnoreCase)),
            "保存草稿、完成选中预览更新并从皮肤库重新加载修改后的值");

        var exportPath = Path.Combine(testRoot, "editor-roundtrip.zip");
        store.ExportZip("ayanami", exportPath, overwriteExisting: true);
        var reimported = store.ReadZip(exportPath);
        var reimportedResolved = SkinResolver.Resolve(reimported, SkinAppearance.Light, SkinScene.Home);
        Check(reimportedResolved.Colors.TryGetValue("color.accent", out var reimportedAccent) &&
              reimportedAccent.Equals("#D02090", StringComparison.OrdinalIgnoreCase) &&
              reimported.Definition.Tokens.TryGetValue("material.fillOpacity", out var fillOpacity) &&
              fillOpacity.Number is { } savedAlpha && Math.Abs(savedAlpha - 0.25) < 0.001 &&
              SkinResolver.Resolve(reimported, SkinAppearance.Dark, SkinScene.Home).Colors.TryGetValue("color.accent", out var darkAccent) &&
              darkAccent.Equals("#a0b7df", StringComparison.OrdinalIgnoreCase),
            "制作器 75% 透明经保存导入为 0.25 alpha，深色变体字段保持一致");

        FindNamed<Button>(window, "EditSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(editor.Visibility == Visibility.Visible), "重新打开已保存皮肤的制作器");
        FindNamed<TabControl>(editor, "EditorTabs").SelectedIndex = 0;
        window.Width = 1240;
        window.Height = 840;
        window.UpdateLayout();
        FindNamed<Grid>(window, "DetailsScroll").UpdateLayout();
        await WaitUntilAsync(async () =>
        {
            var background = await ReadBackgroundProbeAsync(renderer);
            return await ReadWindowPageAsync(browser) == "package:home" &&
                   FindNamed<TextBlock>(window, "PreviewStatus").Text == "预览草稿 · 点击组件可切换编辑目标" &&
                   renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Light &&
                   renderer.CurrentResolvedSkin.Colors.TryGetValue("color.accent", out var accent) &&
                   accent.Equals("#D02090", StringComparison.OrdinalIgnoreCase) &&
                   renderer.CurrentResolvedSkin.Background?.ImageBytes is { Length: > 0 } &&
                   background["imageLayer"] == "present";
        },
            "制作器常规尺寸下显示同一浅色首页预览");
        await SaveManagerWindowScreenshotAsync(window, browser,
            Path.Combine(screenshotDirectory, "制作器-常规窗口.png"));
        window.Width = 960;
        window.Height = 640;
        window.UpdateLayout();
        await Task.Delay(200);
        await SaveManagerWindowScreenshotAsync(window, browser,
            Path.Combine(screenshotDirectory, "制作器-最小窗口-960x640.png"));

        var cancelField = FindEditorField(FindNamed<ItemsControl>(editor, "TokenFields"), "color.accent");
        cancelField.Value = "#00AACC";
        var clickNo = ClickDialogButtonWhenVisibleAsync("保留草稿修改？", 7);
        FindNamed<Button>(editor, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await clickNo;
        await WaitUntilAsync(() => Task.FromResult(editor.Visibility == Visibility.Collapsed),
            "取消制作器修改并离开编辑态");
        var afterCancel = store.LoadSkin("ayanami");
        Check(afterCancel.Colors.TryGetValue("color.accent", out var afterCancelAccent) &&
              afterCancelAccent.Equals("#D02090", StringComparison.OrdinalIgnoreCase),
            "取消未保存的制作器修改不会改动皮肤库原包");
    }

    private static async Task VerifyPreviewReleaseAndReopenAsync(MainWindow window)
    {
        FindNamed<Button>(window, "EditSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = FindNamed<SkinEditorView>(window, "SkinEditor");
        await WaitUntilAsync(() => Task.FromResult(
            editor.Visibility == Visibility.Visible &&
            FindNamed<TextBlock>(window, "PreviewStatus").Text == "预览草稿 · 点击组件可切换编辑目标"),
            "预览释放前进入可视化制作器");
        editor.SelectComponent("sidebar");

        var oldRenderer = GetWindowRenderer(window);
        var oldBrowser = FindNamed<WebView2>(window, "PreviewBrowser");
        FindNamed<Button>(window, "PreviewPowerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var host = FindNamed<Grid>(window, "PreviewBrowserHost");
        await WaitUntilAsync(() => Task.FromResult(
            FindNamed<StackPanel>(window, "PreviewClosedPanel").Visibility == Visibility.Visible &&
            FindVisualChildren<WebView2>(host).Count() == 0 &&
            typeof(MainWindow).GetField("_previewRenderer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) is null),
            "关闭预览时释放并移除已 Dispose 的 WebView2 与 renderer");

        var openButton = FindVisualChildren<Button>(FindNamed<StackPanel>(window, "PreviewClosedPanel"))
            .Single(button => button.Content?.ToString() == "打开预览");
        openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(async () =>
        {
            if (typeof(MainWindow).GetField("_previewRenderer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
                is not ThemePreviewRenderer newRenderer || ReferenceEquals(newRenderer, oldRenderer))
            {
                return false;
            }

            var newBrowser = FindNamed<WebView2>(window, "PreviewBrowser");
            return !ReferenceEquals(newBrowser, oldBrowser) && newBrowser.CoreWebView2 is not null &&
                   FindVisualChildren<WebView2>(host).Count() == 1 &&
                   await ReadWindowPageAsync(newBrowser) == "package:home" &&
                   await newRenderer.ExecuteScriptAsync("window.__SKIN_MANAGER_COMPONENT_PICKER__?.enabled === true;") == "true";
        }, "重新打开时创建独立 WebView2 / renderer 并恢复当前预览");

        var reopenedRenderer = GetWindowRenderer(window);
        await reopenedRenderer.ExecuteScriptAsync("document.querySelector('[data-skin-part=\"window\"]')?.click(); true;");
        await WaitUntilAsync(() => Task.FromResult(
            FindNamed<ComboBox>(editor, "ComponentInput").SelectedValue as string == "window"),
            "重开预览后点击仍在本地切换组件编辑目标");
        FindNamed<Button>(editor, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(editor.Visibility == Visibility.Collapsed),
            "完成预览重开验收并退出制作器");
    }

    private static SkinEditorView.SkinEditorField FindEditorField(ItemsControl items, string key) =>
        items.Items.Cast<SkinEditorView.SkinEditorField>()
            .Single(field => field.Path.Count > 1 && string.Equals(field.Path[^1], key, StringComparison.Ordinal));

    private static void SelectEditorScope(ComboBox input, string scope)
    {
        input.SelectedItem = input.Items.Cast<ComboBoxItem>()
            .Single(item => string.Equals(item.Tag?.ToString(), scope, StringComparison.Ordinal));
    }

    private static async Task ClickDialogButtonWhenVisibleAsync(string title, int buttonId)
    {
        var expectedProcessId = (uint)Environment.ProcessId;
        await Task.Run(async () =>
        {
            var timeout = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < timeout)
            {
                var dialog = FindDialogInProcess(title, expectedProcessId);
                if (dialog != IntPtr.Zero)
                {
                    var button = GetDlgItem(dialog, buttonId);
                    if (button != IntPtr.Zero)
                    {
                        _ = SendMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                        return;
                    }
                }

                await Task.Delay(40);
            }

            throw new TimeoutException($"找不到对话框按钮：{title} / {buttonId}");
        });
    }

    private static IntPtr FindDialogInProcess(string title, uint expectedProcessId)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId != expectedProcessId)
            {
                return true;
            }

            var text = new System.Text.StringBuilder(256);
            _ = GetWindowText(window, text, text.Capacity);
            if (string.Equals(text.ToString(), title, StringComparison.Ordinal))
            {
                result = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int controlId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static ResourceDictionary LoadOfflineApplicationResources()
    {
        var repositoryDirectory = FindRepositoryDirectory();
        var appPath = Path.Combine(repositoryDirectory.FullName, "src", "SkinManager.Desktop", "App.xaml");
        var document = XDocument.Load(appPath);
        var root = document.Root ?? throw new InvalidDataException("App.xaml 缺少根元素。");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = root.Element(presentation + "Application.Resources")
            ?? throw new InvalidDataException("App.xaml 缺少 Application.Resources。");
        var dictionary = new XElement(presentation + "ResourceDictionary");
        foreach (var attribute in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            dictionary.Add(new XAttribute(attribute));
        }

        foreach (var child in resources.Elements())
        {
            dictionary.Add(new XElement(child));
        }

        return XamlReader.Parse(dictionary.ToString(SaveOptions.DisableFormatting)) as ResourceDictionary
            ?? throw new InvalidDataException("无法加载离线预览资源字典。");
    }

    private static bool GetPreviewActivityState(MainWindow window) =>
        typeof(MainWindow).GetField("_previewWasActive", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) is true;

    private static async Task PrepareNeutralCaptureAsync(ThemePreviewRenderer renderer, WebView2 browser)
    {
        await renderer.ExecuteScriptAsync("document.querySelector('[data-skin-part=\"composer.editor\"][contenteditable=\"true\"]')?.blur(); true;");
        await browser.CoreWebView2!.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new { type = "mouseMoved", x = -10, y = -10 }));
        await WaitUntilAsync(async () =>
        {
            var state = await ReadObjectAsync(renderer, """
                (() => {
                  const editor = document.querySelector('[data-skin-part="composer.editor"][contenteditable="true"]');
                  const runningAnimations = document.getAnimations().filter(animation =>
                    animation.playState === 'running' && Number.isFinite(animation.effect?.getTiming().iterations));
                  return {
                    fontsLoaded: String(document.fonts.status === 'loaded'),
                    editorFocused: String(Boolean(editor && document.activeElement === editor)),
                    runningAnimations: String(runningAnimations.length)
                  };
                })()
                """);
            return state["fontsLoaded"] == "true" && state["editorFocused"] == "false" && state["runningAnimations"] == "0";
        }, "截图前固定鼠标、编辑器、字体与过渡状态");
    }

    private static bool PngPixelsMatch(string leftPath, string rightPath)
    {
        var left = LoadPixels(leftPath);
        var right = LoadPixels(rightPath);
        if (left.Width != right.Width || left.Height != right.Height)
        {
            return false;
        }

        if (left.Bytes.AsSpan().SequenceEqual(right.Bytes))
        {
            return true;
        }

        var differingPixels = 0;
        var firstDifferentPixel = -1;
        for (var offset = 0; offset < left.Bytes.Length; offset += 4)
        {
            if (!left.Bytes.AsSpan(offset, 4).SequenceEqual(right.Bytes.AsSpan(offset, 4)))
            {
                differingPixels++;
                firstDifferentPixel = firstDifferentPixel < 0 ? offset / 4 : firstDifferentPixel;
            }
        }

        var firstX = firstDifferentPixel % left.Width;
        var firstY = firstDifferentPixel / left.Width;
        Console.WriteLine($"PNG 像素差异：{Path.GetFileName(leftPath)} vs {Path.GetFileName(rightPath)}，{differingPixels} 个像素不同，首个位置({firstX},{firstY})，rgba {PixelToString(left.Bytes, firstDifferentPixel * 4)} -> {PixelToString(right.Bytes, firstDifferentPixel * 4)}");
        return false;
    }

    private static string PixelToString(byte[] pixels, int offset) =>
        $"{pixels[offset + 2]},{pixels[offset + 1]},{pixels[offset]},{pixels[offset + 3]}";

    private static (int Width, int Height, byte[] Bytes) LoadPixels(string path)
    {
        using var stream = File.OpenRead(path);
        var source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return (converted.PixelWidth, converted.PixelHeight, pixels);
    }

    private static SkinPackage CreateStateFixture(
        string directory,
        string id = "preview-state-fixture",
        bool noneAssistant = false,
        string? assistantColor = null)
    {
        Directory.CreateDirectory(directory);
        var definition = JsonNode.Parse("""
            {
              "formatVersion": 1,
              "id": "preview-state-fixture",
              "name": "预览状态夹具",
              "version": "1.0.0",
              "appearance": "light",
              "tokens": {
                "color.surface.window": "#f5f1eb",
                "color.surface.sidebar": "#e9e2d8",
                "color.surface.elevated": "#fffaf4",
                "color.surface.input": "rgba(255,255,255,0.72)",
                "color.surface.userMessage": "#cc6039",
                "color.surface.assistantMessage": "#fffaf4",
                "color.surface.code": "#eee8df",
                "color.text.primary": "#29231e",
                "color.text.secondary": "#625850",
                "color.text.disabled": "#92867c",
                "color.text.link": "#a84421",
                "color.border.default": "#b8aa9b",
                "color.border.focus": "#ed5a2d",
                "color.border.error": "#c52733",
                "color.accent": "#d94f26",
                "border.width": 1,
                "material.fillOpacity": 0.72,
                "shadow.panel": { "x": 0, "y": 3, "blur": 9, "spread": 0, "color": "#22150f", "opacity": 0.28 }
              },
              "components": {
                "window": { "background": "{color.surface.window}", "color": "{color.text.primary}" },
                "sidebar": { "background": "{color.surface.sidebar}", "borderRightColor": "{color.border.default}", "borderRightWidth": "{border.width}" },
                "composer": {
                  "background": "{color.surface.input}",
                  "fillOpacity": "{material.fillOpacity}",
                  "borderColor": "{color.border.default}",
                  "borderWidth": "{border.width}",
                  "states": { "focused": {
                    "background": "{color.surface.userMessage}",
                    "fillOpacity": 0.18,
                    "borderColor": "{color.border.focus}"
                  } }
                },
                "composer.editor": { "color": "{color.text.primary}" },
                "message.assistant": { "background": "{color.surface.assistantMessage}", "shadow": "{shadow.panel}" },
                "button.primary": {
                  "background": "{color.accent}",
                  "color": "{color.text.primary}",
                  "states": {
                    "hover": { "shadow": "{shadow.panel}" },
                    "disabled": { "background": "{color.surface.elevated}", "color": "{color.text.disabled}" }
                  }
                }
              },
              "background": { "color": "#f7f3ee" },
              "scenes": {
                "home": { "components": { "composer": { "background": "#e8dfd4" } } },
                "conversation": { "components": { "composer": { "background": "#f6e6d6" } } }
              }
            }
            """
        )?.AsObject() ?? throw new InvalidDataException("無法建立控制型狀態夾具。");
        definition["id"] = id;
        definition["name"] = id == "preview-state-fixture" ? "预览状态夹具" : "none 样式夹具";
        if (noneAssistant)
        {
            definition["scenes"]!["conversation"]!["components"]!["message.assistant"] = new JsonObject
            {
                ["background"] = "none",
                ["shadow"] = "none"
            };
        }

        if (assistantColor is not null)
        {
            definition["components"]!["message.assistant"]!["color"] = assistantColor;
        }

        File.WriteAllText(Path.Combine(directory, "skin.json"), definition.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        return SkinPackageReader.ReadFolder(directory);
    }

    private static SkinPackage CreateDeclaredDarkFixture(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = """
            {
              "formatVersion": 1,
              "id": "declared-dark",
              "name": "声明深色",
              "version": "2.3.3",
              "appearance": "dark",
              "tokens": {
                "color.surface.window": "#202124",
                "color.text.primary": "#f3f4f6"
              }
            }
            """;
        File.WriteAllText(Path.Combine(directory, "skin.json"), definition, new System.Text.UTF8Encoding(false));
        return SkinPackageReader.ReadFolder(directory);
    }

    private static SkinPackage CreateSystemAppearanceFixture(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = """
            {
              "formatVersion": 1,
              "id": "system-appearance-fixture",
              "name": "系统外观夹具",
              "version": "2.3.3",
              "appearance": "system",
              "tokens": {
                "color.surface.window": "#f5f7fb",
                "color.text.primary": "#202b3e"
              }
            }
            """;
        File.WriteAllText(Path.Combine(directory, "skin.json"), definition, new System.Text.UTF8Encoding(false));
        return SkinPackageReader.ReadFolder(directory);
    }

    private static byte[] ReadEmbeddedPackage(string name)
    {
        var resourceName = $"SkinManager.Preview.Tests.Resources.{name}.zip";
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到嵌入的 {name} 样例包。");
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static Window CreatePreviewWindow(UIElement content, double width, double height) => new()
    {
        Content = content,
        Left = Math.Max(0, (SystemParameters.WorkArea.Width - width) / 2),
        Top = Math.Max(0, (SystemParameters.WorkArea.Height - height) / 2),
        Width = width,
        Height = height,
        ShowActivated = true,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize
    };

    private static void RaiseKeyDown(TextBox input, Key key)
    {
        var source = PresentationSource.FromVisual(input) ??
            throw new InvalidOperationException("透明度输入控件尚未连接到 WPF 窗口。");
        input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static void RaiseLostKeyboardFocus(TextBox input, Window window)
    {
        input.RaiseEvent(new KeyboardFocusChangedEventArgs(
            Keyboard.PrimaryDevice, Environment.TickCount, input, window)
        {
            RoutedEvent = Keyboard.LostKeyboardFocusEvent
        });
    }

    private static void InvokeWindowMethod(MainWindow window, string name, params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, name);
        method.Invoke(window, arguments);
    }

    private static async Task<string> ReadWindowPageAsync(WebView2 browser)
    {
        if (browser.CoreWebView2 is null)
        {
            return string.Empty;
        }

        var json = await browser.CoreWebView2.ExecuteScriptAsync("""
            (() => `${document.documentElement.getAttribute('data-skin-preview-mode') || ''}:${
              document.querySelector('[data-fixture-page="task"]') ? 'conversation' : 'home'
            }`)()
            """);
        return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }

    private static T FindNamed<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"找不到窗口控件 {name}。");

    private static T? FindNamedVisual<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && string.Equals(element.Name, name, StringComparison.Ordinal))
        {
            return element;
        }

        if (root is not Visual)
        {
            return null;
        }

        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < children; index++)
        {
            var match = FindNamedVisual<T>(VisualTreeHelper.GetChild(root, index), name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        if (root is not Visual)
        {
            yield break;
        }

        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < children; index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private static string GetRowId(object row)
    {
        var entry = row.GetType().GetProperty("Entry", BindingFlags.Instance | BindingFlags.Public)?.GetValue(row);
        return entry?.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)?.GetValue(entry)?.ToString()
            ?? throw new InvalidOperationException("皮肤列表行没有轻量目录项 ID。");
    }

    private static string GetColorValue(ItemsControl items, string key)
    {
        return TryGetColorValue(items, key)
            ?? throw new InvalidOperationException($"色卡中没有令牌 {key}。");
    }

    private static string? TryGetColorValue(ItemsControl items, string key)
    {
        foreach (var item in items.Items)
        {
            var type = item.GetType();
            var rawName = type.GetProperty("RawName", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item)?.ToString();
            if (string.Equals(rawName, key, StringComparison.Ordinal))
            {
                return type.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item)?.ToString()
                    ?? string.Empty;
            }
        }

        return null;
    }

    private static string CssColorToBrowserValue(string value)
    {
        if (!CssColorParser.TryParse(value, out var color))
        {
            throw new InvalidDataException($"無法轉換測試顏色值 {value}。");
        }

        return color.Alpha == byte.MaxValue
            ? $"rgb({color.Red}, {color.Green}, {color.Blue})"
            : $"rgba({color.Red}, {color.Green}, {color.Blue}, {(color.Alpha / 255d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
    }

    private static double ContrastRatio(string foreground, string background)
    {
        if (!CssColorParser.TryParse(foreground, out var foregroundColor) ||
            !CssColorParser.TryParse(background, out var backgroundColor) ||
            foregroundColor.Alpha != byte.MaxValue || backgroundColor.Alpha != byte.MaxValue)
        {
            throw new InvalidDataException("对比度验收需要有效的不透明颜色。" );
        }

        var foregroundLuminance = RelativeLuminance(foregroundColor);
        var backgroundLuminance = RelativeLuminance(backgroundColor);
        var brighter = Math.Max(foregroundLuminance, backgroundLuminance);
        var darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (brighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(CssRgba color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.Red) + 0.7152 * Linearize(color.Green) + 0.0722 * Linearize(color.Blue);
    }

    private static ThemePreviewRenderer GetWindowRenderer(MainWindow window) =>
        typeof(MainWindow).GetField("_previewRenderer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as ThemePreviewRenderer
        ?? throw new InvalidOperationException("管理器窗口没有暴露预览渲染器。");

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, string label)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limit)
        {
            if (await predicate())
            {
                Console.WriteLine($"通过：{label}");
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"等待超时：{label}");
    }

    private static void Near(double expected, double actual, double tolerance, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidDataException($"{label}：预期约 {expected:0.###}，实际 {actual:0.###}。");
        }

        Console.WriteLine($"通过：{label} = {actual:0.###}");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{label}失败。");
        }

        Console.WriteLine($"通过：{label}");
    }

    internal static string FindTestOutputDirectory()
    {
        return Path.Combine(FindRepositoryDirectory().FullName, "artifacts", "test-results", "preview");
    }

    private static DirectoryInfo FindRepositoryDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SkinManager.sln")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory;
            }
        }

        throw new DirectoryNotFoundException("无法定位仓库根目录。");
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 8 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt < 7)
                {
                    Thread.Sleep(250);
                }
            }
        }
    }

    private static CodexInstance CreateFakeInstance(
        int processId,
        int? debugPort,
        CodexInstanceKind kind,
        bool canConnect,
        string windowTitle) => new(
            new CodexProcessIdentity(
                processId,
                new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero).AddSeconds(processId),
                $"C:\\FakeCodex\\ChatGPT-{processId}.exe"),
            windowTitle,
            processId * 16L,
            debugPort,
            $"C:\\FakeCodex\\Profile-{processId}")
        {
            Kind = kind,
            CanConnect = canConnect
        };

    private sealed class FakeCodexInstanceService(IEnumerable<CodexInstance> instances) : ICodexInstanceService
    {
        public IReadOnlyList<CodexInstance> Instances { get; set; } = instances.ToArray();
        public IReadOnlyList<CodexInstance>? InstancesAfterNextCloseFailure { get; set; }
        public List<CodexInstance> LocateCalls { get; } = [];
        public List<(CodexInstance Instance, bool Force)> CloseCalls { get; } = [];
        public Queue<CodexCloseResult> CloseResults { get; } = new();
        public Exception? NextCloseException { get; set; }
        public CodexLocateResult LocateResult { get; set; } = CodexLocateResult.Located;

        public Task<IReadOnlyList<CodexInstance>> GetInstancesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Instances);
        }

        public Task<CodexLocateResult> LocateAsync(CodexInstance instance, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocateCalls.Add(instance);
            return Task.FromResult(LocateResult);
        }

        public Task<CodexCloseOperationResult> CloseAsync(CodexInstance instance, bool force = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseCalls.Add((instance, force));
            if (NextCloseException is { } exception)
            {
                NextCloseException = null;
                if (InstancesAfterNextCloseFailure is { } updatedInstances)
                {
                    Instances = updatedInstances;
                    InstancesAfterNextCloseFailure = null;
                }
                return Task.FromException<CodexCloseOperationResult>(exception);
            }

            var result = CloseResults.Count > 0 ? CloseResults.Dequeue() : CodexCloseResult.Closed;
            if (result == CodexCloseResult.Closed)
            {
                Instances = Instances.Where(candidate => candidate.Identity != instance.Identity).ToArray();
                return Task.FromResult(new CodexCloseOperationResult(result, instance with { PendingExitSnapshot = null }, "fake 所选实例已退出。"));
            }

            if (result is CodexCloseResult.StillRunning or CodexCloseResult.NativeQuitUnavailable or CodexCloseResult.Incomplete)
            {
                var snapshot = instance.PendingExitSnapshot ?? new CodexProcessExitSnapshot(
                    instance.Identity,
                    [new CodexProcessTreeEntry(instance.Identity, 0, null, 0)],
                    Array.Empty<int>(),
                    result == CodexCloseResult.Incomplete ? "fake 仍有进程未确认" : null);
                var pending = instance with { PendingExitSnapshot = snapshot };
                return Task.FromResult(new CodexCloseOperationResult(result, pending,
                    result == CodexCloseResult.NativeQuitUnavailable
                        ? "所选实例没有可读取的原生 Quit 菜单命令。可单独确认强制结束已核验进程。"
                        : result == CodexCloseResult.Incomplete
                            ? "fake 强制结束后仍有进程未确认。"
                            : "fake 正常退出后实例仍在运行。"));
            }

            return Task.FromResult(new CodexCloseOperationResult(result, instance, "fake 身份已变化。"));
        }
    }

    private sealed class FakeUpdateClient(string downloadDirectory, Version? releaseVersion = null) : IUpdateClient
    {
        private static string LongReleaseNotes => "<script>alert('plain text')</script>\n更新发布说明。\n" +
            string.Join(Environment.NewLine, Enumerable.Range(1, 80).Select(index => $"更新条目 {index}：修复预览与安装交接细节。"));

        private static UpdateRelease CreateRelease(Version? version = null)
        {
            version ??= new Version(2, 3, 0);
            return new UpdateRelease(
            version,
            $"v{version.ToString(3)}",
            LongReleaseNotes,
            new Uri($"https://updates.invalid/SkinManager-Setup-{version.ToString(3)}.exe"),
            4,
            "0000000000000000000000000000000000000000000000000000000000000000");
        }

        public UpdateRelease Release { get; } = CreateRelease(releaseVersion);

        public UpdateCheckResult CheckResult { get; set; } = new(UpdateCheckState.Available, CreateRelease(releaseVersion));

        public int CheckCount { get; private set; }
        public int DownloadCount { get; private set; }
        public int ValidationCount { get; private set; }
        public bool BlockNextDownload { get; set; }
        public Exception? ValidationError { get; set; }
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
        {
            CheckCount++;
            return Task.FromResult(CheckResult);
        }

        public async Task<DownloadedUpdate> DownloadAsync(
            UpdateRelease release,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            DownloadStarted.TrySetResult();
            if (BlockNextDownload)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Directory.CreateDirectory(downloadDirectory);
            var filePath = Path.Combine(downloadDirectory, $"setup-{Guid.NewGuid():N}.exe");
            progress?.Report(new UpdateDownloadProgress(2, release.Size));
            await File.WriteAllBytesAsync(filePath, new byte[] { 0x4D, 0x5A, 0x00, 0x00 }, cancellationToken);
            progress?.Report(new UpdateDownloadProgress(release.Size, release.Size));
            return new DownloadedUpdate(release, filePath);
        }

        public Task ValidateAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
        {
            ValidationCount++;
            return ValidationError is null ? Task.CompletedTask : Task.FromException(ValidationError);
        }
    }

    private sealed class FakeInstallerLauncher : IInstallerLauncher
    {
        public bool ShouldStart { get; set; } = true;
        public int LaunchCount { get; private set; }
        public string? InstallerPath { get; private set; }
        public string? InstallDirectory { get; private set; }

        public bool Launch(string installerPath, string installDirectory)
        {
            LaunchCount++;
            InstallerPath = installerPath;
            InstallDirectory = installDirectory;
            return ShouldStart;
        }
    }
}

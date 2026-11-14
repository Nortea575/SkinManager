using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Market;
using SkinManager.Runtime;

namespace SkinManager.Preview.Tests;

internal static class Beta2UiChecks
{
    private const string SkinOrderDragFormat = "SkinManager.SkinId";

    internal static async Task RunAsync()
    {
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"SkinManager-beta2-ui-{Guid.NewGuid():N}"));
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!testRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Beta 2 UI 检查的临时目录超出系统临时目录。");
        }

        Directory.CreateDirectory(testRoot);
        AppThemeService.Initialize(Path.Combine(testRoot, "preferences", "app-preferences.json"));
        AppThemeService.SetMode(AppThemeMode.Night);
        try
        {
            StartupPreferencesChecks.RunServiceChecks(testRoot);
            VerifyPackagedFontGlyphs();
            await VerifyInitialSkinAndSearchApplyPersistenceAsync(testRoot);
            await VerifyMissingAutostartSkinAsync(testRoot);
            await VerifyAutostartAppliesSavedSkinAsync(testRoot);
            await VerifyDirectCardReorderingAsync(testRoot);
            await VerifyThemeAndEntrySurfacesAsync(testRoot);
            await VerifyWeakConnectionSurvivesRefreshAsync(testRoot);
            Console.WriteLine("通过：Beta 2 UI、双主题、弹层、字体字形、入口与弱 CDP 刷新检查");
        }
        finally
        {
            AppThemeService.SetMode(AppThemeMode.Night);
            TryDeleteDirectory(testRoot);
        }
    }

    private static void VerifyPackagedFontGlyphs()
    {
        var fontLocation = new Uri(
            $"pack://application:,,,/{typeof(MainWindow).Assembly.GetName().Name};component/Fonts/",
            UriKind.Absolute);
        var families = Fonts.GetFontFamilies(fontLocation).ToArray();
        var spaceGrotesk = families.FirstOrDefault(family =>
            family.FamilyNames.Values.Contains("Space Grotesk", StringComparer.OrdinalIgnoreCase));
        var notoSans = families.FirstOrDefault(family =>
            family.FamilyNames.Values.Contains("Noto Sans SC", StringComparer.OrdinalIgnoreCase));
        Require(spaceGrotesk is not null && notoSans is not null,
            "程序集内包含 Space Grotesk 与 Noto Sans SC 两个正式字体族");

        var spaceGlyphs = ReadPackagedGlyphTypeface(spaceGrotesk!, 'A');
        Require(spaceGlyphs.Any(glyph => glyph.FontUri?.ToString().Contains("SpaceGrotesk", StringComparison.OrdinalIgnoreCase) == true),
            "Space Grotesk 内嵌字形可绘制拉丁字母 A");
        var notoCodepoints = new[] { 0x76AE, 0x80A4, 0x8BBE, 0x7F6E };
        var notoGlyphs = ReadPackagedGlyphTypefaces(notoSans!);
        Require(notoGlyphs.Any(glyph =>
                glyph.FontUri?.ToString().Contains("NotoSansSC", StringComparison.OrdinalIgnoreCase) == true &&
                notoCodepoints.All(codepoint => glyph.CharacterToGlyphMap.TryGetValue(codepoint, out var index) && index != 0)),
            "Noto Sans SC 内嵌字形可绘制‘皮肤’与‘设置’全部汉字");

        var bodyFont = Application.Current.FindResource("KlayBodyFont") as FontFamily;
        Require(bodyFont is not null && bodyFont.Source.Contains("Space Grotesk", StringComparison.OrdinalIgnoreCase) &&
                bodyFont.Source.Contains("Noto Sans SC", StringComparison.OrdinalIgnoreCase) &&
                !bodyFont.Source.Contains("Inter", StringComparison.OrdinalIgnoreCase),
            "正文使用统一的 Space Grotesk 与 Noto Sans SC 回退字体资源");
    }

    private static GlyphTypeface[] ReadPackagedGlyphTypeface(FontFamily family, int codepoint) =>
        ReadPackagedGlyphTypefaces(family)
            .Where(glyph => glyph.CharacterToGlyphMap.TryGetValue(codepoint, out var glyphIndex) && glyphIndex != 0)
            .ToArray();

    private static GlyphTypeface[] ReadPackagedGlyphTypefaces(FontFamily family) => family.GetTypefaces()
        .Select(typeface => typeface.TryGetGlyphTypeface(out var glyphTypeface) ? glyphTypeface : null)
        .Where(glyphTypeface => glyphTypeface is not null &&
                                glyphTypeface.FontUri?.ToString().Contains(";component/Fonts/", StringComparison.OrdinalIgnoreCase) == true)
        .Cast<GlyphTypeface>()
        .ToArray();

    private static async Task VerifyInitialSkinAndSearchApplyPersistenceAsync(string testRoot)
    {
        var ayanami = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Ayanami"));
        var saya = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Saya"));
        var basePackage = App.ReadEmbeddedBasePackage();
        var oneSkinRoot = Path.Combine(testRoot, "first-skin-selection");
        var oneSkinStore = CreateStore(Path.Combine(oneSkinRoot, "skins"), ayanami);
        var oneSkinRun = new FakeStartupRunRegistration();
        var oneSkinStartup = new StartupPreferencesService(
            Path.Combine(oneSkinRoot, "preferences", "startup-preferences.json"), oneSkinRun);
        var oneSkinWindow = CreateMainWindow(
            oneSkinStore,
            basePackage,
            Path.Combine(oneSkinRoot, "preview-profile"),
            readRuntimeStatusOnLoad: false,
            startupPreferencesService: oneSkinStartup);
        oneSkinWindow.Show();
        try
        {
            await WaitForMainWindowAsync(oneSkinWindow, "首次手动启动单皮肤库预览");
            Require(oneSkinStartup.Read().LastSelectedSkinId == ayanami.Id,
                "首次手动启动时默认选中的单款已保存皮肤会记录为上次选择");
        }
        finally
        {
            if (oneSkinWindow.IsVisible)
            {
                oneSkinWindow.Close();
            }
        }

        var searchRoot = Path.Combine(testRoot, "search-apply-selection");
        var searchStore = CreateStore(Path.Combine(searchRoot, "skins"), ayanami, saya);
        var searchRun = new FakeStartupRunRegistration();
        var searchStartup = new StartupPreferencesService(
            Path.Combine(searchRoot, "preferences", "startup-preferences.json"), searchRun);
        var debugIdentity = new CodexProcessIdentity(
            45_923,
            new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero),
            @"C:\Fixtures\SkinManagerFakeCodex.exe");
        var debugInstance = new CodexInstance(debugIdentity, "Fake debug Codex", 0, 9335, @"C:\Fixtures\Profile")
        {
            Kind = CodexInstanceKind.Debug,
            CanConnect = true,
            DebugConnectionState = CodexDebugConnectionState.Ready,
            DebugEndpoint = "http://127.0.0.1:9335/"
        };
        var instances = new FixtureCodexInstanceService([debugInstance]);
        var searchWindow = CreateMainWindow(
            searchStore,
            basePackage,
            Path.Combine(searchRoot, "preview-profile"),
            readRuntimeStatusOnLoad: false,
            codexInstanceService: instances,
            startupPreferencesService: searchStartup);
        var launchCalls = 0;
        var applyCalls = 0;
        string? appliedSkinId = null;
        searchWindow.SetRuntimeApplyWorkflowForTesting(new RuntimeApplyWorkflow(
            (_, _) =>
            {
                Interlocked.Increment(ref launchCalls);
                return Task.FromResult(new RuntimeStatus(
                    RuntimeStatusState.Connecting,
                    null,
                    "http://127.0.0.1:9335",
                    "Fake launch only.",
                    true));
            },
            (request, _) =>
            {
                Interlocked.Increment(ref applyCalls);
                appliedSkinId = request.SkinId;
                return Task.FromResult(new RuntimeStatus(
                    RuntimeStatusState.Connected,
                    request.SkinId,
                    request.Endpoint,
                    "Fake skin applied.",
                    true,
                    appliedTargetCount: 1,
                    connectedInstance: debugIdentity));
            },
            instances,
            readinessTimeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(5)));
        searchWindow.Show();
        try
        {
            await WaitForMainWindowAsync(searchWindow, "搜索选择测试预览");
            var search = Find<TextBox>(searchWindow, "SearchBox");
            search.Text = "saya";
            await WaitUntilAsync(
                () => Find<ListBox>(searchWindow, "SkinList").SelectedItem is { } item && ReadSkinId(item) == saya.Id,
                "搜索筛选自动选择 Saya");
            Require(searchStartup.Read().LastSelectedSkinId == ayanami.Id,
                "搜索结果的自动选中不会覆盖此前记录的上次皮肤");

            Find<Button>(searchWindow, "ApplyRuntimeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => applyCalls == 1 && !ReadRuntimeOperationInProgress(searchWindow),
                "通过假调试实例完成 Saya 应用工作流");
            Require(appliedSkinId == saya.Id && launchCalls == 0 &&
                    searchStartup.Read().LastSelectedSkinId == saya.Id,
                "直接应用搜索自动选中的皮肤会持久化 Saya ID，且测试没有启动真实 Codex");
        }
        finally
        {
            if (searchWindow.IsVisible)
            {
                searchWindow.Close();
            }
        }
    }

    private static async Task VerifyMissingAutostartSkinAsync(string testRoot)
    {
        var ayanami = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Ayanami"));
        var root = Path.Combine(testRoot, "missing-autostart-skin");
        var store = CreateStore(Path.Combine(root, "skins"), ayanami);
        var runRegistration = new FakeStartupRunRegistration();
        runRegistration.SetEnabled(true);
        var startupPreferences = new StartupPreferencesService(
            Path.Combine(root, "preferences", "startup-preferences.json"), runRegistration);
        startupPreferences.SetApplyLastSelectedSkin(true);
        startupPreferences.RecordLastSelectedSkin("deleted-skin-id");
        var window = CreateMainWindow(
            store,
            App.ReadEmbeddedBasePackage(),
            Path.Combine(root, "preview-profile"),
            readRuntimeStatusOnLoad: true,
            codexInstanceService: new FixtureCodexInstanceService([]),
            runtimeStatusReader: _ => Task.FromResult(new RuntimeStatus(
                RuntimeStatusState.Stopped, null, null, "Fake status refresh.", false)),
            startupPreferencesService: startupPreferences,
            launchedByAutostart: true);
        var launchCalls = 0;
        var applyCalls = 0;
        window.SetRuntimeApplyWorkflowForTesting(new RuntimeApplyWorkflow(
            (_, _) =>
            {
                Interlocked.Increment(ref launchCalls);
                return Task.FromResult(new RuntimeStatus(RuntimeStatusState.Connecting, null, null, "Fake.", true));
            },
            (_, _) =>
            {
                Interlocked.Increment(ref applyCalls);
                return Task.FromResult(new RuntimeStatus(RuntimeStatusState.Connected, null, null, "Fake.", true));
            },
            new FixtureCodexInstanceService([]),
            readinessTimeout: TimeSpan.FromMilliseconds(20),
            pollInterval: TimeSpan.FromMilliseconds(5)));
        window.Show();
        try
        {
            await WaitUntilAsync(
                () => window.IsLoaded &&
                      Find<TextBlock>(window, "RuntimeStatusMessage").Text.Contains("deleted-skin-id", StringComparison.Ordinal),
                "缺失皮肤提示完成首轮连接状态刷新");
            Require(Find<ListBox>(window, "SkinList").SelectedItem is null &&
                    startupPreferences.Read().LastSelectedSkinId == "deleted-skin-id" &&
                    launchCalls == 0 && applyCalls == 0,
                "缺失的上次皮肤保持未选中，不回退到首项、不启动或应用 Codex");

            var refresh = typeof(MainWindow).GetMethod("RefreshRuntimeStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("找不到运行状态刷新方法。");
            await (Task)(refresh.Invoke(window, null)
                ?? throw new InvalidOperationException("运行状态刷新没有返回 Task。"));
            Require(Find<TextBlock>(window, "RuntimeStatusMessage").Text.Contains("deleted-skin-id", StringComparison.Ordinal),
                "后续运行状态刷新保留缺失皮肤原因提示");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static async Task VerifyAutostartAppliesSavedSkinAsync(string testRoot)
    {
        var ayanami = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Ayanami"));
        var saya = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Saya"));
        var root = Path.Combine(testRoot, "autostart-valid-skin");
        var store = CreateStore(Path.Combine(root, "skins"), ayanami, saya);
        var runRegistration = new FakeStartupRunRegistration();
        runRegistration.SetEnabled(true);
        var startupPreferences = new StartupPreferencesService(
            Path.Combine(root, "preferences", "startup-preferences.json"), runRegistration);
        startupPreferences.SetApplyLastSelectedSkin(true);
        startupPreferences.RecordLastSelectedSkin(saya.Id);
        var identity = new CodexProcessIdentity(
            46_021,
            new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero),
            @"C:\Fixtures\AutostartFakeCodex.exe");
        var debugInstance = new CodexInstance(identity, "Fake debug Codex", 0, 9335, @"C:\Fixtures\AutostartProfile")
        {
            Kind = CodexInstanceKind.Debug,
            CanConnect = true,
            DebugConnectionState = CodexDebugConnectionState.Ready,
            DebugEndpoint = "http://127.0.0.1:9335/"
        };
        var instances = new FixtureCodexInstanceService([debugInstance]);
        var statusReads = 0;
        var window = CreateMainWindow(
            store,
            App.ReadEmbeddedBasePackage(),
            Path.Combine(root, "preview-profile"),
            readRuntimeStatusOnLoad: true,
            codexInstanceService: instances,
            runtimeStatusReader: _ =>
            {
                Interlocked.Increment(ref statusReads);
                return Task.FromResult(new RuntimeStatus(
                    RuntimeStatusState.Stopped, null, null, "Fake initial status.", false));
            },
            startupPreferencesService: startupPreferences,
            launchedByAutostart: true);
        var launchCalls = 0;
        var applyCalls = 0;
        string? appliedSkinId = null;
        window.SetRuntimeApplyWorkflowForTesting(new RuntimeApplyWorkflow(
            (_, _) =>
            {
                Interlocked.Increment(ref launchCalls);
                return Task.FromResult(new RuntimeStatus(
                    RuntimeStatusState.Connecting, null, null, "Fake debug launch.", true));
            },
            (request, _) =>
            {
                Interlocked.Increment(ref applyCalls);
                appliedSkinId = request.SkinId;
                return Task.FromResult(new RuntimeStatus(
                    RuntimeStatusState.Connected,
                    request.SkinId,
                    request.Endpoint,
                    "Fake skin applied.",
                    true,
                    appliedTargetCount: 1,
                    connectedInstance: identity));
            },
            instances,
            readinessTimeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(5)));
        window.Show();
        try
        {
            await WaitForMainWindowAsync(window, "开机恢复有效皮肤预览");
            await WaitUntilAsync(() => applyCalls == 1 && !ReadRuntimeOperationInProgress(window),
                "初始连接状态刷新后执行一次开机应用");
            var refresh = typeof(MainWindow).GetMethod("RefreshRuntimeStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("找不到运行状态刷新方法。");
            await (Task)(refresh.Invoke(window, null)
                ?? throw new InvalidOperationException("运行状态刷新没有返回 Task。"));
            Require(statusReads >= 2 && applyCalls == 1 && appliedSkinId == saya.Id && launchCalls == 0 &&
                    Find<ListBox>(window, "SkinList").SelectedItem is { } selected && ReadSkinId(selected) == saya.Id,
                "显式 --autostart 恢复并只应用已保存的 Saya ID，后续状态刷新不会重复应用或启动真实 Codex");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static bool ReadRuntimeOperationInProgress(MainWindow window) =>
        typeof(MainWindow).GetField("_runtimeOperationInProgress", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(window) is true;

    private static async Task VerifyDirectCardReorderingAsync(string testRoot)
    {
        var ayanami = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Ayanami"));
        var saya = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Saya"));
        var basePackage = App.ReadEmbeddedBasePackage();
        var managerRoot = Path.Combine(testRoot, "direct-drag");
        var profile = Path.Combine(managerRoot, "preview-profile");
        var store = CreateStore(Path.Combine(managerRoot, "skins"), ayanami, saya);
        var window = CreateMainWindow(store, basePackage, profile, readRuntimeStatusOnLoad: false);
        window.Show();
        try
        {
            await WaitForMainWindowAsync(window, "主窗口离线预览加载");
            var list = Find<ListBox>(window, "SkinList");
            var listItemStyle = list.ItemContainerStyle
                ?? throw new InvalidOperationException("皮肤卡片没有列表样式。");
            Require(listItemStyle.Setters.OfType<EventSetter>().Any(setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent) &&
                    listItemStyle.Setters.OfType<EventSetter>().Any(setter => setter.Event == UIElement.PreviewMouseMoveEvent) &&
                    !FindVisualChildren<Button>(GetContainer(list, 0)).Any(),
                "整张卡片接收拖动手势，卡片内没有独立排序手柄");

            var dragStart = new Point(20, 20);
            var horizontalThreshold = SystemParameters.MinimumHorizontalDragDistance;
            var verticalThreshold = SystemParameters.MinimumVerticalDragDistance;
            Require(!MainWindow.HasPassedSkinDragThreshold(
                        dragStart,
                        new Point(dragStart.X + horizontalThreshold - 1, dragStart.Y + verticalThreshold - 1)) &&
                    MainWindow.HasPassedSkinDragThreshold(
                        dragStart,
                        new Point(dragStart.X + horizontalThreshold + 1, dragStart.Y)),
                "系统拖动阈值内只完成单击，超过阈值才进入拖动");

            var search = Find<TextBox>(window, "SearchBox");
            search.Text = "ayanami";
            await WaitUntilAsync(() => list.Items.Count == 1 && Equals(list.Tag, false), "搜索时禁用排序");
            var filteredOver = RaiseDragEvent(
                GetContainer(list, 0), DragDrop.DragOverEvent,
                new DataObject(SkinOrderDragFormat, saya.Id), new Point(10, 2));
            Require(filteredOver.Effects == DragDropEffects.None,
                "搜索筛选期间拒绝拖动排序");

            search.Clear();
            await WaitUntilAsync(() => list.Items.Count == 2 && Equals(list.Tag, true), "清空搜索后恢复排序");
            var sayaCard = list.Items.Cast<object>().Single(item => string.Equals(ReadSkinId(item), saya.Id, StringComparison.OrdinalIgnoreCase));
            list.SelectedItem = sayaCard;
            await WaitUntilAsync(() => ReadSkinId(list.SelectedItem!) == saya.Id, "单击选择 Saya 卡片");
            var firstCard = GetContainer(list, 0);
            var payload = new DataObject(SkinOrderDragFormat, saya.Id);
            var dragOver = RaiseDragEvent(firstCard, DragDrop.DragOverEvent, payload, new Point(10, 2));
            Require(dragOver.Effects == DragDropEffects.Move, "整张 ListBoxItem 卡片可作为排序落点");
            var drop = RaiseDragEvent(firstCard, DragDrop.DropEvent, payload, new Point(10, 2));
            await WaitUntilAsync(() => ReadVisibleSkinIds(list).SequenceEqual([saya.Id, ayanami.Id], StringComparer.OrdinalIgnoreCase),
                "按皮肤 ID 将 Saya 移到 Ayanami 前");
            var persisted = new SkinOrderStore(Path.Combine(store.DataDirectory, ".skin-order.json"))
                .Reconcile(store.GetCatalog().Select(entry => entry.Id));
            Require(drop.Effects == DragDropEffects.Move &&
                    persisted.SequenceEqual([saya.Id, ayanami.Id], StringComparer.OrdinalIgnoreCase) &&
                    string.Equals(ReadSkinId(list.SelectedItem!), saya.Id, StringComparison.OrdinalIgnoreCase),
                "排序持久化皮肤 ID 并保留当前选中 ID");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static async Task VerifyThemeAndEntrySurfacesAsync(string testRoot)
    {
        var settingsPath = Path.Combine(testRoot, "preferences", "app-preferences.json");
        AppThemeService.SetMode(AppThemeMode.Day);
        Require(AppThemeService.Mode == AppThemeMode.Day &&
                File.ReadAllText(settingsPath).Contains("Day", StringComparison.Ordinal),
            "白昼模式即时应用并写入独立应用偏好");
        AppThemeService.Initialize(settingsPath);
        Require(AppThemeService.Mode == AppThemeMode.Day, "重新载入应用偏好后恢复白昼模式");
        AppThemeService.SetMode(AppThemeMode.Night);
        Require(AppThemeService.Mode == AppThemeMode.Night, "夜间模式可即时切换并保存");

        var screenshotDirectory = Path.Combine(Program.FindTestOutputDirectory(), "beta2-ui");
        Directory.CreateDirectory(screenshotDirectory);
        var ayanamiBytes = ReadEmbeddedPackage("Ayanami");
        var sayaBytes = ReadEmbeddedPackage("Saya");
        var ayanami = SkinPackageReader.ReadZipBytes(ayanamiBytes);
        var saya = SkinPackageReader.ReadZipBytes(sayaBytes);
        var basePackage = App.ReadEmbeddedBasePackage();
        var managerRoot = Path.Combine(testRoot, "theme-screenshots");
        var store = CreateStore(Path.Combine(managerRoot, "skins"), ayanami, saya);
        var fakeInstances = new FixtureCodexInstanceService([]);
        var startupRun = new FakeStartupRunRegistration();
        var startupPreferences = new StartupPreferencesService(
            Path.Combine(managerRoot, "preferences", "startup-preferences.json"), startupRun);
        startupPreferences.SetManagerStartsAtWindowsSignIn(true);
        startupPreferences.SetApplyLastSelectedSkin(true);
        startupPreferences.RecordLastSelectedSkin(saya.Id);
        var window = CreateMainWindow(
            store,
            basePackage,
            Path.Combine(managerRoot, "preview-profile"),
            readRuntimeStatusOnLoad: false,
            codexInstanceService: fakeInstances,
            skinMarketClientFactory: MarketUiChecks.CreateReviewClientFactory(ayanamiBytes),
            startupPreferencesService: startupPreferences);
        var manualLaunchCalls = 0;
        var manualApplyCalls = 0;
        window.SetRuntimeApplyWorkflowForTesting(new RuntimeApplyWorkflow(
            (_, _) =>
            {
                Interlocked.Increment(ref manualLaunchCalls);
                return Task.FromResult(new RuntimeStatus(RuntimeStatusState.Connecting, null, null, "Fake only.", true));
            },
            (_, _) =>
            {
                Interlocked.Increment(ref manualApplyCalls);
                return Task.FromResult(new RuntimeStatus(RuntimeStatusState.Connected, null, null, "Fake only.", true));
            },
            fakeInstances,
            readinessTimeout: TimeSpan.FromMilliseconds(20),
            pollInterval: TimeSpan.FromMilliseconds(5)));

        SettingsWindow? settings = null;
        SkinMarketWindow? market = null;
        SupportAuthorWindow? support = null;
        InstancesWindow? instances = null;
        UpdatesWindow? updates = null;
        DebugModeSwitchConfirmationWindow? confirmation = null;
        ToolTip? tooltip = null;
        try
        {
            window.Show();
            await WaitForMainWindowAsync(window, "主题检查主窗口预览加载");
            Require(ReadSkinId(Find<ListBox>(window, "SkinList").SelectedItem!) == saya.Id &&
                    manualLaunchCalls == 0 && manualApplyCalls == 0,
                "即使两项开机设置都开启，手动启动也只恢复上次选择，不启动或应用 Codex");
            var browser = Find<WebView2>(window, "PreviewBrowser");
            await SaveManagerScreenshotAsync(window, browser,
                Path.Combine(screenshotDirectory, "主窗口-夜间-1240x840.png"));

            Find<Button>(window, "SettingsEntryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            settings = await WaitForWindowAsync<SettingsWindow>("打开设置窗口");
            settings.Width = 760;
            settings.Height = 570;
            Require(Find<CheckBox>(settings, "StartManagerAtLoginCheckBox").IsChecked == true &&
                    Find<CheckBox>(settings, "ApplyLastSelectedSkinCheckBox").IsChecked == true &&
                    Find<CheckBox>(settings, "ApplyLastSelectedSkinCheckBox").IsEnabled,
                "开机组显示已启用状态，第二项仅在管理器自启开启时可选");
            await SaveWindowScreenshotAsync(settings,
                Path.Combine(screenshotDirectory, "设置-夜间-760x570.png"));

            var managerStartupCheckBox = Find<CheckBox>(settings, "StartManagerAtLoginCheckBox");
            var applyStartupCheckBox = Find<CheckBox>(settings, "ApplyLastSelectedSkinCheckBox");
            managerStartupCheckBox.IsChecked = false;
            Require(!startupRun.IsEnabled() && applyStartupCheckBox.IsChecked == true &&
                    !applyStartupCheckBox.IsEnabled && !startupPreferences.Read().ShouldApplyLastSelectedSkinOnAutostart,
                "关闭管理器自启后第二项保留原偏好但禁用且不生效");
            managerStartupCheckBox.IsChecked = true;
            Require(startupRun.IsEnabled() && applyStartupCheckBox.IsChecked == true && applyStartupCheckBox.IsEnabled,
                "重新开启管理器自启后恢复第二项生效条件");

            Find<Button>(window, "MarketEntryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            market = await WaitForWindowAsync<SkinMarketWindow>("打开本地 fixture 市场窗口");
            await WaitUntilAsync(() => market.Rows.Count == 1 && market.IsCatalogVisible, "本地市场目录加载");
            await WaitUntilAsync(() => market.Opacity >= 0.999 &&
                                      market.Rows.Count == 1 &&
                                      market.Rows[0].HasThumbnail &&
                                      !market.Rows[0].IsThumbnailLoading,
                "市场窗口淡入与 Ayanami 封面缩略图稳定");
            await SaveWindowScreenshotAsync(market,
                Path.Combine(screenshotDirectory, "皮肤市场-夜间-说明.png"));

            Find<Button>(window, "SupportAuthorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            support = await WaitForWindowAsync<SupportAuthorWindow>("打开提出建议面板");
            support.UpdateLayout();
            var supportText = FindVisualChildren<TextBlock>(support).Select(block => block.Text).ToArray();
            var githubStarText = Find<TextBlock>(support, "GithubStarText");
            var freeDistributionText = Find<TextBlock>(support, "FreeDistributionText");
            var supportDirectionText = Find<TextBlock>(support, "SupportDirectionText");
            Require(support.Title == "提出建议" &&
                    supportText.Contains("作者：Ancimen") &&
                    supportText.Contains("感谢您使用SkinManager！若您愿意，希望您为Github仓库Star~💖") &&
                    supportText.Contains("本软件为免费无偿分发软件，若您为付费购买请举报售卖方并联系作者") &&
                    supportText.Contains("若您愿意支持作者，您可以点击“支持作者”前往赞助页") &&
                    githubStarText.TextWrapping == TextWrapping.Wrap &&
                    freeDistributionText.TextWrapping == TextWrapping.Wrap &&
                    supportDirectionText.TextWrapping == TextWrapping.Wrap &&
                    githubStarText.ActualWidth > 0 && githubStarText.ActualHeight > 0 &&
                    freeDistributionText.ActualWidth > 0 && freeDistributionText.ActualHeight > 0 &&
                    supportDirectionText.ActualWidth > 0 && supportDirectionText.ActualHeight > 0,
                "支持作者面板保留原两段说明，并显示新增 GitHub 邀请与完整换行文案");
            var supportActions = Find<StackPanel>(support, "ActionButtons").Children.OfType<Button>().ToArray();
            Require(supportActions.Select(button => button.Name)
                        .SequenceEqual(["SupportAuthorLinkButton", "GitHubLinkButton", "FeedbackButton"], StringComparer.Ordinal) &&
                    !supportActions[1].IsDefault && support.WindowStyle != WindowStyle.None &&
                    AutomationProperties.GetName(supportActions[1]) == "打开 GitHub 仓库" &&
                    FindVisualChildren<System.Windows.Shapes.Path>(supportActions[1]).Any(),
                "支持方式、GitHub 与反馈入口并列显示，GitHub 保留五角星矢量图标和原生标题栏关闭按钮");
            var requestedExternalUris = new List<Uri>();
            support.ExternalUriLauncher = requestedExternalUris.Add;
            supportActions[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var requestedSponsorUri = requestedExternalUris.SingleOrDefault();
            Require(requestedExternalUris.Count == 1 &&
                    requestedSponsorUri is { Scheme: "https", Host: "blog.ancimen.top", AbsolutePath: "/", IsDefaultPort: true } &&
                    GetQueryValue(requestedSponsorUri, "page_id") == "170" &&
                    GetQueryValue(requestedSponsorUri, "lang") == "zh",
                "点击支持作者只向系统启动器请求打开指定赞助页");
            supportActions[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(requestedExternalUris.Count == 2 &&
                    requestedExternalUris[1].AbsoluteUri == "https://github.com/Nortea575/SkinManager",
                "点击 GitHub 按钮只向可替换的外部启动器请求仓库地址");
            await SaveWindowScreenshotAsync(support,
                Path.Combine(screenshotDirectory, "提出建议-夜间.png"));

            instances = new InstancesWindow(fakeInstances) { Owner = window };
            updates = new UpdatesWindow(null) { Owner = window };
            confirmation = new DebugModeSwitchConfirmationWindow { Owner = window };
            instances.Show();
            updates.Show();
            confirmation.Show();
            await WaitUntilAsync(() => confirmation.IsLoaded && confirmation.ActualWidth > 0 &&
                                      confirmation.ActualHeight > 0 && confirmation.Opacity >= 0.999,
                "确认弹窗夜间布局与淡入完成");
            await SaveWindowScreenshotAsync(confirmation,
                Path.Combine(screenshotDirectory, "确认切换-夜间.png"));

            var moreButton = Find<Button>(window, "MoreActionsButton");
            var menu = moreButton.ContextMenu
                ?? throw new InvalidOperationException("更多菜单未连接到皮肤操作按钮。");
            menu.PlacementTarget = moreButton;
            menu.IsOpen = true;
            await WaitUntilAsync(() => menu.IsOpen && menu.ActualWidth > 0 && menu.ActualHeight > 0, "打开更多菜单");
            var deleteItem = menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString()?.StartsWith("删除", StringComparison.Ordinal) == true);
            Require(deleteItem.Tag?.ToString() == "Danger" && MenuMotionBehavior.IsAttached(deleteItem),
                "更多菜单提供可识别的危险删除项与共享菜单反馈");
            await SaveVisualAsync(menu, Path.Combine(screenshotDirectory, "更多菜单-夜间.png"));

            tooltip = new ToolTip { Content = "皮肤操作提示", PlacementTarget = moreButton };
            tooltip.IsOpen = true;
            await WaitUntilAsync(() => tooltip.IsOpen && tooltip.ActualWidth > 0 && tooltip.ActualHeight > 0, "打开工具提示");
            var settingsTabs = Find<TabControl>(settings, "SettingsTabs");
            settingsTabs.SelectedItem = Find<TabItem>(settings, "AppearanceTab");
            await WaitUntilAsync(() => settings.AppearanceModeBox.IsLoaded &&
                                      settings.AppearanceModeBox.IsVisible &&
                                      settings.AppearanceModeBox.ActualWidth > 0 &&
                                      settings.AppearanceModeBox.ActualHeight > 0,
                "切换到外观设置页并完成控件布局");
            await settings.Dispatcher.InvokeAsync(settings.UpdateLayout, DispatcherPriority.Render);
            settings.AppearanceModeBox.IsDropDownOpen = true;
            await WaitUntilAsync(() => settings.AppearanceModeBox.IsDropDownOpen, "打开外观模式下拉弹层");

            var browserWidth = browser.ActualWidth;
            var browserHeight = browser.ActualHeight;
            var browserOpacity = browser.Opacity;
            AppThemeService.SetMode(AppThemeMode.Day);
            var openWindows = new Window[] { window, market, settings, support, instances, updates, confirmation };
            foreach (var openWindow in openWindows)
            {
                openWindow.UpdateLayout();
            }
            await Application.Current.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            var expectedWindowSurface = GetBrush("WindowSurface");
            var windowSurfaces = openWindows.Select(openWindow =>
                    $"{openWindow.GetType().Name}={FormatBrush(openWindow.Background)}")
                .ToArray();
            var allWindowsUseDaySurface = openWindows.All(openWindow => GetBrush(openWindow.Background) == expectedWindowSurface);
            Require(allWindowsUseDaySurface,
                $"已打开的窗口同步切换白昼主题（期望 {FormatColor(expectedWindowSurface)}；实际 {string.Join(", ", windowSurfaces)}）");
            var expanderHeaders = GetExpanderHeaderButtons(window);
            Require(expanderHeaders.Length > 0 && expanderHeaders.All(header =>
                    GetBrush(header.Foreground) == GetBrush("PrimaryText")),
                "所有 Expander 标题跟随白昼 PrimaryText 语义色");
            Require(GetBrush(menu.Background) == GetBrush("PanelSurface") &&
                    GetBrush(tooltip.Background) == GetBrush("RaisedSurface") &&
                    GetBrush(settings.AppearanceModeBox.Background) == GetBrush("FieldSurface") &&
                    settings.AppearanceModeBox.SelectedIndex == 1,
                "已打开的菜单、工具提示与下拉弹层同步切换白昼语义色");
            Require(Math.Abs(browser.ActualWidth - browserWidth) < 0.5 &&
                    Math.Abs(browser.ActualHeight - browserHeight) < 0.5 &&
                    Math.Abs(browser.Opacity - browserOpacity) < 0.001,
                "切换管理器主题不调整或淡化 WebView2 客户区");
            Require(File.ReadAllText(settingsPath).Contains("Day", StringComparison.Ordinal),
                "白昼选择器切换立即写入应用主题偏好");

            await SaveManagerScreenshotAsync(window, browser,
                Path.Combine(screenshotDirectory, "主窗口-白昼-1240x840.png"));
            await SaveWindowScreenshotAsync(settings,
                Path.Combine(screenshotDirectory, "设置-白昼-760x570.png"));
            await SaveWindowScreenshotAsync(support,
                Path.Combine(screenshotDirectory, "提出建议-白昼.png"));
            await SaveWindowScreenshotAsync(confirmation,
                Path.Combine(screenshotDirectory, "确认切换-白昼.png"));
            await SaveVisualAsync(menu, Path.Combine(screenshotDirectory, "更多菜单-白昼.png"));

            menu.IsOpen = false;
            tooltip.IsOpen = false;
            settings.AppearanceModeBox.IsDropDownOpen = false;
            AppThemeService.SetMode(AppThemeMode.Night);
            window.Width = 960;
            window.Height = 640;
            window.UpdateLayout();
            await WaitUntilAsync(() => window.ActualWidth >= 959 && window.ActualHeight >= 639, "主窗口 960×640 布局");
            await SaveManagerScreenshotAsync(window, browser,
                Path.Combine(screenshotDirectory, "主窗口-夜间-960x640.png"));
            AppThemeService.SetMode(AppThemeMode.Day);
            await SaveManagerScreenshotAsync(window, browser,
                Path.Combine(screenshotDirectory, "主窗口-白昼-960x640.png"));

            AppThemeService.Initialize(settingsPath);
            Require(AppThemeService.Mode == AppThemeMode.Day &&
                    GetBrush(window.Background) == GetBrush("WindowSurface"),
                "重新初始化主题服务后仍保留已保存的白昼主题");
            AppThemeService.SetMode(AppThemeMode.Night);
            var screenshotConfirmation = confirmation;
            screenshotConfirmation.Close();
            await WaitUntilAsync(() => !screenshotConfirmation.IsVisible &&
                                      !Application.Current.Windows.OfType<DebugModeSwitchConfirmationWindow>()
                                          .Any(openConfirmation => openConfirmation.IsVisible),
                "关闭截图用的非模态确认窗口");
            confirmation = null;
            await VerifyDebugModeSwitchConfirmationDispatchAsync(window);
        }
        finally
        {
            if (tooltip is not null)
            {
                tooltip.IsOpen = false;
                tooltip = null;
            }
            if (settings is not null)
            {
                settings.AppearanceModeBox.IsDropDownOpen = false;
                settings.Close();
            }
            market?.Close();
            support?.Close();
            instances?.Close();
            updates?.Close();
            confirmation?.Close();
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static async Task VerifyWeakConnectionSurvivesRefreshAsync(string testRoot)
    {
        var ayanami = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Ayanami"));
        var managerRoot = Path.Combine(testRoot, "weak-connection");
        var store = CreateStore(Path.Combine(managerRoot, "skins"), ayanami);
        AppLocalization.Initialize(Path.Combine(managerRoot, "language.json"));
        AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
        var identity = new CodexProcessIdentity(44621, DateTimeOffset.UnixEpoch, "C:\\Fixtures\\Codex.exe");
        var endpoint = "http://127.0.0.1:9335";
        var weakInstance = new CodexInstance(identity, "Fixture Codex", 0, 9335, null)
        {
            Kind = CodexInstanceKind.Debug,
            CanConnect = false,
            DebugConnectionState = CodexDebugConnectionState.WaitingForListener,
            DebugEndpoint = endpoint
        };
        var instanceService = new FixtureCodexInstanceService([weakInstance]);
        var statusReads = 0;
        var launchCalls = 0;
        var applyCalls = 0;
        var status = new RuntimeStatus(
            RuntimeStatusState.Connected,
            "ayanami",
            endpoint,
            AppLocalization.Format("已应用到 {0} 个 Codex 页面。", 1),
            false,
            appliedTargetCount: 1,
            connectedInstance: identity);
        var window = CreateMainWindow(
            store,
            App.ReadEmbeddedBasePackage(),
            Path.Combine(managerRoot, "preview-profile"),
            readRuntimeStatusOnLoad: true,
            codexInstanceService: instanceService,
            runtimeStatusReader: _ =>
            {
                Interlocked.Increment(ref statusReads);
                return Task.FromResult(status);
            });
        window.SetRuntimeApplyWorkflowForTesting(new RuntimeApplyWorkflow(
            (_, _) =>
            {
                Interlocked.Increment(ref launchCalls);
                return Task.FromResult(new RuntimeStatus(RuntimeStatusState.Connecting, null, endpoint, "launch", true));
            },
            (_, _) =>
            {
                Interlocked.Increment(ref applyCalls);
                return Task.FromResult(status);
            },
            instanceService));
        window.Show();
        try
        {
            await WaitForMainWindowAsync(window, "弱 CDP 状态主窗口加载");
            await WaitUntilAsync(() => Find<TextBlock>(window, "RuntimeStatusText").Text.Contains("已连接", StringComparison.Ordinal),
                "弱 HTTP listener 状态保留已连接状态");
            var refreshTimer = typeof(MainWindow).GetField("_runtimeStatusRefreshTimer", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(window) as DispatcherTimer
                ?? throw new InvalidOperationException("找不到连接状态刷新计时器。");
            refreshTimer.Stop();
            Require(Find<TextBlock>(window, "RuntimeStatusMessage").Text == "已应用到 1 个 Codex 页面。" &&
                    instanceService.InstanceSnapshotReads > 1,
                "HTTP listener 不可验证时保留活跃会话与原状态信息");

            var selectedBefore = Find<ListBox>(window, "SkinList").SelectedItem;
            var selectedSkinIdBefore = selectedBefore is null ? null : ReadSkinId(selectedBefore);
            var previewBrowser = Find<WebView2>(window, "PreviewBrowser");
            var statusReadBaseline = Volatile.Read(ref statusReads);
            var instanceReadBaseline = instanceService.InstanceSnapshotReads;
            AppLocalization.SetLanguage(AppLanguage.English);
            await WaitUntilAsync(() =>
                    Find<TextBlock>(window, "RuntimeStatusText").Text.Contains("Connected", StringComparison.Ordinal) &&
                    Find<TextBlock>(window, "RuntimeStatusMessage").Text.Contains("1", StringComparison.Ordinal) &&
                    !Find<TextBlock>(window, "RuntimeStatusMessage").Text.Any(character => character is >= '\u4e00' and <= '\u9fff'),
                "活动连接状态切换为英文摘要");
            AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
            await WaitUntilAsync(() =>
                    Find<TextBlock>(window, "RuntimeStatusText").Text == "已连接并已应用" &&
                    Find<TextBlock>(window, "RuntimeStatusMessage").Text == "已应用到 1 个 Codex 页面。",
                "活动连接状态切回中文摘要");
            var selectedAfter = Find<ListBox>(window, "SkinList").SelectedItem;
            Require(statusReads == statusReadBaseline && instanceService.InstanceSnapshotReads == instanceReadBaseline &&
                    launchCalls == 0 && applyCalls == 0 && selectedSkinIdBefore == (selectedAfter is null ? null : ReadSkinId(selectedAfter)) &&
                    ReferenceEquals(previewBrowser, Find<WebView2>(window, "PreviewBrowser")),
                "中英文来回切换仅重绘活动连接摘要，不重连、不重新应用，也不重建预览或改变选中皮肤");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static async Task VerifyDebugModeSwitchConfirmationDispatchAsync(MainWindow window)
    {
        var confirmationMethod = typeof(MainWindow).GetMethod(
            "ConfirmDebugModeSwitchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到普通版 Codex 切换确认回调。");
        var fixtureInstance = new CodexInstance(
            new CodexProcessIdentity(44624, DateTimeOffset.UnixEpoch, "C:\\Fixtures\\Codex.exe"),
            "Fixture Codex",
            0,
            null,
            null);

        using (var cancellation = new CancellationTokenSource())
        {
            var cancelledConfirmation = InvokeConfirmationFromWorkerThread(
                confirmationMethod,
                window,
                fixtureInstance,
                cancellation.Token);
            var cancelDialog = await WaitForWindowAsync<DebugModeSwitchConfirmationWindow>(
                "后台线程调用后在 UI Dispatcher 打开确认窗口");
            cancellation.Cancel();
            var cancellationPropagated = false;
            try
            {
                await cancelledConfirmation;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                cancellationPropagated = true;
            }

            await WaitUntilAsync(() => !cancelDialog.IsVisible, "取消令牌关闭确认窗口");
            Require(cancellationPropagated, "确认窗口的取消令牌会关闭窗口并向工作流传播取消");
        }

        var acceptedConfirmation = InvokeConfirmationFromWorkerThread(
            confirmationMethod,
            window,
            fixtureInstance,
            CancellationToken.None);
        var confirmDialog = await WaitForWindowAsync<DebugModeSwitchConfirmationWindow>(
            "确认普通版退出的第二个调度弹窗");
        await window.Dispatcher.InvokeAsync(() =>
        {
            Find<Button>(confirmDialog, "ConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, DispatcherPriority.Normal);
        Require(await acceptedConfirmation, "后台线程收到 Dispatcher 确认结果后允许切换");
    }

    private static Task<bool> InvokeConfirmationFromWorkerThread(
        System.Reflection.MethodInfo confirmationMethod,
        MainWindow window,
        CodexInstance instance,
        CancellationToken cancellationToken) =>
        Task.Run(() => (Task<bool>)(confirmationMethod.Invoke(
            window,
            [instance, cancellationToken]) ?? throw new InvalidOperationException("确认回调未返回任务。")));

    private static MainWindow CreateMainWindow(
        SkinStore store,
        BasePackage basePackage,
        string profile,
        bool readRuntimeStatusOnLoad,
        ICodexInstanceService? codexInstanceService = null,
        Func<CancellationToken, Task<RuntimeStatus>>? runtimeStatusReader = null,
        Func<SkinMarketClient>? skinMarketClientFactory = null,
        IStartupPreferencesService? startupPreferencesService = null,
        bool launchedByAutostart = false) =>
        new(
            store,
            basePackage,
            previewProfileDirectory: profile,
            readRuntimeStatusOnLoad: readRuntimeStatusOnLoad,
            codexInstanceService: codexInstanceService,
            runtimeStatusReader: runtimeStatusReader,
            skinMarketClientFactory: skinMarketClientFactory,
            codexConfigReadabilityService: new FixtureCodexConfigReadabilityService(
                Path.Combine(profile, "codex-fixture", "config.toml")),
            startupPreferencesService: startupPreferencesService,
            launchedByAutostart: launchedByAutostart)
        {
            Width = 1240,
            Height = 840,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false
        };

    private static SkinStore CreateStore(string path, params SkinPackage[] packages)
    {
        var store = new SkinStore(path);
        store.Initialize();
        foreach (var package in packages)
        {
            store.Save(package, replaceExisting: false);
        }

        return store;
    }

    private static async Task WaitForMainWindowAsync(MainWindow window, string label) =>
        await WaitUntilAsync(() => window.IsLoaded && Find<WebView2>(window, "PreviewBrowser").CoreWebView2 is not null &&
                                  Find<WebView2>(window, "PreviewBrowser").ActualHeight > 0, label);

    private static async Task<T> WaitForWindowAsync<T>(string label) where T : Window
    {
        await WaitUntilAsync(() => Application.Current.Windows.OfType<T>().Any(window => window.IsVisible && window.IsLoaded), label);
        return Application.Current.Windows.OfType<T>().First(window => window.IsVisible && window.IsLoaded);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"Beta 2 UI 检查失败：{label}超时。");
            }

            await Task.Delay(25);
        }
    }

    private static T Find<T>(Window window, string name) where T : FrameworkElement =>
        window.FindName(name) is T element
            ? element
            : throw new InvalidOperationException($"窗口 {window.Title} 缺少控件 {name}。");

    private static ToggleButton[] GetExpanderHeaderButtons(MainWindow window) =>
        FindVisualChildren<Expander>(window)
            .Select(expander =>
            {
                expander.ApplyTemplate();
                return expander.Template.FindName("HeaderSite", expander) as ToggleButton;
            })
            .Where(header => header is not null)
            .Cast<ToggleButton>()
            .ToArray();

    private static ListBoxItem GetContainer(ListBox list, int index)
    {
        list.UpdateLayout();
        return list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem
            ?? throw new InvalidOperationException($"皮肤列表尚未生成第 {index} 项卡片。");
    }

    private static DragEventArgs RaiseDragEvent(ListBoxItem source, RoutedEvent routedEvent, IDataObject data, Point point)
    {
        var args = Activator.CreateInstance(
            typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, source, point],
            culture: null) as DragEventArgs
            ?? throw new InvalidOperationException("无法创建 WPF 拖放测试事件参数。");
        args.RoutedEvent = routedEvent;
        source.RaiseEvent(args);
        return args;
    }

    private static string[] ReadVisibleSkinIds(ListBox list) =>
        list.Items.Cast<object>().Select(ReadSkinId).ToArray();

    private static string ReadSkinId(object card)
    {
        var entry = card.GetType().GetProperty("Entry", BindingFlags.Instance | BindingFlags.Public)?.GetValue(card)
            ?? throw new InvalidOperationException("皮肤卡片没有公开目录项数据。");
        return entry.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)?.GetValue(entry) as string
            ?? throw new InvalidOperationException("皮肤卡片没有皮肤 ID。");
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

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private static Color GetBrush(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    private static string FormatBrush(Brush? brush) => brush is SolidColorBrush solid
        ? FormatColor(solid.Color)
        : brush?.GetType().Name ?? "null";

    private static string FormatColor(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color GetBrush(string resourceKey) =>
        Application.Current.TryFindResource(resourceKey) is SolidColorBrush brush ? brush.Color : Colors.Transparent;

    private static async Task SaveManagerScreenshotAsync(MainWindow window, WebView2 browser, string path)
    {
        await WaitForStableManagerCaptureAsync(window, browser);
        await Program.SaveManagerWindowScreenshotAsync(window, browser, path);
    }

    private static async Task WaitForStableManagerCaptureAsync(MainWindow window, WebView2 browser)
    {
        var stableSamples = 0;
        await WaitUntilAsync(() =>
        {
            var isBusy = typeof(MainWindow)
                .GetField("_isBusy", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(window) is true;
            var previewRenderer = typeof(MainWindow)
                .GetField("_previewRenderer", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(window);
            var resolvedSkin = previewRenderer?.GetType()
                .GetProperty("CurrentResolvedSkin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                .GetValue(previewRenderer);
            var previewStatus = Find<TextBlock>(window, "PreviewStatus");
            var details = Find<FrameworkElement>(window, "DetailsScroll");
            var summary = Find<FrameworkElement>(window, "SelectedSummary");
            var palette = Find<FrameworkElement>(window, "ColorSection");
            var colorItems = Find<FrameworkElement>(window, "ColorItems");
            var activeTransitions = typeof(MainWindow)
                .GetField("_activeVisibilityTransitions", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(window);
            var activeTransitionCount = activeTransitions?.GetType().GetProperty("Count")?.GetValue(activeTransitions) as int? ?? 0;
            var surfacesAreSettled = new[] { details, summary, palette, colorItems }.All(element =>
                Math.Abs(element.Opacity - 1) < 0.001 &&
                Math.Abs((double)element.GetAnimationBaseValue(UIElement.OpacityProperty) - element.Opacity) < 0.001);
            var previewIsReady = window.IsVisible &&
                                 !isBusy &&
                                 resolvedSkin is not null &&
                                 previewStatus.Visibility == Visibility.Collapsed &&
                                 browser.Visibility == Visibility.Visible &&
                                 browser.ActualWidth > 0 && browser.ActualHeight > 0 &&
                                 Find<ItemsControl>(window, "ColorItems").Items.Count > 0 &&
                                 details.Visibility == Visibility.Visible &&
                                 palette.Visibility == Visibility.Visible &&
                                 activeTransitionCount == 0 &&
                                 surfacesAreSettled;
            stableSamples = previewIsReady ? stableSamples + 1 : 0;
            return stableSamples >= 3;
        }, "等待主窗口预览完成与调色板/详情淡入稳定");

        await window.Dispatcher.InvokeAsync(() =>
        {
            window.UpdateLayout();
            browser.UpdateLayout();
        }, DispatcherPriority.Render);
    }

    private static async Task SaveWindowScreenshotAsync(Window window, string path) =>
        await Program.SaveWindowScreenshotAsync(window, path);

    private static async Task SaveVisualAsync(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(visual);
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY));
        var image = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
        await output.FlushAsync();
    }

    private static byte[] ReadEmbeddedPackage(string name)
    {
        var resource = $"SkinManager.Preview.Tests.Resources.{name}.zip";
        using var source = typeof(Beta2UiChecks).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"测试皮肤包资源不存在：{resource}");
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var partKey = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            if (partKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(separator < 0 ? string.Empty : part[(separator + 1)..]);
            }
        }

        return null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Beta 2 UI 检查失败：{message}");
        }

        Console.WriteLine($"通过：{message}");
    }

    private static void TryDeleteDirectory(string path)
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

    private sealed class FixtureCodexConfigReadabilityService(string path) : ICodexConfigReadabilityService
    {
        public CodexConfigReadabilityResult Inspect() => new(path, CodexConfigReadabilityState.Missing);
    }

    private sealed class FixtureCodexInstanceService(IReadOnlyList<CodexInstance> instances) : ICodexInstanceService
    {
        public int InstanceSnapshotReads { get; private set; }

        public Task<IReadOnlyList<CodexInstance>> GetInstancesAsync(CancellationToken cancellationToken = default)
        {
            InstanceSnapshotReads++;
            return Task.FromResult(instances);
        }

        public Task<CodexLocateResult> LocateAsync(CodexInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(CodexLocateResult.NoWindow);

        public Task<CodexCloseOperationResult> CloseAsync(
            CodexInstance instance,
            bool force = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexCloseOperationResult(CodexCloseResult.Closed, instance, "Fixture only."));

    }
}

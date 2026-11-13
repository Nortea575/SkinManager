using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Runtime;

namespace SkinManager.Preview.Tests;

internal static class MainWindowLanguageChecks
{
    internal static async Task RunAsync()
    {
        var previousLanguage = AppLocalization.Preference;
        var previousTheme = AppThemeService.Mode;
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"SkinManager-main-language-{Guid.NewGuid():N}"));
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!testRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MainWindow language test root escaped the system temp directory.");
        }

        Directory.CreateDirectory(testRoot);
        AppLocalization.Initialize(Path.Combine(Program.FindTestOutputDirectory(), "language-harness", "language.json"));
        AppThemeService.Initialize(Path.Combine(testRoot, "preferences", "app-preferences.json"));
        AppThemeService.SetMode(AppThemeMode.Day);

        MainWindow? window = null;
        var screenshotDirectory = Path.Combine(Program.FindTestOutputDirectory(), "localization");
        Directory.CreateDirectory(screenshotDirectory);
        try
        {
            AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
            VerifyRawTextDoesNotReapplyRemovedLocalizationSource();

            var ayanamiBytes = CustomizePackage(
                ReadEmbeddedPackage("Ayanami"),
                "用户自定义·綾波",
                "素材/綾波用户背景");
            var sayaBytes = CustomizePackage(
                ReadEmbeddedPackage("Saya"),
                "用户自定义·纱耶",
                "素材/纱耶用户背景");
            var ayanami = SkinPackageReader.ReadZipBytes(ayanamiBytes);
            var saya = SkinPackageReader.ReadZipBytes(sayaBytes);
            var store = new SkinStore(Path.Combine(testRoot, "skins"));
            store.Initialize();
            store.Save(ayanami, replaceExisting: false);
            store.Save(saya, replaceExisting: false);
            new SkinOrderStore(Path.Combine(store.DataDirectory, ".skin-order.json"))
                .SaveOrder([saya.Id, ayanami.Id]);

            var startupPreferences = new StartupPreferencesService(
                Path.Combine(testRoot, "preferences", "startup-preferences.json"),
                new FakeStartupRunRegistration());
            startupPreferences.RecordLastSelectedSkin(ayanami.Id);

            var identity = new CodexProcessIdentity(
                84_601,
                new DateTimeOffset(2026, 11, 13, 2, 0, 0, TimeSpan.Zero),
                @"C:\Fixtures\用户自定义\Codex.exe");
            var endpoint = "http://127.0.0.1:9335/";
            var instance = new CodexInstance(
                identity,
                "用户自定义 Codex 窗口",
                0,
                9335,
                Path.Combine(testRoot, "用户配置"))
            {
                Kind = CodexInstanceKind.Debug,
                CanConnect = true,
                DebugConnectionState = CodexDebugConnectionState.Ready,
                DebugEndpoint = endpoint
            };
            var instances = new FixtureCodexInstanceService([instance]);
            var status = new RuntimeStatus(
                RuntimeStatusState.Connected,
                ayanami.Id,
                endpoint,
                string.Empty,
                true,
                appliedTargetCount: 1,
                connectedInstance: identity);
            var statusReads = 0;
            var launchCalls = 0;
            var applyCalls = 0;

            var basePackage = App.ReadEmbeddedBasePackage();
            window = new MainWindow(
                store,
                basePackage,
                previewProfileDirectory: Path.Combine(testRoot, "preview-profile"),
                readRuntimeStatusOnLoad: true,
                editorClosePrompt: _ => MessageBoxResult.No,
                codexInstanceService: instances,
                runtimeStatusReader: _ =>
                {
                    Interlocked.Increment(ref statusReads);
                    return Task.FromResult(status);
                },
                startupPreferencesService: startupPreferences)
            {
                Width = 1240,
                Height = 840,
                Left = -2_000,
                Top = -2_000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            window.SetRuntimeApplyWorkflowForTesting(new RuntimeApplyWorkflow(
                (_, _) =>
                {
                    Interlocked.Increment(ref launchCalls);
                    return Task.FromResult(new RuntimeStatus(RuntimeStatusState.Connecting, null, endpoint, string.Empty, true));
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref applyCalls);
                    return Task.FromResult(status);
                },
                instances,
                readinessTimeout: TimeSpan.FromMilliseconds(50),
                pollInterval: TimeSpan.FromMilliseconds(5)));

            window.Show();
            await WaitForMainWindowAsync(window);
            await WaitUntilAsync(
                () => Find<TextBlock>(window, "RuntimeStatusText").Text == AppLocalization.Get("已连接并已应用"),
                "fake runtime reaches connected and applied status");
            StopRuntimeStatusRefreshTimer(window);

            var list = Find<ListBox>(window, "SkinList");
            var search = Find<TextBox>(window, "SearchBox");
            search.Text = "用户自定义";
            await WaitUntilAsync(
                () => list.Items.Count == 2 && ReadSkinId(list.SelectedItem!) == ayanami.Id,
                "Chinese user search keeps the selected Ayanami card visible");
            await FlushUiAsync(window);

            var expectedOrder = new[] { saya.Id, ayanami.Id };
            Require(ReadVisibleSkinIds(list).SequenceEqual(expectedOrder, StringComparer.OrdinalIgnoreCase),
                "custom library ordering is established before changing languages");
            Require(Find<TextBlock>(window, "SelectedName").Text == ayanami.Name &&
                    Find<TextBlock>(window, "RuntimeStatusMessage").Text == AppLocalization.Format("已应用到 {0} 个 Codex 页面。", 1),
                "Chinese custom skin name and applied status appear in the real main window");
            Require(Find<TextBlock>(window, "RuntimeConnectedTargetText").ToolTip?.ToString()?.Contains(
                        identity.ExecutablePath, StringComparison.Ordinal) == true,
                "the fake connected process path is visible as user data");

            AppLocalization.SetLanguage(AppLanguage.English);
            await FlushUiAsync(window);
            await RequireEnglishMainWindowAsync(
                window,
                list,
                ayanami.Id,
                saya.Id,
                identity,
                expectedOrder,
                basePackage.SourceVersion);
            Require(Find<TextBlock>(window, "SelectedName").Text == ayanami.Name &&
                    Find<TextBlock>(window, "RuntimeConnectedTargetText").ToolTip?.ToString()?.Contains(
                        identity.ExecutablePath, StringComparison.Ordinal) == true,
                "skin names and user-provided process paths remain unchanged in the English main window");
            await SaveScreenshotAsync(window, Path.Combine(screenshotDirectory, "mainwindow-en-day.png"));

            AppThemeService.SetMode(AppThemeMode.Night);
            await FlushUiAsync(window);
            Require(AppThemeService.Mode == AppThemeMode.Night && AppLocalization.IsEnglish,
                "night palette change keeps the English interface active");
            await SaveScreenshotAsync(window, Path.Combine(screenshotDirectory, "mainwindow-en-night.png"));

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await FlushUiAsync(window);
            Require(Math.Abs(window.ActualWidth - 960) < 2 && Math.Abs(window.ActualHeight - 640) < 2,
                "minimum-size screenshot uses the real 960×640 main window");
            RequireRuntimeControlsWithinBounds(window, identity.ProcessId, ayanami.Name);
            RequirePreviewViewportFitsMetadataSlot(window);
            await SaveScreenshotAsync(window, Path.Combine(screenshotDirectory, "mainwindow-en-minimum-960x640.png"));

            window.Width = 1240;
            window.Height = 840;
            await FlushUiAsync(window);
            Find<Button>(window, "EditSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var editor = Find<SkinEditorView>(window, "SkinEditor");
            await WaitUntilAsync(() => editor.IsLoaded && editor.Visibility == Visibility.Visible,
                "editor opens for the currently selected custom skin");

            var nameInput = Find<TextBox>(editor, "NameInput");
            nameInput.Text = "未保存的中文草稿名称";
            var editorTabs = Find<TabControl>(editor, "EditorTabs");
            editorTabs.SelectedIndex = 1;
            var scopeInput = Find<ComboBox>(editor, "ScopeInput");
            await FlushUiAsync(window);

            var backgroundPath = Find<TextBlock>(editor, "BackgroundImagePathText");
            var expectedBackgroundPath = ayanami.ImagePath
                ?? throw new InvalidOperationException("Customized Ayanami fixture did not retain its background path.");
            var baseEditorDiagnostic =
                $"dirty={editor.IsDirty}, name='{nameInput.Text}', scope='{editor.SelectedScope}', " +
                $"backgroundPathText='{backgroundPath.Text}', backgroundPathToolTip='{backgroundPath.ToolTip}', " +
                $"draftBackgroundImagePath='{ReadDraftBackgroundImagePath(editor)}'";
            Console.WriteLine($"English editor base-scope diagnostic: {baseEditorDiagnostic}");
            Require(editor.IsDirty && nameInput.Text == "未保存的中文草稿名称" &&
                    editor.SelectedScope == "base" && backgroundPath.Text == expectedBackgroundPath &&
                    backgroundPath.ToolTip?.ToString() == expectedBackgroundPath &&
                    ReadDraftBackgroundImagePath(editor) == expectedBackgroundPath,
                $"English base editor displays the unchanged Chinese asset path beside the dirty draft ({baseEditorDiagnostic})");
            await SaveScreenshotAsync(window, Path.Combine(screenshotDirectory, "mainwindow-en-editor-base-image-path.png"));

            scopeInput.SelectedItem = scopeInput.Items.Cast<ComboBoxItem>()
                .Single(item => string.Equals(item.Tag?.ToString(), "dark", StringComparison.Ordinal));
            await FlushUiAsync(window);
            var expectedInheritedImageLabel = AppLocalization.Get("沿用基础背景图");
            var expectedInheritedImageTooltip = AppLocalization.Get("当前范围未覆盖图片，将沿用基础背景图");
            var draftBackgroundImagePath = ReadDraftBackgroundImagePath(editor);
            var editorStateDiagnostic =
                $"dirty={editor.IsDirty}, name='{nameInput.Text}', scope='{editor.SelectedScope}', " +
                $"backgroundPathText='{backgroundPath.Text}', backgroundPathToolTip='{backgroundPath.ToolTip}', " +
                $"draftBackgroundImagePath='{draftBackgroundImagePath}'";
            Console.WriteLine($"English editor state diagnostic: {editorStateDiagnostic}");
            Require(editor.IsDirty && nameInput.Text == "未保存的中文草稿名称" &&
                    editor.SelectedScope == "dark" && backgroundPath.Text == expectedInheritedImageLabel &&
                    backgroundPath.ToolTip?.ToString() == expectedInheritedImageTooltip &&
                    draftBackgroundImagePath == expectedBackgroundPath,
                $"English dark-scope editor preserves its dirty draft and inherited Chinese asset path ({editorStateDiagnostic})");
            Require(ReadVisibleSkinIds(list).SequenceEqual(expectedOrder, StringComparer.OrdinalIgnoreCase) &&
                    ReadSkinId(list.SelectedItem!) == ayanami.Id && search.Text == "用户自定义",
                "opening the editor preserves the selected skin, filtered order, and search input");
            await SaveScreenshotAsync(window, Path.Combine(screenshotDirectory, "mainwindow-en-editor.png"));

            var workflowCallsBeforeSwitch = Volatile.Read(ref launchCalls) + Volatile.Read(ref applyCalls);
            var statusReadsBeforeSwitch = Volatile.Read(ref statusReads);
            AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
            await FlushUiAsync(window);

            Require(Find<Button>(window, "NewSkinButton").Content?.ToString() == "新建皮肤" &&
                    Find<TextBlock>(window, "SortStatusText").Text == AppLocalization.Get("搜索结果已筛选；清空搜索后可排序") &&
                    Find<TextBlock>(window, "RuntimeStatusText").Text == AppLocalization.Get("已连接并已应用") &&
                    Find<TextBlock>(window, "RuntimeStatusMessage").Text == AppLocalization.Format("已应用到 {0} 个 Codex 页面。", 1),
                "Chinese navigation, filtered-list state, and live applied-status text return in the actual window");
            RequireChineseVersionAndPreviewLabels(window, basePackage.SourceVersion);
            Require(editor.IsDirty && nameInput.Text == "未保存的中文草稿名称" &&
                    editor.SelectedScope == "dark" &&
                    backgroundPath.Text == AppLocalization.Get("沿用基础背景图") &&
                    backgroundPath.ToolTip?.ToString() == AppLocalization.Get("当前范围未覆盖图片，将沿用基础背景图") &&
                    ReadDraftBackgroundImagePath(editor) == expectedBackgroundPath &&
                    ReadSkinId(list.SelectedItem!) == ayanami.Id && search.Text == "用户自定义" &&
                    ReadVisibleSkinIds(list).SequenceEqual(expectedOrder, StringComparer.OrdinalIgnoreCase),
                "language switching preserves the unsaved editor name, scope, image path, selected skin, search, and order");
            Require(Volatile.Read(ref launchCalls) + Volatile.Read(ref applyCalls) == workflowCallsBeforeSwitch &&
                    Volatile.Read(ref statusReads) == statusReadsBeforeSwitch,
                "language switching issues no connect/apply workflow call and performs no runtime-status refresh");
            RequirePreviewOptionsSummary(window, english: false);

            Console.WriteLine($"MainWindow localization screenshots saved to: {screenshotDirectory}");
        }
        finally
        {
            if (window?.IsVisible == true)
            {
                window.Close();
            }

            AppLocalization.SetLanguage(previousLanguage);
            AppThemeService.SetMode(previousTheme);
            TryDeleteDirectory(testRoot);
        }
    }

    private static Task RequireEnglishMainWindowAsync(
        MainWindow window,
        ListBox list,
        string ayanamiId,
        string sayaId,
        CodexProcessIdentity identity,
        IReadOnlyList<string> expectedOrder,
        string baseVersion)
    {
        var newSkinButton = Find<Button>(window, "NewSkinButton");
        var marketButton = Find<Button>(window, "MarketEntryButton");
        var supportButton = Find<Button>(window, "SupportAuthorButton");
        var updatesButton = Find<Button>(window, "UpdateEntryButton");
        var settingsButton = Find<Button>(window, "SettingsEntryButton");
        var search = Find<TextBox>(window, "SearchBox");
        var searchHint = Find<TextBlock>(window, "SearchHint");
        var sortStatus = Find<TextBlock>(window, "SortStatusText");
        var selectedAppearance = Find<TextBlock>(window, "SelectedAppearance");
        var runtimeStatus = Find<TextBlock>(window, "RuntimeStatusText");
        var runtimeMessage = Find<TextBlock>(window, "RuntimeStatusMessage");

        Require(window.Title == AppLocalization.Get("ChatGPT 皮肤管理器") &&
                AutomationProperties.GetName(marketButton) == AppLocalization.Get("打开皮肤市场") &&
                AutomationProperties.GetName(supportButton) == AppLocalization.Get("支持作者") &&
                AutomationProperties.GetName(updatesButton) == AppLocalization.Get("版本与更新") &&
                AutomationProperties.GetName(settingsButton) == AppLocalization.Get("设置") &&
                newSkinButton.Content?.ToString() == AppLocalization.Get("新建皮肤") &&
                Find<Button>(window, "ImportButton").Content?.ToString() == AppLocalization.Get("导入皮肤") &&
                search.Text == "用户自定义" && searchHint.Text == AppLocalization.Get("搜索名称或 ID"),
            "main title, all navigation/action labels, and search hint render in English without changing user search text");
        Require(!ContainsCjk(AutomationProperties.GetName(marketButton)) &&
                !ContainsCjk(AutomationProperties.GetName(supportButton)) &&
                !ContainsCjk(AutomationProperties.GetName(updatesButton)) &&
                !ContainsCjk(AutomationProperties.GetName(settingsButton)) &&
                !ContainsCjk(newSkinButton.Content?.ToString() ?? string.Empty) &&
                sortStatus.Text == AppLocalization.Get("搜索结果已筛选；清空搜索后可排序") &&
                !ContainsCjk(sortStatus.Text),
            "navigation and current filtered-list status are English rather than just English resource lookups");
        Require(runtimeStatus.Text == AppLocalization.Get("已连接并已应用") &&
                !ContainsCjk(runtimeStatus.Text) &&
                runtimeMessage.Text == AppLocalization.Format("已应用到 {0} 个 Codex 页面。", 1) &&
                !ContainsCjk(runtimeMessage.Text),
            "the connected-and-applied state and its page-count message update to English in place");
        Require(ReadSkinId(list.SelectedItem!) == ayanamiId &&
                ReadVisibleSkinIds(list).SequenceEqual(expectedOrder, StringComparer.OrdinalIgnoreCase) &&
                Find<TextBlock>(window, "SelectedName").Text == "用户自定义·綾波" &&
                Find<TextBlock>(window, "RuntimeConnectedTargetText").ToolTip?.ToString()?.Contains(
                    identity.ExecutablePath, StringComparison.Ordinal) == true,
            "English refresh retains the custom skin, its selection/order, and the connected process path");
        Require(selectedAppearance.Text.Contains("Package", StringComparison.OrdinalIgnoreCase) &&
                !ContainsCjk(selectedAppearance.Text),
            "skin metadata labels refresh in English while their package values remain intact");

        RequireEnglishVersionAndPreviewLabels(window, baseVersion);
        RequirePreviewOptionsSummary(window, english: true);

        var appearanceRows = FindVisualChildren<TextBlock>(list)
            .Where(textBlock => textBlock.Name == "RowAppearanceLabel")
            .Select(textBlock => textBlock.Text)
            .ToArray();
        Require(appearanceRows.Length == 2 && appearanceRows.All(text => !ContainsCjk(text)),
            $"both visible skin-list appearance labels render in English (Ayanami={ayanamiId}, Saya={sayaId})");
        return Task.CompletedTask;
    }

    private static void RequireEnglishVersionAndPreviewLabels(MainWindow window, string baseVersion)
    {
        var updateLabel = Find<TextBlock>(window, "UpdateEntryLabel");
        var updateFullLabel = AppLocalization.Format("版本与更新 · {0}", UpdateCoordinator.ReadCurrentVersionTag());
        var previewBaseVersion = Find<TextBlock>(window, "PreviewBaseVersion");
        var previewLabel = AppLocalization.Format("原生静态样板 · {0}", baseVersion);
        var previewTooltip = AppLocalization.Format(
            "依据本地原生静态样板构建的结构预览，不是当前运行的真实客户端截图。来源版本：{0}",
            baseVersion);

        Require(updateLabel.Text == AppLocalization.Get("版本与更新") &&
                updateLabel.ToolTip?.ToString() == updateFullLabel &&
                Find<Button>(window, "UpdateEntryButton").ToolTip?.ToString() == updateFullLabel &&
                !ContainsCjk(updateLabel.Text) && !ContainsCjk(updateFullLabel),
            $"the update navigation label and version tooltip refresh in English while retaining the version ({updateLabel.Text}; {updateLabel.ToolTip})");
        Require(previewBaseVersion.Text == previewLabel &&
                previewBaseVersion.ToolTip?.ToString() == previewTooltip &&
                !ContainsCjk(previewBaseVersion.Text) && !ContainsCjk(previewTooltip),
            $"the native preview label and explanatory tooltip refresh in English while retaining the source version ({previewBaseVersion.Text})");
    }

    private static void RequireChineseVersionAndPreviewLabels(MainWindow window, string baseVersion)
    {
        var updateLabel = Find<TextBlock>(window, "UpdateEntryLabel");
        var updateFullLabel = AppLocalization.Format("版本与更新 · {0}", UpdateCoordinator.ReadCurrentVersionTag());
        var previewBaseVersion = Find<TextBlock>(window, "PreviewBaseVersion");

        Require(updateLabel.Text == AppLocalization.Get("版本与更新") &&
                updateLabel.ToolTip?.ToString() == updateFullLabel &&
                Find<Button>(window, "UpdateEntryButton").ToolTip?.ToString() == updateFullLabel,
            $"the update navigation label and version tooltip return to Chinese without changing the version ({updateLabel.Text}; {updateLabel.ToolTip})");
        Require(previewBaseVersion.Text == AppLocalization.Format("原生静态样板 · {0}", baseVersion) &&
                previewBaseVersion.ToolTip?.ToString() == AppLocalization.Format(
                    "依据本地原生静态样板构建的结构预览，不是当前运行的真实客户端截图。来源版本：{0}",
                    baseVersion),
            $"the native preview label and explanatory tooltip return to Chinese without changing the source version ({previewBaseVersion.Text})");
    }

    private static void RequirePreviewOptionsSummary(MainWindow window, bool english)
    {
        const string source = "外观、场景、侧栏和原版对照只影响预览；透明度也会用于下一次应用。";
        var expected = AppLocalization.Get(source);
        var summary = FindVisualChildren<TextBlock>(window)
            .SingleOrDefault(textBlock => textBlock.ToolTip?.ToString() == expected)
            ?? throw new InvalidOperationException("MainWindow preview-options summary with a tooltip was not found.");
        Require(summary.Text == expected && !string.IsNullOrWhiteSpace(summary.ToolTip?.ToString()) &&
                summary.ToolTip?.ToString() == expected &&
                (english ? !ContainsCjk(summary.Text) : ContainsCjk(summary.Text)),
            $"preview-options summary updates in {(english ? "English" : "Chinese")} and retains its full tooltip ({summary.Text})");
    }

    private static void VerifyRawTextDoesNotReapplyRemovedLocalizationSource()
    {
        const string rawPath = "素材/自定义中文路径.png";
        var probe = new TextBlock();
        LocalizedUiText.SetText(probe, "新建皮肤");
        Require(probe.Text == AppLocalization.Get("新建皮肤"),
            "raw-text probe starts with a registered static label");

        LocalizedUiText.SetRawText(probe, rawPath);
        AppLocalization.SetLanguage(AppLanguage.English);
        Require(probe.Text == rawPath,
            "SetRawText unregisters the prior static source before English refresh without requiring garbage collection");

        AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
        Require(probe.Text == rawPath,
            "SetRawText keeps the Chinese user path unchanged on the next language refresh");
    }

    private static void RequireRuntimeControlsWithinBounds(
        MainWindow window,
        int connectedProcessId,
        string selectedSkinName)
    {
        var controlsBorder = Find<Border>(window, "RuntimeControlsBorder");
        var selectedSkin = Find<TextBlock>(window, "RuntimeSelectedSkinText");
        var target = Find<TextBlock>(window, "RuntimeTargetText");
        var connectedLabel = Find<TextBlock>(window, "RuntimeConnectedTargetLabel");
        var connectedTarget = Find<TextBlock>(window, "RuntimeConnectedTargetText");
        var connectionHeader = FindVisualChildren<TextBlock>(controlsBorder)
            .SingleOrDefault(textBlock => textBlock.Text == AppLocalization.Get("连接与应用"));
        var actionButtons = new[]
        {
            Find<Button>(window, "SelectRuntimeTargetButton"),
            Find<Button>(window, "ApplyRuntimeButton"),
            Find<Button>(window, "RemoveRuntimeButton")
        };
        var contents = new FrameworkElement?[]
        {
            connectionHeader,
            selectedSkin,
            target,
            connectedLabel,
            connectedTarget
        }.Concat(actionButtons.Cast<FrameworkElement?>()).ToArray();
        var viewport = new Rect(0, 0, controlsBorder.ActualWidth, controlsBorder.ActualHeight);
        var bounds = contents
            .Where(element => element is not null)
            .Select(element => $"{element!.Name}:{GetBounds(controlsBorder, element)}")
            .ToArray();
        var expectedTarget = AppLocalization.Format(
            "自动查找调试版 · 端口 {0}",
            Find<TextBox>(window, "RuntimePortInput").Text.Trim());

        Console.WriteLine($"English 960×640 runtime-controls bounds: {string.Join("; ", bounds)}");
        Console.WriteLine(
            $"English 960×640 runtime-controls text: header='{connectionHeader?.Text}', " +
            $"selectedSkin='{selectedSkin.Text}' (expected '{selectedSkinName}'), " +
            $"target='{target.Text}' (expected current auto-target '{expectedTarget}'), " +
            $"connectedLabel='{connectedLabel.Text}' (expected '{AppLocalization.Get("当前连接")}'), " +
            $"connectedTarget='{connectedTarget.Text}' (expected 'PID {connectedProcessId}'), " +
            $"buttons={string.Join("; ", actionButtons.Select(button =>
                $"{button.Name}:visible={button.Visibility}/{button.IsVisible}, enabled={button.IsEnabled}, content='{button.Content}'"))}");
        Require(connectionHeader is not null && connectionHeader.Text == AppLocalization.Get("连接与应用") &&
                selectedSkin.Text == selectedSkinName && target.Text == expectedTarget &&
                connectedLabel.Text == AppLocalization.Get("当前连接") &&
                connectedTarget.Text == $"PID {connectedProcessId}" &&
                actionButtons.All(button => button.Visibility == Visibility.Visible && button.IsVisible) &&
                contents.All(element => element is not null &&
                    IsWithinBounds(GetBounds(controlsBorder, element), viewport)),
            $"English runtime header, custom skin/target, PID, and all three action buttons fit inside the 960×640 runtime panel ({string.Join("; ", bounds)})");
    }

    private static void RequirePreviewViewportFitsMetadataSlot(MainWindow window)
    {
        var details = Find<Grid>(window, "DetailsScroll");
        var previewContainer = Find<Border>(window, "PreviewContainer");
        var previewViewport = Find<Grid>(window, "PreviewViewport");
        var metadata = Find<Grid>(window, "SelectedMetadata");
        var previewRow = window.FindName("PreviewRow") as RowDefinition
            ?? throw new InvalidOperationException("MainWindow preview row definition was not found.");
        var previewBounds = GetBounds(details, previewContainer);
        var viewportBounds = GetBounds(previewContainer, previewViewport);
        var metadataBounds = GetBounds(details, metadata);
        var detailsBounds = new Rect(0, 0, details.ActualWidth, details.ActualHeight);
        var containerBounds = new Rect(0, 0, previewContainer.ActualWidth, previewContainer.ActualHeight);
        Console.WriteLine(
            $"English 960×640 preview layout: row={previewRow.ActualHeight:0.##}, " +
            $"container={previewBounds}, viewport={viewportBounds}, metadata={metadataBounds}");

        Require(previewRow.ActualHeight > 0 &&
                previewContainer.ActualHeight <= previewRow.ActualHeight + 1 &&
                previewViewport.ActualHeight <= previewRow.ActualHeight + 1 &&
                IsWithinBounds(previewBounds, detailsBounds) &&
                IsWithinBounds(viewportBounds, containerBounds) &&
                previewBounds.Bottom <= metadataBounds.Top + 1,
            $"English preview viewport stays inside its row and does not overlap skin metadata at 960×640 ({previewBounds}; {viewportBounds}; {metadataBounds})");
    }

    private static byte[] CustomizePackage(byte[] bytes, string name, string imageStem)
    {
        using var sourceStream = new MemoryStream(bytes, writable: false);
        using var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = sourceArchive.Entries.Single(entry =>
            string.Equals(Path.GetFileName(entry.FullName), "skin.json", StringComparison.OrdinalIgnoreCase));
        var prefix = manifestEntry.FullName[..^"skin.json".Length];
        JsonObject manifest;
        using (var input = manifestEntry.Open())
        using (var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            manifest = JsonNode.Parse(reader.ReadToEnd())?.AsObject()
                ?? throw new InvalidDataException("Fixture skin.json must contain an object.");
        }

        manifest["name"] = name;
        var background = manifest["background"] as JsonObject
            ?? throw new InvalidDataException("Fixture skin.json must contain a background object.");
        var oldImagePath = background["image"]?.GetValue<string>()
            ?? throw new InvalidDataException("Fixture must contain a background image path.");
        var extension = Path.GetExtension(oldImagePath);
        var newImagePath = $"{imageStem}{extension}";
        background["image"] = newImagePath;
        var oldImageEntryName = prefix + oldImagePath.Replace('\\', '/');
        var newImageEntryName = prefix + newImagePath;
        if (!sourceArchive.Entries.Any(entry => string.Equals(entry.FullName, oldImageEntryName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Fixture background image entry was not found: {oldImageEntryName}");
        }

        using var resultStream = new MemoryStream();
        using (var resultArchive = new ZipArchive(resultStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in sourceArchive.Entries)
            {
                var targetName = string.Equals(entry.FullName, manifestEntry.FullName, StringComparison.OrdinalIgnoreCase)
                    ? entry.FullName
                    : string.Equals(entry.FullName, oldImageEntryName, StringComparison.OrdinalIgnoreCase)
                        ? newImageEntryName
                        : entry.FullName;
                var target = resultArchive.CreateEntry(targetName, CompressionLevel.Optimal);
                using var targetStream = target.Open();
                if (string.Equals(entry.FullName, manifestEntry.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
                    targetStream.Write(manifestBytes);
                }
                else
                {
                    using var entryStream = entry.Open();
                    entryStream.CopyTo(targetStream);
                }
            }
        }

        return resultStream.ToArray();
    }

    private static async Task SaveScreenshotAsync(MainWindow window, string path)
    {
        await FlushUiAsync(window);
        var browser = Find<WebView2>(window, "PreviewBrowser");
        await Program.SaveManagerWindowScreenshotAsync(window, browser, path);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"MainWindow screenshot was not created: {path}");
        }
    }

    private static async Task WaitForMainWindowAsync(MainWindow window)
    {
        await WaitUntilAsync(
            () => window.IsLoaded &&
                  Find<WebView2>(window, "PreviewBrowser").CoreWebView2 is not null &&
                  Find<WebView2>(window, "PreviewBrowser").ActualHeight > 0,
            "main window and local preview browser are ready");
    }

    private static async Task FlushUiAsync(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(() =>
        {
            window.UpdateLayout();
            Find<WebView2>(window, "PreviewBrowser").UpdateLayout();
        }, DispatcherPriority.Render);
        await Task.Delay(80);
    }

    private static void StopRuntimeStatusRefreshTimer(MainWindow window)
    {
        var timer = typeof(MainWindow).GetField("_runtimeStatusRefreshTimer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(window) as DispatcherTimer
            ?? throw new InvalidOperationException("MainWindow runtime status timer was not found.");
        timer.Stop();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"MainWindow language check timed out: {label}.");
            }

            await Task.Delay(25);
        }
    }

    private static T Find<T>(Window window, string name) where T : FrameworkElement =>
        window.FindName(name) is T element
            ? element
            : throw new InvalidOperationException($"Window {window.Title} is missing control {name}.");

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) is T element
            ? element
            : throw new InvalidOperationException($"Element {root.Name} is missing control {name}.");

    private static string[] ReadVisibleSkinIds(ListBox list) =>
        list.Items.Cast<object>().Select(ReadSkinId).ToArray();

    private static string ReadSkinId(object card)
    {
        var entry = card.GetType().GetProperty("Entry")?.GetValue(card)
            ?? throw new InvalidOperationException("Skin card has no catalog entry.");
        return entry.GetType().GetProperty("Id")?.GetValue(entry) as string
            ?? throw new InvalidOperationException("Skin card catalog entry has no ID.");
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

    private static bool ContainsCjk(string value) => value.Any(character => character is >= '\u3400' and <= '\u9FFF');

    private static Rect GetBounds(FrameworkElement ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

    private static bool IsWithinBounds(Rect bounds, Rect viewport) =>
        bounds.Left >= viewport.Left - 1 && bounds.Top >= viewport.Top - 1 &&
        bounds.Right <= viewport.Right + 1 && bounds.Bottom <= viewport.Bottom + 1;

    private static string? ReadDraftBackgroundImagePath(SkinEditorView editor) =>
        editor.Draft.Manifest["background"]?["image"]?.GetValue<string>();

    private static byte[] ReadEmbeddedPackage(string name)
    {
        var resource = $"SkinManager.Preview.Tests.Resources.{name}.zip";
        using var source = typeof(MainWindowLanguageChecks).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Fixture skin package is missing: {resource}");
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"MainWindow language check failed: {message}");
        }

        Console.WriteLine($"Passed: {message}");
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

    private sealed class FixtureCodexInstanceService(IReadOnlyList<CodexInstance> instances) : ICodexInstanceService
    {
        public Task<IReadOnlyList<CodexInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(instances);

        public Task<CodexLocateResult> LocateAsync(CodexInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(CodexLocateResult.NoWindow);

        public Task<CodexCloseOperationResult> CloseAsync(
            CodexInstance instance,
            bool force = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexCloseOperationResult(CodexCloseResult.Closed, instance, "Fixture only."));
    }

    private sealed class FakeStartupRunRegistration : IStartupRunRegistration
    {
        public bool IsEnabled() => false;

        public void SetEnabled(bool enabled) => throw new InvalidOperationException("This test must not write a startup registration.");
    }
}

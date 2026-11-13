using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Rendering;
using SkinManager.Runtime;

namespace SkinManager.Preview.Tests;

internal static class BetaWorkspaceChecks
{
    private const string SkinOrderDragFormat = "SkinManager.SkinId";

    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"SkinManager-beta-workspace-{Guid.NewGuid():N}"));
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Beta 工作区检查的临时目录超出系统临时目录。");
        }

        Directory.CreateDirectory(root);
        try
        {
            var ayanami = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Ayanami"));
            var saya = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Saya"));
            var native = App.ReadEmbeddedBasePackage();

            VerifySkinOrderPersistence(root, ayanami, saya);
            await VerifyLibraryReorderAndPreviewAsync(root, ayanami, saya, native);
            await VerifySettingsLanguageAndTabsAsync(root, ayanami, saya, native);
            await VerifyRuntimeResultSurvivesPassiveRefreshAsync(root, native);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void VerifySkinOrderPersistence(string root, SkinPackage ayanami, SkinPackage saya)
    {
        var store = CreateStore(Path.Combine(root, "order-skins"), ayanami, saya);
        var path = Path.Combine(store.DataDirectory, ".skin-order.json");
        var order = new SkinOrderStore(path);
        var initialIds = store.GetCatalog().Select(entry => entry.Id).ToArray();
        var initialOrder = order.Reconcile(initialIds);
        Require(initialOrder.SequenceEqual(initialIds, StringComparer.OrdinalIgnoreCase),
            "首次载入建立当前皮肤顺序");
        Require(order.Move(saya.Id, ayanami.Id, insertAfter: false),
            "通过皮肤 ID 将 Saya 移到 Ayanami 前面");

        var persistedOrder = new SkinOrderStore(path).Reconcile(initialIds);
        Require(persistedOrder.SequenceEqual([saya.Id, ayanami.Id], StringComparer.OrdinalIgnoreCase),
            "重建排序存储后仍保留用户排序");

        store.Delete(ayanami.Id);
        var afterDelete = order.Reconcile(store.GetCatalog().Select(entry => entry.Id));
        Require(afterDelete.SequenceEqual([saya.Id], StringComparer.OrdinalIgnoreCase),
            "删除 Ayanami 后保留 Saya 的相对顺序并清除失效 ID");

        store.Save(ayanami, replaceExisting: false);
        var afterImport = order.Reconcile(store.GetCatalog().Select(entry => entry.Id));
        Require(afterImport.SequenceEqual([saya.Id, ayanami.Id], StringComparer.OrdinalIgnoreCase),
            "重新导入 Ayanami 后追加到现有顺序末尾");
        Require(File.Exists(path), "皮肤顺序单独保存在皮肤库目录，不改皮肤包和 ID");
    }

    private static async Task VerifyLibraryReorderAndPreviewAsync(
        string root,
        SkinPackage ayanami,
        SkinPackage saya,
        BasePackage native)
    {
        var managerRoot = Path.Combine(root, "manager");
        var store = CreateStore(Path.Combine(managerRoot, "skins"), ayanami, saya);
        var profile = Path.Combine(managerRoot, "preview-profile");
        var window = CreateMainWindow(store, native, profile, readRuntimeStatusOnLoad: false);
        window.Show();
        try
        {
            await WaitForMainPreviewAsync(window, "Beta 主窗口加载 Ayanami 离线预览");
            var list = Find<ListBox>(window, "SkinList");
            Require(ReadVisibleSkinIds(list).SequenceEqual([ayanami.Id, saya.Id], StringComparer.OrdinalIgnoreCase),
                "未自定义排序时按当前皮肤库目录顺序显示");
            var itemStyle = list.ItemContainerStyle
                ?? throw new InvalidOperationException("皮肤卡片没有直接列表样式。");
            var dragDown = itemStyle.Setters.OfType<EventSetter>()
                .Any(setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
            var dragMove = itemStyle.Setters.OfType<EventSetter>()
                .Any(setter => setter.Event == UIElement.PreviewMouseMoveEvent);
            Require(dragDown && dragMove && !FindVisualChildren<Button>(GetContainer(list, 0)).Any(),
                "拖动手势直接绑定整张卡片，没有独立手柄按钮");
            var dragDistance = SystemParameters.MinimumHorizontalDragDistance;
            var dragStart = new Point(20, 20);
            Require(!MainWindow.HasPassedSkinDragThreshold(dragStart, new Point(20 + dragDistance - 1, 20)) &&
                    MainWindow.HasPassedSkinDragThreshold(dragStart, new Point(20 + dragDistance + 1, 20)),
                "单击移动距离不触发拖动，超过系统阈值后可拖动");

            var search = Find<TextBox>(window, "SearchBox");
            search.Text = "ayanami";
            await WaitUntilAsync(() => list.Items.Count == 1 && Equals(list.Tag, false), "搜索过滤后关闭拖动排序");
            Require(Find<TextBlock>(window, "SortStatusText").Text.Contains("清空搜索", StringComparison.Ordinal),
                "过滤状态说明清空搜索后才能排序");
            var filteredTarget = GetContainer(list, 0);
            var filteredDrag = RaiseDragEvent(
                filteredTarget,
                DragDrop.DragOverEvent,
                new DataObject(SkinOrderDragFormat, saya.Id),
                new Point(8, 2));
            Require(filteredDrag.Effects == DragDropEffects.None &&
                    ReadVisibleSkinIds(list).SequenceEqual([ayanami.Id], StringComparer.OrdinalIgnoreCase),
                "搜索结果过滤时拒绝列表拖动排序");

            search.Text = string.Empty;
            await WaitUntilAsync(() => list.Items.Count == 2 && Equals(list.Tag, true), "清空搜索后恢复排序");
            var firstItem = GetContainer(list, 0);
            var reorderData = new DataObject(SkinOrderDragFormat, saya.Id);
            var dragOver = RaiseDragEvent(firstItem, DragDrop.DragOverEvent, reorderData, new Point(8, 2));
            Require(dragOver.Effects == DragDropEffects.Move,
                "SkinList 的实际 DragOver 路由接受皮肤 ID 拖动");
            var drop = RaiseDragEvent(firstItem, DragDrop.DropEvent, reorderData, new Point(8, 2));
            Require(drop.Effects == DragDropEffects.Move &&
                    ReadVisibleSkinIds(list).SequenceEqual([saya.Id, ayanami.Id], StringComparer.OrdinalIgnoreCase),
                "SkinList 的实际 Drop 路由将 Saya 放在 Ayanami 前面");
            var savedOrder = new SkinOrderStore(Path.Combine(store.DataDirectory, ".skin-order.json"))
                .Reconcile(store.GetCatalog().Select(entry => entry.Id));
            Require(savedOrder.SequenceEqual([saya.Id, ayanami.Id], StringComparer.OrdinalIgnoreCase),
                "主界面拖动结果持久化到 skin ID 顺序");

            store.Delete(ayanami.Id);
            InvokePrivate(window, "RefreshList", saya.Id);
            await WaitUntilAsync(() => ReadVisibleSkinIds(list).SequenceEqual([saya.Id], StringComparer.OrdinalIgnoreCase),
                "删除皮肤后刷新列表");
            await InvokeTaskAsync(window, "SaveImportedPackageAsync", ayanami, null);
            await WaitUntilAsync(() => ReadVisibleSkinIds(list).SequenceEqual([saya.Id, ayanami.Id], StringComparer.OrdinalIgnoreCase),
                "经主界面导入路径将 Ayanami 追加到末尾");
            Require(list.SelectedItem is not null &&
                    string.Equals(ReadSkinId(list.SelectedItem), ayanami.Id, StringComparison.OrdinalIgnoreCase),
                "导入后仍选中刚导入的 Ayanami");

            var version = Find<TextBlock>(window, "AppVersionLabel");
            Require(version.Text == "1.1.4-beta.2",
                "主窗口显示本轮公开测试版本标签");
            var updateLabel = Find<TextBlock>(window, "UpdateEntryLabel");
            Require(updateLabel.Text == "版本与更新" &&
                    updateLabel.ToolTip?.ToString()?.Contains(version.Text, StringComparison.OrdinalIgnoreCase) == true,
                "无更新时更新入口保留短标签并在 Tooltip 显示完整版本");
            var screenshotDirectory = Program.FindTestOutputDirectory();
            var browser = Find<WebView2>(window, "PreviewBrowser");
            window.Width = 1240;
            window.Height = 840;
            window.UpdateLayout();
            await WaitForMainPreviewAsync(window, "常规窗口 Ayanami 预览稳定");
            await Program.SaveManagerWindowScreenshotAsync(
                window,
                browser,
                Path.Combine(screenshotDirectory, "beta2-main-ayanami-1240x840.png"));

            window.Width = 960;
            window.Height = 640;
            window.UpdateLayout();
            await WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                return window.ActualWidth >= 959 && window.ActualHeight >= 639 &&
                       Find<Grid>(window, "PreviewViewport").ActualHeight > 0;
            }, "主窗口最小 960×640 尺寸完成布局");
            var entryNames = new[] { "SupportAuthorButton", "MarketEntryButton", "UpdateEntryButton", "SettingsEntryButton" };
            var entryBounds = entryNames.Select(name => GetBounds(window, Find<Button>(window, name))).ToArray();
            Require(entryNames.All(name => Find<Button>(window, name).IsVisible) &&
                    Find<TextBlock>(window, "AppVersionLabel").IsVisible &&
                    entryBounds.All(bounds => IsWithinBounds(bounds, new Rect(0, 0, window.ActualWidth, window.ActualHeight))) &&
                    !entryBounds.Where((bounds, index) => entryBounds.Skip(index + 1).Any(bounds.IntersectsWith)).Any(),
                "最小窗口的版本标签与支持作者、市场、更新、设置入口均完整且不重叠");
            Require(Find<Button>(window, "SelectRuntimeTargetButton").IsVisible,
                "连接区保留‘选择窗口’入口");
            await Program.SaveManagerWindowScreenshotAsync(
                window,
                browser,
                Path.Combine(screenshotDirectory, "beta2-main-ayanami-960x640.png"));

            window.Width = 1240;
            window.Height = 840;
            window.UpdateLayout();
            await WaitForMainPreviewAsync(window, "恢复常规尺寸后预览稳定");
            await VerifyPreviewCloseAndDraftReopenAsync(window, ayanami.Id);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static async Task VerifyPreviewCloseAndDraftReopenAsync(MainWindow window, string expectedDraftId)
    {
        var editor = Find<SkinEditorView>(window, "SkinEditor");
        Find<Button>(window, "EditSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => editor.Visibility == Visibility.Visible &&
            Find<TextBlock>(window, "PreviewStatus").Text == "预览草稿 · 点击组件可切换编辑目标",
            "进入 Ayanami 草稿预览");

        var oldRenderer = GetRenderer(window);
        var host = Find<Grid>(window, "PreviewBrowserHost");
        var previewContainer = Find<Border>(window, "PreviewContainer");
        var previewViewport = Find<Grid>(window, "PreviewViewport");
        var selectedMetadata = Find<Grid>(window, "SelectedMetadata");
        var oldBrowser = Find<WebView2>(window, "PreviewBrowser");
        Find<Button>(window, "PreviewPowerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var previewRow = Find<Grid>(window, "DetailsScroll").RowDefinitions[3];
        await WaitUntilAsync(() =>
            previewContainer.Visibility == Visibility.Collapsed &&
            Find<Grid>(window, "PreviewClosedHeader").Visibility == Visibility.Visible &&
            Find<Grid>(window, "PreviewActiveHeader").Visibility == Visibility.Collapsed &&
            previewRow.Height.Value == 0 && previewRow.MinHeight == 0 &&
            FindVisualChildren<WebView2>(host).Count() == 0 && GetRendererOrNull(window) is null,
            "关闭预览时完整折叠容器与布局行并释放 WebView2");
        await Program.SaveWindowScreenshotAsync(
            window,
            Path.Combine(Program.FindTestOutputDirectory(), "beta2-main-preview-closed-1240x840.png"));

        Find<Button>(window, "PreviewReopenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(async () =>
        {
            var currentRenderer = GetRendererOrNull(window);
            var currentBrowser = FindVisualChildren<WebView2>(host).SingleOrDefault();
            var previewBounds = GetBounds(window, previewContainer);
            var viewportBounds = GetBounds(window, previewViewport);
            var metadataBounds = GetBounds(window, selectedMetadata);
            if (currentRenderer is null || ReferenceEquals(currentRenderer, oldRenderer) ||
                currentBrowser is null || ReferenceEquals(currentBrowser, oldBrowser) ||
                currentBrowser.CoreWebView2 is null ||
                previewContainer.Visibility != Visibility.Visible ||
                !previewRow.Height.IsStar || previewRow.MinHeight <= 0 ||
                Math.Abs(previewRow.MinHeight - previewViewport.MinHeight) > 0.1 ||
                previewRow.ActualHeight <= 0 || previewViewport.ActualHeight < previewViewport.MinHeight - 1 ||
                !previewViewport.IsVisible ||
                !IsWithinBounds(viewportBounds, previewBounds) || previewBounds.Bottom > metadataBounds.Top + 1 ||
                Find<TextBlock>(window, "PreviewStatus").Text != "预览草稿 · 点击组件可切换编辑目标" ||
                editor.Visibility != Visibility.Visible)
            {
                return false;
            }

            try
            {
                if (!string.Equals(editor.Draft.BuildPackage().Id, expectedDraftId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            var pageJson = await currentBrowser.CoreWebView2.ExecuteScriptAsync("""
                (() => `${document.documentElement.getAttribute('data-skin-preview-mode') || ''}:${
                  document.querySelector('[data-fixture-page="task"]') ? 'conversation' : 'home'
                }`)()
                """);
            return JsonSerializer.Deserialize<string>(pageJson) == "package:home" &&
                   await currentRenderer.ExecuteScriptAsync(
                       "window.__SKIN_MANAGER_COMPONENT_PICKER__?.enabled === true;") == "true";
        }, "重开后重建 WebView2 并恢复 Ayanami 草稿");
        var renderer = GetRenderer(window);
        Require(await renderer.ExecuteScriptAsync("window.__SKIN_MANAGER_COMPONENT_PICKER__?.enabled === true;") == "true",
            "重开后的预览仍启用草稿组件选择");
        Find<Button>(editor, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => editor.Visibility == Visibility.Collapsed, "取消草稿并回到皮肤库");
    }

    private static async Task VerifySettingsLanguageAndTabsAsync(
        string root,
        SkinPackage ayanami,
        SkinPackage saya,
        BasePackage native)
    {
        var managerRoot = Path.Combine(root, "settings");
        var store = CreateStore(Path.Combine(managerRoot, "skins"), ayanami, saya);
        var profile = Path.Combine(managerRoot, "profile");
        Directory.CreateDirectory(profile);
        AppLocalization.Initialize(Path.Combine(managerRoot, "language.json"));
        AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
        var compatibilityPath = Path.Combine(profile, "compatibility-settings.json");
        const string legacySettings = "{\"forceSimplifiedChinese\":true}";
        File.WriteAllText(compatibilityPath, legacySettings);

        var window = CreateMainWindow(store, native, profile, readRuntimeStatusOnLoad: false);
        window.Show();
        try
        {
            await WaitForMainPreviewAsync(window, "设置迁移用主窗口加载");
            Find<Button>(window, "SettingsEntryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = await WaitForSettingsWindowAsync(window);
            Require(settings.Title == "设置" &&
                    Find<TabItem>(settings, "GeneralTab").Header?.ToString() == "常规" &&
                    Find<TabItem>(settings, "AppearanceTab").Header?.ToString() == "外观" &&
                    Find<TabItem>(settings, "CompatibilityTab").Header?.ToString() == "兼容性",
                "设置入口打开标题为‘设置’并显示横向分类标签");
            Require(settings.FindName("ForceSimplifiedChineseCheckBox") is null &&
                    settings.FindName("LanguageSavedStatusText") is null &&
                    File.ReadAllText(compatibilityPath) == legacySettings,
                "旧固定中文偏好不再出现在设置界面，也不会因打开设置被改写");

            var languageBox = Find<ComboBox>(settings, "LanguageBox");
            languageBox.SelectedItem = languageBox.Items.OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag?.ToString(), nameof(AppLanguage.English), StringComparison.Ordinal));
            await WaitUntilAsync(() => settings.Title == "Settings", "切换英文设置窗口");
            Require(Find<TabItem>(settings, "GeneralTab").Header?.ToString() == "General" &&
                    Find<TabItem>(settings, "CompatibilityTab").Header?.ToString() == "Compatibility",
                "真实主窗口的设置标签会随偏好即时切换语言");

            Find<TabControl>(settings, "SettingsTabs").SelectedItem = Find<TabItem>(settings, "CompatibilityTab");
            var settingsScrollViewer = Find<ScrollViewer>(settings, "SettingsScrollViewer");
            var refreshCompatibility = Find<Button>(settings, "InspectButton");
            var screenshotDirectory = Program.FindTestOutputDirectory();
            settings.Width = 760;
            settings.Height = 570;
            settings.UpdateLayout();
            await WaitUntilAsync(() => settings.ActualWidth >= 759 && settings.ActualHeight >= 569,
                "设置窗口常规尺寸完成布局");
            await WaitUntilAsync(() => refreshCompatibility.IsVisible &&
                IsWithinBounds(GetBounds(settings, refreshCompatibility), GetBounds(settings, settingsScrollViewer)),
                "兼容性检测入口显示在760×570设置视口");
            await settings.Dispatcher.InvokeAsync(() => settings.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
            Require(Find<TextBlock>(settings, "CodexConfigStatusText").Text.Length > 0 &&
                    refreshCompatibility.IsVisible &&
                    IsWithinBounds(GetBounds(settings, refreshCompatibility), GetBounds(settings, settingsScrollViewer)),
                "760×570设置截图显示配置状态与Magpie重新检测入口");
            await Program.SaveWindowScreenshotAsync(
                settings,
                Path.Combine(screenshotDirectory, "beta2-settings-compatibility-760x570.png"));

            settings.Width = 620;
            settings.Height = 440;
            settings.UpdateLayout();
            await WaitUntilAsync(() => settings.ActualWidth >= 619 && settings.ActualHeight >= 439,
                "设置窗口最小尺寸完成布局");
            refreshCompatibility.BringIntoView();
            await settings.Dispatcher.InvokeAsync(() => settings.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
            await WaitUntilAsync(() => refreshCompatibility.IsVisible &&
                IsWithinBounds(GetBounds(settings, refreshCompatibility), GetBounds(settings, settingsScrollViewer)),
                "兼容性检测入口显示在620×440设置视口");
            await settings.Dispatcher.InvokeAsync(() => settings.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
            Require(refreshCompatibility.IsVisible &&
                    IsWithinBounds(GetBounds(settings, refreshCompatibility), GetBounds(settings, settingsScrollViewer)),
                "620×440设置截图仍能显示兼容性检测入口");
            await Program.SaveWindowScreenshotAsync(
                settings,
                Path.Combine(screenshotDirectory, "beta2-settings-compatibility-620x440.png"));
            settings.Close();
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
            AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    private static async Task VerifyRuntimeResultSurvivesPassiveRefreshAsync(string root, BasePackage native)
    {
        var store = new SkinStore(Path.Combine(root, "runtime-error-skins"));
        store.Initialize();
        var pollStatus = new RuntimeStatus(RuntimeStatusState.Stopped, null, null, "缓存未连接", false);
        var window = new MainWindow(
            store,
            native,
            Path.Combine(root, "runtime-error-profile"),
            readRuntimeStatusOnLoad: true,
            runtimeStatusReader: _ => Task.FromResult(pollStatus))
        {
            Width = 960,
            Height = 640,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await WaitUntilAsync(() => Find<TextBlock>(window, "RuntimeStatusText").Text == "未连接" &&
                !GetField<bool>(window, "_runtimeStatusRefreshInProgress"), "初始被动状态刷新结束");
            const string operationError = "模拟本次皮肤应用失败：目标页面未响应。";
            InvokePrivate(window, "SetRuntimeError", operationError, "本次操作未完成。");

            pollStatus = new RuntimeStatus(
                RuntimeStatusState.Connected,
                "old-skin",
                "http://127.0.0.1:9335",
                "缓存中的旧连接状态",
                true,
                1);
            await InvokeTaskAsync(window, "RefreshRuntimeStatusAsync");
            Require(Find<TextBlock>(window, "RuntimeStatusText").Text == "本次操作未完成" &&
                    Find<TextBlock>(window, "RuntimeStatusMessage").Text == operationError,
                "被动 oldConnected 状态不覆盖本次操作错误状态和原因");

            pollStatus = new RuntimeStatus(RuntimeStatusState.Stopped, null, null, "目标已断开", false);
            await InvokeTaskAsync(window, "RefreshRuntimeStatusAsync");
            Require(Find<TextBlock>(window, "RuntimeStatusText").Text == "未连接" &&
                    Find<TextBlock>(window, "RuntimeStatusMessage").Text == operationError,
                "真实断开状态可更新，同时保留本次操作结果直到新操作开始");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static SkinStore CreateStore(string path, SkinPackage ayanami, SkinPackage saya)
    {
        var store = new SkinStore(path);
        store.Initialize();
        store.Save(ayanami, replaceExisting: false);
        store.Save(saya, replaceExisting: false);
        return store;
    }

    private static MainWindow CreateMainWindow(
        SkinStore store,
        BasePackage native,
        string profile,
        bool readRuntimeStatusOnLoad) =>
        new(store, native, profile, readRuntimeStatusOnLoad: readRuntimeStatusOnLoad,
            codexConfigReadabilityService: new FixtureCodexConfigReadabilityService(
                Path.Combine(profile, "codex-fixture", "config.toml")))
        {
            Width = 1240,
            Height = 840,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false
        };

    private sealed class FixtureCodexConfigReadabilityService(string path) : ICodexConfigReadabilityService
    {
        public CodexConfigReadabilityResult Inspect() => new(path, CodexConfigReadabilityState.Missing);
    }

    private static string[] ReadVisibleSkinIds(ListBox list) =>
        list.Items.Cast<object>().Select(ReadSkinId).ToArray();

    private static string ReadSkinId(object card)
    {
        var entry = card.GetType().GetProperty("Entry", BindingFlags.Instance | BindingFlags.Public)?.GetValue(card)
            ?? throw new InvalidOperationException("皮肤列表项目没有公开 SkinCatalogEntry。");
        return entry.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)?.GetValue(entry) as string
            ?? throw new InvalidOperationException("皮肤列表项目没有 Skin ID。");
    }

    private static ListBoxItem GetContainer(ListBox list, int index)
    {
        list.UpdateLayout();
        return list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem
            ?? throw new InvalidOperationException($"皮肤列表尚未生成第 {index} 项容器。");
    }

    private static DragEventArgs RaiseDragEvent(
        ListBoxItem source,
        RoutedEvent routedEvent,
        IDataObject data,
        Point point)
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

    private static async Task WaitForMainPreviewAsync(MainWindow window, string label)
    {
        await WaitUntilAsync(() =>
        {
            if (!window.IsLoaded || window.FindName("PreviewBrowser") is not WebView2 browser ||
                browser.CoreWebView2 is null || window.FindName("PreviewStatus") is not TextBlock status)
            {
                return false;
            }

            return status.Visibility == Visibility.Collapsed;
        }, label);
    }

    private static async Task<SettingsWindow> WaitForSettingsWindowAsync(MainWindow owner)
    {
        SettingsWindow? found = null;
        await WaitUntilAsync(() =>
        {
            found = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault(window =>
                ReferenceEquals(window.Owner, owner) && window.IsLoaded);
            return found is not null;
        }, "设置窗口打开");
        return found!;
    }

    private static ThemePreviewRenderer GetRenderer(MainWindow window) => GetRendererOrNull(window)
        ?? throw new InvalidOperationException("主窗口没有活动预览渲染器。");

    private static ThemePreviewRenderer? GetRendererOrNull(MainWindow window) =>
        typeof(MainWindow).GetField("_previewRenderer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            as ThemePreviewRenderer;

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) is T element
            ? element
            : throw new InvalidOperationException($"缺少测试控件 {name}。");

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

    private static object? InvokePrivate(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, arguments);
    }

    private static async Task InvokeTaskAsync(object target, string name, params object?[] arguments)
    {
        if (InvokePrivate(target, name, arguments) is Task task)
        {
            await task;
        }
    }

    private static bool GetField<T>(object target, string name) where T : struct =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) is T value &&
        Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);

    private static Rect GetBounds(FrameworkElement ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(), element.RenderSize));

    private static bool IsWithinBounds(Rect bounds, Rect viewport) =>
        bounds.Left >= viewport.Left - 1 && bounds.Top >= viewport.Top - 1 &&
        bounds.Right <= viewport.Right + 1 && bounds.Bottom <= viewport.Bottom + 1;

    private static void Toggle(CheckBox checkBox)
    {
        var peer = new CheckBoxAutomationPeer(checkBox);
        var toggle = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider
            ?? throw new InvalidOperationException("简体中文开关没有 Toggle 自动化操作。");
        toggle.Toggle();
    }

    private static byte[] ReadEmbeddedPackage(string name)
    {
        var resourceName = $"SkinManager.Preview.Tests.Resources.{name}.zip";
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"缺少嵌入的 {name} 测试包。");
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"Beta 工作区检查失败：{label}超时。");
            }

            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"Beta 工作区检查失败：{label}超时。");
            }

            await Task.Delay(25);
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Beta 工作区检查失败：{label}");
        }

        Console.WriteLine($"通过：{label}");
    }
}

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Core;
using SkinManager.Desktop;

namespace SkinManager.Preview.Tests;

internal static class CreationWorkflowChecks
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
        AppLocalization.Initialize(Path.Combine(testRoot, "editor-language.json"));
        AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
        await VerifyCreationWorkflowAsync(testRoot, basePackage, ayanamiBytes, screenshotDirectory);
        await VerifyCopyWorkflowAsync(testRoot, basePackage, ayanamiBytes, sayaBytes, screenshotDirectory);
    }

    private static async Task VerifyCreationWorkflowAsync(
        string testRoot,
        BasePackage basePackage,
        byte[] ayanamiBytes,
        string screenshotDirectory)
    {
        var store = new SkinStore(Path.Combine(testRoot, "creation-library"));
        store.Initialize();
        Check(store.GetCatalog().Count == 0, "创作专项从空库启动");
        var window = CreateWindow(store, basePackage, Path.Combine(testRoot, "creation-preview"));
        window.Show();
        var editor = Find<SkinEditorView>(window, "SkinEditor");

        // Exercise the real startup race: open the editor before WebView2 initialization completes.
        Find<Button>(window, "NewSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(editor.IsNew && editor.IsCreationMode, "空库中新建直接进入创作页");
        await WaitUntilAsync(() => Task.FromResult(Find<TextBox>(editor, "NameInput").IsVisible),
            "进入新建皮肤后名称输入在保存区保持可见");
        Check(Find<Button>(editor, "CancelButton").IsVisible && Find<Button>(editor, "SaveButton").IsVisible,
            "新建草稿的创建与取消操作可见");
        Check(!Find<Button>(editor, "SaveAsButton").IsVisible &&
              Find<Grid>(editor, "SaveActionsGrid").ColumnDefinitions[4].Width.Value == 0,
            "新建保存区隐藏另存为并回收空列宽");

        var followTransparency = Find<CheckBox>(window, "PreviewTransparencyFollowCheckBox");
        var transparencyInput = Find<TextBox>(window, "PreviewTransparencyInput");
        followTransparency.IsChecked = false;
        transparencyInput.Text = "83";
        await CommitTransparencyAsync(window);

        var renderer = GetRenderer(window);
        var browser = Find<WebView2>(window, "PreviewBrowser");
        await WaitUntilAsync(() => Task.FromResult(
            browser.Visibility == Visibility.Visible && renderer.CurrentResolvedSkin is not null),
            "WebView2初始化后恢复首个草稿预览");
        Check(Find<TextBlock>(window, "PreviewTransparencyValue").Text.Contains("草稿默认值", StringComparison.Ordinal),
            "创作预览显示草稿默认透明度");
        Check(!followTransparency.IsEnabled && !transparencyInput.IsEnabled && transparencyInput.Text == "83",
            "创作时禁用应用透明度覆盖并保留用户设置");
        var initialDraft = editor.Draft.BuildPackage();
        Check(GetPreviewOptions(window, initialDraft).UserTransparency is null,
            "手动应用透明度不覆盖未保存草稿预览");
        Check(renderer.CurrentResolvedSkin?.PanelOpacityOverride is null,
            "草稿解析使用皮肤自身面板透明度");

        await CaptureAsync(window, browser, Path.Combine(screenshotDirectory, "创作-常规-1240x840.png"));
        window.Width = 960;
        window.Height = 640;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout);
        await CaptureAsync(window, browser, Path.Combine(screenshotDirectory, "创作-最小-960x640-浅色.png"));

        var creationAppearance = Find<ComboBox>(editor, "CreationAppearanceInput");
        SelectByTag(creationAppearance, "dark");
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Dark), "创作明暗切到深色");
        Check(ReadString(editor.Draft.Manifest["appearance"]) == "dark" &&
              ReadString(editor.Draft.Manifest["tokens"] as System.Text.Json.Nodes.JsonObject, "color.surface.window") == "#202735",
            "深色切换更新默认外观与内置基础色");
        await CaptureAsync(window, browser, Path.Combine(screenshotDirectory, "创作-最小-960x640-深色.png"));

        var modeTabs = Find<TabControl>(editor, "EditorModeTabs");
        modeTabs.SelectedIndex = 1;
        await WaitUntilAsync(() => Task.FromResult(Find<ComboBox>(editor, "ScopeInput").IsVisible), "切入精细编辑并显示写入范围");
        var scope = Find<ComboBox>(editor, "ScopeInput");
        SelectByTag(scope, "dark");
        await WaitUntilAsync(() => Task.FromResult(renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Dark),
            "精细编辑范围同步到深色预览");
        await VerifyEditorCatalogLocalizationAsync(window, editor, scope);
        await CaptureAsync(window, browser, Path.Combine(screenshotDirectory, "精细编辑-最小-960x640-深色.png"));

        // A preview-only appearance switch must leave the explicit dark write scope in place.
        SelectByTag(Find<ComboBox>(window, "PreviewAppearanceBox"), "light");
        await WaitUntilAsync(() => Task.FromResult(renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Light),
            "右侧预览可以独立切换浅色查看");
        Check(Find<ComboBox>(editor, "ScopeInput").SelectedItem is ComboBoxItem { Tag: "dark" },
            "右侧预览切换不会改变精细编辑范围");
        var tokenFields = Find<ItemsControl>(editor, "TokenFields");
        var accentOverride = tokenFields.Items.Cast<SkinEditorView.SkinEditorField>()
            .Single(field => field.Path.SequenceEqual(new[] { "variants", "dark", "tokens", "color.accent" }));
        Check(accentOverride.Value.Length == 0, "深色变体强调色初始跟随基础范围");
        accentOverride.Value = "#2CA38E";
        Check(ReadString(editor.Draft.Manifest["variants"]?["dark"]?["tokens"] as System.Text.Json.Nodes.JsonObject,
                  "color.accent") == "#2CA38E",
            "主动编辑后只写入 variants.dark 覆盖");
        accentOverride.Value = string.Empty;
        Check(editor.Draft.Manifest["variants"]?["dark"]?["tokens"]?["color.accent"] is null,
            "清空单独覆盖后恢复跟随基础范围");

        // Switching back to the creator restores the draft's overall appearance preview.
        modeTabs.SelectedIndex = 0;
        await WaitUntilAsync(() => Task.FromResult(renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Dark),
            "返回创作页后预览回到草稿整体明暗");

        Find<Button>(editor, "CreationAccentPickerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var palettePopup = Find<Popup>(editor, "ColorPalettePopup");
        Check(palettePopup.IsOpen && Find<ColorPickerControl>(editor, "ColorPicker").IsVisible,
            "主色按钮打开可自由调节的应用内色盘");
        Find<ColorPickerControl>(editor, "ColorPicker").SetColor(new CssRgba(20, 184, 166, 255), notify: true);
        Check(Find<TextBox>(editor, "CreationAccentHexInput").Text == "#14B8A6" &&
              ReadString(editor.Draft.Manifest["tokens"] as System.Text.Json.Nodes.JsonObject, "color.accent") == "#14B8A6",
            "自由色盘即时同步主色HEX与草稿");
        Find<Button>(editor, "PaletteConfirmButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!palettePopup.IsOpen, "色盘确认关闭弹层");
        Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var imagePackage = SkinPackageReader.ReadZipBytes(ayanamiBytes);
        var imageBytes = imagePackage.ImageBytes ?? throw new InvalidDataException("Ayanami 缺少测试背景图。");
        var imagePath = Path.Combine(testRoot, "drop-ayanami.png");
        await File.WriteAllBytesAsync(imagePath, imageBytes);
        var dropZone = Find<Border>(editor, "BackgroundDropZone");
        Check(dropZone.AllowDrop, "背景素材区域允许拖放文件");
        editor.SetBackgroundImageFromFile(imagePath);
        await WaitUntilAsync(() => Task.FromResult(
            ReadString(editor.Draft.Manifest["background"] as System.Text.Json.Nodes.JsonObject, "image") is not null),
            "拖放图片写入草稿背景资源");
        var imageStatus = Find<TextBlock>(editor, "CreationBackgroundPathText");
        Check(imageStatus.Text == "已选择背景图片" &&
              !(imageStatus.ToolTip?.ToString() ?? string.Empty).Contains("assets/", StringComparison.OrdinalIgnoreCase),
            "创作页以简短状态显示图片，不暴露资源路径与哈希");
        var imageDraft = editor.Draft.BuildPackage();
        var draftImagePath = imageDraft.Definition.Background?.Properties.TryGetValue("image", out var imagePathValue) == true
            ? imagePathValue.Text!
            : throw new InvalidDataException("草稿背景图片路径未写入 SkinPackage。");
        Check(imageDraft.Files.TryGetValue(draftImagePath, out var storedImage) && storedImage.AsSpan().SequenceEqual(imageBytes),
            "拖入的Ayanami图片字节进入草稿资源");
        window.Width = 1240;
        window.Height = 840;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout);
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Background?.ImageBytes is { Length: > 0 }), "选入Ayanami后预览显示背景图片");
        await CaptureAsync(window, browser, Path.Combine(screenshotDirectory, "创作-选入Ayanami.png"));
        window.Width = 960;
        window.Height = 640;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout);

        var darkness = Find<Slider>(editor, "BackgroundDarknessSlider");
        darkness.Value = 37;
        Near(0.37, ReadNumber(editor.Draft.Manifest["background"]?["overlayOpacity"], double.NaN), 0.001,
            "背景明暗滑块映射到遮罩透明度");

        var accentInput = Find<TextBox>(editor, "CreationAccentHexInput");
        var accentBefore = ReadString(editor.Draft.Manifest["tokens"] as System.Text.Json.Nodes.JsonObject, "color.accent");
        accentInput.Text = "#A0B7DF";
        Check(ReadString(editor.Draft.Manifest["tokens"] as System.Text.Json.Nodes.JsonObject, "color.accent") == "#A0B7DF",
            "主色HEX写入基础令牌");
        Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReadString(editor.Draft.Manifest["tokens"] as System.Text.Json.Nodes.JsonObject, "color.accent") == accentBefore,
            "主色修改支持撤销");
        Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReadString(editor.Draft.Manifest["tokens"] as System.Text.Json.Nodes.JsonObject, "color.accent") == "#A0B7DF",
            "主色修改支持重做");

        var panelSlider = Find<Slider>(editor, "PanelTransparencySlider");
        var oldFill = ReadNumber(editor.Draft.Manifest["tokens"]?["material.fillOpacity"], 0.4);
        ChangeSliderInOneGesture(panelSlider, 42, 48, 54);
        Near(0.46, ReadNumber(editor.Draft.Manifest["tokens"]?["material.fillOpacity"], double.NaN), 0.001,
            "面板透明度滑块映射到主题填充透明度");
        Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Near(oldFill, ReadNumber(editor.Draft.Manifest["tokens"]?["material.fillOpacity"], double.NaN), 0.001,
            "透明度滑块整段手势只撤销一步");
        Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Near(0.46, ReadNumber(editor.Draft.Manifest["tokens"]?["material.fillOpacity"], double.NaN), 0.001,
            "透明度滑块整段手势可重做");

        var radiusSlider = Find<Slider>(editor, "CornerRadiusSlider");
        ChangeSliderInOneGesture(radiusSlider, 19, 22, 24);
        Near(24, ReadNumber(editor.Draft.Manifest["tokens"]?["radius.panel"], double.NaN), 0.001,
            "圆角滑块同步整体面板与控件圆角");
        Near(24, ReadNumber(editor.Draft.Manifest["tokens"]?["radius.control"], double.NaN), 0.001,
            "整体圆角同时更新控件圆角");

        var keyboardPanelValue = panelSlider.Value;
        RaiseSliderKeyDown(panelSlider, Key.Right);
        panelSlider.Value = 58;
        RaiseSliderKeyDown(panelSlider, Key.Right);
        panelSlider.Value = 62;
        RaiseSliderLostKeyboardFocus(panelSlider, window);
        var previousName = Find<TextBox>(editor, "NameInput").Text;
        Find<TextBox>(editor, "NameInput").Text = "Tab离开后的独立名称修改";
        Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Find<TextBox>(editor, "NameInput").Text == previousName && Math.Abs(panelSlider.Value - 62) < 0.001,
            "Tab离开滑块后名称修改可单独撤销，滑块手势已结束");
        Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Near(keyboardPanelValue, panelSlider.Value, 0.001, "失焦后滑块键盘手势单独撤销");

        var focusToggle = Find<CheckBox>(editor, "BackgroundFocusModeCheckBox");
        await WaitUntilAsync(() => Task.FromResult(renderer.CurrentResolvedSkin is { } resolved &&
            Math.Abs((resolved.Background?.OverlayOpacity ?? double.NaN) - 0.37) < 0.001 &&
            resolved.Tokens.TryGetValue("material.fillOpacity", out var renderedFill) &&
            Math.Abs((renderedFill.Number ?? double.NaN) - 0.46) < 0.001 &&
            resolved.Tokens.TryGetValue("radius.panel", out var renderedRadius) &&
            Math.Abs((renderedRadius.Number ?? double.NaN) - 24) < 0.001),
            "连续创作参数已刷新到右侧预览");
        focusToggle.IsChecked = true;
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "window.__SKIN_MANAGER_BACKGROUND_POSITION__?.enabled === true;") == "true",
            "连续样式调整后开启背景焦点拖动");
        await BackgroundDragChecks.RunAsync(browser, renderer);
        await WaitUntilAsync(() => Task.FromResult(
            editor.Draft.Manifest["background"]?["focusY"] is not null &&
            Math.Abs(ReadNumber(editor.Draft.Manifest["background"]?["focusY"], 0.5) - 0.5) > 0.001),
            "真实鼠标拖动contain留白更新草稿焦点坐标");
        var afterFocus = ReadNumber(editor.Draft.Manifest["background"]?["focusY"], 0.5);
        Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(editor.Draft.Manifest["background"]?["focusY"] is null,
            "一次焦点拖动可用一次撤销完整还原");
        Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Near(afterFocus, ReadNumber(editor.Draft.Manifest["background"]?["focusY"], 0.5), 0.002,
            "重做恢复焦点手势结果");

        focusToggle.IsChecked = false;
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "window.__SKIN_MANAGER_BACKGROUND_POSITION__ === undefined;") == "true",
            "关闭背景位置模式后移除拖动拦截");
        await renderer.ExecuteScriptAsync("document.querySelector('[data-skin-part=\"window\"]')?.click(); true;");
        await WaitUntilAsync(() => Task.FromResult(modeTabs.SelectedIndex == 1 &&
            Find<TabControl>(editor, "EditorTabs").SelectedItem is TabItem { Name: "ComponentsTab" }),
            "关闭拖动后预览组件点击仍进入组件精细编辑页");
        modeTabs.SelectedIndex = 0;

        var backgroundColor = Find<TextBox>(editor, "CreationBackgroundHexInput");
        // The image currently covers the HEX panel; use a solid fill after validating image drag.
        Find<Button>(editor, "CreationUseColorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(editor.Draft.Manifest["background"]?["image"] is null && !focusToggle.IsChecked.GetValueOrDefault(),
            "切换纯色背景会移除图片并关闭焦点模式");
        Check(editor.Draft.BuildPackage().ImageBytes is null, "纯色背景不携带已移除图片资源");
        var solidColorBeforeHex = ReadString(editor.Draft.Manifest["background"] as System.Text.Json.Nodes.JsonObject, "color");
        backgroundColor.Text = "#FFF4D6";
        Check(ReadString(editor.Draft.Manifest["background"] as System.Text.Json.Nodes.JsonObject, "color") == "#FFF4D6",
            "纯色HEX写入背景底色");

        // Removing the image is reversible; undo/redo keeps the scoped package asset alive when referenced.
        Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReadString(editor.Draft.Manifest["background"] as System.Text.Json.Nodes.JsonObject, "color") == solidColorBeforeHex &&
              editor.Draft.Manifest["background"]?["image"] is null,
            "撤销纯色背景颜色修改仍保留纯色步骤");
        Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReadString(editor.Draft.Manifest["background"] as System.Text.Json.Nodes.JsonObject, "image") is not null &&
              editor.Draft.BuildPackage().ImageBytes is { Length: > 0 },
            "撤销纯色切换恢复背景图片及资源");
        Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(editor.Draft.Manifest["background"]?["image"] is null, "重做纯色切换再次移除图片");
        Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReadString(editor.Draft.Manifest["background"] as System.Text.Json.Nodes.JsonObject, "color") == "#FFF4D6",
            "重做纯色背景颜色并恢复最终值");

        var newId = ReadString(editor.Draft.Manifest, "id")!;
        Find<Expander>(editor, "MetadataExpander").IsExpanded = true;
        var authorInput = Find<TextBox>(editor, "AuthorInput");
        Check(authorInput.Text.Length == 0 && editor.Draft.Manifest["author"] is null,
            "新建草稿作者默认留空且不自动填入固定作者");
        authorInput.Text = "创作作者验收";
        Check(ReadString(editor.Draft.Manifest, "author") == "创作作者验收", "作者信息写入已有author字段");
        authorInput.Text = string.Empty;
        Check(editor.Draft.Manifest["author"] is null, "清空作者后删除可选author字段");
        authorInput.Text = "创作作者验收";
        Find<TextBox>(editor, "NameInput").Text = "创作流程验收";
        var savedDraft = editor.Draft.BuildPackage();
        Check(GetPreviewOptions(window, savedDraft).UserTransparency is null,
            "保存前皮肤预览仍不受手动应用透明度影响");
        Check(renderer.CurrentResolvedSkin?.PanelOpacityOverride is null,
            "皮肤默认透明度在草稿预览中保持生效");
        Find<Button>(editor, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(!editor.IsDirty && editor.Visibility == Visibility.Collapsed && store.ContainsId(newId)),
            "新建皮肤使用名称区保存到皮肤库");
        await WaitUntilAsync(() => Task.FromResult(Find<CheckBox>(window, "PreviewTransparencyFollowCheckBox").IsEnabled),
            "保存完成并恢复预览透明度控件");
        var transparencyFollowEnabled = Find<CheckBox>(window, "PreviewTransparencyFollowCheckBox").IsEnabled;
        var restoredUserTransparency = GetPreviewOptions(window, store.LoadSkin(newId)).UserTransparency;
        Check(transparencyFollowEnabled && restoredUserTransparency == 83,
            $"退出编辑后恢复原有应用透明度偏好 (enabled={transparencyFollowEnabled}, value={restoredUserTransparency}, input={transparencyInput.Text})");
        Check(store.LoadSkin(newId).Definition.Author == "创作作者验收",
            "新建皮肤保存重载后保留填写的作者");

        CloseTestWindow(window);
    }

    private static async Task VerifyEditorCatalogLocalizationAsync(
        MainWindow window,
        SkinEditorView editor,
        ComboBox scope)
    {
        var editorTabs = Find<TabControl>(editor, "EditorTabs");
        var previousTab = editorTabs.SelectedItem;
        editorTabs.SelectedItem = Find<TabItem>(editor, "ComponentsTab");
        var componentInput = Find<ComboBox>(editor, "ComponentInput");
        var stateInput = Find<ComboBox>(editor, "ComponentStateInput");
        var propertyInput = Find<ComboBox>(editor, "AddComponentPropertyInput");
        var fields = Find<ItemsControl>(editor, "ComponentFields");

        componentInput.SelectedValue = "content";
        componentInput.SelectedValue = "window";
        stateInput.SelectedValue = "hover";
        var state = editor.Draft.Manifest["variants"]?["dark"]?["components"]?["window"]?["states"]?["hover"] as System.Text.Json.Nodes.JsonObject;
        var definition = SkinDefinitionCatalog.DefaultComponentEditorFields.FirstOrDefault(candidate =>
            candidate.EditorKind != SkinEditorFieldKind.Unsupported && state?[candidate.Key] is null)
            ?? throw new InvalidDataException("测试用例找不到可添加的组件属性。");
        propertyInput.SelectedValue = definition.Key;
        var addProperty = typeof(SkinEditorView).GetMethod("AddComponentPropertyButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SkinEditorView), "AddComponentPropertyButton_Click");
        addProperty.Invoke(editor, [new Button(), new RoutedEventArgs()]);
        var field = fields.Items.Cast<SkinEditorView.SkinEditorField>()
            .Single(candidate => candidate.Path[^1] == definition.Key);
        field.Value = field.IsColor ? "#C0FFEE" : "17";

        var draftJson = editor.Draft.Manifest.ToJsonString();
        var draftValue = field.Value;
        var draftChangedEvents = 0;
        editor.DraftChanged += (_, _) => draftChangedEvents++;
        AppLocalization.SetLanguage(AppLanguage.English);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
        Check(ReadOptionLabel(componentInput.SelectedItem) == "Window" &&
              ReadOptionLabel(stateInput.SelectedItem) == "Hover" &&
              ReadOptionLabel(propertyInput.SelectedItem) == AppLocalization.Get(definition.Label) &&
              field.Label == AppLocalization.Get(definition.Label),
            "英语下动态组件、状态、属性选项与字段标签来自 Core source 翻译");
        Check(editor.SelectedScope == "dark" && componentInput.SelectedValue as string == "window" &&
              stateInput.SelectedValue as string == "hover" && propertyInput.SelectedValue as string == definition.Key &&
              field.Value == draftValue && editor.Draft.Manifest.ToJsonString() == draftJson && editor.IsDirty &&
              draftChangedEvents == 0,
            "切换到英语保留写入范围、组件/属性/状态选择及脏草稿值");
        Check(Find<TextBlock>(editor, "EditorTitle").Text == "New skin" &&
              Find<Button>(editor, "SaveButton").Content?.ToString() == "Create skin",
            "编辑器标题和长保存按钮使用英语，未重建草稿表单");

        AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
        Check(ReadOptionLabel(componentInput.SelectedItem) == "窗口" &&
              ReadOptionLabel(stateInput.SelectedItem) == "悬停" &&
              ReadOptionLabel(propertyInput.SelectedItem) == definition.Label && field.Label == definition.Label &&
              field.Value == draftValue && editor.Draft.Manifest.ToJsonString() == draftJson &&
              editor.SelectedScope == "dark" && draftChangedEvents == 0,
            "切回中文后恢复 Core 中文source显示且不覆盖字段草稿或当前选择");

        field.Value = string.Empty;
        stateInput.SelectedValue = "default";
        editorTabs.SelectedItem = previousTab;
    }

    private static string ReadOptionLabel(object? option) =>
        option?.GetType().GetProperty("Label", BindingFlags.Instance | BindingFlags.Public)?.GetValue(option) as string
        ?? string.Empty;

    private static async Task VerifyCopyWorkflowAsync(
        string testRoot,
        BasePackage basePackage,
        byte[] ayanamiBytes,
        byte[] sayaBytes,
        string screenshotDirectory)
    {
        var store = new SkinStore(Path.Combine(testRoot, "copy-library"));
        store.Initialize();
        var ayanami = SkinPackageReader.ReadZipBytes(ayanamiBytes);
        var saya = SkinPackageReader.ReadZipBytes(sayaBytes);
        store.Save(ayanami, replaceExisting: false);
        store.Save(saya, replaceExisting: false);

        var window = CreateWindow(store, basePackage, Path.Combine(testRoot, "copy-preview"));
        window.Show();
        await WaitUntilAsync(() => Task.FromResult(Find<ListBox>(window, "SkinList").Items.Count == 2), "载入Ayanami与Saya样例皮肤");
        var renderer = GetRenderer(window);
        var browser = Find<WebView2>(window, "PreviewBrowser");
        await WaitUntilAsync(() => Task.FromResult(renderer.CurrentResolvedSkin is not null), "加载样例皮肤本地预览");

        // A visible original-base comparison must not leave the editor showing the base document.
        var originalBase = Find<CheckBox>(window, "PreviewOriginalBaseCheckBox");
        originalBase.IsChecked = true;
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "document.documentElement.getAttribute('data-skin-preview-mode') === 'base';") == "true",
            "原版对照切到基础预览");
        Find<Button>(window, "NewSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = Find<SkinEditorView>(window, "SkinEditor");
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "document.documentElement.getAttribute('data-skin-preview-mode') === 'package';") == "true",
            "从原版对照进入编辑后显示草稿皮肤");
        Check(!originalBase.IsChecked.GetValueOrDefault() && browser.Visibility == Visibility.Visible,
            "进入编辑时清除原版对照并恢复WebView预览");
        Find<Button>(editor, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(editor.Visibility == Visibility.Collapsed), "取消未改动的新建草稿");

        // The original-image overlay uses a separate WPF layer and must be cleared on editor entry too.
        Find<Button>(window, "OriginalButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(Find<Grid>(window, "OriginalImageLayer").Visibility == Visibility.Visible),
            "打开原图对照层");
        Find<Button>(window, "NewSkinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        editor = Find<SkinEditorView>(window, "SkinEditor");
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "document.documentElement.getAttribute('data-skin-preview-mode') === 'package';") == "true",
            "从原图层进入编辑后重新显示草稿");
        Check(Find<Grid>(window, "OriginalImageLayer").Visibility == Visibility.Collapsed &&
              browser.Visibility == Visibility.Visible,
            "进入编辑时隐藏原图层并恢复草稿预览");
        Find<Button>(editor, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(editor.Visibility == Visibility.Collapsed), "取消第二个未改动草稿");

        SelectSkin(window, saya.Id);
        await WaitUntilAsync(() => Task.FromResult(renderer.CurrentResolvedSkin?.Appearance == SkinAppearance.Dark),
            "选中Saya深色样例");
        var menu = Find<Button>(window, "MoreActionsButton").ContextMenu
            ?? throw new InvalidOperationException("皮肤更多操作菜单未创建。");
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        editor = Find<SkinEditorView>(window, "SkinEditor");
        Check(editor.IsNew && editor.IsCreationMode, "以Saya为基础创建进入简化创作页");
        var firstCopyId = ReadString(editor.Draft.Manifest, "id")!;
        Check(firstCopyId == $"{saya.Id}-copy" && !store.ContainsId(firstCopyId),
            "基于皮肤创建分配第一个唯一副本ID");
        var draftCopy = editor.Draft.BuildPackage();
        Check(draftCopy.Definition.Author == saya.Definition.Author && FilesEqual(draftCopy, saya),
            "副本保留Saya作者声明与所有图片资源");
        Near(saya.Definition.Tokens["radius.control"].Number ?? double.NaN,
            Find<Slider>(editor, "CornerRadiusSlider").Value, 0.001,
            "Saya副本创作页显示来源皮肤已有的控件圆角");
        var copyBrowser = Find<WebView2>(window, "PreviewBrowser");
        await WaitUntilAsync(() => Task.FromResult(
            renderer.CurrentResolvedSkin?.Background?.ImageBytes is { Length: > 0 }), "Saya副本创作预览保留背景图片");
        await WaitForCreatorPreviewStableAsync(window, editor, copyBrowser, renderer);
        await CaptureAsync(window, copyBrowser, Path.Combine(screenshotDirectory, "创作-以Saya为基础.png"));

        Find<TextBox>(editor, "NameInput").Text = "Saya 副本验收";
        Find<Button>(editor, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(editor.Visibility == Visibility.Collapsed && store.ContainsId(firstCopyId)),
            "保存Saya副本而不覆盖原皮肤");
        await WaitUntilAsync(() => Task.FromResult(Find<Button>(window, "ImportButton").IsEnabled),
            "副本保存与预览刷新完成后再创建第二个副本");
        var savedCopy = store.LoadSkin(firstCopyId);
        Check(savedCopy.Name == "Saya 副本验收" && savedCopy.Definition.Author == saya.Definition.Author && FilesEqual(savedCopy, saya),
            "副本保存名称且保留作者与背景图片资源");

        SelectSkin(window, saya.Id);
        await WaitUntilAsync(() => Task.FromResult(GetSelectedPackageId(window) == saya.Id), "重新选中Saya作为副本来源");
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        editor = Find<SkinEditorView>(window, "SkinEditor");
        var secondCopyId = ReadString(editor.Draft.Manifest, "id")!;
        var expectedSecondCopyId = $"{saya.Id}-copy-2";
        Check(secondCopyId == expectedSecondCopyId && !store.ContainsId(secondCopyId),
            $"再次创建副本使用新的唯一ID (actual={secondCopyId}, expected={expectedSecondCopyId})");
        Find<CheckBox>(editor, "BackgroundFocusModeCheckBox").IsChecked = true;
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "window.__SKIN_MANAGER_BACKGROUND_POSITION__?.enabled === true;") == "true",
            "副本开启背景焦点编辑");
        Find<Button>(editor, "CreationUseColorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "window.__SKIN_MANAGER_BACKGROUND_POSITION__ === undefined;") == "true",
            "纯色切换通知预览退出焦点模式");
        Check(Find<CheckBox>(editor, "BackgroundFocusModeCheckBox").IsChecked == false &&
              editor.Draft.Manifest["background"]?["image"] is null,
            "切换纯色后创作面板关闭图片焦点工具");
        Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(editor.Draft.BuildPackage().ImageBytes is { Length: > 0 },
            "撤销纯色切换后副本图片资源仍可恢复");
        var copyFocusToggle = Find<CheckBox>(editor, "BackgroundFocusModeCheckBox");
        copyFocusToggle.IsChecked = true;
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "window.__SKIN_MANAGER_BACKGROUND_POSITION__?.enabled === true;") == "true",
            "副本撤销恢复图片后可再次开启焦点模式");
        Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(async () => await renderer.ExecuteScriptAsync(
            "window.__SKIN_MANAGER_BACKGROUND_POSITION__ === undefined;") == "true",
            "重做移除图片时同步关闭预览焦点模式");
        Check(copyFocusToggle.IsChecked == false && editor.Draft.Manifest["background"]?["image"] is null,
            "重做撤销到无图状态后关闭焦点工具");

        editor.SetSkinId(saya.Id);
        Find<Button>(editor, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(Find<TextBlock>(editor, "ValidationErrorText").Visibility == Visibility.Visible),
            "同ID新建被阻止并显示恢复提示");
        Check(store.LoadSkin(saya.Id).Definition.Author == saya.Definition.Author && store.GetCatalog().Count == 3,
            "新建或副本ID冲突不会替换已有皮肤");
        editor.SetSkinId(secondCopyId);
        Find<TextBox>(editor, "NameInput").Text = "Saya 第二副本";
        Find<Button>(editor, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => Task.FromResult(editor.Visibility == Visibility.Collapsed && store.ContainsId(secondCopyId)),
            "唯一副本ID通过验证并保存");

        CloseTestWindow(window);
    }

    private static MainWindow CreateWindow(SkinStore store, BasePackage basePackage, string previewPath) => new(
        store,
        basePackage,
        previewProfileDirectory: previewPath,
        readRuntimeStatusOnLoad: false,
        closeAfterSuccessfulInstall: false)
    {
        Width = 1240,
        Height = 840,
        Left = -2_000,
        Top = -2_000,
        WindowStartupLocation = WindowStartupLocation.Manual,
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize
    };

    private static async Task CaptureAsync(MainWindow window, WebView2 browser, string path)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout);
        await Program.SaveManagerWindowScreenshotAsync(window, browser, path);
    }

    private static Task WaitForCreatorPreviewStableAsync(
        MainWindow window,
        SkinEditorView editor,
        WebView2 browser,
        ThemePreviewRenderer renderer) => WaitUntilAsync(() => Task.FromResult(
            editor.IsCreationMode &&
            editor.Visibility == Visibility.Visible && editor.Opacity >= 0.99 &&
            Find<Border>(window, "EditorPanel").Visibility == Visibility.Visible &&
            Find<Border>(window, "EditorPanel").Opacity >= 0.99 &&
            Find<Border>(window, "LibraryBorder").Visibility == Visibility.Collapsed &&
            Find<Grid>(window, "SelectedSummary").Visibility == Visibility.Collapsed &&
            browser.Visibility == Visibility.Visible &&
            Find<TextBlock>(window, "PreviewStatus").Text == "预览草稿 · 点击组件可切换编辑目标" &&
            Find<TextBlock>(window, "PreviewStatus").Opacity >= 0.99 &&
            renderer.CurrentResolvedSkin?.Background?.ImageBytes is { Length: > 0 }),
            "创作页切换与副本预览完整稳定后再截图");

    private static async Task CommitTransparencyAsync(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("CommitTransparencyInputAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "CommitTransparencyInputAsync");
        await (Task)(method.Invoke(window, null) ?? throw new InvalidOperationException("透明度提交没有返回任务。"));
    }

    private static PreviewOptions GetPreviewOptions(MainWindow window, SkinPackage package)
    {
        var method = typeof(MainWindow).GetMethod("GetPreviewOptions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "GetPreviewOptions");
        return (PreviewOptions)(method.Invoke(window, [package])
            ?? throw new InvalidOperationException("管理器没有返回预览选项。"));
    }

    private static ThemePreviewRenderer GetRenderer(MainWindow window) =>
        typeof(MainWindow).GetField("_previewRenderer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as ThemePreviewRenderer
        ?? throw new InvalidOperationException("管理器没有预览渲染器。");

    private static void ChangeSliderInOneGesture(Slider slider, params double[] values)
    {
        slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
        });
        foreach (var value in values)
        {
            slider.Value = value;
        }

        slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
        });
    }

    private static void RaiseSliderKeyDown(Slider slider, Key key)
    {
        var source = PresentationSource.FromVisual(slider)
            ?? throw new InvalidOperationException("创作滑块尚未连接到 WPF 窗口。");
        slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static void RaiseSliderLostKeyboardFocus(Slider slider, Window window) =>
        slider.RaiseEvent(new KeyboardFocusChangedEventArgs(
            Keyboard.PrimaryDevice, Environment.TickCount, slider, window)
        {
            RoutedEvent = Keyboard.LostKeyboardFocusEvent
        });

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<ComboBoxItem>()
            .Single(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    private static void SelectSkin(MainWindow window, string id)
    {
        var list = Find<ListBox>(window, "SkinList");
        list.SelectedItem = list.Items.Cast<object>().Single(item =>
        {
            var entry = item.GetType().GetProperty("Entry", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item);
            return string.Equals(entry?.GetType().GetProperty("Id")?.GetValue(entry)?.ToString(), id, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string? GetSelectedPackageId(MainWindow window) =>
        typeof(MainWindow).GetField("_selectedPackage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(window) is SkinPackage package ? package.Id : null;

    private static bool FilesEqual(SkinPackage left, SkinPackage right)
    {
        var leftFiles = left.Files.Where(pair => !pair.Key.Equals("skin.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var rightFiles = right.Files.Where(pair => !pair.Key.Equals("skin.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return leftFiles.Count == rightFiles.Count && leftFiles.All(pair =>
            rightFiles.TryGetValue(pair.Key, out var bytes) && pair.Value.AsSpan().SequenceEqual(bytes));
    }

    private static string? ReadString(System.Text.Json.Nodes.JsonObject? parent, string key) =>
        parent?[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? ReadString(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double ReadNumber(System.Text.Json.Nodes.JsonNode? node, double fallback) =>
        double.TryParse(node?.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : fallback;

    private static T Find<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"找不到控件 {name}。");

    private static void CloseTestWindow(MainWindow window)
    {
        typeof(MainWindow).GetField("_allowWindowClose", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(window, true);
        window.Close();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                Check(true, label);
                return;
            }

            await Task.Delay(80);
        }

        throw new TimeoutException($"等待超时：{label}");
    }

    private static void Near(double expected, double actual, double tolerance, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidDataException($"{label}：预期 {expected:0.###}，实际 {actual:0.###}。");
        }

        Check(true, $"{label} = {actual:0.###}");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{label}失败。");
        }

        Console.WriteLine($"通过：{label}");
    }
}

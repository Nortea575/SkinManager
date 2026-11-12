using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json.Nodes;
using SkinManager.Core;
using SkinManager.Desktop;

namespace SkinManager.Preview.Tests;

internal static class ColorPickerChecks
{
    internal static async Task RunAsync()
    {
        RunColorMathChecks();
        var screenshots = Program.FindTestOutputDirectory();
        Directory.CreateDirectory(screenshots);
        var testRoot = Path.Combine(Path.GetTempPath(), "SkinManagerColorPicker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        var package = SkinPackageReader.ReadZipBytes(ReadEmbeddedPackage("Ayanami"));
        var editor = new SkinEditorView();
        editor.SetDraft(SkinDraft.FromPackage(package), isNew: false);
        var workArea = SystemParameters.WorkArea;
        var window = new Window
        {
            Title = "SkinManager 色盘检查",
            Content = editor,
            Width = 470,
            Height = 640,
            Left = Math.Max(workArea.Left + 12, workArea.Right - 490),
            Top = Math.Max(workArea.Top + 12, workArea.Bottom - 660),
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.CanResize
        };

        var flushCount = 0;
        editor.PreviewFlushRequested += (_, _) => flushCount++;
        try
        {
            window.Show();
            await DispatchLayoutAsync(window, editor);

            await VerifyCancelPreservesHistoryAsync(window, editor, () => flushCount);
            await VerifyReferenceAndInheritanceAsync(window, editor);
            await VerifyOneUndoForWholeSessionAsync(window, editor);
            await VerifyReturnToOriginalIsNoOpAsync(window, editor);
            await VerifyTextInputAsync(window, editor);
            await VerifyColorEntryPointsAsync(window, editor);
            await CaptureThemeAndMinimumPopupAsync(window, editor, screenshots);
            await VerifyCreationBackgroundEntryPointAsync(window, editor);

            Console.WriteLine($"色盘专项截图：{Path.Combine(screenshots, "色盘-夜间.png")}");
            Console.WriteLine($"色盘专项截图：{Path.Combine(screenshots, "色盘-日间.png")}");
            Console.WriteLine($"色盘专项截图：{Path.Combine(screenshots, "色盘-靠底小窗口.png")}");
        }
        finally
        {
            if (FindPopup(editor).IsOpen)
            {
                FindButtonInPopup(editor, "PaletteCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            window.Close();
            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RunColorMathChecks()
    {
        var samples = new[]
        {
            new CssRgba(0, 0, 0, 0),
            new CssRgba(255, 255, 255, 255),
            new CssRgba(255, 0, 0, 173),
            new CssRgba(0, 255, 0, 128),
            new CssRgba(0, 0, 255, 1),
            new CssRgba(35, 92, 171, 219),
            new CssRgba(173, 49, 88, 64)
        };
        foreach (var sample in samples)
        {
            var hsv = ColorPickerMath.FromRgba(sample, retainedHue: 216);
            var roundTrip = ColorPickerMath.ToRgba(hsv, sample.Alpha);
            Require(Math.Abs(roundTrip.Red - sample.Red) <= 1 &&
                    Math.Abs(roundTrip.Green - sample.Green) <= 1 &&
                    Math.Abs(roundTrip.Blue - sample.Blue) <= 1 && roundTrip.Alpha == sample.Alpha,
                $"RGB/HSV 往返保留 {ColorPickerMath.ToCssHex(sample)} 与 alpha");
        }

        Require(Math.Abs(ColorPickerMath.FromRgba(new CssRgba(0, 0, 0, 255), 216).Hue - 216) < 0.001 &&
                Math.Abs(ColorPickerMath.FromRgba(new CssRgba(255, 255, 255, 255), 216).Hue - 216) < 0.001,
            "黑白色保持最近的色相位置");
        Require(ColorPickerMath.ToRgba(new HsvColor(0, 1, 1), 255) == new CssRgba(255, 0, 0, 255) &&
                ColorPickerMath.ToRgba(new HsvColor(120, 1, 1), 255) == new CssRgba(0, 255, 0, 255) &&
                ColorPickerMath.ToRgba(new HsvColor(240, 1, 1), 255) == new CssRgba(0, 0, 255, 255),
            "色相环的红、绿、蓝角度对应正确颜色");
        Require(CssColorParser.TryParse("#F08C", out var shortAlpha) &&
                shortAlpha == new CssRgba(255, 0, 136, 204) &&
                CssColorParser.TryParse("#12345678", out var longAlpha) &&
                longAlpha == new CssRgba(18, 52, 86, 120) &&
                CssColorParser.TryParse("rgba(18, 52, 86, 0.5)", out var rgba) &&
                rgba == new CssRgba(18, 52, 86, 128),
            "HEX、RGBA 的 alpha 顺序与 CSS 解析一致");
    }

    private static async Task VerifyCancelPreservesHistoryAsync(Window window, SkinEditorView editor, Func<int> readFlushCount)
    {
        var originalAccent = ReadString(editor.Draft.Manifest["tokens"]?["color.accent"])
            ?? throw new InvalidDataException("Ayanami 缺少基础强调色。");
        editor.SetSkinId("ayanami-color-picker-history");
        Click(Find<Button>(editor, "UndoButton"));
        Require(!editor.IsDirty && !Find<Button>(editor, "UndoButton").IsEnabled &&
                Find<Button>(editor, "RedoButton").IsEnabled,
            "取消基线同时保留一条可重做记录");

        var flushesBeforeCancel = readFlushCount();
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        var popup = FindPopup(editor);
        Require(ReadTextBoxInPopup(editor, "PaletteHexInput").Text == originalAccent.ToUpperInvariant(),
            "色盘打开时读取基础强调色");
        SelectColor(editor, new CssRgba(17, 34, 51, 255));
        FindColorPicker(editor).CompleteInteraction();
        Require(editor.IsDirty && ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == "#112233",
            "拖动过程实时更新草稿预览目标");
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));
        await DispatchLayoutAsync(window, editor);
        Require(!popup.IsOpen && !editor.IsDirty &&
                ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == originalAccent &&
                !Find<Button>(editor, "UndoButton").IsEnabled && Find<Button>(editor, "RedoButton").IsEnabled &&
                flushesBeforeCancel + 2 == readFlushCount(),
            "取消恢复原值、dirty 与 undo/redo，并 flush 预览");

        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        SelectColor(editor, new CssRgba(38, 68, 102, 255));
        popup.IsOpen = false;
        typeof(SkinEditorView).GetMethod("ColorPalettePopup_Closed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(editor, [popup, EventArgs.Empty]);
        await DispatchLayoutAsync(window, editor);
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == originalAccent && !editor.IsDirty,
            $"点击弹层外部取消颜色会话（isOpen={popup.IsOpen}, dirty={editor.IsDirty}, actual={ReadString(editor.Draft.Manifest["tokens"]?["color.accent"])}, expected={originalAccent}）");

        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        SelectColor(editor, new CssRgba(34, 68, 102, 255));
        RaiseEscapeInPopup(editor);
        await DispatchLayoutAsync(window, editor);
        Require(!FindPopup(editor).IsOpen && ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == originalAccent,
            "Escape 取消颜色会话并恢复草稿");

        Click(Find<Button>(editor, "RedoButton"));
        Require(ReadString(editor.Draft.Manifest["id"]) == "ayanami-color-picker-history" && editor.IsDirty,
            "颜色取消未清除原有 redo 历史和 dirty 结果");
        Click(Find<Button>(editor, "UndoButton"));
        Require(ReadString(editor.Draft.Manifest["id"]) == "ayanami" && !editor.IsDirty,
            "撤销原有历史后草稿回到基线");
        Require(readFlushCount() >= flushesBeforeCancel + 4,
            "鼠标释放、弹层外部关闭和 Escape 都请求预览最终刷新");
    }

    private static async Task VerifyReferenceAndInheritanceAsync(Window window, SkinEditorView editor)
    {
        var rawReference = ReadString(editor.Draft.Manifest["tokens"]?["color.border.focus"]);
        Require(rawReference == "{color.accent}", "测试夹具包含主色引用令牌");
        var undoWasEnabled = Find<Button>(editor, "UndoButton").IsEnabled;
        var redoWasEnabled = Find<Button>(editor, "RedoButton").IsEnabled;
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.border.focus"));
        Require(ReadTextBoxInPopup(editor, "PaletteHexInput").Text == "#5875A7",
            "打开令牌引用时显示解析后的实际颜色");
        Click(FindButtonInPopup(editor, "PaletteConfirmButton"));
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.border.focus"]) == rawReference &&
                Find<Button>(editor, "UndoButton").IsEnabled == undoWasEnabled &&
                Find<Button>(editor, "RedoButton").IsEnabled == redoWasEnabled,
            "不变更确认保留令牌引用且不新增历史");

        SelectScope(editor, "light");
        await DispatchLayoutAsync(window, editor);
        var inheritedAccent = FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent");
        OpenPicker(editor, inheritedAccent);
        Require(ReadTextBoxInPopup(editor, "PaletteHexInput").Text == "#5875A7",
            "浅色范围继承时仍显示基础范围实际颜色");
        SelectColor(editor, new CssRgba(40, 60, 80, 255));
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));
        await DispatchLayoutAsync(window, editor);
        Require(editor.Draft.Manifest["variants"]?["light"]?["tokens"]?["color.accent"] is null &&
                ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == "#5875a7",
            "取消浅色继承字段不写入覆盖且不影响基础值");

        SelectScope(editor, "base");
        await DispatchLayoutAsync(window, editor);
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        SelectColor(editor, new CssRgba(24, 48, 72, 255));
        SelectScope(editor, "dark");
        await DispatchLayoutAsync(window, editor);
        Require(!FindPopup(editor).IsOpen &&
                ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == "#5875a7" &&
                ReadString(editor.Draft.Manifest["variants"]?["dark"]?["tokens"]?["color.accent"]) == "#a0b7df",
            "切换写入范围会取消草稿并保护新范围字段");

        SelectScope(editor, "home");
        await DispatchLayoutAsync(window, editor);
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));
        SelectScope(editor, "conversation");
        await DispatchLayoutAsync(window, editor);
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));
        Require(editor.Draft.Manifest["scenes"] is null,
            "首页和对话范围打开、取消继承色时不误写场景路径");
        SelectScope(editor, "base");
    }

    private static async Task VerifyOneUndoForWholeSessionAsync(Window window, SkinEditorView editor)
    {
        var original = ReadString(editor.Draft.Manifest["tokens"]?["color.accent"])
            ?? throw new InvalidDataException("基础强调色缺失。");
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        SelectColor(editor, new CssRgba(17, 34, 51, 255));
        SelectColor(editor, new CssRgba(34, 68, 102, 255));
        SelectColor(editor, new CssRgba(52, 104, 160, 255));
        Click(FindButtonInPopup(editor, "PaletteConfirmButton"));
        await DispatchLayoutAsync(window, editor);
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == "#3468A0" &&
                Find<Button>(editor, "UndoButton").IsEnabled && !Find<Button>(editor, "RedoButton").IsEnabled,
            "同一色盘的多次更新确认后只有一条 undo 且清除 redo");
        Click(Find<Button>(editor, "UndoButton"));
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == original,
            "一次 undo 撤销整个色盘会话");
        Click(Find<Button>(editor, "RedoButton"));
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == "#3468A0",
            "一次 redo 重做整个色盘会话");
    }

    private static async Task VerifyReturnToOriginalIsNoOpAsync(Window window, SkinEditorView editor)
    {
        var before = ReadString(editor.Draft.Manifest["tokens"]?["color.accent"])
            ?? throw new InvalidDataException("强调色缺失。");
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        SelectColor(editor, new CssRgba(255, 0, 0, 255));
        Require(CssColorParser.TryParse(before, out var original), "原始强调色可解析");
        SelectColor(editor, original);
        Click(FindButtonInPopup(editor, "PaletteConfirmButton"));
        await DispatchLayoutAsync(window, editor);
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == before,
            "拖离后回到原色会还原原始值");
        Click(Find<Button>(editor, "UndoButton"));
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == "#5875a7",
            "回到原色确认不额外制造撤销步骤");
        Click(Find<Button>(editor, "RedoButton"));
        await DispatchLayoutAsync(window, editor);
    }

    private static async Task VerifyTextInputAsync(Window window, SkinEditorView editor)
    {
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        var hex = ReadTextBoxInPopup(editor, "PaletteHexInput");
        hex.Text = string.Empty;
        var expectedHex = string.Empty;
        foreach (var character in "#FF8000")
        {
            expectedHex += character;
            hex.Text += character;
            Require(hex.Text == expectedHex, "HEX 逐字符输入过程中保留当前文本和光标位置");
        }

        Require(hex.Text == "#FF8000" &&
                FindColorPicker(editor).SelectedColor == new CssRgba(255, 128, 0, 255) &&
                ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == "#FF8000",
            "逐字符输入完整 HEX 后精确联动 RGB 与草稿");

        var red = ReadTextBoxInPopup(editor, "PaletteRedInput");
        red.Text = string.Empty;
        var expectedRed = string.Empty;
        foreach (var character in "128")
        {
            expectedRed += character;
            red.Text += character;
            Require(red.Text == expectedRed, "RGB 通道逐字符输入时保留当前文本和光标位置");
        }

        Require(red.Text == "128" && FindColorPicker(editor).SelectedColor == new CssRgba(128, 128, 0, 255),
            "RGB 红色通道逐字符编辑时不跳光标且联动 HEX");
        Click(FindButtonInPopup(editor, "PaletteConfirmButton"));
        await DispatchLayoutAsync(window, editor);
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == "#808000",
            "RGB 输入在确认后保存为对应精确颜色");

        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        var current = ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]);
        var invalidHex = ReadTextBoxInPopup(editor, "PaletteHexInput");
        invalidHex.Text = "#GGGGGG";
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == current,
            "非法 HEX 输入不写入草稿");
        Click(FindButtonInPopup(editor, "PaletteConfirmButton"));
        Require(FindPopup(editor).IsOpen,
            "非法输入时阻止色盘确认并保留弹层");
        ReadTextBoxInPopup(editor, "PaletteRedInput").Text = "999";
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == current,
            "超出 0 到 255 的 RGB 输入不写入草稿");
        Click(FindButtonInPopup(editor, "PaletteConfirmButton"));
        Require(FindPopup(editor).IsOpen,
            "非法 RGB 输入同样阻止色盘确认");
        invalidHex.Text = "#102030";
        Click(FindButtonInPopup(editor, "PaletteConfirmButton"));
        await DispatchLayoutAsync(window, editor);
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == "#102030",
            "修正 HEX 后可继续确认颜色");
    }

    private static async Task VerifyColorEntryPointsAsync(Window window, SkinEditorView editor)
    {
        SelectScope(editor, "base");
        var accentButton = FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent");
        OpenPicker(editor, accentButton);
        var originalAccent = ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]);
        var alphaSlider = Find<Slider>(editor, "PaletteAlphaSlider");
        alphaSlider.Value = 128;
        Require(FindColorPicker(editor).SelectedColor.Alpha == 128 &&
                ReadString(editor.Draft.Manifest["tokens"]?["color.accent"])?.EndsWith("80", StringComparison.Ordinal) == true,
            "透明度滑杆即时联动色盘和草稿 alpha");
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));
        Require(ReadString(editor.Draft.Manifest["tokens"]?["color.accent"]) == originalAccent,
            "取消透明度预览恢复原始颜色 alpha");

        var editors = Find<TabControl>(editor, "EditorTabs");
        editors.SelectedIndex = 1;
        await DispatchLayoutAsync(window, editor);
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "BackgroundFields"), "overlayColor"));
        Require(FindPopup(editor).IsOpen && CssColorParser.TryParse(ReadTextBoxInPopup(editor, "PaletteHexInput").Text, out _),
            "背景遮罩颜色入口共用统一色盘");
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));

        editors.SelectedIndex = 2;
        Find<ComboBox>(editor, "ComponentInput").SelectedValue = "composer";
        Find<ComboBox>(editor, "ComponentStateInput").SelectedValue = "default";
        await DispatchLayoutAsync(window, editor);
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "ComponentFields"), "background"));
        Require(ReadTextBoxInPopup(editor, "PaletteHexInput").Text == "#E7EEF8",
            "组件令牌引用颜色在色盘内显示解析后的实际颜色");
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));

        Find<ComboBox>(editor, "ComponentStateInput").SelectedValue = "focused";
        await DispatchLayoutAsync(window, editor);
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "ComponentFields"), "borderColor"));
        Require(ReadTextBoxInPopup(editor, "PaletteHexInput").Text == "#102030",
            $"组件状态色入口可解析令牌引用并共用色盘（actual={ReadTextBoxInPopup(editor, "PaletteHexInput").Text}）");
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));
        Require(ReadString(editor.Draft.Manifest["components"]?["composer"]?["states"]?["focused"]?["borderColor"]) == "{color.border.focus}",
            "取消组件状态色会话保留原引用文字");
        editors.SelectedIndex = 0;
    }

    private static async Task VerifyCreationBackgroundEntryPointAsync(Window window, SkinEditorView editor)
    {
        Find<TabControl>(editor, "EditorModeTabs").SelectedIndex = 0;
        await DispatchLayoutAsync(window, editor);
        Click(Find<Button>(editor, "CreationUseColorButton"));
        var pickerButton = Find<Button>(editor, "CreationBackgroundPickerButton");
        Require(pickerButton.IsEnabled, "切换纯色背景后创作背景色入口可用");
        OpenPicker(editor, pickerButton);
        Require(ReadTextBoxInPopup(editor, "PaletteHexInput").Text == "#F5F7FB",
            "创作背景颜色入口读取当前背景的实际颜色");
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));
    }

    private static async Task CaptureThemeAndMinimumPopupAsync(Window window, SkinEditorView editor, string screenshots)
    {
        var tokenFields = Find<ItemsControl>(editor, "TokenFields");
        OpenPicker(editor, FindColorButton(tokenFields, "color.accent"));
        SelectColor(editor, new CssRgba(195, 62, 133, 144));
        await Task.Delay(300);
        await SavePopupScreenshotAsync(editor, Path.Combine(screenshots, "色盘-夜间.png"));
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));

        var dayPalette = new ResourceDictionary
        {
            Source = new Uri("/SkinManager;component/Themes/Klay.Day.xaml", UriKind.Relative)
        };
        window.Resources.MergedDictionaries.Add(dayPalette);
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        SelectColor(editor, new CssRgba(195, 62, 133, 144));
        await Task.Delay(300);
        await SavePopupScreenshotAsync(editor, Path.Combine(screenshots, "色盘-日间.png"));
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));
        window.Resources.MergedDictionaries.Remove(dayPalette);

        var workArea = SystemParameters.WorkArea;
        window.Width = 390;
        window.Height = Math.Min(470, workArea.Height - 24);
        window.Left = Math.Max(workArea.Left + 8, workArea.Right - window.Width - 8);
        window.Top = Math.Max(workArea.Top + 8, workArea.Bottom - window.Height - 4);
        await DispatchLayoutAsync(window, editor);
        OpenPicker(editor, FindColorButton(Find<ItemsControl>(editor, "TokenFields"), "color.accent"));
        SelectColor(editor, new CssRgba(195, 62, 133, 144));
        await Task.Delay(300);
        var confirmButton = FindButtonInPopup(editor, "PaletteConfirmButton");
        Require(confirmButton.IsVisible && confirmButton.ActualHeight > 0,
            "靠近屏幕底部的小窗口中确定按钮仍可见");
        await SavePopupScreenshotAsync(editor, Path.Combine(screenshots, "色盘-靠底小窗口.png"));
        Click(FindButtonInPopup(editor, "PaletteCancelButton"));
    }

    private static void OpenPicker(SkinEditorView editor, Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(FindPopup(editor).IsOpen, "颜色入口打开调色弹层");
    }

    private static void SelectColor(SkinEditorView editor, CssRgba color) =>
        FindColorPicker(editor).SetColor(color, notify: true);

    private static void SelectScope(SkinEditorView editor, string scope)
    {
        var combo = Find<ComboBox>(editor, "ScopeInput");
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>()
            .Single(item => string.Equals(item.Tag?.ToString(), scope, StringComparison.Ordinal));
    }

    private static void RaiseEscapeInPopup(SkinEditorView editor)
    {
        var scrollViewer = FindVisualChildren<ScrollViewer>((DependencyObject)FindPopup(editor).Child!)
            .FirstOrDefault() ?? throw new InvalidOperationException("找不到色盘弹层滚动容器。");
        var source = PresentationSource.FromVisual(scrollViewer)
            ?? throw new InvalidOperationException("色盘弹层尚未连接到窗口。");
        scrollViewer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static async Task DispatchLayoutAsync(Window window, SkinEditorView editor)
    {
        await window.Dispatcher.InvokeAsync(() =>
        {
            window.UpdateLayout();
            editor.UpdateLayout();
        });
    }

    private static async Task SavePopupScreenshotAsync(SkinEditorView editor, string path)
    {
        var surface = Find<Border>(editor, "ColorPickerPopupSurface");
        await editor.Dispatcher.InvokeAsync(() =>
        {
            surface.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(surface);
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path);
            encoder.Save(output);
        });

        Require(File.Exists(path) && new FileInfo(path).Length > 10_000,
            $"生成并检查色盘截图 {Path.GetFileName(path)}");
    }

    private static Button FindColorButton(ItemsControl items, string property)
    {
        items.ApplyTemplate();
        items.UpdateLayout();
        foreach (var field in items.Items.OfType<SkinEditorView.SkinEditorField>()
                     .Where(field => string.Equals(field.Path[^1], property, StringComparison.Ordinal)))
        {
            if (items.ItemContainerGenerator.ContainerFromItem(field) is ContentPresenter presenter &&
                presenter.ContentTemplate?.FindName("ColorPickerFieldButton", presenter) is Button templateButton)
            {
                return templateButton;
            }
        }

        var match = FindVisualChildren<Button>(items)
            .FirstOrDefault(button => button.Tag is SkinEditorView.SkinEditorField field &&
                                      string.Equals(field.Path[^1], property, StringComparison.Ordinal));
        return match ?? throw new InvalidOperationException($"找不到颜色字段 {property} 的调色按钮。");
    }

    private static Popup FindPopup(SkinEditorView editor) => Find<Popup>(editor, "ColorPalettePopup");

    private static ColorPickerControl FindColorPicker(SkinEditorView editor) =>
        Find<ColorPickerControl>(editor, "ColorPicker");

    private static Button FindButtonInPopup(SkinEditorView editor, string name) =>
        Find<Button>(editor, name);

    private static TextBox ReadTextBoxInPopup(SkinEditorView editor, string name) =>
        Find<TextBox>(editor, name);

    private static T Find<T>(DependencyObject root, string name) where T : class
    {
        if (root is FrameworkElement owner && owner.FindName(name) is T named)
        {
            return named;
        }

        return FindVisualChildren<FrameworkElement>(root)
            .FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal)) as T
            ?? throw new InvalidOperationException($"找不到控件 {name}。");
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
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

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static byte[] ReadEmbeddedPackage(string name)
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream($"SkinManager.Preview.Tests.Resources.{name}.zip")
            ?? throw new FileNotFoundException($"测试皮肤资源 {name} 不存在。");
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException($"色盘检查失败：{message}。");
        }

        Console.WriteLine($"通过：{message}");
    }
}

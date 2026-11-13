using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkinManager.Core;
using SkinManager.Desktop;
using SkinManager.Runtime;

namespace SkinManager.Preview.Tests;

internal static class CompatibilityWindowTests
{
    public static async Task RunAsync()
    {
        var previousLanguagePreference = AppLocalization.Preference;
        var settingsPath = Path.Combine(Program.FindTestOutputDirectory(), "settings-language", "language.json");
        try
        {
            AppLocalization.Initialize(settingsPath);
            AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);

            await VerifiesTabsAndImmediateLanguageSwitchAsync(settingsPath);
            await VerifiesMagpieActionsAreIndependentAsync();
            await VerifiesMinimumEnglishLayoutAsync();
            await VerifiesCloseAndEscapeAsync();
        }
        finally
        {
            AppLocalization.SetLanguage(previousLanguagePreference);
        }
    }

    private static async Task VerifiesTabsAndImmediateLanguageSwitchAsync(string settingsPath)
    {
        var configInspection = new CountingCodexConfigReadabilityService();
        var magpieInspections = 0;
        var window = CreateWindow(
            inspectMagpie: _ =>
            {
                magpieInspections++;
                return MakeMagpieResult();
            },
            codexConfigReadabilityService: configInspection);
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded, "设置窗口加载");
            await WaitUntilAsync(() => window.Opacity >= 0.999, "设置窗口淡入完成");
            var tabs = Find<TabControl>(window, "SettingsTabs");
            var general = Find<TabItem>(window, "GeneralTab");
            var appearance = Find<TabItem>(window, "AppearanceTab");
            var compatibility = Find<TabItem>(window, "CompatibilityTab");
            Require(general.Header?.ToString() == "常规" && appearance.Header?.ToString() == "外观" &&
                    compatibility.Header?.ToString() == "兼容性" &&
                    configInspection.InspectCalls == 1 && magpieInspections == 0,
                "设置按常规、外观、兼容性横向分类，初始化不展开检测 Magpie");
            Require(window.FindName("ForceSimplifiedChineseCheckBox") is null &&
                    window.FindName("LanguageSavedStatusText") is null &&
                    window.FindName("LanguageRuntimeStatusText") is null &&
                    window.FindName("LaunchDebugCodexButton") is null,
                "旧固定中文开关、运行状态与调试版设置入口已移除");

            var language = Find<ComboBox>(window, "LanguageBox");
            SelectLanguage(language, AppLanguage.English);
            await WaitUntilAsync(() => general.Header?.ToString() == "General", "标签即时翻译");
            Require(appearance.Header?.ToString() == "Appearance" && compatibility.Header?.ToString() == "Compatibility" &&
                    window.Title == "Settings" && AppLocalization.Preference == AppLanguage.English,
                "语言切换即时更新标题和全部标签，并写入当前应用偏好");

            var preference = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(settingsPath));
            Require(preference is not null && preference.GetValueOrDefault("Language") == "English",
                "设置语言写入隔离的测试偏好文件");

            tabs.SelectedItem = compatibility;
            await WaitUntilAsync(() => magpieInspections == 1, "首次打开兼容性页后检测 Magpie");
            var configStatus = Find<TextBlock>(window, "CodexConfigStatusText").Text;
            var magpieTitle = Find<TextBlock>(window, "SupportTitle").Text;
            Require(configStatus == "config.toml is readable" &&
                    magpieTitle == "Verified: startup does not include debug arguments",
                "兼容页中的 config.toml 与 Magpie 状态按当前语言呈现");
            var compatibilityBorder = compatibility.Template.FindName("TabBorder", compatibility) as Border;
            var compatibilityHeader = compatibility.Template.FindName("HeaderPresenter", compatibility) as ContentPresenter;
            var compatibilityBorderBounds = compatibilityBorder is null
                ? Rect.Empty
                : GetBounds(compatibility, compatibilityBorder);
            Require(compatibilityBorder is not null && compatibilityBorder.CornerRadius.TopRight >= 10 &&
                    compatibilityBorder.BorderThickness.Right > 0 &&
                    compatibilityBorderBounds.Right <= compatibility.ActualWidth + 1 &&
                    compatibilityHeader is not null &&
                    TextElement.GetFontWeight(compatibilityHeader) == FontWeights.SemiBold &&
                    Find<TextBlock>(window, "SupportSubtitle").FontWeight == FontWeights.Normal,
                "选中标签保留完整圆角描边与强调字重，兼容性页副文案保持常规字重");
            await SaveScreenshotAsync(window, Path.Combine(Program.FindTestOutputDirectory(), "settings-english-tabs.png"));
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task VerifiesMagpieActionsAreIndependentAsync()
    {
        var inspectCalls = 0;
        var launchCalls = 0;
        var configService = new CountingCodexConfigReadabilityService();
        var window = CreateWindow(
            inspectMagpie: _ =>
            {
                inspectCalls++;
                return MakeMagpieResult();
            },
            launchMagpie: (_, _) =>
            {
                launchCalls++;
                return Task.CompletedTask;
            },
            codexConfigReadabilityService: configService);
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded, "兼容性页加载");
            Find<TabControl>(window, "SettingsTabs").SelectedItem = Find<TabItem>(window, "CompatibilityTab");
            await WaitUntilAsync(() => inspectCalls == 1, "兼容页首次打开检测 Magpie");
            Find<Button>(window, "InspectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => inspectCalls == 2 && configService.InspectCalls == 3, "手动重检刷新配置与 Magpie");
            Find<Button>(window, "LaunchMagpieButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => launchCalls == 1 && inspectCalls == 3 && configService.InspectCalls == 4,
                "启动 Magpie 后重新读取兼容信息");
            var actionStatus = Find<TextBlock>(window, "MagpieActionStatusText").Text;
            var configStatus = Find<TextBlock>(window, "CodexConfigStatusText").Text;
            Require(actionStatus == "Magpie started. Its configuration was not changed." &&
                    configStatus == "config.toml is readable",
                "Magpie 启动反馈保持只读配置边界，config.toml 状态正常刷新");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task VerifiesMinimumEnglishLayoutAsync()
    {
        AppLocalization.SetLanguage(AppLanguage.English);
        var evidence = string.Join(" ", Enumerable.Repeat(
            "Compatibility details wrap and remain available through the page scroll area.", 24));
        var executablePath = @"C:\Example\" + string.Join(@"\", Enumerable.Repeat("MagpieFolder_" + new string('x', 30), 8)) + @"\Magpie.exe";
        var configService = new FixtureCodexConfigReadabilityService();
        var window = CreateWindow(
            inspectMagpie: _ => new MagpieCompatibilityResult(
                true, false, executablePath, "0.1.3237", MagpieCompatibilitySupport.Unsupported,
                true, evidence, "Magpie launches Codex without debugging parameters."),
            codexConfigReadabilityService: configService);
        window.Show();
        try
        {
            await WaitUntilAsync(() => window.IsLoaded, "英文最小尺寸设置窗口加载");
            window.Width = 620;
            window.Height = 440;
            window.UpdateLayout();
            var startManager = Find<CheckBox>(window, "StartManagerAtLoginCheckBox");
            var startSkin = Find<CheckBox>(window, "ApplyLastSelectedSkinCheckBox");
            var startSkinText = startSkin.Content as TextBlock ??
                throw new InvalidOperationException("启动皮肤复选框应使用可换行 TextBlock 内容。");
            var tabs = Find<TabControl>(window, "SettingsTabs");
            var general = Find<TabItem>(window, "GeneralTab");
            var generalBorder = general.Template.FindName("TabBorder", general) as Border;
            var generalHeader = general.Template.FindName("HeaderPresenter", general) as ContentPresenter;
            var generalBorderBounds = generalBorder is null ? Rect.Empty : GetBounds(general, generalBorder);
            Console.WriteLine(
                $"Settings long-checkbox layout: window={window.ActualWidth:0.##}x{window.ActualHeight:0.##}, " +
                $"tab={tabs.ActualWidth:0.##}, " +
                $"checkbox={startSkin.ActualWidth:0.##}x{startSkin.ActualHeight:0.##}, " +
                $"text={startSkinText.ActualWidth:0.##}x{startSkinText.ActualHeight:0.##}, " +
                $"maxWidth={startSkinText.MaxWidth:0.##}, wrapping={startSkinText.TextWrapping}");
            var startupTextBounds = GetBounds(window, startSkinText);
            Require(window.Width == 620 && window.Height == 440 &&
                    startManager.IsVisible && startSkin.IsVisible && startSkinText.TextWrapping == TextWrapping.Wrap &&
                    startSkinText.Text == AppLocalization.Get("开机时启动并应用上次选择的皮肤（调试版）") &&
                    startSkinText.ActualHeight > startSkinText.FontSize * 1.4 &&
                    startSkin.FontWeight == FontWeights.Normal && startSkinText.FontWeight == FontWeights.Normal &&
                    GetBounds(window, startSkin).Left > GetBounds(window, startManager).Left &&
                    IsWithinBounds(startupTextBounds, new Rect(0, 0, window.ActualWidth, window.ActualHeight)) &&
                    IsWithinBounds(GetBounds(window, startSkin), new Rect(0, 0, window.ActualWidth, window.ActualHeight)) &&
                    generalBorder is not null && generalBorder.CornerRadius.TopRight >= 10 && generalBorder.BorderThickness.Right > 0 &&
                    generalBorderBounds.Right <= general.ActualWidth + 1 &&
                    generalHeader is not null && TextElement.GetFontWeight(generalHeader) == FontWeights.SemiBold,
                "620×440英文常规页长开机选项实际换行且保持缩进，内容字重正常，标签页圆角描边完整");
            await SaveScreenshotAsync(window, Path.Combine(Program.FindTestOutputDirectory(), "settings-english-minimum-620x440.png"));

            Find<TabControl>(window, "SettingsTabs").SelectedItem = Find<TabItem>(window, "CompatibilityTab");
            await WaitUntilAsync(() => Find<TextBlock>(window, "SupportTitle").Text == "Verified: startup does not include debug arguments",
                "英文兼容页渲染");
            window.UpdateLayout();
            var close = Find<Button>(window, "CloseButton");
            var scroll = Find<ScrollViewer>(window, "SettingsScrollViewer");
            Require(close.IsVisible && IsWithinBounds(GetBounds(window, close), new Rect(0, 0, window.ActualWidth, window.ActualHeight)) &&
                    scroll.ScrollableHeight > 0 && Find<TextBlock>(window, "EvidenceText").Text == evidence,
                "长英文兼容信息可滚动查看，底部关闭按钮固定在可视范围");
            await SaveScreenshotAsync(window, Path.Combine(Program.FindTestOutputDirectory(), "settings-english-compatibility-620x440.png"));
        }
        finally
        {
            window.Close();
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
            await WaitUntilAsync(() => window.IsLoaded, "设置页响应 Escape");
            var source = PresentationSource.FromVisual(window) ??
                throw new InvalidOperationException("设置页尚未连接到 WPF 窗口消息源。");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            await WaitUntilAsync(() => !window.IsVisible, "按 Escape 关闭设置页");
        }
        finally
        {
            if (window.IsVisible)
                window.Close();
        }
    }

    private static SettingsWindow CreateWindow(
        Func<string?, MagpieCompatibilityResult>? inspectMagpie = null,
        Func<string?, CancellationToken, Task>? launchMagpie = null,
        ICodexConfigReadabilityService? codexConfigReadabilityService = null) =>
        new(
            inspectMagpie ?? (_ => MakeMagpieResult()),
            launchMagpie ?? ((_, _) => Task.CompletedTask),
            codexConfigReadabilityService ?? new FixtureCodexConfigReadabilityService())
        {
            Width = 760,
            Height = 570,
            Left = -2_000,
            Top = -2_000,
            ShowActivated = false,
            ShowInTaskbar = false
        };

    private static MagpieCompatibilityResult MakeMagpieResult() => new(
        true,
        false,
        @"C:\Example\Magpie.exe",
        "0.1.3237",
        MagpieCompatibilitySupport.Unsupported,
        true,
        "已核对官方 Magpie v0.1.3237 源码：Windows 重开 Codex 时通过应用标识启动，未传递远程调试参数。",
        "网关模型配置独立于皮肤启动参数。");

    private static void SelectLanguage(ComboBox comboBox, AppLanguage language)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag?.ToString(), language.ToString(), StringComparison.Ordinal));
    }

    private static T Find<T>(Window window, string name) where T : FrameworkElement =>
        window.FindName(name) is T element
            ? element
            : throw new InvalidOperationException($"设置页缺少控件 {name}。");

    private sealed class FixtureCodexConfigReadabilityService : ICodexConfigReadabilityService
    {
        public CodexConfigReadabilityResult Inspect() => new(
            Path.Combine(Path.GetTempPath(), "SkinManager-fixture", "codex", "config.toml"),
            CodexConfigReadabilityState.Readable);
    }

    private sealed class CountingCodexConfigReadabilityService : ICodexConfigReadabilityService
    {
        public int InspectCalls { get; private set; }

        public CodexConfigReadabilityResult Inspect()
        {
            InspectCalls++;
            return new CodexConfigReadabilityResult(
                Path.Combine(Path.GetTempPath(), "SkinManager-fixture", "codex", "config.toml"),
                CodexConfigReadabilityState.Readable);
        }
    }

    private static Rect GetBounds(FrameworkElement ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

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
                throw new InvalidOperationException($"设置页回归失败：{label}超时。");
            await Task.Delay(25);
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"设置页回归失败：{label}");
        Console.WriteLine($"通过：{label}");
    }
}

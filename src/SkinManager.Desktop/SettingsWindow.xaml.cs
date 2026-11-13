using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using SkinManager.Core;
using SkinManager.Runtime;

namespace SkinManager.Desktop;

public partial class SettingsWindow : Window
{
    private readonly Func<string?, MagpieCompatibilityResult> _inspectMagpie;
    private readonly Func<string?, CancellationToken, Task> _launchMagpie;
    private readonly Func<CodexConfigReadabilityResult> _inspectCodexConfig;
    private readonly IStartupPreferencesService? _startupPreferencesService;
    private StartupPreferencesState? _startupPreferences;
    private CodexConfigReadabilityResult? _codexConfigResult;
    private MagpieCompatibilityResult? _currentMagpieResult;
    private bool _hasInspectedMagpie;
    private bool _themeModeReady;
    private bool _updatingThemeMode;
    private bool _languageModeReady;
    private bool _updatingLanguageMode;
    private bool _updatingStartupControls;
    private bool _startupPreferencesLoaded;
    private bool _settingsReady;
    private bool _isBusy;

    public SettingsWindow(
        MagpieCompatibilityService? magpieService = null,
        ICodexConfigReadabilityService? codexConfigReadabilityService = null,
        IStartupPreferencesService? startupPreferencesService = null)
        : this(
            CreateMagpieActions(magpieService),
            codexConfigReadabilityService,
            startupPreferencesService)
    {
    }

    internal SettingsWindow(
        Func<string?, MagpieCompatibilityResult> inspectMagpie,
        Func<string?, CancellationToken, Task> launchMagpie,
        ICodexConfigReadabilityService? codexConfigReadabilityService = null,
        IStartupPreferencesService? startupPreferencesService = null)
        : this(
            (inspectMagpie, launchMagpie),
            codexConfigReadabilityService,
            startupPreferencesService)
    {
    }

    private SettingsWindow(
        (Func<string?, MagpieCompatibilityResult> Inspect,
         Func<string?, CancellationToken, Task> Launch) magpieActions,
        ICodexConfigReadabilityService? codexConfigReadabilityService,
        IStartupPreferencesService? startupPreferencesService)
    {
        _inspectMagpie = magpieActions.Inspect;
        _launchMagpie = magpieActions.Launch;
        _inspectCodexConfig = (codexConfigReadabilityService ?? new CodexConfigReadabilityService()).Inspect;
        _startupPreferencesService = startupPreferencesService;

        InitializeComponent();
        SetLanguageSelection(AppLocalization.Preference);
        _languageModeReady = true;
        _themeModeReady = true;
        SetAppearanceSelection(AppThemeService.Mode);
        AppThemeService.ThemeChanged += AppThemeService_ThemeChanged;
        AppLocalization.LanguageChanged += AppLocalization_LanguageChanged;
        Closed += SettingsWindow_Closed;
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: true, animateClose: true, animateButtonPress: true);

        RenderCodexConfigStatus(InspectCodexConfig());
        RefreshStartupPreferences();
        _settingsReady = true;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageModeReady || _updatingLanguageMode || LanguageBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string languageText || !Enum.TryParse<AppLanguage>(languageText, out var language))
        {
            return;
        }

        try
        {
            AppLocalization.SetLanguage(language);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetLanguageSelection(AppLocalization.Preference);
            WindowStatusText.Text = AppLocalization.Format("无法保存界面语言：{0}", exception.Message);
        }
    }

    private void SetLanguageSelection(AppLanguage language)
    {
        _updatingLanguageMode = true;
        LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), language.ToString(), StringComparison.Ordinal));
        _updatingLanguageMode = false;
    }

    private void AppearanceModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeModeReady || _updatingThemeMode || AppearanceModeBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string modeText || !Enum.TryParse<AppThemeMode>(modeText, out var mode))
        {
            return;
        }

        try
        {
            AppThemeService.SetMode(mode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetAppearanceSelection(AppThemeService.Mode);
            WindowStatusText.Text = AppLocalization.Format("无法保存界面外观：{0}", exception.Message);
        }
    }

    private void AppThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => AppThemeService_ThemeChanged(sender, e)));
            return;
        }

        SetAppearanceSelection(AppThemeService.Mode);
    }

    private void SetAppearanceSelection(AppThemeMode mode)
    {
        _updatingThemeMode = true;
        AppearanceModeBox.SelectedIndex = mode == AppThemeMode.Day ? 1 : 0;
        _updatingThemeMode = false;
    }

    private void AppLocalization_LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => AppLocalization_LanguageChanged(sender, e)));
            return;
        }

        SetLanguageSelection(AppLocalization.Preference);
        if (_startupPreferencesLoaded)
            RenderStartupPreferences();
        if (_codexConfigResult is not null)
            RenderCodexConfigStatus(_codexConfigResult);
        if (_currentMagpieResult is not null)
            RenderMagpieResult(_currentMagpieResult);
        UpdateMagpieControls(_currentMagpieResult);
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        AppThemeService.ThemeChanged -= AppThemeService_ThemeChanged;
        AppLocalization.LanguageChanged -= AppLocalization_LanguageChanged;
        Closed -= SettingsWindow_Closed;
    }

    private CodexConfigReadabilityResult InspectCodexConfig()
    {
        try
        {
            return _inspectCodexConfig();
        }
        catch (Exception)
        {
            return new CodexConfigReadabilityResult(string.Empty, CodexConfigReadabilityState.InvalidPath);
        }
    }

    private void RenderCodexConfigStatus(CodexConfigReadabilityResult result)
    {
        _codexConfigResult = result;
        var (label, brushKey) = result.State switch
        {
            CodexConfigReadabilityState.Readable => ("config.toml 可读取", "SuccessText"),
            CodexConfigReadabilityState.Missing => ("未找到 config.toml", "WarningText"),
            CodexConfigReadabilityState.Unreadable => ("config.toml 无法读取", "ErrorText"),
            _ => ("Codex 配置路径无效", "ErrorText")
        };
        CodexConfigStatusText.Text = AppLocalization.Get(label);
        CodexConfigStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        CodexConfigPathText.Text = string.IsNullOrWhiteSpace(result.Path)
            ? AppLocalization.Get("配置文件路径无法解析。")
            : result.Path;
    }

    private void RefreshStartupPreferences()
    {
        if (_startupPreferencesService is null)
        {
            _startupPreferences = null;
            _startupPreferencesLoaded = false;
            RenderStartupPreferences("此窗口未连接开机启动设置服务。");
            return;
        }

        try
        {
            _startupPreferences = _startupPreferencesService.Read();
            _startupPreferencesLoaded = true;
            RenderStartupPreferences();
        }
        catch (Exception exception)
        {
            _startupPreferences = null;
            _startupPreferencesLoaded = false;
            RenderStartupPreferences(AppLocalization.Format("无法读取开机启动设置：{0}", exception.Message));
        }
    }

    private void RenderStartupPreferences(string? error = null)
    {
        _updatingStartupControls = true;
        var state = _startupPreferences;
        StartManagerAtLoginCheckBox.IsChecked = state?.ManagerStartsAtWindowsSignIn == true;
        ApplyLastSelectedSkinCheckBox.IsChecked = state?.ApplyLastSelectedSkin == true;
        StartManagerAtLoginCheckBox.IsEnabled = _startupPreferencesLoaded && !_isBusy;
        ApplyLastSelectedSkinCheckBox.IsEnabled = _startupPreferencesLoaded &&
            state?.ManagerStartsAtWindowsSignIn == true && !_isBusy;
        _updatingStartupControls = false;

        if (!string.IsNullOrWhiteSpace(error))
        {
            StartupPreferencesStatusText.Text = error;
            StartupPreferencesStatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorText");
            return;
        }

        if (state is null)
        {
            StartupPreferencesStatusText.Text = AppLocalization.Get("开机启动状态未知。");
            StartupPreferencesStatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorText");
            return;
        }

        var lastSkin = string.IsNullOrWhiteSpace(state.LastSelectedSkinId)
            ? AppLocalization.Get("尚无已记录的上次皮肤。")
            : AppLocalization.Format("上次选择的皮肤 ID：{0}。", state.LastSelectedSkinId);
        var paused = state.ApplyLastSelectedSkin && !state.ManagerStartsAtWindowsSignIn
            ? AppLocalization.Get("第二项已保留，开启管理器自启后才会生效。")
            : null;
        StartupPreferencesStatusText.Text = string.Join(" ", new[] { lastSkin, paused }.Where(text => !string.IsNullOrWhiteSpace(text)));
        StartupPreferencesStatusText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
    }

    private void StartupPreferenceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingStartupControls || !_startupPreferencesLoaded || _startupPreferencesService is null || _isBusy)
            return;

        try
        {
            _startupPreferences = ReferenceEquals(sender, StartManagerAtLoginCheckBox)
                ? _startupPreferencesService.SetManagerStartsAtWindowsSignIn(StartManagerAtLoginCheckBox.IsChecked == true)
                : _startupPreferencesService.SetApplyLastSelectedSkin(ApplyLastSelectedSkinCheckBox.IsChecked == true);
            _startupPreferencesLoaded = true;
            RenderStartupPreferences();
            WindowStatusText.Text = AppLocalization.Get("开机启动设置已保存。");
        }
        catch (Exception exception)
        {
            RenderStartupPreferences(AppLocalization.Format("开机启动设置未能保存：{0}", exception.Message));
            WindowStatusText.Text = AppLocalization.Get("开机启动设置未保存；原值仍保留。");
        }
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || !ReferenceEquals(e.OriginalSource, SettingsTabs) ||
            !ReferenceEquals(SettingsTabs.SelectedItem, CompatibilityTab) || _hasInspectedMagpie)
        {
            return;
        }

        RefreshCompatibilityStatuses();
    }

    private void InspectButton_Click(object sender, RoutedEventArgs e) => RefreshCompatibilityStatuses();

    private void RefreshCompatibilityStatuses()
    {
        RenderCodexConfigStatus(InspectCodexConfig());
        InspectMagpieCompatibility();
    }

    private void InspectMagpieCompatibility()
    {
        try
        {
            var selectedPath = string.IsNullOrWhiteSpace(ExecutablePathInput.Text)
                ? null
                : ExecutablePathInput.Text.Trim();
            var result = _inspectMagpie(selectedPath);
            _hasInspectedMagpie = true;
            if (string.IsNullOrWhiteSpace(selectedPath) && !string.IsNullOrWhiteSpace(result.ExecutablePath))
                ExecutablePathInput.Text = result.ExecutablePath;
            RenderMagpieResult(result);
        }
        catch (Exception exception)
        {
            _hasInspectedMagpie = true;
            _currentMagpieResult = null;
            SupportIndicator.SetResourceReference(Ellipse.FillProperty, "ErrorText");
            SupportTitle.Text = AppLocalization.Get("无法完成检测");
            SupportSubtitle.Text = AppLocalization.Get("检测未能读取 Magpie 状态。");
            MagpieRunningText.Text = AppLocalization.Get("运行状态：未知");
            CompatibilityMessageText.Text = exception.Message;
            VersionText.Text = AppLocalization.Get("版本：未检测");
            ExecutablePathText.Text = AppLocalization.Get("路径：未检测");
            EvidenceText.Text = string.Empty;
            LaunchMagpieButton.IsEnabled = false;
            UpdateMagpieControls();
        }
    }

    private void RenderMagpieResult(MagpieCompatibilityResult result)
    {
        _currentMagpieResult = result;
        var (title, brushKey) = result.Support switch
        {
            MagpieCompatibilitySupport.NotInstalled => ("未检测到 Magpie", "SecondaryText"),
            MagpieCompatibilitySupport.Unsupported => ("已核实：启动不附带调试参数", "WarningText"),
            MagpieCompatibilitySupport.Unverified => ("此版本的启动参数尚未核实", "WarningText"),
            MagpieCompatibilitySupport.InvalidExecutable => ("所选程序不是有效的 Magpie", "ErrorText"),
            MagpieCompatibilitySupport.UnsupportedPlatform => ("当前平台无法核验启动方式", "ErrorText"),
            _ => ("启动参数状态未知", "SecondaryText")
        };

        SupportIndicator.SetResourceReference(Ellipse.FillProperty, brushKey);
        SupportTitle.Text = AppLocalization.Get(title);
        SupportSubtitle.Text = result.IsInstalled
            ? result.ProductVersion is { Length: > 0 } version
                ? AppLocalization.Format("检测到 Magpie · {0}", version)
                : AppLocalization.Get("检测到 Magpie")
            : AppLocalization.Get("未检测到可识别的 Magpie，可选择便携版或指定路径。");
        MagpieRunningText.Text = AppLocalization.Get(result.IsRunning ? "运行状态：正在运行" : "运行状态：未运行");
        CompatibilityMessageText.Text = result.Support switch
        {
            MagpieCompatibilitySupport.Unsupported => AppLocalization.Get("Magpie 自身启动 Codex 不附带调试参数。需要换肤时，从 SkinManager 启动调试版并应用皮肤；网关模型配置仍可独立使用。"),
            MagpieCompatibilitySupport.Unverified => AppLocalization.Get("此版本的 Magpie 启动参数尚未核实。网关模型配置与皮肤启动参数相互独立。"),
            _ => AppLocalization.Get(result.Message)
        };
        VersionText.Text = result.ProductVersion is { Length: > 0 } productVersion
            ? AppLocalization.Format("版本：{0}", productVersion)
            : AppLocalization.Get("版本：未检测");
        ExecutablePathText.Text = result.ExecutablePath is { Length: > 0 } executablePath
            ? AppLocalization.Format("路径：{0}", executablePath)
            : AppLocalization.Get("路径：未检测");
        EvidenceText.Text = AppLocalization.Get(result.Evidence);
        UpdateMagpieControls(result);
    }

    private void ChooseExecutableButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = AppLocalization.Get("选择 Magpie 程序"),
            Filter = AppLocalization.Get("应用程序|*.exe|所有文件|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        ExecutablePathInput.Text = dialog.FileName;
        InspectMagpieCompatibility();
    }

    private async void LaunchMagpieButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        var selectedPath = string.IsNullOrWhiteSpace(ExecutablePathInput.Text)
            ? null
            : ExecutablePathInput.Text.Trim();
        SetBusy(true, AppLocalization.Get("正在启动 Magpie…"));
        try
        {
            await _launchMagpie(selectedPath, CancellationToken.None);
            MagpieActionStatusText.Text = AppLocalization.Get("已启动 Magpie；未修改其配置。");
            RefreshCompatibilityStatuses();
        }
        catch (Exception exception)
        {
            MagpieActionStatusText.Text = AppLocalization.Format("启动 Magpie 失败：{0}", exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateMagpieControls(MagpieCompatibilityResult? result = null)
    {
        ChooseExecutableButton.IsEnabled = !_isBusy;
        InspectButton.IsEnabled = !_isBusy;
        LaunchMagpieButton.IsEnabled = !_isBusy && result is { CanLaunch: true, IsRunning: false };
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        StartManagerAtLoginCheckBox.IsEnabled = _startupPreferencesLoaded && !_isBusy;
        ApplyLastSelectedSkinCheckBox.IsEnabled = _startupPreferencesLoaded &&
            _startupPreferences?.ManagerStartsAtWindowsSignIn == true && !_isBusy;
        LanguageBox.IsEnabled = !_isBusy;
        AppearanceModeBox.IsEnabled = !_isBusy;
        if (!string.IsNullOrWhiteSpace(message))
            WindowStatusText.Text = message;
        UpdateMagpieControls(_currentMagpieResult);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
            return;
        e.Handled = true;
        Close();
    }

    private static (Func<string?, MagpieCompatibilityResult> Inspect,
        Func<string?, CancellationToken, Task> Launch) CreateMagpieActions(MagpieCompatibilityService? service)
    {
        var actualService = service ?? new MagpieCompatibilityService();
        return (actualService.Inspect, actualService.LaunchAsync);
    }
}

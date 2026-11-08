using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SkinManager.Runtime;

namespace SkinManager.Desktop;

public partial class CompatibilityWindow : Window
{
    private readonly Func<RuntimeCompatibilityOptions, CancellationToken, Task<RuntimeCompatibilityResult?>> _saveAndConfigure;
    private readonly Func<RuntimeCompatibilityOptions> _reloadSettings;
    private readonly Func<string?, MagpieCompatibilityResult> _inspectMagpie;
    private readonly Func<string?, CancellationToken, Task> _launchMagpie;
    private readonly Func<Task<RuntimeStatus>> _launchDebugCodex;
    private RuntimeCompatibilityOptions _options;
    private RuntimeCompatibilityResult? _lastRuntimeResult;
    private bool _settingsLoaded;
    private bool _updatingSettingsControl;
    private bool _isBusy;
    private bool _hasInspectedMagpie;

    public CompatibilityWindow(
        RuntimeCompatibilityOptions options,
        bool settingsLoaded,
        string? settingsLoadError,
        RuntimeCompatibilityResult? lastRuntimeResult,
        Func<RuntimeCompatibilityOptions, CancellationToken, Task<RuntimeCompatibilityResult?>> saveAndConfigure,
        Func<RuntimeCompatibilityOptions> reloadSettings,
        Func<Task<RuntimeStatus>> launchDebugCodex,
        MagpieCompatibilityService? magpieService = null)
        : this(
            options,
            settingsLoaded,
            settingsLoadError,
            lastRuntimeResult,
            saveAndConfigure,
            reloadSettings,
            launchDebugCodex,
            CreateMagpieActions(magpieService))
    {
    }

    internal CompatibilityWindow(
        RuntimeCompatibilityOptions options,
        bool settingsLoaded,
        string? settingsLoadError,
        RuntimeCompatibilityResult? lastRuntimeResult,
        Func<RuntimeCompatibilityOptions, CancellationToken, Task<RuntimeCompatibilityResult?>> saveAndConfigure,
        Func<RuntimeCompatibilityOptions> reloadSettings,
        Func<Task<RuntimeStatus>> launchDebugCodex,
        Func<string?, MagpieCompatibilityResult> inspectMagpie,
        Func<string?, CancellationToken, Task> launchMagpie)
        : this(
            options,
            settingsLoaded,
            settingsLoadError,
            lastRuntimeResult,
            saveAndConfigure,
            reloadSettings,
            launchDebugCodex,
            (inspectMagpie, launchMagpie))
    {
    }

    private CompatibilityWindow(
        RuntimeCompatibilityOptions options,
        bool settingsLoaded,
        string? settingsLoadError,
        RuntimeCompatibilityResult? lastRuntimeResult,
        Func<RuntimeCompatibilityOptions, CancellationToken, Task<RuntimeCompatibilityResult?>> saveAndConfigure,
        Func<RuntimeCompatibilityOptions> reloadSettings,
        Func<Task<RuntimeStatus>> launchDebugCodex,
        (Func<string?, MagpieCompatibilityResult> Inspect,
         Func<string?, CancellationToken, Task> Launch) magpieActions)
    {
        _options = options;
        _settingsLoaded = settingsLoaded;
        _lastRuntimeResult = lastRuntimeResult;
        _saveAndConfigure = saveAndConfigure;
        _reloadSettings = reloadSettings;
        _launchDebugCodex = launchDebugCodex;
        _inspectMagpie = magpieActions.Inspect;
        _launchMagpie = magpieActions.Launch;
        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: true, animateClose: true, animateButtonPress: true);
        InitializePreferenceView(settingsLoadError);
    }

    private void InitializePreferenceView(string? settingsLoadError)
    {
        _updatingSettingsControl = true;
        ForceSimplifiedChineseCheckBox.IsChecked = _settingsLoaded ? _options.ForceSimplifiedChinese : null;
        ForceSimplifiedChineseCheckBox.IsEnabled = _settingsLoaded;
        RetrySettingsLoadButton.Visibility = _settingsLoaded ? Visibility.Collapsed : Visibility.Visible;
        RetrySettingsLoadButton.IsEnabled = true;
        _updatingSettingsControl = false;
        RenderSavedPreference(settingsLoadError);
        RenderRuntimePreference(_settingsLoaded ? _lastRuntimeResult : null);
    }

    private async void ForceSimplifiedChineseCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSettingsControl || !_settingsLoaded || _isBusy)
        {
            return;
        }

        var requested = new RuntimeCompatibilityOptions(ForceSimplifiedChineseCheckBox.IsChecked == true);
        if (requested == _options)
        {
            return;
        }

        SetBusy(true, "正在保存界面语言设置…");
        try
        {
            var result = await _saveAndConfigure(requested, CancellationToken.None);
            _options = requested;
            _settingsLoaded = true;
            _lastRuntimeResult = result;
            RenderSavedPreference(null);
            RenderRuntimePreference(result);
            WindowStatusText.Text = "设置已保存。";
        }
        catch (Exception exception)
        {
            _updatingSettingsControl = true;
            ForceSimplifiedChineseCheckBox.IsChecked = _settingsLoaded ? _options.ForceSimplifiedChinese : null;
            _updatingSettingsControl = false;
            LanguageSavedStatusText.Text = $"未能保存：{exception.Message}";
            LanguageSavedStatusText.Foreground = (Brush)FindResource("ErrorText");
            WindowStatusText.Text = "设置未保存；原值仍保留。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RetrySettingsLoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _options = _reloadSettings();
            _settingsLoaded = true;
            _updatingSettingsControl = true;
            ForceSimplifiedChineseCheckBox.IsChecked = _options.ForceSimplifiedChinese;
            ForceSimplifiedChineseCheckBox.IsEnabled = true;
            _updatingSettingsControl = false;
            RetrySettingsLoadButton.Visibility = Visibility.Collapsed;
            RenderSavedPreference(null);
            RenderRuntimePreference(_lastRuntimeResult);
            WindowStatusText.Text = "已读取保存的设置。";
        }
        catch (Exception exception)
        {
            LanguageSavedStatusText.Text = $"无法读取保存值：{exception.Message}";
            LanguageSavedStatusText.Foreground = (Brush)FindResource("ErrorText");
            WindowStatusText.Text = "设置仍未知；修复存储后可重试读取。";
        }
    }

    internal void UpdateRuntimePreference(RuntimeCompatibilityResult? result)
    {
        _lastRuntimeResult = result;
        RenderRuntimePreference(result);
    }

    private void RenderSavedPreference(string? settingsLoadError)
    {
        if (!_settingsLoaded)
        {
            LanguageSavedStatusText.Text = string.IsNullOrWhiteSpace(settingsLoadError)
                ? "无法读取保存值；当前偏好未知。"
                : $"无法读取保存值：{settingsLoadError}";
            LanguageSavedStatusText.Foreground = (Brush)FindResource("ErrorText");
            return;
        }

        LanguageSavedStatusText.Text = _options.ForceSimplifiedChinese
            ? "已保存：固定使用简体中文。"
            : "已保存：关闭固定语言。";
        LanguageSavedStatusText.Foreground = (Brush)FindResource("PrimaryText");
    }

    private void RenderRuntimePreference(RuntimeCompatibilityResult? result)
    {
        if (result is null)
        {
            LanguageRuntimeStatusText.Text = _options.ForceSimplifiedChinese
                ? "待连接：连接调试版 Codex 后生效。"
                : "未连接：关闭设置已保存；连接实例后可确认恢复状态。";
            LanguageRuntimeStatusText.Foreground = (Brush)FindResource("SecondaryText");
            return;
        }

        var (text, brushKey) = result.State switch
        {
            RuntimeCompatibilityState.Applied when _options.ForceSimplifiedChinese =>
                ("已生效：当前目标使用简体中文。", "SuccessText"),
            RuntimeCompatibilityState.Applied =>
                ("状态异常：目标已应用语言覆盖，但当前开关为关闭。", "WarningText"),
            RuntimeCompatibilityState.Disabled when !_options.ForceSimplifiedChinese =>
                ($"已关闭：{result.Message ?? "固定简体中文不再启用。"}", "SuccessText"),
            RuntimeCompatibilityState.Disabled =>
                ("未生效：目标语言覆盖当前处于关闭状态。", "WarningText"),
            RuntimeCompatibilityState.Pending =>
                ($"待处理：{result.Message ?? "目标尚未就绪，设置已保存。"}", "WarningText"),
            RuntimeCompatibilityState.Unsupported =>
                ($"当前宿主不支持：{result.Message ?? "无法设置界面语言。"}", "WarningText"),
            RuntimeCompatibilityState.TimedOut =>
                ($"等待超时：{result.Message ?? "未能确认界面语言状态。"}", "ErrorText"),
            _ => ($"未能生效：{result.Message ?? "请检查目标状态后重试。"}", "ErrorText")
        };
        LanguageRuntimeStatusText.Text = text;
        LanguageRuntimeStatusText.Foreground = (Brush)FindResource(brushKey);
        LanguageRuntimeStatusText.ToolTip = string.IsNullOrWhiteSpace(result.Endpoint)
            ? text
            : $"{text}\n{result.Endpoint}";
    }

    private void MagpieSettingsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (!_hasInspectedMagpie)
        {
            InspectMagpieCompatibility();
        }
    }

    private void InspectButton_Click(object sender, RoutedEventArgs e) => InspectMagpieCompatibility();

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
            {
                ExecutablePathInput.Text = result.ExecutablePath;
            }

            RenderMagpieResult(result);
        }
        catch (Exception exception)
        {
            _hasInspectedMagpie = true;
            _currentMagpieResult = null;
            SupportIndicator.Fill = (Brush)FindResource("ErrorText");
            SupportTitle.Text = "无法完成检测";
            SupportSubtitle.Text = "检测未能读取 Magpie 状态。";
            MagpieRunningText.Text = "运行状态：未知";
            CompatibilityMessageText.Text = exception.Message;
            VersionText.Text = "版本：未检测";
            ExecutablePathText.Text = "路径：未检测";
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

        SupportIndicator.Fill = (Brush)FindResource(brushKey);
        SupportTitle.Text = title;
        SupportSubtitle.Text = result.IsInstalled
            ? result.ProductVersion is { Length: > 0 } version ? $"检测到 Magpie · {version}" : "检测到 Magpie"
            : "未检测到可识别的 Magpie，可选择便携版或指定路径。";
        MagpieRunningText.Text = result.IsRunning ? "运行状态：正在运行" : "运行状态：未运行";
        CompatibilityMessageText.Text = result.Support switch
        {
            MagpieCompatibilitySupport.Unsupported =>
                "Magpie 自身启动 Codex 不附带调试参数。需要换肤时，从 SkinManager 启动调试版并应用皮肤；网关模型配置仍可独立使用。",
            MagpieCompatibilitySupport.Unverified =>
                "此版本的 Magpie 启动参数尚未核实。网关模型配置与皮肤启动参数相互独立。",
            _ => result.Message
        };
        VersionText.Text = result.ProductVersion is { Length: > 0 } productVersion
            ? $"版本：{productVersion}"
            : "版本：未检测";
        ExecutablePathText.Text = result.ExecutablePath is { Length: > 0 } executablePath
            ? $"路径：{executablePath}"
            : "路径：未检测";
        EvidenceText.Text = result.Evidence;
        UpdateMagpieControls(result);
    }

    private void ChooseExecutableButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Magpie 程序",
            Filter = "应用程序|*.exe|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ExecutablePathInput.Text = dialog.FileName;
        InspectMagpieCompatibility();
    }

    private async void LaunchMagpieButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var selectedPath = string.IsNullOrWhiteSpace(ExecutablePathInput.Text)
            ? null
            : ExecutablePathInput.Text.Trim();
        SetBusy(true, "正在启动 Magpie…");
        try
        {
            await _launchMagpie(selectedPath, CancellationToken.None);
            MagpieActionStatusText.Text = "已启动 Magpie；未修改其配置。";
            InspectMagpieCompatibility();
        }
        catch (Exception exception)
        {
            MagpieActionStatusText.Text = $"启动 Magpie 失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LaunchDebugCodexButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true, "正在请求启动 Codex 调试版…");
        try
        {
            var status = await _launchDebugCodex();
            WindowStatusText.Text = status.State == RuntimeStatusState.Error
                ? $"启动未完成：{status.Message ?? "请检查实例状态后重试。"}"
                : status.State == RuntimeStatusState.Connected
                    ? status.Message ?? "目标已连接并同步界面语言设置；未应用皮肤。"
                    : "已请求启动。此操作不会应用皮肤；端口就绪后会核验目标并同步保存的语言设置。";
        }
        catch (Exception exception)
        {
            WindowStatusText.Text = $"启动 Codex 调试版失败：{exception.Message}";
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
        ForceSimplifiedChineseCheckBox.IsEnabled = _settingsLoaded && !_isBusy;
        RetrySettingsLoadButton.IsEnabled = !_isBusy;
        LaunchDebugCodexButton.IsEnabled = !_isBusy;
        if (!string.IsNullOrWhiteSpace(message))
        {
            WindowStatusText.Text = message;
        }

        UpdateMagpieControls(_currentMagpieResult);
    }

    private MagpieCompatibilityResult? _currentMagpieResult;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CompatibilityWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
        {
            return;
        }

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

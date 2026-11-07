using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SkinManager.Runtime;

namespace SkinManager.Desktop;

public partial class CompatibilityWindow : Window
{
    private readonly Func<string?, MagpieCompatibilityResult> _inspect;
    private readonly Func<string?, CancellationToken, Task> _launchMagpie;
    private readonly Func<Task<RuntimeStatus>> _launchDebugCodex;
    private MagpieCompatibilityResult? _result;
    private bool _isBusy;

    public CompatibilityWindow(
        Func<Task<RuntimeStatus>> launchDebugCodex,
        MagpieCompatibilityService? service = null)
        : this(launchDebugCodex, CreateActions(service))
    {
    }

    internal CompatibilityWindow(
        Func<Task<RuntimeStatus>> launchDebugCodex,
        Func<string?, MagpieCompatibilityResult> inspect,
        Func<string?, CancellationToken, Task> launchMagpie)
        : this(launchDebugCodex, (inspect, launchMagpie))
    {
    }

    private CompatibilityWindow(
        Func<Task<RuntimeStatus>> launchDebugCodex,
        (Func<string?, MagpieCompatibilityResult> Inspect,
         Func<string?, CancellationToken, Task> Launch) actions)
    {
        _launchDebugCodex = launchDebugCodex;
        _inspect = actions.Inspect;
        _launchMagpie = actions.Launch;
        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: true, animateClose: true, animateButtonPress: true);
        Loaded += (_, _) => InspectCompatibility();
    }

    private void InspectButton_Click(object sender, RoutedEventArgs e) => InspectCompatibility();

    private void InspectCompatibility()
    {
        try
        {
            var selectedPath = string.IsNullOrWhiteSpace(ExecutablePathInput.Text)
                ? null
                : ExecutablePathInput.Text.Trim();
            _result = _inspect(selectedPath);
            if (string.IsNullOrWhiteSpace(selectedPath) && !string.IsNullOrWhiteSpace(_result.ExecutablePath))
            {
                ExecutablePathInput.Text = _result.ExecutablePath;
            }

            RenderResult(_result);
        }
        catch (Exception exception)
        {
            _result = null;
            SupportIndicator.Fill = (Brush)FindResource("ErrorText");
            SupportTitle.Text = "无法完成检测";
            SupportSubtitle.Text = "检测未能读取 Magpie 状态。";
            MagpieRunningText.Text = "运行状态：未知";
            CompatibilityMessageText.Text = exception.Message;
            VersionText.Text = "版本：未检测";
            ExecutablePathText.Text = "路径：未检测";
            EvidenceText.Text = string.Empty;
            LaunchMagpieButton.IsEnabled = false;
            UpdateControls();
        }
    }

    private void RenderResult(MagpieCompatibilityResult result)
    {
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
                "Magpie 自身启动 Codex 不附带调试参数。从 SkinManager 启动可同时换肤并使用网关模型。",
            MagpieCompatibilitySupport.Unverified =>
                "此版本的 Magpie 启动参数尚未核实。从 SkinManager 启动可换肤并使用网关模型。",
            _ => result.Message
        };
        VersionText.Text = result.ProductVersion is { Length: > 0 } productVersion
            ? $"版本：{productVersion}"
            : "版本：未检测";
        ExecutablePathText.Text = result.ExecutablePath is { Length: > 0 } executablePath
            ? $"路径：{executablePath}"
            : "路径：未检测";
        EvidenceText.Text = result.Evidence;
        LaunchMagpieButton.IsEnabled = result.CanLaunch && !result.IsRunning && !_isBusy;
        UpdateControls();
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
        InspectCompatibility();
    }

    private async void LaunchMagpieButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _result is not { CanLaunch: true } result || result.IsRunning)
        {
            return;
        }

        SetBusy(true, "正在启动 Magpie…");
        try
        {
            var selectedPath = string.IsNullOrWhiteSpace(ExecutablePathInput.Text)
                ? null
                : ExecutablePathInput.Text.Trim();
            await _launchMagpie(selectedPath, CancellationToken.None);
            ActionStatusText.Text = "已启动 Magpie；未修改其启动参数配置。";
            InspectCompatibility();
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = $"启动 Magpie 失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

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
            ActionStatusText.Text = status.State == RuntimeStatusState.Error
                ? $"启动未完成：{status.Message ?? "请检查Codex实例状态后重试。"}"
                : "已提交启动请求。此操作不会应用皮肤；请等待调试端口就绪。";
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = $"启动 Codex 调试版失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        if (!string.IsNullOrWhiteSpace(message))
        {
            ActionStatusText.Text = message;
        }

        UpdateControls();
    }

    private void UpdateControls()
    {
        ChooseExecutableButton.IsEnabled = !_isBusy;
        InspectButton.IsEnabled = !_isBusy;
        LaunchDebugCodexButton.IsEnabled = !_isBusy;
        LaunchMagpieButton.IsEnabled = !_isBusy && _result is { CanLaunch: true, IsRunning: false };
    }

    private static (Func<string?, MagpieCompatibilityResult> Inspect,
        Func<string?, CancellationToken, Task> Launch) CreateActions(MagpieCompatibilityService? service)
    {
        var actualService = service ?? new MagpieCompatibilityService();
        return (actualService.Inspect, actualService.LaunchAsync);
    }
}

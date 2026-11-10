using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using SkinManager.Core;
using SkinManager.Market;
using SkinManager.Rendering;
using SkinManager.Runtime;
using SkinManager.Updating;

namespace SkinManager.Desktop;

public partial class MainWindow : Window
{
    private readonly SkinStore _store;
    private readonly SkinOrderStore _skinOrderStore;
    private ThemePreviewRenderer? _previewRenderer;
    private readonly BasePackage _basePackage;
    private readonly UpdateCoordinator? _updateCoordinator;
    private readonly IInstallerLauncher? _installerLauncher;
    private readonly ICodexInstanceService? _codexInstanceService;
    private readonly ICodexConfigReadabilityService _codexConfigReadabilityService;
    private readonly Func<CodexInstance, bool>? _confirmInstanceClose;
    private readonly Func<CodexInstance, bool>? _confirmInstanceForceClose;
    private readonly Func<CancellationToken, Task<RuntimeStatus>>? _runtimeStatusReader;
    private readonly Func<string, MessageBoxResult>? _editorClosePrompt;
    private readonly Func<string, MessageBoxResult>? _importReplacePrompt;
    private readonly Func<SkinMarketClient>? _skinMarketClientFactory;
    private readonly Func<IMarketAccountAuthProvider>? _marketAccountAuthProviderFactory;
    private readonly bool _closeAfterSuccessfulInstall;
    private readonly string _previewProfileDirectory;
    private readonly RuntimeController _runtimeController = new();
    private readonly RuntimeApplyWorkflow _runtimeApplyWorkflow;
    private readonly CompatibilitySettingsStore _compatibilitySettingsStore;
    private readonly SemaphoreSlim _compatibilityConfigureGate = new(1, 1);
    private readonly bool _readRuntimeStatusOnLoad;
    private readonly ObservableCollection<SkinCard> _skins = [];
    private readonly ICollectionView _skinsView;
    private readonly Dictionary<UIElement, long> _visibilityTransitions = [];
    private readonly HashSet<UIElement> _activeVisibilityTransitions = [];
    private SkinPackage? _selectedPackage;
    private bool _showingOriginal;
    private bool _isBusy;
    private bool _imageReadWarning;
    private bool _selectedImageAvailable;
    private bool _previewInitialized;
    private bool _previewInitializationFailed;
    private string? _previewErrorMessage;
    private bool _interactionMotionReady;
    private bool _hasLoaded;
    private bool _hasBeenActivated;
    private bool _previewWasActive;
    private bool _previewNeedsRefresh;
    private bool _runtimeOperationInProgress;
    private bool _runtimeActionCancellable;
    private bool _isEditing;
    private bool _previewClosed;
    private bool _editorResourcesChanged;
    private bool _allowWindowClose;
    private bool _closePreparationInProgress;
    private UpdatesWindow? _updatesWindow;
    private SkinMarketWindow? _skinMarketWindow;
    private SettingsWindow? _settingsWindow;
    private SupportAuthorWindow? _supportAuthorWindow;
    private SkinPackage? _editOriginalPackage;
    private string? _editOriginalId;
    private int _previewTransparency = 30;
    private long _editorPreviewSequence;
    private long _previewStatusSequence;
    private long _originalTransitionSequence;
    private long _runtimeStatusSequence;
    private readonly DispatcherTimer _editorPreviewTimer;
    private readonly DispatcherTimer _runtimeStatusRefreshTimer;
    private CodexInstance? _selectedConnectionInstance;
    private RuntimeStatus? _lastRuntimeStatus;
    private string? _protectedRuntimeResultMessage;
    private CodexProcessIdentity? _lastConnectedInstance;
    private bool _runtimeStatusRefreshInProgress;
    private bool _windowClosed;
    private CancellationTokenSource? _runtimeActionCancellation;
    private bool _settingInstancePort;
    private RuntimeCompatibilityOptions _compatibilityOptions = new();
    private bool _compatibilitySettingsLoaded;
    private string? _compatibilitySettingsLoadError;
    private RuntimeCompatibilityResult? _lastCompatibilityResult;
    private Func<RuntimeCompatibilityRequest, CancellationToken, Task<RuntimeCompatibilityResult>> _configureCompatibility = null!;
    private bool _startupCompatibilityReconciled;
    private long _visibilitySequence;

    private const double SkinThumbnailWidth = 76;
    private const double SkinThumbnailHeight = 58;

    public MainWindow(
        SkinStore store,
        BasePackage basePackage,
        string? previewProfileDirectory = null,
        bool readRuntimeStatusOnLoad = true,
        UpdateCoordinator? updateCoordinator = null,
        IInstallerLauncher? installerLauncher = null,
        Func<string, MessageBoxResult>? editorClosePrompt = null,
        bool closeAfterSuccessfulInstall = true,
        ICodexInstanceService? codexInstanceService = null,
        Func<CodexInstance, bool>? confirmInstanceClose = null,
        Func<CodexInstance, bool>? confirmInstanceForceClose = null,
        Func<CancellationToken, Task<RuntimeStatus>>? runtimeStatusReader = null,
        Func<string, MessageBoxResult>? importReplacePrompt = null,
        Func<SkinMarketClient>? skinMarketClientFactory = null,
        ICodexConfigReadabilityService? codexConfigReadabilityService = null,
        Func<CodexInstance, CancellationToken, Task<bool>>? confirmDebugModeSwitch = null,
        Func<IMarketAccountAuthProvider>? marketAccountAuthProviderFactory = null)
    {
        _store = store;
        _skinOrderStore = new SkinOrderStore(Path.Combine(store.DataDirectory, ".skin-order.json"));
        _basePackage = basePackage;
        _readRuntimeStatusOnLoad = readRuntimeStatusOnLoad;
        _updateCoordinator = updateCoordinator;
        _installerLauncher = installerLauncher;
        _codexInstanceService = codexInstanceService;
        _codexConfigReadabilityService = codexConfigReadabilityService ?? new CodexConfigReadabilityService();
        _runtimeApplyWorkflow = new RuntimeApplyWorkflow(
            _runtimeController.LaunchDebugClientAsync,
            _runtimeController.ApplyAsync,
            codexInstanceService,
            progress: QueueRuntimeProgress,
            confirmDebugModeSwitch: confirmDebugModeSwitch ?? ConfirmDebugModeSwitchAsync);
        _configureCompatibility = _runtimeController.ConfigureCompatibilityAsync;
        _confirmInstanceClose = confirmInstanceClose;
        _confirmInstanceForceClose = confirmInstanceForceClose;
        _runtimeStatusReader = runtimeStatusReader;
        _editorClosePrompt = editorClosePrompt;
        _importReplacePrompt = importReplacePrompt;
        _skinMarketClientFactory = skinMarketClientFactory;
        _marketAccountAuthProviderFactory = marketAccountAuthProviderFactory;
        _closeAfterSuccessfulInstall = closeAfterSuccessfulInstall;
        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this, animateWindow: false, animateClose: false, animateButtonPress: false);
        AppThemeService.ThemeChanged += MainWindow_ThemeChanged;
        UpdateRuntimeTargetSummary();
        _previewProfileDirectory = Path.GetFullPath(previewProfileDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatGPTSkinManager",
            "PreviewBrowser"));
        var compatibilitySettingsPath = previewProfileDirectory is null
            ? null
            : Path.Combine(_previewProfileDirectory, "compatibility-settings.json");
        _compatibilitySettingsStore = new CompatibilitySettingsStore(compatibilitySettingsPath);
        TryLoadCompatibilitySettings();
        _previewRenderer = new ThemePreviewRenderer(PreviewBrowser, _previewProfileDirectory, basePackage);
        _editorPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _editorPreviewTimer.Tick += EditorPreviewTimer_Tick;
        _runtimeStatusRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _runtimeStatusRefreshTimer.Tick += RuntimeStatusRefreshTimer_Tick;
        PreviewBaseVersion.Text = $"原生静态样板 · {basePackage.SourceVersion}";
        PreviewViewport.ToolTip = $"本地原生样板 {basePackage.SourceVersion} · 离线静态结构预览";
        PreviewBaseVersion.ToolTip = $"依据本地原生静态样板构建的结构预览，不是当前运行的真实客户端截图。来源版本：{basePackage.SourceVersion}";
        AppVersionLabel.Text = UpdateCoordinator.ReadCurrentVersionTag();
        UpdateUpdateEntryLabel();
        if (_updateCoordinator is not null)
        {
            _updateCoordinator.PropertyChanged += UpdateCoordinator_PropertyChanged;
        }
        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;
        StateChanged += MainWindow_StateChanged;
        IsVisibleChanged += MainWindow_IsVisibleChanged;
        PreviewBrowser.IsVisibleChanged += PreviewBrowser_IsVisibleChanged;
        _previewRenderer.CurrentResolvedSkinChanged += PreviewRenderer_CurrentResolvedSkinChanged;
        _previewRenderer.ComponentSelected += PreviewRenderer_ComponentSelected;
        _previewRenderer.BackgroundFocusChanged += PreviewRenderer_BackgroundFocusChanged;
        _runtimeController.StatusChanged += RuntimeController_StatusChanged;
        SkinEditor.DraftChanged += SkinEditor_DraftChanged;
        SkinEditor.ScopeChanged += SkinEditor_ScopeChanged;
        SkinEditor.SaveRequested += SkinEditor_SaveRequested;
        SkinEditor.CancelRequested += SkinEditor_CancelRequested;
        SkinEditor.SaveAsRequested += SkinEditor_SaveAsRequested;
        SkinEditor.BackgroundFocusModeChanged += SkinEditor_BackgroundFocusModeChanged;
        SkinEditor.EditorModeChanged += SkinEditor_EditorModeChanged;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _windowClosed = true;
            AppThemeService.ThemeChanged -= MainWindow_ThemeChanged;
            _runtimeActionCancellation?.Cancel();
            if (_updateCoordinator is not null)
            {
                _updateCoordinator.PropertyChanged -= UpdateCoordinator_PropertyChanged;
                _updateCoordinator.Dispose();
            }
            _skinMarketWindow?.Close();
            _settingsWindow?.Close();
            _supportAuthorWindow?.Close();
            _previewStatusSequence++;
            _originalTransitionSequence++;
            _editorPreviewTimer.Stop();
            _runtimeStatusRefreshTimer.Stop();
            _runtimeController.StatusChanged -= RuntimeController_StatusChanged;
            if (_previewRenderer is { } renderer)
            {
                renderer.CurrentResolvedSkinChanged -= PreviewRenderer_CurrentResolvedSkinChanged;
                renderer.ComponentSelected -= PreviewRenderer_ComponentSelected;
                renderer.BackgroundFocusChanged -= PreviewRenderer_BackgroundFocusChanged;
                renderer.Dispose();
                _previewRenderer = null;
            }
        };
        _skinsView = CollectionViewSource.GetDefaultView(_skins);
        _skinsView.Filter = MatchesSearch;
        SkinList.ItemsSource = _skinsView;
        UpdateSortStatus();
        UpdateListEmptyState();
        ShowNoSelection("皮肤库还是空的", "从市场浏览并下载皮肤，或导入本地皮肤包。");
        UpdateRuntimeControls();
    }

    private async void ApplyRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeOperationInProgress || _selectedPackage is not { } package)
        {
            return;
        }

        if (!TryReadRuntimePort(out var port))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _runtimeActionCancellation = cancellation;
        var operationGeneration = BeginRuntimeOperation("正在核验连接目标…", cancellable: true);
        try
        {
            var transparency = PreviewTransparencyFollowCheckBox.IsChecked == true
                ? null
                : (double?)_previewTransparency;
            var request = BuildRuntimeApplyRequest(package, port, transparency);
            var activeInstance = GetActiveConnectionInstance(port, out var hasActiveConnection);
            var status = await _runtimeApplyWorkflow.StartOrApplyAsync(
                request,
                port,
                _selectedConnectionInstance,
                activeInstance,
                hasActiveConnection,
                cancellation.Token);
            DisplayRuntimeStatus(
                status,
                status.State == RuntimeStatusState.Error
                    ? "本次操作未完成。请查看原因后重试。"
                    : status.AppliedTargetCount > 0
                        ? $"当前皮肤：{package.Name}，已应用到 {status.AppliedTargetCount} 个页面。"
                        : $"Codex 已连接，当前页面尚未就绪；皮肤“{package.Name}”尚未应用。",
                protectRuntimeResult: status.State == RuntimeStatusState.Error);
            if (status.State == RuntimeStatusState.Connected && _compatibilitySettingsLoaded)
            {
                RuntimeCompatibilityResult? compatibilityResult = status.CompatibilityResult;
                if (compatibilityResult is null)
                {
                    try
                    {
                        compatibilityResult = await ConfigureCompatibilityForCurrentTargetAsync(
                            _compatibilityOptions,
                            cancellation.Token,
                            callerOwnsBusyState: true);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        compatibilityResult = new RuntimeCompatibilityResult(
                            RuntimeCompatibilityState.Pending,
                            status.Endpoint,
                            status.AppliedTargetCount > 0
                                ? "皮肤已应用；语言状态确认已取消。已保存设置会在下次连接后重试。"
                                : "Codex 连接已建立，皮肤尚未应用；语言状态确认已取消。已保存设置会在下次连接后重试。",
                            status.IsDebugPortOpen,
                            status.ConnectedInstance);
                    }
                }

                _lastCompatibilityResult = compatibilityResult ?? new RuntimeCompatibilityResult(
                    RuntimeCompatibilityState.Pending,
                    status.Endpoint,
                    status.AppliedTargetCount > 0
                        ? "皮肤已应用；当前实例的语言状态尚未确认。"
                        : "Codex 连接已建立，皮肤尚未应用；当前实例的语言状态尚未确认。",
                    status.IsDebugPortOpen,
                    status.ConnectedInstance);
                _settingsWindow?.UpdateRuntimePreference(_lastCompatibilityResult);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetRuntimeActionCanceledNotice();
        }
        catch (Exception exception)
        {
            SetRuntimeError(exception.Message, "本次操作未完成。请查看原因后重试。");
        }
        finally
        {
            if (ReferenceEquals(_runtimeActionCancellation, cancellation))
            {
                _runtimeActionCancellation = null;
            }
            cancellation.Dispose();
            FinishRuntimeOperation(operationGeneration);
        }
    }

    private static bool TryReadRuntimeStatusPort(RuntimeStatus status, out int port)
    {
        if (Uri.TryCreate(status.Endpoint, UriKind.Absolute, out var endpoint))
        {
            port = endpoint.Port;
            return port is >= 1 and <= 65535;
        }

        port = 0;
        return false;
    }

    private CodexProcessIdentity? GetActiveConnectionInstance(int port, out bool isConnected)
    {
        if (_lastRuntimeStatus is { State: RuntimeStatusState.Connected } status &&
            TryReadRuntimeStatusPort(status, out var connectedPort) && connectedPort == port)
        {
            isConnected = true;
            return status.ConnectedInstance;
        }

        isConnected = false;
        return null;
    }

    private RuntimeApplyRequest BuildRuntimeApplyRequest(SkinPackage package, int port, double? transparency)
    {
        var selected = _selectedConnectionInstance;
        var endpoint = selected?.DebugEndpoint;
        if (_lastRuntimeStatus is
            { State: RuntimeStatusState.Connected, ConnectedInstance: { } connectedIdentity, Endpoint: { } activeEndpoint } &&
            (selected is null || SameProcessIdentity(selected.Identity, connectedIdentity)) &&
            TryReadRuntimeStatusPort(_lastRuntimeStatus, out var activePort) && activePort == port &&
            Uri.TryCreate(activeEndpoint, UriKind.Absolute, out var verifiedEndpoint) &&
            verifiedEndpoint.Scheme == Uri.UriSchemeHttp && verifiedEndpoint.IsLoopback)
        {
            endpoint = verifiedEndpoint.ToString();
        }

        return new RuntimeApplyRequest
        {
            SkinId = package.Id,
            RuntimeScript = SkinRuntimeCompiler.BuildRuntimeScript(package, transparency),
            RemoveScript = SkinRuntimeCompiler.BuildRemoveScript(),
            Endpoint = endpoint ?? $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}",
            ExpectedInstance = selected?.Identity,
            Compatibility = _compatibilitySettingsLoaded ? _compatibilityOptions : null
        };
    }

    private void TryLoadCompatibilitySettings()
    {
        try
        {
            _compatibilityOptions = _compatibilitySettingsStore.Load();
            _compatibilitySettingsLoaded = true;
            _compatibilitySettingsLoadError = null;
        }
        catch (Exception exception)
        {
            _compatibilityOptions = new RuntimeCompatibilityOptions();
            _compatibilitySettingsLoaded = false;
            _compatibilitySettingsLoadError = exception.Message;
        }
    }

    private RuntimeCompatibilityOptions ReloadCompatibilitySettings()
    {
        _compatibilityOptions = _compatibilitySettingsStore.Load();
        _compatibilitySettingsLoaded = true;
        _compatibilitySettingsLoadError = null;
        return _compatibilityOptions;
    }

    private async Task<RuntimeCompatibilityResult?> SaveAndConfigureCompatibilityAsync(
        RuntimeCompatibilityOptions options,
        CancellationToken cancellationToken)
    {
        if (_runtimeOperationInProgress)
        {
            throw new InvalidOperationException("管理器正在执行其他操作，请完成后重试。");
        }

        SetRuntimeOperationInProgress(true, "正在保存并同步界面语言设置…");
        try
        {
            _compatibilitySettingsStore.Save(options);
            _compatibilityOptions = options;
            _compatibilitySettingsLoaded = true;
            _compatibilitySettingsLoadError = null;
            var result = await ConfigureCompatibilityForCurrentTargetAsync(options, cancellationToken, callerOwnsBusyState: true);
            _lastCompatibilityResult = result;
            return result;
        }
        finally
        {
            SetRuntimeOperationInProgress(false);
        }
    }

    private async Task<RuntimeCompatibilityResult?> ConfigureCompatibilityForCurrentTargetAsync(
        RuntimeCompatibilityOptions options,
        CancellationToken cancellationToken,
        bool callerOwnsBusyState = false)
    {
        if (!_compatibilitySettingsLoaded)
        {
            return null;
        }

        var candidate = GetCompatibilityTarget();
        if (candidate is null)
        {
            return null;
        }

        var endpoint = candidate.Value.Endpoint;
        if (_codexInstanceService is null)
        {
            var missingService = new RuntimeCompatibilityResult(
                RuntimeCompatibilityState.Pending,
                endpoint,
                "无法核验 Codex 实例；请使用“选择窗口”刷新并重新选择目标。",
                false,
                candidate.Value.Identity);
            _lastCompatibilityResult = missingService;
            _settingsWindow?.UpdateRuntimePreference(missingService);
            return missingService;
        }

        if (_runtimeOperationInProgress && !callerOwnsBusyState)
        {
            var deferred = new RuntimeCompatibilityResult(
                RuntimeCompatibilityState.Pending,
                endpoint,
                "管理器正在执行其他操作；设置已保存，将在下一次连接时应用。",
                false,
                candidate.Value.Identity);
            _lastCompatibilityResult = deferred;
            _settingsWindow?.UpdateRuntimePreference(deferred);
            return deferred;
        }

        var ownsBusyState = false;
        if (!callerOwnsBusyState)
        {
            SetRuntimeOperationInProgress(true, "正在同步界面语言设置…");
            ownsBusyState = true;
        }

        var acquiredGate = false;
        try
        {
            await _compatibilityConfigureGate.WaitAsync(cancellationToken);
            acquiredGate = true;
            var instances = await _codexInstanceService.GetInstancesAsync(cancellationToken);
            var target = instances.FirstOrDefault(instance => SameProcessIdentity(instance.Identity, candidate.Value.Identity));
            if (target is not
                { CanConnect: true, Kind: CodexInstanceKind.Debug, IsExitResidual: false, DebugPort: int debugPort, DebugEndpoint: { } verifiedEndpoint } ||
                debugPort != candidate.Value.Port)
            {
                var unavailable = new RuntimeCompatibilityResult(
                    RuntimeCompatibilityState.Pending,
                    endpoint,
                    "所选 Codex 实例已退出或端口尚未就绪；设置已保存，刷新实例后可重试。",
                    false,
                    candidate.Value.Identity);
                _lastCompatibilityResult = unavailable;
                _settingsWindow?.UpdateRuntimePreference(unavailable);
                return unavailable;
            }

            endpoint = GetActiveEndpointFor(candidate.Value.Identity, candidate.Value.Port) ?? verifiedEndpoint;

            var request = new RuntimeCompatibilityRequest
            {
                Endpoint = endpoint,
                ExpectedInstance = target.Identity,
                Options = options
            };
            var result = await _configureCompatibility(request, cancellationToken);
            _lastCompatibilityResult = result;
            _settingsWindow?.UpdateRuntimePreference(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failed = new RuntimeCompatibilityResult(
                RuntimeCompatibilityState.Failed,
                endpoint,
                exception.Message,
                false,
                candidate.Value.Identity);
            _lastCompatibilityResult = failed;
            _settingsWindow?.UpdateRuntimePreference(failed);
            return failed;
        }
        finally
        {
            if (acquiredGate)
            {
                _compatibilityConfigureGate.Release();
            }
            if (ownsBusyState)
            {
                SetRuntimeOperationInProgress(false);
            }
        }
    }

    private (CodexProcessIdentity Identity, int Port, string Endpoint)? GetCompatibilityTarget()
    {
        if (_lastRuntimeStatus is
            { State: RuntimeStatusState.Connected, ConnectedInstance: { } connectedIdentity, Endpoint: { } endpointText } &&
            Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback && endpoint.Port is >= 1 and <= 65535 &&
            TryReadRuntimePort(out var selectedPort) && endpoint.Port == selectedPort &&
            (_selectedConnectionInstance is null || SameProcessIdentity(_selectedConnectionInstance.Identity, connectedIdentity)))
        {
            return (connectedIdentity, endpoint.Port, endpoint.ToString());
        }

        if (_selectedConnectionInstance is
            { CanConnect: true, Kind: CodexInstanceKind.Debug, IsExitResidual: false, DebugPort: int selectedDebugPort, DebugEndpoint: { } selectedEndpoint } selected)
        {
            return (selected.Identity, selectedDebugPort, selectedEndpoint);
        }

        return null;
    }

    private string? GetActiveEndpointFor(CodexProcessIdentity identity, int port) =>
        _lastRuntimeStatus is
            { State: RuntimeStatusState.Connected, ConnectedInstance: { } connectedIdentity, Endpoint: { } endpointText } &&
            SameProcessIdentity(connectedIdentity, identity) &&
            TryReadRuntimeStatusPort(_lastRuntimeStatus, out var connectedPort) && connectedPort == port &&
            Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback
                ? endpoint.ToString()
                : null;

    private static bool SameProcessIdentity(CodexProcessIdentity left, CodexProcessIdentity right) =>
        left.ProcessId == right.ProcessId &&
        left.StartedAtUtc.UtcDateTime == right.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(left.ExecutablePath), Path.GetFullPath(right.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    internal void SetCompatibilityConfiguratorForTesting(
        Func<RuntimeCompatibilityRequest, CancellationToken, Task<RuntimeCompatibilityResult>> configure) =>
        _configureCompatibility = configure;

    private async Task<RuntimeStatus> StartOrConnectAndConfigureCompatibilityAsync(int port, CancellationToken cancellationToken)
    {
        var activeInstance = GetActiveConnectionInstance(port, out var hasActiveConnection);
        var status = await _runtimeApplyWorkflow.StartOrConnectAsync(
            port,
            _selectedConnectionInstance,
            activeInstance,
            hasActiveConnection,
            cancellationToken,
            activeEndpoint: hasActiveConnection ? _lastRuntimeStatus?.Endpoint : null);
        if (status.State != RuntimeStatusState.Connected)
        {
            DisplayRuntimeStatus(status, protectRuntimeResult: status.State == RuntimeStatusState.Error);
            return status;
        }

        Interlocked.Increment(ref _runtimeStatusSequence);
        DisplayRuntimeStatus(status, "Codex 调试目标已核验；没有应用皮肤。");
        if (!_compatibilitySettingsLoaded)
        {
            var unavailableMessage = $"调试目标已就绪；未能读取兼容设置，因此没有同步界面语言。{_compatibilitySettingsLoadError} 尚未应用皮肤。";
            var unavailable = new RuntimeStatus(
                RuntimeStatusState.Connected,
                null,
                status.Endpoint,
                unavailableMessage,
                true,
                0,
                status.ConnectedInstance);
            DisplayRuntimeStatus(unavailable);
            return unavailable;
        }

        var result = await ConfigureCompatibilityForCurrentTargetAsync(
            _compatibilityOptions,
            cancellationToken,
            callerOwnsBusyState: true);
        var compatibilityMessage = result switch
        {
            { State: RuntimeCompatibilityState.Applied } => "简体中文设置已应用。",
            { State: RuntimeCompatibilityState.Disabled } disabled => $"固定语言已关闭：{disabled.Message ?? "当前目标已响应关闭设置。"}",
            { State: RuntimeCompatibilityState.Pending } pending => $"界面语言待处理：{pending.Message ?? "目标暂不可用。"}",
            { State: RuntimeCompatibilityState.Unsupported } unsupported => $"当前目标不支持界面语言设置：{unsupported.Message ?? "宿主接口不可用。"}",
            { State: RuntimeCompatibilityState.TimedOut } timedOut => $"等待界面语言状态超时：{timedOut.Message ?? "请在兼容设置中重试。"}",
            { State: RuntimeCompatibilityState.Failed } failed => $"界面语言设置未完成：{failed.Message ?? "请在兼容设置中重试。"}",
            _ => "界面语言设置已保存；连接状态待确认。"
        };
        var completed = new RuntimeStatus(
            RuntimeStatusState.Connected,
            null,
            status.Endpoint,
            $"调试目标已核验。{compatibilityMessage} 未应用皮肤。",
            true,
            0,
            status.ConnectedInstance);
        DisplayRuntimeStatus(completed);
        return completed;
    }

    private async void LaunchDebugButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeOperationInProgress || !TryReadRuntimePort(out var port))
        {
            return;
        }

        var operationGeneration = BeginRuntimeOperation("正在核验调试目标并同步界面语言…");
        try
        {
            await StartOrConnectAndConfigureCompatibilityAsync(port, CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetRuntimeError(exception.Message, "调试版未能启动或界面语言未能同步。请检查“选择窗口”与兼容设置后重试。");
        }
        finally
        {
            FinishRuntimeOperation(operationGeneration);
        }
    }

    private async void RemoveRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeOperationInProgress)
        {
            return;
        }

        Interlocked.Increment(ref _runtimeStatusSequence);
        SetRuntimeOperationInProgress(true, "正在恢复原生外观…");
        try
        {
            var stopped = await _runtimeController.StopAsync();
            DisplayRuntimeStatus(
                stopped,
                stopped.State == RuntimeStatusState.Error
                    ? "未能恢复原生外观。请确认桌面应用仍在运行后重试。"
                    : "原生外观已恢复。管理器连接已断开。调试端口仍由桌面应用管理。",
                protectRuntimeResult: stopped.State == RuntimeStatusState.Error);
        }
        catch (Exception exception)
        {
            SetRuntimeError(exception.Message, "未能恢复原生外观。请确认桌面应用仍在运行后重试。");
        }
        finally
        {
            SetRuntimeOperationInProgress(false);
        }
    }

    private void RuntimeController_StatusChanged(object? sender, RuntimeStatus status)
    {
        if (_windowClosed)
        {
            return;
        }

        var sequence = Volatile.Read(ref _runtimeStatusSequence);
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_windowClosed && sequence == Volatile.Read(ref _runtimeStatusSequence))
            {
                DisplayRuntimeStatus(status, preserveRuntimeResult: true);
            }
        });
    }

    private void RuntimePortInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingInstancePort || RuntimeTargetText is null)
        {
            return;
        }

        _selectedConnectionInstance = null;
        UpdateRuntimeTargetSummary();
        UpdateRuntimeControls();
        if (_lastCompatibilityResult is not null &&
            (!TryReadRuntimePort(out var selectedPort) ||
             !Uri.TryCreate(_lastCompatibilityResult.Endpoint, UriKind.Absolute, out var configuredEndpoint) ||
             configuredEndpoint.Port != selectedPort))
        {
            _lastCompatibilityResult = null;
            _settingsWindow?.UpdateRuntimePreference(null);
        }
    }

    private void UpdateRuntimeTargetSummary()
    {
        if (RuntimeTargetText is null || RuntimePortInput is null)
        {
            return;
        }

        var targetText = _selectedConnectionInstance is { Kind: CodexInstanceKind.Debug, DebugPort: int debugPort } instance
            ? $"{(string.IsNullOrWhiteSpace(instance.WindowTitle) ? "调试版 Codex" : instance.WindowTitle)} · PID {instance.Identity.ProcessId} · {instance.DebugEndpoint ?? $"端口 {debugPort}"}{(instance.CanConnect && !instance.IsExitResidual ? string.Empty : " · 暂不可连接")}"
            : $"自动查找调试版 · 端口 {RuntimePortInput.Text.Trim()}";
        RuntimeTargetText.Text = targetText;
        RuntimeTargetText.ToolTip = _selectedConnectionInstance is { } selected
            ? $"{targetText}\n{selected.Identity.ExecutablePath}\n启动时间：{selected.Identity.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}{(string.IsNullOrWhiteSpace(selected.ConnectionMessage) ? string.Empty : $"\n{selected.ConnectionMessage}")}"
            : targetText;

        var hasLiveConnectionIdentity = _lastRuntimeStatus is
            { State: RuntimeStatusState.Connected or RuntimeStatusState.Applying, ConnectedInstance: not null };
        if (_lastRuntimeStatus is { State: RuntimeStatusState.Connected } connectedStatus)
        {
            _lastConnectedInstance = connectedStatus.ConnectedInstance;
        }
        else if (_lastRuntimeStatus is { State: RuntimeStatusState.Applying, ConnectedInstance: not null } applyingStatus)
        {
            _lastConnectedInstance = applyingStatus.ConnectedInstance;
        }
        else if (_lastRuntimeStatus?.State == RuntimeStatusState.Stopped)
        {
            _lastConnectedInstance = null;
        }

        var connected = hasLiveConnectionIdentity
            ? _lastRuntimeStatus?.ConnectedInstance
            : _lastRuntimeStatus?.State == RuntimeStatusState.Stopped ? null : _lastConnectedInstance;
        RuntimeConnectedTargetLabel.Text = hasLiveConnectionIdentity
            ? "当前连接"
            : connected is null ? "当前连接" : "上次确认";
        var connectedText = connected is null ? "未连接" : $"PID {connected.ProcessId}";
        RuntimeConnectedTargetText.Text = connectedText;
        RuntimeConnectedTargetText.ToolTip = connected is null
            ? "运行状态尚未报告连接实例。"
            : $"{(hasLiveConnectionIdentity ? "当前连接" : "上次确认连接")}: PID {connected.ProcessId}\n{connected.ExecutablePath}\n启动时间：{connected.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    private async void SelectRuntimeTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeOperationInProgress || _isBusy || _isEditing || _codexInstanceService is not { } instanceService)
        {
            return;
        }

        var dialog = new InstancesWindow(
            instanceService,
            _lastRuntimeStatus?.ConnectedInstance,
            _confirmInstanceClose,
            _confirmInstanceForceClose)
        {
            Owner = this
        };
        var accepted = dialog.ShowDialog() == true;
        if (accepted && dialog.SelectedForConnection is
            { CanConnect: true, Kind: CodexInstanceKind.Debug, IsExitResidual: false, DebugPort: int debugPort, DebugEndpoint: { } debugEndpoint } instance)
        {
            Interlocked.Increment(ref _runtimeStatusSequence);
            _selectedConnectionInstance = instance;
            _settingInstancePort = true;
            RuntimePortInput.Text = debugPort.ToString(CultureInfo.InvariantCulture);
            _settingInstancePort = false;
            UpdateRuntimeTargetSummary();
            _lastCompatibilityResult = null;
            _settingsWindow?.UpdateRuntimePreference(null);
            RuntimeStatusMessage.Text = "已选择调试实例。界面语言会在该目标连接后单独配置；应用皮肤仍需点击主操作。";
            RuntimeStatusMessage.ToolTip = RuntimeStatusMessage.Text;

            if (_compatibilitySettingsLoaded)
            {
                SetRuntimeOperationInProgress(true, "正在核验目标并同步界面语言…");
                try
                {
                    await ConfigureCompatibilityForCurrentTargetAsync(
                        _compatibilityOptions,
                        CancellationToken.None,
                        callerOwnsBusyState: true);
                }
                catch (Exception exception)
                {
                    var failed = new RuntimeCompatibilityResult(
                        RuntimeCompatibilityState.Failed,
                        debugEndpoint,
                        exception.Message,
                        false,
                        instance.Identity);
                    _lastCompatibilityResult = failed;
                    _settingsWindow?.UpdateRuntimePreference(failed);
                }
                finally
                {
                    SetRuntimeOperationInProgress(false);
                }
            }
        }

        if (_readRuntimeStatusOnLoad &&
            _lastRuntimeStatus?.ConnectedInstance is { } connectedIdentity &&
            dialog.ClosedIdentities.Contains(connectedIdentity))
        {
            await RefreshRuntimeStatusAsync();
        }
    }

    private Task<bool> ConfirmDebugModeSwitchAsync(CodexInstance instance, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(
                () => ShowDebugModeSwitchConfirmation(cancellationToken),
                DispatcherPriority.Normal,
                cancellationToken).Task;
        }

        return Task.FromResult(ShowDebugModeSwitchConfirmation(cancellationToken));
    }

    private bool ShowDebugModeSwitchConfirmation(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new DebugModeSwitchConfirmationWindow { Owner = this };
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (dialog.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _ = dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (dialog.IsVisible)
                {
                    dialog.Close();
                }
            }), DispatcherPriority.Send);
        });

        var accepted = dialog.ShowDialog() == true;
        cancellationToken.ThrowIfCancellationRequested();
        return accepted;
    }

    private void SupportAuthorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_supportAuthorWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SupportAuthorWindow { Owner = this };
        _supportAuthorWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_supportAuthorWindow, window))
            {
                _supportAuthorWindow = null;
            }
        };
        window.Show();
    }

    private void MainWindow_ThemeChanged(object? sender, EventArgs e)
    {
        if (_windowClosed)
        {
            return;
        }

        void UpdateInteractiveSurfaces()
        {
            if (_windowClosed)
            {
                return;
            }

            foreach (var button in FindVisualChildren<Button>(this))
            {
                SetButtonSurface(button, hovered: button.IsMouseOver, pressed: button.IsPressed, animate: false);
            }

            foreach (var item in FindVisualChildren<ListBoxItem>(SkinList))
            {
                AnimateSkinListItem(item, item.IsSelected, animate: false);
            }
        }

        if (Dispatcher.CheckAccess())
        {
            UpdateInteractiveSurfaces();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(new Action(UpdateInteractiveSurfaces));
        }
    }

    private void MarketEntryButton_Click(object sender, RoutedEventArgs e) => OpenMarketWindow();

    private void SettingsEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeOperationInProgress || _isBusy || _isEditing)
        {
            return;
        }

        if (_settingsWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(
            _compatibilityOptions,
            _compatibilitySettingsLoaded,
            _compatibilitySettingsLoadError,
            _lastCompatibilityResult,
            SaveAndConfigureCompatibilityAsync,
            ReloadCompatibilitySettings,
            StartDebugClientFromCompatibilityAsync,
            codexConfigReadabilityService: _codexConfigReadabilityService)
        {
            Owner = this
        };
        _settingsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        };
        window.Show();
    }

    private async Task<RuntimeStatus> StartDebugClientFromCompatibilityAsync()
    {
        if (_runtimeOperationInProgress)
        {
            return new RuntimeStatus(RuntimeStatusState.Error, null, null,
                "管理器正在执行其他操作，请完成后重试。", null);
        }

        if (!TryReadRuntimePort(out var port))
        {
            return new RuntimeStatus(RuntimeStatusState.Error, null, null,
                "端口无效。请检查高级设置中的端口。", null);
        }

        Interlocked.Increment(ref _runtimeStatusSequence);
        SetRuntimeOperationInProgress(true, "正在核验调试目标并同步界面语言…");
        try
        {
            return await StartOrConnectAndConfigureCompatibilityAsync(port, CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetRuntimeError(exception.Message, "未能启动 Codex 调试版或同步界面语言。请检查“选择窗口”与兼容设置。");
            return new RuntimeStatus(RuntimeStatusState.Error, null, null, exception.Message, null);
        }
        finally
        {
            SetRuntimeOperationInProgress(false);
        }
    }

    internal SkinMarketWindow? OpenMarketWindow(SkinMarketClient? clientOverride = null)
    {
        if (_runtimeOperationInProgress || _isBusy || _isEditing)
        {
            clientOverride?.Dispose();
            return null;
        }

        if (_skinMarketWindow is { IsVisible: true } existing)
        {
            clientOverride?.Dispose();
            existing.Activate();
            return existing;
        }

        var client = clientOverride ?? _skinMarketClientFactory?.Invoke() ?? new SkinMarketClient();
        var accountAuthProvider = _marketAccountAuthProviderFactory?.Invoke();
        var window = new SkinMarketWindow(
            client,
            UpdateCoordinator.ReadCurrentVersion(),
            _basePackage,
            _previewProfileDirectory,
            SaveMarketPackageAsync,
            ReadInstalledSkinVersions,
            accountAuthProvider,
            useDefaultAccountAuth: clientOverride is null && _skinMarketClientFactory is null && _marketAccountAuthProviderFactory is null,
            readLocalSkinCatalog: _store.GetCatalog,
            exportLocalSkinPackage: (id, path) => _store.ExportZip(id, path, overwriteExisting: true))
        {
            Owner = this
        };
        _skinMarketWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_skinMarketWindow, window))
            {
                _skinMarketWindow = null;
            }
        };
        window.Show();
        return window;
    }

    internal SkinMarketWindow? MarketWindow => _skinMarketWindow;

    private async Task<bool> SaveMarketPackageAsync(SkinPackage package)
    {
        if (_runtimeOperationInProgress || _isBusy || _isEditing)
        {
            throw new InvalidOperationException("管理器正在处理其他操作。请完成后重试安装。");
        }

        SetBusy(true);
        try
        {
            return await SaveImportedPackageAsync(package, _skinMarketWindow);
        }
        finally
        {
            SetBusy(false);
            UpdateLibraryWarning();
        }
    }

    private IReadOnlyDictionary<string, string> ReadInstalledSkinVersions() =>
        _store.GetCatalog().ToDictionary(entry => entry.Id, entry => entry.Version, StringComparer.OrdinalIgnoreCase);

    private async void LocateConnectedWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeOperationInProgress || _isBusy || _codexInstanceService is not { } instanceService ||
            _lastRuntimeStatus?.ConnectedInstance is not { } connectedIdentity)
        {
            return;
        }

        LocateConnectedWindowButton.IsEnabled = false;
        RuntimeStatusMessage.Text = $"正在定位当前连接 PID {connectedIdentity.ProcessId}…";
        RuntimeStatusMessage.ToolTip = RuntimeStatusMessage.Text;
        try
        {
            var instances = await instanceService.GetInstancesAsync();
            var connectedInstance = instances.FirstOrDefault(instance => instance.Identity == connectedIdentity);
            if (connectedInstance is null)
            {
                RuntimeStatusMessage.Text = $"当前连接 PID {connectedIdentity.ProcessId} 已不在实例列表中。请刷新连接状态或重新选择实例。";
                RuntimeStatusMessage.ToolTip = RuntimeStatusMessage.Text;
                return;
            }

            var result = await instanceService.LocateAsync(connectedInstance);
            RuntimeStatusMessage.Text = result switch
            {
                CodexLocateResult.Located => $"已定位当前连接实例 PID {connectedIdentity.ProcessId}。",
                CodexLocateResult.NoWindow => $"当前连接实例 PID {connectedIdentity.ProcessId} 没有可见窗口。",
                CodexLocateResult.ForegroundDenied => $"已找到当前连接实例 PID {connectedIdentity.ProcessId}，但 Windows 未允许切换前台。",
                _ => $"无法定位当前连接实例 PID {connectedIdentity.ProcessId}。"
            };
            RuntimeStatusMessage.ToolTip = RuntimeStatusMessage.Text;
        }
        catch (Exception exception)
        {
            RuntimeStatusMessage.Text = $"定位当前连接窗口失败：{exception.Message}";
            RuntimeStatusMessage.ToolTip = RuntimeStatusMessage.Text;
        }
        finally
        {
            UpdateRuntimeControls();
        }
    }

    private bool TryReadRuntimePort(out int port)
    {
        if (TryParseRuntimePort(RuntimePortInput.Text, out port))
        {
            return true;
        }

        SetRuntimeError("端口必须是 1 到 65535 之间的整数。", "请输入有效的本地调试端口。");
        RuntimePortInput.Focus();
        RuntimePortInput.SelectAll();
        return false;
    }

    private static bool TryParseRuntimePort(string? value, out int port) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
        port is >= 1 and <= 65535;

    private void SetRuntimeOperationInProgress(bool inProgress, string? message = null, bool cancellable = false)
    {
        if (_runtimeOperationInProgress != inProgress)
        {
            Interlocked.Increment(ref _runtimeStatusSequence);
            if (inProgress)
            {
                _protectedRuntimeResultMessage = null;
            }
        }

        _runtimeOperationInProgress = inProgress;
        _runtimeActionCancellable = inProgress && cancellable && _runtimeActionCancellation is not null;
        if (!inProgress)
        {
            CancelRuntimeActionButton.Content = "取消操作";
        }
        if (!string.IsNullOrWhiteSpace(message))
        {
            RuntimeStatusMessage.Text = message;
            RuntimeStatusMessage.ToolTip = message;
        }

        UpdateRuntimeControls();
    }

    private void UpdateRuntimeControls()
    {
        if (RuntimePortInput is null || RuntimePrimaryActionText is null || LocateConnectedWindowButton is null)
        {
            return;
        }

        RuntimePortInput.IsEnabled = !_runtimeOperationInProgress;
        MarketEntryButton.IsEnabled = !_runtimeOperationInProgress && !_isBusy && !_isEditing;
        LaunchDebugButton.IsEnabled = !_runtimeOperationInProgress && !_isEditing;
        ApplyRuntimeButton.IsEnabled = !_runtimeOperationInProgress && !_isBusy && !_isEditing && _selectedPackage is not null;
        RemoveRuntimeButton.IsEnabled = !_runtimeOperationInProgress && !_isEditing;
                SelectRuntimeTargetButton.IsEnabled = !_runtimeOperationInProgress && !_isBusy && !_isEditing && _codexInstanceService is not null;
        SettingsEntryButton.IsEnabled = !_runtimeOperationInProgress && !_isBusy && !_isEditing;
        LocateConnectedWindowButton.IsEnabled = !_runtimeOperationInProgress && !_isBusy && !_isEditing &&
            _codexInstanceService is not null && _lastRuntimeStatus?.ConnectedInstance is not null;
        CancelRuntimeActionButton.Visibility = _runtimeOperationInProgress && _runtimeActionCancellable
            ? Visibility.Visible : Visibility.Collapsed;
        CancelRuntimeActionButton.IsEnabled = _runtimeOperationInProgress && _runtimeActionCancellable &&
            _runtimeActionCancellation?.IsCancellationRequested != true;
        var currentPortMatches = _lastRuntimeStatus is { State: RuntimeStatusState.Connected } connectedStatus &&
            TryParseRuntimePort(RuntimePortInput.Text, out var requestedPort) &&
            TryReadRuntimeStatusPort(connectedStatus, out var connectedPort) && requestedPort == connectedPort;
        var hasActiveConnection = currentPortMatches;
        RuntimeSelectedSkinText.Text = _selectedPackage is { } package
            ? package.Name
            : "尚未选择皮肤；可以先启动调试版 Codex";
        RuntimePrimaryActionText.Text = RuntimeConnectionViewState.GetPrimaryActionLabel(
            hasActiveConnection,
            _selectedConnectionInstance is not null,
            _selectedPackage is not null);
        AutomationProperties.SetName(ApplyRuntimeButton, RuntimePrimaryActionText.Text);
        EditSkinButton.IsEnabled = !_isBusy && !_isEditing && _selectedPackage is not null;
    }

    private void CancelRuntimeActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_runtimeOperationInProgress || !_runtimeActionCancellable || _runtimeActionCancellation is null)
        {
            return;
        }

        RuntimeStatusMessage.Text = "正在取消操作；结果待确认。管理器不会关闭 Codex，也不会自动恢复皮肤。";
        RuntimeStatusMessage.ToolTip = RuntimeStatusMessage.Text;
        CancelRuntimeActionButton.Content = "正在取消…";
        CancelRuntimeActionButton.IsEnabled = false;
        _runtimeActionCancellation.Cancel();
    }

    private void SetRuntimeActionCanceledNotice()
    {
        RuntimeStatusIndicator.SetResourceReference(Ellipse.FillProperty, "WarningText");
        RuntimeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "WarningText");
        RuntimeStatusText.Text = "已取消，结果待确认";
        RuntimeStatusMessage.Text = "操作已取消。管理器不会关闭 Codex，也不会自动恢复皮肤；请稍后查看连接状态。";
        RuntimeStatusText.ToolTip = RuntimeStatusMessage.Text;
        RuntimeStatusMessage.ToolTip = RuntimeStatusMessage.Text;
    }

    private void NewSkinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditing || _isBusy)
        {
            return;
        }

        var draft = SkinCreation.Create($"skin-{Guid.NewGuid():N}"[..13], "新建皮肤", SkinAppearance.Light);
        EnterEditor(draft, isNew: true);
    }

    private void CreateCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditing || _isBusy || _selectedPackage is not { } package)
        {
            return;
        }

        var id = CreateUniqueCopyId(package.Id);
        var draft = SkinCreation.CreateCopy(package, id, $"{package.Name} 副本");
        EnterEditor(draft, isNew: true);
    }

    private string CreateUniqueCopyId(string sourceId)
    {
        var stem = sourceId.Length > 45 ? sourceId[..45] : sourceId;
        var candidate = $"{stem}-copy";
        var suffix = 2;
        while (_store.ContainsId(candidate))
        {
            candidate = $"{stem}-copy-{suffix++}";
        }

        return candidate;
    }

    private void EditSkinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditing || _isBusy || _selectedPackage is not { } package)
        {
            return;
        }

        EnterEditor(SkinDraft.FromPackage(package), isNew: false);
    }

    private void EnterEditor(SkinDraft draft, bool isNew)
    {
        _editOriginalPackage = _selectedPackage;
        _editOriginalId = _selectedPackage?.Id;
        _isEditing = true;
        _editorResourcesChanged = false;
        SkinEditor.SetDraft(draft, isNew);
        SkinEditor_EditorModeChanged(SkinEditor, EventArgs.Empty);
        _originalTransitionSequence++;
        OriginalImageLayer.BeginAnimation(OpacityProperty, null);
        _showingOriginal = false;
        OriginalImageLayer.Visibility = Visibility.Collapsed;
        OriginalImageLayer.Opacity = 1;
        OriginalImageSource.Source = null;
        OriginalButton.Content = "原图";
        if (PreviewOriginalBaseCheckBox.IsChecked == true)
        {
            PreviewOriginalBaseCheckBox.IsChecked = false;
        }
        WorkspaceLeftColumn.Width = new GridLength(380);
        TransitionVisibility(LibraryBorder, Visibility.Collapsed, 145);
        TransitionVisibility(EditorPanel, Visibility.Visible, 165);
        TransitionVisibility(SkinEditor, Visibility.Visible, 165);
        TransitionVisibility(DetailsEmptyState, Visibility.Collapsed, 135);
        TransitionVisibility(DetailsScroll, Visibility.Visible, 145);
        TransitionVisibility(SelectedSummary, Visibility.Collapsed, 135);
        TransitionVisibility(RuntimeControlsBorder, Visibility.Collapsed, 135);
        TransitionVisibility(SelectedMetadata, Visibility.Collapsed, 135);
        TransitionVisibility(ColorSection, Visibility.Collapsed, 135);
        PreviewHeading.Text = "草稿预览";
        PreviewStatus.Text = "正在加载本地草稿…";
        PreviewStatus.Visibility = Visibility.Visible;
        ImportButton.IsEnabled = false;
        NewSkinButton.IsEnabled = false;
        EditSkinButton.IsEnabled = false;
        SkinList.IsEnabled = false;
        SearchBox.IsEnabled = false;
        _ = _previewRenderer?.SetComponentSelectionModeAsync(true);
        _ = _previewRenderer?.SetBackgroundPositionModeAsync(SkinEditor.IsBackgroundFocusEditing);
        UpdateRuntimeControls();
        UpdateTransparencyControls();
        QueueEditorPreviewUpdate(resourcesChanged: false);
        if (!_previewClosed && _previewInitialized && !_previewInitializationFailed)
        {
            PreviewBrowser.Visibility = Visibility.Visible;
        }
        QueuePreviewActivityUpdate();
    }

    private void ExitEditor(bool restoreSavedPreview)
    {
        if (!_isEditing)
        {
            return;
        }

        _isEditing = false;
        _editorPreviewTimer.Stop();
        Interlocked.Increment(ref _editorPreviewSequence);
        _editorResourcesChanged = false;
        _ = _previewRenderer?.SetComponentSelectionModeAsync(false);
        _ = _previewRenderer?.SetBackgroundPositionModeAsync(false);
        SkinEditor.SetBackgroundFocusEditing(false);
        TransitionVisibility(SkinEditor, Visibility.Collapsed, 135);
        TransitionVisibility(EditorPanel, Visibility.Collapsed, 135);
        TransitionVisibility(LibraryBorder, Visibility.Visible, 160);
        WorkspaceLeftColumn.Width = new GridLength(262);
        TransitionVisibility(SelectedSummary, _selectedPackage is null ? Visibility.Collapsed : Visibility.Visible, 145);
        TransitionVisibility(RuntimeControlsBorder, Visibility.Visible, 145);
        TransitionVisibility(SelectedMetadata, _selectedPackage is null ? Visibility.Collapsed : Visibility.Visible, 145);
        TransitionVisibility(ColorSection, _selectedPackage is not null && ColorItems.Items.Count > 0
            ? Visibility.Visible : Visibility.Collapsed, 145);
        TransitionVisibility(DetailsEmptyState, _selectedPackage is null ? Visibility.Visible : Visibility.Collapsed, 155);
        TransitionVisibility(DetailsScroll, _selectedPackage is null ? Visibility.Collapsed : Visibility.Visible, 145);
        PreviewHeading.Text = "预览";
        ImportButton.IsEnabled = !_isBusy;
        NewSkinButton.IsEnabled = !_isBusy;
        EditSkinButton.IsEnabled = !_isBusy && _selectedPackage is not null;
        SkinList.IsEnabled = !_isBusy;
        SearchBox.IsEnabled = !_isBusy;
        _editOriginalPackage = null;
        _editOriginalId = null;
        UpdateTransparencyControls();
        UpdateRuntimeControls();
        if (restoreSavedPreview && _selectedPackage is { } package && !_previewClosed)
        {
            _ = RenderPreviewAsync(package);
        }
    }

    private void SkinEditor_DraftChanged(object? sender, SkinEditorChangedEventArgs e)
    {
        QueueEditorPreviewUpdate(e.ResourcesChanged);
    }

    private void SkinEditor_ScopeChanged(object? sender, EventArgs e)
    {
        if (!_isEditing)
        {
            return;
        }

        var (appearance, scene) = SkinEditor.SelectedScope switch
        {
            "light" => ("light", "home"),
            "dark" => ("dark", "home"),
            "home" => ("skin", "home"),
            "conversation" => ("skin", "conversation"),
            _ => ("skin", "home")
        };
        SelectPreviewValue(PreviewAppearanceBox, appearance);
        SelectPreviewValue(PreviewSceneBox, scene);
        QueueEditorPreviewUpdate(resourcesChanged: false);
    }

    private void SkinEditor_EditorModeChanged(object? sender, EventArgs e)
    {
        if (!_isEditing)
        {
            return;
        }

        if (SkinEditor.IsCreationMode)
        {
            SelectPreviewValue(PreviewAppearanceBox, "skin");
            SelectPreviewValue(PreviewSceneBox, "home");
            QueueEditorPreviewUpdate(resourcesChanged: false);
        }
        else
        {
            SkinEditor_ScopeChanged(sender, e);
        }
    }

    private async void SkinEditor_BackgroundFocusModeChanged(object? sender, EventArgs e)
    {
        if (_previewRenderer is { } renderer)
        {
            await renderer.SetBackgroundPositionModeAsync(_isEditing && SkinEditor.IsBackgroundFocusEditing);
        }
    }

    private void PreviewRenderer_BackgroundFocusChanged(object? sender, PreviewBackgroundFocusEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_isEditing || !SkinEditor.IsBackgroundFocusEditing)
            {
                return;
            }

            if (e.Phase == "begin")
            {
                SkinEditor.BeginBackgroundFocusGesture();
                return;
            }

            SkinEditor.SetBackgroundFocus(e.FocusX, e.FocusY);
            if (e.Phase == "end")
            {
                SkinEditor.EndBackgroundFocusGesture();
            }
        });
    }

    private static void SelectPreviewValue(ComboBox control, string tag)
    {
        var item = control.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), tag, StringComparison.Ordinal));
        if (item is not null)
        {
            control.SelectedItem = item;
        }
    }

    private void QueueEditorPreviewUpdate(bool resourcesChanged)
    {
        if (!_isEditing)
        {
            return;
        }

        _editorResourcesChanged |= resourcesChanged;
        _editorPreviewTimer.Stop();
        _editorPreviewTimer.Start();
    }

    private async void EditorPreviewTimer_Tick(object? sender, EventArgs e)
    {
        _editorPreviewTimer.Stop();
        await UpdateEditorPreviewAsync();
    }

    private async Task UpdateEditorPreviewAsync()
    {
        if (!_isEditing || _previewClosed || !_previewInitialized || _previewRenderer is not { } renderer)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _editorPreviewSequence);
        try
        {
            SkinEditor.PruneDraftResources();
            var package = SkinEditor.Draft.BuildPackage();
            var options = GetPreviewOptions(package);
            SetPreviewStatus("正在更新草稿预览…");
            var resourcesChanged = _editorResourcesChanged;
            _editorResourcesChanged = false;
            var displayed = resourcesChanged
                ? await renderer.DisplayPackageAsync(package, options)
                : await renderer.UpdatePackageStylesAsync(package, options);
            if (sequence != Volatile.Read(ref _editorPreviewSequence) || !_isEditing)
            {
                return;
            }

            if (!displayed)
            {
                SetPreviewStatus("预览仍在更新…");
                return;
            }

            PreviewBrowser.Visibility = Visibility.Visible;
            SetPreviewStatus("预览草稿 · 点击组件可切换编辑目标");
            UpdateTransparencyControls();
        }
        catch (OperationCanceledException) when (
            sequence != Volatile.Read(ref _editorPreviewSequence) ||
            !_isEditing ||
            _previewClosed ||
            !ReferenceEquals(_previewRenderer, renderer))
        {
            return;
        }
        catch (OperationCanceledException)
        {
            SetPreviewStatus("预览更新已中止，请重试。", persistent: true);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException or InvalidOperationException)
        {
            SkinEditor.SetValidationError(exception.Message);
            SetPreviewStatus("草稿有需要修正的字段", persistent: true);
        }
    }

    private async void SkinEditor_SaveRequested(object? sender, EventArgs e) => await SaveEditorAsync(asCopy: false);
    private async void SkinEditor_SaveAsRequested(object? sender, EventArgs e) => await SaveEditorAsync(asCopy: true);

    private async Task<bool> SaveEditorAsync(bool asCopy)
    {
        if (!_isEditing || _isBusy)
        {
            return false;
        }

        try
        {
            if (asCopy && !SkinEditor.IsNew &&
                string.Equals(ReadDraftId(), _editOriginalId, StringComparison.OrdinalIgnoreCase))
            {
                SkinEditor.SetSkinId(CreateUniqueCopyId(ReadDraftId()));
            }

            SkinEditor.PruneDraftResources();
            var package = SkinEditor.Draft.BuildPackage();
            var isOriginalId = string.Equals(package.Id, _editOriginalId, StringComparison.OrdinalIgnoreCase);
            var exists = _store.ContainsId(package.Id);
            var replace = !asCopy && !SkinEditor.IsNew && isOriginalId;
            if (exists && !replace)
            {
                if (SkinEditor.IsNew || asCopy)
                {
                    SkinEditor.SetValidationError($"皮肤 ID“{package.Id}”已经存在，请修改 ID 后再保存。新建皮肤和副本不会替换已有内容。");
                    return false;
                }

                var answer = MessageBox.Show(
                    this,
                    $"皮肤 ID“{package.Id}”已经存在。确认后会替换现有皮肤。",
                    "替换现有皮肤？",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    return false;
                }

                replace = true;
            }

            SetBusy(true);
            await Task.Run(() => _store.Save(package, replace));
            SkinEditor.MarkSaved();
            _selectedPackage = package;
            ExitEditor(restoreSavedPreview: false);
            RefreshList(package.Id);
            if (!_previewClosed && _previewInitialized && _selectedPackage is { } savedPreview)
            {
                await RenderPreviewAsync(savedPreview);
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException or DuplicateSkinException)
        {
            SkinEditor.SetValidationError(exception.Message);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SkinEditor_CancelRequested(object? sender, EventArgs e)
    {
        if (!SkinEditor.IsDirty)
        {
            ExitEditor(restoreSavedPreview: true);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "当前草稿有未保存的修改。是否先保存？",
            "保留草稿修改？",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Yes)
        {
            await SaveEditorAsync(asCopy: false);
        }
        else if (answer == MessageBoxResult.No)
        {
            ExitEditor(restoreSavedPreview: true);
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose)
        {
            return;
        }

        _runtimeActionCancellation?.Cancel();
        e.Cancel = true;
        if (_closePreparationInProgress)
        {
            return;
        }

        _closePreparationInProgress = true;
        try
        {
            if (!await TryPrepareToCloseAsync("关闭管理器"))
            {
                return;
            }

            _allowWindowClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
        }
        finally
        {
            _closePreparationInProgress = false;
        }
    }

    internal async Task<bool> TryPrepareToCloseAsync(string action)
    {
        if (!_isEditing || !SkinEditor.IsDirty)
        {
            return true;
        }

        var prompt = $"当前制作器有未保存的修改。是否先保存再{action}？";
        var answer = _editorClosePrompt?.Invoke(prompt) ?? MessageBox.Show(
            this,
            prompt,
            "保留草稿修改？",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Yes)
        {
            return await SaveEditorAsync(asCopy: false);
        }

        if (answer == MessageBoxResult.No)
        {
            ExitEditor(restoreSavedPreview: false);
            return true;
        }

        return false;
    }

    private void UpdateCoordinator_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateCoordinator_PropertyChanged(sender, e)));
            return;
        }

        if (_updateCoordinator is not null)
        {
            UpdateUpdateEntryLabel();
        }
    }

    private void UpdateUpdateEntryLabel()
    {
        var fullLabel = _updateCoordinator?.UpdateEntryLabel ??
                        $"版本与更新 · {UpdateCoordinator.ReadCurrentVersionTag()}";
        UpdateEntryLabel.Text = fullLabel.StartsWith("版本与更新 · ", StringComparison.Ordinal)
            ? "版本与更新"
            : fullLabel;
        UpdateEntryLabel.ToolTip = fullLabel;
        UpdateEntryButton.ToolTip = fullLabel;
    }

    private void UpdateEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatesWindow is { IsVisible: true })
        {
            _updatesWindow.Activate();
            return;
        }

        _updatesWindow = new UpdatesWindow(_updateCoordinator, CanInstallUpdateNow)
        {
            Owner = this
        };
        _updatesWindow.InstallRequested += InstallUpdateAsync;
        _updatesWindow.Closed += (_, _) => _updatesWindow = null;
        _updatesWindow.Show();
    }

    private bool CanInstallUpdateNow() => !_isBusy && !_runtimeOperationInProgress &&
                                         !_closePreparationInProgress && !_allowWindowClose &&
                                         _updateCoordinator?.HasDownloadedUpdate == true;

    internal async Task<bool> InstallUpdateAsync()
    {
        var coordinator = _updateCoordinator;
        var downloaded = coordinator?.DownloadedUpdate;
        if (coordinator is null || downloaded is null || _installerLauncher is null)
        {
            coordinator?.ReportInstallFailure("没有可用的安装器或已验证的更新包。可以重新下载后重试。");
            return false;
        }

        if (_isBusy || _runtimeOperationInProgress || _closePreparationInProgress || _allowWindowClose)
        {
            coordinator.ReportInstallFailure("管理器正在保存或连接皮肤。操作完成后可以重试。");
            return false;
        }

        if (!await TryPrepareToCloseAsync("安装更新并关闭管理器"))
        {
            return false;
        }

        if (_isBusy || _runtimeOperationInProgress)
        {
            coordinator.ReportInstallFailure("管理器正在保存或连接皮肤。操作完成后可以重试。");
            return false;
        }

        SetBusy(true);
        try
        {
            await coordinator.ValidateAsync(downloaded);
            if (!_installerLauncher.Launch(downloaded.FilePath, AppContext.BaseDirectory))
            {
                coordinator.ReportInstallFailure("Windows 没有启动安装程序。可以重试安装。");
                return false;
            }

            if (_closeAfterSuccessfulInstall)
            {
                _allowWindowClose = true;
                Close();
            }
            return true;
        }
        catch (Exception exception)
        {
            coordinator.ReportInstallFailure($"{exception.Message} 可以重新下载后重试。");
            return false;
        }
        finally
        {
            if (IsVisible)
            {
                SetBusy(false);
            }
        }
    }

    private string ReadDraftId() =>
        (SkinEditor.Draft.Manifest["id"] as System.Text.Json.Nodes.JsonValue)?.GetValue<string>() ?? string.Empty;

    private void PreviewPowerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewClosed)
        {
            _ = OpenPreviewAsync();
        }
        else
        {
            ClosePreview();
        }
    }

    private void ClosePreview()
    {
        if (_previewClosed)
        {
            return;
        }

        _previewClosed = true;
        _previewWasActive = false;
        _previewNeedsRefresh = true;
        _previewInitialized = false;
        _previewInitializationFailed = false;
        _showingOriginal = false;
        OriginalImageSource.Source = null;
        OriginalImageLayer.Visibility = Visibility.Collapsed;
        OriginalButton.Content = "查看原图";
        PreviewBrowser.IsVisibleChanged -= PreviewBrowser_IsVisibleChanged;
        if (_previewRenderer is { } renderer)
        {
            renderer.CurrentResolvedSkinChanged -= PreviewRenderer_CurrentResolvedSkinChanged;
            renderer.ComponentSelected -= PreviewRenderer_ComponentSelected;
            renderer.BackgroundFocusChanged -= PreviewRenderer_BackgroundFocusChanged;
            renderer.Dispose();
            _previewRenderer = null;
        }

        UnregisterName("PreviewBrowser");
        PreviewBrowserHost.Children.Remove(PreviewBrowser);
        PreviewActiveHeader.Visibility = Visibility.Collapsed;
        PreviewClosedHeader.Visibility = Visibility.Visible;
        PreviewContainer.Visibility = Visibility.Collapsed;
        PreviewRow.MinHeight = 0;
        PreviewRow.Height = new GridLength(0);
        PreviewPowerButton.Content = "打开预览";
        PreviewPowerButton.ToolTip = "重新打开本地离线预览";
        SetPreviewStatus("本地预览已关闭");
    }

    private async Task OpenPreviewAsync()
    {
        if (!_previewClosed)
        {
            return;
        }

        _previewClosed = false;
        PreviewClosedHeader.Visibility = Visibility.Collapsed;
        PreviewActiveHeader.Visibility = Visibility.Visible;
        PreviewContainer.Visibility = Visibility.Visible;
        PreviewRow.MinHeight = 150;
        PreviewRow.Height = new GridLength(1, GridUnitType.Star);
        PreviewPowerButton.Content = "关闭预览";
        PreviewPowerButton.ToolTip = "释放本地预览浏览器内存";
        var browser = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        RegisterName("PreviewBrowser", browser);
        PreviewBrowserHost.Children.Add(browser);
        PreviewBrowser = browser;
        PreviewBrowser.IsVisibleChanged += PreviewBrowser_IsVisibleChanged;
        var renderer = new ThemePreviewRenderer(browser, _previewProfileDirectory, _basePackage);
        _previewRenderer = renderer;
        renderer.CurrentResolvedSkinChanged += PreviewRenderer_CurrentResolvedSkinChanged;
        renderer.ComponentSelected += PreviewRenderer_ComponentSelected;
        renderer.BackgroundFocusChanged += PreviewRenderer_BackgroundFocusChanged;
        try
        {
            SetPreviewStatus("正在打开本地预览…", persistent: true);
            await renderer.InitializeAsync();
            _previewInitialized = true;
            _previewInitializationFailed = false;
            FitPreviewBrowser();
            if (_isEditing)
            {
                _editorResourcesChanged = true;
                await UpdateEditorPreviewAsync();
                await renderer.SetComponentSelectionModeAsync(true);
                await renderer.SetBackgroundPositionModeAsync(SkinEditor.IsBackgroundFocusEditing);
            }
            else if (_selectedPackage is { } package)
            {
                await RenderPreviewAsync(package);
            }
            else
            {
                PreviewBrowser.Visibility = Visibility.Collapsed;
                SetPreviewStatus("选择皮肤以显示预览", persistent: true);
            }

            QueuePreviewActivityUpdate();
        }
        catch (Exception exception)
        {
            ShowPreviewError(exception);
        }
    }

    private void DisplayRuntimeStatus(
        RuntimeStatus status,
        string? fallbackMessage = null,
        bool preserveRuntimeResult = false,
        bool protectRuntimeResult = false)
    {
        _lastRuntimeStatus = status;
        UpdateRuntimeTargetSummary();
        var preservesError = preserveRuntimeResult && !string.IsNullOrWhiteSpace(_protectedRuntimeResultMessage);
        var viewStatus = preservesError && status.State != RuntimeStatusState.Stopped
            ? new RuntimeStatus(
                RuntimeStatusState.Error,
                status.SkinId,
                status.Endpoint,
                _protectedRuntimeResultMessage,
                status.IsDebugPortOpen,
                status.AppliedTargetCount,
                status.ConnectedInstance,
                status.CompatibilityResult)
            : status;
        var viewState = RuntimeConnectionViewState.From(viewStatus);
        RuntimeStatusIndicator.SetResourceReference(Ellipse.FillProperty, viewState.BrushResourceKey);
        RuntimeStatusText.SetResourceReference(TextBlock.ForegroundProperty, viewState.BrushResourceKey);
        RuntimeStatusText.Text = viewState.StatusLabel;
        var message = string.IsNullOrWhiteSpace(status.Message) ? fallbackMessage ?? viewState.Message : status.Message;
        if (protectRuntimeResult && status.State == RuntimeStatusState.Error)
        {
            _protectedRuntimeResultMessage = message;
        }
        else if (preservesError)
        {
            message = _protectedRuntimeResultMessage;
        }

        RuntimeStatusMessage.Text = message;
        RuntimeStatusMessage.ToolTip = message;
        RuntimeStatusText.ToolTip = message;
        UpdateRuntimeControls();
    }

    private void SetRuntimeError(string detail, string fallbackMessage)
    {
        DisplayRuntimeStatus(new RuntimeStatus(
            RuntimeStatusState.Error,
            _lastRuntimeStatus?.SkinId,
            _lastRuntimeStatus?.Endpoint,
            string.IsNullOrWhiteSpace(detail) ? fallbackMessage : detail,
            _lastRuntimeStatus?.IsDebugPortOpen),
            protectRuntimeResult: true);
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = sender as UIElement ?? ImportButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        var zipItem = new MenuItem { Header = "从 ZIP 包导入" };
        var folderItem = new MenuItem { Header = "从文件夹导入" };
        zipItem.Click += async (_, _) => await ImportZipAsync();
        folderItem.Click += async (_, _) => await ImportFolderAsync();
        menu.Items.Add(zipItem);
        menu.Items.Add(folderItem);
        menu.IsOpen = true;
    }

    private async Task ImportZipAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择皮肤 ZIP 包",
            Filter = "皮肤 ZIP 包 (*.zip)|*.zip",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await ImportPackageAsync(() => Task.Run(() => _store.ReadZip(dialog.FileName)));
    }

    private async Task ImportFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择皮肤文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await ImportPackageAsync(() => Task.Run(() => _store.ReadFolder(dialog.FolderName)));
    }

    private async Task ImportPackageAsync(Func<Task<SkinPackage>> readPackage)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var package = await readPackage();
            await SaveImportedPackageAsync(package);
        }
        catch (Exception exception)
        {
            ShowOperationError("导入失败", exception);
        }
        finally
        {
            SetBusy(false);
            UpdateLibraryWarning();
        }
    }

    private async Task<bool> SaveImportedPackageAsync(SkinPackage package, Window? promptOwner = null)
    {
        if (package.ImageBytes is { Length: > 0 } imageBytes)
        {
            try
            {
                _ = PreviewImageCropper.ReadDimensions(imageBytes);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException)
            {
                throw new InvalidDataException("背景图片无法由系统图像解码器读取。请使用有效的 PNG 或 JPG 文件后重试。", exception);
            }
        }

        var replace = false;
        if (_store.ContainsId(package.Id))
        {
            var prompt = $"皮肤 ID“{package.Id}”已经存在。确认后会用“{package.Name}”替换现有皮肤。";
            var answer = _importReplacePrompt?.Invoke(prompt) ?? MessageBox.Show(
                promptOwner ?? this,
                prompt,
                "替换现有皮肤？",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }

            replace = true;
        }

        await Task.Run(() => _store.Save(package, replace));
        RefreshList(package.Id);
        return true;
    }

    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreActionsButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = MoreActionsButton;
        menu.IsOpen = true;
    }

    private void MoreActionsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            WindowMotion.AttachBehaviors(menu);
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPackage is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出皮肤 ZIP 包",
            Filter = "皮肤 ZIP 包 (*.zip)|*.zip",
            FileName = $"{_selectedPackage.Id}.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
        _store.ExportZip(_selectedPackage.Id, dialog.FileName, overwriteExisting: true);
            MessageBox.Show(this, "皮肤包已导出。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowOperationError("导出失败", exception);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPackage is null)
        {
            return;
        }

        var package = _selectedPackage;
        var answer = MessageBox.Show(
            this,
            $"确定从皮肤库中删除“{package.Name}”吗？\n\n这不会影响原始导入文件。",
            "删除皮肤？",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _store.Delete(package.Id);
            RefreshList();
        }
        catch (Exception exception)
        {
            ShowOperationError("删除失败", exception);
        }
    }

    private void OriginalButton_Click(object sender, RoutedEventArgs e)
    {
        var transition = ++_originalTransitionSequence;
        if (!_showingOriginal)
        {
            if (_selectedPackage?.ImageBytes is not { Length: > 0 } bytes)
            {
                return;
            }

            try
            {
                OriginalImageSource.Source = PreviewImageCropper.Decode(bytes);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException)
            {
                ShowOperationError("查看原图失败", exception);
                return;
            }

            _showingOriginal = true;
            OriginalButton.Content = "返回聊天预览";
            if (!_previewClosed)
            {
                PreviewBrowser.Visibility = Visibility.Collapsed;
            }
            SetPreviewStatus(null);
            QueuePreviewActivityUpdate();
            OriginalImageLayer.BeginAnimation(OpacityProperty, null);
            OriginalImageLayer.Opacity = 0;
            OriginalImageLayer.Visibility = Visibility.Visible;
            AnimateOpacity(OriginalImageLayer, 1, 160);
            return;
        }

        _showingOriginal = false;
        OriginalButton.Content = "查看原图";
        if (_previewClosed)
        {
            OriginalImageLayer.Visibility = Visibility.Collapsed;
            OriginalImageSource.Source = null;
            SetPreviewStatus("本地预览已关闭");
            return;
        }

        if (_previewInitializationFailed)
        {
            PreviewBrowser.Visibility = Visibility.Collapsed;
            SetPreviewStatus("皮肤文件预览暂不可用。仍可继续管理皮肤。", persistent: true);
            FadeOriginalLayerOut(transition, null);
            return;
        }

        if (!_previewInitialized)
        {
            PreviewBrowser.Visibility = Visibility.Collapsed;
            SetPreviewStatus("正在加载皮肤文件预览…", persistent: true);
            FadeOriginalLayerOut(transition, _selectedPackage);
            return;
        }

        SetPreviewStatus("正在恢复聊天预览…");
        FadeOriginalLayerOut(transition, _selectedPackage);
    }

    private async void PreviewOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await UpdatePreviewFromOptionsAsync();
    }

    private async void PreviewOptions_ToggleChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, PreviewTransparencyFollowCheckBox) &&
            PreviewTransparencyInput is not null && PreviewTransparencyError is not null)
        {
            RestoreTransparencyInput();
        }

        UpdateTransparencyControls();
        await UpdatePreviewFromOptionsAsync();
    }

    private void PreviewTransparencyInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewTransparencyError is not null && PreviewTransparencyError.Visibility != Visibility.Collapsed)
        {
            PreviewTransparencyError.Visibility = Visibility.Collapsed;
        }
    }

    private async void PreviewTransparencyInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        await CommitTransparencyInputAsync();
    }

    private async void PreviewTransparencyInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitTransparencyInputAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            RestoreTransparencyInput();
        }
    }

    private async Task CommitTransparencyInputAsync()
    {
        if (_isBusy || PreviewTransparencyFollowCheckBox.IsChecked == true)
        {
            return;
        }

        var text = PreviewTransparencyInput.Text.Trim();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var transparency) ||
            transparency is < 0 or > 100)
        {
            PreviewTransparencyError.Text = "请输入 0 到 100 的整数。";
            PreviewTransparencyError.Visibility = Visibility.Visible;
            return;
        }

        var changed = _previewTransparency != transparency;
        _previewTransparency = transparency;
        PreviewTransparencyInput.Text = transparency.ToString(CultureInfo.InvariantCulture);
        PreviewTransparencyError.Visibility = Visibility.Collapsed;
        UpdateTransparencyControls();
        if (changed)
        {
            await UpdatePreviewFromOptionsAsync();
        }
    }

    private void RestoreTransparencyInput()
    {
        PreviewTransparencyInput.Text = _previewTransparency.ToString(CultureInfo.InvariantCulture);
        PreviewTransparencyError.Visibility = Visibility.Collapsed;
    }

    private async Task UpdatePreviewFromOptionsAsync()
    {
        if (_isEditing)
        {
            QueueEditorPreviewUpdate(resourcesChanged: false);
            return;
        }

        if (_selectedPackage is not null && _previewInitialized && !_showingOriginal &&
            !_previewClosed && OriginalImageLayer.Visibility != Visibility.Visible && !_isBusy)
        {
            _previewNeedsRefresh = true;
            if (IsPreviewActive)
            {
                await RenderPreviewAsync(_selectedPackage);
            }
        }
    }

    private void UpdateTransparencyControls()
    {
        if (PreviewTransparencyFollowCheckBox is null || PreviewTransparencyInput is null)
        {
            return;
        }

        var followsTheme = PreviewTransparencyFollowCheckBox.IsChecked == true;
        PreviewTransparencyFollowCheckBox.IsEnabled = !_isBusy && !_isEditing;
        if (_isEditing)
        {
            PreviewTransparencyInput.IsEnabled = false;
            if (PreviewTransparencyValue is not null)
            {
                PreviewTransparencyValue.Text = "编辑中使用草稿默认值";
            }

            if (PreviewTransparencyError is not null)
            {
                PreviewTransparencyError.Visibility = Visibility.Collapsed;
            }

            return;
        }

        PreviewTransparencyInput.IsEnabled = !_isBusy && !followsTheme;
        if (followsTheme)
        {
            var defaultTransparency = GetResolvedThemeTransparency();
            PreviewTransparencyInput.Text = defaultTransparency ?? string.Empty;
            if (PreviewTransparencyValue is not null)
            {
                PreviewTransparencyValue.Text = defaultTransparency is null ? "由皮肤决定" : "主题默认";
            }
        }
        else if (PreviewTransparencyValue is not null)
        {
            PreviewTransparencyValue.Text = string.Empty;
        }
    }

    private string? GetResolvedThemeTransparency()
    {
        if ((!_isEditing && _selectedPackage is null) || _previewRenderer?.CurrentResolvedSkin is not { } resolved ||
            !resolved.Tokens.TryGetValue("material.fillOpacity", out var fillOpacity) ||
            fillOpacity.Kind != SkinValueKind.Ratio || fillOpacity.Number is not { } alpha ||
            !double.IsFinite(alpha) || alpha is < 0 or > 1)
        {
            return null;
        }

        return ((1 - alpha) * 100).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private PreviewOptions GetPreviewOptions(SkinPackage package)
    {
        var selectedAppearance = SelectedPreviewValue(PreviewAppearanceBox, "skin");
        var appearance = selectedAppearance.Equals("skin", StringComparison.OrdinalIgnoreCase)
            ? package.Appearance.ToString().ToLowerInvariant()
            : selectedAppearance;
        if (appearance is not ("system" or "light" or "dark"))
        {
            appearance = "system";
        }

        return new PreviewOptions(
            Scene: SelectedPreviewValue(PreviewSceneBox, "home"),
            Appearance: appearance,
            SidebarVisible: PreviewSidebarCheckBox.IsChecked == true,
            UserTransparency: PreviewTransparencyFollowCheckBox.IsChecked == true
                || _isEditing
                ? null
                : _previewTransparency,
            ShowOriginalBase: PreviewOriginalBaseCheckBox.IsChecked == true);
    }

    private static string SelectedPreviewValue(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _updateCoordinator?.StartAutomatically();
        if (_hasLoaded)
        {
            await UpdatePreviewActivityAsync();
            return;
        }

        _hasLoaded = true;
        InitializeInteractionFeedback();
        RefreshList();
        if (_readRuntimeStatusOnLoad)
        {
            _runtimeStatusRefreshTimer.Start();
            _ = RefreshRuntimeStatusAsync();
        }
        if (_previewClosed)
        {
            return;
        }

        try
        {
            await _previewRenderer!.InitializeAsync();
            _previewInitialized = true;
            FitPreviewBrowser();
            if (_isEditing)
            {
                _editorResourcesChanged = true;
                await UpdateEditorPreviewAsync();
                await _previewRenderer!.SetComponentSelectionModeAsync(true);
                await _previewRenderer.SetBackgroundPositionModeAsync(SkinEditor.IsBackgroundFocusEditing);
            }
            else if (_selectedPackage is { } package)
            {
                await RenderPreviewAsync(package);
            }
            else
            {
                SetPreviewStatus("选择皮肤以显示预览", persistent: true);
            }

            await UpdatePreviewActivityAsync();
        }
        catch (Exception exception)
        {
            ShowPreviewError(exception);
        }
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (!_hasBeenActivated)
        {
            _hasBeenActivated = true;
            return;
        }

        if (_readRuntimeStatusOnLoad && _hasLoaded && !_runtimeOperationInProgress)
        {
            _ = RefreshRuntimeStatusAsync();
        }
    }

    private async Task RefreshRuntimeStatusAsync()
    {
        if (_windowClosed || !_readRuntimeStatusOnLoad || _runtimeStatusRefreshInProgress || _runtimeOperationInProgress)
        {
            return;
        }

        _runtimeStatusRefreshInProgress = true;
        var sequence = Volatile.Read(ref _runtimeStatusSequence);
        try
        {
            var status = await (_runtimeStatusReader?.Invoke(CancellationToken.None) ?? _runtimeController.GetStatusAsync());
            if (CanApplyRuntimeRefresh(sequence))
            {
                if (_codexInstanceService is not null)
                {
                    var instances = await _codexInstanceService.GetInstancesAsync();
                    if (!CanApplyRuntimeRefresh(sequence))
                    {
                        return;
                    }

                    if (_selectedConnectionInstance is { } selected)
                    {
                        var latestSelected = instances.FirstOrDefault(instance => SameProcessIdentity(instance.Identity, selected.Identity));
                        _selectedConnectionInstance = latestSelected;
                    }

                    if ((status.State is RuntimeStatusState.Connected or RuntimeStatusState.Applying) &&
                        status.ConnectedInstance is { } connectedIdentity)
                    {
                        var health = await _codexInstanceService.VerifyActiveConnectionAsync(
                            connectedIdentity,
                            status.Endpoint,
                            CancellationToken.None);
                        if (!CanApplyRuntimeRefresh(sequence))
                        {
                            return;
                        }

                        if (health.ShouldDisconnect)
                        {
                            if (_selectedConnectionInstance is { } selectedTarget &&
                                SameProcessIdentity(selectedTarget.Identity, connectedIdentity))
                            {
                                _selectedConnectionInstance = null;
                            }

                            status = new RuntimeStatus(
                                RuntimeStatusState.Stopped,
                                null,
                                null,
                                $"{health.Message} 可重新启动或使用“选择窗口”选择目标。",
                                null);
                        }
                    }
                }

                if (CanApplyRuntimeRefresh(sequence))
                {
                    DisplayRuntimeStatus(status, preserveRuntimeResult: true);
                }

                if (CanApplyRuntimeRefresh(sequence) &&
                    _readRuntimeStatusOnLoad && _runtimeStatusReader is null &&
                    !_startupCompatibilityReconciled &&
                    status.State == RuntimeStatusState.Connected &&
                    status.ConnectedInstance is not null &&
                    _compatibilitySettingsLoaded)
                {
                    _startupCompatibilityReconciled = true;
                    await ConfigureCompatibilityForCurrentTargetAsync(
                        _compatibilityOptions,
                        CancellationToken.None);
                }
            }
        }
        catch (Exception exception)
        {
            if (CanApplyRuntimeRefresh(sequence))
            {
                SetRuntimeError(exception.Message, "无法读取当前连接状态。");
            }
        }
        finally
        {
            _runtimeStatusRefreshInProgress = false;
        }
    }

    private bool CanApplyRuntimeRefresh(long sequence) =>
        !_windowClosed && !_runtimeOperationInProgress && sequence == Volatile.Read(ref _runtimeStatusSequence);

    private async void RuntimeStatusRefreshTimer_Tick(object? sender, EventArgs e) =>
        await RefreshRuntimeStatusAsync();

    private async Task RenderPreviewAsync(SkinPackage package)
    {
        if (!_previewInitialized || _showingOriginal)
        {
            return;
        }

        if (!IsPreviewActive)
        {
            _previewNeedsRefresh = true;
            return;
        }

        SetPreviewStatus("正在更新预览…");
        try
        {
            var renderer = _previewRenderer;
            if (renderer is null || _previewClosed)
            {
                _previewNeedsRefresh = true;
                return;
            }

            var displayed = await renderer.DisplayPackageAsync(package, GetPreviewOptions(package));
            if (!displayed || !ReferenceEquals(_selectedPackage, package) || _showingOriginal)
            {
                if (ReferenceEquals(_selectedPackage, package) && !_showingOriginal)
                {
                    _previewNeedsRefresh = true;
                }

                return;
            }

            _previewNeedsRefresh = false;
            UpdateTransparencyControls();
            UpdateColorItemsFromResolvedSkin(package);
            PreviewBrowser.Visibility = Visibility.Visible;
            SetPreviewStatus(null);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_selectedPackage, package))
            {
                ShowPreviewError(exception);
            }
        }
    }

    private void ShowPreviewError(Exception exception)
    {
        _previewInitializationFailed = true;
        _previewErrorMessage = exception.Message;
        PreviewBrowser.Visibility = Visibility.Collapsed;
        OriginalImageLayer.Visibility = Visibility.Collapsed;
        SetPreviewStatus(_showingOriginal ? null : "皮肤文件预览暂不可用。仍可继续管理皮肤。", persistent: true);
        PreviewStatus.ToolTip = exception.Message;
        QueuePreviewActivityUpdate();
    }

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitPreviewBrowser();
        QueuePreviewActivityUpdate();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e) => QueuePreviewActivityUpdate();

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => QueuePreviewActivityUpdate();

    private void PreviewBrowser_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => QueuePreviewActivityUpdate();

    private void DetailsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => QueuePreviewActivityUpdate();

    private bool IsPreviewActive =>
        !_previewClosed && _previewRenderer is not null && _previewInitialized && IsVisible && WindowState != WindowState.Minimized &&
        DetailsScroll.Visibility == Visibility.Visible && !_showingOriginal &&
        OriginalImageLayer.Visibility != Visibility.Visible && !_previewInitializationFailed &&
        IsPreviewWithinVisibleArea();

    private bool IsPreviewWithinVisibleArea()
    {
        if (PreviewViewport.ActualWidth <= 0 || PreviewViewport.ActualHeight <= 0 ||
            DetailsScroll.ActualWidth <= 0 || DetailsScroll.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var transform = PreviewViewport.TransformToAncestor(DetailsScroll);
            var previewBounds = transform.TransformBounds(new Rect(new Point(0, 0), PreviewViewport.RenderSize));
            var viewportBounds = new Rect(0, 0, DetailsScroll.ActualWidth, DetailsScroll.ActualHeight);
            return previewBounds.IntersectsWith(viewportBounds);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void QueuePreviewActivityUpdate()
    {
        if (!_previewInitialized || _previewClosed)
        {
            return;
        }

        _ = UpdatePreviewActivityAsync();
    }

    private async Task UpdatePreviewActivityAsync()
    {
        if (!_previewInitialized || _previewClosed)
        {
            return;
        }

        var active = IsPreviewActive;
        var wasActive = _previewWasActive;
        _previewWasActive = active;
        try
        {
        if (_previewRenderer is not { } renderer)
        {
            return;
        }

        await renderer.SetSuspendedAsync(!active);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (active && !wasActive && _previewNeedsRefresh && _isEditing)
        {
            await UpdateEditorPreviewAsync();
        }
        else if (active && !wasActive && _previewNeedsRefresh && _selectedPackage is { } package)
        {
            await RenderPreviewAsync(package);
        }
    }

    private void FitPreviewBrowser()
    {
        if (_previewClosed || _previewRenderer is null)
        {
            return;
        }

        var width = PreviewViewport.ActualWidth;
        var height = PreviewViewport.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (double.IsFinite(width) && double.IsFinite(height))
        {
            if (_previewClosed)
            {
                return;
            }

            PreviewBrowser.Width = width;
            PreviewBrowser.Height = height;
            if (_previewInitialized)
            {
                PreviewBrowser.ZoomFactor = 1;
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null)
        {
            SearchHint.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_interactionMotionReady && UiAnimationsEnabled)
        {
            AnimateOpacityPulse(SkinList, 0.9, 145);
        }

        var list = SkinList;
        var view = _skinsView;
        if (list is null || view is null)
        {
            return;
        }

        UpdateSortStatus();

        view.Refresh();
        if (list.SelectedItem is SkinCard selected && !view.Contains(selected))
        {
            list.SelectedItem = null;
        }

        if (list.SelectedItem is null && view.Cast<object>().FirstOrDefault() is SkinCard firstVisible)
        {
            list.SelectedItem = firstVisible;
        }

        if (list.SelectedItem is null)
        {
            ShowNoSelection(_skins.Count == 0 ? "皮肤库还是空的" : "没有匹配的皮肤",
                _skins.Count == 0
                    ? "导入一个皮肤包或皮肤文件夹，查看背景图、配色和聊天布局预览。"
                    : "试试其他名称或 ID，或者清空搜索内容。");
        }

        UpdateListEmptyState();
    }

    private void SkinList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems)
        {
            AnimateSkinListItem(SkinList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem, selected: false);
        }

        foreach (var item in e.AddedItems)
        {
            AnimateSkinListItem(SkinList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem, selected: true);
        }

        if (SkinList.SelectedItem is SkinCard card)
        {
            try
            {
                ShowPackage(_store.LoadSkin(card.Entry.Id));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                ShowOperationError("无法打开皮肤", exception);
                RefreshList();
            }
        }
        else if (_skins.Count == 0)
        {
            ShowNoSelection("皮肤库还是空的", "导入一个皮肤包或皮肤文件夹，查看背景图、配色和聊天布局预览。 ");
        }
        else
        {
            ShowNoSelection("没有匹配的皮肤", "试试其他名称或 ID，或者清空搜索内容。 ");
        }

        UpdateListEmptyState();
    }

    private void RefreshList(string? preferredId = null)
    {
        var previousId = preferredId ?? _selectedPackage?.Id;
        _imageReadWarning = false;
        _skins.Clear();
        var catalog = _store.GetCatalog();
        IReadOnlyList<string> orderedIds;
        try
        {
            _skinOrderPersistenceWarning = null;
            orderedIds = _skinOrderStore.Reconcile(catalog.Select(entry => entry.Id));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _skinOrderPersistenceWarning = "无法读取或保存皮肤顺序；当前按名称显示。";
            orderedIds = catalog.Select(entry => entry.Id).ToArray();
        }

        var order = orderedIds
            .Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.OrderBy(entry => order.TryGetValue(entry.Id, out var index) ? index : int.MaxValue))
        {
            BitmapSource? thumbnail = null;
            if (!string.IsNullOrWhiteSpace(entry.BackgroundImagePath))
            {
                try
                {
                    var imageBytes = _store.ReadCatalogImage(entry);
                    if (imageBytes is not { Length: > 0 })
                    {
                        throw new InvalidDataException("背景图片不存在。");
                    }

                    var dpi = VisualTreeHelper.GetDpi(this);
                    thumbnail = PreviewImageCropper.CreateThumbnail(
                        imageBytes,
                        SkinThumbnailWidth,
                        SkinThumbnailHeight,
                        dpi.DpiScaleX,
                        dpi.DpiScaleY,
                        entry.FocusX ?? 0.5,
                        entry.FocusY ?? 0.5);
                }
                catch (Exception exception) when (exception is NotSupportedException or IOException or ArgumentException or FormatException or InvalidOperationException)
                {
                    _imageReadWarning = true;
                }
            }

            _skins.Add(new SkinCard(entry, thumbnail));
        }

        _skinsView.Refresh();
        UpdateSortStatus();
        var selection = _skins.FirstOrDefault(item =>
            previousId is not null && string.Equals(item.Entry.Id, previousId, StringComparison.OrdinalIgnoreCase));
        if ((selection is null || !_skinsView.Contains(selection)) && _skinsView.Cast<object>().FirstOrDefault() is SkinCard firstVisible)
        {
            selection = firstVisible;
        }
        else if (selection is not null && !_skinsView.Contains(selection))
        {
            selection = null;
        }

        SkinList.SelectedItem = selection;
        UpdateListEmptyState();
        UpdateLibraryWarning();
        if (selection is null)
        {
            ShowNoSelection(_skins.Count == 0 ? "皮肤库还是空的" : "没有匹配的皮肤",
                _skins.Count == 0
                    ? "导入一个皮肤包或皮肤文件夹，查看背景图、配色和聊天布局预览。"
                    : "试试其他名称或 ID，或者清空搜索内容。");
        }
    }

    private bool MatchesSearch(object item)
    {
        if (item is not SkinCard card)
        {
            return false;
        }

        var search = SearchBox?.Text?.Trim();
        return string.IsNullOrEmpty(search) ||
               card.Entry.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               card.Entry.Id.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateListEmptyState()
    {
        if (ListEmptyState is null)
        {
            return;
        }

        var noSkins = _skins.Count == 0;
        var noMatches = !noSkins && !_skinsView.Cast<object>().Any();
        TransitionVisibility(SkinList, noSkins || noMatches ? Visibility.Collapsed : Visibility.Visible, 140);
        TransitionVisibility(ListEmptyState, noSkins || noMatches ? Visibility.Visible : Visibility.Collapsed, 150);
        ListEmptyTitle.Text = noSkins ? "暂无本地皮肤" : "没有搜索结果";
        ListEmptyText.Text = "试试其他名称或 ID，或者清空搜索内容。";
        ListEmptyText.Visibility = noMatches ? Visibility.Visible : Visibility.Collapsed;
        ListEmptyMarketButton.Visibility = Visibility.Collapsed;
        ListEmptyImportButton.Visibility = Visibility.Collapsed;
    }

    private void ShowPackage(SkinPackage package)
    {
        var preserveOriginalUntilPreviewReady = OriginalImageLayer.Visibility == Visibility.Visible;
        var originalTransition = ++_originalTransitionSequence;
        _selectedPackage = package;
        _previewNeedsRefresh = true;
        _showingOriginal = false;
        OriginalButton.Content = "查看原图";
        if (!preserveOriginalUntilPreviewReady)
        {
            OriginalImageLayer.Visibility = Visibility.Collapsed;
            OriginalImageLayer.Opacity = 1;
        }

        SetPreviewStatus(_previewInitializationFailed
            ? "皮肤文件预览暂不可用。仍可继续管理皮肤。"
            : _previewInitialized ? "正在更新预览…" : "正在加载皮肤文件预览…",
            persistent: _previewInitializationFailed);
        PreviewStatus.ToolTip = _previewErrorMessage;
        TransitionVisibility(DetailsEmptyState, Visibility.Collapsed, 135);
        TransitionVisibility(DetailsScroll, Visibility.Visible, 145);

        SelectedName.Text = package.Name;
        SelectedName.ToolTip = package.Name;
        SelectedId.Text = $"ID  {package.Id} · 皮肤版本 {package.Version}";
        SelectedAppearance.Text = $"适用外观：{TranslateAppearance(package.Appearance)} · 包格式：v{package.Definition.FormatVersion}";
        SelectedImageDimensions.ToolTip = package.ImagePath;
        SelectedImageDimensions.Text = "正在解析背景…";
        OriginalImageSource.Source = null;
        _selectedImageAvailable = false;
        OriginalButton.IsEnabled = false;

        ColorItems.ItemsSource = null;
        TransitionVisibility(ColorSection, Visibility.Collapsed, 130);
        AnimateOpacityPulse(SelectedSummary, 0.86, 155);
        UpdateRuntimeControls();
        QueuePreviewActivityUpdate();

        if (preserveOriginalUntilPreviewReady)
        {
            FadeOriginalLayerOut(originalTransition, package);
        }
        else if (_previewInitialized && !_previewInitializationFailed)
        {
            _ = RenderPreviewAsync(package);
        }
    }

    private void UpdateColorItemsFromResolvedSkin(SkinPackage package)
    {
        if (!ReferenceEquals(_selectedPackage, package) || _previewRenderer?.CurrentResolvedSkin is not { } resolved)
        {
            return;
        }

        var colors = resolved.Colors
            .OrderBy(pair => ColorOrder(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new ColorSwatch(pair.Key, TranslateColorName(pair.Key), pair.Value, MakeBrush(pair.Value)))
            .ToArray();
        ColorItems.ItemsSource = colors;
        TransitionVisibility(ColorSection, colors.Length > 0 ? Visibility.Visible : Visibility.Collapsed, 155);
        AnimateOpacityPulse(ColorItems, 0.88, 165);

        UpdateResolvedBackgroundDetails(resolved);
    }

    private void UpdateResolvedBackgroundDetails(ResolvedSkin resolved)
    {
        var background = resolved.Background;
        SelectedImageDimensions.ToolTip = background?.ImagePath;
        if (background?.ImageBytes is not { Length: > 0 } imageBytes)
        {
            OriginalImageSource.Source = null;
            _selectedImageAvailable = false;
            SelectedImageDimensions.Text = "无背景图";
            OriginalButton.IsEnabled = !_isBusy && _selectedImageAvailable;
            return;
        }

        try
        {
            var dimensions = PreviewImageCropper.ReadDimensions(imageBytes);
            _selectedImageAvailable = true;
            SelectedImageDimensions.Text = $"背景尺寸：{dimensions.Width} × {dimensions.Height} px";
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or ArgumentException or FormatException or InvalidOperationException)
        {
            OriginalImageSource.Source = null;
            _selectedImageAvailable = false;
            SelectedImageDimensions.Text = "背景图片无法读取";
            _imageReadWarning = true;
            UpdateLibraryWarning();
        }

        OriginalButton.IsEnabled = !_isBusy && _selectedImageAvailable;
    }

    private void InitializeInteractionFeedback()
    {
        if (_interactionMotionReady)
        {
            return;
        }

        _interactionMotionReady = true;
        foreach (var button in FindVisualChildren<Button>(this))
        {
            WindowMotion.PrepareButtonPressFeedback(button);
            button.MouseEnter += Button_MouseEnter;
            button.MouseLeave += Button_MouseLeave;
            button.PreviewMouseDown += Button_PreviewMouseDown;
            button.PreviewMouseUp += Button_PreviewMouseUp;
            button.IsEnabledChanged += Button_IsEnabledChanged;
            button.LostMouseCapture += Button_LostMouseCapture;
            button.ApplyTemplate();
            SetButtonSurface(button, hovered: button.IsMouseOver, pressed: button.IsPressed, animate: false);
        }

        foreach (var checkBox in FindVisualChildren<CheckBox>(this))
        {
            checkBox.Checked += CheckBox_Toggled;
            checkBox.Unchecked += CheckBox_Toggled;
            checkBox.ApplyTemplate();
        }

        foreach (var item in FindVisualChildren<ListBoxItem>(SkinList))
        {
            AnimateSkinListItem(item, item.IsSelected, animate: false);
        }

        _interactionMotionReady = true;
    }

    private void Button_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            SetButtonSurface(button, hovered: true, pressed: button.IsPressed);
        }
    }

    private void Button_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            SetButtonSurface(button, hovered: false, pressed: false);
            WindowMotion.AnimateButtonPressFeedback(button, pressed: false);
        }
    }

    private void Button_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button)
        {
            SetButtonSurface(button, hovered: button.IsMouseOver, pressed: true);
            WindowMotion.AnimateButtonPressFeedback(button, pressed: true);
        }
    }

    private void Button_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button)
        {
            SetButtonSurface(button, hovered: button.IsMouseOver, pressed: false);
            WindowMotion.AnimateButtonPressFeedback(button, pressed: false);
        }
    }

    private void Button_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Button button && e.NewValue is false)
        {
            SetButtonSurface(button, hovered: false, pressed: false);
            WindowMotion.AnimateButtonPressFeedback(button, pressed: false);
        }
    }

    private void Button_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            SetButtonSurface(button, hovered: button.IsMouseOver, pressed: false);
            WindowMotion.AnimateButtonPressFeedback(button, pressed: false);
        }
    }

    private void CheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            checkBox.ApplyTemplate();
            if (checkBox.Template.FindName("CheckSurface", checkBox) is Border surface)
            {
                AnimateOpacityPulse(surface, 0.76, 135);
            }
        }
    }

    private void SkinListItem_MouseEnter(object sender, MouseEventArgs e) =>
        AnimateSkinListItem(sender as ListBoxItem, selected: (sender as ListBoxItem)?.IsSelected == true);

    private void SkinListItem_MouseLeave(object sender, MouseEventArgs e) =>
        AnimateSkinListItem(sender as ListBoxItem, selected: (sender as ListBoxItem)?.IsSelected == true);

    private void AnimateSkinListItem(ListBoxItem? item, bool selected, bool animate = true)
    {
        if (item is null)
        {
            return;
        }

        item.ApplyTemplate();
        if (item.Template.FindName("RowSurface", item) is not Border surface)
        {
            return;
        }

        var targetColor = selected
            ? GetResourceColor("SelectedSurface", Color.FromRgb(63, 72, 22))
            : item.IsMouseOver
                ? GetResourceColor("HoverSurface", Color.FromRgb(36, 38, 40))
                : Colors.Transparent;
        AnimateBrushColor(surface, targetColor, selected ? 150 : 130, animate);
    }

    private void SetButtonSurface(Button button, bool hovered, bool pressed, bool animate = true)
    {
        if (!button.IsEnabled)
        {
            hovered = false;
            pressed = false;
        }

        button.ApplyTemplate();
        if (button.Template.FindName("ButtonSurface", button) is not Border surface)
        {
            return;
        }

        var baseColor = (button.Background as SolidColorBrush)?.Color ?? Colors.White;
        var lightSurface = GetPerceivedBrightness(baseColor) >= 128;
        var delta = pressed ? (lightSurface ? -14 : -15) : hovered ? (lightSurface ? -6 : 15) : 0;
        var targetColor = AdjustColor(baseColor, delta);
        AnimateBrushColor(surface, targetColor, pressed ? 90 : 140, animate);
    }

    private void AnimateBrushColor(Border surface, Color targetColor, int durationMilliseconds, bool animate = true)
    {
        var sourceBrush = surface.Background as SolidColorBrush;
        if (sourceBrush is null)
        {
            surface.Background = new SolidColorBrush(targetColor);
            return;
        }

        var currentColor = sourceBrush.Color;
        var brush = sourceBrush.CloneCurrentValue();
        surface.Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = currentColor;
        if (!animate || !UiAnimationsEnabled)
        {
            brush.Color = targetColor;
            return;
        }

        var animation = new ColorAnimation(currentColor, targetColor, TimeSpan.FromMilliseconds(durationMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = targetColor;
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateOpacityPulse(UIElement element, double startOpacity, int durationMilliseconds)
    {
        if (!_interactionMotionReady || !UiAnimationsEnabled)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        var currentOpacity = element.Opacity;
        var baseOpacity = (double)element.GetAnimationBaseValue(OpacityProperty);
        var wasAnimating = Math.Abs(currentOpacity - baseOpacity) > 0.001;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = wasAnimating ? currentOpacity : startOpacity;
        AnimateOpacity(element, 1, durationMilliseconds);
    }

    private void AnimateOpacity(UIElement element, double targetOpacity, int durationMilliseconds, Action? completed = null)
        => WindowMotion.AnimateOpacity(element, targetOpacity, durationMilliseconds, completed);

    private void SetPreviewStatus(string? text, bool persistent = false)
    {
        var sequence = ++_previewStatusSequence;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (PreviewStatus.Visibility != Visibility.Visible)
            {
                return;
            }

            if (!_interactionMotionReady || !UiAnimationsEnabled)
            {
                PreviewStatus.Visibility = Visibility.Collapsed;
                PreviewStatus.Opacity = 1;
                return;
            }

            AnimateOpacity(PreviewStatus, 0, 150, () =>
            {
                if (sequence == _previewStatusSequence)
                {
                    PreviewStatus.Visibility = Visibility.Collapsed;
                    PreviewStatus.Opacity = 1;
                }
            });
            return;
        }

        PreviewStatus.Text = text;
        PreviewStatus.Visibility = Visibility.Visible;
        if (persistent || !_interactionMotionReady || !UiAnimationsEnabled)
        {
            PreviewStatus.BeginAnimation(OpacityProperty, null);
            PreviewStatus.Opacity = 1;
            return;
        }

        PreviewStatus.BeginAnimation(OpacityProperty, null);
        PreviewStatus.Opacity = 0;
        AnimateOpacity(PreviewStatus, 1, 130);
    }

    private void FadeOriginalLayerOut(long transition, SkinPackage? package)
    {
        if (OriginalImageLayer.Visibility != Visibility.Visible)
        {
            ContinueAfterOriginalLayer(package);
            return;
        }

        AnimateOpacity(OriginalImageLayer, 0, 155, () =>
        {
            if (transition != _originalTransitionSequence || _showingOriginal)
            {
                return;
            }

            OriginalImageLayer.Visibility = Visibility.Collapsed;
            OriginalImageLayer.Opacity = 1;
            OriginalImageSource.Source = null;
            ContinueAfterOriginalLayer(package);
        });
    }

    private async void ContinueAfterOriginalLayer(SkinPackage? package)
    {
        if (_previewClosed)
        {
            SetPreviewStatus("本地预览已关闭");
            return;
        }

        if (_previewInitializationFailed)
        {
            PreviewBrowser.Visibility = Visibility.Collapsed;
            SetPreviewStatus("皮肤文件预览暂不可用。仍可继续管理皮肤。", persistent: true);
            await UpdatePreviewActivityAsync();
        }
        else if (package is not null && _previewInitialized)
        {
            await UpdatePreviewActivityAsync();
            if (ReferenceEquals(_selectedPackage, package) && !_previewNeedsRefresh && !_previewInitializationFailed)
            {
                PreviewBrowser.Visibility = Visibility.Visible;
                SetPreviewStatus(null);
            }
        }
        else if (package is null)
        {
            PreviewBrowser.Visibility = Visibility.Collapsed;
            SetPreviewStatus("选择皮肤以显示预览", persistent: true);
            await UpdatePreviewActivityAsync();
        }
        else
        {
            PreviewBrowser.Visibility = Visibility.Collapsed;
            SetPreviewStatus("正在加载皮肤文件预览…", persistent: true);
            await UpdatePreviewActivityAsync();
        }
    }

    private static bool UiAnimationsEnabled => WindowMotion.AnimationsEnabled;

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

    private static Color GetResourceColor(string key, Color fallback) =>
        Application.Current?.Resources[key] is SolidColorBrush brush ? brush.Color : fallback;

    private static int GetPerceivedBrightness(Color color) =>
        (int)Math.Round((color.R * 299 + color.G * 587 + color.B * 114) / 1000d);

    private static Color AdjustColor(Color color, int delta) => Color.FromRgb(
        (byte)Math.Clamp(color.R + delta, 0, 255),
        (byte)Math.Clamp(color.G + delta, 0, 255),
        (byte)Math.Clamp(color.B + delta, 0, 255));

    private void PreviewRenderer_CurrentResolvedSkinChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_isEditing)
            {
                UpdateTransparencyControls();
                return;
            }

            if (_selectedPackage is { } package)
            {
                UpdateColorItemsFromResolvedSkin(package);
            }

            UpdateTransparencyControls();
        });
    }

    private void PreviewRenderer_ComponentSelected(object? sender, PreviewComponentSelectedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_isEditing)
            {
                SkinEditor.SelectComponent(e.ComponentId);
            }
        });
    }

    private void ShowNoSelection(string title, string message)
    {
        _originalTransitionSequence++;
        _selectedPackage = null;
        _previewNeedsRefresh = false;
        _showingOriginal = false;
        UpdateTransparencyControls();
        _selectedImageAvailable = false;
        OriginalButton.Content = "查看原图";
        OriginalButton.IsEnabled = false;
        ColorItems.ItemsSource = null;
        TransitionVisibility(ColorSection, Visibility.Collapsed, 130);
        if (!_previewClosed)
        {
            PreviewBrowser.Visibility = Visibility.Collapsed;
        }
        OriginalImageLayer.Visibility = Visibility.Collapsed;
        OriginalImageLayer.Opacity = 1;
        SetPreviewStatus("选择皮肤以显示预览", persistent: true);
        PreviewStatus.ToolTip = null;
        DetailsEmptyTitle.Text = title;
        DetailsEmptyMessage.Text = message;
        TransitionVisibility(DetailsScroll, Visibility.Collapsed, 145);
        TransitionVisibility(DetailsEmptyState, Visibility.Visible, 165);
        UpdateRuntimeControls();
        QueuePreviewActivityUpdate();
    }

    private void TransitionVisibility(UIElement element, Visibility target, int durationMilliseconds)
    {
        var inProgress = _activeVisibilityTransitions.Contains(element);
        if (element.Visibility == target && !inProgress)
        {
            return;
        }

        var sequence = ++_visibilitySequence;
        _visibilityTransitions[element] = sequence;
        _activeVisibilityTransitions.Add(element);
        if (!_interactionMotionReady || !UiAnimationsEnabled)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Visibility = target;
            element.Opacity = 1;
            _activeVisibilityTransitions.Remove(element);
            return;
        }

        if (target == Visibility.Visible)
        {
            if (element.Visibility != Visibility.Visible)
            {
                element.Visibility = Visibility.Visible;
                element.Opacity = 0;
            }

            AnimateOpacity(element, 1, durationMilliseconds, () => CompleteVisibilityTransition(element, sequence, target));
            return;
        }

        if (element.Visibility != Visibility.Visible)
        {
            element.Visibility = target;
            element.Opacity = 1;
            _activeVisibilityTransitions.Remove(element);
            return;
        }

        AnimateOpacity(element, 0, durationMilliseconds, () => CompleteVisibilityTransition(element, sequence, target));
    }

    private void CompleteVisibilityTransition(UIElement element, long sequence, Visibility target)
    {
        if (!_visibilityTransitions.TryGetValue(element, out var currentSequence) || currentSequence != sequence)
        {
            return;
        }

        element.Visibility = target;
        element.Opacity = 1;
        _activeVisibilityTransitions.Remove(element);
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = !busy && !_isEditing;
        MarketEntryButton.IsEnabled = !busy && !_isEditing && !_runtimeOperationInProgress;
        NewSkinButton.IsEnabled = !busy && !_isEditing;
        EditSkinButton.IsEnabled = !busy && !_isEditing && _selectedPackage is not null;
        ListEmptyImportButton.IsEnabled = !busy;
        ListEmptyMarketButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy && !_isEditing;
        SkinList.IsEnabled = !busy && !_isEditing;
        SkinEditor.IsEnabled = !busy;
        UpdateSortStatus();
        OriginalButton.IsEnabled = !busy && _selectedImageAvailable;
        MoreActionsButton.IsEnabled = !busy && !_isEditing && _selectedPackage is not null;
        PreviewSceneBox.IsEnabled = !busy;
        PreviewAppearanceBox.IsEnabled = !busy;
        PreviewSidebarCheckBox.IsEnabled = !busy;
        PreviewOriginalBaseCheckBox.IsEnabled = !busy;
        PreviewTransparencyFollowCheckBox.IsEnabled = !busy && !_isEditing;
        UpdateTransparencyControls();
        UpdateRuntimeControls();
    }

    private void UpdateLibraryWarning()
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(_store.LastLoadWarning))
        {
            warnings.Add(_store.LastLoadWarning);
        }

        if (_imageReadWarning)
        {
            warnings.Add("有皮肤图片无法解码；原文件已保留，请重新导入有效 PNG 或 JPG。 ");
        }

        if (!string.IsNullOrWhiteSpace(_skinOrderPersistenceWarning))
        {
            warnings.Add(_skinOrderPersistenceWarning);
        }

        LibraryWarningText.Text = string.Join(" ", warnings);
        LibraryWarning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SolidColorBrush MakeBrush(string value, double opacity = 1)
    {
        if (!CssColorParser.TryParse(value, out var rgba))
        {
            throw new FormatException($"无法显示不受支持的 CSS 颜色“{value}”。");
        }

        var color = Color.FromArgb(rgba.Alpha, rgba.Red, rgba.Green, rgba.Blue);
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static string TranslateAppearance(SkinAppearance appearance) => appearance switch
    {
        SkinAppearance.Light => "浅色",
        SkinAppearance.Dark => "深色",
        SkinAppearance.System => "跟随系统",
        _ => appearance.ToString()
    };

    private static string TranslateColorName(string key) =>
        SkinDefinitionCatalog.TryGetToken(key, out var definition)
            ? definition.PaletteLabel ?? definition.Label
            : key.StartsWith("color.", StringComparison.OrdinalIgnoreCase) ? key[6..] : key;

    private static int ColorOrder(string key) =>
        SkinDefinitionCatalog.TryGetToken(key, out var definition) ? definition.PaletteOrder : int.MaxValue;

    private void ShowOperationError(string title, Exception exception)
    {
        var message = exception switch
        {
            InvalidDataException => exception.Message.Trim(),
            DuplicateSkinException => exception.Message.Trim(),
            UnauthorizedAccessException => "没有权限访问该文件或皮肤库。请检查目录权限后重试。",
            IOException => exception.Message.Trim(),
            _ => "操作未完成。请检查所选文件后重试。"
        };
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private sealed class SkinCard(SkinCatalogEntry entry, BitmapSource? thumbnail)
    {
        public SkinCatalogEntry Entry { get; } = entry;
        public BitmapSource? Thumbnail { get; } = thumbnail;
        public string AppearanceLabel => $"{TranslateAppearance(Entry.Appearance)} · v{Entry.Version}";
    }

    private sealed class ColorSwatch(string rawName, string displayName, string value, Brush brush)
    {
        public string RawName { get; } = rawName;
        public string DisplayName { get; } = displayName;
        public string Value { get; } = value;
        public Brush Brush { get; } = brush;
    }
}

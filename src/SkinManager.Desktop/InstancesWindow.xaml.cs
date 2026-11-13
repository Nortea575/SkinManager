using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SkinManager.Runtime;

namespace SkinManager.Desktop;

public partial class InstancesWindow : Window
{
    private readonly ICodexInstanceService _instanceService;
    private readonly CodexProcessIdentity? _connectedIdentity;
    private readonly Func<CodexInstance, bool> _confirmClose;
    private readonly Func<CodexInstance, bool> _confirmForceClose;
    private readonly ObservableCollection<InstanceRow> _rows = [];
    private readonly HashSet<CodexProcessIdentity> _closedIdentities = [];
    private CodexInstance? _forceEligibleInstance;
    private string _statusSource = string.Empty;
    private object?[] _statusArguments = [];
    private bool _statusIsError;
    private bool _statusIsLocalized = true;
    private bool _instanceListLoadFailed;
    private bool _isBusy;
    private bool _restoringSelection;

    public CodexInstance? SelectedForConnection { get; private set; }
    public CodexProcessIdentity? LastClosedIdentity { get; private set; }
    public IReadOnlySet<CodexProcessIdentity> ClosedIdentities => _closedIdentities;
    public IReadOnlyList<CodexInstance> Instances => _rows.Select(row => row.Instance).ToArray();

    public InstancesWindow(
        ICodexInstanceService instanceService,
        CodexProcessIdentity? connectedIdentity = null,
        Func<CodexInstance, bool>? confirmClose = null,
        Func<CodexInstance, bool>? confirmForceClose = null)
    {
        _instanceService = instanceService ?? throw new ArgumentNullException(nameof(instanceService));
        _connectedIdentity = connectedIdentity;
        _confirmClose = confirmClose ?? ConfirmClose;
        _confirmForceClose = confirmForceClose ?? ConfirmForceClose;
        InitializeComponent();
        Icon = App.ReadApplicationIcon();
        WindowMotion.Attach(this);
        InstanceList.ItemsSource = _rows;
        AppLocalization.LanguageChanged += Localization_LanguageChanged;
        Closed += (_, _) => AppLocalization.LanguageChanged -= Localization_LanguageChanged;
        Loaded += async (_, _) => await RefreshAsync();
        UpdateControls();
    }

    public async Task RefreshAsync()
    {
        if (_isBusy)
        {
            return;
        }

        var selectedIdentity = (InstanceList.SelectedItem as InstanceRow)?.Instance.Identity;
        _isBusy = true;
        SetStatus("正在刷新实例列表…");
        UpdateControls();
        try
        {
            var instances = await _instanceService.GetInstancesAsync();
            _instanceListLoadFailed = false;
            _restoringSelection = true;
            _rows.Clear();
            foreach (var instance in instances)
            {
                _rows.Add(new InstanceRow(instance, _connectedIdentity));
            }

            InstanceList.SelectedItem = selectedIdentity is null
                ? null
                : _rows.FirstOrDefault(row => row.Instance.Identity == selectedIdentity);
            _forceEligibleInstance = (InstanceList.SelectedItem as InstanceRow)?.Instance is { PendingExitSnapshot.CanAttemptForce: true } pending
                ? pending
                : null;
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Count == 0)
            {
                _instanceListLoadFailed = false;
                EmptyTitle.Text = AppLocalization.Get("没有找到 Codex 主实例");
                EmptyMessage.Text = AppLocalization.Get("点击刷新重新扫描。这里只显示 Codex 主窗口实例。");
                SetStatus("未找到 Codex 主实例。");
            }
            else
            {
                SetStatus("已找到 {0} 个 Codex 主实例。", arguments: [_rows.Count]);
            }
        }
        catch (Exception exception)
        {
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Count == 0)
            {
                _instanceListLoadFailed = true;
                EmptyTitle.Text = AppLocalization.Get("无法读取实例列表");
                EmptyMessage.Text = AppLocalization.Get("请检查本机 WMI 查询权限后重试。");
            }
            SetStatusRaw(exception.Message, isError: true);
        }
        finally
        {
            _restoringSelection = false;
            _isBusy = false;
            UpdateControls();
        }
    }

    private void InstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_restoringSelection &&
            GetSelectedInstance() is { PendingExitSnapshot.CanAttemptForce: true } selected)
        {
            _forceEligibleInstance = selected;
        }
        else if (!_restoringSelection)
        {
            _forceEligibleInstance = null;
        }

        if (!_restoringSelection && GetSelectedInstance() is { } selectedInstance &&
            GetConnectionIssue(selectedInstance) is { } connectionIssue)
        {
            SetStatusRaw(connectionIssue, isError: true);
        }
        else if (!_restoringSelection && GetSelectedInstance() is { } connectableInstance)
        {
            SetStatus("已核验调试连接端点：{0}。可选择该实例。 ",
                arguments: [connectableInstance.DebugEndpoint]);
        }

        UpdateControls();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void LocateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || GetSelectedInstance() is not { } instance)
        {
            return;
        }

        _isBusy = true;
        SetStatus("正在定位 PID {0}…", arguments: [instance.Identity.ProcessId]);
        UpdateControls();
        try
        {
            var result = await _instanceService.LocateAsync(instance);
            switch (result)
            {
                case CodexLocateResult.Located:
                    SetStatus("已定位 {0}。", arguments: [GetInstanceSummary(instance)]);
                    break;
                case CodexLocateResult.NoWindow:
                    SetStatus("{0} 当前没有可见窗口。请刷新后重试。", arguments: [GetInstanceSummary(instance)]);
                    break;
                case CodexLocateResult.ForegroundDenied:
                    SetStatus("已找到 {0}，但 Windows 未允许切换前台。", arguments: [GetInstanceSummary(instance)]);
                    break;
                default:
                    SetStatus("无法定位 {0}。", isError: true, arguments: [GetInstanceSummary(instance)]);
                    break;
            }
        }
        catch (InvalidOperationException exception)
        {
            var message = exception.Message;
            SetStatusRaw(exception.Message, isError: true);
            await RefreshAfterActionAsync();
            SetStatus("{0} 列表已刷新。", isError: true, arguments: [message]);
        }
        catch (Exception exception)
        {
            SetStatus("定位失败：{0}", isError: true, arguments: [exception.Message]);
        }
        finally
        {
            _isBusy = false;
            UpdateControls();
        }
    }

    private async void UseInstanceButton_Click(object sender, RoutedEventArgs e) => await UseSelectedInstanceAsync();

    private async Task UseSelectedInstanceAsync()
    {
        if (_isBusy || GetSelectedInstance() is not { } selected)
        {
            return;
        }

        _isBusy = true;
        SetStatus("正在核验 PID {0} 的调试端口…", arguments: [selected.Identity.ProcessId]);
        UpdateControls();
        int? refreshNoticeProcessId = null;
        try
        {
            var instances = await _instanceService.GetInstancesAsync();
            var latest = instances.FirstOrDefault(instance => SameIdentity(instance.Identity, selected.Identity));
            if (latest is null)
            {
                refreshNoticeProcessId = selected.Identity.ProcessId;
            }
            else
            {
                UpdateInstance(latest);
                if (GetConnectionIssue(latest) is { } connectionIssue)
                {
                    SetStatusRaw(connectionIssue, isError: true);
                }
                else
                {
                    SelectedForConnection = latest;
                    try
                    {
                        DialogResult = true;
                    }
                    catch (InvalidOperationException)
                    {
                        Close();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            SetStatus("无法核验所选实例：{0}", isError: true, arguments: [exception.Message]);
        }
        finally
        {
            _isBusy = false;
            UpdateControls();
        }

        if (refreshNoticeProcessId is { } processId)
        {
            await RefreshAsync();
            SetStatus("所选实例 PID {0} 已退出。正在刷新列表…", isError: true, arguments: [processId]);
        }
    }

    private async void CloseInstanceButton_Click(object sender, RoutedEventArgs e) => await CloseSelectedAsync(force: false);

    private async void ForceCloseButton_Click(object sender, RoutedEventArgs e) => await CloseSelectedAsync(force: true);

    private async Task CloseSelectedAsync(bool force)
    {
        if (_isBusy)
        {
            return;
        }

        var instance = force
            ? _forceEligibleInstance is { } eligible && IsSelectedIdentity(eligible.Identity) ? eligible : null
            : GetSelectedInstance();
        if (instance is null)
        {
            _forceEligibleInstance = null;
            ForceCloseButton.Visibility = Visibility.Collapsed;
            UpdateControls();
            return;
        }

        if (force ? !_confirmForceClose(instance) : !_confirmClose(instance))
        {
            SetStatus("已取消退出请求。");
            return;
        }

        _isBusy = true;
            SetStatus(force ? "正在按已核验的进程归属处理 PID {0}…" : "正在请求退出 PID {0}…",
                arguments: [instance.Identity.ProcessId]);
        UpdateControls();
        var shouldRefresh = false;
        string? statusAfterRefresh = null;
        var statusAfterRefreshIsError = false;
        try
        {
            var operation = await _instanceService.CloseAsync(instance, force);
            var result = operation.Outcome;
            UpdateInstance(operation.Instance);
            switch (result)
            {
                case CodexCloseResult.Closed:
                    LastClosedIdentity = operation.Instance.Identity;
                    _closedIdentities.Add(operation.Instance.Identity);
                    _forceEligibleInstance = null;
                    ForceCloseButton.Visibility = Visibility.Collapsed;
                    statusAfterRefresh = operation.Message;
                    SetStatusRaw(statusAfterRefresh);
                    shouldRefresh = true;
                    break;
                case CodexCloseResult.NativeQuitUnavailable:
                case CodexCloseResult.StillRunning:
                case CodexCloseResult.Incomplete:
                    _forceEligibleInstance = operation.Instance.PendingExitSnapshot?.CanAttemptForce == true
                        ? operation.Instance
                        : null;
                    ForceCloseButton.Visibility = _forceEligibleInstance is null ? Visibility.Collapsed : Visibility.Visible;
                    SetStatusRaw(operation.Message, isError: result == CodexCloseResult.Incomplete && _forceEligibleInstance is null);
                    break;
                case CodexCloseResult.IdentityChanged:
                    _forceEligibleInstance = null;
                    ForceCloseButton.Visibility = Visibility.Collapsed;
                    statusAfterRefresh = operation.Message;
                    SetStatusRaw(statusAfterRefresh);
                    shouldRefresh = true;
                    break;
                default:
                    SetStatus("退出请求没有返回可识别的结果。", isError: true);
                    break;
            }
        }
        catch (InvalidOperationException exception)
        {
            _forceEligibleInstance = null;
            ForceCloseButton.Visibility = Visibility.Collapsed;
            statusAfterRefresh = exception.Message;
            statusAfterRefreshIsError = true;
            SetStatusRaw(statusAfterRefresh, isError: true);
            shouldRefresh = true;
        }
        catch (Exception exception)
        {
            SetStatus("退出失败：{0}", isError: true, arguments: [exception.Message]);
        }
        finally
        {
            _isBusy = false;
            UpdateControls();
        }

        if (shouldRefresh)
        {
            await RefreshAsync();
            if (statusAfterRefresh is not null)
            {
                SetStatusRaw(statusAfterRefresh, statusAfterRefreshIsError);
            }
        }
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshAfterActionAsync()
    {
        _isBusy = false;
        await RefreshAsync();
        _isBusy = true;
    }

    private CodexInstance? GetSelectedInstance() => (InstanceList.SelectedItem as InstanceRow)?.Instance;

    private void UpdateInstance(CodexInstance instance)
    {
        var row = _rows.FirstOrDefault(candidate => candidate.Instance.Identity == instance.Identity);
        if (row is null)
            return;
        var index = _rows.IndexOf(row);
        _rows[index] = new InstanceRow(instance, _connectedIdentity);
        InstanceList.SelectedItem = _rows[index];
    }

    private bool IsSelectedIdentity(CodexProcessIdentity identity) =>
        (InstanceList.SelectedItem as InstanceRow)?.Instance.Identity == identity;

    private void UpdateControls()
    {
        if (InstanceList is null)
        {
            return;
        }

        var selected = GetSelectedInstance();
        RefreshButton.IsEnabled = !_isBusy;
        LocateButton.IsEnabled = !_isBusy && selected is { IsExitResidual: false };
        UseInstanceButton.IsEnabled = !_isBusy && IsConnectableTarget(selected);
        CloseInstanceButton.IsEnabled = !_isBusy && selected is { IsExitResidual: false };
        ForceCloseButton.IsEnabled = !_isBusy && selected?.PendingExitSnapshot?.CanAttemptForce == true &&
            _forceEligibleInstance is { } eligible && selected.Identity == eligible.Identity;
        if (!ForceCloseButton.IsEnabled)
        {
            ForceCloseButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ForceCloseButton.Visibility = Visibility.Visible;
        }
    }

    private void SetStatus(string source, bool isError = false, object?[]? arguments = null)
    {
        _statusSource = source;
        _statusArguments = arguments ?? [];
        _statusIsError = isError;
        _statusIsLocalized = true;
        RenderStatus();
    }

    private void SetStatusRaw(string message, bool isError = false)
    {
        _statusSource = message;
        _statusArguments = [];
        _statusIsError = isError;
        _statusIsLocalized = false;
        RenderStatus();
    }

    private void RenderStatus()
    {
        var message = !_statusIsLocalized
            ? _statusSource
            : _statusArguments.Length == 0
                ? AppLocalization.Get(_statusSource)
                : AppLocalization.Format(_statusSource, _statusArguments);
        ActionStatus.Text = message;
        ActionStatus.ToolTip = message;
        ActionStatus.SetResourceReference(TextBlock.ForegroundProperty, _statusIsError ? "ErrorText" : "SecondaryText");
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => Localization_LanguageChanged(sender, e));
            return;
        }

        var selectedIdentity = (InstanceList.SelectedItem as InstanceRow)?.Instance.Identity;
        _restoringSelection = true;
        var currentInstances = _rows.Select(row => row.Instance).ToArray();
        _rows.Clear();
        foreach (var instance in currentInstances)
        {
            _rows.Add(new InstanceRow(instance, _connectedIdentity));
        }
        InstanceList.SelectedItem = selectedIdentity is null
            ? null
            : _rows.FirstOrDefault(row => row.Instance.Identity == selectedIdentity);
        _restoringSelection = false;

        if (_rows.Count == 0)
        {
            EmptyTitle.Text = AppLocalization.Get(_instanceListLoadFailed ? "无法读取实例列表" : "没有找到 Codex 主实例");
            EmptyMessage.Text = AppLocalization.Get(_instanceListLoadFailed
                ? "请检查本机 WMI 查询权限后重试。"
                : "点击刷新重新扫描。这里只显示 Codex 主窗口实例。");
        }

        RenderStatus();
        UpdateControls();
    }

    private static string GetInstanceSummary(CodexInstance instance)
    {
        var kind = AppLocalization.Get(instance.Kind == CodexInstanceKind.Debug ? "调试版" : "普通版");
        var title = instance.IsExitResidual ? AppLocalization.Get("后台残留") :
            string.IsNullOrWhiteSpace(instance.WindowTitle) ? AppLocalization.Get("无窗口标题") : instance.WindowTitle;
        return AppLocalization.Format("{0} PID {1} · {2}", kind,
            instance.Identity.ProcessId.ToString(AppLocalization.Culture), title);
    }

    private static bool IsConnectableTarget(CodexInstance? instance) =>
        instance is { CanConnect: true, Kind: CodexInstanceKind.Debug, IsExitResidual: false, DebugPort: >= 1 and <= 65535 } &&
        !string.IsNullOrWhiteSpace(instance.DebugEndpoint);

    private static string? GetConnectionIssue(CodexInstance instance)
    {
        if (instance.IsExitResidual)
        {
            return AppLocalization.Format(
                "调试实例 PID {0} 的主进程已退出，但仍有后台进程残留；不能把它用作连接目标。",
                instance.Identity.ProcessId);
        }

        if (!string.IsNullOrWhiteSpace(instance.ConnectionMessage))
        {
            return instance.ConnectionMessage;
        }

        if (instance.Kind != CodexInstanceKind.Debug)
        {
            return AppLocalization.Format("PID {0} 是普通版 Codex，没有调试端口，不能连接。", instance.Identity.ProcessId);
        }

        if (instance.DebugPort is not int port || port is < 1 or > 65535)
        {
            return AppLocalization.Format("调试实例 PID {0} 没有有效端口。请刷新后检查启动参数。", instance.Identity.ProcessId);
        }

        if (instance.CanConnect && string.IsNullOrWhiteSpace(instance.DebugEndpoint))
        {
            return AppLocalization.Format("调试实例 PID {0} 的监听端口已响应，但没有经过身份核验的连接 Endpoint。请刷新实例列表后重试。", instance.Identity.ProcessId);
        }

        return instance.CanConnect
            ? null
            : AppLocalization.Format("调试实例 PID {0} 声明了端口 {1}，但尚未核验为此进程拥有。可能端口未就绪或被其他进程占用；刷新后可重试。",
                instance.Identity.ProcessId, port);
    }

    private static bool SameIdentity(CodexProcessIdentity left, CodexProcessIdentity right) =>
        left.ProcessId == right.ProcessId &&
        left.StartedAtUtc.UtcDateTime == right.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(left.ExecutablePath), Path.GetFullPath(right.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private bool ConfirmClose(CodexInstance instance)
    {
        var message = AppLocalization.Format("确认请求以下实例正常退出？\n\n{0}\n\n将请求 Codex 正常退出；如应用需要确认，请先处理提示。不会自动强制结束。",
            GetInstanceSummary(instance));
        return MessageBox.Show(this, message, AppLocalization.Get("退出 Codex 实例"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool ConfirmForceClose(CodexInstance instance)
    {
        var count = instance.PendingExitSnapshot?.Processes.Count ?? 0;
        var unverifiedCount = instance.PendingExitSnapshot?.UnverifiedProcessIds.Count ?? 0;
        var message = AppLocalization.Format("确认强制结束以下实例及后台进程？\n\n{0}\n\n本次记录有 {1} 个可核验进程，另有 {2} 个身份未核验的 PID。仅处理所选实例，排除其他 Codex 实例和管理器；未核验的 PID 不会结束，若仍存活，结果会显示未完成。此操作可能丢失未保存内容。",
            GetInstanceSummary(instance), count, unverifiedCount);
        return MessageBox.Show(this, message, AppLocalization.Get("强制结束 Codex 实例"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private sealed class InstanceRow(CodexInstance instance, CodexProcessIdentity? connectedIdentity)
    {
        public CodexInstance Instance { get; } = instance;
        public string WindowTitle => Instance.IsExitResidual
            ? AppLocalization.Get("（主进程已退出 · 后台残留）")
            : string.IsNullOrWhiteSpace(Instance.WindowTitle) ? AppLocalization.Get("（无窗口标题）") : Instance.WindowTitle;
        public string ProcessIdText => Instance.Identity.ProcessId.ToString(AppLocalization.Culture);
        public string KindText => AppLocalization.Get(Instance.Kind == CodexInstanceKind.Debug ? "调试版" : "普通版");
        public string PortText => Instance.DebugPort is int port ? port.ToString(AppLocalization.Culture) : "—";
        public string ConnectionText => !IsConnectableTarget(Instance)
            ? AppLocalization.Get("不可连接")
            : Instance.Identity == connectedIdentity ? AppLocalization.Get("当前连接")
            : IsIpv6Endpoint(Instance.DebugEndpoint) ? AppLocalization.Get("IPv6 · 可连接") : AppLocalization.Get("可连接");
        public string ConnectionDetail => GetConnectionIssue(Instance) ??
            AppLocalization.Format("已核验调试连接端点：{0}。可作为连接目标。", Instance.DebugEndpoint);

        private static bool IsIpv6Endpoint(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var endpoint) && endpoint.HostNameType == UriHostNameType.IPv6;
    }
}

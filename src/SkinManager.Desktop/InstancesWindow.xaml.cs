using System.Collections.ObjectModel;
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
                EmptyTitle.Text = "没有找到 Codex 主实例";
                EmptyMessage.Text = "点击刷新重新扫描。这里只显示 Codex 主窗口实例。";
                SetStatus("未找到 Codex 主实例。");
            }
            else
            {
                SetStatus($"已找到 {_rows.Count} 个 Codex 主实例。");
            }
        }
        catch (Exception exception)
        {
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Count == 0)
            {
                EmptyTitle.Text = "无法读取实例列表";
                EmptyMessage.Text = "请检查本机 WMI 查询权限后重试。";
            }
            SetStatus(exception.Message, isError: true);
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
        SetStatus($"正在定位 PID {instance.Identity.ProcessId}…");
        UpdateControls();
        try
        {
            var result = await _instanceService.LocateAsync(instance);
            switch (result)
            {
                case CodexLocateResult.Located:
                    SetStatus($"已定位 {GetInstanceSummary(instance)}。");
                    break;
                case CodexLocateResult.NoWindow:
                    SetStatus($"{GetInstanceSummary(instance)} 当前没有可见窗口。请刷新后重试。");
                    break;
                case CodexLocateResult.ForegroundDenied:
                    SetStatus($"已找到 {GetInstanceSummary(instance)}，但 Windows 未允许切换前台。");
                    break;
                default:
                    SetStatus($"无法定位 {GetInstanceSummary(instance)}。", isError: true);
                    break;
            }
        }
        catch (InvalidOperationException exception)
        {
            var message = exception.Message;
            SetStatus(exception.Message, isError: true);
            await RefreshAfterActionAsync();
            SetStatus($"{message} 列表已刷新。", isError: true);
        }
        catch (Exception exception)
        {
            SetStatus($"定位失败：{exception.Message}", isError: true);
        }
        finally
        {
            _isBusy = false;
            UpdateControls();
        }
    }

    private void UseInstanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || GetSelectedInstance() is not { CanConnect: true, DebugPort: not null } instance)
        {
            return;
        }

        SelectedForConnection = instance;
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
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
        SetStatus(force ? $"正在按已核验的进程归属处理 PID {instance.Identity.ProcessId}…" : $"正在请求退出 PID {instance.Identity.ProcessId}…");
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
                    SetStatus(statusAfterRefresh);
                    shouldRefresh = true;
                    break;
                case CodexCloseResult.NativeQuitUnavailable:
                case CodexCloseResult.StillRunning:
                case CodexCloseResult.Incomplete:
                    _forceEligibleInstance = operation.Instance.PendingExitSnapshot?.CanAttemptForce == true
                        ? operation.Instance
                        : null;
                    ForceCloseButton.Visibility = _forceEligibleInstance is null ? Visibility.Collapsed : Visibility.Visible;
                    SetStatus(operation.Message, isError: result == CodexCloseResult.Incomplete && _forceEligibleInstance is null);
                    break;
                case CodexCloseResult.IdentityChanged:
                    _forceEligibleInstance = null;
                    ForceCloseButton.Visibility = Visibility.Collapsed;
                    statusAfterRefresh = operation.Message;
                    SetStatus(statusAfterRefresh);
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
            SetStatus(statusAfterRefresh, isError: true);
            shouldRefresh = true;
        }
        catch (Exception exception)
        {
            SetStatus($"退出失败：{exception.Message}", isError: true);
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
                SetStatus(statusAfterRefresh, statusAfterRefreshIsError);
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
        UseInstanceButton.IsEnabled = !_isBusy && selected is { IsExitResidual: false, CanConnect: true, DebugPort: not null };
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

    private void SetStatus(string message, bool isError = false)
    {
        ActionStatus.Text = message;
        ActionStatus.ToolTip = message;
        ActionStatus.Foreground = isError
            ? (Brush)FindResource("ErrorText")
            : (Brush)FindResource("SecondaryText");
    }

    private static string GetInstanceSummary(CodexInstance instance)
    {
        var kind = instance.Kind == CodexInstanceKind.Debug ? "调试版" : "普通版";
        var title = instance.IsExitResidual ? "后台残留" :
            string.IsNullOrWhiteSpace(instance.WindowTitle) ? "无窗口标题" : instance.WindowTitle;
        return $"{kind} PID {instance.Identity.ProcessId} · {title}";
    }

    private bool ConfirmClose(CodexInstance instance)
    {
        var message = $"确认请求以下实例正常退出？\n\n{GetInstanceSummary(instance)}\n\n将请求 Codex 正常退出；如应用需要确认，请先处理提示。不会自动强制结束。";
        return MessageBox.Show(this, message, "退出 Codex 实例", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool ConfirmForceClose(CodexInstance instance)
    {
        var count = instance.PendingExitSnapshot?.Processes.Count ?? 0;
        var unverifiedCount = instance.PendingExitSnapshot?.UnverifiedProcessIds.Count ?? 0;
        var message = $"确认强制结束以下实例及后台进程？\n\n{GetInstanceSummary(instance)}\n\n本次记录有 {count} 个可核验进程，另有 {unverifiedCount} 个身份未核验的 PID。仅处理所选实例，排除其他 Codex 实例和管理器；未核验的 PID 不会结束，若仍存活，结果会显示未完成。此操作可能丢失未保存内容。";
        return MessageBox.Show(this, message, "强制结束 Codex 实例", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private sealed class InstanceRow(CodexInstance instance, CodexProcessIdentity? connectedIdentity)
    {
        public CodexInstance Instance { get; } = instance;
        public string WindowTitle => Instance.IsExitResidual
            ? "（主进程已退出 · 后台残留）"
            : string.IsNullOrWhiteSpace(Instance.WindowTitle) ? "（无窗口标题）" : Instance.WindowTitle;
        public int ProcessId => Instance.Identity.ProcessId;
        public string KindText => Instance.Kind == CodexInstanceKind.Debug ? "调试版" : "普通版";
        public string PortText => Instance.DebugPort is int port ? port.ToString() : "—";
        public string ConnectionText => Instance.Identity == connectedIdentity ? "当前连接" : "—";
    }
}

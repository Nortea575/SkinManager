using System.Globalization;
using System.IO;
using SkinManager.Runtime;

namespace SkinManager.Desktop;

internal sealed class RuntimeApplyWorkflow
{
    private readonly Func<DebugClientLaunchOptions, CancellationToken, Task<RuntimeStatus>> _launchDebugClient;
    private readonly Func<RuntimeApplyRequest, CancellationToken, Task<RuntimeStatus>> _apply;
    private readonly ICodexInstanceService? _instances;
    private readonly TimeSpan _readinessTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly Action<RuntimeStatus>? _progress;
    private readonly Func<CodexInstance, CancellationToken, Task<bool>>? _confirmDebugModeSwitch;

    internal RuntimeApplyWorkflow(
        Func<DebugClientLaunchOptions, CancellationToken, Task<RuntimeStatus>> launchDebugClient,
        Func<RuntimeApplyRequest, CancellationToken, Task<RuntimeStatus>> apply,
        ICodexInstanceService? instances,
        TimeSpan? readinessTimeout = null,
        TimeSpan? pollInterval = null,
        Action<RuntimeStatus>? progress = null,
        Func<CodexInstance, CancellationToken, Task<bool>>? confirmDebugModeSwitch = null)
    {
        _launchDebugClient = launchDebugClient;
        _apply = apply;
        _instances = instances;
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(45);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(900);
        _progress = progress;
        _confirmDebugModeSwitch = confirmDebugModeSwitch;
    }

    internal async Task<RuntimeStatus> StartOrApplyAsync(
        RuntimeApplyRequest request,
        int port,
        CodexInstance? selectedTarget,
        CodexProcessIdentity? activeConnectedInstance,
        bool hasActiveConnection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (port is < 1 or > 65535)
        {
            return Error("调试端口必须是 1 到 65535 之间的整数。", request.Endpoint, request.SkinId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(request.SkinId, request.Endpoint, "正在检查 Codex 调试实例和端口身份。");
        var resolution = await ResolveTargetAsync(
            port,
            request.Endpoint,
            request.SkinId,
            selectedTarget,
            activeConnectedInstance,
            hasActiveConnection,
            activeEndpoint: null,
            connectionOnly: false,
            cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var target = resolution.Target!;
        ReportProgress(request.SkinId, target.DebugEndpoint!, "目标身份已核验，正在连接并应用皮肤。", target.Identity);
        return await ApplyToFreshTargetAsync(
            request,
            target,
            cancellationToken,
            preserveRequestEndpoint: resolution.PreserveRequestEndpoint).ConfigureAwait(false);
    }

    internal async Task<RuntimeStatus> StartOrConnectAsync(
        int port,
        CodexInstance? selectedTarget,
        CodexProcessIdentity? activeConnectedInstance,
        bool hasActiveConnection,
        CancellationToken cancellationToken,
        string? activeEndpoint = null)
    {
        if (port is < 1 or > 65535)
        {
            var invalidEndpoint = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
            return Error("调试端口必须是 1 到 65535 之间的整数。", invalidEndpoint);
        }

        var endpoint = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(null, endpoint, "正在检查 Codex 调试实例和端口身份。");
        var resolution = await ResolveTargetAsync(
            port,
            endpoint,
            skinId: null,
            selectedTarget,
            activeConnectedInstance,
            hasActiveConnection,
            activeEndpoint,
            connectionOnly: true,
            cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ConnectedWithoutSkin(resolution.Target!, endpoint, resolution.PreferredEndpoint);
    }

    private async Task<TargetResolution> ResolveTargetAsync(
        int port,
        string endpoint,
        string? skinId,
        CodexInstance? selectedTarget,
        CodexProcessIdentity? activeConnectedInstance,
        bool hasActiveConnection,
        string? activeEndpoint,
        bool connectionOnly,
        CancellationToken cancellationToken)
    {
        if (_instances is null)
        {
            return TargetResolution.Failed(Error(
                connectionOnly
                    ? "无法核验 Codex 实例身份。请打开“选择窗口”确认目标后重试。"
                    : "无法核验 Codex 实例身份。请打开“选择窗口”确认目标后再应用。",
                endpoint,
                skinId));
        }

        var instances = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var running = GetRunningInstances(instances);
        var normalTargets = running.Where(instance => instance.Kind == CodexInstanceKind.Normal).ToArray();
        var debugTargets = running.Where(instance => instance.Kind == CodexInstanceKind.Debug).ToArray();
        if (instances.Count == 1 && normalTargets.Length == 1 && debugTargets.Length == 0)
        {
            var switchError = await ConfirmAndSwitchNormalInstanceAsync(
                normalTargets[0], endpoint, skinId, cancellationToken).ConfigureAwait(false);
            if (switchError is not null)
                return TargetResolution.Failed(switchError);
            instances = Array.Empty<CodexInstance>();
        }
        else if (normalTargets.Length > 1 && debugTargets.Length == 0)
        {
            return TargetResolution.Failed(Error(
                "检测到多个普通版 Codex。请在“选择窗口”中处理具体实例后重试；未关闭任何窗口。",
                endpoint,
                skinId));
        }

        var latestSelectedTarget = selectedTarget is null
            ? null
            : instances.FirstOrDefault(instance => SameIdentity(instance.Identity, selectedTarget.Identity));
        if (latestSelectedTarget is not null)
        {
            var invalid = ValidatePinnedTarget(latestSelectedTarget, port, endpoint, skinId, connectionOnly);
            if (invalid is not null)
            {
                return TargetResolution.Failed(invalid);
            }

            var selectedMatchesActive = hasActiveConnection && activeConnectedInstance is not null &&
                SameIdentity(latestSelectedTarget.Identity, activeConnectedInstance);
            var selected = await WaitForPinnedTargetAsync(
                latestSelectedTarget, port, endpoint, skinId, connectionOnly, cancellationToken).ConfigureAwait(false);
            return selected.Error is not null
                ? TargetResolution.Failed(selected.Error)
                : new TargetResolution(selected.Target, null, selectedMatchesActive,
                    selectedMatchesActive && IsEndpointForPort(activeEndpoint, port) ? activeEndpoint : null);
        }

        if (hasActiveConnection && activeConnectedInstance is not null)
        {
            var activeTarget = instances.FirstOrDefault(instance => SameIdentity(instance.Identity, activeConnectedInstance));
            if (activeTarget is { Kind: CodexInstanceKind.Debug, IsExitResidual: false, DebugPort: int activePort } && activePort == port)
            {
                var invalid = ValidatePinnedTarget(activeTarget, port, endpoint, skinId, connectionOnly);
                if (invalid is not null)
                {
                    return TargetResolution.Failed(invalid);
                }

                var active = await WaitForPinnedTargetAsync(
                    activeTarget, port, endpoint, skinId, connectionOnly, cancellationToken).ConfigureAwait(false);
                return active.Error is not null
                    ? TargetResolution.Failed(active.Error)
                    : new TargetResolution(active.Target, null, true,
                        IsEndpointForPort(activeEndpoint, port) ? activeEndpoint : null);
            }
        }

        var residual = instances.FirstOrDefault(instance => instance.IsExitResidual);
        if (residual is not null)
        {
            return TargetResolution.Failed(Error(
                AppLocalization.Format("检测到 Codex 后台残留（PID {0}）。请在“选择窗口”中检查并处理后再启动。", residual.Identity.ProcessId),
                endpoint,
                skinId));
        }

        running = GetRunningInstances(instances);
        debugTargets = running.Where(instance => instance.Kind == CodexInstanceKind.Debug).ToArray();
        if (debugTargets.Length > 1)
        {
            return TargetResolution.Failed(Error(DescribeMultipleDebugTargets(debugTargets, port, "检测到多个正在运行的调试版 Codex"), endpoint, skinId));
        }

        if (debugTargets.Length == 1)
        {
            var existing = debugTargets[0];
            if (existing.DebugPort != port)
            {
                return TargetResolution.Failed(Error(DescribePortMismatch(existing, port), endpoint, skinId));
            }

            var invalid = ValidatePinnedTarget(existing, port, endpoint, skinId, connectionOnly);
            if (invalid is not null)
            {
                return TargetResolution.Failed(invalid);
            }

            var existingResolution = await WaitForPinnedTargetAsync(
                existing, port, endpoint, skinId, connectionOnly, cancellationToken,
                requireUniqueDebugInstance: true).ConfigureAwait(false);
            return existingResolution.Error is not null
                ? TargetResolution.Failed(existingResolution.Error)
                : new TargetResolution(existingResolution.Target, null, false, null);
        }

        if (running.Any(instance => instance.Kind == CodexInstanceKind.Normal))
        {
            return TargetResolution.Failed(Error(
                "仍检测到普通版 Codex。请在“选择窗口”中处理退出失败或其他实例后，再启动调试版。",
                endpoint,
                skinId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(skinId, endpoint, "正在启动 Codex 调试版。");
        var launchStatus = await _launchDebugClient(new DebugClientLaunchOptions(port), cancellationToken).ConfigureAwait(false);
        if (launchStatus.State != RuntimeStatusState.Connecting)
        {
            return TargetResolution.Failed(WithSkinId(launchStatus, skinId));
        }

        ReportProgress(skinId, endpoint,
            "启动请求已发送，正在自动检测调试窗口、监听端口和 CDP 身份。");
        var launched = await WaitForUniqueDebugTargetAsync(
            port, endpoint, skinId, connectionOnly, cancellationToken).ConfigureAwait(false);
        return launched.Error is not null
            ? TargetResolution.Failed(launched.Error)
            : new TargetResolution(launched.Target, null, false, null);
    }

    private async Task<RuntimeStatus?> ConfirmAndSwitchNormalInstanceAsync(
        CodexInstance initialInstance,
        string endpoint,
        string? skinId,
        CancellationToken cancellationToken)
    {
        if (_confirmDebugModeSwitch is null)
        {
            return Error(
                "检测到唯一普通版 Codex。切换到调试版前需要确认退出；请在“选择窗口”中处理该实例。",
                endpoint,
                skinId);
        }

        bool accepted;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            accepted = await _confirmDebugModeSwitch(initialInstance, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
            catch (Exception exception)
        {
            return Error(AppLocalization.Format("无法确认普通版 Codex 切换：{0}", exception.Message), endpoint, skinId);
        }

        if (!accepted)
            return new RuntimeStatus(
                RuntimeStatusState.Stopped,
                skinId,
                endpoint,
                AppLocalization.Get("已取消切换；当前 Codex 保持运行，未启动调试版或应用皮肤。"),
                false);

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CodexInstance> beforeClose;
        try
        {
            beforeClose = await _instances!.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Error(AppLocalization.Format("确认后无法重新核验 Codex 窗口：{0}。未关闭或启动实例。", exception.Message), endpoint, skinId);
        }

        var stillSelected = beforeClose.Count == 1 &&
            SameIdentity(beforeClose[0].Identity, initialInstance.Identity) &&
            beforeClose[0].Kind == CodexInstanceKind.Normal &&
            !beforeClose[0].IsExitResidual;
        if (!stillSelected)
            return Error("确认期间 Codex 窗口或完整进程身份发生变化。未关闭或启动实例；请在“选择窗口”中刷新。", endpoint, skinId);

        cancellationToken.ThrowIfCancellationRequested();
        CodexCloseOperationResult closeResult;
        try
        {
            closeResult = await _instances.CloseAsync(beforeClose[0], force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Error(AppLocalization.Format("退出普通版 Codex 时未能确认完成：{0}。未启动调试版或应用皮肤。", exception.Message), endpoint, skinId);
        }

        if (closeResult.Outcome != CodexCloseResult.Closed)
        {
            return Error(AppLocalization.Format("普通版 Codex 尚未确认完全退出：{0}。未启动调试版或应用皮肤。", closeResult.Message), endpoint, skinId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CodexInstance> afterClose;
        try
        {
            afterClose = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Error(AppLocalization.Format("普通版退出后无法核验是否仍有 Codex 窗口或后台残留：{0}。未启动调试版或应用皮肤。", exception.Message), endpoint, skinId);
        }

        if (afterClose.Count != 0)
        {
            return Error("普通版 Codex 退出后仍检测到实例或后台残留。请在“选择窗口”中处理后重试；未启动调试版或应用皮肤。", endpoint, skinId);
        }

        return null;
    }

    private async Task<(CodexInstance? Target, RuntimeStatus? Error)> WaitForPinnedTargetAsync(
        CodexInstance initialTarget,
        int port,
        string endpoint,
        string? skinId,
        bool connectionOnly,
        CancellationToken cancellationToken,
        bool requireUniqueDebugInstance = false)
    {
        var initialError = ValidatePinnedTarget(initialTarget, port, endpoint, skinId, connectionOnly);
        if (initialError is not null)
        {
            return (null, initialError);
        }

        if (IsReadyTarget(initialTarget, port))
        {
            return (initialTarget, null);
        }

        var operationNotSent = GetOperationNotSent(connectionOnly);
        ReportProgress(skinId, endpoint,
            AppLocalization.Format("已发现调试实例 PID {0}，正在等待监听端口和 CDP 身份就绪。", initialTarget.Identity.ProcessId),
            initialTarget.Identity);
        var deadline = DateTimeOffset.UtcNow + _readinessTimeout;
        var lastState = initialTarget.DebugConnectionState;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instances = await _instances!.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
            if (requireUniqueDebugInstance)
            {
                var debugTargets = GetRunningInstances(instances)
                    .Where(instance => instance.Kind == CodexInstanceKind.Debug)
                    .ToArray();
                if (debugTargets.Length > 1)
                {
                    return (null, Error(
                        AppLocalization.Format("{0}；{1}。", DescribeMultipleDebugTargets(debugTargets, port, "等待期间出现多个调试版 Codex"), operationNotSent),
                        endpoint,
                        skinId));
                }
            }

            var current = instances.FirstOrDefault(instance => SameIdentity(instance.Identity, initialTarget.Identity));
            if (current is null)
            {
                var samePidReplacement = instances.FirstOrDefault(instance => instance.Identity.ProcessId == initialTarget.Identity.ProcessId);
                var message = samePidReplacement is null
                    ? AppLocalization.Format("等待期间调试实例 PID {0} 已退出；{1}。请刷新实例列表后重试。", initialTarget.Identity.ProcessId, operationNotSent)
                    : AppLocalization.Format("等待期间 PID {0} 的启动时间或可执行文件身份发生变化；{1}。已停止连接。", initialTarget.Identity.ProcessId, operationNotSent);
                return (null, Error(message, endpoint, skinId));
            }

            var invalid = ValidatePinnedTarget(current, port, endpoint, skinId, connectionOnly);
            if (invalid is not null)
            {
                return (null, invalid);
            }

            if (IsReadyTarget(current, port))
            {
                return (current, null);
            }

            if (current.DebugConnectionState != lastState)
            {
                ReportProgress(skinId, endpoint, DescribeWaitingState(current, connectionOnly), current.Identity);
                lastState = current.DebugConnectionState;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return (null, Error(
                AppLocalization.Format("等待调试实例 PID {0} 的端口 {1} 就绪超时；{2}。{3}", initialTarget.Identity.ProcessId, port, operationNotSent, DescribeWaitingState(initialTarget with { DebugConnectionState = lastState }, connectionOnly)),
            endpoint,
            skinId));
    }

    private async Task<(CodexInstance? Target, RuntimeStatus? Error)> WaitForUniqueDebugTargetAsync(
        int port,
        string endpoint,
        string? skinId,
        bool connectionOnly,
        CancellationToken cancellationToken)
    {
        var operationNotSent = GetOperationNotSent(connectionOnly);
        var deadline = DateTimeOffset.UtcNow + _readinessTimeout;
        CodexDebugConnectionState? lastReportedState = null;
        CodexProcessIdentity? discoveredIdentity = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instances = await _instances!.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
            var residual = instances.FirstOrDefault(instance => instance.IsExitResidual);
            if (residual is not null)
            {
                return (null, Error(
                    AppLocalization.Format("启动等待期间检测到 Codex 后台残留（PID {0}）；{1}。请在“选择窗口”中检查并处理。", residual.Identity.ProcessId, operationNotSent),
                    endpoint,
                    skinId));
            }

            var running = GetRunningInstances(instances);
            if (running.Any(instance => instance.Kind == CodexInstanceKind.Normal))
            {
                return (null, Error(
                    AppLocalization.Format("启动后检测到普通版 Codex；{0}。请在“选择窗口”中检查当前窗口。", operationNotSent),
                    endpoint,
                    skinId));
            }

            var debugTargets = running.Where(instance => instance.Kind == CodexInstanceKind.Debug).ToArray();
            if (debugTargets.Length > 1)
            {
                return (null, Error(
                    AppLocalization.Format("{0}；{1}。", DescribeMultipleDebugTargets(debugTargets, port, "等待期间出现多个调试版 Codex"), operationNotSent),
                    endpoint,
                    skinId));
            }

            if (debugTargets.Length == 1)
            {
                var target = debugTargets[0];
                if (discoveredIdentity is not null && !SameIdentity(discoveredIdentity, target.Identity))
                {
                    return (null, Error(
                        AppLocalization.Format("启动等待期间调试实例的进程身份发生变化；{0}。已停止自动选择。", operationNotSent),
                        endpoint,
                        skinId));
                }

                if (target.DebugPort != port)
                {
                    return (null, Error(AppLocalization.Format("启动后检测到端口为 {0} 的调试实例，与请求端口 {1} 不同；{2}。请在“选择窗口”中选择窗口。", FormatPort(target.DebugPort), port, operationNotSent), endpoint, skinId));
                }

                discoveredIdentity ??= target.Identity;

                var invalid = ValidatePinnedTarget(target, port, endpoint, skinId, connectionOnly);
                if (invalid is not null)
                {
                    return (null, invalid);
                }

                if (IsReadyTarget(target, port))
                {
                    return (target, null);
                }

                if (lastReportedState != target.DebugConnectionState)
                {
                    ReportProgress(skinId, endpoint, DescribeWaitingState(target, connectionOnly), target.Identity);
                    lastReportedState = target.DebugConnectionState;
                }
            }
            else if (discoveredIdentity is not null)
            {
                return (null, Error(
                    AppLocalization.Format("等待期间调试实例 PID {0} 已退出；{1}。请刷新实例列表后重试。", discoveredIdentity.ProcessId, operationNotSent),
                    endpoint,
                    skinId));
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return (null, Error(
            AppLocalization.Format("等待 Codex 调试端口 {0} 就绪超时；{1}。已启动的 Codex 会保持运行。", port, operationNotSent),
            endpoint,
            skinId));
    }

    private static RuntimeStatus? ValidatePinnedTarget(
        CodexInstance target,
        int port,
        string endpoint,
        string? skinId,
        bool connectionOnly)
    {
        if (target.IsExitResidual)
        {
            return Error(
                AppLocalization.Format("所选调试实例 PID {0} 的主进程已退出，但仍有后台进程残留。{1}。请在“选择窗口”中检查并处理后再启动。", target.Identity.ProcessId, GetOperationNotSent(connectionOnly)),
                endpoint,
                skinId);
        }

        if (target.Kind != CodexInstanceKind.Debug || target.DebugPort != port)
        {
            return Error("所选实例的类型或调试端口已变化。请刷新实例列表并重新选择。", endpoint, skinId);
        }

        if (target.DebugConnectionState == CodexDebugConnectionState.Rejected)
        {
            return Error(DescribeUnavailableTarget(target), endpoint, skinId);
        }

        if (target.DebugConnectionState == CodexDebugConnectionState.Ready && !IsReadyTarget(target, port))
        {
            return Error(DescribeUnavailableTarget(target), endpoint, skinId);
        }

        if (target.DebugConnectionState is not CodexDebugConnectionState.Ready and
            not CodexDebugConnectionState.WaitingForListener and
            not CodexDebugConnectionState.WaitingForCdp)
        {
            return Error(
                AppLocalization.Format("调试实例 PID {0} 缺少可靠的 listener/CDP 就绪状态；{1}。请刷新实例列表。", target.Identity.ProcessId, GetOperationNotSent(connectionOnly)),
                endpoint,
                skinId);
        }

        return null;
    }

    private Task<RuntimeStatus> ApplyToFreshTargetAsync(
        RuntimeApplyRequest request,
        CodexInstance target,
        CancellationToken cancellationToken,
        bool preserveRequestEndpoint = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _apply(request with
        {
            Endpoint = preserveRequestEndpoint && IsEndpointForPort(request.Endpoint, target.DebugPort!.Value)
                ? request.Endpoint
                : target.DebugEndpoint ?? request.Endpoint,
            ExpectedInstance = target.Identity
        }, cancellationToken);
    }

    private void ReportProgress(string? skinId, string? endpoint, string message, CodexProcessIdentity? identity = null) =>
        _progress?.Invoke(new RuntimeStatus(RuntimeStatusState.Connecting, skinId, endpoint, message, null, 0, identity));

    private static RuntimeStatus ConnectedWithoutSkin(CodexInstance target, string endpoint, string? preferredEndpoint = null) =>
        new(
            RuntimeStatusState.Connected,
            null,
            IsEndpointForPort(preferredEndpoint, target.DebugPort ?? 0)
                ? preferredEndpoint
                : target.DebugEndpoint ?? endpoint,
            "Codex 调试目标已核验；没有应用皮肤。",
            true,
            0,
            target.Identity);

    private static RuntimeStatus WithSkinId(RuntimeStatus status, string? skinId) =>
        status.SkinId == skinId
            ? status
            : new RuntimeStatus(status.State, skinId, status.Endpoint, status.Message, status.IsDebugPortOpen,
                status.AppliedTargetCount, status.ConnectedInstance, status.CompatibilityResult);

    private static List<CodexInstance> GetRunningInstances(IReadOnlyList<CodexInstance> instances) =>
        instances.Where(instance => !instance.IsExitResidual).ToList();

    private static bool IsReadyTarget(CodexInstance target, int port) =>
        target.Kind == CodexInstanceKind.Debug &&
        !target.IsExitResidual &&
        target.DebugPort == port &&
        target.DebugConnectionState == CodexDebugConnectionState.Ready &&
        target.CanConnect &&
        IsEndpointForPort(target.DebugEndpoint, port);

    private static bool IsEndpointForPort(string? value, int port) =>
        Uri.TryCreate(value, UriKind.Absolute, out var endpoint) &&
        endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback && endpoint.Port == port;

    private static string DescribeUnavailableTarget(CodexInstance target)
    {
        if (target.IsExitResidual)
        {
            return AppLocalization.Format("调试实例 PID {0} 的主进程已退出，但仍有后台进程残留。", target.Identity.ProcessId);
        }

        if (!string.IsNullOrWhiteSpace(target.ConnectionMessage))
        {
            return target.ConnectionMessage;
        }

        if (target.DebugPort is not int port || port is < 1 or > 65535)
        {
            return AppLocalization.Format("调试实例 PID {0} 没有有效的调试端口。请刷新实例列表检查启动参数。", target.Identity.ProcessId);
        }

        if (target.DebugConnectionState == CodexDebugConnectionState.WaitingForListener)
        {
            return AppLocalization.Format("调试实例 PID {0} 的端口 {1} 尚未开始监听。", target.Identity.ProcessId, port);
        }

        if (target.DebugConnectionState == CodexDebugConnectionState.WaitingForCdp)
        {
            return AppLocalization.Format("调试实例 PID {0} 已拥有端口 {1}，但 CDP 服务尚未就绪。", target.Identity.ProcessId, port);
        }

        if (target.CanConnect && string.IsNullOrWhiteSpace(target.DebugEndpoint))
        {
            return AppLocalization.Format("调试实例 PID {0} 的监听端口已响应，但 Runtime 未返回经过身份核验的 Endpoint。", target.Identity.ProcessId);
        }

        return AppLocalization.Format("调试实例 PID {0} 声明了端口 {1}，但尚未核验到由该进程拥有的可连接端口。请处理占用进程并刷新列表。", target.Identity.ProcessId, port);
    }

    private static string DescribeWaitingState(CodexInstance target, bool connectionOnly) =>
        target.DebugConnectionState switch
        {
            CodexDebugConnectionState.WaitingForListener =>
                AppLocalization.Format("正在等待调试实例 PID {0} 开始监听端口 {1}；{2}。", target.Identity.ProcessId, FormatPort(target.DebugPort), GetOperationNotSent(connectionOnly)),
            CodexDebugConnectionState.WaitingForCdp =>
                AppLocalization.Format("端口已由调试实例 PID {0} 持有，正在等待 CDP 服务完成身份核验；{1}。", target.Identity.ProcessId, GetOperationNotSent(connectionOnly)),
            _ => AppLocalization.Format("正在等待调试实例 PID {0} 完成端口和 CDP 身份核验；{1}。", target.Identity.ProcessId, GetOperationNotSent(connectionOnly))
        };

    private static string DescribePortMismatch(CodexInstance instance, int requestedPort) =>
        AppLocalization.Format("检测到正在运行的调试版 Codex（端口 {0}），与当前端口 {1} 不匹配。请在“选择窗口”中选择窗口。", FormatPort(instance.DebugPort), requestedPort);

    private static string DescribeMultipleDebugTargets(IReadOnlyList<CodexInstance> targets, int requestedPort, string prefix)
    {
        var ports = string.Join("、", targets.Select(instance => instance.DebugPort).Distinct()
            .OrderBy(port => port ?? int.MaxValue).Select(FormatPort));
        return AppLocalization.Format("{0}（端口 {1}），当前请求端口为 {2}。请在“选择窗口”中选择窗口。", AppLocalization.Get(prefix), ports, requestedPort);
    }

    private static string FormatPort(int? port) => port?.ToString(CultureInfo.InvariantCulture) ?? AppLocalization.Get("未知");

    private static string GetOperationNotSent(bool connectionOnly) => AppLocalization.Get(connectionOnly ? "未应用皮肤" : "尚未发送皮肤脚本");

    private static bool SameIdentity(CodexProcessIdentity left, CodexProcessIdentity right) =>
        left.ProcessId == right.ProcessId &&
        left.StartedAtUtc.UtcDateTime == right.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(left.ExecutablePath), Path.GetFullPath(right.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static RuntimeStatus Error(string message, string? endpoint = null, string? skinId = null) =>
        new(RuntimeStatusState.Error, skinId, endpoint, AppLocalization.Get(message), false);

    private sealed record TargetResolution(
        CodexInstance? Target,
        RuntimeStatus? Error,
        bool PreserveRequestEndpoint,
        string? PreferredEndpoint)
    {
        public static TargetResolution Failed(RuntimeStatus error) => new(null, error, false, null);
    }
}

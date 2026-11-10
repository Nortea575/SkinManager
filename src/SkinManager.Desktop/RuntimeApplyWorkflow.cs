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

    internal RuntimeApplyWorkflow(
        Func<DebugClientLaunchOptions, CancellationToken, Task<RuntimeStatus>> launchDebugClient,
        Func<RuntimeApplyRequest, CancellationToken, Task<RuntimeStatus>> apply,
        ICodexInstanceService? instances,
        TimeSpan? readinessTimeout = null,
        TimeSpan? pollInterval = null,
        Action<RuntimeStatus>? progress = null)
    {
        _launchDebugClient = launchDebugClient;
        _apply = apply;
        _instances = instances;
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(45);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(900);
        _progress = progress;
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
            compatibilityOnly: false,
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
            compatibilityOnly: true,
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
        bool compatibilityOnly,
        CancellationToken cancellationToken)
    {
        if (_instances is null)
        {
            return TargetResolution.Failed(Error(
                compatibilityOnly
                    ? "无法核验 Codex 实例身份。请使用“管理实例”选择窗口后重试。"
                    : "无法核验 Codex 实例身份。请使用“管理实例”选择窗口后再应用。",
                endpoint,
                skinId));
        }

        var instances = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var latestSelectedTarget = selectedTarget is null
            ? null
            : instances.FirstOrDefault(instance => SameIdentity(instance.Identity, selectedTarget.Identity));
        if (latestSelectedTarget is not null)
        {
            var invalid = ValidatePinnedTarget(latestSelectedTarget, port, endpoint, skinId, compatibilityOnly);
            if (invalid is not null)
            {
                return TargetResolution.Failed(invalid);
            }

            var selectedMatchesActive = hasActiveConnection && activeConnectedInstance is not null &&
                SameIdentity(latestSelectedTarget.Identity, activeConnectedInstance);
            var selected = await WaitForPinnedTargetAsync(
                latestSelectedTarget, port, endpoint, skinId, compatibilityOnly, cancellationToken).ConfigureAwait(false);
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
                var invalid = ValidatePinnedTarget(activeTarget, port, endpoint, skinId, compatibilityOnly);
                if (invalid is not null)
                {
                    return TargetResolution.Failed(invalid);
                }

                var active = await WaitForPinnedTargetAsync(
                    activeTarget, port, endpoint, skinId, compatibilityOnly, cancellationToken).ConfigureAwait(false);
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
                $"检测到 Codex 后台残留（PID {residual.Identity.ProcessId}）。请在“管理实例”中检查并处理后再启动。",
                endpoint,
                skinId));
        }

        var running = GetRunningInstances(instances);
        var debugTargets = running.Where(instance => instance.Kind == CodexInstanceKind.Debug).ToArray();
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

            var invalid = ValidatePinnedTarget(existing, port, endpoint, skinId, compatibilityOnly);
            if (invalid is not null)
            {
                return TargetResolution.Failed(invalid);
            }

            var existingResolution = await WaitForPinnedTargetAsync(
                existing, port, endpoint, skinId, compatibilityOnly, cancellationToken,
                requireUniqueDebugInstance: true).ConfigureAwait(false);
            return existingResolution.Error is not null
                ? TargetResolution.Failed(existingResolution.Error)
                : new TargetResolution(existingResolution.Target, null, false, null);
        }

        if (running.Any(instance => instance.Kind == CodexInstanceKind.Normal))
        {
            return TargetResolution.Failed(Error(
                "检测到普通版 Codex 仍在运行。请先在“管理实例”中定位并手动退出，再启动调试版；管理器不会自动关闭或重启 Codex。",
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
            port, endpoint, skinId, compatibilityOnly, cancellationToken).ConfigureAwait(false);
        return launched.Error is not null
            ? TargetResolution.Failed(launched.Error)
            : new TargetResolution(launched.Target, null, false, null);
    }

    private async Task<(CodexInstance? Target, RuntimeStatus? Error)> WaitForPinnedTargetAsync(
        CodexInstance initialTarget,
        int port,
        string endpoint,
        string? skinId,
        bool compatibilityOnly,
        CancellationToken cancellationToken,
        bool requireUniqueDebugInstance = false)
    {
        var initialError = ValidatePinnedTarget(initialTarget, port, endpoint, skinId, compatibilityOnly);
        if (initialError is not null)
        {
            return (null, initialError);
        }

        if (IsReadyTarget(initialTarget, port))
        {
            return (initialTarget, null);
        }

        var operationNotSent = GetOperationNotSent(compatibilityOnly);
        ReportProgress(skinId, endpoint,
            $"已发现调试实例 PID {initialTarget.Identity.ProcessId}，正在等待监听端口和 CDP 身份就绪。",
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
                        DescribeMultipleDebugTargets(debugTargets, port, "等待期间出现多个调试版 Codex") + $"；{operationNotSent}。",
                        endpoint,
                        skinId));
                }
            }

            var current = instances.FirstOrDefault(instance => SameIdentity(instance.Identity, initialTarget.Identity));
            if (current is null)
            {
                var samePidReplacement = instances.FirstOrDefault(instance => instance.Identity.ProcessId == initialTarget.Identity.ProcessId);
                var message = samePidReplacement is null
                    ? $"等待期间调试实例 PID {initialTarget.Identity.ProcessId} 已退出；{operationNotSent}。请刷新实例列表后重试。"
                    : $"等待期间 PID {initialTarget.Identity.ProcessId} 的启动时间或可执行文件身份发生变化；{operationNotSent}。已停止连接。";
                return (null, Error(message, endpoint, skinId));
            }

            var invalid = ValidatePinnedTarget(current, port, endpoint, skinId, compatibilityOnly);
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
                ReportProgress(skinId, endpoint, DescribeWaitingState(current, compatibilityOnly), current.Identity);
                lastState = current.DebugConnectionState;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return (null, Error(
            $"等待调试实例 PID {initialTarget.Identity.ProcessId} 的端口 {port} 就绪超时；{operationNotSent}。{DescribeWaitingState(initialTarget with { DebugConnectionState = lastState }, compatibilityOnly)}",
            endpoint,
            skinId));
    }

    private async Task<(CodexInstance? Target, RuntimeStatus? Error)> WaitForUniqueDebugTargetAsync(
        int port,
        string endpoint,
        string? skinId,
        bool compatibilityOnly,
        CancellationToken cancellationToken)
    {
        var operationNotSent = GetOperationNotSent(compatibilityOnly);
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
                    $"启动等待期间检测到 Codex 后台残留（PID {residual.Identity.ProcessId}）；{operationNotSent}。请在“管理实例”中检查并处理。",
                    endpoint,
                    skinId));
            }

            var running = GetRunningInstances(instances);
            if (running.Any(instance => instance.Kind == CodexInstanceKind.Normal))
            {
                return (null, Error(
                    $"启动后检测到普通版 Codex；{operationNotSent}。请在“管理实例”中检查当前窗口。",
                    endpoint,
                    skinId));
            }

            var debugTargets = running.Where(instance => instance.Kind == CodexInstanceKind.Debug).ToArray();
            if (debugTargets.Length > 1)
            {
                return (null, Error(
                    DescribeMultipleDebugTargets(debugTargets, port, "等待期间出现多个调试版 Codex") + $"；{operationNotSent}。",
                    endpoint,
                    skinId));
            }

            if (debugTargets.Length == 1)
            {
                var target = debugTargets[0];
                if (discoveredIdentity is not null && !SameIdentity(discoveredIdentity, target.Identity))
                {
                    return (null, Error(
                        $"启动等待期间调试实例的进程身份发生变化；{operationNotSent}。已停止自动选择。",
                        endpoint,
                        skinId));
                }

                if (target.DebugPort != port)
                {
                    return (null, Error($"启动后检测到端口为 {FormatPort(target.DebugPort)} 的调试实例，与请求端口 {port} 不同；{operationNotSent}。请在“管理实例”中选择窗口。", endpoint, skinId));
                }

                discoveredIdentity ??= target.Identity;

                var invalid = ValidatePinnedTarget(target, port, endpoint, skinId, compatibilityOnly);
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
                    ReportProgress(skinId, endpoint, DescribeWaitingState(target, compatibilityOnly), target.Identity);
                    lastReportedState = target.DebugConnectionState;
                }
            }
            else if (discoveredIdentity is not null)
            {
                return (null, Error(
                    $"等待期间调试实例 PID {discoveredIdentity.ProcessId} 已退出；{operationNotSent}。请刷新实例列表后重试。",
                    endpoint,
                    skinId));
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return (null, Error(
            $"等待 Codex 调试端口 {port} 就绪超时；{operationNotSent}。已启动的 Codex 会保持运行。",
            endpoint,
            skinId));
    }

    private static RuntimeStatus? ValidatePinnedTarget(
        CodexInstance target,
        int port,
        string endpoint,
        string? skinId,
        bool compatibilityOnly)
    {
        if (target.IsExitResidual)
        {
            return Error(
                $"所选调试实例 PID {target.Identity.ProcessId} 的主进程已退出，但仍有后台进程残留。{GetOperationNotSent(compatibilityOnly)}。请在“管理实例”中检查并处理后再启动。",
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
                $"调试实例 PID {target.Identity.ProcessId} 缺少可靠的 listener/CDP 就绪状态；{GetOperationNotSent(compatibilityOnly)}。请刷新实例列表。",
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
            return $"调试实例 PID {target.Identity.ProcessId} 的主进程已退出，但仍有后台进程残留。";
        }

        if (!string.IsNullOrWhiteSpace(target.ConnectionMessage))
        {
            return target.ConnectionMessage;
        }

        if (target.DebugPort is not int port || port is < 1 or > 65535)
        {
            return $"调试实例 PID {target.Identity.ProcessId} 没有有效的调试端口。请刷新实例列表检查启动参数。";
        }

        if (target.DebugConnectionState == CodexDebugConnectionState.WaitingForListener)
        {
            return $"调试实例 PID {target.Identity.ProcessId} 的端口 {port} 尚未开始监听。";
        }

        if (target.DebugConnectionState == CodexDebugConnectionState.WaitingForCdp)
        {
            return $"调试实例 PID {target.Identity.ProcessId} 已拥有端口 {port}，但 CDP 服务尚未就绪。";
        }

        if (target.CanConnect && string.IsNullOrWhiteSpace(target.DebugEndpoint))
        {
            return $"调试实例 PID {target.Identity.ProcessId} 的监听端口已响应，但 Runtime 未返回经过身份核验的 Endpoint。";
        }

        return $"调试实例 PID {target.Identity.ProcessId} 声明了端口 {port}，但尚未核验到由该进程拥有的可连接端口。请处理占用进程并刷新列表。";
    }

    private static string DescribeWaitingState(CodexInstance target, bool compatibilityOnly) =>
        target.DebugConnectionState switch
        {
            CodexDebugConnectionState.WaitingForListener =>
                $"正在等待调试实例 PID {target.Identity.ProcessId} 开始监听端口 {target.DebugPort}；{GetOperationNotSent(compatibilityOnly)}。",
            CodexDebugConnectionState.WaitingForCdp =>
                $"端口已由调试实例 PID {target.Identity.ProcessId} 持有，正在等待 CDP 服务完成身份核验；{GetOperationNotSent(compatibilityOnly)}。",
            _ => $"正在等待调试实例 PID {target.Identity.ProcessId} 完成端口和 CDP 身份核验；{GetOperationNotSent(compatibilityOnly)}。"
        };

    private static string DescribePortMismatch(CodexInstance instance, int requestedPort) =>
        $"检测到正在运行的调试版 Codex（端口 {FormatPort(instance.DebugPort)}），与当前端口 {requestedPort} 不匹配。请在“管理实例”中选择窗口。";

    private static string DescribeMultipleDebugTargets(IReadOnlyList<CodexInstance> targets, int requestedPort, string prefix)
    {
        var ports = string.Join("、", targets.Select(instance => instance.DebugPort).Distinct()
            .OrderBy(port => port ?? int.MaxValue).Select(FormatPort));
        return $"{prefix}（端口 {ports}），当前请求端口为 {requestedPort}。请在“管理实例”中选择窗口。";
    }

    private static string FormatPort(int? port) => port?.ToString(CultureInfo.InvariantCulture) ?? "未知";

    private static string GetOperationNotSent(bool compatibilityOnly) => compatibilityOnly ? "未配置界面语言" : "未发送皮肤脚本";

    private static bool SameIdentity(CodexProcessIdentity left, CodexProcessIdentity right) =>
        left.ProcessId == right.ProcessId &&
        left.StartedAtUtc.UtcDateTime == right.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(left.ExecutablePath), Path.GetFullPath(right.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static RuntimeStatus Error(string message, string? endpoint = null, string? skinId = null) =>
        new(RuntimeStatusState.Error, skinId, endpoint, message, false);

    private sealed record TargetResolution(
        CodexInstance? Target,
        RuntimeStatus? Error,
        bool PreserveRequestEndpoint,
        string? PreferredEndpoint)
    {
        public static TargetResolution Failed(RuntimeStatus error) => new(null, error, false, null);
    }
}

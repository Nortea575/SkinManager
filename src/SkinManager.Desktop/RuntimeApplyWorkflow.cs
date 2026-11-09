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

    internal RuntimeApplyWorkflow(
        Func<DebugClientLaunchOptions, CancellationToken, Task<RuntimeStatus>> launchDebugClient,
        Func<RuntimeApplyRequest, CancellationToken, Task<RuntimeStatus>> apply,
        ICodexInstanceService? instances,
        TimeSpan? readinessTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _launchDebugClient = launchDebugClient;
        _apply = apply;
        _instances = instances;
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(45);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(900);
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
            return Error("调试端口必须是 1 到 65535 之间的整数。", request.Endpoint);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_instances is null)
        {
            return Error("无法核验 Codex 实例身份。请使用“管理实例”选择窗口后再应用。", request.Endpoint);
        }

        var instances = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var latestSelectedTarget = selectedTarget is null
            ? null
            : instances.FirstOrDefault(instance => SameIdentity(instance.Identity, selectedTarget.Identity));
        if (latestSelectedTarget is not null)
        {
            if (latestSelectedTarget.IsExitResidual)
            {
                return Error("所选调试实例已退出，但仍有后台进程残留。请在“管理实例”中检查并处理后再启动。", request.Endpoint);
            }

            if (latestSelectedTarget.Kind != CodexInstanceKind.Debug || latestSelectedTarget.DebugPort != port)
            {
                return Error("所选实例的类型或调试端口已变化。请刷新实例列表并重新选择。", request.Endpoint);
            }

            if (!latestSelectedTarget.CanConnect || string.IsNullOrWhiteSpace(latestSelectedTarget.DebugEndpoint))
            {
                return Error(DescribeUnavailableTarget(latestSelectedTarget), request.Endpoint);
            }

            var selectedMatchesActive = hasActiveConnection && activeConnectedInstance is not null &&
                SameIdentity(latestSelectedTarget.Identity, activeConnectedInstance);
            return await ApplyToFreshTargetAsync(
                request, latestSelectedTarget, cancellationToken, preserveRequestEndpoint: selectedMatchesActive).ConfigureAwait(false);
        }

        if (hasActiveConnection && activeConnectedInstance is not null)
        {
            var activeTarget = instances.FirstOrDefault(instance => SameIdentity(instance.Identity, activeConnectedInstance));
            if (activeTarget is { CanConnect: true, Kind: CodexInstanceKind.Debug, IsExitResidual: false, DebugPort: int activePort, DebugEndpoint: { Length: > 0 } } &&
                activePort == port)
            {
                return await ApplyToFreshTargetAsync(request, activeTarget, cancellationToken, preserveRequestEndpoint: true).ConfigureAwait(false);
            }
        }

        var running = GetRunningInstances(instances);
        if (instances.FirstOrDefault(instance => instance.IsExitResidual) is { } residual)
        {
            return Error(
                $"检测到 Codex 后台残留（PID {residual.Identity.ProcessId}）。请在“管理实例”中检查并处理后再启动。",
                request.Endpoint);
        }

        var matchingTargets = FindConnectableDebugTargets(running, port);
        var samePortTargets = running
            .Where(instance => instance.Kind == CodexInstanceKind.Debug && instance.DebugPort == port)
            .ToArray();
        if (samePortTargets.Length > 1)
        {
            return Error($"端口 {port} 对应多个 Codex 调试实例。请在“管理实例”中选择连接目标。", request.Endpoint);
        }

        if (samePortTargets.Length == 1 &&
            (!samePortTargets[0].CanConnect || string.IsNullOrWhiteSpace(samePortTargets[0].DebugEndpoint)))
        {
            return Error(DescribeUnavailableTarget(samePortTargets[0]), request.Endpoint);
        }

        if (matchingTargets.Count == 1)
        {
            return await ApplyToFreshTargetAsync(request, matchingTargets[0], cancellationToken).ConfigureAwait(false);
        }

        if (samePortTargets.Length == 1)
        {
            var readySamePortTarget = await WaitForDebugTargetAsync(port, request.Endpoint, cancellationToken).ConfigureAwait(false);
            if (readySamePortTarget.Error is not null)
            {
                return readySamePortTarget.Error;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await ApplyToFreshTargetAsync(request, readySamePortTarget.Target!, cancellationToken).ConfigureAwait(false);
        }

        var otherDebugTargets = running
            .Where(instance => instance.Kind == CodexInstanceKind.Debug)
            .ToArray();
        if (otherDebugTargets.Length > 0)
        {
            var ports = string.Join("、", otherDebugTargets
                .Select(instance => instance.DebugPort)
                .Where(candidatePort => candidatePort.HasValue)
                .Select(candidatePort => candidatePort!.Value)
                .Distinct()
                .Order());
            return Error(
                $"检测到正在运行的调试版 Codex（端口 {ports}），与当前端口 {port} 不匹配。请在“管理实例”中选择窗口。",
                request.Endpoint);
        }

        if (running.Any(instance => instance.Kind == CodexInstanceKind.Normal))
        {
            return Error(
                "检测到普通版 Codex 仍在运行。请先在“管理实例”中定位并手动退出，再启动调试版；管理器不会自动关闭或重启 Codex。",
                request.Endpoint);
        }

        var launchStatus = await _launchDebugClient(new DebugClientLaunchOptions(port), cancellationToken).ConfigureAwait(false);
        if (launchStatus.State != RuntimeStatusState.Connecting)
        {
            return launchStatus;
        }

        var readyTarget = await WaitForDebugTargetAsync(port, request.Endpoint, cancellationToken).ConfigureAwait(false);
        if (readyTarget.Error is not null)
        {
            return readyTarget.Error;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await ApplyToFreshTargetAsync(request, readyTarget.Target!, cancellationToken).ConfigureAwait(false);
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
            return Error("调试端口必须是 1 到 65535 之间的整数。", $"http://127.0.0.1:{port}");
        }

        var endpoint = $"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        cancellationToken.ThrowIfCancellationRequested();
        if (_instances is null)
        {
            return Error("无法核验 Codex 实例身份。请使用“管理实例”选择窗口后重试。", endpoint);
        }

        var initialInstances = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var latestSelectedTarget = selectedTarget is null
            ? null
            : initialInstances.FirstOrDefault(instance => SameIdentity(instance.Identity, selectedTarget.Identity));
        if (latestSelectedTarget is not null)
        {
            if (latestSelectedTarget.IsExitResidual)
            {
                return Error("所选调试实例已退出，但仍有后台进程残留。请在“管理实例”中检查并处理后再启动。", endpoint);
            }

            if (latestSelectedTarget.Kind != CodexInstanceKind.Debug || latestSelectedTarget.DebugPort != port)
            {
                return Error("所选实例的类型或调试端口已变化。请刷新实例列表并重新选择。", endpoint);
            }

            var selectedMatchesActive = hasActiveConnection && activeConnectedInstance is not null &&
                SameIdentity(latestSelectedTarget.Identity, activeConnectedInstance);
            return latestSelectedTarget.CanConnect && !string.IsNullOrWhiteSpace(latestSelectedTarget.DebugEndpoint)
                ? ConnectedWithoutSkin(latestSelectedTarget, endpoint,
                    selectedMatchesActive && IsEndpointForPort(activeEndpoint, port) ? activeEndpoint : null)
                : Error(DescribeUnavailableTarget(latestSelectedTarget), endpoint);
        }

        if (hasActiveConnection && activeConnectedInstance is not null)
        {
            var activeTarget = initialInstances.FirstOrDefault(instance => SameIdentity(instance.Identity, activeConnectedInstance));
            if (activeTarget is { CanConnect: true, Kind: CodexInstanceKind.Debug, IsExitResidual: false, DebugPort: int connectedPort, DebugEndpoint: { Length: > 0 } } &&
                connectedPort == port)
            {
                return ConnectedWithoutSkin(activeTarget, endpoint,
                    IsEndpointForPort(activeEndpoint, port) ? activeEndpoint : null);
            }
        }

        var running = GetRunningInstances(initialInstances);
        if (initialInstances.FirstOrDefault(instance => instance.IsExitResidual) is { } residual)
        {
            return Error(
                $"检测到 Codex 后台残留（PID {residual.Identity.ProcessId}）。请在“管理实例”中检查并处理后再启动。",
                endpoint);
        }
        var matchingTargets = FindConnectableDebugTargets(running, port);
        var samePortTargets = running
            .Where(instance => instance.Kind == CodexInstanceKind.Debug && instance.DebugPort == port)
            .ToArray();
        if (samePortTargets.Length > 1 || matchingTargets.Count > 1)
        {
            return Error($"端口 {port} 对应多个 Codex 调试实例。请在“管理实例”中选择连接目标。", endpoint);
        }

        if (samePortTargets.Length == 1 &&
            (!samePortTargets[0].CanConnect || string.IsNullOrWhiteSpace(samePortTargets[0].DebugEndpoint)))
        {
            return Error(DescribeUnavailableTarget(samePortTargets[0]), endpoint);
        }

        if (matchingTargets.Count == 1)
        {
            return ConnectedWithoutSkin(matchingTargets[0], endpoint);
        }

        if (samePortTargets.Length == 1)
        {
            var readySamePortTarget = await WaitForDebugTargetAsync(
                port, endpoint, cancellationToken, compatibilityOnly: true).ConfigureAwait(false);
            return readySamePortTarget.Error ?? ConnectedWithoutSkin(readySamePortTarget.Target!, endpoint);
        }

        var otherDebugTarget = running.FirstOrDefault(instance => instance.Kind == CodexInstanceKind.Debug);
        if (otherDebugTarget is not null)
        {
            var otherPort = otherDebugTarget.DebugPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未知";
            return Error($"检测到正在运行的调试版 Codex（端口 {otherPort}），与当前端口 {port} 不匹配。请在“管理实例”中选择窗口。", endpoint);
        }

        if (running.Any(instance => instance.Kind == CodexInstanceKind.Normal))
        {
            return Error(
                "检测到普通版 Codex 仍在运行。请先在“管理实例”中定位并手动退出，再启动调试版；管理器不会自动关闭或重启 Codex。",
                endpoint);
        }

        var launchStatus = await _launchDebugClient(new DebugClientLaunchOptions(port), cancellationToken).ConfigureAwait(false);
        if (launchStatus.State != RuntimeStatusState.Connecting)
        {
            return launchStatus;
        }

        var readyTarget = await WaitForDebugTargetAsync(
            port, endpoint, cancellationToken, compatibilityOnly: true).ConfigureAwait(false);
        if (readyTarget.Error is not null)
        {
            return readyTarget.Error;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ConnectedWithoutSkin(readyTarget.Target!, endpoint);
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

    private async Task<(CodexInstance? Target, RuntimeStatus? Error)> WaitForDebugTargetAsync(
        int port,
        string endpoint,
        CancellationToken cancellationToken,
        bool compatibilityOnly = false)
    {
        var operationNotSent = compatibilityOnly ? "未配置界面语言" : "未发送皮肤脚本";
        if (_instances is null)
        {
            return (null, Error($"无法核验刚启动的 Codex 调试实例，{operationNotSent}。", endpoint));
        }

        var deadline = DateTimeOffset.UtcNow + _readinessTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instances = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
            var running = GetRunningInstances(instances);
            var matchingTargets = FindConnectableDebugTargets(running, port);
            if (matchingTargets.Count > 1)
            {
                return (null, Error($"端口 {port} 对应多个调试实例，{operationNotSent}。请在“管理实例”中选择窗口。", endpoint));
            }

            if (matchingTargets.Count == 1)
            {
                return (matchingTargets[0], null);
            }

            if (running.Any(instance => instance.Kind == CodexInstanceKind.Normal))
            {
                return (null, Error(
                    $"启动后检测到普通版 Codex，而不是调试版；{operationNotSent}。请在“管理实例”中检查当前窗口。",
                    endpoint));
            }

            var otherDebugTarget = running.FirstOrDefault(instance => instance.Kind == CodexInstanceKind.Debug);
            if (otherDebugTarget is not null && otherDebugTarget.DebugPort is int otherPort && otherPort != port)
            {
                return (null, Error(
                    $"启动后检测到端口为 {otherPort} 的调试实例，与请求端口 {port} 不同；{operationNotSent}。请在“管理实例”中选择窗口。",
                    endpoint));
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return (null, Error(
            $"等待 Codex 调试端口 {port} 就绪超时；{operationNotSent}。已启动的 Codex 会保持运行。",
            endpoint));
    }

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

    private static List<CodexInstance> GetRunningInstances(IReadOnlyList<CodexInstance> instances) =>
        instances.Where(instance => !instance.IsExitResidual).ToList();

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

        if (target.CanConnect && string.IsNullOrWhiteSpace(target.DebugEndpoint))
        {
            return $"调试实例 PID {target.Identity.ProcessId} 的监听端口已响应，但 Runtime 未返回经过身份核验的 Endpoint。请刷新实例列表后重试。";
        }

        return $"调试实例 PID {target.Identity.ProcessId} 声明了端口 {port}，但尚未核验到由该进程拥有的可连接端口。请刷新列表；若端口由其他进程占用，请先处理占用进程。";
    }

    private static List<CodexInstance> FindConnectableDebugTargets(IEnumerable<CodexInstance> instances, int port) =>
        instances.Where(instance => instance.Kind == CodexInstanceKind.Debug &&
                                    instance.CanConnect &&
                                    !string.IsNullOrWhiteSpace(instance.DebugEndpoint) &&
                                    !instance.IsExitResidual &&
                                    instance.DebugPort == port)
            .ToList();

    private static bool SameIdentity(CodexProcessIdentity left, CodexProcessIdentity right) =>
        left.ProcessId == right.ProcessId &&
        left.StartedAtUtc.UtcDateTime == right.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(left.ExecutablePath), Path.GetFullPath(right.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static RuntimeStatus Error(string message, string? endpoint = null) =>
        new(RuntimeStatusState.Error, null, endpoint, message, false);
}

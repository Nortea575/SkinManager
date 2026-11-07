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
        if (selectedTarget is not null)
        {
            return await ApplyToSelectedTargetAsync(request, port, selectedTarget, cancellationToken).ConfigureAwait(false);
        }

        if (hasActiveConnection)
        {
            var connectedRequest = activeConnectedInstance is null
                ? request
                : request with { ExpectedInstance = activeConnectedInstance };
            return await _apply(connectedRequest, cancellationToken).ConfigureAwait(false);
        }

        if (_instances is null)
        {
            return Error("无法核验 Codex 实例身份。请使用“管理实例”选择窗口后再应用。", request.Endpoint);
        }

        var instances = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var running = GetRunningInstances(instances);
        var matchingTargets = FindConnectableDebugTargets(running, port);
        var samePortTargets = running
            .Where(instance => instance.Kind == CodexInstanceKind.Debug && instance.DebugPort == port)
            .ToArray();
        if (samePortTargets.Length > 1)
        {
            return Error($"端口 {port} 对应多个 Codex 调试实例。请在“管理实例”中选择连接目标。", request.Endpoint);
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

    private async Task<RuntimeStatus> ApplyToSelectedTargetAsync(
        RuntimeApplyRequest request,
        int port,
        CodexInstance selectedTarget,
        CancellationToken cancellationToken)
    {
        if (_instances is null)
        {
            return Error("无法重新核验所选 Codex 实例。请刷新实例列表后重试。", request.Endpoint);
        }

        var latestInstances = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var latestTarget = latestInstances.FirstOrDefault(instance => SameIdentity(instance.Identity, selectedTarget.Identity));
        if (latestTarget is not { CanConnect: true, DebugPort: int selectedPort } ||
            selectedPort != port || latestTarget.Kind != CodexInstanceKind.Debug || latestTarget.IsExitResidual)
        {
            return Error(
                "所选窗口已退出或调试端口已变化，未发送皮肤脚本。请刷新实例列表并重新选择。",
                request.Endpoint);
        }

        return await ApplyToFreshTargetAsync(request, latestTarget, cancellationToken).ConfigureAwait(false);
    }

    private Task<RuntimeStatus> ApplyToFreshTargetAsync(
        RuntimeApplyRequest request,
        CodexInstance target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = target.DebugPort!.Value;
        var endpoint = $"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return _apply(request with
        {
            Endpoint = endpoint,
            ExpectedInstance = target.Identity
        }, cancellationToken);
    }

    private async Task<(CodexInstance? Target, RuntimeStatus? Error)> WaitForDebugTargetAsync(
        int port,
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (_instances is null)
        {
            return (null, Error("无法核验刚启动的 Codex 调试实例，未发送皮肤脚本。", endpoint));
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
                return (null, Error($"端口 {port} 对应多个调试实例，未发送皮肤脚本。请在“管理实例”中选择窗口。", endpoint));
            }

            if (matchingTargets.Count == 1)
            {
                return (matchingTargets[0], null);
            }

            if (running.Any(instance => instance.Kind == CodexInstanceKind.Normal))
            {
                return (null, Error(
                    "启动后检测到普通版 Codex，而不是调试版；未发送皮肤脚本。请在“管理实例”中检查当前窗口。",
                    endpoint));
            }

            var otherDebugTarget = running.FirstOrDefault(instance => instance.Kind == CodexInstanceKind.Debug);
            if (otherDebugTarget is not null && otherDebugTarget.DebugPort is int otherPort && otherPort != port)
            {
                return (null, Error(
                    $"启动后检测到端口为 {otherPort} 的调试实例，与请求端口 {port} 不同；未发送皮肤脚本。请在“管理实例”中选择窗口。",
                    endpoint));
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return (null, Error(
            $"等待 Codex 调试端口 {port} 就绪超时；未发送皮肤脚本。已启动的 Codex 会保持运行。",
            endpoint));
    }

    private static List<CodexInstance> GetRunningInstances(IReadOnlyList<CodexInstance> instances) =>
        instances.Where(instance => !instance.IsExitResidual).ToList();

    private static List<CodexInstance> FindConnectableDebugTargets(IEnumerable<CodexInstance> instances, int port) =>
        instances.Where(instance => instance.Kind == CodexInstanceKind.Debug &&
                                    instance.CanConnect &&
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

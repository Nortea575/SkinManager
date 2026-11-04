using System.Text.Json;

namespace SkinManager.Runtime;

internal sealed class RuntimeCoordinator : IAsyncDisposable
{
    private readonly ICodexEndpointIdentityVerifier _identityVerifier;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CdpRuntimeSession? _session;
    private RuntimeStatus _status = RuntimeStatus.Stopped();
    private readonly TaskCompletionSource _sessionTerminated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RuntimeCoordinator(ICodexEndpointIdentityVerifier identityVerifier) => _identityVerifier = identityVerifier;
    public Task SessionTerminated => _sessionTerminated.Task;
    public bool HasActiveSession => Volatile.Read(ref _session) is { IsConnected: true };

    public async Task<RuntimeStatus> HandleAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return command.Operation switch
            {
                AgentOperation.Apply => await ApplyAsync(command.ApplyRequest, cancellationToken).ConfigureAwait(false),
                AgentOperation.Remove => await EndSessionAsync("皮肤已移除并恢复当前 Codex 页面。", cancellationToken).ConfigureAwait(false),
                AgentOperation.Stop => await EndSessionAsync("皮肤连接已停止并恢复当前页面。Codex 调试端口仍由 Codex 进程管理，停止连接不会关闭它。", cancellationToken).ConfigureAwait(false),
                AgentOperation.GetStatus => await GetStatusAsync(cancellationToken).ConfigureAwait(false),
                _ => RuntimeStatus.Error("后台收到不支持的命令。")
            };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<RuntimeStatus> ApplyAsync(RuntimeApplyRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SkinId) || string.IsNullOrWhiteSpace(request.RuntimeScript) || string.IsNullOrWhiteSpace(request.RemoveScript))
            return SetStatus(RuntimeStatus.Error("Apply 请求缺少皮肤 ID、运行脚本或恢复脚本。"));
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint))
            return SetStatus(RuntimeStatus.Error("调试端点不是有效的绝对 URI。", request.SkinId, request.Endpoint));

        SetStatus(new RuntimeStatus(RuntimeStatusState.Connecting, request.SkinId, endpoint.ToString(), "正在确认 Codex 调试端点身份。", null));
        try
        {
            if (_session is { IsConnected: true } current && current.Endpoint == endpoint)
            {
                if (request.ExpectedInstance is not null && !SameInstance(current.ConnectedInstance, request.ExpectedInstance))
                    return SetStatus(new RuntimeStatus(RuntimeStatusState.Error, request.SkinId, endpoint.ToString(), "当前连接属于另一个 Codex 实例，未更新皮肤。请刷新实例列表后重试。", true, current.AppliedTargetCount, current.ConnectedInstance));
                SetStatus(new RuntimeStatus(RuntimeStatusState.Applying, request.SkinId, endpoint.ToString(), "正在更新当前皮肤。", true, current.AppliedTargetCount, current.ConnectedInstance));
                await current.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
                var updateMessage = current.AppliedTargetCount > 0
                    ? "皮肤已应用到已验证的 Codex 页面。"
                    : "连接已验证，Codex 主界面 DOM 标记尚未就绪；脚本会在标记出现后应用。";
                return SetStatus(new RuntimeStatus(RuntimeStatusState.Connected, request.SkinId, endpoint.ToString(), updateMessage, true, current.AppliedTargetCount, current.ConnectedInstance));
            }

            if (_session is not null)
            {
                try { await _session.RemoveAndStopAsync(cancellationToken).ConfigureAwait(false); }
                finally { await DisposeCurrentSessionAsync().ConfigureAwait(false); }
            }

            var newSession = await CdpRuntimeSession.ConnectAndApplyAsync(endpoint, request, _identityVerifier, cancellationToken, request.ExpectedInstance).ConfigureAwait(false);
            _session = newSession;
            newSession.AppliedTargetCountChanged += count =>
            {
                if (!ReferenceEquals(Volatile.Read(ref _session), newSession))
                    return;
                var current = Volatile.Read(ref _status);
                if (current.State is RuntimeStatusState.Error or RuntimeStatusState.Stopped)
                    return;
                var updatedMessage = count > 0
                    ? "皮肤已应用到包含 Codex DOM 标记的页面。"
                    : "连接已验证，尚无包含 Codex DOM 标记的页面；脚本会在主界面就绪后应用。";
                SetStatus(new RuntimeStatus(current.State, current.SkinId, current.Endpoint, updatedMessage, current.IsDebugPortOpen, count, current.ConnectedInstance));
            };
            newSession.Disconnected += message =>
            {
                if (ReferenceEquals(_session, newSession))
                {
                    SetStatus(RuntimeStatus.Error(message, request.SkinId, endpoint.ToString()));
                    _sessionTerminated.TrySetResult();
                }
            };
            if (!newSession.IsConnected)
            {
                var disconnectMessage = "Codex browser WebSocket 已断开；不会自动连接复用端口。";
                SetStatus(RuntimeStatus.Error(disconnectMessage, request.SkinId, endpoint.ToString()));
                _sessionTerminated.TrySetResult();
                await DisposeCurrentSessionAsync().ConfigureAwait(false);
                return Volatile.Read(ref _status);
            }
            var message = newSession.AppliedTargetCount > 0
                ? "已连接并应用皮肤。页面只在 app scheme 且包含 Codex DOM 标记时运行脚本。"
                : "连接已验证，尚无包含 Codex DOM 标记的页面；脚本会在主界面就绪后应用。";
            return SetStatus(new RuntimeStatus(RuntimeStatusState.Connected, request.SkinId, endpoint.ToString(), message, true, newSession.AppliedTargetCount, newSession.ConnectedInstance));
        }
        catch (OperationCanceledException)
        {
            return SetStatus(RuntimeStatus.Error("皮肤连接或应用已取消。", request.SkinId, endpoint.ToString()));
        }
        catch (Exception ex)
        {
            await DisposeCurrentSessionAsync().ConfigureAwait(false);
            return SetStatus(RuntimeStatus.Error(ex.Message, request.SkinId, endpoint.ToString()));
        }
    }

    private static bool SameInstance(CodexProcessIdentity? actual, CodexProcessIdentity expected) =>
        actual is not null && actual.ProcessId == expected.ProcessId &&
        actual.StartedAtUtc.UtcDateTime == expected.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(actual.ExecutablePath), Path.GetFullPath(expected.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private Task<RuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_session is not { IsConnected: true } && _status.State is RuntimeStatusState.Connected or RuntimeStatusState.Applying)
            return Task.FromResult(SetStatus(RuntimeStatus.Error("Codex browser WebSocket 已断开；不会自动连接复用端口。", _status.SkinId, _status.Endpoint)));
        return Task.FromResult(Volatile.Read(ref _status));
    }

    private async Task<RuntimeStatus> EndSessionAsync(string message, CancellationToken cancellationToken)
    {
        if (_session is null)
            return SetStatus(RuntimeStatus.Stopped(message));
        Exception? failure = null;
        try
        {
            await _session.RemoveAndStopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            await DisposeCurrentSessionAsync().ConfigureAwait(false);
        }
        return failure is null
            ? SetStatus(RuntimeStatus.Stopped(message))
            : SetStatus(RuntimeStatus.Error($"连接已断开，但恢复脚本执行失败：{failure.Message}"));
    }

    private RuntimeStatus SetStatus(RuntimeStatus status)
    {
        Volatile.Write(ref _status, status);
        return status;
    }

    private async Task DisposeCurrentSessionAsync()
    {
        var current = _session;
        _session = null;
        if (current is not null)
            await current.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            try { await _session.DisposeAsync().ConfigureAwait(false); }
            catch { }
            _session = null;
        }
        _operationGate.Dispose();
    }
}

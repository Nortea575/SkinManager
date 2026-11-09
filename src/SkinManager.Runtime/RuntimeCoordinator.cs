using System.Net.Http;

namespace SkinManager.Runtime;

internal sealed class RuntimeCoordinator : IAsyncDisposable
{
    private readonly ICodexEndpointIdentityVerifier _identityVerifier;
    private readonly LocaleOverrideSnapshotStore _snapshotStore;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CdpRuntimeSession? _session;
    private RuntimeStatus _status = RuntimeStatus.Stopped();
    private RuntimeCompatibilityOptions _compatibilityOptions = new();
    private bool _hasCompatibilityIntent;
    private readonly TaskCompletionSource _sessionTerminated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RuntimeCoordinator(ICodexEndpointIdentityVerifier identityVerifier, LocaleOverrideSnapshotStore? snapshotStore = null)
    {
        _identityVerifier = identityVerifier;
        _snapshotStore = snapshotStore ?? new LocaleOverrideSnapshotStore();
    }

    public Task SessionTerminated => _sessionTerminated.Task;
    public bool HasActiveSession => Volatile.Read(ref _session) is { IsConnected: true };
    public RuntimeCompatibilityResult? LastCompatibilityResult { get; private set; }

    public async Task<RuntimeStatus> HandleAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return command.Operation switch
            {
                AgentOperation.Apply => await ApplyAsync(command.ApplyRequest, cancellationToken).ConfigureAwait(false),
                AgentOperation.Remove => await RemoveSkinOrKeepCompatibilityAsync("皮肤已移除并恢复当前 Codex 页面。", cancellationToken).ConfigureAwait(false),
                AgentOperation.Stop => await EndSessionAsync("皮肤连接已停止并恢复当前页面。Codex 调试端口仍由 Codex 进程管理，停止连接不会关闭它。", cancellationToken).ConfigureAwait(false),
                AgentOperation.GetStatus => await GetStatusAsync(cancellationToken).ConfigureAwait(false),
                AgentOperation.ConfigureCompatibility => await ConfigureCompatibilityCommandAsync(command.CompatibilityRequest, cancellationToken).ConfigureAwait(false),
                _ => RuntimeStatus.Error("后台收到不支持的命令。")
            };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RuntimeCompatibilityResult> ConfigureCompatibilityAsync(RuntimeCompatibilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await TryConfigureCompatibilityCoreAsync(request, cancellationToken).ConfigureAwait(false);
            LastCompatibilityResult = result;
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<RuntimeStatus> ConfigureCompatibilityCommandAsync(RuntimeCompatibilityRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            LastCompatibilityResult = new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, Message: "ConfigureCompatibility 请求缺少设置。 ");
            return Volatile.Read(ref _status);
        }
        LastCompatibilityResult = await TryConfigureCompatibilityCoreAsync(request, cancellationToken).ConfigureAwait(false);
        return Volatile.Read(ref _status);
    }

    private async Task<RuntimeStatus> ApplyAsync(RuntimeApplyRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SkinId) || string.IsNullOrWhiteSpace(request.RuntimeScript) || string.IsNullOrWhiteSpace(request.RemoveScript))
            return SetStatus(RuntimeStatus.Error("Apply 请求缺少皮肤 ID、运行脚本或恢复脚本。"));
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint))
            return SetStatus(RuntimeStatus.Error("调试端点不是有效的绝对 URI。", request.SkinId, request.Endpoint));

        var priorSession = _session;
        SetStatus(new RuntimeStatus(RuntimeStatusState.Connecting, request.SkinId, endpoint.ToString(), "正在确认 Codex 调试端点身份。", null));
        try
        {
            if (_session is { IsConnected: true } current && current.Endpoint == endpoint)
            {
                if (request.ExpectedInstance is not null && !SameInstance(current.ConnectedInstance, request.ExpectedInstance))
                    return SetStatus(new RuntimeStatus(RuntimeStatusState.Error, request.SkinId, endpoint.ToString(), "当前连接属于另一个 Codex 实例，未更新皮肤。请刷新实例列表后重试。", true, current.AppliedTargetCount, current.ConnectedInstance));
                if (request.Compatibility is not null)
                {
                    _compatibilityOptions = request.Compatibility;
                    _hasCompatibilityIntent = true;
                }
                SetStatus(new RuntimeStatus(RuntimeStatusState.Applying, request.SkinId, endpoint.ToString(), "正在更新当前皮肤。", true, current.AppliedTargetCount, current.ConnectedInstance));
                await current.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
                if (request.Compatibility is not null)
                {
                    LastCompatibilityResult = await TryConfigureCompatibilityCoreAsync(
                        new RuntimeCompatibilityRequest { Endpoint = endpoint.ToString(), ExpectedInstance = request.ExpectedInstance, Options = request.Compatibility },
                        cancellationToken).ConfigureAwait(false);
                }
                else if (_hasCompatibilityIntent && !_compatibilityOptions.ForceSimplifiedChinese)
                {
                    try
                    {
                        if (_snapshotStore.Load() is not null)
                            LastCompatibilityResult = await TryConfigureCompatibilityCoreAsync(
                                new RuntimeCompatibilityRequest { Endpoint = endpoint.ToString(), ExpectedInstance = request.ExpectedInstance, Options = _compatibilityOptions },
                                cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                    {
                        LastCompatibilityResult = new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), $"读取语言恢复状态失败：{ex.Message}", true, current.ConnectedInstance);
                    }
                }
                if (!current.IsConnected)
                    return SetStatus(RuntimeStatus.Stopped("Codex browser WebSocket 已断开；皮肤连接已停止。"));
                var updateMessage = current.AppliedTargetCount > 0
                    ? "皮肤已应用到已验证的 Codex 页面。"
                    : "连接已验证，Codex 主界面 DOM 标记尚未就绪；脚本会在标记出现后应用。";
                return SetStatus(new RuntimeStatus(RuntimeStatusState.Connected, request.SkinId, endpoint.ToString(), updateMessage, true, current.AppliedTargetCount, current.ConnectedInstance, LastCompatibilityResult));
            }

            if (_session is { IsConnected: false })
            {
                await DisposeCurrentSessionAsync().ConfigureAwait(false);
                SetStatus(RuntimeStatus.Stopped("Codex browser WebSocket 已断开；旧皮肤连接已清理。"));
            }
            else if (_session is not null)
            {
                try { await _session.RemoveAndStopAsync(cancellationToken).ConfigureAwait(false); }
                finally { await DisposeCurrentSessionAsync().ConfigureAwait(false); }
            }

            var sessionOptions = request.Compatibility ?? _compatibilityOptions;
            if (request.Compatibility is not null)
            {
                _compatibilityOptions = request.Compatibility;
                _hasCompatibilityIntent = true;
            }
            var newSession = await CdpRuntimeSession.ConnectAsync(endpoint, request, sessionOptions, _identityVerifier, cancellationToken, request.ExpectedInstance).ConfigureAwait(false);
            _session = newSession;
            AttachSessionHandlers(newSession, request.SkinId, endpoint);
            if (!newSession.IsConnected)
            {
                var disconnectMessage = "Codex browser WebSocket 已断开；不会自动连接复用端口。";
                SetStatus(RuntimeStatus.Stopped(disconnectMessage));
                _sessionTerminated.TrySetResult();
                await DisposeCurrentSessionAsync().ConfigureAwait(false);
                return Volatile.Read(ref _status);
            }

            var restoreSnapshotExists = false;
            if (_hasCompatibilityIntent && !sessionOptions.ForceSimplifiedChinese)
            {
                try { restoreSnapshotExists = _snapshotStore.Load() is not null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                {
                    LastCompatibilityResult = new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), $"读取语言恢复状态失败：{ex.Message}", true, newSession.ConnectedInstance);
                }
            }

            if (request.Compatibility is not null || sessionOptions.ForceSimplifiedChinese || restoreSnapshotExists)
            {
                var compatibilityOptions = request.Compatibility ?? sessionOptions;
                LastCompatibilityResult = await TryConfigureCompatibilityCoreAsync(
                    new RuntimeCompatibilityRequest { Endpoint = endpoint.ToString(), ExpectedInstance = request.ExpectedInstance, Options = compatibilityOptions },
                    cancellationToken).ConfigureAwait(false);
            }

            if (!newSession.IsConnected)
                return SetStatus(RuntimeStatus.Stopped("Codex browser WebSocket 已断开；皮肤连接已停止。"));

            var message = newSession.AppliedTargetCount > 0
                ? "已连接并应用皮肤。页面只在 app scheme 且包含 Codex DOM 标记时运行脚本。"
                : "连接已验证，尚无包含 Codex DOM 标记的页面；脚本会在主界面就绪后应用。";
            return SetStatus(new RuntimeStatus(RuntimeStatusState.Connected, request.SkinId, endpoint.ToString(), message, true, newSession.AppliedTargetCount, newSession.ConnectedInstance, LastCompatibilityResult));
        }
        catch (OperationCanceledException)
        {
            return SetStatus(RuntimeStatus.Error("皮肤连接或应用已取消。", request.SkinId, endpoint.ToString()));
        }
        catch (Exception ex)
        {
            var browserDisconnected = _session is { IsConnected: false };
            await DisposeCurrentSessionAsync().ConfigureAwait(false);
            if (browserDisconnected)
                return SetStatus(RuntimeStatus.Stopped("Codex browser WebSocket 已断开；旧皮肤连接已清理。"));
            if (priorSession is null || ReferenceEquals(priorSession, _session))
                return SetStatus(RuntimeStatus.Error(ex.Message, request.SkinId, endpoint.ToString()));
            return SetStatus(RuntimeStatus.Error(ex.Message, request.SkinId, endpoint.ToString()));
        }
    }

    private async Task<RuntimeCompatibilityResult> TryConfigureCompatibilityCoreAsync(RuntimeCompatibilityRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await ConfigureCompatibilityCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.TimedOut, request.Endpoint, "语言设置操作超时或被取消。", _session is { IsConnected: true }, _session?.ConnectedInstance);
        }
        catch (Exception ex)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, request.Endpoint, $"语言设置操作失败：{ex.Message}", _session is { IsConnected: true }, _session?.ConnectedInstance);
        }
    }

    private async Task<RuntimeCompatibilityResult> ConfigureCompatibilityCoreAsync(RuntimeCompatibilityRequest request, CancellationToken cancellationToken)
    {
        var options = request.Options ?? new RuntimeCompatibilityOptions();
        Uri endpoint;
        try
        {
            if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out endpoint!))
                throw new ArgumentException("调试端点不是有效的绝对 URI。", nameof(request));
            CdpBrowserClient.ValidateEndpoint(endpoint);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, request.Endpoint, ex.Message);
        }

        if (_session is { IsConnected: false })
        {
            await DisposeCurrentSessionAsync().ConfigureAwait(false);
            SetStatus(RuntimeStatus.Stopped("Codex browser WebSocket 已断开；旧连接已清理。"));
        }

        if (_session is { IsConnected: true, HasSkin: true } pinnedSession && pinnedSession.Endpoint != endpoint)
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), "兼容设置不能切换到另一个 Codex 端点；请先让当前皮肤连接离开该实例。", true, pinnedSession.ConnectedInstance);
        if (_session is { IsConnected: true } sameEndpointSession && sameEndpointSession.Endpoint == endpoint &&
            request.ExpectedInstance is not null && !SameInstance(sameEndpointSession.ConnectedInstance, request.ExpectedInstance))
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), "当前连接属于另一个 Codex 实例，未修改语言设置。", true, sameEndpointSession.ConnectedInstance);

        LocaleOverrideRestoreSnapshot? pendingSnapshot;
        try { pendingSnapshot = _snapshotStore.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), $"读取语言恢复状态失败：{ex.Message}", _session is { IsConnected: true }, _session?.ConnectedInstance);
        }

        _compatibilityOptions = options;
        _hasCompatibilityIntent = true;

        if (!options.ForceSimplifiedChinese && pendingSnapshot is null && _session is null)
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Disabled, endpoint.ToString(), "简体中文固定模式已关闭。", null, null);

        if (_session is { IsConnected: true } existing && existing.Endpoint == endpoint)
        {
            var existingResult = await existing.ConfigureCompatibilityAsync(options, _snapshotStore, cancellationToken).ConfigureAwait(false);
            if (!options.ForceSimplifiedChinese && existingResult.State == RuntimeCompatibilityState.Disabled && !existing.HasSkin)
            {
                await StopAndDisposeCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(RuntimeStatus.Stopped("简体中文固定模式已关闭。"));
                return existingResult;
            }
            UpdateConnectedStatus(existing, existingResult.Message);
            return existingResult;
        }

        if (_session is { IsConnected: true } otherSession && !otherSession.HasSkin)
        {
            try { await otherSession.RemoveAndStopAsync(cancellationToken).ConfigureAwait(false); }
            finally { await DisposeCurrentSessionAsync().ConfigureAwait(false); }
        }
        if (_session is null && !options.ForceSimplifiedChinese && pendingSnapshot is null)
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Disabled, endpoint.ToString(), "简体中文固定模式已关闭。", null, null);

        CdpRuntimeSession session;
        try
        {
            session = await CdpRuntimeSession.ConnectAsync(endpoint, null, options, _identityVerifier, cancellationToken, request.ExpectedInstance).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.TimedOut, endpoint.ToString(), "连接 Codex 语言设置宿主超时。", null, null);
        }
        catch (HttpRequestException ex)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Pending, endpoint.ToString(), $"等待 Codex 调试端口就绪：{ex.Message}", false, null);
        }
        catch (TimeoutException ex)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Pending, endpoint.ToString(), $"等待 Codex 调试端口就绪：{ex.Message}", false, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, endpoint.ToString(), ex.Message, true, null);
        }

        _session = session;
        AttachSessionHandlers(session, null, endpoint);
        var result = await TryConfigureOnSessionAsync(session, options, cancellationToken).ConfigureAwait(false);
        if (!options.ForceSimplifiedChinese && result.State == RuntimeCompatibilityState.Disabled && !session.HasSkin)
        {
            await StopAndDisposeCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(RuntimeStatus.Stopped("简体中文固定模式已关闭。"));
            return result;
        }

        UpdateConnectedStatus(session, result.Message);
        return result;
    }

    private async Task<RuntimeCompatibilityResult> TryConfigureOnSessionAsync(CdpRuntimeSession session, RuntimeCompatibilityOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await session.ConfigureCompatibilityAsync(options, _snapshotStore, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.TimedOut, session.Endpoint.ToString(), "语言设置操作超时或被取消。", session.IsConnected, session.ConnectedInstance);
        }
        catch (Exception ex)
        {
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, session.Endpoint.ToString(), $"语言设置操作失败：{ex.Message}", session.IsConnected, session.ConnectedInstance);
        }
    }

    private void AttachSessionHandlers(CdpRuntimeSession session, string? skinId, Uri endpoint)
    {
        session.AppliedTargetCountChanged += count =>
        {
            if (!ReferenceEquals(Volatile.Read(ref _session), session))
                return;
            var current = Volatile.Read(ref _status);
            if (current.State is RuntimeStatusState.Error or RuntimeStatusState.Stopped)
                return;
            var updatedMessage = count > 0
                ? "皮肤已应用到包含 Codex DOM 标记的页面。"
                : session.HasSkin
                    ? "连接已验证，尚无包含 Codex DOM 标记的页面；脚本会在主界面就绪后应用。"
                    : "Codex 语言兼容连接仍在运行。";
            SetStatus(new RuntimeStatus(current.State, session.HasSkin ? current.SkinId ?? skinId : null, endpoint.ToString(), updatedMessage, current.IsDebugPortOpen, count, session.ConnectedInstance, current.CompatibilityResult));
        };
        session.Disconnected += message =>
        {
            if (ReferenceEquals(_session, session))
            {
                SetStatus(RuntimeStatus.Stopped(message));
                _sessionTerminated.TrySetResult();
            }
        };
        session.CompatibilityResultChanged += result =>
        {
            if (!ReferenceEquals(Volatile.Read(ref _session), session))
                return;
            LastCompatibilityResult = result;
            var current = Volatile.Read(ref _status);
            if (current.State is RuntimeStatusState.Error or RuntimeStatusState.Stopped)
                return;
            var message = session.HasSkin ? current.Message : result.Message;
            SetStatus(new RuntimeStatus(current.State, session.HasSkin ? current.SkinId : null, endpoint.ToString(), message,
                current.IsDebugPortOpen, session.AppliedTargetCount, session.ConnectedInstance, result));
            if (result.State == RuntimeCompatibilityState.Disabled && !session.HasSkin && !_compatibilityOptions.ForceSimplifiedChinese)
                _ = StopIdleCompatibilitySessionAsync(session);
        };
    }

    private async Task StopIdleCompatibilitySessionAsync(CdpRuntimeSession session)
    {
        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_session, session) || session.HasSkin || _compatibilityOptions.ForceSimplifiedChinese || _snapshotStore.Load() is not null)
                    return;
                await StopAndDisposeCurrentSessionAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus(RuntimeStatus.Stopped("原语言已恢复，空闲语言连接已停止。"));
                _sessionTerminated.TrySetResult();
            }
            finally { _operationGate.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    private void UpdateConnectedStatus(CdpRuntimeSession session, string? message)
    {
        if (!session.IsConnected)
        {
            SetStatus(RuntimeStatus.Stopped("Codex browser WebSocket 已断开；皮肤连接已停止。"));
            return;
        }
        var current = Volatile.Read(ref _status);
        var statusMessage = message ?? (session.HasSkin ? current.Message : "Codex 语言兼容连接仍在运行。");
        SetStatus(new RuntimeStatus(RuntimeStatusState.Connected, session.HasSkin ? current.SkinId : null, session.Endpoint.ToString(), statusMessage, true, session.AppliedTargetCount, session.ConnectedInstance, LastCompatibilityResult));
    }

    private async Task<RuntimeStatus> RemoveSkinOrKeepCompatibilityAsync(string message, CancellationToken cancellationToken)
    {
        if (_session is null)
            return SetStatus(RuntimeStatus.Stopped(message));
        if (!_session.IsConnected)
        {
            await DisposeCurrentSessionAsync().ConfigureAwait(false);
            return SetStatus(RuntimeStatus.Stopped(message));
        }

        if (!_hasCompatibilityIntent)
            return await EndSessionAsync(message, cancellationToken).ConfigureAwait(false);

        bool hasPendingRestore;
        try { hasPendingRestore = _snapshotStore.Load() is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            hasPendingRestore = true;
        }

        if (!_compatibilityOptions.ForceSimplifiedChinese && !hasPendingRestore)
            return await EndSessionAsync(message, cancellationToken).ConfigureAwait(false);

        try
        {
            await _session.RemoveSkinAsync(cancellationToken).ConfigureAwait(false);
            if (!_compatibilityOptions.ForceSimplifiedChinese && hasPendingRestore)
                LastCompatibilityResult = await TryConfigureOnSessionAsync(_session, _compatibilityOptions, cancellationToken).ConfigureAwait(false);
            if (!_compatibilityOptions.ForceSimplifiedChinese && LastCompatibilityResult?.State == RuntimeCompatibilityState.Disabled)
                return await EndSessionAsync(message, cancellationToken).ConfigureAwait(false);
            var connected = Volatile.Read(ref _session);
            if (connected is null || !connected.IsConnected)
            {
                await DisposeCurrentSessionAsync().ConfigureAwait(false);
                return SetStatus(RuntimeStatus.Stopped(message));
            }
            return SetStatus(new RuntimeStatus(RuntimeStatusState.Connected, null, connected.Endpoint.ToString(),
                _compatibilityOptions.ForceSimplifiedChinese
                    ? "皮肤已断开，简体中文固定设置保持启用。"
                    : LastCompatibilityResult?.State == RuntimeCompatibilityState.Pending
                        ? "皮肤已断开；原语言恢复仍待 Codex 设置宿主就绪。"
                        : "皮肤已移除，正在完成语言设置恢复。",
                true, 0, connected.ConnectedInstance, LastCompatibilityResult));
        }
        catch (Exception ex)
        {
            if (_session is { IsConnected: false })
            {
                await DisposeCurrentSessionAsync().ConfigureAwait(false);
                return SetStatus(RuntimeStatus.Stopped("Codex browser WebSocket 已断开；皮肤连接已停止。"));
            }
            return SetStatus(RuntimeStatus.Error($"移除皮肤或处理语言兼容连接失败：{ex.Message}", _status.SkinId, _status.Endpoint));
        }
    }

    private static bool SameInstance(CodexProcessIdentity? actual, CodexProcessIdentity expected) =>
        actual is not null && actual.ProcessId == expected.ProcessId &&
        actual.StartedAtUtc.UtcDateTime == expected.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(actual.ExecutablePath), Path.GetFullPath(expected.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private Task<RuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_session is { IsConnected: false })
            return Task.FromResult(SetStatus(RuntimeStatus.Stopped("Codex browser WebSocket 已断开；皮肤连接已停止。")));
        return Task.FromResult(Volatile.Read(ref _status));
    }

    private async Task<RuntimeStatus> EndSessionAsync(string message, CancellationToken cancellationToken)
    {
        if (_session is null)
            return SetStatus(RuntimeStatus.Stopped(message));
        if (!_session.IsConnected)
        {
            await DisposeCurrentSessionAsync().ConfigureAwait(false);
            return SetStatus(RuntimeStatus.Stopped(message));
        }
        Exception? failure = null;
        var disconnectedDuringEnd = false;
        try
        {
            await _session.RemoveAndStopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            disconnectedDuringEnd = _session is { IsConnected: false };
        }
        finally
        {
            await DisposeCurrentSessionAsync().ConfigureAwait(false);
        }
        return failure is null || disconnectedDuringEnd
            ? SetStatus(RuntimeStatus.Stopped(message))
            : SetStatus(RuntimeStatus.Error($"连接已断开，但恢复脚本执行失败：{failure.Message}"));
    }

    private async Task StopAndDisposeCurrentSessionAsync(CancellationToken cancellationToken)
    {
        var current = _session;
        if (current is null)
            return;
        try { await current.RemoveAndStopAsync(cancellationToken).ConfigureAwait(false); }
        finally { await DisposeCurrentSessionAsync().ConfigureAwait(false); }
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

using System.Text.Json;
using SkinManager.Codex;

namespace SkinManager.Runtime;

internal sealed class CdpRuntimeSession : IAsyncDisposable
{
    private readonly CdpBrowserClient _browser;
    private readonly SemaphoreSlim _targetGate = new(1, 1);
    private readonly Dictionary<string, CdpTarget> _targets = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _eventPump;
    private readonly string _bindingName;
    private RuntimeApplyRequest _activeRequest;
    private int _appliedTargetCount;
    private bool _stopping;
    private bool _hadCodexTarget;
    private int _disposed;

    public bool IsConnected => !_stopping && _browser.IsConnected;
    public Uri Endpoint { get; }
    public int AppliedTargetCount => Volatile.Read(ref _appliedTargetCount);
    public CodexProcessIdentity? ConnectedInstance => _browser.ConnectedInstance;
    public event Action<string>? Disconnected;
    public event Action<int>? AppliedTargetCountChanged;

    private CdpRuntimeSession(Uri endpoint, CdpBrowserClient browser, RuntimeApplyRequest request)
    {
        Endpoint = endpoint;
        _browser = browser;
        _activeRequest = request;
        _bindingName = "__skinManagerApplied_" + Guid.NewGuid().ToString("N");
        _eventPump = PumpEventsAsync();
    }

    public static async Task<CdpRuntimeSession> ConnectAndApplyAsync(
        Uri endpoint,
        RuntimeApplyRequest request,
        ICodexEndpointIdentityVerifier identityVerifier,
        CancellationToken cancellationToken,
        CodexProcessIdentity? expectedInstance = null)
    {
        expectedInstance ??= request.ExpectedInstance;
        var browser = await CdpBrowserClient.ConnectAsync(endpoint, identityVerifier, cancellationToken, expectedInstance).ConfigureAwait(false);
        var session = new CdpRuntimeSession(endpoint, browser, request);
        try
        {
            await browser.SendAsync("Target.setDiscoverTargets", new { discover = true }, cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = await browser.SendAsync("Target.getTargets", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.TryGetProperty("targetInfos", out var infos) && infos.ValueKind == JsonValueKind.Array)
            {
                foreach (var targetInfo in infos.EnumerateArray())
                    await session.HandleTargetInfoAsync(targetInfo, cancellationToken).ConfigureAwait(false);
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpdateAsync(RuntimeApplyRequest request, CancellationToken cancellationToken)
    {
        await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            var nextToken = Guid.NewGuid().ToString("N");
            foreach (var target in _targets.Values.ToArray())
            {
                if (!target.IsCodexAppUrl)
                    continue;
                await RemoveEarlyScriptAsync(target, cancellationToken).ConfigureAwait(false);
                await ClearGuardAsync(target, target.RuntimeToken, cancellationToken).ConfigureAwait(false);
                target.RuntimeToken = nextToken;
                target.IsApplied = await InstallRuntimeAsync(target, request.RuntimeScript, cancellationToken).ConfigureAwait(false);
            }
            _activeRequest = request;
        }
        finally
        {
            RecountAppliedTargets();
            _targetGate.Release();
        }
    }

    public async Task RemoveAndStopAsync(CancellationToken cancellationToken)
    {
        await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping)
                return;
            _stopping = true;
            var failures = new List<Exception>();
            var destroyedTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in _targets.Values.ToArray())
            {
                try { await RemoveEarlyScriptAsync(target, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (IsDestroyedTargetFailure(ex)) { destroyedTargets.Add(target.TargetId); }
                catch (Exception ex) { failures.Add(ex); }
            }
            foreach (var target in _targets.Values.ToArray())
            {
                if (destroyedTargets.Contains(target.TargetId) || !target.IsCodexAppUrl)
                    continue;
                try
                {
                    await EvaluateGuardedScriptAsync(target, _activeRequest.RemoveScript, target.RuntimeToken, cancellationToken, isRemove: true).ConfigureAwait(false);
                    target.IsApplied = false;
                }
                catch (Exception ex) when (IsDestroyedTargetFailure(ex)) { destroyedTargets.Add(target.TargetId); }
                catch (Exception ex) { failures.Add(ex); }
            }
            foreach (var target in _targets.Values.ToArray())
            {
                if (destroyedTargets.Contains(target.TargetId) || target.SessionId is null || !_browser.IsConnected)
                    continue;
                if (target.EarlyScriptId is not null)
                    continue;
                try
                {
                    await _browser.SendAsync("Target.detachFromTarget", new { sessionId = target.SessionId }, cancellationToken: cancellationToken).ConfigureAwait(false);
                    target.SessionId = null;
                }
                catch (Exception ex) when (IsDestroyedTargetFailure(ex)) { destroyedTargets.Add(target.TargetId); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (_browser.IsConnected)
            {
                try { await _browser.SendAsync("Target.setDiscoverTargets", new { discover = false }, cancellationToken: cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { failures.Add(ex); }
            }
            foreach (var targetId in _targets.Values.Where(target => target.EarlyScriptId is null).Select(target => target.TargetId).ToArray())
                _targets.Remove(targetId);
            RecountAppliedTargets();
            if (failures.Count > 0)
                throw new AggregateException("一个或多个 Codex 页面未能完全移除皮肤。", failures);
        }
        finally
        {
            _targetGate.Release();
        }
    }

    private async Task PumpEventsAsync()
    {
        try
        {
            await foreach (var cdpEvent in _browser.Events.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await HandleEventAsync(cdpEvent, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsDestroyedTargetFailure(ex))
                {
                    await ForgetTargetAsync(cdpEvent, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FailBrowserSession(ex);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FailBrowserSession(ex);
        }
    }

    private async Task HandleEventAsync(CdpEvent cdpEvent, CancellationToken cancellationToken)
    {
        switch (cdpEvent.Method)
        {
            case "Target.targetCreated":
            case "Target.targetInfoChanged":
                if (cdpEvent.Parameters.TryGetProperty("targetInfo", out var targetInfo))
                    await HandleTargetInfoAsync(targetInfo, cancellationToken).ConfigureAwait(false);
                break;
            case "Target.targetDestroyed":
                if (cdpEvent.Parameters.TryGetProperty("targetId", out var destroyedId) && destroyedId.ValueKind == JsonValueKind.String)
                {
                    await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        _targets.Remove(destroyedId.GetString()!);
                        RecountAppliedTargets();
                        await NotifyIfNoCodexTargetsRemainAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally { _targetGate.Release(); }
                }
                break;
            case "Target.detachedFromTarget":
                if (cdpEvent.Parameters.TryGetProperty("sessionId", out var detachedId) && detachedId.ValueKind == JsonValueKind.String)
                    await ForgetTargetSessionAsync(detachedId.GetString()!, cancellationToken).ConfigureAwait(false);
                break;
            case "Runtime.bindingCalled":
                await HandleAppliedBindingAsync(cdpEvent, cancellationToken).ConfigureAwait(false);
                break;
            case "Page.loadEventFired":
                if (cdpEvent.SessionId is not null)
                    await ReevaluateTargetAsync(cdpEvent.SessionId, cancellationToken).ConfigureAwait(false);
                break;
            case "Runtime.executionContextCreated":
                if (cdpEvent.SessionId is not null && IsMainDefaultContext(cdpEvent.Parameters))
                    await ReevaluateTargetAsync(cdpEvent.SessionId, cancellationToken).ConfigureAwait(false);
                break;
            case "Page.frameNavigated":
                if (cdpEvent.SessionId is not null && IsMainFrameNavigation(cdpEvent.Parameters))
                    await ReevaluateTargetAsync(cdpEvent.SessionId, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleTargetInfoAsync(JsonElement info, CancellationToken cancellationToken)
    {
        var targetId = ReadString(info, "targetId");
        if (string.IsNullOrWhiteSpace(targetId))
            return;
        var targetType = ReadString(info, "type");
        var url = ReadString(info, "url") ?? string.Empty;
        var isCandidate = string.Equals(targetType, "page", StringComparison.Ordinal) && IsCodexAppUrl(url);

        await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            if (_targets.TryGetValue(targetId, out var existing))
            {
                existing.Url = url;
                if (isCandidate && existing.SessionId is not null)
                {
                    if (existing.EarlyScriptId is null)
                    {
                        existing.RuntimeToken = Guid.NewGuid().ToString("N");
                        existing.IsApplied = await InstallRuntimeAsync(existing, _activeRequest.RuntimeScript, cancellationToken).ConfigureAwait(false);
                    }
                    RecountAppliedTargets();
                    return;
                }
                if (!isCandidate)
                {
                    if (existing.SessionId is not null)
                        await DetachTargetAsync(existing, cancellationToken).ConfigureAwait(false);
                    _targets.Remove(targetId);
                    RecountAppliedTargets();
                    await NotifyIfNoCodexTargetsRemainAsync(cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            if (!isCandidate)
                return;

            var attached = await _browser.SendAsync("Target.attachToTarget", new { targetId, flatten = true }, cancellationToken: cancellationToken).ConfigureAwait(false);
            var sessionId = ReadString(attached, "sessionId") ?? throw new InvalidOperationException("CDP 未返回页面 sessionId。 ");
            var target = new CdpTarget(targetId, sessionId, url, Guid.NewGuid().ToString("N"));
            _hadCodexTarget = true;
            _targets[targetId] = target;
            await _browser.SendAsync("Page.enable", sessionId: sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _browser.SendAsync("Runtime.enable", sessionId: sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _browser.SendAsync("Runtime.addBinding", new { name = _bindingName }, sessionId, cancellationToken).ConfigureAwait(false);
            target.IsApplied = await InstallRuntimeAsync(target, _activeRequest.RuntimeScript, cancellationToken).ConfigureAwait(false);
            RecountAppliedTargets();
        }
        finally
        {
            _targetGate.Release();
        }
    }

    private async Task ReevaluateTargetAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = _targets.Values.FirstOrDefault(candidate => candidate.SessionId == sessionId);
            if (target is null || !target.IsCodexAppUrl || _stopping)
                return;
            target.IsApplied = await ProbeAppliedStateAsync(target, cancellationToken).ConfigureAwait(false);
            RecountAppliedTargets();
        }
        finally
        {
            _targetGate.Release();
        }
    }

    private async Task<bool> ProbeAppliedStateAsync(CdpTarget target, CancellationToken cancellationToken)
    {
        var expression = "(()=>location.protocol==='app:'&&(location.hostname==='codex'||location.hostname==='-')&&Boolean(document.querySelector('" +
            CodexHostContract.AppFrameSelector + "')&&document.querySelector('" + CodexHostContract.MainSurfaceSelector + "')&&window[" +
            JsonSerializer.Serialize("__skinManagerApplied_" + target.RuntimeToken) + "]===true))()";
        var result = await _browser.SendAsync("Runtime.evaluate", new { expression, returnByValue = true }, target.SessionId, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("exceptionDetails", out var exceptionDetails))
        {
            var text = ReadString(exceptionDetails, "text") ?? "JavaScript 状态查询失败";
            throw new InvalidOperationException($"Codex 页面状态查询失败：{text}");
        }
        return result.TryGetProperty("result", out var remoteObject) && remoteObject.TryGetProperty("value", out var applied) && applied.ValueKind == JsonValueKind.True;
    }

    private async Task ClearGuardAsync(CdpTarget target, string token, CancellationToken cancellationToken)
    {
        var key = JsonSerializer.Serialize("__skinManagerApplied_" + token);
        var observerKey = JsonSerializer.Serialize("__skinManagerObserver_" + token);
        var expression = "(()=>{if(location.protocol!=='app:'||(location.hostname!=='codex'&&location.hostname!=='-'))return false;const observer=window[" + observerKey + "];if(observer){observer.disconnect();delete window[" + observerKey + "];}delete window[" + key + "];return true;})()";
        var result = await _browser.SendAsync("Runtime.evaluate", new { expression, returnByValue = true }, target.SessionId, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("exceptionDetails", out var exceptionDetails))
            throw new InvalidOperationException($"无法清理旧页面 guard：{ReadString(exceptionDetails, "text") ?? "JavaScript 状态清理失败"}");
    }

    private async Task HandleAppliedBindingAsync(CdpEvent cdpEvent, CancellationToken cancellationToken)
    {
        if (cdpEvent.SessionId is null || ReadString(cdpEvent.Parameters, "name") != _bindingName)
            return;
        await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = _targets.Values.FirstOrDefault(candidate => candidate.SessionId == cdpEvent.SessionId);
            if (target is null || !target.IsCodexAppUrl || ReadString(cdpEvent.Parameters, "payload") != target.RuntimeToken)
                return;
            target.IsApplied = true;
            RecountAppliedTargets();
        }
        finally
        {
            _targetGate.Release();
        }
    }

    private async Task<bool> InstallRuntimeAsync(CdpTarget target, string runtimeScript, CancellationToken cancellationToken)
    {
        var installed = await _browser.SendAsync(
            "Page.addScriptToEvaluateOnNewDocument",
            new { source = BuildGuardedScript(runtimeScript, target.RuntimeToken, _bindingName), runImmediately = false },
            target.SessionId,
            cancellationToken).ConfigureAwait(false);
        target.EarlyScriptId = ReadString(installed, "identifier") ?? throw new InvalidOperationException("CDP 未返回 early-script identifier。 ");
        return await EvaluateGuardedScriptAsync(target, runtimeScript, target.RuntimeToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveEarlyScriptAsync(CdpTarget target, CancellationToken cancellationToken)
    {
        if (target.EarlyScriptId is null || !_browser.IsConnected)
            return;
        var id = target.EarlyScriptId;
        await _browser.SendAsync("Page.removeScriptToEvaluateOnNewDocument", new { identifier = id }, target.SessionId, cancellationToken).ConfigureAwait(false);
        target.EarlyScriptId = null;
    }

    private async Task<bool> EvaluateGuardedScriptAsync(CdpTarget target, string script, string token, CancellationToken cancellationToken, bool isRemove = false)
    {
        var result = await _browser.SendAsync(
            "Runtime.evaluate",
            new { expression = BuildGuardedScript(script, token, _bindingName, isRemove), returnByValue = true, awaitPromise = true },
            target.SessionId,
            cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("exceptionDetails", out var exceptionDetails))
        {
            var text = ReadString(exceptionDetails, "text") ?? "JavaScript 运行异常";
            if (exceptionDetails.TryGetProperty("exception", out var exception) && ReadString(exception, "description") is { Length: > 0 } description)
                text = description;
            throw new InvalidOperationException($"Codex 页面脚本执行失败：{text}");
        }
        return result.TryGetProperty("result", out var remoteObject) &&
            remoteObject.TryGetProperty("value", out var appliedValue) &&
            appliedValue.ValueKind == JsonValueKind.True;
    }

    private async Task DetachTargetAsync(CdpTarget target, CancellationToken cancellationToken)
    {
        if (target.SessionId is null)
            return;
        await RemoveEarlyScriptAsync(target, cancellationToken).ConfigureAwait(false);
        if (target.IsCodexAppUrl && target.RuntimeToken is not null)
            await EvaluateGuardedScriptAsync(target, _activeRequest.RemoveScript, target.RuntimeToken, cancellationToken, isRemove: true).ConfigureAwait(false);
        await _browser.SendAsync("Target.detachFromTarget", new { sessionId = target.SessionId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        target.SessionId = null;
        target.IsApplied = false;
        RecountAppliedTargets();
    }

    private static string BuildGuardedScript(string script, string token, string bindingName, bool isRemove = false)
    {
        var quotedToken = JsonSerializer.Serialize("__skinManagerApplied_" + token);
        var observerToken = JsonSerializer.Serialize("__skinManagerObserver_" + token);
        var quotedBindingName = JsonSerializer.Serialize(bindingName);
        var quotedAppliedToken = JsonSerializer.Serialize(token);
        var appHostGuard = "(location.hostname==='codex'||location.hostname==='-')";
        var documentGuard = "location.protocol==='app:'&&" + appHostGuard;
        if (isRemove)
        return "(()=>{const key=" + quotedToken + ";const observerKey=" + observerToken + ";if(!( " + documentGuard + "))return false;const observer=window[observerKey];if(observer){observer.disconnect();delete window[observerKey];}const wasApplied=window[key]===true;const hasMarkers=document.querySelector('" + CodexHostContract.AppFrameSelector + "')&&document.querySelector('" + CodexHostContract.MainSurfaceSelector + "');if(hasMarkers||wasApplied){(()=>{\n" + script + "\n})();}delete window[key];return Boolean(hasMarkers||wasApplied);})()";
        return "(()=>{const key=" + quotedToken + ";const observerKey=" + observerToken + ";const bindingName=" + quotedBindingName + ";if(!( " + documentGuard + "))return false;const run=()=>{if(!(location.protocol==='app:'&&" + appHostGuard + "))return false;if(!document.querySelector('" + CodexHostContract.AppFrameSelector + "')||!document.querySelector('" + CodexHostContract.MainSurfaceSelector + "'))return false;if(window[key])return true;try{(()=>{\n" + script + "\n})();window[key]=true;const notify=window[bindingName];if(typeof notify==='function')notify(" + quotedAppliedToken + ");const oldObserver=window[observerKey];if(oldObserver){oldObserver.disconnect();delete window[observerKey];}return true;}catch(error){const observer=window[observerKey];if(observer){observer.disconnect();delete window[observerKey];}delete window[key];throw error;}};if(run())return true;if(!window[observerKey]){const observer=new MutationObserver(()=>{if(run()){observer.disconnect();delete window[observerKey];}});observer.observe(document,{childList:true,subtree:true});window[observerKey]=observer;}return false;})()";
    }

    private static bool IsCodexAppUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "app", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(uri.Host, "codex", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, "-", StringComparison.Ordinal);
    }

    private static bool IsMainDefaultContext(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("context", out var context) || !context.TryGetProperty("auxData", out var auxData) ||
            !auxData.TryGetProperty("isDefault", out var isDefault) || isDefault.ValueKind != JsonValueKind.True)
            return false;
        var origin = ReadString(context, "origin");
        return origin is not null && IsCodexAppUrl(origin);
    }

    private static bool IsMainFrameNavigation(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("frame", out var frame))
            return false;
        return !frame.TryGetProperty("parentId", out var parentId) || parentId.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task ForgetTargetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targetId = _targets.Values.FirstOrDefault(target => target.SessionId == sessionId)?.TargetId;
            if (targetId is not null)
                _targets.Remove(targetId);
            RecountAppliedTargets();
            await NotifyIfNoCodexTargetsRemainAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _targetGate.Release();
        }
    }

    private async Task ForgetTargetAsync(CdpEvent cdpEvent, CancellationToken cancellationToken)
    {
        var targetId = ReadString(cdpEvent.Parameters, "targetId");
        if (targetId is null && cdpEvent.Parameters.TryGetProperty("targetInfo", out var targetInfo))
            targetId = ReadString(targetInfo, "targetId");
        if (targetId is not null)
        {
            await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _targets.Remove(targetId);
                RecountAppliedTargets();
                await NotifyIfNoCodexTargetsRemainAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _targetGate.Release(); }
            return;
        }
        if (cdpEvent.SessionId is not null)
            await ForgetTargetSessionAsync(cdpEvent.SessionId, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsDestroyedTargetFailure(Exception exception)
    {
        if (exception is not CdpProtocolException protocolException)
            return false;
        var message = protocolException.Message;
        return message.Contains("No target with given id", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Target is closing", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Session with given id not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No session with given id", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Target detached", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find default execution context", StringComparison.OrdinalIgnoreCase);
    }

    private void FailBrowserSession(Exception exception)
    {
        if (_stopping)
            return;
        _browser.Abort(exception);
        Disconnected?.Invoke($"Codex browser 会话已停止：{exception.Message}");
    }

    private void RecountAppliedTargets()
    {
        var count = _targets.Values.Count(target => target.IsApplied);
        if (Interlocked.Exchange(ref _appliedTargetCount, count) == count)
            return;
        var handlers = AppliedTargetCountChanged;
        if (handlers is null)
            return;
        foreach (Action<int> handler in handlers.GetInvocationList())
        {
            try { handler(count); }
            catch { }
        }
    }

    private async Task NotifyIfNoCodexTargetsRemainAsync(CancellationToken cancellationToken)
    {
        if (!_hadCodexTarget || _targets.Count != 0 || _stopping || !_browser.IsConnected)
            return;
        var result = await _browser.SendAsync("Target.getTargets", cancellationToken: cancellationToken).ConfigureAwait(false);
        var hasCodexPage = result.TryGetProperty("targetInfos", out var infos) && infos.ValueKind == JsonValueKind.Array &&
            infos.EnumerateArray().Any(info => ReadString(info, "type") == "page" && IsCodexAppUrl(ReadString(info, "url") ?? string.Empty));
        if (!hasCodexPage)
            FailBrowserSession(new IOException("Codex 已关闭所有受支持的 app 页面，皮肤连接已停止。"));
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new IOException("Codex browser WebSocket 已断开；不会尝试连接已复用的端口。 ");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (await _targetGate.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false))
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                if (_browser.IsConnected)
                {
                    foreach (var target in _targets.Values.ToArray())
                    {
                        try { await RemoveEarlyScriptAsync(target, cleanupTimeout.Token).ConfigureAwait(false); }
                        catch { }
                    }
                }
                _targets.Clear();
                RecountAppliedTargets();
            }
            finally { _targetGate.Release(); }
        }
        _stopping = true;
        _lifetime.Cancel();
        await _browser.DisposeAsync().ConfigureAwait(false);
        try { await _eventPump.ConfigureAwait(false); }
        catch { }
        _lifetime.Dispose();
    }

    private sealed class CdpTarget(string targetId, string sessionId, string url, string runtimeToken)
    {
        public string TargetId { get; } = targetId;
        public string? SessionId { get; set; } = sessionId;
        public string Url { get; set; } = url;
        public string RuntimeToken { get; set; } = runtimeToken;
        public string? EarlyScriptId { get; set; }
        public bool IsApplied { get; set; }
        public bool IsCodexAppUrl => CdpRuntimeSession.IsCodexAppUrl(Url);
    }
}

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
    private RuntimeApplyRequest? _activeRequest;
    private RuntimeCompatibilityOptions _compatibilityOptions;
    private LocaleOverrideSnapshotStore? _compatibilitySnapshotStore;
    private int _appliedTargetCount;
    private bool _stopping;
    private bool _hadCodexTarget;
    private int _disposed;
    private long _compatibilityGeneration;

    public bool IsConnected => !_stopping && _browser.IsConnected;
    public Uri Endpoint { get; }
    public int AppliedTargetCount => Volatile.Read(ref _appliedTargetCount);
    public CodexProcessIdentity? ConnectedInstance => _browser.ConnectedInstance;
    public bool HasSkin => _activeRequest is not null;
    public event Action<string>? Disconnected;
    public event Action<int>? AppliedTargetCountChanged;
    public event Action<RuntimeCompatibilityResult>? CompatibilityResultChanged;

    private CdpRuntimeSession(Uri endpoint, CdpBrowserClient browser, RuntimeApplyRequest? request, RuntimeCompatibilityOptions compatibilityOptions)
    {
        Endpoint = endpoint;
        _browser = browser;
        _activeRequest = request;
        _compatibilityOptions = compatibilityOptions;
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
        return await ConnectAsync(endpoint, request, request.Compatibility ?? new RuntimeCompatibilityOptions(), identityVerifier, cancellationToken, expectedInstance ?? request.ExpectedInstance).ConfigureAwait(false);
    }

    public static async Task<CdpRuntimeSession> ConnectAsync(
        Uri endpoint,
        RuntimeApplyRequest? request,
        RuntimeCompatibilityOptions compatibilityOptions,
        ICodexEndpointIdentityVerifier identityVerifier,
        CancellationToken cancellationToken,
        CodexProcessIdentity? expectedInstance = null)
    {
        ArgumentNullException.ThrowIfNull(compatibilityOptions);
        var browser = await CdpBrowserClient.ConnectAsync(endpoint, identityVerifier, cancellationToken, expectedInstance).ConfigureAwait(false);
        var session = new CdpRuntimeSession(endpoint, browser, request, compatibilityOptions);
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
                if (!target.SkinBindingInstalled)
                {
                    await _browser.SendAsync("Runtime.addBinding", new { name = _bindingName }, target.SessionId, cancellationToken).ConfigureAwait(false);
                    target.SkinBindingInstalled = true;
                }
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

    public async Task<RuntimeCompatibilityResult> ConfigureCompatibilityAsync(
        RuntimeCompatibilityOptions options,
        LocaleOverrideSnapshotStore snapshotStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            _compatibilityOptions = options;
            _compatibilitySnapshotStore = snapshotStore;
            var generation = Interlocked.Increment(ref _compatibilityGeneration).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var targets = _targets.Values.Where(target => target.IsCodexAppUrl && target.SessionId is not null).ToArray();
            if (targets.Length == 0)
            {
                if (!options.ForceSimplifiedChinese)
                {
                    try
                    {
                        if (snapshotStore.Load() is null)
                            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Disabled, Endpoint.ToString(), "简体中文固定模式已关闭。", true, ConnectedInstance);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                    {
                        return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, Endpoint.ToString(), $"读取语言恢复状态失败：{ex.Message}", true, ConnectedInstance);
                    }
                }
                return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Pending, Endpoint.ToString(), "已连接，但尚无可用的 Codex 主窗口；语言设置会在页面就绪后重试。", true, ConnectedInstance);
            }

            if (!options.ForceSimplifiedChinese)
            {
                LocaleOverrideRestoreSnapshot? snapshot;
                try { snapshot = snapshotStore.Load(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                {
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, Endpoint.ToString(), $"读取语言恢复状态失败：{ex.Message}", true, ConnectedInstance);
                }

                foreach (var target in targets)
                {
                    await RemoveCompatibilityEarlyScriptAsync(target, cancellationToken).ConfigureAwait(false);
                    var disabled = await EvaluateCompatibilityScriptAsync(target, CodexLocaleCompatibilityScript.BuildStateScript(generation, enabled: false), cancellationToken).ConfigureAwait(false);
                    if (disabled.State is "pending" or "cancelled")
                        return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Pending, Endpoint.ToString(), "语言兼容钩子正在等待页面桥接就绪。", true, ConnectedInstance);
                    if (disabled.State == "unsupported")
                        return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Unsupported, Endpoint.ToString(), "当前 Codex 窗口不支持已验证的语言适配入口。", true, ConnectedInstance);
                }

                if (snapshot is null)
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Disabled, Endpoint.ToString(), "简体中文固定模式已关闭。", true, ConnectedInstance);

                var restored = false;
                var shouldKeepSnapshot = false;
                foreach (var target in targets)
                {
                    var result = await EvaluateCompatibilityScriptAsync(
                        target,
                        CodexLocaleCompatibilityScript.BuildRestoreLocaleOverrideScript(generation, snapshot.OriginalValue),
                        cancellationToken).ConfigureAwait(false);
                    switch (result.State)
                    {
                        case "restored":
                            restored = true;
                            break;
                        case "preserved":
                            restored = true;
                            break;
                        case "pending":
                        case "cancelled":
                            shouldKeepSnapshot = true;
                            break;
                        case "unsupported":
                            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Unsupported, Endpoint.ToString(), "当前 Codex 窗口不支持已验证的语言设置宿主接口。", true, ConnectedInstance);
                        default:
                            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, Endpoint.ToString(), "恢复原语言设置失败；原值快照仍保留以便重试。", true, ConnectedInstance);
                    }
                    if (result.State is "restored" or "preserved")
                        break;
                }

                if (shouldKeepSnapshot || !restored)
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Pending, Endpoint.ToString(), "原语言设置等待 Codex 设置宿主就绪；恢复快照已保留。", true, ConnectedInstance);

                try { snapshotStore.Clear(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, Endpoint.ToString(), $"语言已恢复，但清理恢复状态失败：{ex.Message}", true, ConnectedInstance);
                }
                return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Disabled, Endpoint.ToString(), "简体中文固定模式已关闭，并按当前值有条件恢复了原语言设置。", true, ConnectedInstance);
            }

            foreach (var target in targets)
            {
                var hook = await EnsureCompatibilityEarlyScriptAsync(target, generation, cancellationToken).ConfigureAwait(false);
                if (hook.State != RuntimeCompatibilityState.Applied)
                    return hook;
            }

            var sawPending = false;
            foreach (var target in targets)
            {
                var read = await EvaluateCompatibilityScriptAsync(target, CodexLocaleCompatibilityScript.BuildReadLocaleOverrideScript(generation), cancellationToken).ConfigureAwait(false);
                if (read.State == "pending")
                {
                    sawPending = true;
                    continue;
                }
                if (read.State == "cancelled")
                {
                    sawPending = true;
                    continue;
                }
                if (read.State == "unsupported")
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Unsupported, Endpoint.ToString(), "当前 Codex 窗口未提供已验证的 localeOverride 设置接口。", true, ConnectedInstance);
                if (read.State != "ready")
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, Endpoint.ToString(), "读取 Codex localeOverride 设置失败。", true, ConnectedInstance);

                try { snapshotStore.CaptureIfMissing(read.Value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
                {
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, Endpoint.ToString(), $"保存原语言恢复状态失败，未写入中文设置：{ex.Message}", true, ConnectedInstance);
                }

                if (string.Equals(read.Value, "zh-CN", StringComparison.Ordinal))
                {
                    target.CompatibilitySettingsGeneration = generation;
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Applied, Endpoint.ToString(), "已确认 localeOverride=zh-CN，并启用本地化入口；界面首次绘制仍由 Codex 自身加载流程决定。", true, ConnectedInstance);
                }

                var write = await EvaluateCompatibilityScriptAsync(target, CodexLocaleCompatibilityScript.BuildWriteSimplifiedChineseScript(generation), cancellationToken).ConfigureAwait(false);
                if (write.State == "applied")
                {
                    foreach (var candidate in targets)
                        candidate.CompatibilitySettingsGeneration = generation;
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Applied, Endpoint.ToString(), "已确认写入 localeOverride=zh-CN，并启用本地化入口；界面首次绘制仍由 Codex 自身加载流程决定。", true, ConnectedInstance);
                }
                if (write.State is "pending" or "cancelled")
                {
                    sawPending = true;
                    continue;
                }
                if (write.State == "unsupported")
                    return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Unsupported, Endpoint.ToString(), "Codex 设置宿主不支持 localeOverride 写入。", true, ConnectedInstance);
                return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, Endpoint.ToString(), "写入或验证 zh-CN 设置失败；原语言恢复快照已保留。", true, ConnectedInstance);
            }

            return new RuntimeCompatibilityResult(
                sawPending ? RuntimeCompatibilityState.Pending : RuntimeCompatibilityState.Failed,
                Endpoint.ToString(),
                sawPending ? "中文设置等待 Codex 设置宿主就绪；原值快照未被覆盖。" : "没有可用窗口完成中文设置写入。",
                true,
                ConnectedInstance);
        }
        finally
        {
            _targetGate.Release();
        }
    }

    public async Task RemoveSkinAsync(CancellationToken cancellationToken)
    {
        await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            var request = _activeRequest;
            if (request is null)
                return;
            foreach (var target in _targets.Values.ToArray())
            {
                if (!target.IsCodexAppUrl || target.SessionId is null)
                    continue;
                await RemoveEarlyScriptAsync(target, cancellationToken).ConfigureAwait(false);
                await EvaluateGuardedScriptAsync(target, request.RemoveScript, target.RuntimeToken, cancellationToken, isRemove: true).ConfigureAwait(false);
                await ClearGuardAsync(target, target.RuntimeToken, cancellationToken).ConfigureAwait(false);
                target.IsApplied = false;
                target.RuntimeToken = Guid.NewGuid().ToString("N");
            }
            _activeRequest = null;
            RecountAppliedTargets();
        }
        finally
        {
            _targetGate.Release();
        }
    }

    private string CurrentCompatibilityGeneration => _bindingName + "_" + Volatile.Read(ref _compatibilityGeneration).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<RuntimeCompatibilityResult> EnsureCompatibilityEarlyScriptAsync(CdpTarget target, string generation, CancellationToken cancellationToken)
    {
        if (target.SessionId is null || !target.IsCodexAppUrl)
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Unsupported, Endpoint.ToString(), "目标不是可用的 Codex 顶层页面。", true, ConnectedInstance);
        if (target.CompatibilityScriptId is not null && !string.Equals(target.CompatibilityScriptGeneration, generation, StringComparison.Ordinal))
            await RemoveCompatibilityEarlyScriptAsync(target, cancellationToken).ConfigureAwait(false);
        if (target.CompatibilityScriptId is null)
        {
            var installed = await _browser.SendAsync(
                "Page.addScriptToEvaluateOnNewDocument",
                new { source = CodexLocaleCompatibilityScript.BuildStateScript(generation, enabled: true), runImmediately = false },
                target.SessionId,
                cancellationToken).ConfigureAwait(false);
            target.CompatibilityScriptId = ReadString(installed, "identifier") ?? throw new InvalidOperationException("CDP 未返回语言兼容脚本 identifier。 ");
            target.CompatibilityScriptGeneration = generation;
        }

        var result = await EvaluateCompatibilityScriptAsync(
            target,
            CodexLocaleCompatibilityScript.BuildStateScript(generation, enabled: true),
            cancellationToken).ConfigureAwait(false);
        if (result.State == "hooked" && result.HookInstalled)
        {
            target.CompatibilityHookGeneration = generation;
            return new RuntimeCompatibilityResult(RuntimeCompatibilityState.Applied, Endpoint.ToString(), "本地化开关钩子已就绪。", true, ConnectedInstance);
        }
        target.CompatibilityHookGeneration = null;
        return result.State switch
        {
            "pending" or "cancelled" => new RuntimeCompatibilityResult(RuntimeCompatibilityState.Pending, Endpoint.ToString(), "语言钩子正在等待 Codex 页面桥接就绪。", true, ConnectedInstance),
            "unsupported" => new RuntimeCompatibilityResult(RuntimeCompatibilityState.Unsupported, Endpoint.ToString(), "当前 Codex 窗口不支持已验证的本地化开关适配。", true, ConnectedInstance),
            _ => new RuntimeCompatibilityResult(RuntimeCompatibilityState.Failed, Endpoint.ToString(), "安装本地化开关失败。", true, ConnectedInstance)
        };
    }

    private async Task<CompatibilityScriptResult> ApplyCompatibilityToTargetAsync(
        CdpTarget target,
        string generation,
        bool reassertIfAlreadyChinese,
        CancellationToken cancellationToken)
    {
        if (target.SessionId is null || !target.IsCodexAppUrl || !_compatibilityOptions.ForceSimplifiedChinese || _compatibilitySnapshotStore is null)
            return new CompatibilityScriptResult("pending");
        if (!string.Equals(target.CompatibilityHookGeneration, generation, StringComparison.Ordinal))
        {
            var hook = await EnsureCompatibilityEarlyScriptAsync(target, generation, cancellationToken).ConfigureAwait(false);
            if (hook.State != RuntimeCompatibilityState.Applied)
                return new CompatibilityScriptResult(hook.State switch
                {
                    RuntimeCompatibilityState.Pending => "pending",
                    RuntimeCompatibilityState.Unsupported => "unsupported",
                    RuntimeCompatibilityState.TimedOut => "pending",
                    _ => "failed"
                }, ErrorMessage: hook.Message);
        }
        if (!reassertIfAlreadyChinese && string.Equals(target.CompatibilitySettingsGeneration, generation, StringComparison.Ordinal))
            return new CompatibilityScriptResult("applied", "zh-CN");

        var read = await EvaluateCompatibilityScriptAsync(target, CodexLocaleCompatibilityScript.BuildReadLocaleOverrideScript(generation), cancellationToken).ConfigureAwait(false);
        if (read.State != "ready")
            return read;

        try { _compatibilitySnapshotStore.CaptureIfMissing(read.Value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        {
            return new CompatibilityScriptResult("failed", ErrorMessage: $"无法持久化原语言恢复状态：{ex.Message}");
        }

        if (!reassertIfAlreadyChinese && string.Equals(read.Value, "zh-CN", StringComparison.Ordinal))
        {
            target.CompatibilitySettingsGeneration = generation;
            return new CompatibilityScriptResult("applied", "zh-CN");
        }

        var write = await EvaluateCompatibilityScriptAsync(target, CodexLocaleCompatibilityScript.BuildWriteSimplifiedChineseScript(generation), cancellationToken).ConfigureAwait(false);
        if (write.State == "applied")
            target.CompatibilitySettingsGeneration = generation;
        return write;
    }

    private async Task<CompatibilityScriptResult> RestoreCompatibilityForTargetAsync(CdpTarget target, string generation, CancellationToken cancellationToken)
    {
        if (target.SessionId is null || !target.IsCodexAppUrl || _compatibilitySnapshotStore is null)
            return new CompatibilityScriptResult("pending");

        await RemoveCompatibilityEarlyScriptAsync(target, cancellationToken).ConfigureAwait(false);
        var disabled = await EvaluateCompatibilityScriptAsync(target, CodexLocaleCompatibilityScript.BuildStateScript(generation, enabled: false), cancellationToken).ConfigureAwait(false);
        if (disabled.State is "pending" or "cancelled")
            return disabled;
        if (disabled.State == "unsupported")
            return disabled;

        LocaleOverrideRestoreSnapshot? snapshot;
        try { snapshot = _compatibilitySnapshotStore.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new CompatibilityScriptResult("failed", ErrorMessage: $"无法读取原语言恢复状态：{ex.Message}");
        }
        if (snapshot is null)
            return new CompatibilityScriptResult("restored");

        var result = await EvaluateCompatibilityScriptAsync(
            target,
            CodexLocaleCompatibilityScript.BuildRestoreLocaleOverrideScript(generation, snapshot.OriginalValue),
            cancellationToken).ConfigureAwait(false);
        if (result.State is "restored" or "preserved")
        {
            try { _compatibilitySnapshotStore.Clear(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CompatibilityScriptResult("failed", ErrorMessage: $"已恢复语言，但清理恢复状态失败：{ex.Message}");
            }
        }
        return result;
    }

    private async Task RemoveCompatibilityEarlyScriptAsync(CdpTarget target, CancellationToken cancellationToken)
    {
        if (target.CompatibilityScriptId is null || !_browser.IsConnected || target.SessionId is null)
            return;
        var id = target.CompatibilityScriptId;
        await _browser.SendAsync("Page.removeScriptToEvaluateOnNewDocument", new { identifier = id }, target.SessionId, cancellationToken).ConfigureAwait(false);
        target.CompatibilityScriptId = null;
        target.CompatibilityHookGeneration = null;
        target.CompatibilityScriptGeneration = null;
        target.CompatibilitySettingsGeneration = null;
    }

    private async Task<CompatibilityScriptResult> EvaluateCompatibilityScriptAsync(CdpTarget target, string expression, CancellationToken cancellationToken)
    {
        JsonElement result;
        try
        {
            result = await _browser.SendAsync(
                "Runtime.evaluate",
                new { expression, returnByValue = true, awaitPromise = true },
                target.SessionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDestroyedTargetFailure(ex))
        {
            return new CompatibilityScriptResult("pending", ErrorMessage: "Codex 正在重新创建页面执行上下文。");
        }
        if (result.TryGetProperty("exceptionDetails", out var exceptionDetails))
        {
            var text = ReadString(exceptionDetails, "text") ?? "语言兼容脚本执行失败";
            var transientContext = text.Contains("context was destroyed", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("execution context", StringComparison.OrdinalIgnoreCase) && text.Contains("not found", StringComparison.OrdinalIgnoreCase);
            return new CompatibilityScriptResult(transientContext ? "pending" : "failed", ErrorMessage: text);
        }
        if (!result.TryGetProperty("result", out var remoteObject) || !remoteObject.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            return new CompatibilityScriptResult("failed", ErrorMessage: "语言兼容脚本未返回可验证结果。");

        var state = ReadString(value, "state");
        if (state is null)
            return new CompatibilityScriptResult("failed", ErrorMessage: "语言兼容脚本状态缺失。");
        var hookInstalled = value.TryGetProperty("hookInstalled", out var hookValue) && hookValue.ValueKind == JsonValueKind.True;
        string? locale = null;
        if (value.TryGetProperty("value", out var localeValue) && localeValue.ValueKind == JsonValueKind.String)
            locale = localeValue.GetString();
        else if (value.TryGetProperty("value", out localeValue) && localeValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            locale = null;
        return new CompatibilityScriptResult(state, locale, hookInstalled);
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
                try { await RemoveCompatibilityEarlyScriptAsync(target, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (IsDestroyedTargetFailure(ex)) { destroyedTargets.Add(target.TargetId); }
                catch (Exception ex) { failures.Add(ex); }
            }
            foreach (var target in _targets.Values.ToArray())
            {
                if (destroyedTargets.Contains(target.TargetId) || !target.IsCodexAppUrl)
                    continue;
                if (_activeRequest is not null)
                {
                    try
                    {
                        await EvaluateGuardedScriptAsync(target, _activeRequest.RemoveScript, target.RuntimeToken, cancellationToken, isRemove: true).ConfigureAwait(false);
                        target.IsApplied = false;
                    }
                    catch (Exception ex) when (IsDestroyedTargetFailure(ex)) { destroyedTargets.Add(target.TargetId); }
                    catch (Exception ex) { failures.Add(ex); }
                }
                if (!destroyedTargets.Contains(target.TargetId))
                {
                    try { await EvaluateCompatibilityScriptAsync(target, CodexLocaleCompatibilityScript.BuildStateScript(CurrentCompatibilityGeneration, enabled: false), cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (IsDestroyedTargetFailure(ex)) { destroyedTargets.Add(target.TargetId); }
                    catch (Exception ex) { failures.Add(ex); }
                }
            }
            foreach (var target in _targets.Values.ToArray())
            {
                if (destroyedTargets.Contains(target.TargetId) || target.SessionId is null || !_browser.IsConnected)
                    continue;
                if (target.EarlyScriptId is not null || target.CompatibilityScriptId is not null)
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
            foreach (var targetId in _targets.Values.Where(target => target.EarlyScriptId is null && target.CompatibilityScriptId is null).Select(target => target.TargetId).ToArray())
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
                var urlChanged = !string.Equals(existing.Url, url, StringComparison.Ordinal);
                existing.Url = url;
                if (isCandidate && existing.SessionId is not null)
                {
                    if (urlChanged)
                    {
                        existing.CompatibilityHookGeneration = null;
                        existing.CompatibilitySettingsGeneration = null;
                    }
                    if (_activeRequest is not null && existing.EarlyScriptId is null)
                    {
                        existing.RuntimeToken = Guid.NewGuid().ToString("N");
                        existing.IsApplied = await InstallRuntimeAsync(existing, _activeRequest.RuntimeScript, cancellationToken).ConfigureAwait(false);
                    }
                    if (_compatibilityOptions.ForceSimplifiedChinese && _compatibilitySnapshotStore is not null)
                        PublishCompatibilityScriptResult(await ApplyCompatibilityToTargetAsync(existing, CurrentCompatibilityGeneration, reassertIfAlreadyChinese: false, cancellationToken).ConfigureAwait(false));
                    else if (!_compatibilityOptions.ForceSimplifiedChinese && _compatibilitySnapshotStore is not null)
                        PublishCompatibilityScriptResult(await RestoreCompatibilityForTargetAsync(existing, CurrentCompatibilityGeneration, cancellationToken).ConfigureAwait(false));
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
            if (_activeRequest is not null)
            {
                await _browser.SendAsync("Runtime.addBinding", new { name = _bindingName }, sessionId, cancellationToken).ConfigureAwait(false);
                target.SkinBindingInstalled = true;
                target.IsApplied = await InstallRuntimeAsync(target, _activeRequest.RuntimeScript, cancellationToken).ConfigureAwait(false);
            }
            if (_compatibilityOptions.ForceSimplifiedChinese)
            {
                if (_compatibilitySnapshotStore is not null)
                    PublishCompatibilityScriptResult(await ApplyCompatibilityToTargetAsync(target, CurrentCompatibilityGeneration, reassertIfAlreadyChinese: false, cancellationToken).ConfigureAwait(false));
                else
                    await EnsureCompatibilityEarlyScriptAsync(target, CurrentCompatibilityGeneration, cancellationToken).ConfigureAwait(false);
            }
            else if (_compatibilitySnapshotStore is not null)
            {
                PublishCompatibilityScriptResult(await RestoreCompatibilityForTargetAsync(target, CurrentCompatibilityGeneration, cancellationToken).ConfigureAwait(false));
            }
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
            if (_activeRequest is not null)
                target.IsApplied = await ProbeAppliedStateAsync(target, cancellationToken).ConfigureAwait(false);
            target.CompatibilityHookGeneration = null;
            target.CompatibilitySettingsGeneration = null;
            if (_compatibilityOptions.ForceSimplifiedChinese && _compatibilitySnapshotStore is not null)
                PublishCompatibilityScriptResult(await ApplyCompatibilityToTargetAsync(target, CurrentCompatibilityGeneration, reassertIfAlreadyChinese: false, cancellationToken).ConfigureAwait(false));
            else if (!_compatibilityOptions.ForceSimplifiedChinese && _compatibilitySnapshotStore is not null)
                PublishCompatibilityScriptResult(await RestoreCompatibilityForTargetAsync(target, CurrentCompatibilityGeneration, cancellationToken).ConfigureAwait(false));
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
            if (target is null || !target.IsCodexAppUrl || _activeRequest is null || ReadString(cdpEvent.Parameters, "payload") != target.RuntimeToken)
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
        await RemoveCompatibilityEarlyScriptAsync(target, cancellationToken).ConfigureAwait(false);
        if (target.IsCodexAppUrl && target.RuntimeToken is not null && _activeRequest is not null)
            await EvaluateGuardedScriptAsync(target, _activeRequest.RemoveScript, target.RuntimeToken, cancellationToken, isRemove: true).ConfigureAwait(false);
        if (target.IsCodexAppUrl)
            await EvaluateCompatibilityScriptAsync(target, CodexLocaleCompatibilityScript.BuildStateScript(CurrentCompatibilityGeneration, enabled: false), cancellationToken).ConfigureAwait(false);
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
        {
            if (_compatibilityOptions.ForceSimplifiedChinese)
                return;
            try
            {
                if (_compatibilitySnapshotStore?.Load() is not null)
                    return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                return;
            }
            FailBrowserSession(new IOException("Codex 已关闭所有受支持的 app 页面，皮肤连接已停止。"));
        }
    }

    private void PublishCompatibilityScriptResult(CompatibilityScriptResult scriptResult)
    {
        var state = scriptResult.State switch
        {
            "applied" or "restored" or "preserved" => _compatibilityOptions.ForceSimplifiedChinese
                ? RuntimeCompatibilityState.Applied
                : RuntimeCompatibilityState.Disabled,
            "pending" or "cancelled" => RuntimeCompatibilityState.Pending,
            "unsupported" => RuntimeCompatibilityState.Unsupported,
            _ => RuntimeCompatibilityState.Failed
        };
        var message = state switch
        {
            RuntimeCompatibilityState.Applied => "Codex 语言设置已确认使用简体中文。",
            RuntimeCompatibilityState.Disabled when scriptResult.State == "preserved" => "已关闭固定中文；保留了用户手动更改的语言。",
            RuntimeCompatibilityState.Disabled => "简体中文固定模式已关闭，并完成原语言恢复。",
            RuntimeCompatibilityState.Pending => "语言设置等待 Codex 主窗口或设置宿主就绪。",
            RuntimeCompatibilityState.Unsupported => "当前 Codex 窗口不支持已验证的语言设置入口。",
            _ => scriptResult.ErrorMessage ?? "Codex 语言设置未完成。"
        };
        var result = new RuntimeCompatibilityResult(state, Endpoint.ToString(), message, true, ConnectedInstance);
        var handlers = CompatibilityResultChanged;
        if (handlers is null)
            return;
        foreach (Action<RuntimeCompatibilityResult> handler in handlers.GetInvocationList())
        {
            try { handler(result); }
            catch { }
        }
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
                        try { await RemoveCompatibilityEarlyScriptAsync(target, cleanupTimeout.Token).ConfigureAwait(false); }
                        catch { }
                        if (target.IsCodexAppUrl && target.SessionId is not null)
                        {
                            try { await EvaluateCompatibilityScriptAsync(target, CodexLocaleCompatibilityScript.BuildStateScript(CurrentCompatibilityGeneration, enabled: false), cleanupTimeout.Token).ConfigureAwait(false); }
                            catch { }
                        }
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

    private sealed record CompatibilityScriptResult(string State, string? Value = null, bool HookInstalled = false, string? ErrorMessage = null);

    private sealed class CdpTarget(string targetId, string sessionId, string url, string runtimeToken)
    {
        public string TargetId { get; } = targetId;
        public string? SessionId { get; set; } = sessionId;
        public string Url { get; set; } = url;
        public string RuntimeToken { get; set; } = runtimeToken;
        public string? EarlyScriptId { get; set; }
        public string? CompatibilityScriptId { get; set; }
        public string? CompatibilityScriptGeneration { get; set; }
        public string? CompatibilityHookGeneration { get; set; }
        public string? CompatibilitySettingsGeneration { get; set; }
        public bool SkinBindingInstalled { get; set; }
        public bool IsApplied { get; set; }
        public bool IsCodexAppUrl => CdpRuntimeSession.IsCodexAppUrl(Url);
    }
}

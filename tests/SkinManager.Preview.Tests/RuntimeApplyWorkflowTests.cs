using SkinManager.Desktop;
using SkinManager.Runtime;

namespace SkinManager.Preview.Tests;

internal static class RuntimeApplyWorkflowTests
{
    public static async Task RunAsync()
    {
        await AppliesToFreshSelectedTargetAsync();
        await RejectsStaleSelectedTargetAsync();
        await StartsAfterSelectedTargetHasExitedAsync();
        await AppliesToExistingConnectedTargetAsync();
        await PreservesActiveVerifiedEndpointWhenAnotherAddressIsDiscoveredAsync();
        await AppliesToUniqueExistingDebugTargetAsync();
        await WaitsForExistingUnreadyUniqueTargetAsync();
        await WaitsForExistingUnreadyTargetWithoutSkinAsync();
        await CancellingExistingTargetWaitDoesNotLaunchOrApplyAsync();
        await ExistingTargetWaitTimesOutWithoutLaunchingOrApplyingAsync();
        await RefusesAutomaticSelectionAcrossDebugPortsButHonorsExplicitSelectionAsync();
        await StopsAutomaticSelectionWhenAnotherDebugInstanceAppearsAsync();
        await RefusesIdentityChangeAfterLaunchedInstanceIsDiscoveredAsync();
        await RefusesAmbiguousOrMismatchedTargetsAsync();
        await DoesNotLaunchAlongsideNormalCodexAsync();
        await DoesNotLaunchAlongsideExitResidualAsync();
        await DoesNotWaitForUnconnectableDebugFlagAsync();
        await LaunchesAndAppliesAfterMatchingDebugTargetAppearsAsync();
        await DoesNotApplyAfterLaunchFailureOrWrongProcessAsync();
        await CancellingWhileWaitingDoesNotApplyAsync();
        await TimeoutDoesNotApplyAsync();
        await ConnectsToExistingTargetWithoutSkinAsync();
        await LaunchesAndWaitsForTargetWithoutSkinAsync();
        await ConnectsToVerifiedIPv6EndpointWithoutSkinAsync();
        await RefusesNormalInstanceForCompatibilityAsync();
        await CancellingCompatibilityReadinessDoesNotConnectAsync();
        VerifiesConnectedDoesNotMeanApplied();
    }

    private static async Task AppliesToFreshSelectedTargetAsync()
    {
        var target = CreateTarget(4101, 9336, debugEndpoint: "http://[::1]:9336/");
        var services = new FakeServices(new FakeInstances([target]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest("http://[::1]:9336/"), 9336, target, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.LaunchCount == 0 && services.Applies.Count == 1,
            "已选调试实例直接应用，未重新启动 Codex");
        Require(services.Applies[0].ExpectedInstance == target.Identity && services.Applies[0].Endpoint == "http://[::1]:9336/",
            "已选目标的完整身份进入应用请求，并保留 Runtime 核验的 IPv6 Endpoint");
    }

    private static async Task RejectsStaleSelectedTargetAsync()
    {
        var selected = CreateTarget(4102, 9337);
        var replacement = CreateTarget(4102, 9337, startedAt: DateTimeOffset.Parse("2026-11-07T00:01:00Z"));
        var services = new FakeServices(new FakeInstances([replacement]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest("http://[::1]:9337"), 9337, selected, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.Applies.Count == 1 && services.LaunchCount == 0 &&
                services.Applies[0].ExpectedInstance == replacement.Identity,
            "旧选择对应进程退出后重新发现并应用到当前已核验的新进程身份");
    }

    private static async Task StartsAfterSelectedTargetHasExitedAsync()
    {
        var selected = CreateTarget(4192, 9342);
        var replacement = CreateTarget(4193, 9342);
        var services = new FakeServices(new FakeInstances([], [replacement]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest("http://[::1]:9342"), 9342, selected, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.LaunchCount == 1 && services.Applies.Count == 1 &&
                services.Applies[0].ExpectedInstance == replacement.Identity,
            "所选目标已退出且没有其他实例时重新启动并应用到新发现的调试进程");
    }

    private static async Task AppliesToExistingConnectedTargetAsync()
    {
        var target = CreateTarget(4103, 9335);
        var services = new FakeServices(new FakeInstances([target]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9335, null, target.Identity, hasActiveConnection: true, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.LaunchCount == 0 && services.Applies.Count == 1 &&
                services.Applies[0].ExpectedInstance == target.Identity,
            "已有连接实例直接应用到当前身份");
    }

    private static async Task PreservesActiveVerifiedEndpointWhenAnotherAddressIsDiscoveredAsync()
    {
        var target = CreateTarget(4197, 9347, debugEndpoint: "http://127.0.0.1:9347/");
        var services = new FakeServices(new FakeInstances([target]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest("http://[::1]:9347/"), 9347, null, target.Identity,
            hasActiveConnection: true, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.Applies.Count == 1 &&
                services.Applies[0].Endpoint == "http://[::1]:9347/" &&
                services.Applies[0].ExpectedInstance == target.Identity,
            "同一进程仍有其他可用地址时，应用继续使用当前会话的已核验 Endpoint");
    }

    private static async Task AppliesToUniqueExistingDebugTargetAsync()
    {
        var target = CreateTarget(4104, 9335);
        var services = new FakeServices(new FakeInstances([target]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9335, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.LaunchCount == 0 && services.Applies.Count == 1 &&
                services.Applies[0].ExpectedInstance == target.Identity,
            "已有唯一匹配调试实例时直接应用");
    }

    private static async Task WaitsForExistingUnreadyUniqueTargetAsync()
    {
        var waiting = CreateTarget(4125, 9348, canConnect: false,
            connectionState: CodexDebugConnectionState.WaitingForListener);
        var ready = CreateTarget(4125, 9348);
        var progress = new List<RuntimeStatus>();
        var services = new FakeServices(new FakeInstances([waiting], [ready]));
        var result = await services.CreateWorkflow(
            readinessTimeout: TimeSpan.FromMilliseconds(250),
            pollInterval: TimeSpan.FromMilliseconds(2),
            progress: progress.Add).StartOrApplyAsync(
            CreateRequest(), 9348, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.LaunchCount == 0 && services.Applies.Count == 1 &&
                services.Applies[0].ExpectedInstance == ready.Identity,
            "唯一已有调试实例 listener 尚未就绪时等待同一进程身份后只应用一次");
        Require(progress.Count > 0 && progress.All(status => status.State == RuntimeStatusState.Connecting && status.SkinId == "ayanami") &&
                progress.Any(status => status.ConnectedInstance == waiting.Identity),
            "进度回调持续携带 Connecting、所选皮肤 ID 与等待中的进程身份");
    }

    private static async Task WaitsForExistingUnreadyTargetWithoutSkinAsync()
    {
        var waiting = CreateTarget(4131, 9353, canConnect: false,
            connectionState: CodexDebugConnectionState.WaitingForCdp);
        var ready = CreateTarget(4131, 9353);
        var services = new FakeServices(new FakeInstances([waiting], [ready]));
        var result = await services.CreateWorkflow(
            readinessTimeout: TimeSpan.FromMilliseconds(250),
            pollInterval: TimeSpan.FromMilliseconds(2)).StartOrConnectAsync(
            9353, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && result.SkinId is null && result.AppliedTargetCount == 0 &&
                services.LaunchCount == 0 && services.Applies.Count == 0,
            "无皮肤连接对已有未就绪实例等待后只连接，不发送 Apply");
    }

    private static async Task CancellingExistingTargetWaitDoesNotLaunchOrApplyAsync()
    {
        var waiting = CreateTarget(4132, 9354, canConnect: false,
            connectionState: CodexDebugConnectionState.WaitingForListener);
        var instances = new FakeInstances([waiting]);
        var waitingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        instances.OnRead = count =>
        {
            if (count >= 2)
            {
                waitingStarted.TrySetResult();
            }
        };
        var services = new FakeServices(instances);
        using var cancellation = new CancellationTokenSource();
        var operation = services.CreateWorkflow(pollInterval: TimeSpan.FromSeconds(10)).StartOrApplyAsync(
            CreateRequest(), 9354, null, null, hasActiveConnection: false, cancellation.Token);
        await waitingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        try
        {
            await operation;
            throw new InvalidOperationException("取消已有实例的就绪等待应结束流程。");
        }
        catch (OperationCanceledException)
        {
        }

        Require(services.LaunchCount == 0 && services.Applies.Count == 0,
            "取消已有实例的就绪等待不会启动第二个 Codex 或补发 Apply");
    }

    private static async Task ExistingTargetWaitTimesOutWithoutLaunchingOrApplyingAsync()
    {
        var waiting = CreateTarget(4133, 9355, canConnect: false,
            connectionState: CodexDebugConnectionState.WaitingForListener);
        var services = new FakeServices(new FakeInstances([waiting]));
        var result = await services.CreateWorkflow(
            readinessTimeout: TimeSpan.FromMilliseconds(20),
            pollInterval: TimeSpan.FromMilliseconds(2)).StartOrApplyAsync(
            CreateRequest(), 9355, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Error && services.LaunchCount == 0 && services.Applies.Count == 0 &&
                result.Message?.Contains("超时", StringComparison.Ordinal) == true,
            "已有实例的有界等待超时后不重启 Codex 或发送皮肤脚本");
    }

    private static async Task RefusesAutomaticSelectionAcrossDebugPortsButHonorsExplicitSelectionAsync()
    {
        var first = CreateTarget(4126, 9349);
        var second = CreateTarget(4127, 9350);
        var automatic = new FakeServices(new FakeInstances([first, second]));
        var automaticResult = await automatic.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9349, null, null, hasActiveConnection: false, CancellationToken.None);
        Require(automaticResult.State == RuntimeStatusState.Error && automatic.Applies.Count == 0 && automatic.LaunchCount == 0,
            "不同调试端口存在多个主实例时不自动连接恰好匹配请求端口的其中一个");

        var selected = new FakeServices(new FakeInstances([first, second]));
        var selectedResult = await selected.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9349, first, null, hasActiveConnection: false, CancellationToken.None);
        Require(selectedResult.State == RuntimeStatusState.Connected && selected.Applies.Count == 1 && selected.LaunchCount == 0 &&
                selected.Applies[0].ExpectedInstance == first.Identity,
            "显式选择的已核验调试实例在多实例环境下优先应用");
    }

    private static async Task StopsAutomaticSelectionWhenAnotherDebugInstanceAppearsAsync()
    {
        var waiting = CreateTarget(4128, 9351, canConnect: false,
            connectionState: CodexDebugConnectionState.WaitingForCdp);
        var otherPort = CreateTarget(4129, 9352);
        var services = new FakeServices(new FakeInstances([waiting], [waiting, otherPort]));
        var result = await services.CreateWorkflow(
            readinessTimeout: TimeSpan.FromMilliseconds(250),
            pollInterval: TimeSpan.FromMilliseconds(2)).StartOrApplyAsync(
            CreateRequest(), 9351, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Error && services.Applies.Count == 0 && services.LaunchCount == 0,
            "自动等待期间出现另一个端口的调试主实例后停止自动选择");
    }

    private static async Task RefusesIdentityChangeAfterLaunchedInstanceIsDiscoveredAsync()
    {
        var waiting = CreateTarget(4134, 9356, canConnect: false,
            connectionState: CodexDebugConnectionState.WaitingForListener);
        var replacement = CreateTarget(4134, 9356,
            startedAt: waiting.Identity.StartedAtUtc.AddSeconds(1));
        var services = new FakeServices(new FakeInstances([], [waiting], [replacement]));
        var result = await services.CreateWorkflow(
            readinessTimeout: TimeSpan.FromMilliseconds(250),
            pollInterval: TimeSpan.FromMilliseconds(2)).StartOrApplyAsync(
            CreateRequest(), 9356, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Error && services.LaunchCount == 1 && services.Applies.Count == 0 &&
                result.Message?.Contains("身份发生变化", StringComparison.Ordinal) == true,
            "启动后首次发现调试实例时锁定完整身份，后续 PID 复用不切换目标");
    }

    private static async Task RefusesAmbiguousOrMismatchedTargetsAsync()
    {
        var first = CreateTarget(4105, 9335);
        var second = CreateTarget(4106, 9335);
        var ambiguous = new FakeServices(new FakeInstances([first, second]));
        var ambiguousResult = await ambiguous.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9335, null, null, hasActiveConnection: false, CancellationToken.None);
        Require(ambiguousResult.State == RuntimeStatusState.Error && ambiguous.Applies.Count == 0 && ambiguous.LaunchCount == 0,
            "同端口有多个调试实例时要求明确选择目标");

        var otherPort = CreateTarget(4107, 9336);
        var mismatch = new FakeServices(new FakeInstances([otherPort]));
        var mismatchResult = await mismatch.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9335, null, null, hasActiveConnection: false, CancellationToken.None);
        Require(mismatchResult.State == RuntimeStatusState.Error && mismatch.Applies.Count == 0 && mismatch.LaunchCount == 0,
            "已有调试实例端口不匹配时不启动另一个 Codex");
    }

    private static async Task DoesNotLaunchAlongsideNormalCodexAsync()
    {
        var normal = CreateTarget(4108, null, kind: CodexInstanceKind.Normal, canConnect: false);
        var services = new FakeServices(new FakeInstances([normal]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9335, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Error && services.LaunchCount == 0 && services.Applies.Count == 0,
            "普通版 Codex 仍运行时既不退出它也不启动另一个实例");
    }

    private static async Task DoesNotLaunchAlongsideExitResidualAsync()
    {
        var residual = CreateTarget(4194, 9335) with { IsExitResidual = true };
        var services = new FakeServices(new FakeInstances([residual]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9335, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Error && services.LaunchCount == 0 && services.Applies.Count == 0 &&
                result.Message?.Contains("后台残留", StringComparison.Ordinal) == true,
            "存在 Codex 后台残留时阻止启动新实例并说明原因");
    }

    private static async Task DoesNotWaitForUnconnectableDebugFlagAsync()
    {
        var flaggedOnly = CreateTarget(4195, 9345, canConnect: false,
            connectionState: CodexDebugConnectionState.Rejected,
            connectionMessage: "fake listener belongs to another process");
        var instances = new FakeInstances([flaggedOnly]);
        var services = new FakeServices(instances);
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9345, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Error && services.LaunchCount == 0 && services.Applies.Count == 0 &&
                result.Message?.Contains("fake listener belongs to another process", StringComparison.Ordinal) == true &&
                instances.ReadCount == 1,
            "拒绝状态保留端口所有权原因并在首次扫描后终止，不进入等待、启动或应用");
    }

    private static async Task LaunchesAndAppliesAfterMatchingDebugTargetAppearsAsync()
    {
        var target = CreateTarget(4109, 9338);
        var services = new FakeServices(new FakeInstances([], [target]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9338, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.LaunchCount == 1 && services.LaunchPort == 9338 &&
                services.Applies.Count == 1 && services.Applies[0].ExpectedInstance == target.Identity,
            "空实例列表时启动调试版，确认端口与进程身份后只应用一次");
    }

    private static async Task DoesNotApplyAfterLaunchFailureOrWrongProcessAsync()
    {
        var launchFailure = new FakeServices(new FakeInstances([]))
        {
            LaunchResult = new RuntimeStatus(RuntimeStatusState.Error, null, null, "fake 启动失败", false)
        };
        var failedResult = await launchFailure.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9335, null, null, hasActiveConnection: false, CancellationToken.None);
        Require(failedResult.State == RuntimeStatusState.Error && launchFailure.LaunchCount == 1 && launchFailure.Applies.Count == 0,
            "调试版启动失败后不继续应用");

        var normal = CreateTarget(4110, null, kind: CodexInstanceKind.Normal, canConnect: false);
        var wrongProcess = new FakeServices(new FakeInstances([], [normal]));
        var wrongProcessResult = await wrongProcess.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9335, null, null, hasActiveConnection: false, CancellationToken.None);
        Require(wrongProcessResult.State == RuntimeStatusState.Error && wrongProcess.LaunchCount == 1 && wrongProcess.Applies.Count == 0,
            "启动后发现普通版 Codex 时不发送皮肤脚本");
    }

    private static async Task CancellingWhileWaitingDoesNotApplyAsync()
    {
        var instances = new FakeInstances([]);
        var waitingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        instances.OnRead = count =>
        {
            if (count >= 2)
            {
                waitingStarted.TrySetResult();
            }
        };
        var services = new FakeServices(instances);
        using var cancellation = new CancellationTokenSource();
        var operation = services.CreateWorkflow(pollInterval: TimeSpan.FromSeconds(10)).StartOrApplyAsync(
            CreateRequest(), 9335, null, null, hasActiveConnection: false, cancellation.Token);
        await waitingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        try
        {
            await operation;
            throw new InvalidOperationException("取消等待时应结束启动与应用流程。");
        }
        catch (OperationCanceledException)
        {
        }

        Require(services.LaunchCount == 1 && services.Applies.Count == 0,
            "等待期间取消不会在后台补发 Apply");
    }

    private static async Task TimeoutDoesNotApplyAsync()
    {
        var services = new FakeServices(new FakeInstances([]));
        var result = await services.CreateWorkflow(
            readinessTimeout: TimeSpan.FromMilliseconds(20),
            pollInterval: TimeSpan.FromMilliseconds(5)).StartOrApplyAsync(
            CreateRequest(), 9335, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Error && services.LaunchCount == 1 && services.Applies.Count == 0,
            "调试端口超时后不发送皮肤脚本");
    }

    private static async Task ConnectsToExistingTargetWithoutSkinAsync()
    {
        var target = CreateTarget(4112, 9335);
        var services = new FakeServices(new FakeInstances([target]));
        var result = await services.CreateWorkflow().StartOrConnectAsync(
            9335, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && result.SkinId is null &&
                result.AppliedTargetCount == 0 && result.ConnectedInstance == target.Identity &&
                services.LaunchCount == 0 && services.Applies.Count == 0,
            "无皮肤时连接唯一就绪的调试目标，不发送Apply");
    }

    private static async Task LaunchesAndWaitsForTargetWithoutSkinAsync()
    {
        var target = CreateTarget(4113, 9339);
        var services = new FakeServices(new FakeInstances([], [target]));
        var result = await services.CreateWorkflow().StartOrConnectAsync(
            9339, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && result.SkinId is null &&
                result.AppliedTargetCount == 0 && result.ConnectedInstance == target.Identity &&
                services.LaunchCount == 1 && services.LaunchPort == 9339 && services.Applies.Count == 0,
            "无皮肤时等待正确调试身份就绪后连接，Apply次数仍为零");
    }

    private static async Task ConnectsToVerifiedIPv6EndpointWithoutSkinAsync()
    {
        var target = CreateTarget(4196, 9346, debugEndpoint: "http://[::1]:9346/");
        var services = new FakeServices(new FakeInstances([target]));
        var result = await services.CreateWorkflow().StartOrConnectAsync(
            9346, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && result.Endpoint == "http://[::1]:9346/" &&
                services.LaunchCount == 0 && services.Applies.Count == 0,
            "无皮肤连接也保留 Runtime 核验出的 IPv6 Endpoint，不回退到 IPv4 拼接地址");
    }

    private static async Task RefusesNormalInstanceForCompatibilityAsync()
    {
        var normal = CreateTarget(4114, null, kind: CodexInstanceKind.Normal, canConnect: false);
        var services = new FakeServices(new FakeInstances([normal]));
        var result = await services.CreateWorkflow().StartOrConnectAsync(
            9335, null, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Error && services.LaunchCount == 0 && services.Applies.Count == 0,
            "普通实例运行时不关闭或重启，也不连接/配置兼容选项");
    }

    private static async Task CancellingCompatibilityReadinessDoesNotConnectAsync()
    {
        var instances = new FakeInstances([]);
        var waitingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        instances.OnRead = count =>
        {
            if (count >= 2)
            {
                waitingStarted.TrySetResult();
            }
        };
        var services = new FakeServices(instances);
        using var cancellation = new CancellationTokenSource();
        var operation = services.CreateWorkflow(pollInterval: TimeSpan.FromSeconds(10)).StartOrConnectAsync(
            9335, null, null, hasActiveConnection: false, cancellation.Token);
        await waitingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        try
        {
            await operation;
            throw new InvalidOperationException("等待目标时取消应结束语言同步启动流程。");
        }
        catch (OperationCanceledException)
        {
        }

        Require(services.LaunchCount == 1 && services.Applies.Count == 0,
            "无皮肤配置的就绪等待取消后不连接、不发送Apply");
    }

    private static void VerifiesConnectedDoesNotMeanApplied()
    {
        var state = RuntimeConnectionViewState.From(
            new RuntimeStatus(RuntimeStatusState.Connected, "ayanami", "http://127.0.0.1:9335", "fake no main window", true, 0));
        Require(state.IsConnected && !state.IsApplied && !state.StatusLabel.Contains("已应用", StringComparison.Ordinal),
            "运行时连接成功但没有匹配主窗口时显示尚未应用");

        var previousTarget = new CodexProcessIdentity(4111, DateTimeOffset.Parse("2026-11-07T00:00:00Z"), @"C:\example\Codex.exe");
        var errorState = RuntimeConnectionViewState.From(
            new RuntimeStatus(RuntimeStatusState.Error, "ayanami", "http://127.0.0.1:9335", null, true, 1, previousTarget));
        Require(errorState.Message.Contains("本次操作未完成", StringComparison.Ordinal) &&
                !errorState.Message.Contains("没有应用皮肤", StringComparison.Ordinal) &&
                !errorState.Message.Contains("已移除", StringComparison.Ordinal),
            "更新失败提示本次操作未完成，不误称旧皮肤已被移除");
    }

    private static CodexInstance CreateTarget(
        int processId,
        int? port,
        DateTimeOffset? startedAt = null,
        CodexInstanceKind? kind = null,
        bool? canConnect = null,
        string? debugEndpoint = null,
        CodexDebugConnectionState? connectionState = null,
        string? connectionMessage = null) =>
        new(
            new CodexProcessIdentity(
                processId,
                startedAt ?? DateTimeOffset.Parse("2026-11-07T00:00:00Z"),
                @"C:\example\Codex.exe"),
            "Codex · 示例窗口",
            processId,
            port,
            null)
        {
            Kind = kind ?? (port is null ? CodexInstanceKind.Normal : CodexInstanceKind.Debug),
            CanConnect = canConnect ?? port is >= 1 and <= 65535,
            DebugConnectionState = connectionState ?? ((canConnect ?? port is >= 1 and <= 65535)
                ? CodexDebugConnectionState.Ready
                : port is null ? CodexDebugConnectionState.NotApplicable : CodexDebugConnectionState.Rejected),
            DebugEndpoint = (canConnect ?? port is >= 1 and <= 65535) && port is int debugPort
                ? debugEndpoint ?? $"http://127.0.0.1:{debugPort}"
                : debugEndpoint,
            ConnectionMessage = connectionMessage ?? ((canConnect ?? port is >= 1 and <= 65535)
                ? null
                : port is int unavailablePort
                    ? $"fake listener for port {unavailablePort} is unavailable"
                    : null)
        };

    private static RuntimeApplyRequest CreateRequest(string endpoint = "http://127.0.0.1:9335") => new()
    {
        SkinId = "ayanami",
        RuntimeScript = "fake apply script",
        RemoveScript = "fake remove script",
        Endpoint = endpoint
    };

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"运行时主流程回归失败：{label}");
        }

        Console.WriteLine($"通过：{label}");
    }

    private sealed class FakeServices(FakeInstances instances)
    {
        public List<RuntimeApplyRequest> Applies { get; } = [];
        public int LaunchCount { get; private set; }
        public int? LaunchPort { get; private set; }
        public RuntimeStatus LaunchResult { get; set; } =
            new(RuntimeStatusState.Connecting, null, "http://127.0.0.1:9335", "fake launching", null);

        public RuntimeApplyWorkflow CreateWorkflow(
            TimeSpan? readinessTimeout = null,
            TimeSpan? pollInterval = null,
            Action<RuntimeStatus>? progress = null) =>
            new(
                (options, _) =>
                {
                    LaunchCount++;
                    LaunchPort = options.Port;
                    return Task.FromResult(LaunchResult);
                },
                (request, _) =>
                {
                    Applies.Add(request);
                    return Task.FromResult(new RuntimeStatus(
                        RuntimeStatusState.Connected,
                        request.SkinId,
                        request.Endpoint,
                        "fake apply completed",
                        true,
                        1,
                        request.ExpectedInstance));
                },
                instances,
                readinessTimeout,
                pollInterval,
                progress);
    }

    private sealed class FakeInstances(params IReadOnlyList<CodexInstance>[] snapshots) : ICodexInstanceService
    {
        private IReadOnlyList<CodexInstance> _last = Array.Empty<CodexInstance>();

        public int ReadCount { get; private set; }
        public Action<int>? OnRead { get; set; }

        public Task<IReadOnlyList<CodexInstance>> GetInstancesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadCount < snapshots.Length)
            {
                _last = snapshots[ReadCount];
            }

            ReadCount++;
            OnRead?.Invoke(ReadCount);
            return Task.FromResult(_last);
        }

        public Task<CodexLocateResult> LocateAsync(CodexInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromException<CodexLocateResult>(new InvalidOperationException("Unexpected fake Locate call."));

        public Task<CodexCloseOperationResult> CloseAsync(
            CodexInstance instance,
            bool force = false,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CodexCloseOperationResult>(new InvalidOperationException("Unexpected fake Close call."));
    }
}

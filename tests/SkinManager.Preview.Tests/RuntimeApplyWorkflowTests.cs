using SkinManager.Desktop;
using SkinManager.Runtime;

namespace SkinManager.Preview.Tests;

internal static class RuntimeApplyWorkflowTests
{
    public static async Task RunAsync()
    {
        await AppliesToFreshSelectedTargetAsync();
        await RejectsStaleSelectedTargetAsync();
        await AppliesToExistingConnectedTargetAsync();
        await AppliesToUniqueExistingDebugTargetAsync();
        await RefusesAmbiguousOrMismatchedTargetsAsync();
        await DoesNotLaunchAlongsideNormalCodexAsync();
        await LaunchesAndAppliesAfterMatchingDebugTargetAppearsAsync();
        await DoesNotApplyAfterLaunchFailureOrWrongProcessAsync();
        await CancellingWhileWaitingDoesNotApplyAsync();
        await TimeoutDoesNotApplyAsync();
        await ConnectsToExistingTargetWithoutSkinAsync();
        await LaunchesAndWaitsForTargetWithoutSkinAsync();
        await RefusesNormalInstanceForCompatibilityAsync();
        await CancellingCompatibilityReadinessDoesNotConnectAsync();
        VerifiesConnectedDoesNotMeanApplied();
    }

    private static async Task AppliesToFreshSelectedTargetAsync()
    {
        var target = CreateTarget(4101, 9336);
        var services = new FakeServices(new FakeInstances([target]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9336, target, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.LaunchCount == 0 && services.Applies.Count == 1,
            "已选调试实例直接应用，未重新启动 Codex");
        Require(services.Applies[0].ExpectedInstance == target.Identity && services.Applies[0].Endpoint.EndsWith(":9336", StringComparison.Ordinal),
            "已选目标的完整身份与端口进入应用请求");
    }

    private static async Task RejectsStaleSelectedTargetAsync()
    {
        var selected = CreateTarget(4102, 9337);
        var replacement = CreateTarget(4102, 9337, startedAt: DateTimeOffset.Parse("2026-11-07T00:01:00Z"));
        var services = new FakeServices(new FakeInstances([replacement]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9337, selected, null, hasActiveConnection: false, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Error && services.Applies.Count == 0 && services.LaunchCount == 0,
            "所选进程身份变化后不发送皮肤脚本");
    }

    private static async Task AppliesToExistingConnectedTargetAsync()
    {
        var target = CreateTarget(4103, 9335);
        var services = new FakeServices(new FakeInstances([]));
        var result = await services.CreateWorkflow().StartOrApplyAsync(
            CreateRequest(), 9335, null, target.Identity, hasActiveConnection: true, CancellationToken.None);

        Require(result.State == RuntimeStatusState.Connected && services.LaunchCount == 0 && services.Applies.Count == 1 &&
                services.Applies[0].ExpectedInstance == target.Identity,
            "已有连接实例直接应用到当前身份");
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
        bool? canConnect = null) =>
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
            CanConnect = canConnect ?? port is >= 1 and <= 65535
        };

    private static RuntimeApplyRequest CreateRequest() => new()
    {
        SkinId = "ayanami",
        RuntimeScript = "fake apply script",
        RemoveScript = "fake remove script",
        Endpoint = "http://127.0.0.1:9335"
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

        public RuntimeApplyWorkflow CreateWorkflow(TimeSpan? readinessTimeout = null, TimeSpan? pollInterval = null) =>
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
                pollInterval);
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

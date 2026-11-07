using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SkinManager.Runtime;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            await MagpieCompatibilityServiceTests.RunAsync();
            await TestDebugClientLaunchAsync();
            await TestCodexInstanceEnumerationAndActionsAsync();
            await TestExpectedInstanceAndPipeIdentityAsync();
            await TestIgnoredLargeEventsAndPayloadAllocationsAsync();
            await TestCorrelatedCommandsAndEventsAsync();
            await TestTargetLifecycleAndApplyRemoveAsync();
            await TestUnsupportedTargetsAndProtocolErrorsAsync();
            await TestRemovalFailuresAndTargetRacesAsync();
            await TestCachedStatusAndAgentDisconnectAsync();
            await TestDisconnectAndEndpointRejectionAsync();
            Console.WriteLine("SkinManager.Runtime.Tests: 全部断言通过。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task TestDebugClientLaunchAsync()
    {
        const string expectedAppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
        const string expectedAddress = "--remote-debugging-address=127.0.0.1";

        var defaultPlatform = new FakeRuntimeLaunchPlatform();
        var defaultController = CreateRuntimeController(defaultPlatform);
        var defaultStatus = await defaultController.LaunchDebugClientAsync();
        var defaultActivation = defaultPlatform.ActivationRequests.Single();
        Assert(defaultStatus.State == RuntimeStatusState.Connecting && defaultStatus.Endpoint == "http://127.0.0.1:9335",
            "debug activation with default options reports the default loopback endpoint");
        Assert(defaultActivation.AppUserModelId == expectedAppUserModelId &&
            defaultActivation.Arguments == $"{expectedAddress} --remote-debugging-port=9335" &&
            !defaultActivation.Arguments.Contains("--user-data-dir", StringComparison.Ordinal) &&
            !defaultActivation.Arguments.Contains("debug-profile", StringComparison.Ordinal),
            "default debug activation preserves the native Store identity and passes only loopback debugging arguments");

        var customPlatform = new FakeRuntimeLaunchPlatform();
        var customStatus = await CreateRuntimeController(customPlatform).LaunchDebugClientAsync(new DebugClientLaunchOptions(19442));
        var customActivation = customPlatform.ActivationRequests.Single();
        Assert(customStatus.State == RuntimeStatusState.Connecting && customStatus.Endpoint == "http://127.0.0.1:19442" &&
            customActivation.Arguments == $"{expectedAddress} --remote-debugging-port=19442",
            "a selected valid debug port is passed through the Store activation arguments and endpoint");

        var runningPlatform = new FakeRuntimeLaunchPlatform { CodexProcessRunning = true };
        var runningStatus = await CreateRuntimeController(runningPlatform).LaunchDebugClientAsync();
        Assert(runningStatus.State == RuntimeStatusState.Error && runningPlatform.ActivationRequests.Count == 0,
            "an existing Codex process blocks activation without attempting to launch another instance");

        foreach (var invalidPort in new[] { 0, 65536 })
        {
            var invalidPlatform = new FakeRuntimeLaunchPlatform();
            var invalidStatus = await CreateRuntimeController(invalidPlatform).LaunchDebugClientAsync(new DebugClientLaunchOptions(invalidPort));
            Assert(invalidStatus.State == RuntimeStatusState.Error && invalidPlatform.ActivationRequests.Count == 0,
                $"invalid debug port {invalidPort} fails before Store activation");
        }

        var unsupportedPlatform = new FakeRuntimeLaunchPlatform { IsWindows = false };
        var unsupportedStatus = await CreateRuntimeController(unsupportedPlatform).LaunchDebugClientAsync();
        Assert(unsupportedStatus.State == RuntimeStatusState.Error && unsupportedPlatform.ActivationRequests.Count == 0,
            "non-Windows launch requests fail before Store activation");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var cancelledPlatform = new FakeRuntimeLaunchPlatform();
            var cancellation = await CaptureAsync(() => CreateRuntimeController(cancelledPlatform)
                .LaunchDebugClientAsync(cancellationToken: cancelled.Token));
            Assert(cancellation is OperationCanceledException && cancelledPlatform.ActivationRequests.Count == 0,
                "a launch cancelled before activation does not activate Codex or report success");
        }

        using (var cancelled = new CancellationTokenSource())
        {
            var activationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var activationFinished = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlightPlatform = new FakeRuntimeLaunchPlatform
            {
                ActivationHandler = async _ =>
                {
                    activationStarted.TrySetResult(true);
                    return await activationFinished.Task;
                }
            };
            var publishedStates = new List<RuntimeStatusState>();
            var controller = CreateRuntimeController(inFlightPlatform);
            controller.StatusChanged += (_, status) => publishedStates.Add(status.State);
            var launch = controller.LaunchDebugClientAsync(cancellationToken: cancelled.Token);
            await activationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancelled.Cancel();
            activationFinished.TrySetResult(4321);
            var cancellation = await CaptureAsync(async () => await launch);
            Assert(cancellation is OperationCanceledException && !publishedStates.Contains(RuntimeStatusState.Connecting),
                "cancellation during activation is not converted into a successful connecting status");
        }

        var failurePlatform = new FakeRuntimeLaunchPlatform
        {
            ActivationFailure = new InvalidOperationException("fake Store activation failure")
        };
        var failureStatus = await CreateRuntimeController(failurePlatform).LaunchDebugClientAsync();
        Assert(failureStatus.State == RuntimeStatusState.Error && failurePlatform.ActivationRequests.Count == 1,
            "a failed Store activation reports an error instead of a successful connecting status");
    }

    private static RuntimeController CreateRuntimeController(IRuntimeLaunchPlatform launchPlatform) =>
        new(Path.Combine(Path.GetTempPath(), "SkinManager.Agent.launch-tests.exe"),
            "SkinManager.Runtime.Tests.Launch." + Guid.NewGuid().ToString("N"), launchPlatform);

    private static async Task TestCodexInstanceEnumerationAndActionsAsync()
    {
        var path = FakeCodexExecutablePath;
        var platform = new FakeCodexInstancePlatform();
        var normal = Candidate(4101, path, $"\"{path}\" --user-data-dir=\"C:\\Users\\A\\Codex Profile\"");
        var debugWaiting = Candidate(4102, path, $"\"{path}\" --remote-debugging-port=19442 --user-data-dir=\"D:\\Profiles\\Debug Waiting\"");
        var debugReady = Candidate(4103, path, $"\"{path}\" --remote-debugging-port 19443 --user-data-dir \"D:\\Profiles\\Debug Ready\"");
        var renderer = Candidate(4104, path, $"\"{path}\" --type=renderer --remote-debugging-port=19444") with { ParentProcessId = 4102 };
        var wrongProduct = Candidate(4105, path, $"\"{path}\" --user-data-dir=\"C:\\Other Profile\"") with { ProductName = "ChatGPT" };
        var wrongPath = Candidate(4106, @"C:\Program Files\ChatGPT\ChatGPT.exe", $"\"C:\\Program Files\\ChatGPT\\ChatGPT.exe\" --user-data-dir=\"C:\\Other Profile\"");
        var managerBranch = Candidate(5550, path, $"\"{path}\" --type=utility");
        var appServerPath = @"C:\Users\X\AppData\Local\OpenAI\Codex\bin\fake-build\codex.exe";
        var debugChild = Candidate(4106, appServerPath, $"\"{appServerPath}\" run --server");
        var debugOtherChild = Candidate(4105, path, $"\"{path}\" --type=utility");
        Assert(WindowsCodexInstancePlatform.MatchesExpectedProcessIdentity(
                debugChild.Identity, debugChild.Identity.ProcessId, debugChild.Identity.StartedAtUtc.UtcDateTime, appServerPath) &&
            !WindowsCodexInstancePlatform.MatchesExpectedProcessIdentity(
                debugChild.Identity, debugChild.Identity.ProcessId, debugChild.Identity.StartedAtUtc.AddSeconds(1).UtcDateTime, appServerPath),
            "the generic descendant identity check accepts codex.exe app-server by exact PID/start/path without requiring ChatGPT.exe product metadata");
        platform.Candidates.AddRange([normal, debugWaiting, debugReady, renderer, wrongProduct, wrongPath]);
        foreach (var candidate in platform.Candidates)
            platform.CurrentIdentities[candidate.Identity.ProcessId] = candidate.Identity;
        platform.CurrentIdentities[4105] = debugOtherChild.Identity;
        platform.CurrentIdentities[4106] = debugChild.Identity;
        platform.CurrentIdentities[5550] = managerBranch.Identity;
        platform.Relations.AddRange([
            new(4101, 100, "ChatGPT.exe"),
            new(4102, 100, "ChatGPT.exe"),
            new(4103, 100, "ChatGPT.exe"),
            new(4104, 4102, "ChatGPT.exe"),
            new(4105, 4103, "codex.exe"),
            new(4106, 4104, "codex.exe"),
            new(5550, Environment.ProcessId, "codex.exe")
        ]);
        platform.Windows[4101] = [new CodexWindowInfo(14101, "Codex normal")];
        platform.Windows[4102] = [new CodexWindowInfo(14102, "Codex debug waiting"), new CodexWindowInfo(14103, "Other owned window")];
        platform.Windows[4103] = [new CodexWindowInfo(14104, "Codex debug ready")];
        platform.Windows[4104] = [new CodexWindowInfo(14105, "Renderer")];
        platform.ListenerOwners[19442] = [4999];
        platform.ListenerOwners[19443] = [4103];

        var service = new WindowsCodexInstanceService(platform);
        var instances = await service.GetInstancesAsync();
        Assert(instances.Count == 3, "instance enumeration keeps only verified Codex main processes and excludes child renderers and wrong identities");
        var normalInstance = instances.Single(instance => instance.Identity.ProcessId == 4101);
        var waitingInstance = instances.Single(instance => instance.Identity.ProcessId == 4102);
        var readyInstance = instances.Single(instance => instance.Identity.ProcessId == 4103);
        Assert(normalInstance.Kind == CodexInstanceKind.Normal && !normalInstance.CanConnect && normalInstance.DebugPort is null,
            "ordinary instances are identified separately from debug instances");
        Assert(normalInstance.WindowTitle == "Codex normal" && normalInstance.WindowHandle == 14101 && normalInstance.UserDataDirectory == @"C:\Users\A\Codex Profile",
            "quoted command-line paths with spaces and owning visible window identity are preserved");
        Assert(waitingInstance.Kind == CodexInstanceKind.Debug && !waitingInstance.CanConnect && waitingInstance.DebugPort == 19442 &&
            waitingInstance.UserDataDirectory == @"D:\Profiles\Debug Waiting",
            "a requested debug mode remains labeled Debug while its listener is not owned and ready");
        Assert(readyInstance.Kind == CodexInstanceKind.Debug && readyInstance.CanConnect && readyInstance.DebugPort == 19443 &&
            readyInstance.UserDataDirectory == @"D:\Profiles\Debug Ready",
            "debug connectivity is enabled only when the selected process uniquely owns its TCP listener");

        var located = await service.LocateAsync(waitingInstance);
        Assert(located == CodexLocateResult.Located && platform.LocatedWindows.SequenceEqual([14102L]) &&
            platform.LocatedIdentities.SequenceEqual([waitingInstance.Identity]),
            "locate restores and foregrounds the selected instance's current owned top-level window");

        var normalService = new WindowsCodexInstanceService(platform, TimeSpan.Zero);
        platform.NativeQuitResponses[4101] = new(CodexNativeQuitRequestState.Requested, "fake native quit");
        platform.QuitExitProcessIds[4101] = [4101];
        var gracefulClose = await normalService.CloseAsync(normalInstance);
        Assert(gracefulClose.Outcome == CodexCloseResult.Closed && platform.NativeQuitRequests.Last().ProcessId == 4101 && platform.TerminatedPids.Count == 0,
            "a verified fake native Quit that exits the selected process reports Closed without force termination");

        platform.NativeQuitResponses[4102] = new(CodexNativeQuitRequestState.Unavailable, "fake unavailable");
        var close = await normalService.CloseAsync(waitingInstance);
        Assert(close.Outcome == CodexCloseResult.NativeQuitUnavailable && close.Instance.PendingExitSnapshot?.CanAttemptForce == true &&
            platform.NativeQuitRequests.Last().ProcessId == 4102 && platform.TerminatedPids.Count == 0,
            "missing native Quit remains explicit, preserves the verified process snapshot, and never treats hidden/no-window as closed");
        var forced = await normalService.CloseAsync(close.Instance, force: true);
        Assert(forced.Outcome == CodexCloseResult.Closed && platform.TerminatedPids.SequenceEqual([4102, 4104, 4106]) &&
            !platform.TerminatedPids.Contains(4103) && !platform.TerminatedPids.Contains(4105) && !platform.TerminatedPids.Contains(5550),
            "confirmed force terminates selected root then its verified subtree, excluding another Codex instance and manager branch");

        var noWindowCandidate = Candidate(4107, path, $"\"{path}\" --user-data-dir=\"C:\\No Window\"");
        platform.Candidates.Add(noWindowCandidate);
        platform.CurrentIdentities[4107] = noWindowCandidate.Identity;
        platform.Relations.Add(new(4107, 100, "ChatGPT.exe"));
        var noWindowInstance = (await service.GetInstancesAsync()).Single(instance => instance.Identity.ProcessId == 4107);
        Assert(await service.LocateAsync(noWindowInstance) == CodexLocateResult.NoWindow,
            "an instance without a visible window reports NoWindow for locate");
        platform.NativeQuitResponses[4107] = new(CodexNativeQuitRequestState.Unavailable, "no menu");
        var noWindowClose = await normalService.CloseAsync(noWindowInstance);
        Assert(noWindowClose.Outcome == CodexCloseResult.NativeQuitUnavailable && noWindowClose.Instance.PendingExitSnapshot is not null,
            "a hidden/no-window instance still gets a verified native Quit attempt and remains eligible for separate force confirmation");

        var priorLocatedCount = platform.LocatedWindows.Count;
        platform.CurrentIdentities[4102] = waitingInstance.Identity with { StartedAtUtc = waitingInstance.Identity.StartedAtUtc.AddSeconds(1) };
        var reusedPidError = await CaptureAsync(() => service.LocateAsync(waitingInstance));
        Assert(reusedPidError is InvalidOperationException && reusedPidError.Message.Contains("刷新", StringComparison.Ordinal) &&
            platform.LocatedWindows.Count == priorLocatedCount,
            "PID reuse is rejected before a stale selected window can be located");

        platform.CurrentIdentities[4102] = waitingInstance.Identity with { ExecutablePath = @"C:\Program Files\ChatGPT.exe" };
        var changedPathError = await CaptureAsync(() => service.LocateAsync(waitingInstance));
        Assert(changedPathError is InvalidOperationException && platform.LocatedWindows.Count == priorLocatedCount,
            "an executable path change is rejected even when the selected PID and start time match");

        platform.CurrentIdentities.Remove(4102);
        platform.AccessDeniedIdentities.Add(4103);
        var deniedError = await CaptureAsync(async () => await service.LocateAsync(readyInstance));
        Assert(deniedError is InvalidOperationException && deniedError.Message.Contains("刷新", StringComparison.Ordinal) &&
            !platform.LocatedIdentities.Contains(readyInstance.Identity),
            "an identity that cannot be re-read because of access denial is rejected with a refreshable error");

        await TestForceExitSnapshotEdgeCasesAsync(path);
    }

    private static async Task TestForceExitSnapshotEdgeCasesAsync(string path)
    {
        var lateRoot = Candidate(4201, path, $"\"{path}\" --user-data-dir=late");
        var latePlatform = new FakeCodexInstancePlatform();
        latePlatform.AddRoot(lateRoot, parentProcessId: 90);
        latePlatform.NativeQuitResponses[4201] = new(CodexNativeQuitRequestState.Unavailable, "fake unavailable");
        var lateService = new WindowsCodexInstanceService(latePlatform, TimeSpan.Zero);
        var lateInstance = (await lateService.GetInstancesAsync()).Single();
        var unavailable = await lateService.CloseAsync(lateInstance);
        var lateChild = Candidate(4202, path, $"\"{path}\" --type=utility");
        latePlatform.AddChild(lateChild.Identity, parentProcessId: 4201);
        var lateForced = await lateService.CloseAsync(unavailable.Instance, force: true);
        Assert(lateForced.Outcome == CodexCloseResult.Closed && latePlatform.TerminatedPids.SequenceEqual([4201, 4202]),
            "force refreshes a still-running selected root and captures children created after the first snapshot");

        var orphanRoot = Candidate(4301, path, $"\"{path}\" --user-data-dir=orphan");
        var orphanChild = Candidate(4302, path, $"\"{path}\" --type=utility");
        var orphanPlatform = new FakeCodexInstancePlatform();
        orphanPlatform.AddRoot(orphanRoot, 90);
        orphanPlatform.AddChild(orphanChild.Identity, orphanRoot.Identity.ProcessId);
        orphanPlatform.NativeQuitResponses[4301] = new(CodexNativeQuitRequestState.Requested, "quit root only");
        orphanPlatform.QuitExitProcessIds[4301] = [4301];
        var orphanService = new WindowsCodexInstanceService(orphanPlatform, TimeSpan.Zero);
        var orphanInstance = (await orphanService.GetInstancesAsync()).Single();
        var rootExited = await orphanService.CloseAsync(orphanInstance);
        var refreshed = await orphanService.GetInstancesAsync();
        var residual = refreshed.Single(instance => instance.Identity.ProcessId == 4301);
        Assert(rootExited.Outcome == CodexCloseResult.StillRunning && rootExited.Instance.IsExitResidual &&
            residual.IsExitResidual && residual.PendingExitSnapshot?.Processes.Any(entry => entry.Identity.ProcessId == 4302) == true,
            "a main process exit with a live helper is not Closed, and refresh retains a residual row for follow-up");
        var residualForce = await orphanService.CloseAsync(residual, force: true);
        Assert(residualForce.Outcome == CodexCloseResult.Closed && orphanPlatform.TerminatedPids.SequenceEqual([4302]),
            "a residual row can continue force cleanup after its original main PID exited");

        var asyncRoot = Candidate(4401, path, $"\"{path}\" --user-data-dir=async");
        var asyncChild = Candidate(4402, path, $"\"{path}\" --type=utility");
        var asyncPlatform = new FakeCodexInstancePlatform();
        asyncPlatform.AddRoot(asyncRoot, 90);
        asyncPlatform.AddChild(asyncChild.Identity, asyncRoot.Identity.ProcessId);
        asyncPlatform.NativeQuitResponses[4401] = new(CodexNativeQuitRequestState.Unavailable, "fake unavailable");
        asyncPlatform.TerminationObservationDelay[4401] = 3;
        var spawnedChild = Candidate(4403, path, $"\"{path}\" --type=utility");
        asyncPlatform.OnTerminate[4401] = () => asyncPlatform.AddChild(spawnedChild.Identity, 4401);
        var asyncService = new WindowsCodexInstanceService(asyncPlatform, TimeSpan.FromSeconds(2));
        var asyncInstance = (await asyncService.GetInstancesAsync()).Single();
        var asyncPending = await asyncService.CloseAsync(asyncInstance);
        var asyncForce = await asyncService.CloseAsync(asyncPending.Instance, force: true);
        Assert(asyncForce.Outcome == CodexCloseResult.Closed && asyncPlatform.TerminatedPids.SequenceEqual([4401, 4402, 4403]),
            "force waits for asynchronous parent exit, tracks a child born during termination, and then processes descendants");

        var reusedParent = Candidate(4501, path, $"\"{path}\" --user-data-dir=reused-parent");
        var olderChild = Candidate(4502, path, $"\"{path}\" --type=utility") with
        {
            Identity = Candidate(4502, path, $"\"{path}\"").Identity with { StartedAtUtc = reusedParent.Identity.StartedAtUtc.AddSeconds(-1) }
        };
        var oldChildDescendant = Candidate(4503, path, $"\"{path}\" --type=utility");
        var reusedParentPlatform = new FakeCodexInstancePlatform();
        reusedParentPlatform.AddRoot(reusedParent, 90);
        reusedParentPlatform.AddChild(olderChild.Identity, reusedParent.Identity.ProcessId);
        reusedParentPlatform.AddChild(oldChildDescendant.Identity, olderChild.Identity.ProcessId);
        reusedParentPlatform.NativeQuitResponses[4501] = new(CodexNativeQuitRequestState.Unavailable, "fake unavailable");
        var reusedParentService = new WindowsCodexInstanceService(reusedParentPlatform, TimeSpan.Zero);
        var reusedParentInstance = (await reusedParentService.GetInstancesAsync()).Single();
        var unsafeSnapshot = await reusedParentService.CloseAsync(reusedParentInstance);
        var unsafeForce = await reusedParentService.CloseAsync(unsafeSnapshot.Instance, force: true);
        Assert(unsafeForce.Outcome == CodexCloseResult.Closed && reusedParentPlatform.TerminatedPids.SequenceEqual([4501]) &&
            reusedParentPlatform.CurrentIdentities.ContainsKey(4502) && reusedParentPlatform.CurrentIdentities.ContainsKey(4503),
            "a process branch predating the current parent PID is excluded as foreign, so it is not killed or kept as a residual");

        var foreignReuseRoot = Candidate(4511, path, $"\"{path}\" --user-data-dir=foreign-reuse");
        var foreignReuseChild = Candidate(4512, path, $"\"{path}\" --type=utility") with
        {
            Identity = Candidate(4512, path, $"\"{path}\"").Identity with { StartedAtUtc = foreignReuseRoot.Identity.StartedAtUtc.AddSeconds(-1) }
        };
        var foreignReusePlatform = new FakeCodexInstancePlatform();
        foreignReusePlatform.AddRoot(foreignReuseRoot, 90);
        foreignReusePlatform.AddChild(foreignReuseChild.Identity, foreignReuseRoot.Identity.ProcessId);
        foreignReusePlatform.NativeQuitResponses[4511] = new(CodexNativeQuitRequestState.Unavailable, "fake unavailable");
        var foreignReuseService = new WindowsCodexInstanceService(foreignReusePlatform, TimeSpan.Zero);
        var foreignReuseInstance = (await foreignReuseService.GetInstancesAsync()).Single();
        var foreignPending = await foreignReuseService.CloseAsync(foreignReuseInstance);
        foreignReusePlatform.CurrentIdentities[4512] = foreignReuseChild.Identity with
        {
            StartedAtUtc = foreignReuseRoot.Identity.StartedAtUtc.AddSeconds(1)
        };
        var foreignReuseForce = await foreignReuseService.CloseAsync(foreignPending.Instance, force: true);
        Assert(foreignReuseForce.Outcome == CodexCloseResult.Closed && foreignReusePlatform.TerminatedPids.SequenceEqual([4511, 4512]),
            "a foreign-exclusion PID that is reused by a newly verified owned child is reevaluated instead of remaining blocked by PID alone");

        var racingRoot = Candidate(4801, path, $"\"{path}\" --user-data-dir=racing-root");
        var racingChild = Candidate(4802, path, $"\"{path}\" --type=utility");
        var racingPlatform = new FakeCodexInstancePlatform();
        racingPlatform.AddRoot(racingRoot, 90);
        racingPlatform.AddChild(racingChild.Identity, racingRoot.Identity.ProcessId);
        racingPlatform.NativeQuitResponses[4801] = new(CodexNativeQuitRequestState.Unavailable, "fake unavailable");
        var racingService = new WindowsCodexInstanceService(racingPlatform, TimeSpan.Zero);
        var racingInstance = (await racingService.GetInstancesAsync()).Single();
        var racingPending = await racingService.CloseAsync(racingInstance);
        racingPlatform.OnNextEnumerateRelations = () => racingPlatform.CurrentIdentities.Remove(4801);
        var racingForce = await racingService.CloseAsync(racingPending.Instance, force: true);
        Assert(racingForce.Outcome == CodexCloseResult.Closed && racingPlatform.TerminatedPids.SequenceEqual([4802]),
            "if the main PID exits between force validation and resnapshot, the last verified snapshot still cleans up its live child");

        var requestRoot = Candidate(4901, path, $"\"{path}\" --user-data-dir=request-race");
        var requestChild = Candidate(4902, path, $"\"{path}\" --type=utility");
        var requestPlatform = new FakeCodexInstancePlatform();
        requestPlatform.AddRoot(requestRoot, 90);
        requestPlatform.AddChild(requestChild.Identity, requestRoot.Identity.ProcessId);
        requestPlatform.OnNativeQuit[4901] = () =>
        {
            requestPlatform.CurrentIdentities.Remove(4901);
            throw new InvalidOperationException("fake main process exited while sending native Quit");
        };
        var requestService = new WindowsCodexInstanceService(requestPlatform, TimeSpan.Zero);
        var requestInstance = (await requestService.GetInstancesAsync()).Single();
        var requestError = await CaptureAsync(() => requestService.CloseAsync(requestInstance));
        var requestResiduals = await requestService.GetInstancesAsync();
        var requestResidual = requestResiduals.Single(instance => instance.Identity.ProcessId == 4901);
        var requestForce = await requestService.CloseAsync(requestResidual, force: true);
        Assert(requestError is InvalidOperationException && requestResidual.IsExitResidual &&
            requestResidual.PendingExitSnapshot?.Processes.Any(entry => entry.Identity.ProcessId == 4902) == true &&
            requestForce.Outcome == CodexCloseResult.Closed && requestPlatform.TerminatedPids.SequenceEqual([4902]),
            "a native Quit send failure after the root exits preserves the pre-recorded helper for refresh and separate force cleanup");

        var deniedRoot = Candidate(4601, path, $"\"{path}\" --user-data-dir=denied");
        var deniedChild = Candidate(4602, path, $"\"{path}\" --type=utility");
        var deniedPlatform = new FakeCodexInstancePlatform();
        deniedPlatform.AddRoot(deniedRoot, 90);
        deniedPlatform.AddChild(deniedChild.Identity, deniedRoot.Identity.ProcessId);
        deniedPlatform.NativeQuitResponses[4601] = new(CodexNativeQuitRequestState.Unavailable, "fake unavailable");
        var deniedService = new WindowsCodexInstanceService(deniedPlatform, TimeSpan.Zero);
        var deniedInstance = (await deniedService.GetInstancesAsync()).Single();
        var deniedPending = await deniedService.CloseAsync(deniedInstance);
        deniedPlatform.AccessDeniedIdentities.Add(4602);
        var deniedForce = await deniedService.CloseAsync(deniedPending.Instance, force: true);
        Assert(deniedForce.Outcome == CodexCloseResult.Incomplete && !deniedPlatform.TerminatedPids.Contains(4602),
            "an inaccessible recorded helper remains incomplete and is not treated as exited or terminated");

        var reusedRoot = Candidate(4701, path, $"\"{path}\" --user-data-dir=reused-root");
        var reusedPlatform = new FakeCodexInstancePlatform();
        reusedPlatform.AddRoot(reusedRoot, 90);
        reusedPlatform.NativeQuitResponses[4701] = new(CodexNativeQuitRequestState.Unavailable, "fake unavailable");
        var reusedService = new WindowsCodexInstanceService(reusedPlatform, TimeSpan.Zero);
        var reusedInstance = (await reusedService.GetInstancesAsync()).Single();
        var reusedPending = await reusedService.CloseAsync(reusedInstance);
        reusedPlatform.CurrentIdentities[4701] = reusedRoot.Identity with { StartedAtUtc = reusedRoot.Identity.StartedAtUtc.AddSeconds(1) };
        var terminatedBeforeReuse = reusedPlatform.TerminatedPids.Count;
        var reusedForce = await reusedService.CloseAsync(reusedPending.Instance, force: true);
        Assert(reusedForce.Outcome == CodexCloseResult.IdentityChanged && reusedPlatform.TerminatedPids.Count == terminatedBeforeReuse,
            "a PID reused before force is rejected without targeting its replacement");
    }

    private static async Task TestExpectedInstanceAndPipeIdentityAsync()
    {
        var expected = FakeConnectedIdentity;
        var other = expected with { ProcessId = expected.ProcessId + 1 };
        await using (var fake = new FakeCdpServer())
        {
            var error = await CaptureAsync(() => CdpRuntimeSession.ConnectAndApplyAsync(
                fake.Endpoint,
                Request("wrong-instance", "apply();", "restore();", fake.Endpoint.ToString()) with { ExpectedInstance = other },
                new FakeIdentityVerifier(),
                CancellationToken.None));
            Assert(error is InvalidOperationException && error.Message.Contains("另一个 Codex 实例", StringComparison.Ordinal) && fake.Commands.Length == 0,
                "an endpoint owned by a different PID is rejected before opening CDP or injecting a script");
        }

        await using (var fake = new FakeCdpServer())
        await using (var coordinator = new RuntimeCoordinator(new FakeIdentityVerifier()))
        {
            var apply = await coordinator.HandleAsync(new AgentCommand
            {
                RequestId = Guid.NewGuid(),
                Operation = AgentOperation.Apply,
                ApplyRequest = Request("expected-instance", "apply();", "restore();", fake.Endpoint.ToString()) with { ExpectedInstance = expected }
            }, CancellationToken.None);
            Assert(apply.State == RuntimeStatusState.Connected && apply.ConnectedInstance == expected,
                "connected runtime status reports the process identity verified from the actual endpoint listener");

            var commandCount = fake.Commands.Length;
            var mismatch = await coordinator.HandleAsync(new AgentCommand
            {
                RequestId = Guid.NewGuid(),
                Operation = AgentOperation.Apply,
                ApplyRequest = Request("wrong-reuse", "wrong();", "restore();", fake.Endpoint.ToString()) with { ExpectedInstance = other }
            }, CancellationToken.None);
            Assert(mismatch.State == RuntimeStatusState.Error && fake.Commands.Length == commandCount && mismatch.ConnectedInstance == expected,
                "reusing a session rejects a mismatched expected instance without updating or injecting scripts");
        }

        var requestId = Guid.NewGuid();
        var status = new RuntimeStatus(RuntimeStatusState.Connected, "pipe", "http://127.0.0.1:19443", "connected", true, 2, expected);
        var wire = AgentResponse.From(requestId, status);
        var serialized = JsonSerializer.Serialize(wire, AgentPipeProtocol.JsonOptions);
        var decoded = JsonSerializer.Deserialize<AgentResponse>(serialized, AgentPipeProtocol.JsonOptions)!;
        var roundTrip = decoded.ToStatus();
        Assert(decoded.RequestId == requestId && roundTrip.ConnectedInstance == expected && roundTrip.AppliedTargetCount == 2,
            "the Agent pipe response carries ConnectedInstance identity through JSON serialization");
    }

    private static CodexProcessCandidate Candidate(int processId, string executablePath, string commandLine) => new(
        new CodexProcessIdentity(processId, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + processId), Path.GetFullPath(executablePath)),
        "Codex",
        commandLine,
        null);

    internal static string FakeCodexExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "WindowsApps",
        "OpenAI.Codex_1.2.3.4_x64__2p2nqsd0c76g0",
        "app",
        "ChatGPT.exe");

    private static CodexProcessIdentity FakeConnectedIdentity => new(
        4242,
        FakeIdentityVerifier.StartedAtUtc,
        Path.GetFullPath(FakeCodexExecutablePath));

    private static async Task TestIgnoredLargeEventsAndPayloadAllocationsAsync()
    {
        TestCdpMessageBufferAllocations();

        await using var fake = new FakeCdpServer();
        await using var client = await CdpBrowserClient.ConnectAsync(fake.Endpoint, new FakeIdentityVerifier(), CancellationToken.None);
        var largeConsoleText = new string('x', 512 * 1024);
        var ignoredParameters = new { args = new[] { new { type = "string", value = largeConsoleText } } };
        for (var index = 0; index < 6; index++)
            await fake.SendEventAsync("Runtime.consoleAPICalled", ignoredParameters);

        await fake.SendEventAsync("Target.targetCreated", new
        {
            targetInfo = new { targetId = "retained-page", type = "page", url = "app://codex/home" }
        });

        var retained = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert(retained.Method == "Target.targetCreated" &&
            retained.Parameters.GetProperty("targetInfo").GetProperty("targetId").GetString() == "retained-page",
            "a required target event is retained after a sequence of large unconsumed console events");
        Assert(!client.Events.TryRead(out _),
            "unconsumed Runtime.consoleAPICalled payloads are filtered before event cloning or channel enqueue");
    }

    private static void TestCdpMessageBufferAllocations()
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteString("large", new string('x', 1024 * 1024));
            writer.WriteEndObject();
        }

        _ = payload.ToArray();
        _ = CdpBrowserClient.GetWrittenMemory(payload);
        var copyAllocation = MeasureToArrayAllocation(payload);
        var viewAllocation = MeasureBufferViewAllocation(payload);

        Assert(copyAllocation >= payload.Length,
            "the allocation baseline confirms MemoryStream.ToArray copies the complete CDP payload");
        Assert(viewAllocation < copyAllocation,
            "the production buffer slice allocates less than the former ToArray payload copy");
        Console.WriteLine($"CDP payload allocation comparison: ToArray={copyAllocation:N0} bytes, buffer view={viewAllocation:N0} bytes.");
    }

    private static long MeasureToArrayAllocation(MemoryStream payload)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var copy = payload.ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(copy);
        return allocated;
    }

    private static long MeasureBufferViewAllocation(MemoryStream payload)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var view = CdpBrowserClient.GetWrittenMemory(payload);
        var length = view.Length;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (length != payload.Length)
            throw new InvalidOperationException("The buffer view did not include exactly the written payload bytes.");
        return allocated;
    }

    private static async Task TestCorrelatedCommandsAndEventsAsync()
    {
        await using var fake = new FakeCdpServer();
        await using var client = await CdpBrowserClient.ConnectAsync(fake.Endpoint, new FakeIdentityVerifier(), CancellationToken.None);

        var slow = client.SendAsync("Test.slow");
        await fake.WaitForCommandAsync(command => command.Method == "Test.slow");
        var fast = client.SendAsync("Test.fast");
        await fake.WaitForCommandAsync(command => command.Method == "Test.fast");
        var results = await Task.WhenAll(slow, fast);
        Assert(results[0].GetProperty("value").GetString() == "slow", "out-of-order CDP responses resolve to the matching command ID");
        Assert(results[1].GetProperty("value").GetString() == "fast", "parallel CDP command response keeps its own correlation ID");

        await fake.SendEventAsync("Target.targetCreated", new
        {
            targetInfo = new { targetId = "event-page", type = "page", url = "app://codex/home" }
        });
        var cdpEvent = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert(cdpEvent.Method == "Target.targetCreated" && cdpEvent.Parameters.GetProperty("targetInfo").GetProperty("targetId").GetString() == "event-page",
            "CDP target events are delivered with their parameters");
    }

    private static async Task TestTargetLifecycleAndApplyRemoveAsync()
    {
        await using var fake = new FakeCdpServer
        {
            InitialTargets =
            [
                new { targetId = "page-one", type = "page", url = "app://-/index.html?initialRoute=home" }
            ]
        };
        await using var session = await CdpRuntimeSession.ConnectAndApplyAsync(fake.Endpoint,
            Request("skin-one", "apply-one();", "remove-one();"), new FakeIdentityVerifier(), CancellationToken.None);

        var initialAdd = await fake.WaitForCommandAsync(command => command.Method == "Page.addScriptToEvaluateOnNewDocument");
        Assert(initialAdd.SessionId == "session-page-one", "initial Codex page gets an attached CDP session");
        var earlySource = initialAdd.Parameters.GetProperty("source").GetString()!;
        Assert(earlySource.Contains("apply-one();", StringComparison.Ordinal), "first early script contains the active opaque runtime payload");
        Assert(!earlySource.Contains("new Function", StringComparison.Ordinal) && !earlySource.Contains("eval(", StringComparison.Ordinal) &&
            earlySource.Contains("location.protocol==='app:'", StringComparison.Ordinal) && earlySource.Contains("data-app-shell-frame", StringComparison.Ordinal) &&
            earlySource.Contains("data-app-shell-main-surface", StringComparison.Ordinal),
            "early guard uses trusted lexical code, strict app URL and Codex DOM markers without eval");
        Assert(earlySource.IndexOf("apply-one();", StringComparison.Ordinal) < earlySource.IndexOf("window[key]=true", StringComparison.Ordinal),
            "the guard token is set only after the compiled script runs successfully");
        Assert(await fake.HasCommandAsync(command => command.Method == "Runtime.evaluate" && HasExpression(command, "apply-one();")),
            "first payload is evaluated on the current Codex document");
        Assert(session.AppliedTargetCount == 1, "a successful current-page evaluation counts as applied");

        await session.UpdateAsync(Request("skin-two", "apply-two();", "remove-two();"), CancellationToken.None);
        Assert(await fake.HasCommandAsync(command => command.Method == "Page.removeScriptToEvaluateOnNewDocument" &&
            command.Parameters.GetProperty("identifier").GetString() == "script-1"), "update removes the previous early script before replacing it");
        Assert(!fake.Commands.Any(command => command.Method == "Runtime.evaluate" && HasExpression(command, "remove-one();")),
            "update retains the existing runtime package and does not run the full remove script");
        var updatedAdd = await fake.WaitForCommandAsync(command => command.Method == "Page.addScriptToEvaluateOnNewDocument" &&
            command.Parameters.GetProperty("source").GetString()!.Contains("apply-two();", StringComparison.Ordinal));
        Assert(updatedAdd.SessionId == "session-page-one", "updated runtime script is registered on the existing page");
        Assert(session.AppliedTargetCount == 1, "a successful in-place update remains applied");

        var afterUpdate = fake.LastSequence;
        await fake.SendEventAsync("Target.targetCreated", new { targetInfo = new { targetId = "page-two", type = "page", url = "app://codex/conversation" } });
        var secondPageAdd = await fake.WaitForCommandAsync(command => command.Sequence > afterUpdate && command.Method == "Page.addScriptToEvaluateOnNewDocument");
        Assert(secondPageAdd.SessionId == "session-page-two" && secondPageAdd.Parameters.GetProperty("source").GetString()!.Contains("apply-two();", StringComparison.Ordinal),
            "targetCreated applies the current skin to a new Codex page");
        await fake.WaitForCommandAsync(command => command.Sequence > secondPageAdd.Sequence && command.Method == "Runtime.evaluate" &&
            command.SessionId == "session-page-two" && HasExpression(command, "apply-two();"));

        var beforeInfo = fake.LastSequence;
        await fake.SendEventAsync("Target.targetInfoChanged", new { targetInfo = new { targetId = "page-two", type = "page", url = "app://codex/conversation", title = "updated" } });
        await fake.SendEventAsync("Page.loadEventFired", new { timestamp = 1.0 }, "session-page-two");
        var loadProbe = await fake.WaitForCommandAsync(command => command.Sequence > beforeInfo && command.Method == "Runtime.evaluate" && command.SessionId == "session-page-two");
        Assert(!HasExpression(loadProbe, "apply-two();"), "load events use a small guard-state probe instead of resending the large skin payload");
        Assert(!fake.Commands.Any(command => command.Sequence > beforeInfo && command.Method == "Page.addScriptToEvaluateOnNewDocument" &&
            command.Parameters.GetProperty("source").GetString()!.Contains("apply-two();", StringComparison.Ordinal)),
            "a page load does not register another full early payload");

        var beforeMainContext = fake.LastSequence;
        await fake.SendEventAsync("Runtime.executionContextCreated", new
        {
            context = new { id = 7, origin = "app://codex/conversation", name = "", auxData = new { isDefault = true } }
        }, "session-page-two");
        var mainContextProbe = await fake.WaitForCommandAsync(command => command.Sequence > beforeMainContext && command.Method == "Runtime.evaluate" && command.SessionId == "session-page-two");
        Assert(!HasExpression(mainContextProbe, "apply-two();"), "main/default execution-context changes use only a small state probe");

        var beforeThirdTarget = fake.LastSequence;
        await fake.SendEventAsync("Target.targetCreated", new { targetInfo = new { targetId = "page-three", type = "page", url = "app://codex/home" } });
        var thirdPageAdd = await fake.WaitForCommandAsync(command => command.Sequence > beforeThirdTarget && command.Method == "Page.addScriptToEvaluateOnNewDocument");
        Assert(thirdPageAdd.SessionId == "session-page-three", "a second targetCreated event attaches another Codex page");

        var beforeNavigate = fake.LastSequence;
        await fake.SendEventAsync("Target.targetInfoChanged", new { targetInfo = new { targetId = "page-three", type = "page", url = "https://example.com/" } });
        var leavingHook = await fake.WaitForCommandAsync(command => command.Sequence > beforeNavigate && command.Method == "Page.removeScriptToEvaluateOnNewDocument" && command.SessionId == "session-page-three");
        Assert(leavingHook.SessionId == "session-page-three", "leaving the app URL removes the early hook before detaching");
        Assert(!fake.Commands.Any(command => command.Sequence > beforeNavigate && command.Method == "Runtime.evaluate" && command.SessionId == "session-page-three" && HasExpression(command, "remove-two();")),
            "the opaque remove script is not evaluated on a regular web page");

        var beforeDestroy = fake.LastSequence;
        await fake.SendEventAsync("Target.targetDestroyed", new { targetId = "page-one" });
        await fake.SendEventAsync("Page.loadEventFired", new { timestamp = 2.0 }, "session-page-two");
        await fake.WaitForCommandAsync(command => command.Sequence > beforeDestroy && command.Method == "Runtime.evaluate" && command.SessionId == "session-page-two");
        var beforeFinalRemove = fake.LastSequence;
        await session.RemoveAndStopAsync(CancellationToken.None);
        Assert(await fake.HasCommandAsync(command => command.Method == "Runtime.evaluate" && HasExpression(command, "remove-two();")),
            "remove executes the latest opaque restore script on remaining Codex pages");
        var removalExpression = fake.Commands.First(command => command.Method == "Runtime.evaluate" && HasExpression(command, "remove-two();"))
            .Parameters.GetProperty("expression").GetString()!;
        var observerDisconnect = removalExpression.IndexOf("observer.disconnect()", StringComparison.Ordinal);
        var removePayload = removalExpression.IndexOf("remove-two();", StringComparison.Ordinal);
        var tokenDelete = removePayload < 0 ? -1 : removalExpression.IndexOf("delete window[key]", removePayload, StringComparison.Ordinal);
        Assert(removalExpression.Contains("const wasApplied=window[key]===true", StringComparison.Ordinal) &&
            removalExpression.Contains("if(hasMarkers||wasApplied)", StringComparison.Ordinal) &&
            observerDisconnect >= 0 && observerDisconnect < removePayload && removePayload < tokenDelete,
            "remove disconnects its guard and runs the opaque restore when previously applied even if DOM markers are temporarily absent");
        Assert(await fake.HasCommandAsync(command => command.Method == "Target.setDiscoverTargets" &&
            command.Parameters.TryGetProperty("discover", out var discover) && discover.ValueKind == JsonValueKind.False),
            "remove disables target discovery before disconnecting");
        Assert(!fake.Commands.Any(command => command.Sequence > beforeFinalRemove && command.Method == "Page.removeScriptToEvaluateOnNewDocument" && command.SessionId == "session-page-one" &&
            command.Parameters.GetProperty("identifier").GetString() == "script-2"), "destroyed targets are discarded from the lifecycle map");

        await using (var noMarkers = new FakeCdpServer
        {
            InitialTargets = [new { targetId = "waiting-page", type = "page", url = "app://codex/home" }],
            EvaluateValue = false
        })
        await using (var waitingSession = await CdpRuntimeSession.ConnectAndApplyAsync(noMarkers.Endpoint,
            Request("waiting", "waited-script();", "remove-waited();"), new FakeIdentityVerifier(), CancellationToken.None))
        {
            Assert(waitingSession.AppliedTargetCount == 0, "an app URL without Codex DOM markers is not reported as applied");
            var binding = noMarkers.Commands.First(command => command.Method == "Runtime.addBinding").Parameters.GetProperty("name").GetString()!;
            var firstEarlyScript = noMarkers.Commands.First(command => command.Method == "Page.addScriptToEvaluateOnNewDocument");
            var firstToken = GetAppliedToken(firstEarlyScript);
            await waitingSession.UpdateAsync(Request("waiting-two", "waited-two();", "remove-waited-two();"), CancellationToken.None);
            var updatedEarlyScript = await noMarkers.WaitForCommandAsync(command => command.Method == "Page.addScriptToEvaluateOnNewDocument" &&
                command.Parameters.GetProperty("source").GetString()!.Contains("waited-two();", StringComparison.Ordinal));
            var updatedToken = GetAppliedToken(updatedEarlyScript);
            Assert(firstToken != updatedToken, "each updated skin gets a new guard token for delayed apply notifications");

            var beforeStaleBinding = noMarkers.LastSequence;
            await noMarkers.SendEventAsync("Runtime.bindingCalled", new { name = binding, payload = firstToken, executionContextId = 7 }, "session-waiting-page");
            await noMarkers.SendEventAsync("Page.loadEventFired", new { timestamp = 6.0 }, "session-waiting-page");
            await noMarkers.WaitForCommandAsync(command => command.Sequence > beforeStaleBinding && command.Method == "Runtime.evaluate" &&
                command.SessionId == "session-waiting-page");
            Assert(waitingSession.AppliedTargetCount == 0, "a delayed binding from the previous skin cannot mark the update as applied");

            var applied = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            waitingSession.AppliedTargetCountChanged += count => { if (count > 0) applied.TrySetResult(count); };
            await noMarkers.SendEventAsync("Runtime.bindingCalled", new { name = binding, payload = updatedToken, executionContextId = 7 }, "session-waiting-page");
            Assert(await applied.Task.WaitAsync(TimeSpan.FromSeconds(3)) == 1 && waitingSession.AppliedTargetCount == 1,
                "the injected guard reports a later successful apply through a CDP binding event");
            await waitingSession.RemoveAndStopAsync(CancellationToken.None);
        }
    }

    private static async Task TestUnsupportedTargetsAndProtocolErrorsAsync()
    {
        await using (var fake = new FakeCdpServer
        {
            InitialTargets = [new { targetId = "ordinary-page", type = "page", url = "https://example.com/" }]
        })
        {
            await using var session = await CdpRuntimeSession.ConnectAndApplyAsync(fake.Endpoint,
                Request("skin", "must-not-run();", "remove();"), new FakeIdentityVerifier(), CancellationToken.None);
            Assert(!fake.Commands.Any(command => command.Method == "Target.attachToTarget" || command.Method == "Page.addScriptToEvaluateOnNewDocument" || command.Method == "Runtime.evaluate"),
                "ordinary web targets are not attached to or evaluated");
            await session.RemoveAndStopAsync(CancellationToken.None);
        }

        await using (var fake = new FakeCdpServer { ErrorMethod = "Target.getTargets" })
        {
            var error = await CaptureAsync(() => CdpRuntimeSession.ConnectAndApplyAsync(fake.Endpoint,
                Request("skin", "apply();", "remove();"), new FakeIdentityVerifier(), CancellationToken.None));
            Assert(error is CdpProtocolException protocolError && protocolError.Code == -32000,
                "CDP error responses become command exceptions and fail connection setup");
        }

        await using (var fake = new FakeCdpServer
        {
            InitialTargets = [new { targetId = "page-error", type = "page", url = "app://codex/home" }],
            ExceptionOnNextEvaluate = true
        })
        {
            var error = await CaptureAsync(() => CdpRuntimeSession.ConnectAndApplyAsync(fake.Endpoint,
                Request("skin", "apply();", "remove();"), new FakeIdentityVerifier(), CancellationToken.None));
            Assert(error is InvalidOperationException && error.Message.Contains("fake JavaScript exception", StringComparison.Ordinal),
                "Runtime.evaluate exceptionDetails are surfaced as runtime failures");
        }
    }

    private static async Task TestRemovalFailuresAndTargetRacesAsync()
    {
        await using (var fake = new FakeCdpServer
        {
            InitialTargets =
            [
                new { targetId = "remove-one", type = "page", url = "app://codex/home" },
                new { targetId = "remove-two", type = "page", url = "app://-/index.html" }
            ]
        })
        {
            var session = await CdpRuntimeSession.ConnectAndApplyAsync(fake.Endpoint,
                Request("skin", "apply();", "restore();"), new FakeIdentityVerifier(), CancellationToken.None);
            fake.ErrorMessage = "synthetic early-script removal failure";
            fake.ErrorCommand = command => command.Method == "Page.removeScriptToEvaluateOnNewDocument" && command.SessionId == "session-remove-one";

            var error = await CaptureAsync(() => session.RemoveAndStopAsync(CancellationToken.None));
            Assert(error is AggregateException, "remove reports per-target cleanup failures after attempting all targets");
            Assert(fake.Commands.Any(command => command.Method == "Runtime.evaluate" && command.SessionId == "session-remove-two" && HasExpression(command, "restore();")),
                "one page cleanup failure does not prevent restore on the other Codex page");
            Assert(fake.Commands.Any(command => command.Method == "Target.setDiscoverTargets" &&
                command.Parameters.TryGetProperty("discover", out var discover) && discover.ValueKind == JsonValueKind.False),
                "remove disables target discovery even when one page cleanup fails");

            var firstAttemptsBeforeDispose = fake.Commands.Count(command => command.Method == "Page.removeScriptToEvaluateOnNewDocument" && command.SessionId == "session-remove-one");
            await session.DisposeAsync();
            var firstAttemptsAfterDispose = fake.Commands.Count(command => command.Method == "Page.removeScriptToEvaluateOnNewDocument" && command.SessionId == "session-remove-one");
            Assert(firstAttemptsAfterDispose == firstAttemptsBeforeDispose + 1,
                "Dispose retries an early script ID whose first removal failed");
        }

        await using (var fake = new FakeCdpServer
        {
            InitialTargets = [new { targetId = "race-one", type = "page", url = "app://codex/home" }]
        })
        await using (var session = await CdpRuntimeSession.ConnectAndApplyAsync(fake.Endpoint,
            Request("skin", "apply();", "restore();"), new FakeIdentityVerifier(), CancellationToken.None))
        {
            var shouldFail = 1;
            fake.ErrorMessage = "No target with given id";
            fake.ErrorCommand = command => command.Method == "Runtime.evaluate" && command.SessionId == "session-race-one" && Interlocked.Exchange(ref shouldFail, 0) == 1;
            var beforeRace = fake.LastSequence;
            await fake.SendEventAsync("Page.loadEventFired", new { timestamp = 4.0 }, "session-race-one");
            var racedCommand = await fake.WaitForCommandAsync(command => command.Sequence > beforeRace && command.Method == "Runtime.evaluate" && command.SessionId == "session-race-one");
            Assert(racedCommand.Method == "Runtime.evaluate", "target event issued a guard-only probe before the synthetic destroyed-target error");

            var beforeNextTarget = fake.LastSequence;
            await fake.SendEventAsync("Target.targetCreated", new { targetInfo = new { targetId = "race-two", type = "page", url = "app://-/index.html" } });
            var nextPageAdd = await fake.WaitForCommandAsync(command => command.Sequence > beforeNextTarget && command.Method == "Page.addScriptToEvaluateOnNewDocument");
            Assert(nextPageAdd.SessionId == "session-race-two" && session.IsConnected,
                "a destroyed target's protocol error is ignored while the browser WebSocket and other targets remain active");
            await session.RemoveAndStopAsync(CancellationToken.None);
        }
    }

    private static async Task TestCachedStatusAndAgentDisconnectAsync()
    {
        await using (var fake = new FakeCdpServer())
        await using (var coordinator = new RuntimeCoordinator(new FakeIdentityVerifier()))
        {
            var apply = await coordinator.HandleAsync(new AgentCommand
            {
                RequestId = Guid.NewGuid(),
                Operation = AgentOperation.Apply,
                ApplyRequest = Request("waiting", "apply();", "restore();", fake.Endpoint.ToString())
            }, CancellationToken.None);
            Assert(apply.State == RuntimeStatusState.Connected && apply.AppliedTargetCount == 0 && !apply.Message!.Contains("已应用", StringComparison.Ordinal) &&
                apply.ConnectedInstance == FakeConnectedIdentity,
                "a manual endpoint returns its actual connected identity and is not reported as applied without Codex targets");

            var sequence = fake.LastSequence;
            var cached = await coordinator.HandleAsync(new AgentCommand { RequestId = Guid.NewGuid(), Operation = AgentOperation.GetStatus }, CancellationToken.None);
            Assert(cached.AppliedTargetCount == 0 && fake.LastSequence == sequence,
                "GetStatus returns the cached NamedPipe status without issuing a CDP probe or reinjection");

            var beforeTarget = fake.LastSequence;
            await fake.SendEventAsync("Target.targetCreated", new { targetInfo = new { targetId = "late-page", type = "page", url = "app://codex/home" } });
            await fake.SendEventAsync("Page.loadEventFired", new { timestamp = 5.0 }, "session-late-page");
            await fake.WaitForCommandAsync(command => command.Sequence > beforeTarget && command.Method == "Runtime.evaluate" &&
                command.SessionId == "session-late-page" && HasExpression(command, "apply();"));
            var statusBarrier = await fake.WaitForCommandAsync(command => command.Sequence > beforeTarget && command.Method == "Runtime.evaluate" &&
                command.SessionId == "session-late-page" && !HasExpression(command, "apply();"));
            Assert(statusBarrier.SessionId == "session-late-page", "the existing page event loop completed the apply before status refresh");
            var afterApply = fake.LastSequence;
            var updated = await coordinator.HandleAsync(new AgentCommand { RequestId = Guid.NewGuid(), Operation = AgentOperation.GetStatus }, CancellationToken.None);
            Assert(updated.AppliedTargetCount == 1 && updated.Message!.Contains("已应用", StringComparison.Ordinal) && fake.LastSequence == afterApply,
                "AppliedTargetCount updates from the existing target event while GetStatus remains cache-only");

            var stopped = await coordinator.HandleAsync(new AgentCommand { RequestId = Guid.NewGuid(), Operation = AgentOperation.Stop }, CancellationToken.None);
            Assert(stopped.State == RuntimeStatusState.Stopped && stopped.IsDebugPortOpen is null,
                "Stop reports only manager disconnection and leaves target port status unknown");
        }

        await using (var fake = new FakeCdpServer
        {
            InitialTargets = [new { targetId = "disconnect-page", type = "page", url = "app://codex/home" }]
        })
        await using (var coordinator = new RuntimeCoordinator(new FakeIdentityVerifier()))
        {
            var connected = await coordinator.HandleAsync(new AgentCommand
            {
                RequestId = Guid.NewGuid(),
                Operation = AgentOperation.Apply,
                ApplyRequest = Request("disconnect", "apply();", "restore();", fake.Endpoint.ToString())
            }, CancellationToken.None);
            Assert(connected.State == RuntimeStatusState.Connected, "fake browser starts an active pinned CDP session");
            await fake.CloseWebSocketAsync();
            await coordinator.SessionTerminated.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(!coordinator.HasActiveSession, "browser WebSocket loss ends the Agent session without reconnecting");
        }

        var missingAgent = Path.Combine(Path.GetTempPath(), "SkinManager.Agent.missing." + Guid.NewGuid().ToString("N") + ".exe");
        var controller = new RuntimeController(missingAgent, "SkinManager.Runtime.Tests." + Guid.NewGuid().ToString("N"));
        var initialStatus = await controller.GetStatusAsync();
        Assert(initialStatus.State == RuntimeStatusState.Stopped && !File.Exists(missingAgent),
            "initial status lookup does not launch the Agent or touch a debug endpoint");
    }

    private static async Task TestDisconnectAndEndpointRejectionAsync()
    {
        await using (var fake = new FakeCdpServer())
        await using (var client = await CdpBrowserClient.ConnectAsync(fake.Endpoint, new FakeIdentityVerifier(), CancellationToken.None))
        {
            await fake.CloseWebSocketAsync();
            var error = await CaptureAsync(async () => await client.Events.Completion.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert(error is IOException, "browser WebSocket disconnect completes the event stream with an error");
            Assert(!client.IsConnected, "browser WebSocket disconnect marks the pinned connection unavailable");
        }

        var nonLoopbackRejected = await CaptureAsync(() => CdpBrowserClient.ConnectAsync(new Uri("http://example.com:9335"),
            new FakeIdentityVerifier(), CancellationToken.None));
        Assert(nonLoopbackRejected is ArgumentException, "non-loopback endpoints are rejected before any network request");

        await using var badResponse = new FakeCdpServer { VersionStatusCode = HttpStatusCode.ServiceUnavailable };
        var responseError = await CaptureAsync(() => CdpBrowserClient.ConnectAsync(badResponse.Endpoint, new FakeIdentityVerifier(), CancellationToken.None));
        Assert(responseError is InvalidOperationException && responseError.Message.Contains("HTTP 503", StringComparison.Ordinal),
            "non-success /json/version responses fail closed");

        await using var badBrowserSocket = new FakeCdpServer { BrowserWebSocketPath = "/devtools/page/not-a-browser" };
        var identityVerifier = new FakeIdentityVerifier();
        var browserSocketError = await CaptureAsync(() => CdpBrowserClient.ConnectAsync(badBrowserSocket.Endpoint, identityVerifier, CancellationToken.None));
        Assert(browserSocketError is InvalidOperationException && identityVerifier.Calls == 0,
            "a page WebSocket cannot impersonate the required browser WebSocket identity");
    }

    private static RuntimeApplyRequest Request(string skinId, string runtimeScript, string removeScript, string? endpoint = null) => new()
    {
        SkinId = skinId,
        RuntimeScript = runtimeScript,
        RemoveScript = removeScript,
        Endpoint = endpoint ?? "http://127.0.0.1:9335"
    };

    private static bool HasExpression(FakeCommand command, string value) =>
        command.Parameters.TryGetProperty("expression", out var expression) && expression.GetString()!.Contains(value, StringComparison.Ordinal);

    private static string GetAppliedToken(FakeCommand earlyScript)
    {
        var source = earlyScript.Parameters.GetProperty("source").GetString()!;
        const string marker = "notify(\"";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException("The generated early script has no applied-token notification.");
        start += marker.Length;
        var end = source.IndexOf("\")", start, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException("The generated early script has a malformed applied-token notification.");
        return source[start..end];
    }

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("Expected an exception, but operation succeeded.");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}

internal sealed record FakeStoreActivationRequest(string AppUserModelId, string Arguments);

internal sealed class FakeRuntimeLaunchPlatform : IRuntimeLaunchPlatform
{
    public bool IsWindows { get; set; } = true;
    public bool CodexProcessRunning { get; set; }
    public Exception? ActivationFailure { get; set; }
    public Func<CancellationToken, Task<uint>>? ActivationHandler { get; set; }
    public List<FakeStoreActivationRequest> ActivationRequests { get; } = [];

    public bool IsCodexProcessRunning() => CodexProcessRunning;

    public Task<uint> ActivateStoreAppAsync(string appUserModelId, string arguments, CancellationToken cancellationToken)
    {
        ActivationRequests.Add(new(appUserModelId, arguments));
        if (ActivationFailure is { } failure)
            return Task.FromException<uint>(failure);
        return ActivationHandler?.Invoke(cancellationToken) ?? Task.FromResult(4321u);
    }
}

internal sealed class FakeIdentityVerifier : ICodexEndpointIdentityVerifier
{
    public static DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    public int Calls { get; private set; }

    public Task<BrowserProcessIdentity> VerifyListenerAsync(int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (port == 9335)
            throw new InvalidOperationException("The fake test server must not use the real default endpoint port.");
        Calls++;
        return Task.FromResult(new BrowserProcessIdentity(4242, Program.FakeCodexExecutablePath, StartedAtUtc));
    }
}

internal sealed class FakeCodexInstancePlatform : ICodexInstancePlatform
{
    private readonly Dictionary<int, int> _pendingTerminationObservations = [];
    public List<CodexProcessCandidate> Candidates { get; } = [];
    public Dictionary<int, CodexProcessIdentity> CurrentIdentities { get; } = [];
    public List<CodexProcessRelation> Relations { get; } = [];
    public Dictionary<int, List<CodexWindowInfo>> Windows { get; } = [];
    public Dictionary<int, List<int>> ListenerOwners { get; } = [];
    public List<long> LocatedWindows { get; } = [];
    public List<CodexProcessIdentity> LocatedIdentities { get; } = [];
    public List<CodexProcessIdentity> NativeQuitRequests { get; } = [];
    public Dictionary<int, CodexNativeQuitRequest> NativeQuitResponses { get; } = [];
    public Dictionary<int, List<int>> QuitExitProcessIds { get; } = [];
    public List<int> TerminatedPids { get; } = [];
    public bool ForegroundAllowed { get; set; } = true;
    public HashSet<int> AccessDeniedIdentities { get; } = [];
    public Dictionary<int, int> TerminationObservationDelay { get; } = [];
    public Dictionary<int, Action> OnTerminate { get; } = [];
    public Dictionary<int, Action> OnNativeQuit { get; } = [];
    public Action? OnNextEnumerateRelations { get; set; }

    public IReadOnlyList<CodexProcessCandidate> EnumerateCandidates(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Candidates.Where(candidate => CurrentIdentities.TryGetValue(candidate.Identity.ProcessId, out var current) &&
            SameIdentity(current, candidate.Identity) && !AccessDeniedIdentities.Contains(candidate.Identity.ProcessId)).ToArray();
    }

    public CodexProcessIdentity? GetIdentity(int processId) => AccessDeniedIdentities.Contains(processId) ? null : CurrentIdentities.GetValueOrDefault(processId);

    public CodexProcessObservation ObserveProcess(int processId)
    {
        if (!CurrentIdentities.TryGetValue(processId, out var identity))
            return new(CodexProcessObservationState.Exited, null);
        if (AccessDeniedIdentities.Contains(processId))
            return new(CodexProcessObservationState.Unknown, null, $"fake access denied PID {processId}");
        if (_pendingTerminationObservations.TryGetValue(processId, out var remaining))
        {
            if (remaining <= 0)
            {
                CurrentIdentities.Remove(processId);
                _pendingTerminationObservations.Remove(processId);
                return new(CodexProcessObservationState.Exited, null);
            }
            _pendingTerminationObservations[processId] = remaining - 1;
        }
        return new(CodexProcessObservationState.Running, identity);
    }

    public IReadOnlyList<CodexProcessRelation> EnumerateProcessRelations(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var running = CurrentIdentities.Keys.ToHashSet();
        var relations = Relations.Where(relation => running.Contains(relation.ProcessId)).ToArray();
        var afterEnumeration = OnNextEnumerateRelations;
        OnNextEnumerateRelations = null;
        afterEnumeration?.Invoke();
        return relations;
    }

    public bool IsCodexIdentity(CodexProcessIdentity identity) => Candidates.Any(candidate =>
        candidate.Identity.ProcessId == identity.ProcessId &&
        candidate.ProductName.Equals("Codex", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(candidate.Identity.ExecutablePath, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<CodexWindowInfo> GetVisibleTopLevelWindows(int processId) =>
        (Windows.GetValueOrDefault(processId) ?? []).Where(window => window.IsVisible).ToArray();

    public bool IsUniqueListenerOwnedByProcess(int port, int processId) =>
        ListenerOwners.TryGetValue(port, out var owners) && owners.Count == 1 && owners[0] == processId;

    public bool RestoreAndForeground(CodexProcessIdentity expectedIdentity, long windowHandle)
    {
        LocatedWindows.Add(windowHandle);
        LocatedIdentities.Add(expectedIdentity);
        return ForegroundAllowed;
    }

    public CodexNativeQuitRequest TrySendNativeQuit(CodexProcessIdentity expectedIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeQuitRequests.Add(expectedIdentity);
        if (OnNativeQuit.TryGetValue(expectedIdentity.ProcessId, out var action))
            action();
        var response = NativeQuitResponses.GetValueOrDefault(expectedIdentity.ProcessId,
            new(CodexNativeQuitRequestState.Unavailable, "fake native Quit unavailable"));
        if (response.State == CodexNativeQuitRequestState.Requested && QuitExitProcessIds.TryGetValue(expectedIdentity.ProcessId, out var exits))
            foreach (var processId in exits)
                CurrentIdentities.Remove(processId);
        return response;
    }

    public void TerminateProcess(CodexProcessIdentity expectedIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CurrentIdentities.TryGetValue(expectedIdentity.ProcessId, out var current) || !SameIdentity(current, expectedIdentity))
            throw new InvalidOperationException($"fake PID {expectedIdentity.ProcessId} identity changed");
        TerminatedPids.Add(expectedIdentity.ProcessId);
        if (OnTerminate.TryGetValue(expectedIdentity.ProcessId, out var action))
            action();
        if (TerminationObservationDelay.TryGetValue(expectedIdentity.ProcessId, out var delayedObservations))
            _pendingTerminationObservations[expectedIdentity.ProcessId] = delayedObservations;
        else
            CurrentIdentities.Remove(expectedIdentity.ProcessId);
    }

    public void AddRoot(CodexProcessCandidate candidate, int parentProcessId)
    {
        Candidates.Add(candidate with { ParentProcessId = parentProcessId });
        CurrentIdentities[candidate.Identity.ProcessId] = candidate.Identity;
        Relations.Add(new(candidate.Identity.ProcessId, parentProcessId, Path.GetFileName(candidate.Identity.ExecutablePath)));
    }

    public void AddChild(CodexProcessIdentity identity, int parentProcessId)
    {
        CurrentIdentities[identity.ProcessId] = identity;
        Relations.Add(new(identity.ProcessId, parentProcessId, Path.GetFileName(identity.ExecutablePath)));
    }

    private static bool SameIdentity(CodexProcessIdentity left, CodexProcessIdentity right) =>
        left.ProcessId == right.ProcessId && left.StartedAtUtc.UtcDateTime == right.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(left.ExecutablePath), Path.GetFullPath(right.ExecutablePath), StringComparison.OrdinalIgnoreCase);
}

internal sealed record FakeCommand(int Sequence, int Id, string Method, JsonElement Parameters, string? SessionId);

internal sealed class FakeCdpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<FakeCommand> _commands = Channel.CreateUnbounded<FakeCommand>();
    private readonly ConcurrentQueue<FakeCommand> _recorded = new();
    private readonly ConcurrentDictionary<string, JsonElement> _targetInfos = new(StringComparer.Ordinal);
    private readonly object _targetInitializationGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Task _httpLoop;
    private Task? _socketLoop;
    private WebSocket? _socket;
    private int _sequence;
    private int _scriptId;
    private int? _deferredSlowId;
    private bool _targetInfosInitialized;

    public int Port { get; }
    public Uri Endpoint => new($"http://127.0.0.1:{Port}/");
    public object[] InitialTargets { get; init; } = [];
    public HttpStatusCode VersionStatusCode { get; init; } = HttpStatusCode.OK;
    public string BrowserWebSocketPath { get; init; } = "/devtools/browser/fake-session";
    public string? ErrorMethod { get; init; }
    public string ErrorMessage { get; set; } = "fake protocol error";
    public Func<FakeCommand, bool>? ErrorCommand { get; set; }
    public bool EvaluateValue { get; set; } = true;
    public bool ExceptionOnNextEvaluate { get; set; }
    public int LastSequence => Volatile.Read(ref _sequence);
    public FakeCommand[] Commands => _recorded.ToArray();

    public FakeCdpServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        if (Port == 9335)
            throw new InvalidOperationException("Random test port unexpectedly matched the real default port.");
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _httpLoop = ServeHttpAsync();
    }

    public async Task<FakeCommand> WaitForCommandAsync(Func<FakeCommand, bool> predicate, int afterSequence = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (true)
        {
            var existing = _recorded.FirstOrDefault(command => command.Sequence > afterSequence && predicate(command));
            if (existing is not null)
                return existing;
            var command = await _commands.Reader.ReadAsync(timeout.Token);
            if (command.Sequence > afterSequence && predicate(command))
                return command;
        }
    }

    public Task<bool> HasCommandAsync(Func<FakeCommand, bool> predicate) => Task.FromResult(_recorded.Any(predicate));

    public async Task SendEventAsync(string method, object parameters, string? sessionId = null)
    {
        UpdateTargetSnapshot(method, parameters);
        var payload = sessionId is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { method, @params = parameters })
            : JsonSerializer.SerializeToUtf8Bytes(new { method, @params = parameters, sessionId });
        await SendSocketMessageAsync(payload, CancellationToken.None);
    }

    public async Task CloseWebSocketAsync()
    {
        if (_socket is { State: WebSocketState.Open } socket)
        {
            await _sendGate.WaitAsync();
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fake close", CancellationToken.None); }
            finally { _sendGate.Release(); }
        }
    }

    private async Task ServeHttpAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && _listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                if (context.Request.Url?.AbsolutePath == "/json/version")
                {
                    context.Response.StatusCode = (int)VersionStatusCode;
                    if (VersionStatusCode == HttpStatusCode.OK)
                    {
                        var body = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            Browser = "Codex/fake",
                            webSocketDebuggerUrl = $"ws://127.0.0.1:{Port}{BrowserWebSocketPath}"
                        });
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = body.Length;
                        await context.Response.OutputStream.WriteAsync(body, _lifetime.Token);
                    }
                    context.Response.Close();
                }
                else if (context.Request.IsWebSocketRequest && context.Request.Url?.AbsolutePath == BrowserWebSocketPath)
                {
                    var accepted = await context.AcceptWebSocketAsync(null);
                    _socket = accepted.WebSocket;
                    _socketLoop = ServeWebSocketAsync(_socket);
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                }
            }
        }
        catch (Exception ex) when (_lifetime.IsCancellationRequested && ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task ServeWebSocketAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        try
        {
            while (!_lifetime.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token);
                    if (frame.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);

                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idValue))
                    continue;
                var id = idValue.GetInt32();
                var method = root.GetProperty("method").GetString()!;
                var parameters = root.TryGetProperty("params", out var paramsValue) ? paramsValue.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                var sessionId = root.TryGetProperty("sessionId", out var sessionValue) ? sessionValue.GetString() : null;
                var command = new FakeCommand(Interlocked.Increment(ref _sequence), id, method, parameters, sessionId);
                _recorded.Enqueue(command);
                await _commands.Writer.WriteAsync(command, _lifetime.Token);
                await RespondAsync(command, socket);
            }
        }
        catch (Exception ex) when (_lifetime.IsCancellationRequested && ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private async Task RespondAsync(FakeCommand command, WebSocket socket)
    {
        if (command.Method == "Test.slow")
        {
            _deferredSlowId = command.Id;
            return;
        }
        if (command.Method == "Test.fast")
        {
            await SendResponseAsync(new { id = command.Id, result = new { value = "fast" } }, socket);
            if (_deferredSlowId is { } slowId)
                await SendResponseAsync(new { id = slowId, result = new { value = "slow" } }, socket);
            return;
        }
        if (string.Equals(command.Method, ErrorMethod, StringComparison.Ordinal))
        {
            await SendResponseAsync(new { id = command.Id, error = new { code = -32000, message = ErrorMessage } }, socket);
            return;
        }
        if (ErrorCommand?.Invoke(command) == true)
        {
            await SendResponseAsync(new { id = command.Id, error = new { code = -32000, message = ErrorMessage } }, socket);
            return;
        }
        if (command.Method == "Target.getTargets")
        {
            await SendResponseAsync(new { id = command.Id, result = new { targetInfos = GetTargetSnapshot() } }, socket);
            return;
        }
        if (command.Method == "Target.attachToTarget")
        {
            var targetId = command.Parameters.GetProperty("targetId").GetString();
            await SendResponseAsync(new { id = command.Id, result = new { sessionId = "session-" + targetId } }, socket);
            return;
        }
        if (command.Method == "Page.addScriptToEvaluateOnNewDocument")
        {
            var scriptId = "script-" + Interlocked.Increment(ref _scriptId);
            await SendResponseAsync(new { id = command.Id, result = new { identifier = scriptId } }, socket);
            return;
        }
        if (command.Method == "Runtime.evaluate")
        {
            if (ExceptionOnNextEvaluate)
            {
                ExceptionOnNextEvaluate = false;
                await SendResponseAsync(new { id = command.Id, result = new { result = new { type = "undefined" }, exceptionDetails = new { text = "fake JavaScript exception" } } }, socket);
                return;
            }
            await SendResponseAsync(new { id = command.Id, result = new { result = new { type = "boolean", value = EvaluateValue } } }, socket);
            return;
        }
        await SendResponseAsync(new { id = command.Id, result = new { } }, socket);
    }

    private async Task SendResponseAsync<T>(T response, WebSocket socket) =>
        await SendSocketMessageAsync(JsonSerializer.SerializeToUtf8Bytes(response), CancellationToken.None, socket);

    private async Task SendSocketMessageAsync(byte[] payload, CancellationToken cancellationToken, WebSocket? socketOverride = null)
    {
        var socket = socketOverride ?? _socket ?? throw new InvalidOperationException("No fake CDP WebSocket is connected.");
        await _sendGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken); }
        finally { _sendGate.Release(); }
    }

    private void UpdateTargetSnapshot(string method, object parameters)
    {
        lock (_targetInitializationGate)
        {
            EnsureTargetSnapshotInitialized();
            var data = JsonSerializer.SerializeToElement(parameters);
            if (method is "Target.targetCreated" or "Target.targetInfoChanged" && data.TryGetProperty("targetInfo", out var targetInfo))
            {
                var targetId = targetInfo.GetProperty("targetId").GetString();
                if (targetId is not null)
                    _targetInfos[targetId] = targetInfo.Clone();
            }
            else if (method == "Target.targetDestroyed" && data.TryGetProperty("targetId", out var destroyedId))
            {
                var targetId = destroyedId.GetString();
                if (targetId is not null)
                    _targetInfos.TryRemove(targetId, out _);
            }
        }
    }

    private JsonElement[] GetTargetSnapshot()
    {
        lock (_targetInitializationGate)
        {
            EnsureTargetSnapshotInitialized();
            return _targetInfos.Values.ToArray();
        }
    }

    private void EnsureTargetSnapshotInitialized()
    {
        if (_targetInfosInitialized)
            return;
        foreach (var target in InitialTargets)
        {
            var info = JsonSerializer.SerializeToElement(target);
            if (info.TryGetProperty("targetId", out var idValue) && idValue.GetString() is { } id)
                _targetInfos[id] = info.Clone();
        }
        _targetInfosInitialized = true;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { _listener.Stop(); }
        catch { }
        try { _socket?.Abort(); }
        catch { }
        try { await _httpLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { }
        if (_socketLoop is not null)
        {
            try { await _socketLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
        }
        _listener.Close();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}

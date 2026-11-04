using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace SkinManager.Runtime;

public enum CodexInstanceKind
{
    Normal,
    Debug
}

public sealed record CodexProcessIdentity(int ProcessId, DateTimeOffset StartedAtUtc, string ExecutablePath);

public sealed record CodexInstance(
    CodexProcessIdentity Identity,
    string WindowTitle,
    long WindowHandle,
    int? DebugPort,
    string? UserDataDirectory)
{
    public CodexInstanceKind Kind { get; init; } = DebugPort is null ? CodexInstanceKind.Normal : CodexInstanceKind.Debug;
    public bool CanConnect { get; init; } = DebugPort is >= 1 and <= 65535;
    public CodexProcessExitSnapshot? PendingExitSnapshot { get; init; }
    public bool IsExitResidual { get; init; }
}

public enum CodexLocateResult
{
    Located,
    NoWindow,
    ForegroundDenied
}

public interface ICodexInstanceService
{
    Task<IReadOnlyList<CodexInstance>> GetInstancesAsync(CancellationToken cancellationToken = default);
    Task<CodexLocateResult> LocateAsync(CodexInstance instance, CancellationToken cancellationToken = default);
    Task<CodexCloseOperationResult> CloseAsync(CodexInstance instance, bool force = false, CancellationToken cancellationToken = default);
}

internal sealed record CodexProcessCandidate(
    CodexProcessIdentity Identity,
    string ProductName,
    string CommandLine,
    int? ParentProcessId);

internal sealed record CodexWindowInfo(long Handle, string Title, bool IsVisible = true);

internal interface ICodexInstancePlatform
{
    IReadOnlyList<CodexProcessCandidate> EnumerateCandidates(CancellationToken cancellationToken);
    CodexProcessIdentity? GetIdentity(int processId);
    bool IsCodexIdentity(CodexProcessIdentity identity);
    IReadOnlyList<CodexWindowInfo> GetVisibleTopLevelWindows(int processId);
    bool IsUniqueListenerOwnedByProcess(int port, int processId);
    bool RestoreAndForeground(CodexProcessIdentity expectedIdentity, long windowHandle);
    IReadOnlyList<CodexProcessRelation> EnumerateProcessRelations(CancellationToken cancellationToken);
    CodexProcessObservation ObserveProcess(int processId);
    CodexNativeQuitRequest TrySendNativeQuit(CodexProcessIdentity expectedIdentity, CancellationToken cancellationToken);
    void TerminateProcess(CodexProcessIdentity expectedIdentity, CancellationToken cancellationToken);
}

public sealed class WindowsCodexInstanceService : ICodexInstanceService
{
    private static readonly TimeSpan DefaultCloseWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ClosePollInterval = TimeSpan.FromMilliseconds(250);
    private readonly ICodexInstancePlatform _platform;
    private readonly TimeSpan _closeWait;
    private readonly object _pendingExitGate = new();
    private readonly Dictionary<CodexProcessIdentity, CodexInstance> _pendingExits = [];

    public WindowsCodexInstanceService() : this(new WindowsCodexInstancePlatform(), DefaultCloseWait) { }

    internal WindowsCodexInstanceService(ICodexInstancePlatform platform, TimeSpan? closeWait = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _closeWait = closeWait ?? DefaultCloseWait;
    }

    public Task<IReadOnlyList<CodexInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<CodexInstance>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<CodexInstance>();
            var candidates = _platform.EnumerateCandidates(cancellationToken);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var arguments = WindowsCommandLine.Parse(candidate.CommandLine);
                if (!IsMainCodexCandidate(candidate, arguments))
                    continue;

                var port = GetOptionValue(arguments, "--remote-debugging-port");
                var hasDebugOption = HasOption(arguments, "--remote-debugging-port");
                int? debugPort = int.TryParse(port, out var parsedPort) && parsedPort is >= 1 and <= 65535 ? parsedPort : null;
                var canConnect = debugPort is { } requestedPort &&
                    _platform.IsUniqueListenerOwnedByProcess(requestedPort, candidate.Identity.ProcessId);

                var windows = _platform.GetVisibleTopLevelWindows(candidate.Identity.ProcessId);
                var window = windows.FirstOrDefault();
                result.Add(new CodexInstance(
                    candidate.Identity,
                    window?.Title ?? string.Empty,
                    window?.Handle ?? 0,
                    debugPort,
                    GetOptionValue(arguments, "--user-data-dir"))
                {
                    Kind = hasDebugOption ? CodexInstanceKind.Debug : CodexInstanceKind.Normal,
                    CanConnect = canConnect
                });
            }

            foreach (var pending in GetPendingExits())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = RefreshPendingSnapshot(
                    pending.PendingExitSnapshot!,
                    cancellationToken,
                    discoverNewDescendants: true,
                    result.Select(instance => instance.Identity).ToArray());
                if (snapshot is null)
                {
                    RemovePendingExit(pending.Identity);
                    continue;
                }

                var matchingIndex = result.FindIndex(instance => SameIdentity(instance.Identity, pending.Identity));
                var baseInstance = matchingIndex >= 0 ? result[matchingIndex] : pending;
                var isResidual = matchingIndex < 0 || !ContainsRoot(snapshot);
                var updated = baseInstance with
                {
                    PendingExitSnapshot = snapshot,
                    IsExitResidual = isResidual,
                    WindowHandle = isResidual ? 0 : baseInstance.WindowHandle,
                    WindowTitle = isResidual ? string.Empty : baseInstance.WindowTitle
                };
                if (matchingIndex >= 0)
                    result[matchingIndex] = updated;
                else
                    result.Add(updated);
                StorePendingExit(updated);
            }

            return result.OrderBy(instance => instance.Identity.ProcessId).ToArray();
        }, cancellationToken);

    public Task<CodexLocateResult> LocateAsync(CodexInstance instance, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();
        VerifyCurrentIdentity(instance.Identity);
        var window = _platform.GetVisibleTopLevelWindows(instance.Identity.ProcessId).FirstOrDefault();
        if (window is null)
            return CodexLocateResult.NoWindow;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return _platform.RestoreAndForeground(instance.Identity, window.Handle)
                ? CodexLocateResult.Located
                : CodexLocateResult.ForegroundDenied;
        }
        catch (Win32Exception ex)
        {
            throw CreateRefreshException(ex);
        }
    }, cancellationToken);

    public Task<CodexCloseOperationResult> CloseAsync(CodexInstance instance, bool force = false, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();
        return force ? ForceSelected(instance, cancellationToken) : RequestNativeQuit(instance, cancellationToken);
    }, cancellationToken);

    private CodexCloseOperationResult RequestNativeQuit(CodexInstance instance, CancellationToken cancellationToken)
    {
        VerifyCurrentIdentity(instance.Identity);
        var snapshot = CaptureProcessExitSnapshot(instance.Identity, cancellationToken);
        var pending = instance with { PendingExitSnapshot = snapshot, IsExitResidual = false };
        // Keep the verified tree before sending anything to the window; sending may race the root exit or fail.
        StorePendingExit(pending);
        var request = _platform.TrySendNativeQuit(instance.Identity, cancellationToken);
        if (request.State == CodexNativeQuitRequestState.Unavailable)
        {
            StorePendingExit(pending);
            return new(CodexCloseResult.NativeQuitUnavailable, pending, request.Message);
        }
        if (request.State == CodexNativeQuitRequestState.Failed)
        {
            StorePendingExit(pending);
            return new(CodexCloseResult.Incomplete, pending, request.Message);
        }

        var stopwatch = Stopwatch.StartNew();
        var current = RefreshPendingSnapshot(snapshot, cancellationToken, discoverNewDescendants: true);
        if (current is null)
        {
            RemovePendingExit(instance.Identity);
            return new(CodexCloseResult.Closed, pending with { PendingExitSnapshot = null }, "已确认所选实例的主进程和已归属后台进程均已退出。");
        }
        StorePendingExit(pending with { PendingExitSnapshot = current, IsExitResidual = !ContainsRoot(current) });

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = RefreshPendingSnapshot(current, cancellationToken);
            if (current is null)
            {
                RemovePendingExit(instance.Identity);
                return new(CodexCloseResult.Closed, pending with { PendingExitSnapshot = null }, "已确认所选实例的主进程和已归属后台进程均已退出。");
            }
            StorePendingExit(pending with { PendingExitSnapshot = current, IsExitResidual = !ContainsRoot(current) });

            if (stopwatch.Elapsed >= _closeWait)
                break;
            var remaining = _closeWait - stopwatch.Elapsed;
            Thread.Sleep(remaining < ClosePollInterval ? remaining : ClosePollInterval);
        }

        current = RefreshPendingSnapshot(current, cancellationToken, discoverNewDescendants: true);
        if (current is null)
        {
            RemovePendingExit(instance.Identity);
            return new(CodexCloseResult.Closed, pending with { PendingExitSnapshot = null }, "已确认所选实例的主进程和已归属后台进程均已退出。");
        }

        var updated = pending with { PendingExitSnapshot = current, IsExitResidual = !ContainsRoot(current) };
        StorePendingExit(updated);
        var outcome = current.Issue is null && current.UnverifiedProcessIds.Count == 0
            ? CodexCloseResult.StillRunning
            : CodexCloseResult.Incomplete;
        return new(outcome, updated, DescribeRemainingProcesses(current, "已发送所选实例的原生 Quit 请求"));
    }

    private CodexCloseOperationResult ForceSelected(CodexInstance instance, CancellationToken cancellationToken)
    {
        var snapshot = instance.PendingExitSnapshot;
        if (snapshot is null || !SameIdentity(snapshot.RootIdentity, instance.Identity))
            return new(CodexCloseResult.Incomplete, instance, "没有可验证的待处理退出快照。请先选择实例并发起退出请求，再单独确认强制结束。");

        var currentRoots = GetVerifiedMainCodexIdentities(cancellationToken);
        var rootObservation = _platform.ObserveProcess(snapshot.RootIdentity.ProcessId);
        if (rootObservation.State == CodexProcessObservationState.Running && rootObservation.Identity is not null)
        {
            if (!SameIdentity(rootObservation.Identity, snapshot.RootIdentity))
                return new(CodexCloseResult.IdentityChanged, instance, $"PID {snapshot.RootIdentity.ProcessId} 已被其他进程复用；未操作新进程。请刷新实例列表。 ");

            try
            {
                var latest = CaptureProcessExitSnapshot(snapshot.RootIdentity, cancellationToken);
                snapshot = MergeSnapshots(snapshot, latest);
            }
            catch (InvalidOperationException) when (
                _platform.ObserveProcess(snapshot.RootIdentity.ProcessId).State == CodexProcessObservationState.Exited)
            {
                // The root may exit after the initial observation. Keep its last verified snapshot
                // and continue with descendants that are still present.
            }
        }
        else if (rootObservation.State == CodexProcessObservationState.Unknown)
        {
            snapshot = snapshot with { Issue = rootObservation.Issue ?? "无法确认主进程身份；未将其视为已退出。" };
        }

        var refreshed = RefreshPendingSnapshot(snapshot, cancellationToken, discoverNewDescendants: true, currentRoots: currentRoots);
        if (refreshed is null)
        {
            RemovePendingExit(instance.Identity);
            return new(CodexCloseResult.Closed, instance with { PendingExitSnapshot = null }, "已确认先前记录的所选进程均已退出；没有发送强制结束命令。");
        }

        var deadline = Stopwatch.StartNew();
        var current = refreshed;
        var attempted = new HashSet<(int ProcessId, DateTime StartedAtUtc)>();
        string? issue = current.Issue;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = false;

            // 先结束主进程/父进程并等待其确实退出，再处理子进程，避免 Electron 继续重生 helper。
            foreach (var target in current.Processes.OrderBy(process => process.Depth))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relations = _platform.EnumerateProcessRelations(cancellationToken);
                currentRoots = GetVerifiedMainCodexIdentities(cancellationToken);
                var protectedRoots = currentRoots
                    .Where(identity => !SameIdentity(identity, current.RootIdentity))
                    .Select(identity => identity.ProcessId)
                    .Append(Environment.ProcessId);
                var protectedProcessIds = CodexProcessOwnership.GetSubtreeProcessIds(protectedRoots, relations);
                if (protectedProcessIds.Contains(target.Identity.ProcessId))
                {
                    issue = "所选进程当前位于其他 Codex 实例或 SkinManager 进程分支中；为避免影响其他程序，未结束该 PID。";
                    continue;
                }

                var before = _platform.ObserveProcess(target.Identity.ProcessId);
                if (before.State == CodexProcessObservationState.Exited)
                {
                    changed = true;
                    continue;
                }
                if (before.State == CodexProcessObservationState.Unknown)
                {
                    issue = before.Issue ?? $"无法读取 PID {target.Identity.ProcessId} 的进程身份；未结束该进程。";
                    break;
                }
                if (before.Identity is null || !SameIdentity(before.Identity, target.Identity))
                {
                    current = MarkUnverified(current, target.Identity.ProcessId,
                        $"PID {target.Identity.ProcessId} 的身份已变化；未向新进程发送结束命令。");
                    issue = current.Issue;
                    break;
                }

                var attemptKey = (target.Identity.ProcessId, target.Identity.StartedAtUtc.UtcDateTime);
                if (!attempted.Add(attemptKey))
                    break;

                try
                {
                    _platform.TerminateProcess(target.Identity, cancellationToken);
                    changed = true;
                }
                catch (InvalidOperationException exception)
                {
                    var afterFailure = _platform.ObserveProcess(target.Identity.ProcessId);
                    if (afterFailure.State == CodexProcessObservationState.Exited)
                        changed = true;
                    else
                        issue = exception.Message;
                }
                catch (Win32Exception exception)
                {
                    issue = CreateRefreshException(exception).Message;
                }

                // Kill 是异步请求；在有限总时限内观察退出，并持续发现父进程退出前新生的子进程。
                while (deadline.Elapsed < _closeWait)
                {
                    var observed = _platform.ObserveProcess(target.Identity.ProcessId);
                    if (observed.State == CodexProcessObservationState.Exited)
                    {
                        changed = true;
                        break;
                    }
                    if (observed.State == CodexProcessObservationState.Unknown)
                    {
                        issue = observed.Issue ?? $"无法确认 PID {target.Identity.ProcessId} 的退出状态。";
                        break;
                    }
                    if (observed.Identity is null || !SameIdentity(observed.Identity, target.Identity))
                    {
                        current = MarkUnverified(current, target.Identity.ProcessId,
                            $"PID {target.Identity.ProcessId} 已变化；未将新进程当作已退出或继续操作。");
                        issue = current.Issue;
                        break;
                    }

                    var refreshedDuringWait = RefreshPendingSnapshot(current, cancellationToken,
                        discoverNewDescendants: true, currentRoots: currentRoots);
                    if (refreshedDuringWait is not null)
                        current = refreshedDuringWait;
                    Thread.Sleep(ClosePollInterval);
                }

                var after = _platform.ObserveProcess(target.Identity.ProcessId);
                if (after.State != CodexProcessObservationState.Exited)
                {
                    // 父进程未确认退出时不处理其子进程；它可能仍在重生后台进程。
                    break;
                }
            }

            var refreshedAfterPass = RefreshPendingSnapshot(current, cancellationToken,
                discoverNewDescendants: true, currentRoots: currentRoots);
            if (refreshedAfterPass is null)
            {
                RemovePendingExit(instance.Identity);
                return new(CodexCloseResult.Closed, instance with { PendingExitSnapshot = null }, "已确认所选实例及本次记录的所有后台进程均已退出。");
            }
            current = refreshedAfterPass;
            if (current.Issue is not null)
                issue = current.Issue;
            current = current with { Issue = issue };

            if (deadline.Elapsed >= _closeWait || !changed)
                break;
        }

        var updated = instance with { PendingExitSnapshot = current, IsExitResidual = !ContainsRoot(current) };
        StorePendingExit(updated);
        return new(CodexCloseResult.Incomplete, updated, DescribeRemainingProcesses(current, "强制结束后仍有进程未能确认退出"));
    }

    internal CodexProcessExitSnapshot CaptureProcessExitSnapshot(CodexProcessIdentity selectedIdentity, CancellationToken cancellationToken)
    {
        var relations = _platform.EnumerateProcessRelations(cancellationToken);
        var protectedRoots = GetVerifiedMainCodexIdentities(cancellationToken)
            .Where(identity => !SameIdentity(identity, selectedIdentity))
            .Select(identity => identity.ProcessId)
            .Append(Environment.ProcessId);
        var excludedProcessIds = CodexProcessOwnership.GetSubtreeProcessIds(protectedRoots, relations);
        var tree = CodexProcessOwnership.SelectOwnedSubtree(selectedIdentity.ProcessId, relations, excludedProcessIds);
        if (tree.Count == 0 || tree[0].Relation.ProcessId != selectedIdentity.ProcessId)
            throw new InvalidOperationException("无法从当前进程树确认所选实例及其父子关系。未发送退出请求；请刷新实例列表后重试。");

        var identities = new Dictionary<int, CodexProcessIdentity>();
        var entries = new List<CodexProcessTreeEntry>();
        var unverified = new HashSet<int>();
        var excludedForeignProcesses = new Dictionary<int, CodexProcessIdentity>();
        string? issue = null;
        foreach (var node in tree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processId = node.Relation.ProcessId;
            if (excludedForeignProcesses.ContainsKey(processId))
                continue;

            var observation = _platform.ObserveProcess(processId);
            if (observation.State == CodexProcessObservationState.Exited)
            {
                if (processId == selectedIdentity.ProcessId)
                    throw new InvalidOperationException("所选 Codex 主进程已退出。请刷新实例列表后重试。");
                continue;
            }
            if (observation.State == CodexProcessObservationState.Unknown || observation.Identity is null)
            {
                unverified.Add(processId);
                issue = observation.Issue ?? $"无法读取 PID {processId} 的身份信息。";
                continue;
            }

            var identity = observation.Identity;
            if (processId == selectedIdentity.ProcessId && !SameIdentity(identity, selectedIdentity))
                throw new InvalidOperationException("所选 Codex 主进程身份已变化。请刷新实例列表后重试。");

            CodexProcessIdentity? parentIdentity = null;
            if (processId != selectedIdentity.ProcessId &&
                !identities.TryGetValue(node.Relation.ParentProcessId, out parentIdentity))
            {
                unverified.Add(processId);
                issue = $"无法确认 PID {processId} 与其父进程之间的身份关系。";
                continue;
            }

            if (parentIdentity is not null && identity.StartedAtUtc < parentIdentity.StartedAtUtc)
            {
                RecordExcludedForeignSubtree(processId, identity, relations, excludedForeignProcesses, unverified, ref issue);
                continue;
            }

            identities[processId] = identity;
            entries.Add(new CodexProcessTreeEntry(identity, node.Relation.ParentProcessId, parentIdentity, node.Depth));
        }

        if (!identities.TryGetValue(selectedIdentity.ProcessId, out var rootIdentity) || !SameIdentity(rootIdentity, selectedIdentity))
            throw new InvalidOperationException("无法为所选 Codex 主进程建立可验证的退出快照。未发送退出请求；请刷新实例列表后重试。");

        return new CodexProcessExitSnapshot(
            selectedIdentity,
            entries.OrderBy(entry => entry.Depth).ToArray(),
            unverified.Order().ToArray(),
            issue)
        {
            ExcludedForeignProcesses = excludedForeignProcesses.Values.OrderBy(identity => identity.ProcessId).ToArray()
        };
    }

    private CodexProcessExitSnapshot? RefreshPendingSnapshot(
        CodexProcessExitSnapshot snapshot,
        CancellationToken cancellationToken,
        bool discoverNewDescendants = false,
        IReadOnlyList<CodexProcessIdentity>? currentRoots = null)
    {
        var relations = _platform.EnumerateProcessRelations(cancellationToken);
        var currentIds = relations.Select(relation => relation.ProcessId).ToHashSet();
        var relationByPid = relations.ToDictionary(relation => relation.ProcessId);
        var processes = new List<CodexProcessTreeEntry>();
        var unverified = snapshot.UnverifiedProcessIds.Where(currentIds.Contains).ToHashSet();
        var issue = unverified.Count > 0 ? "部分进程 PID 尚未获得可核验的身份信息；未将其视为已退出。" : null;
        var verifiedRunning = new Dictionary<int, CodexProcessTreeEntry>();
        var excludedForeignProcesses = snapshot.ExcludedForeignProcesses.ToDictionary(identity => identity.ProcessId);
        var originalTargetIds = snapshot.Processes.Select(entry => entry.Identity.ProcessId)
            .Concat(snapshot.UnverifiedProcessIds)
            .ToHashSet();

        foreach (var excluded in excludedForeignProcesses.Values.ToArray())
        {
            if (!currentIds.Contains(excluded.ProcessId))
            {
                excludedForeignProcesses.Remove(excluded.ProcessId);
                continue;
            }

            var observation = _platform.ObserveProcess(excluded.ProcessId);
            if (observation.State == CodexProcessObservationState.Exited)
            {
                excludedForeignProcesses.Remove(excluded.ProcessId);
                continue;
            }
            if (observation.State == CodexProcessObservationState.Unknown || observation.Identity is null)
            {
                excludedForeignProcesses.Remove(excluded.ProcessId);
                unverified.Add(excluded.ProcessId);
                issue = observation.Issue ?? $"无法核验此前排除的 PID {excluded.ProcessId}；未将其视为已退出。";
                foreach (var descendantPid in CodexProcessOwnership.GetSubtreeProcessIds([excluded.ProcessId], relations))
                {
                    if (currentIds.Contains(descendantPid))
                        unverified.Add(descendantPid);
                }
                continue;
            }

            if (!SameIdentity(observation.Identity, excluded))
                excludedForeignProcesses.Remove(excluded.ProcessId);
            else
                unverified.Remove(excluded.ProcessId);
        }

        foreach (var processId in snapshot.UnverifiedProcessIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!relationByPid.TryGetValue(processId, out var relation))
                continue;
            var childObservation = _platform.ObserveProcess(processId);
            var parentObservation = _platform.ObserveProcess(relation.ParentProcessId);
            if (childObservation.State == CodexProcessObservationState.Running && childObservation.Identity is not null &&
                parentObservation.State == CodexProcessObservationState.Running && parentObservation.Identity is not null &&
                childObservation.Identity.StartedAtUtc < parentObservation.Identity.StartedAtUtc)
            {
                unverified.Remove(processId);
                RecordExcludedForeignSubtree(processId, childObservation.Identity, relations, excludedForeignProcesses, unverified, ref issue);
            }
        }

        foreach (var entry in snapshot.Processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedForeignProcesses.ContainsKey(entry.Identity.ProcessId))
                continue;
            var observation = _platform.ObserveProcess(entry.Identity.ProcessId);
            if (observation.State == CodexProcessObservationState.Exited)
                continue;
            if (observation.State == CodexProcessObservationState.Running && observation.Identity is not null && !SameIdentity(observation.Identity, entry.Identity))
            {
                unverified.Add(entry.Identity.ProcessId);
                issue = $"PID {entry.Identity.ProcessId} 的身份已变化；不会将其当作已完成，也不会结束新进程。";
                continue;
            }

            if (entry.ParentIdentity is not null && entry.Identity.StartedAtUtc < entry.ParentIdentity.StartedAtUtc)
            {
                RecordExcludedForeignSubtree(entry.Identity.ProcessId, entry.Identity, relations, excludedForeignProcesses, unverified, ref issue);
                continue;
            }

            processes.Add(entry);
            if (observation.State == CodexProcessObservationState.Unknown)
                issue = observation.Issue ?? $"无法确认 PID {entry.Identity.ProcessId} 是否仍是原进程。";
            else if (observation.Identity is not null)
                verifiedRunning[entry.Identity.ProcessId] = entry;
        }

        if (discoverNewDescendants)
        {
            currentRoots ??= GetVerifiedMainCodexIdentities(cancellationToken);
            var protectedRoots = currentRoots
                .Where(identity => !SameIdentity(identity, snapshot.RootIdentity))
                .Select(identity => identity.ProcessId)
                .Append(Environment.ProcessId);
            var protectedProcessIds = CodexProcessOwnership.GetSubtreeProcessIds(protectedRoots, relations);
            var childRelations = relations.GroupBy(relation => relation.ParentProcessId)
                .ToDictionary(group => group.Key, group => group.OrderBy(relation => relation.ProcessId).ToArray());
            var pendingParents = new Queue<CodexProcessTreeEntry>(verifiedRunning.Values.OrderBy(entry => entry.Depth));
            var seen = processes.Select(entry => entry.Identity.ProcessId).Concat(unverified).ToHashSet();

            while (pendingParents.TryDequeue(out var parent))
            {
                if (!childRelations.TryGetValue(parent.Identity.ProcessId, out var children))
                    continue;
                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (protectedProcessIds.Contains(child.ProcessId))
                        continue;

                    var observation = _platform.ObserveProcess(child.ProcessId);
                    if (excludedForeignProcesses.TryGetValue(child.ProcessId, out var excludedIdentity))
                    {
                        if (observation.State == CodexProcessObservationState.Exited)
                        {
                            excludedForeignProcesses.Remove(child.ProcessId);
                            seen.Add(child.ProcessId);
                            continue;
                        }
                        if (observation.State == CodexProcessObservationState.Unknown || observation.Identity is null)
                        {
                            excludedForeignProcesses.Remove(child.ProcessId);
                            unverified.Add(child.ProcessId);
                            issue = observation.Issue ?? $"无法读取先前排除的 PID {child.ProcessId} 身份；不会将其视为已退出。";
                            foreach (var descendantPid in CodexProcessOwnership.GetSubtreeProcessIds([child.ProcessId], relations))
                            {
                                if (!protectedProcessIds.Contains(descendantPid))
                                    unverified.Add(descendantPid);
                            }
                            seen.Add(child.ProcessId);
                            continue;
                        }
                        if (SameIdentity(observation.Identity, excludedIdentity) &&
                            observation.Identity.StartedAtUtc < parent.Identity.StartedAtUtc)
                        {
                            seen.Add(child.ProcessId);
                            continue;
                        }
                        // A new identity may reuse this PID. Re-evaluate it under the live selected parent.
                        excludedForeignProcesses.Remove(child.ProcessId);
                        unverified.Remove(child.ProcessId);
                        seen.Remove(child.ProcessId);
                    }
                    if (!seen.Add(child.ProcessId))
                        continue;
                    if (observation.State == CodexProcessObservationState.Exited)
                        continue;
                    if (observation.State == CodexProcessObservationState.Unknown || observation.Identity is null)
                    {
                        unverified.Add(child.ProcessId);
                        issue = observation.Issue ?? $"无法读取新发现的子进程 PID {child.ProcessId} 的身份。";
                        foreach (var descendantPid in CodexProcessOwnership.GetSubtreeProcessIds([child.ProcessId], relations))
                        {
                            if (!protectedProcessIds.Contains(descendantPid) && currentIds.Contains(descendantPid))
                                unverified.Add(descendantPid);
                        }
                        continue;
                    }

                    if (observation.Identity.StartedAtUtc < parent.Identity.StartedAtUtc)
                    {
                        RecordExcludedForeignSubtree(child.ProcessId, observation.Identity, relations,
                            excludedForeignProcesses, unverified, ref issue);
                        continue;
                    }

                    var entry = new CodexProcessTreeEntry(observation.Identity, child.ParentProcessId, parent.Identity, parent.Depth + 1);
                    processes.Add(entry);
                    verifiedRunning[entry.Identity.ProcessId] = entry;
                    pendingParents.Enqueue(entry);
                }
            }

            foreach (var relation in relations)
            {
                if (!originalTargetIds.Contains(relation.ParentProcessId) || seen.Contains(relation.ProcessId) ||
                    protectedProcessIds.Contains(relation.ProcessId) || excludedForeignProcesses.ContainsKey(relation.ProcessId) ||
                    excludedForeignProcesses.ContainsKey(relation.ParentProcessId))
                    continue;

                foreach (var descendantPid in CodexProcessOwnership.GetSubtreeProcessIds([relation.ProcessId], relations))
                {
                    if (!protectedProcessIds.Contains(descendantPid) && currentIds.Contains(descendantPid))
                        unverified.Add(descendantPid);
                }
                issue = "发现父进程已退出或 PID 已复用后的未验证子进程；为避免结束错误实例，保留为未完成项。";
            }
        }

        if (excludedForeignProcesses.Count > 0)
        {
            processes.RemoveAll(entry => excludedForeignProcesses.ContainsKey(entry.Identity.ProcessId));
            unverified.ExceptWith(excludedForeignProcesses.Keys);
            foreach (var processId in excludedForeignProcesses.Keys)
                verifiedRunning.Remove(processId);
        }
        if (unverified.Count == 0 && processes.All(entry => verifiedRunning.ContainsKey(entry.Identity.ProcessId)))
            issue = null;

        if (processes.Count == 0 && unverified.Count == 0)
            return null;

        return snapshot with
        {
            Processes = processes.OrderBy(entry => entry.Depth).ToArray(),
            UnverifiedProcessIds = unverified.Order().ToArray(),
            ExcludedForeignProcesses = excludedForeignProcesses.Values.OrderBy(identity => identity.ProcessId).ToArray(),
            Issue = issue
        };
    }

    private IReadOnlyList<CodexProcessIdentity> GetVerifiedMainCodexIdentities(CancellationToken cancellationToken) =>
        _platform.EnumerateCandidates(cancellationToken)
            .Where(candidate => IsMainCodexCandidate(candidate, WindowsCommandLine.Parse(candidate.CommandLine)))
            .Select(candidate => candidate.Identity)
            .ToArray();

    private void RecordExcludedForeignSubtree(
        int rootProcessId,
        CodexProcessIdentity rootIdentity,
        IReadOnlyList<CodexProcessRelation> relations,
        IDictionary<int, CodexProcessIdentity> excludedForeignProcesses,
        ISet<int> unverifiedProcessIds,
        ref string? issue)
    {
        excludedForeignProcesses[rootProcessId] = rootIdentity;
        var children = relations.GroupBy(relation => relation.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.OrderBy(relation => relation.ProcessId).ToArray());
        var pending = new Queue<(int ProcessId, CodexProcessIdentity Identity)>();
        pending.Enqueue((rootProcessId, rootIdentity));
        while (pending.TryDequeue(out var parent))
        {
            if (!children.TryGetValue(parent.ProcessId, out var descendants))
                continue;

            foreach (var relation in descendants)
            {
                var processId = relation.ProcessId;
                var observation = _platform.ObserveProcess(processId);
                if (observation.State == CodexProcessObservationState.Exited)
                    continue;
                if (observation.State == CodexProcessObservationState.Running && observation.Identity is not null &&
                    observation.Identity.StartedAtUtc >= parent.Identity.StartedAtUtc)
                {
                    excludedForeignProcesses[processId] = observation.Identity;
                    pending.Enqueue((processId, observation.Identity));
                    continue;
                }

                foreach (var uncertainPid in CodexProcessOwnership.GetSubtreeProcessIds([processId], relations))
                    unverifiedProcessIds.Add(uncertainPid);
                issue = observation.Issue ?? $"无法确认外部分支 PID {processId} 的父子身份链；未将其视为已退出。";
            }
        }
    }

    private static bool IsMainCodexCandidate(CodexProcessCandidate candidate, IReadOnlyList<string> arguments) =>
        WindowsCodexIdentityVerifier.IsKnownCodexExecutable(candidate.Identity.ExecutablePath) &&
        string.Equals(candidate.ProductName, "Codex", StringComparison.OrdinalIgnoreCase) &&
        !arguments.Skip(1).Any(argument => argument.StartsWith("--type=", StringComparison.OrdinalIgnoreCase));

    private CodexInstance[] GetPendingExits()
    {
        lock (_pendingExitGate)
            return _pendingExits.Values.ToArray();
    }

    private void StorePendingExit(CodexInstance instance)
    {
        if (instance.PendingExitSnapshot is null)
            return;
        lock (_pendingExitGate)
            _pendingExits[instance.Identity] = instance;
    }

    private void RemovePendingExit(CodexProcessIdentity identity)
    {
        lock (_pendingExitGate)
            _pendingExits.Remove(identity);
    }

    private static bool ContainsRoot(CodexProcessExitSnapshot snapshot) =>
        snapshot.Processes.Any(entry => SameIdentity(entry.Identity, snapshot.RootIdentity));

    private static CodexProcessExitSnapshot MergeSnapshots(CodexProcessExitSnapshot previous, CodexProcessExitSnapshot latest)
    {
        var entries = new Dictionary<int, CodexProcessTreeEntry>();
        var latestEntries = latest.Processes.ToDictionary(entry => entry.Identity.ProcessId);
        var latestExcluded = latest.ExcludedForeignProcesses.ToDictionary(identity => identity.ProcessId);
        var unverified = latest.UnverifiedProcessIds
            .Concat(previous.UnverifiedProcessIds.Where(processId => !latestEntries.ContainsKey(processId) && !latestExcluded.ContainsKey(processId)))
            .ToHashSet();
        var previousVerifiedIds = previous.Processes.Select(entry => entry.Identity.ProcessId).ToHashSet();
        var excludedForeignProcesses = previous.ExcludedForeignProcesses.ToDictionary(identity => identity.ProcessId);
        foreach (var excluded in latestExcluded.Values)
        {
            if (!previousVerifiedIds.Contains(excluded.ProcessId))
                excludedForeignProcesses[excluded.ProcessId] = excluded;
        }
        foreach (var processId in latestEntries.Keys.Concat(latest.UnverifiedProcessIds))
            excludedForeignProcesses.Remove(processId);
        unverified.ExceptWith(latestExcluded.Keys.Where(processId => !latest.UnverifiedProcessIds.Contains(processId)));
        var issue = latest.Issue;
        foreach (var entry in previous.Processes)
        {
            var pid = entry.Identity.ProcessId;
            if (!unverified.Contains(pid) && !excludedForeignProcesses.ContainsKey(pid))
                entries[pid] = entry;
        }

        // A fresh capture is only taken while the original root identity is still live. Its
        // parent-chain validation can therefore supersede a stale descendant PID observation.
        foreach (var entry in latest.Processes)
        {
            var pid = entry.Identity.ProcessId;
            entries[pid] = entry;
            unverified.Remove(pid);
        }

        foreach (var pid in unverified)
            entries.Remove(pid);
        foreach (var pid in excludedForeignProcesses.Keys)
            entries.Remove(pid);

        return latest with
        {
            Processes = entries.Values.OrderBy(entry => entry.Depth).ToArray(),
            UnverifiedProcessIds = unverified.Order().ToArray(),
            ExcludedForeignProcesses = excludedForeignProcesses.Values.OrderBy(identity => identity.ProcessId).ToArray(),
            Issue = issue
        };
    }

    private static CodexProcessExitSnapshot MarkUnverified(CodexProcessExitSnapshot snapshot, int processId, string issue)
    {
        return snapshot with
        {
            Processes = snapshot.Processes.Where(entry => entry.Identity.ProcessId != processId).ToArray(),
            UnverifiedProcessIds = snapshot.UnverifiedProcessIds.Append(processId).Distinct().Order().ToArray(),
            Issue = issue
        };
    }

    private static bool SameIdentity(CodexProcessIdentity left, CodexProcessIdentity right) =>
        left.ProcessId == right.ProcessId &&
        left.StartedAtUtc.UtcDateTime == right.StartedAtUtc.UtcDateTime &&
        string.Equals(Path.GetFullPath(left.ExecutablePath), Path.GetFullPath(right.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static string DescribeRemainingProcesses(CodexProcessExitSnapshot snapshot, string prefix)
    {
        if (!ContainsRoot(snapshot))
            return $"{prefix}。主进程已退出，但仍有 {snapshot.RemainingCount} 个已记录或身份未确认的后台 PID；可单独确认后继续处理。";
        if (snapshot.Issue is not null)
            return $"{prefix}。仍有 {snapshot.RemainingCount} 个进程未完成确认：{snapshot.Issue}";
        return $"{prefix}。所选实例仍有 {snapshot.RemainingCount} 个已记录进程运行；可单独确认后强制结束。";
    }

    private void VerifyCurrentIdentity(CodexProcessIdentity expected)
    {
        var observation = _platform.ObserveProcess(expected.ProcessId);
        if (observation.State != CodexProcessObservationState.Running || observation.Identity is null ||
            !SameIdentity(observation.Identity, expected) || !_platform.IsCodexIdentity(observation.Identity))
        {
            var detail = observation.Issue is null ? string.Empty : $" {observation.Issue}";
            throw new InvalidOperationException($"无法确认所选 Codex 实例仍是原进程，可能已退出、身份已变化或权限不足。{detail}请刷新实例列表后重试。");
        }
    }

    private static InvalidOperationException CreateRefreshException(Win32Exception innerException) =>
        new("无法操作所选 Codex 实例，可能是进程已变化或权限不足。请刷新实例列表后重试。", innerException);

    private static string? GetOptionValue(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return argument[(name.Length + 1)..];
            if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Count)
                return arguments[index + 1];
        }
        return null;
    }

    private static bool HasOption(IReadOnlyList<string> arguments, string name) =>
        arguments.Skip(1).Any(argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
}

internal sealed class WindowsCodexInstancePlatform : ICodexInstancePlatform
{
    private const uint GaRoot = 2;
    private const int SwRestore = 9;
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint WmCommand = 0x0111;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint MfByPosition = 0x0400;
    private const uint MfDisabled = 0x0002;
    private const uint MfGrayed = 0x0001;

    public IReadOnlyList<CodexProcessCandidate> EnumerateCandidates(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("实例管理仅支持 Windows Store 安装的 Codex 桌面版。");

        var commandLines = ReadWmiProcessData();
        var candidates = new List<CodexProcessCandidate>();
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!WindowsCodexIdentityVerifier.IsKnownCodexExecutable(path) ||
                        !commandLines.TryGetValue(process.Id, out var processData) || string.IsNullOrWhiteSpace(processData.CommandLine))
                        continue;
                    var productName = FileVersionInfo.GetVersionInfo(path!).ProductName;
                    if (productName is null || !string.Equals(productName, "Codex", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var identity = new CodexProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), Path.GetFullPath(path!));
                    candidates.Add(new CodexProcessCandidate(identity, productName, processData.CommandLine, processData.ParentProcessId));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or System.Security.SecurityException)
                {
                    // An unreadable or exiting process is omitted and can be included after a refresh.
                }
            }
        }
        return candidates;
    }

    public CodexProcessIdentity? GetIdentity(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
                return null;
            return new CodexProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public CodexProcessObservation ObserveProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            _ = process.Handle;
            if (process.HasExited)
                return new(CodexProcessObservationState.Exited, null);

            try
            {
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path))
                    return new(CodexProcessObservationState.Unknown, null, $"无法读取 PID {processId} 的可执行文件路径。");
                var identity = new CodexProcessIdentity(
                    process.Id,
                    new DateTimeOffset(process.StartTime.ToUniversalTime()),
                    Path.GetFullPath(path));
                return new(CodexProcessObservationState.Running, identity);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                return new(CodexProcessObservationState.Exited, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or System.Security.SecurityException)
            {
                return new(CodexProcessObservationState.Unknown, null, $"无法核验 PID {processId} 的启动时间和可执行文件：{ex.Message}");
            }
        }
        catch (ArgumentException)
        {
            return new(CodexProcessObservationState.Exited, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or System.Security.SecurityException)
        {
            return new(CodexProcessObservationState.Unknown, null, $"无法核验 PID {processId} 的进程状态：{ex.Message}");
        }
    }

    public bool IsCodexIdentity(CodexProcessIdentity identity)
    {
        if (!WindowsCodexIdentityVerifier.IsKnownCodexExecutable(identity.ExecutablePath))
            return false;
        try
        {
            return string.Equals(FileVersionInfo.GetVersionInfo(identity.ExecutablePath).ProductName, "Codex", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or ArgumentException or Win32Exception or System.Security.SecurityException)
        {
            return false;
        }
    }

    public IReadOnlyList<CodexWindowInfo> GetVisibleTopLevelWindows(int processId)
    {
        var windows = new List<CodexWindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetAncestor(handle, GaRoot) != handle)
                return true;
            GetWindowThreadProcessId(handle, out var ownerPid);
            if (ownerPid != processId)
                return true;
            var length = GetWindowTextLength(handle);
            var title = new System.Text.StringBuilder(Math.Max(length + 1, 2));
            _ = GetWindowText(handle, title, title.Capacity);
            windows.Add(new CodexWindowInfo(handle.ToInt64(), title.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public bool IsUniqueListenerOwnedByProcess(int port, int processId) => WindowsCodexIdentityVerifier.IsUniqueListenerOwnedByProcess(port, processId);

    public bool RestoreAndForeground(CodexProcessIdentity expectedIdentity, long windowHandle)
    {
        using var process = OpenVerifiedProcess(expectedIdentity, requireCodexMain: true);
        var handle = new IntPtr(windowHandle);
        if (!IsOwnedVisibleTopLevelWindow(handle, process.Id))
            return false;
        if (IsIconic(handle))
            _ = ShowWindow(handle, SwRestore);
        return SetForegroundWindow(handle);
    }

    public IReadOnlyList<CodexProcessRelation> EnumerateProcessRelations(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("进程树读取仅支持 Windows。");

        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows 进程父子关系。");

        var result = new List<CodexProcessRelation>();
        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        if (!Process32First(snapshot, ref entry))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows 进程快照。");

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new CodexProcessRelation((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExecutableName ?? string.Empty));
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (Process32Next(snapshot, ref entry));

        var error = Marshal.GetLastWin32Error();
        if (error is not 0 and not 18)
            throw new Win32Exception(error, "读取 Windows 进程父子关系时失败。");
        return result;
    }

    public CodexNativeQuitRequest TrySendNativeQuit(CodexProcessIdentity expectedIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = OpenVerifiedProcess(expectedIdentity, requireCodexMain: true);
        foreach (var window in GetTopLevelWindows(process.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = new IntPtr(window.Handle);
            if (!IsOwnedTopLevelWindow(handle, process.Id))
                continue;

            var menu = GetMenu(handle);
            if (menu == IntPtr.Zero || !TryFindNativeQuitCommand(menu, out var commandId))
                continue;

            using var verified = OpenVerifiedProcess(expectedIdentity, requireCodexMain: true);
            if (!IsOwnedTopLevelWindow(handle, verified.Id) || GetMenu(handle) != menu ||
                !TryFindNativeQuitCommand(menu, out var revalidatedCommandId) || revalidatedCommandId != commandId)
                return new(CodexNativeQuitRequestState.Failed, "原生菜单或窗口归属在发送前发生变化；未发送退出命令。");

            var sent = SendMessageTimeout(handle, WmCommand, new IntPtr(commandId), IntPtr.Zero, SmtoAbortIfHung, 1_500, out _);
            return sent != IntPtr.Zero
                ? new(CodexNativeQuitRequestState.Requested, "已向所选实例窗口发送当前菜单中的 Quit 命令；等待其进程树退出确认。")
                : new(CodexNativeQuitRequestState.Failed, $"向所选实例发送原生 Quit 命令失败（Win32 {Marshal.GetLastWin32Error()}）；未改用关闭窗口。");
        }

        return new(CodexNativeQuitRequestState.Unavailable, "当前 Codex 没有可用的正常退出入口。可从托盘退出，或单独确认强制结束此实例及后台进程。");
    }

    public void TerminateProcess(CodexProcessIdentity expectedIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The service passes only identities captured inside the selected, verified process tree.
        // Descendants such as codex.exe app-server are valid targets even though they are not ChatGPT.exe.
        using var process = OpenVerifiedProcess(expectedIdentity, requireCodexMain: false);
        try { process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }

    private static Process OpenVerifiedProcess(CodexProcessIdentity expectedIdentity, bool requireCodexMain)
    {
        Process process;
        try { process = Process.GetProcessById(expectedIdentity.ProcessId); }
        catch (ArgumentException) { throw new InvalidOperationException("所选 Codex 实例已退出。请刷新实例列表后重试。"); }
        try
        {
            // Pin a handle to this OS process before reading identity fields. Process.Kill reuses this handle,
            // so a PID reused after verification cannot redirect the termination to its replacement.
            _ = process.Handle;
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path) ||
                !MatchesExpectedProcessIdentity(expectedIdentity, process.Id, process.StartTime.ToUniversalTime(), path) ||
                (requireCodexMain && (!WindowsCodexIdentityVerifier.IsKnownCodexExecutable(path) ||
                    !string.Equals(FileVersionInfo.GetVersionInfo(path).ProductName, "Codex", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("所选 Codex 实例身份已变化。请刷新实例列表后重试。");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static bool MatchesExpectedProcessIdentity(
        CodexProcessIdentity expectedIdentity,
        int processId,
        DateTime startedAtUtc,
        string executablePath) =>
        processId == expectedIdentity.ProcessId &&
        startedAtUtc == expectedIdentity.StartedAtUtc.UtcDateTime &&
        !string.IsNullOrWhiteSpace(executablePath) &&
        string.Equals(Path.GetFullPath(executablePath), Path.GetFullPath(expectedIdentity.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnedVisibleTopLevelWindow(IntPtr handle, int processId)
    {
        return IsOwnedTopLevelWindow(handle, processId) && IsWindowVisible(handle);
    }

    private static bool IsOwnedTopLevelWindow(IntPtr handle, int processId)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle) || GetAncestor(handle, GaRoot) != handle)
            return false;
        GetWindowThreadProcessId(handle, out var ownerPid);
        return ownerPid == processId;
    }

    private static IReadOnlyList<CodexWindowInfo> GetTopLevelWindows(int processId)
    {
        var windows = new List<CodexWindowInfo>();
        _ = EnumWindows((handle, _) =>
        {
            if (GetAncestor(handle, GaRoot) != handle)
                return true;
            GetWindowThreadProcessId(handle, out var ownerPid);
            if (ownerPid != processId)
                return true;
            var length = GetWindowTextLength(handle);
            var title = new System.Text.StringBuilder(Math.Max(length + 1, 2));
            _ = GetWindowText(handle, title, title.Capacity);
            windows.Add(new CodexWindowInfo(handle.ToInt64(), title.ToString(), IsWindowVisible(handle)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool TryFindNativeQuitCommand(IntPtr menu, out uint commandId, int depth = 0)
    {
        commandId = 0;
        if (menu == IntPtr.Zero || depth > 8)
            return false;

        var count = GetMenuItemCount(menu);
        for (var position = 0; position < count; position++)
        {
            var submenu = GetSubMenu(menu, position);
            if (submenu != IntPtr.Zero)
            {
                if (TryFindNativeQuitCommand(submenu, out commandId, depth + 1))
                    return true;
                continue;
            }

            var id = GetMenuItemID(menu, position);
            if (id == uint.MaxValue)
                continue;
            var text = new System.Text.StringBuilder(256);
            if (GetMenuString(menu, (uint)position, text, text.Capacity, MfByPosition) <= 0 || !IsQuitLabel(text.ToString()))
                continue;
            var state = GetMenuState(menu, id, 0);
            if (state == uint.MaxValue || (state & (MfDisabled | MfGrayed)) != 0)
                continue;

            commandId = id;
            return true;
        }

        return false;
    }

    private static bool IsQuitLabel(string text)
    {
        var normalized = text.Trim().Replace("…", string.Empty, StringComparison.Ordinal).TrimEnd('.').Trim();
        return normalized.Equals("Quit", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Exit", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("退出", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("退出 Codex", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Quit Codex", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Exit Codex", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<int, WmiProcessData> ReadWmiProcessData()
    {
        object? locator = null;
        object? services = null;
        object? rows = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!;
            locator = Activator.CreateInstance(locatorType) ?? throw new InvalidOperationException("无法连接 WMI。");
            dynamic locatorObject = locator;
            services = locatorObject.ConnectServer(".", @"root\cimv2");
            dynamic servicesObject = services;
            rows = servicesObject.ExecQuery("SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name='ChatGPT.exe'", "WQL", 0);
            dynamic rowsObject = rows;
            var count = Convert.ToInt32(rowsObject.Count, System.Globalization.CultureInfo.InvariantCulture);
            var result = new Dictionary<int, WmiProcessData>();
            for (var index = 0; index < count; index++)
            {
                object? row = rowsObject.ItemIndex(index);
                try
                {
                    var processId = Convert.ToInt32(GetWmiPropertyValue(row!, "ProcessId"), System.Globalization.CultureInfo.InvariantCulture);
                    var parentProcessId = Convert.ToInt32(GetWmiPropertyValue(row!, "ParentProcessId"), System.Globalization.CultureInfo.InvariantCulture);
                    var commandLine = Convert.ToString(GetWmiPropertyValue(row!, "CommandLine"), System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(commandLine))
                        result[processId] = new WmiProcessData(commandLine, parentProcessId);
                }
                finally
                {
                    ReleaseComObject(row);
                }
            }
            return result;
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException("无法读取 Codex 进程的只读命令行信息。请确认当前 Windows 用户具有本机 WMI 查询权限后刷新。", ex);
        }
        finally
        {
            ReleaseComObject(rows);
            ReleaseComObject(services);
            ReleaseComObject(locator);
        }
    }

    private static object? GetWmiPropertyValue(object row, string propertyName)
    {
        object? properties = null;
        object? property = null;
        try
        {
            dynamic processRow = row;
            properties = processRow.Properties_;
            dynamic propertySet = properties;
            property = propertySet.Item(propertyName);
            dynamic wmiProperty = property;
            return wmiProperty.Value;
        }
        finally
        {
            ReleaseComObject(property);
            ReleaseComObject(properties);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private sealed record WmiProcessData(string CommandLine, int ParentProcessId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint UsageCount;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableName;
    }

    private sealed class ProcessSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ProcessSnapshotHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ProcessSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(ProcessSnapshotHandle snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(ProcessSnapshotHandle snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int maximumCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMenuStringW", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint item, System.Text.StringBuilder text, int maximumCount, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}

internal static class WindowsCommandLine
{
    public static IReadOnlyList<string> Parse(string commandLine)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows 命令行解析仅支持 Windows。");
        if (string.IsNullOrWhiteSpace(commandLine))
            return Array.Empty<string>();
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++)
                result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            return result;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

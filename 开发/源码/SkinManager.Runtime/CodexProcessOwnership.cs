namespace SkinManager.Runtime;

public sealed record CodexProcessRelation(int ProcessId, int ParentProcessId, string ImageName);

public sealed record CodexProcessTreeEntry(
    CodexProcessIdentity Identity,
    int ParentProcessId,
    CodexProcessIdentity? ParentIdentity,
    int Depth);

public sealed record CodexProcessExitSnapshot(
    CodexProcessIdentity RootIdentity,
    IReadOnlyList<CodexProcessTreeEntry> Processes,
    IReadOnlyList<int> UnverifiedProcessIds,
    string? Issue = null)
{
    public IReadOnlyList<CodexProcessIdentity> ExcludedForeignProcesses { get; init; } = [];
    public bool CanAttemptForce => Processes.Count > 0;
    public int RemainingCount => Processes.Count + UnverifiedProcessIds.Count;
}

public enum CodexProcessObservationState
{
    Running,
    Exited,
    Unknown
}

public sealed record CodexProcessObservation(
    CodexProcessObservationState State,
    CodexProcessIdentity? Identity,
    string? Issue = null);

public enum CodexNativeQuitRequestState
{
    Requested,
    Unavailable,
    Failed
}

public sealed record CodexNativeQuitRequest(CodexNativeQuitRequestState State, string Message);

public enum CodexCloseResult
{
    Closed,
    NativeQuitUnavailable,
    StillRunning,
    Incomplete,
    IdentityChanged
}

public sealed record CodexCloseOperationResult(CodexCloseResult Outcome, CodexInstance Instance, string Message);

internal sealed record CodexProcessTreeNode(CodexProcessRelation Relation, int Depth);

internal static class CodexProcessOwnership
{
    public static IReadOnlySet<int> GetSubtreeProcessIds(
        IEnumerable<int> rootProcessIds,
        IReadOnlyList<CodexProcessRelation> relations)
    {
        var children = BuildChildren(relations);
        var result = new HashSet<int>();
        var pending = new Queue<int>();
        foreach (var rootProcessId in rootProcessIds)
        {
            if (rootProcessId > 0 && result.Add(rootProcessId))
                pending.Enqueue(rootProcessId);
        }

        while (pending.TryDequeue(out var parentProcessId))
        {
            if (!children.TryGetValue(parentProcessId, out var descendants))
                continue;
            foreach (var descendant in descendants)
            {
                if (result.Add(descendant.ProcessId))
                    pending.Enqueue(descendant.ProcessId);
            }
        }

        return result;
    }

    public static IReadOnlyList<CodexProcessTreeNode> SelectOwnedSubtree(
        int rootProcessId,
        IReadOnlyList<CodexProcessRelation> relations,
        IReadOnlySet<int> excludedProcessIds)
    {
        if (excludedProcessIds.Contains(rootProcessId))
            return Array.Empty<CodexProcessTreeNode>();

        var relationByPid = relations.ToDictionary(relation => relation.ProcessId);
        if (!relationByPid.TryGetValue(rootProcessId, out var root))
            return Array.Empty<CodexProcessTreeNode>();

        var children = BuildChildren(relations);
        var selected = new List<CodexProcessTreeNode> { new(root, 0) };
        var pending = new Queue<(int ProcessId, int Depth)>();
        var seen = new HashSet<int> { rootProcessId };
        pending.Enqueue((rootProcessId, 0));

        while (pending.TryDequeue(out var parent))
        {
            if (!children.TryGetValue(parent.ProcessId, out var descendants))
                continue;

            foreach (var descendant in descendants)
            {
                if (excludedProcessIds.Contains(descendant.ProcessId) || !seen.Add(descendant.ProcessId))
                    continue;

                var depth = parent.Depth + 1;
                selected.Add(new CodexProcessTreeNode(descendant, depth));
                pending.Enqueue((descendant.ProcessId, depth));
            }
        }

        return selected;
    }

    private static Dictionary<int, List<CodexProcessRelation>> BuildChildren(IReadOnlyList<CodexProcessRelation> relations)
    {
        var children = new Dictionary<int, List<CodexProcessRelation>>();
        foreach (var relation in relations)
        {
            if (relation.ProcessId <= 0 || relation.ParentProcessId <= 0)
                continue;

            if (!children.TryGetValue(relation.ParentProcessId, out var descendants))
            {
                descendants = [];
                children.Add(relation.ParentProcessId, descendants);
            }

            descendants.Add(relation);
        }

        foreach (var descendants in children.Values)
            descendants.Sort(static (left, right) => left.ProcessId.CompareTo(right.ProcessId));

        return children;
    }
}

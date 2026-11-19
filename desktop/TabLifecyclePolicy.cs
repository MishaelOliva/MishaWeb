namespace MishaWeb;

internal enum TabLifecycleMode
{
    Off,
    Standard,
    Ultra
}

internal enum TabLifecycleAction
{
    Suspend,
    Discard
}

internal readonly record struct TabLifecycleSnapshot(
    int Id,
    DateTime LastActiveUtc,
    bool IsActive,
    bool IsClosed,
    bool IsLoading,
    bool IsInitializing,
    bool IsAudible,
    bool HasActiveDownload,
    bool HasCore,
    bool IsSuspended,
    bool KeepAwake,
    int ProtectedResidentRank = -1,
    bool IsVisibleInPane = false);

internal readonly record struct TabLifecycleDecision(int Id, TabLifecycleAction Action);

internal static class TabLifecyclePolicy
{
    // The active page and the two most recently used ordinary pages must stay
    // resident. This is deliberately independent of the machine-size budget:
    // memory pressure may trim every older page, but it must not turn normal
    // tab switching among the user's three working pages into a reload loop.
    internal const int ProtectedResidentTabCount = 3;
    internal const int SuspendFailureFallbackThreshold = 3;
    private const int MaximumStackAllocatedSnapshots = 256;
    private static readonly TimeSpan StandardPressureDiscardDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StandardDiscardDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StandardPressureStalledWorkDiscardDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StandardStalledWorkDiscardDelay = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UltraSuspendDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResidentBudgetDiscardDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailedSuspendUrgentDiscardDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailedSuspendDiscardDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PressureDiscardDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SmallMachineDiscardDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnconditionalDiscardDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StalledLoadDiscardDelay = TimeSpan.FromMinutes(2);
    private const uint MemoryPressureThresholdPercent = 75;
    private const ulong FourGibibytes = 4UL * 1024 * 1024 * 1024;
    private const ulong EightGibibytes = 8UL * 1024 * 1024 * 1024;

    public static TabLifecycleMode ResolveMode(bool memorySaverEnabled, bool ultraLightEnabled)
    {
        if (ultraLightEnabled) return TabLifecycleMode.Ultra;
        return memorySaverEnabled ? TabLifecycleMode.Standard : TabLifecycleMode.Off;
    }

    public static bool ShouldUseLowMemoryTarget(
        TabLifecycleMode mode,
        bool isForeground,
        bool requiresBackgroundExecution,
        int consecutiveSuspendFailures)
    {
        if (isForeground || mode == TabLifecycleMode.Off) return false;

        // Standard Memory Saver uses the Low/Normal target pair exclusively so
        // background scripts and connections keep working. Ultra-light owns the
        // separate TrySuspend/Resume strategy; pages that require background
        // execution or repeatedly refuse suspension use the live low target.
        return mode == TabLifecycleMode.Standard
            || (mode == TabLifecycleMode.Ultra
                && (requiresBackgroundExecution
                    || consecutiveSuspendFailures >= SuspendFailureFallbackThreshold));
    }

    public static IReadOnlyList<TabLifecycleDecision> PlanSweep(
        IReadOnlyList<TabLifecycleSnapshot> snapshots,
        TabLifecycleMode mode,
        DateTime nowUtc,
        uint memoryLoadPercent,
        ulong totalPhysicalBytes,
        int maximumActions)
    {
        // Compatibility path for callers that do not yet pass the richer resource snapshot.
        // A successful GlobalMemoryStatusEx call always reports non-zero physical memory, so
        // the default failure snapshot (all zeroes) must not be interpreted as 0% load.
        var memorySnapshotIsValid = totalPhysicalBytes > 0 && memoryLoadPercent <= 100;
        return PlanSweep(
            snapshots,
            mode,
            nowUtc,
            memoryLoadPercent,
            memorySnapshotIsValid,
            totalPhysicalBytes,
            Environment.ProcessorCount,
            maximumActions);
    }

    public static IReadOnlyList<TabLifecycleDecision> PlanSweep(
        IReadOnlyList<TabLifecycleSnapshot> snapshots,
        TabLifecycleMode mode,
        DateTime nowUtc,
        uint memoryLoadPercent,
        bool memorySnapshotIsValid,
        ulong totalPhysicalBytes,
        int logicalProcessorCount,
        int maximumActions)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        if (maximumActions <= 0
            || nowUtc.Kind != DateTimeKind.Utc
            || mode is not (TabLifecycleMode.Standard or TabLifecycleMode.Ultra)
            || snapshots.Count == 0)
        {
            return Array.Empty<TabLifecycleDecision>();
        }

        var hasValidMemoryLoad = memorySnapshotIsValid && memoryLoadPercent <= 100;
        var underMemoryPressure = hasValidMemoryLoad
            && memoryLoadPercent >= MemoryPressureThresholdPercent;
        var isKnownSmallMachine = totalPhysicalBytes is > 0 and <= FourGibibytes
            || logicalProcessorCount is > 0 and <= 2;
        Span<bool> discardForResidentBudget = snapshots.Count <= MaximumStackAllocatedSnapshots
            ? stackalloc bool[snapshots.Count]
            : new bool[snapshots.Count];
        discardForResidentBudget.Clear();
        MarkResidentBudgetDiscards(
            snapshots,
            mode,
            nowUtc,
            totalPhysicalBytes,
            logicalProcessorCount,
            discardForResidentBudget);

        var decisionCapacity = Math.Min(maximumActions, snapshots.Count);
        Span<PlannedDecision> plannedDecisions = decisionCapacity <= MaximumStackAllocatedSnapshots
            ? stackalloc PlannedDecision[decisionCapacity]
            : new PlannedDecision[decisionCapacity];
        var plannedDecisionCount = 0;
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            var action = DecideAction(
                snapshot,
                mode,
                nowUtc,
                underMemoryPressure,
                isKnownSmallMachine,
                discardForResidentBudget[index]);
            if (action is null) continue;

            InsertPlannedDecision(
                plannedDecisions,
                ref plannedDecisionCount,
                new PlannedDecision(snapshot.Id, action.Value, snapshot.LastActiveUtc, index));
        }

        if (plannedDecisionCount == 0) return Array.Empty<TabLifecycleDecision>();

        var decisions = new TabLifecycleDecision[plannedDecisionCount];
        for (var index = 0; index < plannedDecisionCount; index++)
        {
            decisions[index] = new TabLifecycleDecision(
                plannedDecisions[index].Id,
                plannedDecisions[index].Action);
        }
        return decisions;
    }

    public static int ResolveUltraResidentCoreBudget(
        ulong totalPhysicalBytes,
        int logicalProcessorCount)
    {
        if (totalPhysicalBytes is > 0 and <= FourGibibytes
            || logicalProcessorCount is > 0 and <= 2)
        {
            return 1;
        }

        if (totalPhysicalBytes is > 0 and <= EightGibibytes
            || logicalProcessorCount is > 0 and <= 4)
        {
            return 2;
        }

        return 3;
    }

    public static bool ShouldDiscardAfterFailedSuspend(
        TabLifecycleSnapshot snapshot,
        TabLifecycleMode mode,
        DateTime nowUtc,
        uint memoryLoadPercent,
        bool memorySnapshotIsValid,
        ulong totalPhysicalBytes,
        int logicalProcessorCount,
        int residentCoreCount,
        int consecutiveFailures)
    {
        if (mode != TabLifecycleMode.Ultra
            || consecutiveFailures < SuspendFailureFallbackThreshold
            || snapshot.IsSuspended
            || IsProtectedResident(snapshot)
            || !IsEligible(snapshot, nowUtc))
        {
            return false;
        }

        var underMemoryPressure = memorySnapshotIsValid
            && memoryLoadPercent <= 100
            && memoryLoadPercent >= MemoryPressureThresholdPercent;
        var isSmallMachine = totalPhysicalBytes is > 0 and <= FourGibibytes
            || logicalProcessorCount is > 0 and <= 2;
        var overResidentBudget = residentCoreCount > ResolveUltraResidentCoreBudget(
            totalPhysicalBytes,
            logicalProcessorCount);
        var delay = underMemoryPressure || isSmallMachine || overResidentBudget
            ? FailedSuspendUrgentDiscardDelay
            : FailedSuspendDiscardDelay;
        return nowUtc - snapshot.LastActiveUtc >= delay;
    }

    private static void MarkResidentBudgetDiscards(
        IReadOnlyList<TabLifecycleSnapshot> snapshots,
        TabLifecycleMode mode,
        DateTime nowUtc,
        ulong totalPhysicalBytes,
        int logicalProcessorCount,
        Span<bool> discardForResidentBudget)
    {
        if (mode != TabLifecycleMode.Ultra) return;

        var residentCoreCount = 0;
        var visiblePaneCount = 0;
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (snapshot.IsClosed) continue;
            if (snapshot.HasCore) residentCoreCount++;
            if (snapshot.IsActive || snapshot.IsVisibleInPane) visiblePaneCount++;
        }
        var residentCoreBudget = Math.Max(
            ResolveUltraResidentCoreBudget(totalPhysicalBytes, logicalProcessorCount),
            visiblePaneCount);
        var excessResidentCores = residentCoreCount - residentCoreBudget;
        if (excessResidentCores <= 0) return;

        Span<int> eligibleIndexes = snapshots.Count <= MaximumStackAllocatedSnapshots
            ? stackalloc int[snapshots.Count]
            : new int[snapshots.Count];
        var eligibleCount = 0;
        for (var index = 0; index < snapshots.Count; index++)
        {
            if (!IsEligibleForResidentBudgetDiscard(snapshots[index], nowUtc)) continue;

            var insertionIndex = eligibleCount;
            while (insertionIndex > 0
                   && IsSnapshotOlder(
                       snapshots,
                       index,
                       eligibleIndexes[insertionIndex - 1]))
            {
                eligibleIndexes[insertionIndex] = eligibleIndexes[insertionIndex - 1];
                insertionIndex--;
            }
            eligibleIndexes[insertionIndex] = index;
            eligibleCount++;
        }

        var discardCount = Math.Min(excessResidentCores, eligibleCount);
        for (var index = 0; index < discardCount; index++)
        {
            discardForResidentBudget[eligibleIndexes[index]] = true;
        }
    }

    private static bool IsSnapshotOlder(
        IReadOnlyList<TabLifecycleSnapshot> snapshots,
        int candidateIndex,
        int existingIndex)
    {
        var timestampComparison = snapshots[candidateIndex].LastActiveUtc.CompareTo(
            snapshots[existingIndex].LastActiveUtc);
        return timestampComparison < 0
            || timestampComparison == 0 && candidateIndex < existingIndex;
    }

    private static void InsertPlannedDecision(
        Span<PlannedDecision> plannedDecisions,
        ref int plannedDecisionCount,
        PlannedDecision candidate)
    {
        var insertionIndex = plannedDecisionCount;
        while (insertionIndex > 0
               && Compare(candidate, plannedDecisions[insertionIndex - 1]) < 0)
        {
            insertionIndex--;
        }

        if (plannedDecisionCount < plannedDecisions.Length)
        {
            for (var index = plannedDecisionCount; index > insertionIndex; index--)
            {
                plannedDecisions[index] = plannedDecisions[index - 1];
            }
            plannedDecisions[insertionIndex] = candidate;
            plannedDecisionCount++;
            return;
        }

        if (insertionIndex >= plannedDecisions.Length) return;
        for (var index = plannedDecisions.Length - 1; index > insertionIndex; index--)
        {
            plannedDecisions[index] = plannedDecisions[index - 1];
        }
        plannedDecisions[insertionIndex] = candidate;
    }

    private static int Compare(PlannedDecision left, PlannedDecision right)
    {
        if (left.Action != right.Action)
        {
            return left.Action == TabLifecycleAction.Discard ? -1 : 1;
        }

        var timestampComparison = left.LastActiveUtc.CompareTo(right.LastActiveUtc);
        return timestampComparison != 0
            ? timestampComparison
            : left.SnapshotIndex.CompareTo(right.SnapshotIndex);
    }

    private static bool IsEligibleForResidentBudgetDiscard(
        TabLifecycleSnapshot snapshot,
        DateTime nowUtc)
    {
        return HasValidInactiveState(snapshot, nowUtc)
            && !IsProtectedResident(snapshot)
            && snapshot.HasCore
            && (snapshot.IsSuspended || (snapshot.IsLoading && !snapshot.IsInitializing))
            && nowUtc - snapshot.LastActiveUtc >= ResidentBudgetDiscardDelay;
    }

    private static TabLifecycleAction? DecideAction(
        TabLifecycleSnapshot snapshot,
        TabLifecycleMode mode,
        DateTime nowUtc,
        bool underMemoryPressure,
        bool isKnownSmallMachine,
        bool discardForResidentBudget)
    {
        // The MRU guarantee prevents reloads, not sleeping: Ultra may suspend a
        // protected page while retaining its live WebView state, but no mode may
        // automatically discard it. Audible, downloading, and keep-awake pages
        // remain independently protected from both operations below.
        var isProtectedResident = IsProtectedResident(snapshot);
        if (!isProtectedResident && ShouldDiscardStalledWork(
                snapshot,
                mode,
                nowUtc,
                underMemoryPressure,
                isKnownSmallMachine,
                discardForResidentBudget))
        {
            return TabLifecycleAction.Discard;
        }
        if (!IsEligible(snapshot, nowUtc)) return null;

        var inactiveFor = nowUtc - snapshot.LastActiveUtc;
        // Standard mode keeps its Low/Normal target strategy and never calls
        // TrySuspend. A direct unload after a generous grace period prevents a
        // long-running session from retaining an unbounded number of WebViews.
        if (mode == TabLifecycleMode.Standard)
        {
            if (isProtectedResident) return null;
            var discardDelay = underMemoryPressure
                ? StandardPressureDiscardDelay
                : StandardDiscardDelay;
            return inactiveFor >= discardDelay ? TabLifecycleAction.Discard : null;
        }
        if (mode != TabLifecycleMode.Ultra) return null;
        if (!snapshot.IsSuspended)
        {
            return inactiveFor >= UltraSuspendDelay ? TabLifecycleAction.Suspend : null;
        }

        if (isProtectedResident) return null;
        if (discardForResidentBudget) return TabLifecycleAction.Discard;
        if (underMemoryPressure && inactiveFor >= PressureDiscardDelay) return TabLifecycleAction.Discard;
        if (isKnownSmallMachine && inactiveFor >= SmallMachineDiscardDelay) return TabLifecycleAction.Discard;
        if (inactiveFor >= UnconditionalDiscardDelay) return TabLifecycleAction.Discard;
        return null;
    }

    private static bool ShouldDiscardStalledWork(
        TabLifecycleSnapshot snapshot,
        TabLifecycleMode mode,
        DateTime nowUtc,
        bool underMemoryPressure,
        bool isKnownSmallMachine,
        bool discardForResidentBudget)
    {
        if (mode is not (TabLifecycleMode.Standard or TabLifecycleMode.Ultra)
            || (!snapshot.IsLoading && !snapshot.IsInitializing)
            || (mode == TabLifecycleMode.Ultra && snapshot.IsLoading && !snapshot.HasCore)
            || !HasValidInactiveState(snapshot, nowUtc))
        {
            return false;
        }

        if (mode == TabLifecycleMode.Standard)
        {
            var standardDelay = underMemoryPressure
                ? StandardPressureStalledWorkDiscardDelay
                : StandardStalledWorkDiscardDelay;
            return nowUtc - snapshot.LastActiveUtc >= standardDelay;
        }

        var delay = discardForResidentBudget
            ? ResidentBudgetDiscardDelay
            : underMemoryPressure
                ? PressureDiscardDelay
                : isKnownSmallMachine
                    ? SmallMachineDiscardDelay
                    : StalledLoadDiscardDelay;
        return nowUtc - snapshot.LastActiveUtc >= delay;
    }

    private static bool HasValidInactiveState(
        TabLifecycleSnapshot snapshot,
        DateTime nowUtc)
    {
        return snapshot.Id >= 0
            && !snapshot.IsActive
            && !snapshot.IsVisibleInPane
            && !snapshot.IsClosed
            && !snapshot.IsAudible
            && !snapshot.HasActiveDownload
            && !snapshot.KeepAwake
            && snapshot.LastActiveUtc != default
            && snapshot.LastActiveUtc.Kind == DateTimeKind.Utc
            && snapshot.LastActiveUtc <= nowUtc;
    }

    private static bool IsEligible(TabLifecycleSnapshot snapshot, DateTime nowUtc)
    {
        return HasValidInactiveState(snapshot, nowUtc)
            && !snapshot.IsLoading
            && !snapshot.IsInitializing
            && snapshot.HasCore;
    }

    internal static bool IsProtectedResident(TabLifecycleSnapshot snapshot)
    {
        return snapshot.ProtectedResidentRank is >= 0 and < ProtectedResidentTabCount;
    }

    private readonly record struct PlannedDecision(
        int Id,
        TabLifecycleAction Action,
        DateTime LastActiveUtc,
        int SnapshotIndex);
}

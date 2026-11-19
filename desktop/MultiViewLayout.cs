namespace MishaWeb;

internal enum MultiViewPreset
{
    Single = 1,
    Split = 2,
    Tri = 3,
    Quad = 4
}

internal readonly record struct MultiViewSplitterDescriptor(
    int Index,
    bool IsVertical,
    Rectangle Bounds,
    Rectangle DragRegionBounds);

internal sealed record MultiViewComputedLayout(
    MultiViewPreset Preset,
    int PaneCount,
    Rectangle[] PaneBounds,
    Rectangle[] HeaderBounds,
    Rectangle[] ContentBounds,
    MultiViewSplitterDescriptor[] Splitters);

internal readonly record struct MultiViewSnapshot(
    MultiViewPreset Preset,
    int PaneCount,
    int FocusedPaneIndex,
    float HorizontalSplitRatio,
    float VerticalSplitRatio,
    int[] PaneTabIds,
    Rectangle[] PaneBounds,
    Rectangle[] HeaderBounds,
    Rectangle[] ContentBounds);

/// <summary>
/// Pure geometry and pane-slot state machine for Multi-View layouts
/// (Single, Split, Tri, Quad). Keeps pane membership unique, enforces safe split
/// ratio bounds, and computes gapless non-overlapping rectangles.
/// </summary>
internal sealed class MultiViewState
{
    internal const float DefaultRatio = 0.5f;
    internal const float MinimumHorizontalRatio = 0.22f;
    internal const float MaximumHorizontalRatio = 0.78f;
    internal const float MinimumVerticalRatio = 0.25f;
    internal const float MaximumVerticalRatio = 0.75f;
    private const int MaximumPanes = 4;

    private readonly int[] paneTabIds = new int[MaximumPanes];

    public MultiViewPreset Preset { get; private set; } = MultiViewPreset.Single;
    public int FocusedPaneIndex { get; private set; }
    public bool IsFocusedPaneMaximized { get; private set; }
    public float HorizontalSplitRatio { get; private set; } = DefaultRatio;
    public float VerticalSplitRatio { get; private set; } = DefaultRatio;
    public int PaneCount => (int)Preset;
    public bool IsMultiViewActive => Preset != MultiViewPreset.Single;

    public int FocusedTabId
    {
        get
        {
            var index = Math.Clamp(FocusedPaneIndex, 0, PaneCount - 1);
            return paneTabIds[index];
        }
    }

    public int[] GetVisibleTabIds()
    {
        var count = PaneCount;
        var result = new int[count];
        Array.Copy(paneTabIds, result, count);
        return result;
    }

    public int GetTabIdForPane(int paneIndex)
    {
        if (paneIndex < 0 || paneIndex >= PaneCount) return 0;
        return paneTabIds[paneIndex];
    }

    public int IndexOfTab(int tabId)
    {
        if (tabId <= 0) return -1;
        var count = PaneCount;
        for (var i = 0; i < count; i++)
        {
            if (paneTabIds[i] == tabId) return i;
        }
        return -1;
    }

    public bool ContainsTab(int tabId) => IndexOfTab(tabId) >= 0;

    public void SetSingle(int tabId)
    {
        Preset = MultiViewPreset.Single;
        FocusedPaneIndex = 0;
        IsFocusedPaneMaximized = false;
        paneTabIds[0] = tabId;
        for (var i = 1; i < MaximumPanes; i++) paneTabIds[i] = 0;
    }

    public bool AddPane(int newTabId)
    {
        if (newTabId <= 0 || Preset == MultiViewPreset.Quad) return false;
        if (ContainsTab(newTabId))
        {
            var existing = IndexOfTab(newTabId);
            FocusedPaneIndex = existing;
            return false;
        }

        var nextPreset = Preset switch
        {
            MultiViewPreset.Single => MultiViewPreset.Split,
            MultiViewPreset.Split => MultiViewPreset.Tri,
            MultiViewPreset.Tri => MultiViewPreset.Quad,
            _ => MultiViewPreset.Quad
        };
        var newIndex = (int)nextPreset - 1;
        paneTabIds[newIndex] = newTabId;
        Preset = nextPreset;
        FocusedPaneIndex = newIndex;
        IsFocusedPaneMaximized = false;
        return true;
    }

    public void ApplyPreset(MultiViewPreset targetPreset, IReadOnlyList<int> orderedTabIds)
    {
        ArgumentNullException.ThrowIfNull(orderedTabIds);
        if (orderedTabIds.Count == 0)
        {
            SetSingle(0);
            return;
        }

        var unique = new List<int>(MaximumPanes);
        foreach (var id in orderedTabIds)
        {
            if (id > 0 && !unique.Contains(id)) unique.Add(id);
            if (unique.Count >= (int)targetPreset) break;
        }
        if (unique.Count == 0)
        {
            SetSingle(0);
            return;
        }

        var actualCount = Math.Min((int)targetPreset, unique.Count);
        Preset = (MultiViewPreset)actualCount;
        IsFocusedPaneMaximized = false;
        for (var i = 0; i < MaximumPanes; i++)
        {
            paneTabIds[i] = i < actualCount ? unique[i] : 0;
        }
        FocusedPaneIndex = Math.Clamp(FocusedPaneIndex, 0, actualCount - 1);
    }

    public void FocusPane(int paneIndex)
    {
        FocusedPaneIndex = Math.Clamp(paneIndex, 0, PaneCount - 1);
    }

    public bool ToggleFocusedPaneMaximized()
    {
        if (Preset == MultiViewPreset.Single)
        {
            IsFocusedPaneMaximized = false;
            return false;
        }

        IsFocusedPaneMaximized = !IsFocusedPaneMaximized;
        return true;
    }

    public bool SwapFocusedPaneWithNext()
    {
        if (Preset == MultiViewPreset.Single) return false;
        var count = PaneCount;
        var current = Math.Clamp(FocusedPaneIndex, 0, count - 1);
        var next = (current + 1) % count;
        (paneTabIds[current], paneTabIds[next]) = (paneTabIds[next], paneTabIds[current]);
        FocusedPaneIndex = next;
        return true;
    }

    public void AssignTabToPane(int paneIndex, int tabId)
    {
        if (tabId <= 0) return;
        var targetIndex = Math.Clamp(paneIndex, 0, PaneCount - 1);
        var existingIndex = IndexOfTab(tabId);
        if (existingIndex == targetIndex)
        {
            FocusedPaneIndex = targetIndex;
            return;
        }

        if (existingIndex >= 0)
        {
            (paneTabIds[existingIndex], paneTabIds[targetIndex]) =
                (paneTabIds[targetIndex], paneTabIds[existingIndex]);
            FocusedPaneIndex = targetIndex;
            return;
        }

        paneTabIds[targetIndex] = tabId;
        FocusedPaneIndex = targetIndex;
    }

    public bool ClosePane(int paneIndex)
    {
        if (Preset == MultiViewPreset.Single || paneIndex < 0 || paneIndex >= PaneCount)
        {
            return false;
        }

        var currentCount = PaneCount;
        for (var i = paneIndex; i < currentCount - 1; i++)
        {
            paneTabIds[i] = paneTabIds[i + 1];
        }
        paneTabIds[currentCount - 1] = 0;

        Preset = (MultiViewPreset)(currentCount - 1);
        IsFocusedPaneMaximized = false;
        if (FocusedPaneIndex >= PaneCount)
        {
            FocusedPaneIndex = PaneCount - 1;
        }
        else if (FocusedPaneIndex > paneIndex)
        {
            FocusedPaneIndex--;
        }
        return true;
    }

    public bool HandleTabClosed(int closedTabId, int? replacementTabId)
    {
        var paneIndex = IndexOfTab(closedTabId);
        if (paneIndex < 0) return false;

        if (replacementTabId is int nextId && nextId > 0 && !ContainsTab(nextId))
        {
            paneTabIds[paneIndex] = nextId;
            return true;
        }

        if (Preset != MultiViewPreset.Single)
        {
            return ClosePane(paneIndex);
        }

        paneTabIds[0] = replacementTabId ?? 0;
        return true;
    }

    public void SetSplitRatios(float? horizontalRatio, float? verticalRatio)
    {
        if (horizontalRatio is float h && float.IsFinite(h))
        {
            HorizontalSplitRatio = Math.Clamp(h, MinimumHorizontalRatio, MaximumHorizontalRatio);
        }
        if (verticalRatio is float v && float.IsFinite(v))
        {
            VerticalSplitRatio = Math.Clamp(v, MinimumVerticalRatio, MaximumVerticalRatio);
        }
    }

    public void ResetRatios()
    {
        HorizontalSplitRatio = DefaultRatio;
        VerticalSplitRatio = DefaultRatio;
    }
}

internal static class MultiViewLayoutEngine
{
    public static MultiViewComputedLayout ComputeLayout(
        Rectangle hostBounds,
        MultiViewState state,
        int splitterThicknessPx,
        int headerHeightPx)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (hostBounds.Width <= 0 || hostBounds.Height <= 0)
        {
            return new MultiViewComputedLayout(
                state.Preset,
                state.PaneCount,
                new Rectangle[state.PaneCount],
                new Rectangle[state.PaneCount],
                new Rectangle[state.PaneCount],
                Array.Empty<MultiViewSplitterDescriptor>());
        }

        var preset = state.Preset;
        var paneCount = (int)preset;
        if (preset == MultiViewPreset.Single)
        {
            return new MultiViewComputedLayout(
                MultiViewPreset.Single,
                1,
                [hostBounds],
                [Rectangle.Empty],
                [hostBounds],
                Array.Empty<MultiViewSplitterDescriptor>());
        }

        var splitter = Math.Clamp(splitterThicknessPx, 2, Math.Max(2, Math.Min(12, Math.Min(hostBounds.Width, hostBounds.Height) / 4)));
        var header = Math.Clamp(headerHeightPx, 18, 48);

        var width = hostBounds.Width;
        var height = hostBounds.Height;
        var minPaneW = Math.Min(240, Math.Max(40, (width - splitter) / 3));
        var usableW = Math.Max(2, width - splitter);
        var maxLeftW = Math.Max(minPaneW, usableW - minPaneW);
        var leftW = Math.Clamp(
            (int)Math.Round(usableW * state.HorizontalSplitRatio),
            Math.Min(minPaneW, maxLeftW),
            maxLeftW);
        var rightW = Math.Max(1, usableW - leftW);
        var rightX = hostBounds.X + leftW + splitter;

        var paneBounds = new Rectangle[paneCount];
        var headerBounds = new Rectangle[paneCount];
        var contentBounds = new Rectangle[paneCount];
        var splitters = new List<MultiViewSplitterDescriptor>(2);

        if (preset == MultiViewPreset.Split)
        {
            paneBounds[0] = new Rectangle(hostBounds.X, hostBounds.Y, leftW, height);
            paneBounds[1] = new Rectangle(rightX, hostBounds.Y, rightW, height);
            splitters.Add(new MultiViewSplitterDescriptor(
                0,
                IsVertical: true,
                new Rectangle(hostBounds.X + leftW, hostBounds.Y, splitter, height),
                hostBounds));
        }
        else
        {
            var minPaneH = Math.Min(180, Math.Max(32, (height - splitter) / 3));
            var usableH = Math.Max(2, height - splitter);
            var maxTopH = Math.Max(minPaneH, usableH - minPaneH);
            var topH = Math.Clamp(
                (int)Math.Round(usableH * state.VerticalSplitRatio),
                Math.Min(minPaneH, maxTopH),
                maxTopH);
            var bottomH = Math.Max(1, usableH - topH);
            var bottomY = hostBounds.Y + topH + splitter;

            if (preset == MultiViewPreset.Tri)
            {
                paneBounds[0] = new Rectangle(hostBounds.X, hostBounds.Y, leftW, height);
                paneBounds[1] = new Rectangle(rightX, hostBounds.Y, rightW, topH);
                paneBounds[2] = new Rectangle(rightX, bottomY, rightW, bottomH);
                splitters.Add(new MultiViewSplitterDescriptor(
                    0,
                    IsVertical: true,
                    new Rectangle(hostBounds.X + leftW, hostBounds.Y, splitter, height),
                    hostBounds));
                splitters.Add(new MultiViewSplitterDescriptor(
                    1,
                    IsVertical: false,
                    new Rectangle(rightX, hostBounds.Y + topH, rightW, splitter),
                    new Rectangle(rightX, hostBounds.Y, rightW, height)));
            }
            else
            {
                paneBounds[0] = new Rectangle(hostBounds.X, hostBounds.Y, leftW, topH);
                paneBounds[1] = new Rectangle(rightX, hostBounds.Y, rightW, topH);
                paneBounds[2] = new Rectangle(hostBounds.X, bottomY, leftW, bottomH);
                paneBounds[3] = new Rectangle(rightX, bottomY, rightW, bottomH);
                splitters.Add(new MultiViewSplitterDescriptor(
                    0,
                    IsVertical: true,
                    new Rectangle(hostBounds.X + leftW, hostBounds.Y, splitter, height),
                    hostBounds));
                splitters.Add(new MultiViewSplitterDescriptor(
                    1,
                    IsVertical: false,
                    new Rectangle(hostBounds.X, hostBounds.Y + topH, width, splitter),
                    hostBounds));
            }
        }

        for (var i = 0; i < paneCount; i++)
        {
            var pane = paneBounds[i];
            var effectiveHeader = Math.Min(header, Math.Max(18, pane.Height / 4));
            headerBounds[i] = new Rectangle(pane.X, pane.Y, pane.Width, effectiveHeader);
            contentBounds[i] = new Rectangle(
                pane.X,
                pane.Y + effectiveHeader,
                pane.Width,
                Math.Max(1, pane.Height - effectiveHeader));
        }

        return new MultiViewComputedLayout(
            preset,
            paneCount,
            paneBounds,
            headerBounds,
            contentBounds,
            splitters.ToArray());
    }
}

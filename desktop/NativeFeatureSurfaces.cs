using System.Drawing.Drawing2D;

namespace MishaWeb;

/// <summary>
/// Shared, allocation-free layout defaults for the small native dialogs.
/// Values are authored at 96 DPI and WinForms scales them through PerMonitorV2.
/// </summary>
internal static class NativeDialogLayout
{
    internal const int ActionRowHeight = 44;
    internal const int WrappedActionRowHeight = 84;
    internal static readonly Font QueryFont = new("Segoe UI", 11f);
    internal static readonly Font ListFont = new("Segoe UI", 10f);
    internal static readonly Font CompactListFont = new("Segoe UI", 9.5f);

    internal static void EnableDpiScaling(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.AutoScaleDimensions = new SizeF(96f, 96f);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = SystemFonts.MessageBoxFont;
    }

    internal static FlowLayoutPanel CreateActionBar(bool wrap = false)
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = wrap,
            Margin = Padding.Empty,
            Padding = new Padding(0, 1, 0, 1)
        };
    }

    internal static void ConfigureButton(Button button, string text, string description)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(72, 30);
        button.Padding = new Padding(8, 0, 8, 0);
        button.Text = text;
        button.Margin = new Padding(4, 4, 0, 4);
        button.AccessibleName = text;
        button.AccessibleDescription = description;
    }

    internal static Point ClampToWorkingArea(Form form, Point proposedLocation)
    {
        ArgumentNullException.ThrowIfNull(form);
        var workingArea = Screen.FromPoint(proposedLocation).WorkingArea;
        var maximumX = Math.Max(workingArea.Left, workingArea.Right - form.Width);
        var maximumY = Math.Max(workingArea.Top, workingArea.Bottom - form.Height);
        return new Point(
            Math.Clamp(proposedLocation.X, workingArea.Left, maximumX),
            Math.Clamp(proposedLocation.Y, workingArea.Top, maximumY));
    }
}

internal sealed class CommandPaletteForm : Form
{
    private readonly TextBox queryBox = new();
    private readonly ListBox resultList = new();
    private readonly System.Windows.Forms.Timer queryTimer = new() { Interval = 35 };
    private readonly Func<string, IReadOnlyList<RankedCommand>> rowProvider;
    private readonly Action<CommandCandidate> activate;
    private IReadOnlyList<RankedCommand> rows = [];

    public CommandPaletteForm(
        Func<string, IReadOnlyList<RankedCommand>> rowProvider,
        Action<CommandCandidate> activate)
    {
        this.rowProvider = rowProvider ?? throw new ArgumentNullException(nameof(rowProvider));
        this.activate = activate ?? throw new ArgumentNullException(nameof(activate));
        NativeDialogLayout.EnableDpiScaling(this);

        Text = "Command palette";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        ClientSize = new Size(680, 410);
        MinimumSize = new Size(520, 320);
        BackColor = SystemColors.Window;
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Command palette";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            RowCount = 2,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        queryBox.Dock = DockStyle.Fill;
        queryBox.PlaceholderText = "Type a command, tab, favorite, session, or closed tab";
        queryBox.AccessibleName = "Command palette search";
        queryBox.Font = NativeDialogLayout.QueryFont;
        resultList.Dock = DockStyle.Fill;
        resultList.IntegralHeight = false;
        resultList.Font = NativeDialogLayout.ListFont;
        resultList.AccessibleName = "Command palette results";
        root.Controls.Add(queryBox, 0, 0);
        root.Controls.Add(resultList, 0, 1);
        Controls.Add(root);
        NativeUiTheme.Apply(this);
        NativeUiTheme.ConfigureModernListBox(
            resultList,
            50,
            (index, _) => BuildPaletteVisual(index));

        queryBox.TextChanged += (_, _) => QueueRefresh();
        queryBox.KeyDown += OnKeyDown;
        resultList.KeyDown += OnKeyDown;
        resultList.MouseClick += (_, _) => ActivateSelected();
        resultList.DoubleClick += (_, _) => ActivateSelected();
        queryTimer.Tick += (_, _) =>
        {
            queryTimer.Stop();
            RefreshRows();
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                CloseWithoutActivation();
            }
        };
        FormClosed += (_, _) => queryTimer.Stop();
        Deactivate += (_, _) =>
        {
            if (Visible) CloseWithoutActivation();
        };
    }

    public event EventHandler? Cancelled;

    public event EventHandler<CommandCandidate>? CommandActivated;

    public void Open(IWin32Window owner)
    {
        if (owner is Control control && control.IsHandleCreated)
        {
            var ownerBounds = control.RectangleToScreen(control.ClientRectangle);
            Location = NativeDialogLayout.ClampToWorkingArea(this, new Point(
                ownerBounds.Left + Math.Max(0, (ownerBounds.Width - Width) / 2),
                ownerBounds.Top + Math.Max(20, ownerBounds.Height / 5)));
        }

        queryBox.Text = string.Empty;
        RefreshRows();
        if (!Visible) Show(owner);
        Activate();
        queryBox.Focus();
        queryBox.SelectAll();
    }

    public void CloseWithoutActivation()
    {
        if (!Visible) return;
        queryTimer.Stop();
        Hide();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void QueueRefresh()
    {
        if (!Visible) return;
        queryTimer.Stop();
        queryTimer.Start();
    }

    private void RefreshRows()
    {
        if (IsDisposed) return;
        rows = rowProvider(queryBox.Text);
        resultList.BeginUpdate();
        try
        {
            resultList.Items.Clear();
            foreach (var row in rows)
            {
                var candidate = row.Candidate;
                var category = candidate.Source switch
                {
                    CommandSource.BrowserCommand => "Command",
                    CommandSource.OpenTab => "Open tab",
                    CommandSource.Favorite => "Favorite",
                    CommandSource.Session => "Session",
                    CommandSource.RecentlyClosed => "Recently closed",
                    _ => "History"
                };
                var shortcut = string.IsNullOrWhiteSpace(candidate.Shortcut)
                    ? string.Empty
                    : $"  [{candidate.Shortcut}]";
                resultList.Items.Add(new PaletteRowDisplay(
                    $"{category}: {candidate.Title}  \u2014  {candidate.Detail}{shortcut}",
                    $"{category}: {candidate.Title}; {candidate.Detail}; shortcut {candidate.Shortcut}"));
            }
        }
        finally
        {
            resultList.EndUpdate();
        }

        resultList.SelectedIndex = resultList.Items.Count > 0 ? 0 : -1;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            ActivateSelected();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            CloseWithoutActivation();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Down && sender == queryBox)
        {
            resultList.Focus();
            if (resultList.Items.Count > 0) resultList.SelectedIndex = 0;
            e.SuppressKeyPress = true;
        }
    }

    private void ActivateSelected()
    {
        if (resultList.SelectedIndex < 0 || resultList.SelectedIndex >= rows.Count) return;
        var candidate = rows[resultList.SelectedIndex].Candidate;
        queryTimer.Stop();
        Hide();
        CommandActivated?.Invoke(this, candidate);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;

        // Palette rows can contain a BrowserTab target. Do not let a hidden,
        // reusable palette keep a closed tab shell alive until it is opened
        // again later in the session.
        queryTimer.Stop();
        rows = [];
        resultList.Items.Clear();
        queryBox.Clear();
    }

    private ModernListRowVisual BuildPaletteVisual(int index)
    {
        if (index < 0 || index >= rows.Count)
        {
            return new ModernListRowVisual(string.Empty, string.Empty, string.Empty);
        }

        var candidate = rows[index].Candidate;
        var (category, glyph, accent) = candidate.Source switch
        {
            CommandSource.BrowserCommand => ("Command", ">", NativeUiTheme.Accent),
            CommandSource.OpenTab => ("Open tab", "T", NativeUiTheme.Positive),
            CommandSource.Favorite => ("Favorite", "*", NativeUiTheme.BrandCoral),
            CommandSource.Session => ("Session", "S", NativeUiTheme.Lavender),
            CommandSource.RecentlyClosed => ("Closed", "R", NativeUiTheme.Focus),
            _ => ("History", "H", NativeUiTheme.Muted)
        };
        return new ModernListRowVisual(
            candidate.Title,
            candidate.Detail,
            glyph,
            accent,
            StatusText: category,
            StatusColor: accent,
            ShortcutText: string.IsNullOrWhiteSpace(candidate.Shortcut) ? null : candidate.Shortcut);
    }

    private sealed record PaletteRowDisplay(string Text, string AccessibleName)
    {
        public override string ToString() => Text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) queryTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class MruSwitcherForm : Form
{
    private readonly ListBox itemList = new();
    private IReadOnlyList<string> labels = [];
    private bool suppressDeactivate;

    public MruSwitcherForm()
    {
        NativeDialogLayout.EnableDpiScaling(this);
        Text = "Switch tab";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        ClientSize = new Size(460, 310);
        MinimumSize = new Size(360, 220);
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Recent tabs";

        itemList.Dock = DockStyle.Fill;
        itemList.IntegralHeight = false;
        itemList.Font = NativeDialogLayout.ListFont;
        itemList.AccessibleName = "Recent tabs list";
        Controls.Add(itemList);
        NativeUiTheme.Apply(this);
        NativeUiTheme.ConfigureModernListBox(
            itemList,
            46,
            (index, _) =>
            {
                if (index < 0 || index >= labels.Count)
                {
                    return new ModernListRowVisual(string.Empty, string.Empty, string.Empty);
                }
                var raw = labels[index];
                var parts = raw.Split(" \u2014 ", 2, StringSplitOptions.None);
                return new ModernListRowVisual(
                    parts[0],
                    parts.Length > 1 ? parts[1] : "Recent browser tab",
                    "T",
                    NativeUiTheme.Accent);
            });
        KeyUp += (_, e) =>
        {
            if (e.KeyCode == Keys.Control && Visible) CommitSelection();
        };
        Deactivate += (_, _) =>
        {
            if (!suppressDeactivate) CancelSelection();
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                CancelSelection();
            }
        };
    }

    public event EventHandler? Cancelled;

    public event EventHandler? Committed;

    public int SelectedIndex => itemList.SelectedIndex;

    public void Open(IWin32Window owner, IReadOnlyList<string> rowLabels, int selectedIndex = 0)
    {
        labels = rowLabels.Take(8).ToArray();
        itemList.Items.Clear();
        itemList.Items.AddRange(labels.Cast<object>().ToArray());
        itemList.SelectedIndex = labels.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, labels.Count - 1);
        if (owner is Control control && control.IsHandleCreated)
        {
            var bounds = control.RectangleToScreen(control.ClientRectangle);
            Location = NativeDialogLayout.ClampToWorkingArea(this, new Point(
                bounds.Left + Math.Max(0, (bounds.Width - Width) / 2),
                bounds.Top + Math.Max(0, (bounds.Height - Height) / 2)));
        }
        if (!Visible) Show(owner);
        Activate();
        itemList.Focus();
    }

    public void MoveSelection(int direction)
    {
        if (itemList.Items.Count == 0) return;
        var next = (itemList.SelectedIndex + (direction < 0 ? -1 : 1) + itemList.Items.Count)
            % itemList.Items.Count;
        itemList.SelectedIndex = next;
    }

    public string? GetSelectedLabel()
    {
        return itemList.SelectedIndex >= 0 && itemList.SelectedIndex < labels.Count
            ? labels[itemList.SelectedIndex]
            : null;
    }

    public void CommitSelection()
    {
        if (!Visible) return;
        suppressDeactivate = true;
        try { Hide(); }
        finally { suppressDeactivate = false; }
        Committed?.Invoke(this, EventArgs.Empty);
    }

    public void CancelSelection()
    {
        if (!Visible) return;
        suppressDeactivate = true;
        try { Hide(); }
        finally { suppressDeactivate = false; }
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Tab))
        {
            MoveSelection(1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            MoveSelection(-1);
            return true;
        }
        if (keyData == Keys.Escape)
        {
            CancelSelection();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

internal enum SessionManagerAction
{
    NewFromCurrent,
    Open,
    Replace,
    Update,
    Rename,
    Delete
}

internal sealed record SessionManagerRequest(SessionManagerAction Action, SavedSessionEntry? Session);

internal sealed class SessionManagerForm : Form
{
    private readonly ListBox sessionList = new();
    private readonly Button newButton = new();
    private readonly Button openButton = new();
    private readonly Button replaceButton = new();
    private readonly Button updateButton = new();
    private readonly Button renameButton = new();
    private readonly Button deleteButton = new();
    private readonly Button closeButton = new();
    private IReadOnlyList<SavedSessionEntry> sessions = [];

    public SessionManagerForm()
    {
        NativeDialogLayout.EnableDpiScaling(this);
        Text = "Saved sessions";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(560, 400);
        Size = new Size(720, 500);
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Saved sessions manager";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 2,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, NativeDialogLayout.WrappedActionRowHeight));
        sessionList.Dock = DockStyle.Fill;
        sessionList.IntegralHeight = false;
        sessionList.Font = NativeDialogLayout.ListFont;
        sessionList.AccessibleName = "Saved sessions list";
        root.Controls.Add(sessionList, 0, 0);

        var actions = NativeDialogLayout.CreateActionBar(wrap: true);
        ConfigureButton(newButton, "New from current", "Save the current window as a new session");
        ConfigureButton(openButton, "Open", "Append the selected session");
        ConfigureButton(replaceButton, "Replace", "Replace current tabs with the selected session");
        ConfigureButton(updateButton, "Update", "Update the selected session from the current window");
        ConfigureButton(renameButton, "Rename", "Rename the selected session");
        ConfigureButton(deleteButton, "Delete", "Delete the selected session");
        ConfigureButton(closeButton, "Close", "Close the session manager");
        actions.Controls.AddRange([closeButton, deleteButton, renameButton, updateButton, replaceButton, openButton, newButton]);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);
        NativeUiTheme.Apply(this, openButton);
        NativeUiTheme.ConfigureModernListBox(
            sessionList,
            50,
            (index, _) =>
            {
                if (index < 0 || index >= sessions.Count)
                {
                    return new ModernListRowVisual(string.Empty, string.Empty, string.Empty);
                }
                var session = sessions[index];
                var age = session.UpdatedUtc == DateTimeOffset.UnixEpoch
                    ? "not dated"
                    : session.UpdatedUtc.ToLocalTime().ToString("g");
                var tabCount = session.Tabs.Count;
                return new ModernListRowVisual(
                    session.Name,
                    $"Updated {age}",
                    "S",
                    NativeUiTheme.Lavender,
                    StatusText: tabCount == 1 ? "1 tab" : $"{tabCount} tabs",
                    StatusColor: NativeUiTheme.Accent);
            });

        sessionList.SelectedIndexChanged += (_, _) => UpdateButtons();
        sessionList.DoubleClick += (_, _) => Raise(SessionManagerAction.Open);
        sessionList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) Raise(SessionManagerAction.Open);
            else if (e.KeyCode == Keys.Delete) Raise(SessionManagerAction.Delete);
        };
        newButton.Click += (_, _) => Raise(SessionManagerAction.NewFromCurrent);
        openButton.Click += (_, _) => Raise(SessionManagerAction.Open);
        replaceButton.Click += (_, _) => Raise(SessionManagerAction.Replace);
        updateButton.Click += (_, _) => Raise(SessionManagerAction.Update);
        renameButton.Click += (_, _) => Raise(SessionManagerAction.Rename);
        deleteButton.Click += (_, _) => Raise(SessionManagerAction.Delete);
        closeButton.Click += (_, _) => Hide();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        FormClosed += (_, _) => Hide();
        UpdateButtons();
    }

    public event EventHandler<SessionManagerRequest>? ActionRequested;

    public void Open(IWin32Window owner, IReadOnlyList<SavedSessionEntry> entries)
    {
        SetSessions(entries);
        if (!Visible) Show(owner);
        Activate();
        sessionList.Focus();
    }

    public void SetSessions(IReadOnlyList<SavedSessionEntry> entries)
    {
        sessions = entries ?? [];
        sessionList.BeginUpdate();
        try
        {
            sessionList.Items.Clear();
            foreach (var session in sessions)
            {
                var age = session.UpdatedUtc == DateTimeOffset.UnixEpoch
                    ? "not dated"
                    : session.UpdatedUtc.ToLocalTime().ToString("g");
                sessionList.Items.Add($"{session.Name} \u2014 {session.Tabs.Count} tabs \u2014 {age}");
            }
        }
        finally
        {
            sessionList.EndUpdate();
        }

        if (sessionList.Items.Count > 0) sessionList.SelectedIndex = 0;
        UpdateButtons();
    }

    public SavedSessionEntry? SelectedSession =>
        sessionList.SelectedIndex >= 0 && sessionList.SelectedIndex < sessions.Count
            ? sessions[sessionList.SelectedIndex]
            : null;

    private void ConfigureButton(Button button, string text, string description)
    {
        NativeDialogLayout.ConfigureButton(button, text, description);
    }

    private void Raise(SessionManagerAction action)
    {
        if (action is not SessionManagerAction.NewFromCurrent && SelectedSession is null) return;
        ActionRequested?.Invoke(this, new SessionManagerRequest(action, SelectedSession));
    }

    private void UpdateButtons()
    {
        var hasSelection = SelectedSession is not null;
        openButton.Enabled = hasSelection;
        replaceButton.Enabled = hasSelection;
        updateButton.Enabled = hasSelection;
        renameButton.Enabled = hasSelection;
        deleteButton.Enabled = hasSelection;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;
        sessions = [];
        sessionList.Items.Clear();
        UpdateButtons();
    }
}

internal enum PermissionPromptDecision
{
    AllowOnce,
    AllowAlways,
    Block,
    Cancel
}

internal sealed class PermissionPromptForm : Form
{
    private readonly Label detailLabel = new();
    private readonly Button onceButton = new();
    private readonly Button alwaysButton = new();
    private readonly Button blockButton = new();
    private readonly Button cancelButton = new();

    public PermissionPromptForm()
    {
        NativeDialogLayout.EnableDpiScaling(this);
        Text = "Website permission";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 190);
        MinimumSize = new Size(440, 190);
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Website permission request";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, NativeDialogLayout.ActionRowHeight));
        detailLabel.Dock = DockStyle.Fill;
        detailLabel.AutoEllipsis = true;
        detailLabel.TextAlign = ContentAlignment.MiddleLeft;
        detailLabel.AccessibleName = "Permission request details";
        root.Controls.Add(detailLabel, 0, 0);

        var actions = NativeDialogLayout.CreateActionBar();
        ConfigureButton(onceButton, "Allow once", "Allow only this request");
        ConfigureButton(alwaysButton, "Always allow", "Remember this permission");
        ConfigureButton(blockButton, "Block", "Block and remember this permission");
        ConfigureButton(cancelButton, "Cancel", "Deny this request once");
        actions.Controls.AddRange([cancelButton, blockButton, alwaysButton, onceButton]);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);
        NativeUiTheme.Apply(this, onceButton);

        onceButton.Click += (_, _) => Decide(PermissionPromptDecision.AllowOnce);
        alwaysButton.Click += (_, _) => Decide(PermissionPromptDecision.AllowAlways);
        blockButton.Click += (_, _) => Decide(PermissionPromptDecision.Block);
        cancelButton.Click += (_, _) => Decide(PermissionPromptDecision.Cancel);
        CancelButton = cancelButton;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                DecisionRequested?.Invoke(this, PermissionPromptDecision.Cancel);
            }
        };
    }

    public event EventHandler<PermissionPromptDecision>? DecisionRequested;

    public void ShowRequest(IWin32Window owner, string origin, string permission, bool userInitiated, bool allowAlways)
    {
        detailLabel.Text = $"{origin} requests {permission}.\r\n\r\nUser gesture: {(userInitiated ? "yes" : "no")}";
        detailLabel.AccessibleName = detailLabel.Text.Replace("\r\n", " ", StringComparison.Ordinal);
        detailLabel.AccessibleDescription = detailLabel.AccessibleName;
        alwaysButton.Visible = allowAlways;
        if (!Visible) Show(owner);
        Activate();
        onceButton.Focus();
    }

    public void CloseRequest()
    {
        if (Visible) Hide();
    }

    public void CancelRequest()
    {
        if (Visible) Decide(PermissionPromptDecision.Cancel);
    }

    private void ConfigureButton(Button button, string text, string description)
    {
        NativeDialogLayout.ConfigureButton(button, text, description);
    }

    private void Decide(PermissionPromptDecision decision)
    {
        Hide();
        DecisionRequested?.Invoke(this, decision);
    }
}

internal sealed record PermissionSettingRow(
    string Origin,
    string Permission,
    string State,
    object? Token = null);

internal enum PermissionManagerAction
{
    ResetSelected,
    ResetAll
}

internal sealed class PermissionManagerForm : Form
{
    private readonly ListBox settingList = new();
    private readonly Button resetButton = new();
    private readonly Button resetAllButton = new();
    private readonly Button closeButton = new();
    private IReadOnlyList<PermissionSettingRow> rows = [];

    public PermissionManagerForm()
    {
        NativeDialogLayout.EnableDpiScaling(this);
        Text = "Website permissions";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(620, 360);
        Size = new Size(820, 520);
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Website permission manager";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 2,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, NativeDialogLayout.ActionRowHeight));
        settingList.Dock = DockStyle.Fill;
        settingList.IntegralHeight = false;
        settingList.Font = NativeDialogLayout.ListFont;
        settingList.AccessibleName = "Saved website permissions list";
        root.Controls.Add(settingList, 0, 0);
        var actions = NativeDialogLayout.CreateActionBar();
        ConfigureButton(resetButton, "Reset selected", "Return the selected permission to its default");
        ConfigureButton(resetAllButton, "Reset all", "Return every saved permission to its default");
        ConfigureButton(closeButton, "Close", "Close the permission manager");
        actions.Controls.AddRange([closeButton, resetAllButton, resetButton]);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);
        NativeUiTheme.Apply(this);
        NativeUiTheme.ConfigureModernListBox(
            settingList,
            50,
            (index, _) =>
            {
                if (index < 0 || index >= rows.Count)
                {
                    return new ModernListRowVisual(string.Empty, string.Empty, string.Empty);
                }
                var row = rows[index];
                var statusColor = row.State.Contains("Allow", StringComparison.OrdinalIgnoreCase)
                    ? NativeUiTheme.Positive
                    : row.State.Contains("Block", StringComparison.OrdinalIgnoreCase)
                        || row.State.Contains("Deny", StringComparison.OrdinalIgnoreCase)
                        ? NativeUiTheme.Negative
                        : NativeUiTheme.Muted;
                var glyph = string.IsNullOrWhiteSpace(row.Permission)
                    ? "P"
                    : char.ToUpperInvariant(row.Permission.TrimStart()[0]).ToString();
                return new ModernListRowVisual(
                    row.Origin,
                    row.Permission,
                    glyph,
                    statusColor,
                    StatusText: row.State,
                    StatusColor: statusColor);
            });
        settingList.SelectedIndexChanged += (_, _) => UpdateButtons();
        resetButton.Click += (_, _) => Raise(PermissionManagerAction.ResetSelected);
        resetAllButton.Click += (_, _) => Raise(PermissionManagerAction.ResetAll);
        closeButton.Click += (_, _) => Hide();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        UpdateButtons();
    }

    public event EventHandler<PermissionManagerAction>? ActionRequested;

    public void Open(IWin32Window owner, IReadOnlyList<PermissionSettingRow> entries)
    {
        SetRows(entries);
        if (!Visible) Show(owner);
        Activate();
        settingList.Focus();
    }

    public void SetRows(IReadOnlyList<PermissionSettingRow> entries)
    {
        rows = entries ?? [];
        settingList.Items.Clear();
        foreach (var row in rows)
        {
            settingList.Items.Add($"{row.Origin} \u2014 {row.Permission} \u2014 {row.State}");
        }
        if (settingList.Items.Count > 0) settingList.SelectedIndex = 0;
        UpdateButtons();
    }

    public PermissionSettingRow? SelectedRow =>
        settingList.SelectedIndex >= 0 && settingList.SelectedIndex < rows.Count
            ? rows[settingList.SelectedIndex]
            : null;

    private void ConfigureButton(Button button, string text, string description)
    {
        NativeDialogLayout.ConfigureButton(button, text, description);
    }

    private void Raise(PermissionManagerAction action)
    {
        if (action == PermissionManagerAction.ResetSelected && SelectedRow is null) return;
        ActionRequested?.Invoke(this, action);
    }

    private void UpdateButtons()
    {
        resetButton.Enabled = SelectedRow is not null;
        resetAllButton.Enabled = rows.Count > 0;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;

        // Permission rows carry WebView2 permission-setting wrappers as their
        // tokens. Release them as soon as the manager is hidden so an old
        // profile/environment can be reclaimed after a browser restart.
        rows = [];
        settingList.Items.Clear();
        UpdateButtons();
    }
}

internal enum DownloadPopupAction
{
    Pause,
    Resume,
    Cancel,
    Open,
    ShowInFolder,
    CopyAddress,
    Remove
}

internal sealed record DownloadPopupRow(
    string FileName,
    string State,
    string Progress,
    string Detail,
    bool CanPause,
    bool CanResume,
    bool CanCancel,
    bool CanOpen,
    bool CanShowInFolder,
    object Token);

internal sealed class DownloadsPopupForm : Form
{
    private readonly Func<IReadOnlyList<DownloadPopupRow>> rowProvider;
    private readonly Action<DownloadPopupAction, object> action;
    private readonly ListBox downloadList = new();
    private readonly Button pauseButton = new();
    private readonly Button resumeButton = new();
    private readonly Button cancelButton = new();
    private readonly Button openButton = new();
    private readonly Button folderButton = new();
    private readonly Button copyButton = new();
    private readonly Button removeButton = new();
    private IReadOnlyList<DownloadPopupRow> rows = [];

    public DownloadsPopupForm(
        Func<IReadOnlyList<DownloadPopupRow>> rowProvider,
        Action<DownloadPopupAction, object> action)
    {
        this.rowProvider = rowProvider ?? throw new ArgumentNullException(nameof(rowProvider));
        this.action = action ?? throw new ArgumentNullException(nameof(action));
        NativeDialogLayout.EnableDpiScaling(this);
        Text = "Downloads";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(520, 340);
        Size = new Size(700, 450);
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Downloads";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            RowCount = 2,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, NativeDialogLayout.WrappedActionRowHeight));
        downloadList.Dock = DockStyle.Fill;
        downloadList.IntegralHeight = false;
        downloadList.Font = NativeDialogLayout.CompactListFont;
        downloadList.AccessibleName = "Downloads list";
        root.Controls.Add(downloadList, 0, 0);
        var actions = NativeDialogLayout.CreateActionBar(wrap: true);
        ConfigureButton(pauseButton, "Pause", "Pause the selected download");
        ConfigureButton(resumeButton, "Resume", "Resume the selected download");
        ConfigureButton(cancelButton, "Cancel", "Cancel the selected download");
        ConfigureButton(openButton, "Open", "Open the downloaded file");
        ConfigureButton(folderButton, "Show in folder", "Show the downloaded file in its folder");
        ConfigureButton(copyButton, "Copy address", "Copy the download address");
        ConfigureButton(removeButton, "Remove", "Remove the selected terminal download from the list");
        actions.Controls.AddRange([removeButton, copyButton, folderButton, openButton, cancelButton, resumeButton, pauseButton]);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);
        NativeUiTheme.Apply(this, openButton);
        NativeUiTheme.ConfigureModernListBox(
            downloadList,
            52,
            (index, _) =>
            {
                if (index < 0 || index >= rows.Count)
                {
                    return new ModernListRowVisual(string.Empty, string.Empty, string.Empty);
                }
                var row = rows[index];
                var stateColor = row.State.Equals("Complete", StringComparison.OrdinalIgnoreCase)
                    ? NativeUiTheme.Positive
                    : row.State.Equals("Interrupted", StringComparison.OrdinalIgnoreCase)
                        || row.State.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
                        ? NativeUiTheme.Negative
                        : row.State.Equals("Paused", StringComparison.OrdinalIgnoreCase)
                            ? NativeUiTheme.SecondaryText
                            : NativeUiTheme.Accent;
                var detail = string.IsNullOrWhiteSpace(row.Detail)
                    ? row.Progress
                    : $"{row.Progress} \u00B7 {row.Detail}";
                return new ModernListRowVisual(
                    row.FileName,
                    detail,
                    "D",
                    stateColor,
                    StatusText: row.State,
                    StatusColor: stateColor);
            });

        downloadList.SelectedIndexChanged += (_, _) => UpdateButtons();
        downloadList.DoubleClick += (_, _) => Raise(DownloadPopupAction.Open);
        downloadList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) Raise(DownloadPopupAction.Open);
            else if (e.KeyCode == Keys.Delete) Raise(DownloadPopupAction.Remove);
        };
        pauseButton.Click += (_, _) => Raise(DownloadPopupAction.Pause);
        resumeButton.Click += (_, _) => Raise(DownloadPopupAction.Resume);
        cancelButton.Click += (_, _) => Raise(DownloadPopupAction.Cancel);
        openButton.Click += (_, _) => Raise(DownloadPopupAction.Open);
        folderButton.Click += (_, _) => Raise(DownloadPopupAction.ShowInFolder);
        copyButton.Click += (_, _) => Raise(DownloadPopupAction.CopyAddress);
        removeButton.Click += (_, _) => Raise(DownloadPopupAction.Remove);
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        UpdateButtons();
    }

    public void Open(IWin32Window owner, Control? anchor = null)
    {
        RefreshRows();
        if (anchor is not null && anchor.IsHandleCreated)
        {
            var bounds = anchor.RectangleToScreen(anchor.ClientRectangle);
            Location = NativeDialogLayout.ClampToWorkingArea(
                this,
                new Point(bounds.Right - Width, bounds.Bottom + 4));
        }
        if (!Visible) Show(owner);
        Activate();
        downloadList.Focus();
    }

    public void RefreshRows()
    {
        var selectedToken = SelectedRow?.Token;
        var previousIndex = downloadList.SelectedIndex;
        rows = rowProvider();
        downloadList.BeginUpdate();
        try
        {
            downloadList.Items.Clear();
            foreach (var row in rows)
            {
                downloadList.Items.Add($"{row.State} \u2014 {row.FileName} \u2014 {row.Progress} \u2014 {row.Detail}");
            }
        }
        finally
        {
            downloadList.EndUpdate();
        }
        if (rows.Count > 0)
        {
            var restoredIndex = -1;
            if (selectedToken is not null)
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    if (!Equals(rows[index].Token, selectedToken)) continue;
                    restoredIndex = index;
                    break;
                }
            }
            downloadList.SelectedIndex = restoredIndex >= 0
                ? restoredIndex
                : Math.Clamp(previousIndex, 0, rows.Count - 1);
        }
        UpdateButtons();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private DownloadPopupRow? SelectedRow =>
        downloadList.SelectedIndex >= 0 && downloadList.SelectedIndex < rows.Count
            ? rows[downloadList.SelectedIndex]
            : null;

    private void ConfigureButton(Button button, string text, string description)
    {
        NativeDialogLayout.ConfigureButton(button, text, description);
    }

    private void Raise(DownloadPopupAction requestedAction)
    {
        var row = SelectedRow;
        if (row is null) return;
        if (requestedAction == DownloadPopupAction.Pause && !row.CanPause) return;
        if (requestedAction == DownloadPopupAction.Resume && !row.CanResume) return;
        if (requestedAction == DownloadPopupAction.Cancel && !row.CanCancel) return;
        if (requestedAction == DownloadPopupAction.Open && !row.CanOpen) return;
        if (requestedAction == DownloadPopupAction.ShowInFolder && !row.CanShowInFolder) return;
        if (requestedAction == DownloadPopupAction.Remove && row.CanCancel) return;
        action(requestedAction, row.Token);
        if (Visible) RefreshRows();
    }

    private void UpdateButtons()
    {
        var row = SelectedRow;
        pauseButton.Enabled = row?.CanPause == true;
        resumeButton.Enabled = row?.CanResume == true;
        cancelButton.Enabled = row?.CanCancel == true;
        openButton.Enabled = row?.CanOpen == true;
        folderButton.Enabled = row?.CanShowInFolder == true;
        copyButton.Enabled = row is not null;
        removeButton.Enabled = row is not null && !row.CanCancel && !row.CanPause && !row.CanResume;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) return;
        rows = [];
        downloadList.Items.Clear();
        UpdateButtons();
    }
}

internal sealed record DownloadNotificationState(
    string FileName,
    string State,
    string ProgressText,
    string DetailText,
    int? Percent,
    int ActiveCount,
    bool CanOpen,
    bool CanShowInFolder,
    bool CanCancel,
    object Token);

/// <summary>
/// Non-activating toast anchored beneath the toolbar Downloads button so users
/// get immediate visual confirmation and quick actions as soon as a download starts.
/// </summary>
internal sealed class DownloadNotificationForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int CompletionAutoHideMs = 5_000;
    private const int HoverRecheckMs = 1_200;

    private static readonly Font HeaderFont = new("Segoe UI Semibold", 9f);
    private static readonly Font FileNameFont = new("Segoe UI Semibold", 9.5f);
    private static readonly Font DetailFont = new("Segoe UI", 8.25f);
    private static readonly Font DismissFont = new("Segoe UI", 9.5f);

    private readonly Action<DownloadPopupAction, object> action;
    private readonly Action openDownloadsList;
    private readonly Label statusLabel = new();
    private readonly Button dismissButton = new();
    private readonly Label fileNameLabel = new();
    private readonly Label detailLabel = new();
    private readonly Panel progressTrack = new();
    private readonly Button openButton = new();
    private readonly Button folderButton = new();
    private readonly Button cancelButton = new();
    private readonly Button allDownloadsButton = new();
    private readonly System.Windows.Forms.Timer autoHideTimer = new() { Interval = CompletionAutoHideMs };
    private DownloadNotificationState? currentState;

    public DownloadNotificationForm(
        Action<DownloadPopupAction, object> action,
        Action openDownloadsList)
    {
        this.action = action ?? throw new ArgumentNullException(nameof(action));
        this.openDownloadsList = openDownloadsList ?? throw new ArgumentNullException(nameof(openDownloadsList));
        NativeDialogLayout.EnableDpiScaling(this);
        DoubleBuffered = true;
        Text = "Download notification";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        ClientSize = new Size(376, 136);
        Padding = new Padding(1);
        AccessibleRole = AccessibleRole.Alert;
        AccessibleName = "Download notification";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(11, 9, 11, 9),
            RowCount = 5,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        var headerRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
            ColumnCount = 2
        };
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));

        statusLabel.Dock = DockStyle.Fill;
        statusLabel.Font = HeaderFont;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.AutoEllipsis = true;
        statusLabel.AccessibleName = "Download status";

        dismissButton.Dock = DockStyle.Fill;
        dismissButton.Margin = Padding.Empty;
        dismissButton.Text = "\u00D7";
        dismissButton.Font = DismissFont;
        dismissButton.TabStop = false;
        dismissButton.AccessibleName = "Dismiss download notification";
        dismissButton.AccessibleDescription = "Hide this download notification";

        headerRow.Controls.Add(statusLabel, 0, 0);
        headerRow.Controls.Add(dismissButton, 1, 0);

        fileNameLabel.Dock = DockStyle.Fill;
        fileNameLabel.Font = FileNameFont;
        fileNameLabel.TextAlign = ContentAlignment.MiddleLeft;
        fileNameLabel.AutoEllipsis = true;
        fileNameLabel.AccessibleName = "Downloading file name";

        detailLabel.Dock = DockStyle.Fill;
        detailLabel.Font = DetailFont;
        detailLabel.TextAlign = ContentAlignment.MiddleLeft;
        detailLabel.AutoEllipsis = true;
        detailLabel.AccessibleName = "Download progress";

        progressTrack.Dock = DockStyle.Fill;
        progressTrack.Margin = new Padding(0, 3, 0, 3);
        progressTrack.Paint += OnProgressTrackPaint;

        var actions = NativeDialogLayout.CreateActionBar(wrap: false);
        ConfigureCompactButton(openButton, "Open", "Open the downloaded file");
        ConfigureCompactButton(folderButton, "Show in folder", "Show the downloaded file in its folder");
        ConfigureCompactButton(cancelButton, "Cancel", "Cancel the download");
        ConfigureCompactButton(allDownloadsButton, "Downloads", "Show all downloads");
        actions.Controls.AddRange([openButton, folderButton, cancelButton, allDownloadsButton]);

        root.Controls.Add(headerRow, 0, 0);
        root.Controls.Add(fileNameLabel, 0, 1);
        root.Controls.Add(detailLabel, 0, 2);
        root.Controls.Add(progressTrack, 0, 3);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);

        NativeUiTheme.Apply(this, openButton);
        ApplyToastBackdropColors(root, headerRow, actions);
        StyleDismissButton();

        dismissButton.Click += (_, _) => Dismiss();
        openButton.Click += (_, _) => InvokeTokenAction(DownloadPopupAction.Open);
        folderButton.Click += (_, _) => InvokeTokenAction(DownloadPopupAction.ShowInFolder);
        cancelButton.Click += (_, _) => InvokeTokenAction(DownloadPopupAction.Cancel);
        allDownloadsButton.Click += (_, _) =>
        {
            Dismiss();
            openDownloadsList();
        };
        autoHideTimer.Tick += (_, _) =>
        {
            if (IsDisposed || !Visible)
            {
                autoHideTimer.Stop();
                return;
            }
            if (Bounds.Contains(Cursor.Position))
            {
                autoHideTimer.Interval = HoverRecheckMs;
                return;
            }
            Dismiss();
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Dismiss();
            }
        };
        SystemColorsChanged += (_, _) =>
        {
            ApplyToastBackdropColors(root, headerRow, actions);
            StyleDismissButton();
            ApplySemanticColors();
        };
    }

    internal DownloadNotificationState? CurrentStateForTesting => currentState;

    internal bool AutoHideArmedForTesting => autoHideTimer.Enabled;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    public void ShowOrUpdate(IWin32Window owner, Control? anchor, DownloadNotificationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplyState(state);
        PositionNearAnchor(anchor);
        if (!Visible)
        {
            Show(owner);
        }
        UpdateAutoHideSchedule(state);
    }

    public void UpdateIfVisible(Control? anchor, DownloadNotificationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplyState(state);
        if (!Visible) return;
        PositionNearAnchor(anchor);
        UpdateAutoHideSchedule(state);
    }

    public void Reposition(Control? anchor)
    {
        if (!Visible) return;
        PositionNearAnchor(anchor);
    }

    public void Dismiss()
    {
        autoHideTimer.Stop();
        if (Visible) Hide();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Dismiss();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width <= 2 || ClientSize.Height <= 2) return;

        var g = e.Graphics;
        var highContrast = SystemInformation.HighContrast;
        var bounds = new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f);
        var dpi = Math.Max(96, DeviceDpi);
        var radius = Math.Clamp((float)(8f * (dpi / 96f)), 4f, 14f);
        using var outline = NativeUiTheme.CreateRoundedPath(bounds, radius);

        if (highContrast)
        {
            using var borderPen = new Pen(SystemColors.WindowText, 1f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawPath(borderPen, outline);
            return;
        }

        g.Clear(NativeUiTheme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new LinearGradientBrush(
            bounds,
            Color.FromArgb(255, 50, 22, 46),
            NativeUiTheme.Window,
            LinearGradientMode.Vertical))
        {
            g.FillPath(fill, outline);
        }

        var clipState = g.Save();
        try
        {
            g.SetClip(outline, CombineMode.Intersect);
            var sheenRect = new RectangleF(bounds.X, bounds.Y, bounds.Width, Math.Max(6f, 16f * (dpi / 96f)));
            using var sheen = new LinearGradientBrush(
                sheenRect,
                Color.FromArgb(36, 255, 240, 250),
                Color.FromArgb(0, NativeUiTheme.Window),
                LinearGradientMode.Vertical);
            g.FillRectangle(sheen, sheenRect);
        }
        finally
        {
            g.Restore(clipState);
        }

        using var rimBrush = new LinearGradientBrush(
            bounds,
            Color.FromArgb(190, 255, 224, 246),
            Color.FromArgb(125, NativeUiTheme.Border),
            LinearGradientMode.Vertical);
        using var rimPen = new Pen(rimBrush, 1f);
        g.DrawPath(rimPen, outline);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            autoHideTimer.Stop();
            autoHideTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ApplyState(DownloadNotificationState state)
    {
        currentState = state;
        statusLabel.Text = BuildHeaderText(state);
        fileNameLabel.Text = state.FileName;
        detailLabel.Text = string.IsNullOrWhiteSpace(state.DetailText)
            ? state.ProgressText
            : $"{state.ProgressText} \u00B7 {state.DetailText}";
        AccessibleDescription = $"{statusLabel.Text}: {state.FileName}. {detailLabel.Text}";

        openButton.Visible = state.CanOpen;
        openButton.Enabled = state.CanOpen;
        folderButton.Visible = state.CanShowInFolder;
        folderButton.Enabled = state.CanShowInFolder;
        cancelButton.Visible = state.CanCancel;
        cancelButton.Enabled = state.CanCancel;
        allDownloadsButton.Visible = true;
        allDownloadsButton.Enabled = true;

        ApplySemanticColors();
        progressTrack.Invalidate();
    }

    private void UpdateAutoHideSchedule(DownloadNotificationState state)
    {
        var isActive = state.ActiveCount > 0
            || state.State.Equals("Downloading", StringComparison.OrdinalIgnoreCase);
        autoHideTimer.Stop();
        if (!isActive)
        {
            autoHideTimer.Interval = CompletionAutoHideMs;
            autoHideTimer.Start();
        }
    }

    private void PositionNearAnchor(Control? anchor)
    {
        if (anchor is null || !anchor.IsHandleCreated) return;
        var bounds = anchor.RectangleToScreen(anchor.ClientRectangle);
        Location = NativeDialogLayout.ClampToWorkingArea(
            this,
            new Point(bounds.Right - Width, bounds.Bottom + 6));
    }

    private void InvokeTokenAction(DownloadPopupAction requestedAction)
    {
        var state = currentState;
        if (state is null) return;
        if (requestedAction != DownloadPopupAction.Cancel)
        {
            Dismiss();
        }
        action(requestedAction, state.Token);
    }

    private void OnProgressTrackPaint(object? sender, PaintEventArgs e)
    {
        var width = progressTrack.ClientSize.Width;
        var height = progressTrack.ClientSize.Height;
        if (width <= 2 || height <= 2) return;

        var g = e.Graphics;
        var highContrast = SystemInformation.HighContrast;
        g.Clear(highContrast ? SystemColors.Window : NativeUiTheme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var trackRect = new RectangleF(0.5f, 0.5f, width - 1f, height - 1f);
        var radius = Math.Max(1.5f, trackRect.Height / 2f);
        using var trackPath = NativeUiTheme.CreateRoundedPath(trackRect, radius);
        using (var trackBrush = new SolidBrush(highContrast ? SystemColors.ControlDark : NativeUiTheme.Surface))
        {
            g.FillPath(trackBrush, trackPath);
        }

        var state = currentState;
        if (state is not null)
        {
            var fillColor = GetStatusColor(state.State, highContrast);
            var fraction = state.State.Equals("Complete", StringComparison.OrdinalIgnoreCase)
                ? 1f
                : state.Percent is { } pct
                    ? Math.Clamp(pct / 100f, 0.04f, 1f)
                    : state.State.Equals("Downloading", StringComparison.OrdinalIgnoreCase)
                        ? 0.35f
                        : 1f;
            var fillWidth = Math.Max(trackRect.Height, trackRect.Width * fraction);
            var fillRect = new RectangleF(trackRect.X, trackRect.Y, Math.Min(trackRect.Width, fillWidth), trackRect.Height);
            var clipState = g.Save();
            try
            {
                g.SetClip(trackPath, CombineMode.Intersect);
                using var fillPath = NativeUiTheme.CreateRoundedPath(fillRect, Math.Min(radius, fillRect.Width / 2f));
                using var fillBrush = new SolidBrush(fillColor);
                g.FillPath(fillBrush, fillPath);
            }
            finally
            {
                g.Restore(clipState);
            }
        }

        using var rimPen = new Pen(
            highContrast ? SystemColors.WindowText : Color.FromArgb(90, NativeUiTheme.Border),
            1f);
        g.DrawPath(rimPen, trackPath);
    }

    private static void ApplyToastBackdropColors(params Control[] containers)
    {
        var backColor = SystemInformation.HighContrast ? SystemColors.Window : Color.Transparent;
        foreach (var container in containers)
        {
            container.BackColor = backColor;
        }
    }

    private void ApplySemanticColors()
    {
        var highContrast = SystemInformation.HighContrast;
        statusLabel.ForeColor = currentState is null
            ? (highContrast ? SystemColors.WindowText : NativeUiTheme.Text)
            : GetStatusColor(currentState.State, highContrast);
        fileNameLabel.ForeColor = highContrast ? SystemColors.WindowText : NativeUiTheme.Text;
        detailLabel.ForeColor = NativeUiTheme.MutedText;
    }

    private void StyleDismissButton()
    {
        if (SystemInformation.HighContrast)
        {
            dismissButton.FlatStyle = FlatStyle.System;
            dismissButton.BackColor = SystemColors.Control;
            dismissButton.ForeColor = SystemColors.ControlText;
            return;
        }
        dismissButton.FlatStyle = FlatStyle.Flat;
        dismissButton.BackColor = Color.Transparent;
        dismissButton.ForeColor = NativeUiTheme.Muted;
        dismissButton.FlatAppearance.BorderSize = 0;
        dismissButton.FlatAppearance.MouseOverBackColor = NativeUiTheme.Hover;
        dismissButton.FlatAppearance.MouseDownBackColor = NativeUiTheme.Pressed;
    }

    private static string BuildHeaderText(DownloadNotificationState state)
    {
        if (state.State.Equals("Downloading", StringComparison.OrdinalIgnoreCase))
        {
            return state.ActiveCount > 1
                ? $"Downloading ({state.ActiveCount} files)"
                : "Downloading\u2026";
        }
        if (state.State.Equals("Complete", StringComparison.OrdinalIgnoreCase))
        {
            return state.ActiveCount > 0
                ? $"Download complete ({state.ActiveCount} still downloading)"
                : "Download complete";
        }
        if (state.State.Equals("Paused", StringComparison.OrdinalIgnoreCase))
        {
            return "Download paused";
        }
        return "Download interrupted";
    }

    private static Color GetStatusColor(string state, bool highContrast)
    {
        if (highContrast) return SystemColors.WindowText;
        if (state.Equals("Complete", StringComparison.OrdinalIgnoreCase)) return NativeUiTheme.Positive;
        if (state.Equals("Interrupted", StringComparison.OrdinalIgnoreCase)) return NativeUiTheme.Negative;
        if (state.Equals("Paused", StringComparison.OrdinalIgnoreCase)) return NativeUiTheme.SecondaryText;
        return NativeUiTheme.Accent;
    }

    private static void ConfigureCompactButton(Button button, string text, string description)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(64, 28);
        button.Padding = new Padding(8, 0, 8, 0);
        button.Margin = new Padding(6, 4, 0, 4);
        button.Text = text;
        button.AccessibleName = text;
        button.AccessibleDescription = description;
    }
}

internal sealed class NativeAlertDialog : Form
{
    private static readonly Font MessageFont = new("Segoe UI", 9.5f);
    private static readonly Font BadgeFont = new("Segoe UI Semibold", 11.5f);

    private NativeAlertDialog(
        string title,
        string message,
        bool isConfirmation,
        string primaryText,
        string? secondaryText,
        bool hasOwner)
    {
        NativeDialogLayout.EnableDpiScaling(this);
        Text = title;
        StartPosition = hasOwner ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(380, 150);
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = title;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 2
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, NativeDialogLayout.ActionRowHeight));

        var badge = new Panel
        {
            Size = new Size(32, 32),
            Margin = new Padding(0, 2, 10, 0),
            TabStop = false
        };
        badge.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var highContrast = SystemInformation.HighContrast;
            var accent = isConfirmation ? NativeUiTheme.BrandCoral : NativeUiTheme.Accent;
            var rect = new RectangleF(1f, 1f, badge.Width - 2f, badge.Height - 2f);
            using var bgBrush = new SolidBrush(highContrast ? SystemColors.Highlight : Color.FromArgb(42, accent));
            using var rimPen = new Pen(highContrast ? SystemColors.WindowText : accent, 1.25f);
            g.FillEllipse(bgBrush, rect);
            g.DrawEllipse(rimPen, rect);
            TextRenderer.DrawText(
                g,
                isConfirmation ? "!" : "i",
                BadgeFont,
                badge.ClientRectangle,
                highContrast ? SystemColors.HighlightText : accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        var messageLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(0, 4, 0, 14),
            Font = MessageFont,
            Text = message,
            AccessibleName = message
        };

        var primaryButton = new Button { DialogResult = DialogResult.OK };
        NativeDialogLayout.ConfigureButton(primaryButton, primaryText, primaryText);

        var actions = NativeDialogLayout.CreateActionBar();
        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            var cancelButton = new Button { DialogResult = DialogResult.Cancel };
            NativeDialogLayout.ConfigureButton(cancelButton, secondaryText, secondaryText);
            actions.Controls.AddRange([cancelButton, primaryButton]);
            CancelButton = cancelButton;
        }
        else
        {
            actions.Controls.Add(primaryButton);
            CancelButton = primaryButton;
        }

        root.Controls.Add(badge, 0, 0);
        root.Controls.Add(messageLabel, 1, 0);
        root.Controls.Add(actions, 0, 1);
        root.SetColumnSpan(actions, 2);

        Controls.Add(root);
        NativeUiTheme.Apply(this, primaryButton);
        badge.BackColor = SystemInformation.HighContrast ? SystemColors.Window : NativeUiTheme.Window;
        AcceptButton = primaryButton;
    }

    internal static void ShowInfo(IWin32Window? owner, string title, string message, string okText = "OK")
    {
        using var dialog = new NativeAlertDialog(title, message, isConfirmation: false, okText, secondaryText: null, hasOwner: owner is not null);
        _ = dialog.ShowDialog(owner);
    }

    internal static bool Confirm(
        IWin32Window? owner,
        string title,
        string message,
        string confirmText = "Continue",
        string cancelText = "Cancel")
    {
        using var dialog = new NativeAlertDialog(title, message, isConfirmation: true, confirmText, cancelText, hasOwner: owner is not null);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}

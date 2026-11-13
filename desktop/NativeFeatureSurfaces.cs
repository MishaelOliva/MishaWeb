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
        resultList.HorizontalScrollbar = true;
        resultList.Font = NativeDialogLayout.ListFont;
        resultList.AccessibleName = "Command palette results";
        root.Controls.Add(queryBox, 0, 0);
        root.Controls.Add(resultList, 0, 1);
        Controls.Add(root);
        NativeUiTheme.Apply(this);

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
        ClientSize = new Size(420, 250);
        MinimumSize = new Size(360, 220);
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Recent tabs";

        itemList.Dock = DockStyle.Fill;
        itemList.IntegralHeight = false;
        itemList.Font = NativeDialogLayout.ListFont;
        itemList.AccessibleName = "Recent tabs list";
        Controls.Add(itemList);
        NativeUiTheme.Apply(this);
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
        downloadList.HorizontalScrollbar = true;
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

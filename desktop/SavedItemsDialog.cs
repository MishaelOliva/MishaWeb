namespace MishaWeb;

internal enum SavedItemsView
{
    Favorites,
    History,
    QuickLinks,
    RecentlyClosed
}

internal enum SavedItemActionKind
{
    Open,
    Rename,
    Remove,
    TogglePin,
    MoveUp,
    MoveDown
}

internal sealed record SavedItemRow(
    string Title,
    string Url,
    bool IsPinned,
    string Detail,
    object? Token = null);

internal sealed record SavedItemAction(
    SavedItemActionKind Kind,
    SavedItemsView View,
    SavedItemRow Item,
    string? NewTitle = null);

/// <summary>
/// A small local-only saved-items manager. It intentionally uses text rows and
/// never creates a WebView or asks a site for a favicon.
/// </summary>
internal sealed class SavedItemsDialog : Form
{
    private const int MaximumDisplayTitleCharacters = 256;
    private const int MaximumDisplayDetailCharacters = 160;
    private const int MaximumDisplayUrlCharacters = 512;
    private const int MaximumDisplayRowCharacters = 960;
    private static readonly Font ListFont = new("Segoe UI", 9.5f);

    private readonly Func<SavedItemsView, IReadOnlyList<SavedItemRow>> itemProvider;
    private readonly ComboBox viewPicker = new();
    private readonly TextBox filterBox = new();
    private readonly ListBox itemList = new();
    private readonly Label emptyLabel = new();
    private readonly Button openButton = new();
    private readonly Button renameButton = new();
    private readonly Button removeButton = new();
    private readonly Button pinButton = new();
    private readonly Button moveUpButton = new();
    private readonly Button moveDownButton = new();
    private readonly Button closeButton = new();
    private IReadOnlyList<SavedItemRow> visibleItems = [];

    public SavedItemsDialog(Func<SavedItemsView, IReadOnlyList<SavedItemRow>> itemProvider)
    {
        this.itemProvider = itemProvider ?? throw new ArgumentNullException(nameof(itemProvider));
        NativeDialogLayout.EnableDpiScaling(this);
        Text = "Saved items";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimumSize = new Size(560, 420);
        Size = new Size(720, 520);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Saved items";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 4
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, NativeDialogLayout.WrappedActionRowHeight));

        var filterLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        viewPicker.Dock = DockStyle.Fill;
        viewPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        viewPicker.Items.AddRange(["Favorites", "History", "Quick links", "Recently closed"]);
        viewPicker.SelectedIndex = 0;
        viewPicker.AccessibleName = "Saved items view";
        filterBox.Dock = DockStyle.Fill;
        filterBox.Margin = new Padding(8, 0, 0, 0);
        filterBox.PlaceholderText = "Filter titles and addresses";
        filterBox.AccessibleName = "Filter saved items";
        filterLayout.Controls.Add(viewPicker, 0, 0);
        filterLayout.Controls.Add(filterBox, 1, 0);
        root.Controls.Add(filterLayout, 0, 0);
        root.SetColumnSpan(filterLayout, 2);

        itemList.Dock = DockStyle.Fill;
        itemList.IntegralHeight = false;
        itemList.Font = ListFont;
        itemList.AccessibleName = "Saved items list";
        itemList.SelectedIndexChanged += (_, _) => UpdateButtons();
        itemList.DoubleClick += (_, _) => RaiseAction(SavedItemActionKind.Open);
        itemList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) RaiseAction(SavedItemActionKind.Open);
            else if (e.KeyCode == Keys.Delete) RaiseAction(SavedItemActionKind.Remove);
        };
        root.Controls.Add(itemList, 0, 1);
        root.SetColumnSpan(itemList, 2);

        emptyLabel.Dock = DockStyle.Fill;
        emptyLabel.TextAlign = ContentAlignment.MiddleLeft;
        emptyLabel.ForeColor = NativeUiTheme.MutedText;
        emptyLabel.Text = "No saved items match this filter.";
        emptyLabel.Visible = false;
        emptyLabel.AccessibleRole = AccessibleRole.StatusBar;
        root.Controls.Add(emptyLabel, 0, 2);
        root.SetColumnSpan(emptyLabel, 2);

        var actions = NativeDialogLayout.CreateActionBar(wrap: true);
        ConfigureButton(openButton, "Open", "Open the selected saved item");
        ConfigureButton(renameButton, "Rename", "Rename the selected saved item");
        ConfigureButton(removeButton, "Remove", "Remove the selected saved item");
        ConfigureButton(pinButton, "Pin", "Pin or unpin the selected item");
        ConfigureButton(moveDownButton, "Down", "Move the selected quick link down");
        ConfigureButton(moveUpButton, "Up", "Move the selected quick link up");
        ConfigureButton(closeButton, "Close", "Close saved items");
        actions.Controls.Add(closeButton);
        actions.Controls.Add(openButton);
        actions.Controls.Add(renameButton);
        actions.Controls.Add(removeButton);
        actions.Controls.Add(pinButton);
        actions.Controls.Add(moveDownButton);
        actions.Controls.Add(moveUpButton);
        root.Controls.Add(actions, 0, 3);
        root.SetColumnSpan(actions, 2);
        Controls.Add(root);
        NativeUiTheme.Apply(this, openButton);
        NativeUiTheme.ConfigureModernListBox(
            itemList,
            50,
            (index, _) => BuildRowVisual(index));
        emptyLabel.ForeColor = NativeUiTheme.MutedText;
        SystemColorsChanged += (_, _) => emptyLabel.ForeColor = NativeUiTheme.MutedText;

        viewPicker.SelectedIndexChanged += (_, _) => RefreshItems();
        filterBox.TextChanged += (_, _) => RefreshItems();
        openButton.Click += (_, _) => RaiseAction(SavedItemActionKind.Open);
        renameButton.Click += (_, _) => RenameSelected();
        removeButton.Click += (_, _) => RaiseAction(SavedItemActionKind.Remove);
        pinButton.Click += (_, _) => RaiseAction(SavedItemActionKind.TogglePin);
        moveUpButton.Click += (_, _) => RaiseAction(SavedItemActionKind.MoveUp);
        moveDownButton.Click += (_, _) => RaiseAction(SavedItemActionKind.MoveDown);
        closeButton.Click += (_, _) => Close();
        AcceptButton = openButton;
        CancelButton = closeButton;
        RefreshItems();
    }

    public event EventHandler<SavedItemAction>? ActionRequested;

    public SavedItemsView SelectedView => (SavedItemsView)Math.Clamp(viewPicker.SelectedIndex, 0, 3);

    public void RefreshItems()
    {
        var query = filterBox.Text.Trim();
        visibleItems = itemProvider(SelectedView)
            .Where(item => query.Length == 0
                || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Url.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        itemList.BeginUpdate();
        try
        {
            itemList.Items.Clear();
            foreach (var item in visibleItems)
            {
                itemList.Items.Add(FormatRowForDisplay(item));
            }
        }
        finally
        {
            itemList.EndUpdate();
        }

        emptyLabel.Visible = visibleItems.Count == 0;
        itemList.Visible = visibleItems.Count > 0;
        if (visibleItems.Count > 0) itemList.SelectedIndex = 0;
        UpdateButtons();
    }

    internal static string FormatRowForDisplay(SavedItemRow item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var title = TextSafety.SanitizeSingleLine(item.Title, MaximumDisplayTitleCharacters);
        var detail = TextSafety.SanitizeSingleLine(item.Detail, MaximumDisplayDetailCharacters);
        var displayUrl = TextSafety.FormatUrlForDisplay(item.Url, MaximumDisplayUrlCharacters);
        var pin = item.IsPinned ? " \u00B7 pinned" : string.Empty;
        return TextSafety.TruncateWithEllipsis(
            $"{title} \u2014 {detail}{pin} \u2014 {displayUrl}",
            MaximumDisplayRowCharacters);
    }

    private ModernListRowVisual BuildRowVisual(int index)
    {
        if (index < 0 || index >= visibleItems.Count)
        {
            return new ModernListRowVisual(string.Empty, string.Empty, string.Empty);
        }

        var item = visibleItems[index];
        var title = TextSafety.SanitizeSingleLine(item.Title, MaximumDisplayTitleCharacters);
        var detail = TextSafety.SanitizeSingleLine(item.Detail, MaximumDisplayDetailCharacters);
        var displayUrl = TextSafety.FormatUrlForDisplay(item.Url, MaximumDisplayUrlCharacters);
        var combinedDetail = string.IsNullOrWhiteSpace(detail)
            ? displayUrl
            : $"{detail} \u00B7 {displayUrl}";
        var (glyph, accent) = SelectedView switch
        {
            SavedItemsView.Favorites => ("*", NativeUiTheme.BrandCoral),
            SavedItemsView.History => ("H", NativeUiTheme.Lavender),
            SavedItemsView.QuickLinks => ("Q", NativeUiTheme.Accent),
            _ => ("R", NativeUiTheme.Focus)
        };
        return new ModernListRowVisual(
            title,
            combinedDetail,
            glyph,
            accent,
            StatusText: item.IsPinned ? "Pinned" : null,
            StatusColor: NativeUiTheme.Accent);
    }

    private void ConfigureButton(Button button, string text, string description)
    {
        NativeDialogLayout.ConfigureButton(button, text, description);
    }

    private void RenameSelected()
    {
        if (!TryGetSelected(out var item)) return;
        if (!TextPromptDialog.TryShow(this, "Rename saved item", "New title:", item.Title, out var title)) return;
        RaiseAction(SavedItemActionKind.Rename, title);
    }

    private void RaiseAction(SavedItemActionKind kind, string? newTitle = null)
    {
        if (!TryGetSelected(out var item)) return;
        ActionRequested?.Invoke(this, new SavedItemAction(kind, SelectedView, item, newTitle));
    }

    private bool TryGetSelected(out SavedItemRow item)
    {
        if (itemList.SelectedIndex >= 0 && itemList.SelectedIndex < visibleItems.Count)
        {
            item = visibleItems[itemList.SelectedIndex];
            return true;
        }

        item = null!;
        return false;
    }

    private void UpdateButtons()
    {
        var hasSelection = itemList.SelectedIndex >= 0 && itemList.SelectedIndex < visibleItems.Count;
        openButton.Enabled = hasSelection;
        renameButton.Enabled = hasSelection
            && SelectedView is not SavedItemsView.QuickLinks
            and not SavedItemsView.RecentlyClosed;
        removeButton.Enabled = hasSelection;
        pinButton.Enabled = hasSelection
            && SelectedView is not SavedItemsView.RecentlyClosed;
        var canReorderQuickLinks = hasSelection
            && filterBox.TextLength == 0
            && SelectedView == SavedItemsView.QuickLinks;
        moveUpButton.Enabled = canReorderQuickLinks && itemList.SelectedIndex > 0;
        moveDownButton.Enabled = canReorderQuickLinks && itemList.SelectedIndex < visibleItems.Count - 1;
        pinButton.Text = hasSelection && visibleItems[itemList.SelectedIndex].IsPinned ? "Unpin" : "Pin";
        pinButton.AccessibleName = pinButton.Text;
    }
}

internal static class TextPromptDialog
{
    public static bool TryShow(
        IWin32Window owner,
        string title,
        string prompt,
        string initialValue,
        out string value)
    {
        using var dialog = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 150),
            MinimumSize = new Size(360, 180),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            AccessibleRole = AccessibleRole.Dialog,
            AccessibleName = title
        };
        NativeDialogLayout.EnableDpiScaling(dialog);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, NativeDialogLayout.ActionRowHeight));

        var label = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = prompt,
            AccessibleName = prompt.TrimEnd(':')
        };
        var input = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = initialValue,
            AccessibleName = prompt.TrimEnd(':')
        };
        var okay = new Button { DialogResult = DialogResult.OK };
        var cancel = new Button { DialogResult = DialogResult.Cancel };
        NativeDialogLayout.ConfigureButton(okay, "OK", "Confirm the new title");
        NativeDialogLayout.ConfigureButton(cancel, "Cancel", "Keep the current title");
        var actions = NativeDialogLayout.CreateActionBar();
        actions.Controls.AddRange([cancel, okay]);
        root.Controls.Add(label, 0, 0);
        root.Controls.Add(input, 0, 1);
        root.Controls.Add(actions, 0, 3);
        dialog.Controls.Add(root);
        NativeUiTheme.Apply(dialog, okay);
        dialog.AcceptButton = okay;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text))
        {
            value = input.Text.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }
}

using System.Drawing.Drawing2D;

namespace MishaWeb;

internal enum ExtensionManagerAction
{
    Open,
    OpenPopup,
    OpenOptions,
    ToggleEnabled,
    Remove
}

internal sealed record ExtensionManagerRow(
    string Id,
    string Name,
    string Version,
    bool IsEnabled,
    string? LaunchPath,
    bool CanUpdate,
    object Token,
    string? PopupPath = null,
    string? OptionsPath = null,
    string? Description = null,
    string? IconPath = null,
    string? StoreId = null,
    string? FolderPath = null,
    IReadOnlyList<string>? Capabilities = null);

internal sealed class ExtensionsManagerForm : Form
{
    private static readonly Font HeaderTitleFont = new("Segoe UI", 12f, FontStyle.Bold);
    private static readonly Font BodyFont = new("Segoe UI", 9f);
    private static readonly Font EmptyLabelFont = new("Segoe UI", 10.5f);
    private static readonly Font CardNameFont = new("Segoe UI", 10.5f, FontStyle.Bold);
    private static readonly Font CardBadgeFont = new("Segoe UI", 8.25f);
    private static readonly Font CardStatusFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    private static readonly Font CardDetailFont = new("Segoe UI", 8.5f);
    private static readonly Font CardIdFont = new("Consolas", 8.25f);
    private static readonly Font FallbackIconFont = new("Segoe UI", 12f, FontStyle.Bold);

    private readonly Func<Task<IReadOnlyList<ExtensionManagerRow>>> rowProvider;
    private readonly Func<string, CancellationToken, Task> installFromStore;
    private readonly Func<string, CancellationToken, Task> importPackage;
    private readonly Func<string, CancellationToken, Task> importUnpacked;
    private readonly Func<ExtensionManagerRow, CancellationToken, Task<string>> updateFromStore;
    private readonly Func<ExtensionManagerAction, ExtensionManagerRow, Task> rowAction;
    private readonly Action openManagedFolder;
    private readonly Action openUserScriptsFolder;

    private readonly TextBox searchBox = new();
    private readonly TextBox storeAddressBox = new();
    private readonly Button installButton = new();
    private readonly Button packageButton = new();
    private readonly Button unpackedButton = new();
    private readonly Button managedFolderButton = new();
    private readonly Button userScriptsButton = new();
    private readonly Button refreshButton = new();
    private readonly Button closeButton = new();
    private readonly Label statusLabel = new();
    private readonly Label emptyLabel = new();
    private readonly Panel cardsContainer = new();
    private readonly TableLayoutPanel cardsStack = new();

    private readonly List<ExtensionCard> loadedCards = [];
    private IReadOnlyList<ExtensionManagerRow> currentRows = [];
    private CancellationTokenSource? operationCancellation;
    private bool busy;
    private bool closeWhenIdle;

    public ExtensionsManagerForm(
        Func<Task<IReadOnlyList<ExtensionManagerRow>>> rowProvider,
        Func<string, CancellationToken, Task> installFromStore,
        Func<string, CancellationToken, Task> importPackage,
        Func<string, CancellationToken, Task> importUnpacked,
        Func<ExtensionManagerRow, CancellationToken, Task<string>> updateFromStore,
        Func<ExtensionManagerAction, ExtensionManagerRow, Task> rowAction,
        Action openManagedFolder,
        Action openUserScriptsFolder)
    {
        this.rowProvider = rowProvider;
        this.installFromStore = installFromStore;
        this.importPackage = importPackage;
        this.importUnpacked = importUnpacked;
        this.updateFromStore = updateFromStore;
        this.rowAction = rowAction;
        this.openManagedFolder = openManagedFolder;
        this.openUserScriptsFolder = openUserScriptsFolder;

        NativeDialogLayout.EnableDpiScaling(this);
        Text = "Extensions";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(840, 580);
        MinimumSize = new Size(660, 460);
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Browser extensions manager";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 1. Notice & Title
        var headerLayout = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8)
        };
        var titleLabel = new Label
        {
            AutoSize = true,
            Font = HeaderTitleFont,
            ForeColor = NativeUiTheme.Text,
            Text = "Extensions"
        };
        var explanation = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(800, 0),
            Margin = new Padding(0, 2, 0, 0),
            ForeColor = NativeUiTheme.SecondaryText,
            Font = BodyFont,
            Text = "Install from Chrome Web Store by URL or extension ID. "
                + "Extensions can read and change webpages, so install only ones you trust."
        };
        explanation.AccessibleName = "Extension safety notice";
        headerLayout.Controls.Add(titleLabel, 0, 0);
        headerLayout.Controls.Add(explanation, 0, 1);
        root.Controls.Add(headerLayout, 0, 0);

        // 2. Search & Install Bar
        var storePanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 3,
            Margin = new Padding(0, 0, 0, 8)
        };
        storePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));
        storePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
        storePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        searchBox.Dock = DockStyle.Fill;
        searchBox.PlaceholderText = "Search installed extensions…";
        searchBox.AccessibleName = "Search installed extensions";
        searchBox.Margin = new Padding(0, 0, 8, 0);

        storeAddressBox.Dock = DockStyle.Fill;
        storeAddressBox.PlaceholderText = "Chrome Web Store URL or 32-character ID";
        storeAddressBox.AccessibleName = "Chrome Web Store extension address";
        storeAddressBox.Margin = new Padding(0, 0, 6, 0);

        ConfigureButton(installButton, "Install", "Download and install the Chrome Web Store extension");
        installButton.Margin = Padding.Empty;

        storePanel.Controls.Add(searchBox, 0, 0);
        storePanel.Controls.Add(storeAddressBox, 1, 0);
        storePanel.Controls.Add(installButton, 2, 0);
        root.Controls.Add(storePanel, 0, 1);

        // 3. Secondary Actions Toolbar
        var importActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        ConfigureButton(packageButton, "Install package…", "Install a local CRX or ZIP extension package");
        ConfigureButton(unpackedButton, "Load unpacked…", "Copy and install an unpacked extension folder");
        ConfigureButton(managedFolderButton, "Managed files", "Open MishaWeb's managed extension package folder");
        ConfigureButton(userScriptsButton, "User scripts", "Open the separate JavaScript user scripts folder");
        ConfigureButton(refreshButton, "Refresh", "Refresh the installed extensions list");
        importActions.Controls.AddRange([packageButton, unpackedButton, managedFolderButton, userScriptsButton, refreshButton]);
        root.Controls.Add(importActions, 0, 2);

        // 4. Cards container
        cardsContainer.Dock = DockStyle.Fill;
        cardsContainer.AutoScroll = true;
        cardsContainer.BackColor = NativeUiTheme.Window;

        cardsStack.Dock = DockStyle.Top;
        cardsStack.AutoSize = true;
        cardsStack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        cardsStack.ColumnCount = 1;
        cardsStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        cardsStack.BackColor = NativeUiTheme.Window;
        cardsStack.Margin = Padding.Empty;
        cardsStack.Padding = new Padding(0, 0, 6, 0);

        emptyLabel.Dock = DockStyle.Fill;
        emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        emptyLabel.Font = EmptyLabelFont;
        emptyLabel.ForeColor = NativeUiTheme.MutedText;
        emptyLabel.Text = "No browser extensions are installed";
        emptyLabel.Visible = false;

        cardsContainer.Controls.Add(cardsStack);
        cardsContainer.Controls.Add(emptyLabel);
        root.Controls.Add(cardsContainer, 0, 3);

        // 5. Bottom status & close bar
        var bottomBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 8, 0, 0)
        };
        bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        statusLabel.AutoEllipsis = true;
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.ForeColor = NativeUiTheme.SecondaryText;
        statusLabel.Text = "Loading installed extensions…";
        statusLabel.AccessibleName = "Extension manager status";

        ConfigureButton(closeButton, "Close", "Close the extensions manager");
        CancelButton = closeButton;

        bottomBar.Controls.Add(statusLabel, 0, 0);
        bottomBar.Controls.Add(closeButton, 1, 0);
        root.Controls.Add(bottomBar, 0, 4);

        Controls.Add(root);
        NativeUiTheme.Apply(this, installButton);
        NativeUiTheme.ApplyDarkScrollbars(cardsContainer);
        ApplyThemeAccents();
        SystemColorsChanged += (_, _) => ApplyThemeAccents();

        // Event wiring
        installButton.Click += async (_, _) => await InstallStoreExtensionAsync();
        storeAddressBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await InstallStoreExtensionAsync();
        };
        searchBox.TextChanged += (_, _) => FilterCards();
        packageButton.Click += async (_, _) => await SelectPackageAsync();
        unpackedButton.Click += async (_, _) => await SelectUnpackedAsync();
        managedFolderButton.Click += (_, _) => openManagedFolder();
        userScriptsButton.Click += (_, _) => openUserScriptsFolder();
        refreshButton.Click += async (_, _) => await RefreshRowsAsync();
        closeButton.Click += (_, _) => Close();

        FormClosing += (_, e) =>
        {
            if (!busy || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            closeWhenIdle = true;
            operationCancellation?.Cancel();
            SetStatus("Finishing the current extension operation before closing…");
        };
        Shown += async (_, _) => await RefreshRowsAsync();
        UpdateControlsState();
    }

    public void PrefillStoreAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(storeAddressBox.Text)
            && !string.IsNullOrWhiteSpace(value))
        {
            storeAddressBox.Text = value;
        }
    }

    public Task RefreshAfterExternalChangeAsync() => RefreshRowsAsync();

    private void FilterCards()
    {
        var filter = searchBox.Text.Trim();
        var visibleCount = 0;
        cardsStack.SuspendLayout();
        try
        {
            foreach (var card in loadedCards)
            {
                var matches = string.IsNullOrEmpty(filter)
                    || card.Row.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                    || card.Row.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (card.Row.Description is not null && card.Row.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
                card.Visible = matches;
                if (matches) visibleCount++;
            }
        }
        finally
        {
            cardsStack.ResumeLayout();
        }

        if (loadedCards.Count == 0)
        {
            emptyLabel.Text = "No browser extensions are installed";
            emptyLabel.Visible = true;
        }
        else if (visibleCount == 0)
        {
            emptyLabel.Text = $"No extensions match \"{filter}\"";
            emptyLabel.Visible = true;
        }
        else
        {
            emptyLabel.Visible = false;
        }
    }

    private async Task InstallStoreExtensionAsync()
    {
        var value = storeAddressBox.Text.Trim();
        if (BrowserExtensions.ParseChromeWebStoreExtensionId(value) is null)
        {
            SetStatus("Paste a Chrome Web Store listing address or extension ID.");
            storeAddressBox.Focus();
            return;
        }
        await RunOperationAsync(
            "Downloading and installing extension…",
            token => installFromStore(value, token),
            "Extension installed");
    }

    private async Task SelectPackageAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Install extension package",
            Filter = "Chrome extension packages (*.crx;*.zip)|*.crx;*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        await RunOperationAsync(
            "Importing extension package…",
            token => importPackage(picker.FileName, token),
            "Extension package installed");
    }

    private async Task SelectUnpackedAsync()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Select the folder containing the extension's manifest.json",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        await RunOperationAsync(
            "Copying and installing unpacked extension…",
            token => importUnpacked(picker.SelectedPath, token),
            "Unpacked extension installed");
    }

    internal async Task RemoveRowAsync(ExtensionManagerRow row)
    {
        if (!NativeAlertDialog.Confirm(
            this,
            "Remove extension",
            $"Remove {row.Name}? Its MishaWeb-managed package files will also be deleted.",
            confirmText: "Remove",
            cancelText: "Cancel"))
        {
            return;
        }
        await RunRowActionCoreAsync(ExtensionManagerAction.Remove, row);
    }

    internal async Task UpdateRowAsync(ExtensionManagerRow row)
    {
        if (!row.CanUpdate || busy) return;
        var completed = "Extension update check completed";
        await RunOperationCoreAsync(
            $"Checking the Chrome Web Store for a signed update for {row.Name}…",
            async token => completed = await updateFromStore(row, token),
            () => completed);
    }

    internal async Task RunRowActionCoreAsync(ExtensionManagerAction action, ExtensionManagerRow row)
    {
        if (busy) return;
        var progress = action switch
        {
            ExtensionManagerAction.Open or ExtensionManagerAction.OpenPopup => $"Opening {row.Name} popup…",
            ExtensionManagerAction.OpenOptions => $"Opening {row.Name} options…",
            ExtensionManagerAction.ToggleEnabled => row.IsEnabled
                ? $"Disabling {row.Name}…"
                : $"Enabling {row.Name}…",
            ExtensionManagerAction.Remove => $"Removing {row.Name}…",
            _ => "Updating extension…"
        };
        var completed = action switch
        {
            ExtensionManagerAction.Open or ExtensionManagerAction.OpenPopup => $"{row.Name} opened",
            ExtensionManagerAction.OpenOptions => $"{row.Name} options opened",
            ExtensionManagerAction.ToggleEnabled => row.IsEnabled
                ? $"{row.Name} disabled"
                : $"{row.Name} enabled",
            ExtensionManagerAction.Remove => $"{row.Name} removed",
            _ => $"{row.Name} updated"
        };
        await RunOperationAsync(progress, _ => rowAction(action, row), completed);
    }

    private async Task RunOperationAsync(
        string progress,
        Func<CancellationToken, Task> operation,
        string completed) =>
        await RunOperationCoreAsync(progress, operation, () => completed);

    private async Task RunOperationCoreAsync(
        string progress,
        Func<CancellationToken, Task> operation,
        Func<string> completed)
    {
        if (busy) return;
        busy = true;
        operationCancellation = new CancellationTokenSource();
        SetStatus(progress);
        UpdateControlsState();
        try
        {
            var operationSucceeded = false;
            try
            {
                await operation(operationCancellation.Token);
                operationSucceeded = true;
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) SetStatus("Extension operation canceled");
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    SetStatus(error.Message);
                    NativeAlertDialog.ShowInfo(
                        this,
                        "Extension operation failed",
                        error.Message);
                }
            }

            if (operationSucceeded && !IsDisposed && !closeWhenIdle)
            {
                try
                {
                    await RefreshRowsCoreAsync();
                    if (!IsDisposed) SetStatus(completed());
                }
                catch (Exception error)
                {
                    if (!IsDisposed) SetStatus($"{completed()}; refresh failed: {error.Message}");
                }
            }
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            busy = false;
            if (!IsDisposed)
            {
                if (closeWhenIdle) Close();
                else UpdateControlsState();
            }
        }
    }

    private async Task RefreshRowsAsync()
    {
        if (busy) return;
        busy = true;
        SetStatus("Refreshing installed extensions…");
        UpdateControlsState();
        try
        {
            await RefreshRowsCoreAsync();
            if (IsDisposed) return;
            SetStatus(currentRows.Count == 0
                ? "No browser extensions are installed"
                : $"{currentRows.Count} extension(s) installed");
        }
        catch (Exception error)
        {
            if (!IsDisposed) SetStatus(error.Message);
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                if (closeWhenIdle) Close();
                else UpdateControlsState();
            }
        }
    }

    private async Task RefreshRowsCoreAsync()
    {
        var rows = await rowProvider();
        if (IsDisposed) return;
        currentRows = rows;
        cardsStack.SuspendLayout();
        try
        {
            foreach (var card in loadedCards)
            {
                cardsStack.Controls.Remove(card);
                card.Dispose();
            }
            loadedCards.Clear();

            var sortedRows = rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (var row in sortedRows)
            {
                var card = new ExtensionCard(row, this);
                loadedCards.Add(card);
                cardsStack.Controls.Add(card);
            }
        }
        finally
        {
            cardsStack.ResumeLayout();
        }
        FilterCards();
    }

    private void UpdateControlsState()
    {
        searchBox.Enabled = !busy;
        storeAddressBox.Enabled = !busy;
        installButton.Enabled = !busy;
        packageButton.Enabled = !busy;
        unpackedButton.Enabled = !busy;
        refreshButton.Enabled = !busy;
        managedFolderButton.Enabled = !busy;
        userScriptsButton.Enabled = !busy;
        foreach (var card in loadedCards)
        {
            card.UpdateBusyState(busy);
        }
    }

    private void SetStatus(string text)
    {
        statusLabel.Text = text;
        statusLabel.AccessibleName = text;
    }

    private void ApplyThemeAccents()
    {
        var highContrast = SystemInformation.HighContrast;
        cardsContainer.BackColor = highContrast ? SystemColors.Window : NativeUiTheme.Window;
        cardsStack.BackColor = highContrast ? SystemColors.Window : NativeUiTheme.Window;
        emptyLabel.ForeColor = NativeUiTheme.MutedText;
        statusLabel.ForeColor = highContrast ? SystemColors.WindowText : NativeUiTheme.SecondaryText;
    }

    private static void ConfigureButton(Button button, string text, string description)
    {
        NativeDialogLayout.ConfigureButton(button, text, description);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            operationCancellation?.Cancel();
            operationCancellation?.Dispose();
            foreach (var card in loadedCards) card.Dispose();
            loadedCards.Clear();
        }
        base.Dispose(disposing);
    }

    private sealed class ExtensionCard : Panel
    {
        public ExtensionManagerRow Row { get; private set; }
        private readonly ExtensionsManagerForm parentForm;
        private readonly Panel detailsPanel = new();
        private readonly Button detailsButton = new();
        private readonly Button toggleButton = new();
        private readonly Button? openPopupButton;
        private readonly Button? optionsButton;
        private readonly Button? updateButton;
        private readonly Button? removeButton;
        private readonly PillBadgeLabel versionBadge;
        private readonly PillBadgeLabel sourceBadge;
        private readonly PillBadgeLabel statusBadge;
        private Image? iconImage;

        public ExtensionCard(
            ExtensionManagerRow row,
            ExtensionsManagerForm parentForm)
        {
            Row = row;
            this.parentForm = parentForm;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0, 0, 0, 10);
            Padding = new Padding(12);
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : NativeUiTheme.Surface;
            ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : NativeUiTheme.Text;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            // Row 1: Top header with icon, title, version, badges
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 0, 0, 6),
                BackColor = Color.Transparent
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            if (row.IconPath is not null && File.Exists(row.IconPath))
            {
                iconImage = TryLoadUnlockedBitmap(row.IconPath);
            }

            var iconBox = new ExtensionIconBox(iconImage, row.Name);
            iconBox.Size = new Size(36, 36);
            iconBox.Margin = new Padding(0, 2, 8, 0);
            header.Controls.Add(iconBox, 0, 0);

            var titleFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = Padding.Empty,
                BackColor = Color.Transparent
            };

            var nameLabel = new Label
            {
                AutoSize = true,
                Font = CardNameFont,
                ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : NativeUiTheme.Text,
                BackColor = Color.Transparent,
                Text = row.Name,
                Margin = new Padding(0, 2, 8, 2)
            };

            versionBadge = new PillBadgeLabel(NativeUiTheme.Accent)
            {
                Font = CardBadgeFont,
                ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : NativeUiTheme.Accent,
                Text = $"v{row.Version}"
            };

            var sourceText = row.StoreId is not null
                ? "Chrome Web Store"
                : row.FolderPath is not null ? "Local / Unpacked" : "System";
            var sourceTint = row.StoreId is not null ? NativeUiTheme.Lavender : NativeUiTheme.Muted;
            sourceBadge = new PillBadgeLabel(sourceTint)
            {
                Font = CardBadgeFont,
                ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : NativeUiTheme.SecondaryText,
                Text = sourceText
            };

            var statusTint = row.IsEnabled ? NativeUiTheme.Positive : NativeUiTheme.Muted;
            statusBadge = new PillBadgeLabel(statusTint)
            {
                Font = CardStatusFont,
                ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : statusTint,
                Text = row.IsEnabled ? "Enabled" : "Disabled"
            };

            titleFlow.Controls.AddRange([nameLabel, versionBadge, sourceBadge, statusBadge]);
            header.Controls.Add(titleFlow, 1, 0);
            layout.Controls.Add(header, 0, 0);

            // Row 2: Description & ID
            var descText = string.IsNullOrWhiteSpace(row.Description)
                ? "No description declared in extension manifest."
                : row.Description;
            var descLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Font = BodyFont,
                ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : NativeUiTheme.SecondaryText,
                BackColor = Color.Transparent,
                Text = descText,
                Margin = new Padding(0, 0, 0, 4)
            };
            layout.Controls.Add(descLabel, 0, 1);

            var idLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Font = CardIdFont,
                ForeColor = NativeUiTheme.MutedText,
                BackColor = Color.Transparent,
                Text = $"ID: {row.Id}",
                Margin = new Padding(0, 0, 0, 8)
            };
            layout.Controls.Add(idLabel, 0, 2);

            // Row 3: Action Buttons
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = Padding.Empty,
                BackColor = Color.Transparent
            };

            var hasPopup = row.PopupPath is not null || row.LaunchPath is not null;
            if (hasPopup)
            {
                openPopupButton = new Button();
                ConfigureButton(openPopupButton, "Open popup", "Open the interactive extension popup page");
                openPopupButton.Click += async (_, _) => await parentForm.RunRowActionCoreAsync(ExtensionManagerAction.OpenPopup, row);
                actions.Controls.Add(openPopupButton);
            }

            var hasOptions = row.OptionsPath is not null;
            if (hasOptions)
            {
                optionsButton = new Button();
                ConfigureButton(optionsButton, "Options", "Open extension settings and options page");
                optionsButton.Click += async (_, _) => await parentForm.RunRowActionCoreAsync(ExtensionManagerAction.OpenOptions, row);
                actions.Controls.Add(optionsButton);
            }

            ConfigureButton(toggleButton, row.IsEnabled ? "Disable" : "Enable", "Enable or disable this extension");
            toggleButton.Click += async (_, _) => await parentForm.RunRowActionCoreAsync(ExtensionManagerAction.ToggleEnabled, row);
            actions.Controls.Add(toggleButton);

            if (row.CanUpdate)
            {
                updateButton = new Button();
                ConfigureButton(updateButton, "Check update", "Check Chrome Web Store for a signed update");
                updateButton.Click += async (_, _) => await parentForm.UpdateRowAsync(row);
                actions.Controls.Add(updateButton);
            }

            var canRemove = row.StoreId is not null || row.FolderPath is not null;
            if (canRemove)
            {
                removeButton = new Button();
                ConfigureButton(removeButton, "Remove", "Uninstall this extension");
                removeButton.Click += async (_, _) => await parentForm.RemoveRowAsync(row);
                actions.Controls.Add(removeButton);
            }

            ConfigureButton(detailsButton, "Details \u25BE", "View extension details, folder, and permissions");
            detailsButton.Click += (_, _) =>
            {
                detailsPanel.Visible = !detailsPanel.Visible;
                detailsButton.Text = detailsPanel.Visible ? "Details \u25B4" : "Details \u25BE";
            };
            actions.Controls.Add(detailsButton);
            layout.Controls.Add(actions, 0, 3);

            // Row 4: Expandable Details Panel
            detailsPanel.Dock = DockStyle.Top;
            detailsPanel.AutoSize = true;
            detailsPanel.Visible = false;
            detailsPanel.BackColor = SystemInformation.HighContrast ? SystemColors.Control : NativeUiTheme.SurfaceRaised;
            detailsPanel.Padding = new Padding(12);
            detailsPanel.Margin = new Padding(0, 8, 0, 0);
            detailsPanel.Paint += OnDetailsPanelPaint;

            var detailsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                Margin = Padding.Empty,
                BackColor = Color.Transparent
            };
            detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            if (!string.IsNullOrEmpty(row.FolderPath))
            {
                var folderLabel = new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    Font = CardDetailFont,
                    ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : NativeUiTheme.SecondaryText,
                    BackColor = Color.Transparent,
                    Text = $"Package folder: {row.FolderPath}",
                    Margin = new Padding(0, 0, 0, 4)
                };
                detailsLayout.Controls.Add(folderLabel, 0, 0);
            }

            if (row.StoreId is not null)
            {
                var storeUrlLabel = new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    Font = CardDetailFont,
                    ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : NativeUiTheme.Accent,
                    BackColor = Color.Transparent,
                    Text = $"Chrome Web Store: https://chromewebstore.google.com/detail/{row.StoreId}",
                    Margin = new Padding(0, 0, 0, 6)
                };
                detailsLayout.Controls.Add(storeUrlLabel, 0, 1);
            }

            var capList = row.Capabilities ?? [];
            var capText = capList.Count == 0
                ? "No special permissions or site access declared in manifest."
                : "Declared permissions and site access:" + Environment.NewLine + string.Join(Environment.NewLine, capList.Select(c => "  \u00B7 " + c));
            var capLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Font = CardDetailFont,
                ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : NativeUiTheme.SecondaryText,
                BackColor = Color.Transparent,
                Text = capText,
                Margin = new Padding(0, 0, 0, 4)
            };
            detailsLayout.Controls.Add(capLabel, 0, 2);

            detailsPanel.Controls.Add(detailsLayout);
            layout.Controls.Add(detailsPanel, 0, 4);

            Controls.Add(layout);
            ApplyCardButtonsTheme();
        }

        protected override void OnSystemColorsChanged(EventArgs e)
        {
            base.OnSystemColorsChanged(e);
            var highContrast = SystemInformation.HighContrast;
            BackColor = highContrast ? SystemColors.Window : NativeUiTheme.Surface;
            ForeColor = highContrast ? SystemColors.WindowText : NativeUiTheme.Text;
            detailsPanel.BackColor = highContrast ? SystemColors.Control : NativeUiTheme.SurfaceRaised;
            ApplyCardButtonsTheme();
            Invalidate(true);
        }

        private void ApplyCardButtonsTheme()
        {
            NativeUiTheme.ApplySecondaryButton(toggleButton);
            NativeUiTheme.ApplySecondaryButton(detailsButton);
            if (optionsButton is not null) NativeUiTheme.ApplySecondaryButton(optionsButton);
            if (updateButton is not null) NativeUiTheme.ApplySecondaryButton(updateButton);
            if (removeButton is not null) NativeUiTheme.ApplySecondaryButton(removeButton);
            if (openPopupButton is not null) NativeUiTheme.ApplyAccentButton(openPopupButton);
        }

        private void OnDetailsPanelPaint(object? sender, PaintEventArgs e)
        {
            if (detailsPanel.Width <= 2 || detailsPanel.Height <= 2) return;
            var g = e.Graphics;
            var highContrast = SystemInformation.HighContrast;
            g.Clear(highContrast ? SystemColors.Window : NativeUiTheme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, detailsPanel.Width - 1f, detailsPanel.Height - 1f);
            using var path = NativeUiTheme.CreateRoundedPath(rect, 8f);
            using var fill = new SolidBrush(highContrast ? SystemColors.Control : NativeUiTheme.SurfaceRaised);
            using var pen = new Pen(highContrast ? SystemColors.WindowText : Color.FromArgb(120, NativeUiTheme.Border), 1f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        private static Bitmap? TryLoadUnlockedBitmap(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var decoded = Image.FromStream(
                    stream,
                    useEmbeddedColorManagement: false,
                    validateImageData: true);
                return new Bitmap(decoded);
            }
            catch
            {
                return null;
            }
        }

        public void UpdateBusyState(bool busy)
        {
            if (openPopupButton is not null) openPopupButton.Enabled = !busy && Row.IsEnabled;
            if (optionsButton is not null) optionsButton.Enabled = !busy && Row.IsEnabled;
            toggleButton.Enabled = !busy;
            if (updateButton is not null) updateButton.Enabled = !busy && Row.CanUpdate && Row.IsEnabled;
            if (removeButton is not null) removeButton.Enabled = !busy;
            detailsButton.Enabled = !busy;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width <= 2 || Height <= 2)
            {
                base.OnPaintBackground(e);
                return;
            }

            var g = e.Graphics;
            var highContrast = SystemInformation.HighContrast;
            g.Clear(highContrast ? SystemColors.Window : NativeUiTheme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using var path = NativeUiTheme.CreateRoundedPath(rect, 11f);
            using (var fill = new SolidBrush(highContrast ? SystemColors.Window : NativeUiTheme.Surface))
            {
                g.FillPath(fill, path);
            }

            if (!highContrast)
            {
                var clipState = g.Save();
                try
                {
                    g.SetClip(path, CombineMode.Intersect);
                    var sheenRect = new RectangleF(rect.X, rect.Y, rect.Width, 14f);
                    using var sheen = new LinearGradientBrush(
                        sheenRect,
                        Color.FromArgb(28, 255, 240, 250),
                        Color.FromArgb(0, NativeUiTheme.Surface),
                        LinearGradientMode.Vertical);
                    g.FillRectangle(sheen, sheenRect);
                }
                finally
                {
                    g.Restore(clipState);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 2 || Height <= 2) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using var path = NativeUiTheme.CreateRoundedPath(rect, 11f);
            if (SystemInformation.HighContrast)
            {
                using var hcPen = new Pen(SystemColors.WindowText, 1f);
                g.DrawPath(hcPen, path);
                return;
            }

            using var rimBrush = new LinearGradientBrush(
                rect,
                Color.FromArgb(165, 255, 224, 246),
                Color.FromArgb(150, NativeUiTheme.Border),
                LinearGradientMode.Vertical);
            using var pen = new Pen(rimBrush, 1f);
            g.DrawPath(pen, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                iconImage?.Dispose();
                iconImage = null;
            }
            base.Dispose(disposing);
        }
    }

    private sealed class PillBadgeLabel : Label
    {
        private readonly Color accentTint;

        public PillBadgeLabel(Color accentTint)
        {
            this.accentTint = accentTint;
            AutoSize = true;
            Padding = new Padding(8, 2, 8, 2);
            Margin = new Padding(0, 2, 6, 2);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            var highContrast = SystemInformation.HighContrast;
            g.Clear(highContrast ? SystemColors.Window : NativeUiTheme.Surface);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 2 || Height <= 2) return;
            var g = e.Graphics;
            var highContrast = SystemInformation.HighContrast;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            var radius = Math.Max(4f, rect.Height / 2f);
            using var path = NativeUiTheme.CreateRoundedPath(rect, radius);

            using (var bg = new SolidBrush(highContrast ? SystemColors.Control : Color.FromArgb(42, accentTint)))
            {
                g.FillPath(bg, path);
            }

            using (var pen = new Pen(highContrast ? SystemColors.WindowText : Color.FromArgb(145, accentTint), 1f))
            {
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(
                g,
                Text,
                Font,
                ClientRectangle,
                highContrast ? SystemColors.ControlText : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    private sealed class ExtensionIconBox : Control
    {
        private readonly Image? iconImage;
        private readonly string name;

        public ExtensionIconBox(Image? iconImage, string name)
        {
            this.iconImage = iconImage;
            this.name = name;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var highContrast = SystemInformation.HighContrast;
            g.Clear(highContrast ? SystemColors.Window : NativeUiTheme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(1f, 1f, Math.Max(2f, Width - 2f), Math.Max(2f, Height - 2f));
            using var squircle = NativeUiTheme.CreateRoundedPath(rect, Math.Max(6f, rect.Width * 0.26f));

            if (iconImage is not null)
            {
                var clipState = g.Save();
                try
                {
                    g.SetClip(squircle, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(iconImage, rect);
                }
                finally
                {
                    g.Restore(clipState);
                }

                using var imageRim = new Pen(
                    highContrast ? SystemColors.WindowText : Color.FromArgb(130, NativeUiTheme.Border),
                    1f);
                g.DrawPath(imageRim, squircle);
                return;
            }

            using (var bgBrush = new SolidBrush(highContrast ? SystemColors.Control : NativeUiTheme.SurfaceRaised))
            {
                g.FillPath(bgBrush, squircle);
            }

            using (var borderPen = new Pen(highContrast ? SystemColors.WindowText : NativeUiTheme.Border, 1f))
            {
                g.DrawPath(borderPen, squircle);
            }

            var letter = string.IsNullOrWhiteSpace(name) ? "E" : name.TrimStart()[0].ToString().ToUpperInvariant();
            using var textBrush = new SolidBrush(highContrast ? SystemColors.ControlText : NativeUiTheme.Accent);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(letter, FallbackIconFont, textBrush, ClientRectangle, sf);
        }
    }
}

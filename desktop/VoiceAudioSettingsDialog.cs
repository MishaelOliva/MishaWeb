using System.Diagnostics;

namespace MishaWeb;

internal sealed record VoiceAudioSettingsResult(
    AudioOutputPolicy AudioOutputPolicy,
    IReadOnlyList<string> AllowedAudioHosts,
    IReadOnlyList<string> MutedHosts,
    AudioInputPolicy AudioInputPolicy,
    IReadOnlyList<string> AllowedMicrophoneHosts,
    IReadOnlyList<string> BlockedMicrophoneHosts);

internal sealed class VoiceAudioSettingsDialog : Form
{
    private static readonly Font PickerFont = NativeUiTheme.GetSharedFont("Segoe UI", 10f, FontStyle.Bold);
    private static readonly Font DescriptionFont = NativeUiTheme.GetSharedFont("Segoe UI", 9f, FontStyle.Italic);
    private static readonly Font ExplanationFont = NativeUiTheme.GetSharedFont("Segoe UI", 9f);
    private static readonly Font SiteListFont = NativeUiTheme.GetSharedFont("Segoe UI", 9.5f);

    private readonly string? currentHost;
    private readonly Action<VoiceAudioSettingsResult> saveCallback;

    // Saved baseline for dirty-state tracking
    private AudioOutputPolicy savedAudioOutputPolicy;
    private string[] savedAllowedAudioHosts;
    private string[] savedMutedHosts;
    private AudioInputPolicy savedAudioInputPolicy;
    private string[] savedAllowedMicrophoneHosts;
    private string[] savedBlockedMicrophoneHosts;

    // Working copies of state
    private AudioOutputPolicy workingAudioOutputPolicy;
    private readonly List<string> workingAllowedAudioHosts;
    private readonly List<string> workingMutedHosts;

    private AudioInputPolicy workingAudioInputPolicy;
    private readonly List<string> workingAllowedMicrophoneHosts;
    private readonly List<string> workingBlockedMicrophoneHosts;
    private ModernListRowVisual[] hostRowVisuals = [];
    private bool updatingControls;

    // UI Controls
    private readonly ComboBox sectionPicker = new();
    private readonly Label sectionDescription = new();

    // Mode dropdown & policy explanation
    private readonly Label policyLabel = new();
    private readonly ComboBox policyPicker = new();
    private readonly Label policyExplanation = new();

    // Site list controls
    private readonly Label listLabel = new();
    private readonly ComboBox listTypePicker = new();
    private readonly ListBox siteListBox = new();
    private readonly Label emptyListLabel = new();
    private readonly Button addSiteButton = new();
    private readonly Button removeSiteButton = new();
    private readonly Button addCurrentSiteButton = new();
    private readonly Button clearListButton = new();

    // OS Integration button
    private readonly Button osSettingsButton = new();

    // Dialog action buttons
    private readonly Button applyButton = new();
    private readonly Button okButton = new();
    private readonly Button cancelButton = new();

    public VoiceAudioSettingsDialog(
        BrowserState state,
        string? currentHost,
        Action<VoiceAudioSettingsResult> saveCallback)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.saveCallback = saveCallback ?? throw new ArgumentNullException(nameof(saveCallback));
        this.currentHost = BrowserPolicy.NormalizeExactHost(currentHost);

        workingAudioOutputPolicy = state.AudioOutputPolicy;
        workingAllowedAudioHosts = new List<string>(state.AllowedAudioHosts ?? []);
        workingMutedHosts = new List<string>(state.MutedHosts ?? []);

        workingAudioInputPolicy = state.AudioInputPolicy;
        workingAllowedMicrophoneHosts = new List<string>(state.AllowedMicrophoneHosts ?? []);
        workingBlockedMicrophoneHosts = new List<string>(state.BlockedMicrophoneHosts ?? []);

        savedAudioOutputPolicy = workingAudioOutputPolicy;
        savedAllowedAudioHosts = workingAllowedAudioHosts.ToArray();
        savedMutedHosts = workingMutedHosts.ToArray();
        savedAudioInputPolicy = workingAudioInputPolicy;
        savedAllowedMicrophoneHosts = workingAllowedMicrophoneHosts.ToArray();
        savedBlockedMicrophoneHosts = workingBlockedMicrophoneHosts.ToArray();

        NativeDialogLayout.EnableDpiScaling(this);
        Text = "Voice and audio settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimumSize = new Size(580, 480);
        Size = new Size(680, 560);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = "Voice and audio settings";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 8
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // section picker
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); // section description
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // policy picker
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); // policy explanation
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // list type picker
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // site list and side buttons
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); // OS shortcut button
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, NativeDialogLayout.ActionRowHeight)); // bottom action bar

        // 0. Section picker
        sectionPicker.Dock = DockStyle.Fill;
        sectionPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        sectionPicker.Font = PickerFont;
        sectionPicker.Items.AddRange(["Audio Output (Sound)", "Voice & Audio Input (Microphone)"]);
        sectionPicker.SelectedIndex = 0;
        sectionPicker.AccessibleName = "Voice and audio section";
        sectionPicker.SelectedIndexChanged += (_, _) => OnSectionChanged();
        root.Controls.Add(sectionPicker, 0, 0);

        // 1. Section description
        sectionDescription.Dock = DockStyle.Fill;
        sectionDescription.AutoSize = false;
        sectionDescription.Font = DescriptionFont;
        sectionDescription.ForeColor = NativeUiTheme.MutedText;
        root.Controls.Add(sectionDescription, 0, 1);

        // 2. Policy picker row
        var policyPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        policyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        policyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        policyLabel.Text = "Mode:";
        policyLabel.Dock = DockStyle.Fill;
        policyLabel.TextAlign = ContentAlignment.MiddleLeft;
        policyPicker.Dock = DockStyle.Fill;
        policyPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        policyPicker.AccessibleName = "Policy mode";
        policyPicker.SelectedIndexChanged += (_, _) => OnPolicyChanged();
        policyPanel.Controls.Add(policyLabel, 0, 0);
        policyPanel.Controls.Add(policyPicker, 1, 0);
        root.Controls.Add(policyPanel, 0, 2);

        // 3. Policy explanation
        policyExplanation.Dock = DockStyle.Fill;
        policyExplanation.AutoSize = false;
        policyExplanation.Font = ExplanationFont;
        root.Controls.Add(policyExplanation, 0, 3);

        // 4. List type selector row
        var listTypePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        listTypePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        listTypePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listLabel.Text = "Manage sites:";
        listLabel.Dock = DockStyle.Fill;
        listLabel.TextAlign = ContentAlignment.MiddleLeft;
        listTypePicker.Dock = DockStyle.Fill;
        listTypePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        listTypePicker.AccessibleName = "Site list selector";
        listTypePicker.SelectedIndexChanged += (_, _) => PopulateSiteList();
        listTypePanel.Controls.Add(listLabel, 0, 0);
        listTypePanel.Controls.Add(listTypePicker, 1, 0);
        root.Controls.Add(listTypePanel, 0, 4);

        // 5. Site list and side buttons
        var listAndButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        listAndButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listAndButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 162));

        var siteListHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        siteListBox.Dock = DockStyle.Fill;
        siteListBox.IntegralHeight = false;
        siteListBox.Font = SiteListFont;
        siteListBox.AccessibleName = "Configured sites list";
        siteListBox.SelectedIndexChanged += (_, _) => UpdateButtonStates();
        siteListBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                OnRemoveSite();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        emptyListLabel.Dock = DockStyle.Fill;
        emptyListLabel.TextAlign = ContentAlignment.MiddleCenter;
        emptyListLabel.Font = ExplanationFont;
        emptyListLabel.Text = "No sites in this list yet. Use \"Add site\u2026\" to add a host.";
        emptyListLabel.Visible = false;
        emptyListLabel.AccessibleRole = AccessibleRole.StatusBar;
        siteListHost.Controls.Add(siteListBox);
        siteListHost.Controls.Add(emptyListLabel);
        listAndButtons.Controls.Add(siteListHost, 0, 0);

        var sideButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = new Padding(8, 0, 0, 0),
            Padding = Padding.Empty
        };
        sideButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) sideButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        sideButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        ConfigureSideButton(addSiteButton, "Add site\u2026", "Add a website host to this list");
        ConfigureSideButton(removeSiteButton, "Remove", "Remove selected website host");
        ConfigureSideButton(addCurrentSiteButton, "Add current site", "Add the current tab's host to this list");
        ConfigureSideButton(clearListButton, "Clear list", "Remove all hosts from this list");

        addSiteButton.Click += (_, _) => OnAddSite();
        removeSiteButton.Click += (_, _) => OnRemoveSite();
        addCurrentSiteButton.Click += (_, _) => OnAddCurrentSite();
        clearListButton.Click += (_, _) => OnClearList();

        sideButtons.Controls.Add(addSiteButton, 0, 0);
        sideButtons.Controls.Add(removeSiteButton, 0, 1);
        sideButtons.Controls.Add(addCurrentSiteButton, 0, 2);
        sideButtons.Controls.Add(clearListButton, 0, 3);
        listAndButtons.Controls.Add(sideButtons, 1, 0);
        root.Controls.Add(listAndButtons, 0, 5);

        // 6. OS integration shortcut button
        NativeDialogLayout.ConfigureButton(osSettingsButton, "Windows sound settings", "Open Windows system settings");
        osSettingsButton.Click += (_, _) => OnOpenOsSettings();
        root.Controls.Add(osSettingsButton, 0, 6);

        // 7. Bottom action bar
        var actions = NativeDialogLayout.CreateActionBar();
        NativeDialogLayout.ConfigureButton(applyButton, "Apply", "Apply settings now");
        NativeDialogLayout.ConfigureButton(okButton, "OK", "Apply settings and close");
        NativeDialogLayout.ConfigureButton(cancelButton, "Close", "Close dialog");

        applyButton.Click += (_, _) => ApplyChanges(close: false);
        okButton.Click += (_, _) => ApplyChanges(close: true);
        cancelButton.Click += (_, _) => Close();

        actions.Controls.AddRange([cancelButton, applyButton, okButton]);
        root.Controls.Add(actions, 0, 7);

        Controls.Add(root);
        AcceptButton = okButton;
        CancelButton = cancelButton;
        NativeUiTheme.Apply(this, okButton);
        NativeUiTheme.ConfigureModernListBox(
            siteListBox,
            46,
            (index, _) => index >= 0 && index < hostRowVisuals.Length
                ? hostRowVisuals[index]
                : new ModernListRowVisual(string.Empty, string.Empty, string.Empty));
        sectionDescription.ForeColor = NativeUiTheme.MutedText;
        emptyListLabel.ForeColor = NativeUiTheme.MutedText;
        SystemColorsChanged += (_, _) =>
        {
            sectionDescription.ForeColor = NativeUiTheme.MutedText;
            emptyListLabel.ForeColor = NativeUiTheme.MutedText;
        };

        OnSectionChanged();
        UpdateButtonStates();
    }

    private bool IsAudioOutputSection => sectionPicker.SelectedIndex == 0;

    private void OnSectionChanged()
    {
        updatingControls = true;
        try
        {
            policyPicker.Items.Clear();
            listTypePicker.Items.Clear();

            if (IsAudioOutputSection)
            {
                sectionDescription.Text = "Configure when websites are allowed to produce audio sound.";
                policyPicker.Items.AddRange([
                    "Allow sound on all sites (except muted)",
                    "Only allow sound from specific sites",
                    "Mute all sites (block all sound)"
                ]);
                listTypePicker.Items.AddRange([
                    "Allowed sound sites (whitelist)",
                    "Muted sound sites (blacklist)"
                ]);
                policyPicker.SelectedIndex = (int)workingAudioOutputPolicy;
                listTypePicker.SelectedIndex = workingAudioOutputPolicy == AudioOutputPolicy.SpecificSitesOnly ? 0 : 1;
                osSettingsButton.Text = "Windows sound & device settings…";
                osSettingsButton.AccessibleDescription = "Open Windows Sound settings to configure output and input devices";
            }
            else
            {
                sectionDescription.Text = "Configure when websites are allowed to take in sound from your microphone.";
                policyPicker.Items.AddRange([
                    "Ask every time a site requests microphone",
                    "Only allow microphone on specific sites",
                    "Block microphone on all sites"
                ]);
                listTypePicker.Items.AddRange([
                    "Allowed microphone sites",
                    "Blocked microphone sites"
                ]);
                policyPicker.SelectedIndex = (int)workingAudioInputPolicy;
                listTypePicker.SelectedIndex = workingAudioInputPolicy == AudioInputPolicy.SpecificSitesOnly ? 0 : 1;
                osSettingsButton.Text = "Windows microphone privacy settings…";
                osSettingsButton.AccessibleDescription = "Open Windows Privacy settings for microphone";
            }
        }
        finally
        {
            updatingControls = false;
        }

        UpdatePolicyExplanation();
        PopulateSiteList();
    }

    private void OnPolicyChanged()
    {
        if (updatingControls || policyPicker.SelectedIndex < 0) return;

        if (IsAudioOutputSection)
        {
            workingAudioOutputPolicy = (AudioOutputPolicy)policyPicker.SelectedIndex;
            if (workingAudioOutputPolicy == AudioOutputPolicy.SpecificSitesOnly)
            {
                listTypePicker.SelectedIndex = 0; // Show Allowed sites
            }
        }
        else
        {
            workingAudioInputPolicy = (AudioInputPolicy)policyPicker.SelectedIndex;
            if (workingAudioInputPolicy == AudioInputPolicy.SpecificSitesOnly)
            {
                listTypePicker.SelectedIndex = 0; // Show Allowed sites
            }
        }

        UpdatePolicyExplanation();
        UpdateButtonStates();
    }

    private void UpdatePolicyExplanation()
    {
        if (IsAudioOutputSection)
        {
            policyExplanation.Text = workingAudioOutputPolicy switch
            {
                AudioOutputPolicy.AllowAll =>
                    "Websites can produce sound normally. Sound will be suppressed only on sites listed in 'Muted sound sites'.",
                AudioOutputPolicy.SpecificSitesOnly =>
                    "Strict mode: The web will ONLY produce sound on sites listed in 'Allowed sound sites'. All other sites are muted.",
                AudioOutputPolicy.MuteAll =>
                    "All web sound is blocked. No website will produce audio.",
                _ => string.Empty
            };
        }
        else
        {
            policyExplanation.Text = workingAudioInputPolicy switch
            {
                AudioInputPolicy.AskEveryTime =>
                    "Websites will prompt for microphone permission. Allowed sites are auto-granted; blocked sites are denied.",
                AudioInputPolicy.SpecificSitesOnly =>
                    "Strict mode: The web will ONLY take in sound from the microphone on sites in 'Allowed microphone sites'. All other sites are blocked without prompts.",
                AudioInputPolicy.BlockAll =>
                    "All microphone access is blocked. No website can take in sound from your microphone.",
                _ => string.Empty
            };
        }
    }

    private List<string> GetActiveHostList()
    {
        if (IsAudioOutputSection)
        {
            return listTypePicker.SelectedIndex == 0
                ? workingAllowedAudioHosts
                : workingMutedHosts;
        }
        else
        {
            return listTypePicker.SelectedIndex == 0
                ? workingAllowedMicrophoneHosts
                : workingBlockedMicrophoneHosts;
        }
    }

    private static void ConfigureSideButton(Button button, string text, string description)
    {
        button.Dock = DockStyle.Fill;
        button.AutoSize = false;
        button.AutoEllipsis = true;
        button.MinimumSize = new Size(140, 30);
        button.Margin = new Padding(0, 0, 0, 6);
        button.Padding = new Padding(6, 0, 6, 0);
        button.Text = text;
        button.AccessibleName = text;
        button.AccessibleDescription = description;
    }

    private void PopulateSiteList()
    {
        var activeList = GetActiveHostList();
        var isAllowedList = listTypePicker.SelectedIndex == 0;
        var statusColor = isAllowedList ? NativeUiTheme.Positive : NativeUiTheme.Negative;
        var statusText = isAllowedList
            ? "Allowed"
            : IsAudioOutputSection ? "Muted" : "Blocked";
        var detail = IsAudioOutputSection
            ? (isAllowedList ? "Sound output allowed" : "Sound output suppressed")
            : (isAllowedList ? "Microphone access allowed" : "Microphone access blocked");
        var visuals = activeList.Count == 0 ? [] : new ModernListRowVisual[activeList.Count];

        siteListBox.BeginUpdate();
        try
        {
            siteListBox.Items.Clear();
            for (var index = 0; index < activeList.Count; index++)
            {
                var host = activeList[index];
                var glyph = string.IsNullOrWhiteSpace(host)
                    ? "S"
                    : char.ToUpperInvariant(host.TrimStart()[0]).ToString();
                visuals[index] = new ModernListRowVisual(
                    host,
                    detail,
                    glyph,
                    statusColor,
                    StatusText: statusText,
                    StatusColor: statusColor);
                siteListBox.Items.Add(host);
            }
            hostRowVisuals = visuals;
        }
        finally
        {
            siteListBox.EndUpdate();
        }

        var hasSites = activeList.Count > 0;
        siteListBox.Visible = hasSites;
        emptyListLabel.Visible = !hasSites;
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        removeSiteButton.Enabled = siteListBox.SelectedIndex >= 0;
        var activeList = GetActiveHostList();
        clearListButton.Enabled = activeList.Count > 0;

        if (string.IsNullOrWhiteSpace(currentHost))
        {
            addCurrentSiteButton.Enabled = false;
            addCurrentSiteButton.Text = "Add current site";
            addCurrentSiteButton.AccessibleName = "Add current site";
        }
        else
        {
            var alreadyInList = activeList.Any(h => BrowserPolicy.IsExactHost(h, currentHost));
            addCurrentSiteButton.Enabled = !alreadyInList;
            var displayHost = TextSafety.TruncateWithEllipsis(currentHost, 18);
            addCurrentSiteButton.Text = $"Add {displayHost}";
            addCurrentSiteButton.AccessibleName = $"Add {currentHost}";
        }

        applyButton.Enabled = HasPendingChanges();
    }

    private bool HasPendingChanges()
    {
        return workingAudioOutputPolicy != savedAudioOutputPolicy
            || workingAudioInputPolicy != savedAudioInputPolicy
            || !HostListsMatch(workingAllowedAudioHosts, savedAllowedAudioHosts)
            || !HostListsMatch(workingMutedHosts, savedMutedHosts)
            || !HostListsMatch(workingAllowedMicrophoneHosts, savedAllowedMicrophoneHosts)
            || !HostListsMatch(workingBlockedMicrophoneHosts, savedBlockedMicrophoneHosts);
    }

    private static bool HostListsMatch(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!BrowserPolicy.IsExactHost(left[i], right[i])) return false;
        }
        return true;
    }

    private void OnAddSite()
    {
        var targetListName = listTypePicker.SelectedItem?.ToString() ?? "sites";
        if (TextPromptDialog.TryShow(
            this,
            "Add website host",
            $"Enter website host for {targetListName} (e.g. example.com):",
            currentHost ?? string.Empty,
            out var rawInput))
        {
            var normalized = BrowserPolicy.NormalizeExactHost(rawInput);
            if (normalized is null)
            {
                NativeAlertDialog.ShowInfo(
                    this,
                    "Invalid host",
                    "Please enter a valid hostname (e.g. example.com).");
                return;
            }

            var activeList = GetActiveHostList();
            if (!activeList.Any(h => BrowserPolicy.IsExactHost(h, normalized)))
            {
                activeList.Insert(0, normalized);
                PopulateSiteList();
                siteListBox.SelectedIndex = 0;
            }
        }
    }

    private void OnRemoveSite()
    {
        var index = siteListBox.SelectedIndex;
        if (index < 0) return;
        var activeList = GetActiveHostList();
        if (index < activeList.Count)
        {
            activeList.RemoveAt(index);
            PopulateSiteList();
            if (siteListBox.Items.Count > 0)
            {
                siteListBox.SelectedIndex = Math.Min(index, siteListBox.Items.Count - 1);
            }
        }
    }

    private void OnAddCurrentSite()
    {
        if (string.IsNullOrWhiteSpace(currentHost)) return;
        var activeList = GetActiveHostList();
        if (!activeList.Any(h => BrowserPolicy.IsExactHost(h, currentHost)))
        {
            activeList.Insert(0, currentHost);
            PopulateSiteList();
            siteListBox.SelectedIndex = 0;
        }
    }

    private void OnClearList()
    {
        var targetListName = listTypePicker.SelectedItem?.ToString() ?? "sites";
        if (NativeAlertDialog.Confirm(
            this,
            "Clear list",
            $"Remove all hosts from {targetListName}?",
            confirmText: "Clear",
            cancelText: "Cancel"))
        {
            GetActiveHostList().Clear();
            PopulateSiteList();
        }
    }

    private void OnOpenOsSettings()
    {
        var uri = IsAudioOutputSection
            ? "ms-settings:apps-volume"
            : "ms-settings:privacy-microphone";
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            try
            {
                if (IsAudioOutputSection)
                {
                    Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
                }
            }
            catch
            {
                NativeAlertDialog.ShowInfo(
                    this,
                    "Settings",
                    "Could not open Windows settings. Please open Windows Settings manually to configure device hardware.");
            }
        }
    }

    private void ApplyChanges(bool close)
    {
        var allowedAudio = workingAllowedAudioHosts.ToArray();
        var mutedAudio = workingMutedHosts.ToArray();
        var allowedMic = workingAllowedMicrophoneHosts.ToArray();
        var blockedMic = workingBlockedMicrophoneHosts.ToArray();
        var result = new VoiceAudioSettingsResult(
            workingAudioOutputPolicy,
            allowedAudio,
            mutedAudio,
            workingAudioInputPolicy,
            allowedMic,
            blockedMic);

        saveCallback(result);
        savedAudioOutputPolicy = workingAudioOutputPolicy;
        savedAllowedAudioHosts = allowedAudio;
        savedMutedHosts = mutedAudio;
        savedAudioInputPolicy = workingAudioInputPolicy;
        savedAllowedMicrophoneHosts = allowedMic;
        savedBlockedMicrophoneHosts = blockedMic;

        if (close)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            UpdateButtonStates();
        }
    }
}

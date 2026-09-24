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
    private readonly string? currentHost;
    private readonly Action<VoiceAudioSettingsResult> saveCallback;

    // Working copies of state
    private AudioOutputPolicy workingAudioOutputPolicy;
    private readonly List<string> workingAllowedAudioHosts;
    private readonly List<string> workingMutedHosts;

    private AudioInputPolicy workingAudioInputPolicy;
    private readonly List<string> workingAllowedMicrophoneHosts;
    private readonly List<string> workingBlockedMicrophoneHosts;
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
        sectionPicker.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        sectionPicker.Items.AddRange(["Audio Output (Sound)", "Voice & Audio Input (Microphone)"]);
        sectionPicker.SelectedIndex = 0;
        sectionPicker.AccessibleName = "Voice and audio section";
        sectionPicker.SelectedIndexChanged += (_, _) => OnSectionChanged();
        root.Controls.Add(sectionPicker, 0, 0);

        // 1. Section description
        sectionDescription.Dock = DockStyle.Fill;
        sectionDescription.AutoSize = false;
        sectionDescription.Font = new Font("Segoe UI", 9f, FontStyle.Italic);
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
        policyExplanation.Font = new Font("Segoe UI", 9f);
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
        listAndButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        siteListBox.Dock = DockStyle.Fill;
        siteListBox.IntegralHeight = false;
        siteListBox.Font = new Font("Segoe UI", 9.5f);
        siteListBox.AccessibleName = "Configured sites list";
        siteListBox.SelectedIndexChanged += (_, _) => UpdateButtonStates();
        listAndButtons.Controls.Add(siteListBox, 0, 0);

        var sideButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty
        };
        NativeDialogLayout.ConfigureButton(addSiteButton, "Add site…", "Add a website host to this list");
        NativeDialogLayout.ConfigureButton(removeSiteButton, "Remove", "Remove selected website host");
        NativeDialogLayout.ConfigureButton(addCurrentSiteButton, "Add current site", "Add the current tab's host to this list");
        NativeDialogLayout.ConfigureButton(clearListButton, "Clear list", "Remove all hosts from this list");

        addSiteButton.Click += (_, _) => OnAddSite();
        removeSiteButton.Click += (_, _) => OnRemoveSite();
        addCurrentSiteButton.Click += (_, _) => OnAddCurrentSite();
        clearListButton.Click += (_, _) => OnClearList();

        sideButtons.Controls.AddRange([addSiteButton, removeSiteButton, addCurrentSiteButton, clearListButton]);
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
        NativeUiTheme.Apply(this, okButton);

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

    private void PopulateSiteList()
    {
        var activeList = GetActiveHostList();
        siteListBox.BeginUpdate();
        siteListBox.Items.Clear();
        foreach (var host in activeList)
        {
            siteListBox.Items.Add(host);
        }
        siteListBox.EndUpdate();
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
        }
        else
        {
            var alreadyInList = activeList.Any(h => BrowserPolicy.IsExactHost(h, currentHost));
            addCurrentSiteButton.Enabled = !alreadyInList;
            addCurrentSiteButton.Text = $"Add {currentHost}";
        }
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
                MessageBox.Show(
                    this,
                    "Please enter a valid hostname (e.g. example.com).",
                    "Invalid host",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
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
        if (MessageBox.Show(
            this,
            $"Remove all hosts from {targetListName}?",
            "Clear list",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes)
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
                MessageBox.Show(
                    this,
                    $"Could not open Windows settings. Please open Windows Settings manually to configure device hardware.",
                    "Settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
    }

    private void ApplyChanges(bool close)
    {
        var result = new VoiceAudioSettingsResult(
            workingAudioOutputPolicy,
            workingAllowedAudioHosts.ToArray(),
            workingMutedHosts.ToArray(),
            workingAudioInputPolicy,
            workingAllowedMicrophoneHosts.ToArray(),
            workingBlockedMicrophoneHosts.ToArray());

        saveCallback(result);

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

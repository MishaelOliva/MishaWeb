using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MishaWeb;

public sealed partial class MainForm
{
    private readonly Dictionary<string, (int Size, Bitmap Icon)> extensionMenuIconCache = new(StringComparer.OrdinalIgnoreCase);
    private bool extensionIconCacheCleanupWired;
    private bool appSubmenusLazyWired;
    private bool favoritesSubmenuDirty = true;
    private bool historySubmenuDirty = true;
    private bool downloadsSubmenuDirty = true;
    private bool searchProviderSubmenuDirty = true;
    private bool extensionsSubmenuDirty = true;

    private void ToggleFavorite()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Favorites are unavailable in a private window");
            return;
        }
        var tab = activeTab;
        if (tab is null || tab.IsStartPage || !BrowserPolicy.IsHttpUrl(tab.Url))
        {
            ShowTransientStatus("Open a website before adding a favorite");
            return;
        }

        var existing = state.Bookmarks.FindIndex(item =>
            BrowserPolicy.UrlEquals(item.Url, tab.Url));
        if (existing >= 0)
        {
            state.Bookmarks.RemoveAt(existing);
            ShowTransientStatus("Removed from favorites");
        }
        else
        {
            state.Bookmarks.Insert(0, new BookmarkEntry(tab.Title, tab.Url));
            if (state.Bookmarks.Count > 200) state.Bookmarks.RemoveRange(200, state.Bookmarks.Count - 200);
            ShowTransientStatus("Added to favorites");
        }

        InvalidateStartPageLinks();
        ScheduleStateSave();
        UpdateFavoriteButton();
        RefreshNativeStartPages(refreshLinks: true);
    }

    private void AddHistory(BrowserTab tab)
    {
        if (isPrivateMode) return;
        if (tab.IsStartPage || !BrowserPolicy.IsHttpUrl(tab.Url)) return;
        state.History.RemoveAll(item => BrowserPolicy.UrlEquals(item.Url, tab.Url));
        state.History.Insert(0, new HistoryEntry(tab.Title, tab.Url, DateTime.UtcNow));
        if (state.History.Count > 300) state.History.RemoveRange(300, state.History.Count - 300);
        InvalidateStartPageLinks();
        ScheduleStateSave();
    }

    private void UpdateHistoryTitle(BrowserTab tab)
    {
        if (isPrivateMode) return;
        if (tab.IsStartPage || !BrowserPolicy.IsHttpUrl(tab.Url)) return;
        var index = state.History.FindIndex(item => BrowserPolicy.UrlEquals(item.Url, tab.Url));
        if (index < 0 || state.History[index].Title.Equals(tab.Title, StringComparison.Ordinal)) return;

        var entry = state.History[index];
        state.History[index] = entry with { Title = tab.Title };
        InvalidateStartPageLinks();
        ScheduleStateSave();
    }

    private async Task ClearBrowsingDataAsync()
    {
        if (!NativeAlertDialog.Confirm(
                this,
                "Clear browsing data",
                "Clear cookies, cache, saved passwords, autofill data, permissions, and browsing history? Favorites are kept.",
                confirmText: "Clear data",
                cancelText: "Cancel"))
        {
            return;
        }

        ShowTransientStatus("Clearing browsing data\u2026", false);
        CancelAllPendingPermissionRequests();
        foreach (var tab in tabs) ClearTabMediaPermissions(tab);

        Exception? profileClearError = null;
        CoreWebView2Controller? temporaryController = null;
        try
        {
            var core = activeTab?.Core
                ?? tabs.Select(item => item.Core).FirstOrDefault(item => item is not null);
            if (core is null)
            {
                var browserEnvironment = await GetOrCreateEnvironmentAsync();
                if (isClosing
                    || environment != browserEnvironment
                    || !IsHandleCreated)
                {
                    throw new InvalidOperationException("The browser profile is not available.");
                }

                var controllerOptions = browserEnvironment.CreateCoreWebView2ControllerOptions();
                var profileOptions = GetControllerProfileSnapshotForTesting(isPrivateMode);
                controllerOptions.IsInPrivateModeEnabled = profileOptions.IsInPrivateModeEnabled;
                if (profileOptions.ProfileName is not null)
                {
                    controllerOptions.ProfileName = profileOptions.ProfileName;
                }
                temporaryController = await browserEnvironment.CreateCoreWebView2ControllerAsync(
                    Handle,
                    controllerOptions);
                temporaryController.IsVisible = false;
                temporaryController.Bounds = Rectangle.Empty;
                core = temporaryController.CoreWebView2;
            }

            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        }
        catch (Exception error)
        {
            profileClearError = error;
            stateStore.Log("Could not completely clear the WebView2 profile", error);
        }
        finally
        {
            try { temporaryController?.Close(); }
            catch { }
            ReleaseBrowserEnvironmentIfIdle();
        }

        state.History.Clear();
        downloads.RemoveAll(item => item.IsTerminal);
        state.RecentDownloads.Clear();
        closedTabs.Clear();
        state.RecentlyClosed.Clear();
        state.DismissedSuggestionUrls.Clear();
        state.SuggestionUsage.Clear();
        state.SiteZoom.Clear();
        state.MutedHosts.Clear();
        state.KeepAwakeHosts.Clear();
        state.AdBlockExceptionHosts.Clear();
        state.DarkModeExcludedHosts.Clear();
        adBlockExceptionHosts.Clear();
        ClearFaviconCache();
        foreach (var tab in tabs)
        {
            tab.ZoomFactor = 1.0;
            tab.IsMuted = false;
            tab.TabMuteOverride = null;
            tab.KeepAwake = false;
            if (tab.Core is { } tabCore)
            {
                try { tabCore.IsMuted = false; }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
            if (tab.View is not null)
            {
                applyingZoomPreference = true;
                try { tab.View.ZoomFactor = 1.0; }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                finally { applyingZoomPreference = false; }
            }
            if (darkModeEnabled && !tab.IsSuspended && tab.Core is not null)
            {
                RunUiTask(() => ApplyWebsiteThemeAsync(tab), "Could not update the website color preference");
            }
            UpdateTabHeader(tab);
        }
        // The site shield exception list was just emptied, so every live document
        // still carrying window.__mishaAdBlockEnabled === false disagrees with
        // the UI until it reloads. Reinstall the page policy so the exception list
        // is empty for future navigations and state the reload requirement.
        if (adBlockExceptionHosts.Count == 0 && adBlockEnabled)
        {
            RefreshAdBlockPagePolicies();
            ShowTransientStatus("Site shield exceptions cleared; reload open pages to re-enable");
        }
        permissionManager?.Hide();
        InvalidateStartPageLinks();
        RefreshNativeStartPages(refreshLinks: true);
        RefreshDownloadsUi();
        UpdateNavigationChrome();

        var stateSaved = true;
        if (persistStateOnClose)
        {
            stateSaveTimer.Stop();
            PopulateStateFromPreferences();
            stateSaved = await stateStore.FlushAsync(state);
        }

        if (profileClearError is null && stateSaved)
        {
            ShowTransientStatus("Browsing data cleared");
            return;
        }

        ShowTransientStatus("Some browsing data could not be cleared");
        var detail = profileClearError is not null && !stateSaved
            ? "The browser profile and the local state file could not be fully cleared."
            : profileClearError is not null
                ? "The local MishaWeb history was cleared, but the browser profile could not be fully cleared."
                : "The browser profile was cleared, but the local state file could not be updated.";
        NativeAlertDialog.ShowInfo(
            this,
            "Browsing data partially cleared",
            detail);
    }

    private void CancelAllPendingPermissionRequests()
    {
        permissionPrompt?.CloseRequest();
        if (activePermissionRequest is not null)
        {
            var request = activePermissionRequest;
            activePermissionRequest = null;
            CompletePermissionRequest(request, PermissionPromptDecision.Cancel);
        }
        while (permissionQueue.Count > 0)
        {
            CompletePermissionRequest(
                permissionQueue.Dequeue(),
                PermissionPromptDecision.Cancel);
        }
    }

    private void ShowSiteInformation()
    {
        var tab = activeTab;
        if (tab is null) return;
        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        ClearAndDisposeMenuItems(siteInfoMenu.Items);
        if (tab.ConnectionState == ConnectionState.Local
            || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri))
        {
            siteInfoMenu.Items.Add(new ToolStripMenuItem(
                isPrivateMode
                    ? "Private start page \u2014 no normal saved data"
                    : "Offline start page \u2014 no request until you navigate")
            {
                Enabled = false,
                Tag = NativeUiTheme.Text
            });
            ApplyMenuPalette(siteInfoMenu, palette);
            siteInfoMenu.Show(siteInfoButton, new Point(0, siteInfoButton.Height), ToolStripDropDownDirection.BelowLeft);
            return;
        }

        var connection = tab.ConnectionState switch
        {
            ConnectionState.LocalFile => "Local file on this device",
            ConnectionState.Secure => "Encrypted HTTPS",
            ConnectionState.Insecure => "Unencrypted HTTP",
            ConnectionState.CertificateError => "Certificate error \u2014 not trusted",
            ConnectionState.Loading => "Still being verified",
            ConnectionState.Failed => "Navigation did not complete",
            _ => "Unknown connection"
        };
        var connectionColor = tab.ConnectionState switch
        {
            ConnectionState.Secure or ConnectionState.Local or ConnectionState.LocalFile => NativeUiTheme.Positive,
            ConnectionState.Insecure or ConnectionState.CertificateError or ConnectionState.Failed => NativeUiTheme.Negative,
            _ => NativeUiTheme.Text
        };
        var locationLabel = uri.IsFile
            ? TextSafety.TruncateWithEllipsis(uri.LocalPath, 80)
            : uri.UserInfo.Length > 0
                ? $"{uri.IdnHost} — credentials in URL hidden"
                : uri.IdnHost;
        siteInfoMenu.Items.Add(new ToolStripMenuItem(locationLabel)
        {
            Enabled = false,
            Font = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 9.5f),
            Tag = NativeUiTheme.Text
        });
        siteInfoMenu.Items.Add(new ToolStripMenuItem($"Connection: {connection}")
        {
            Enabled = false,
            Tag = connectionColor
        });

        var blockedLabel = tab.BlockedRequestCount > 0
            ? $"Shield blocked {tab.BlockedRequestCount} (Ads {tab.BlockedAdCount} \u00B7 Trackers {tab.BlockedTrackerCount} \u00B7 Scripts {tab.BlockedScriptCount})"
            : "Shield blocked: 0";
        if (tab.BlockedRequestCount > 0 || tab.RecentBlockedHosts.Count > 0)
        {
            var blockedMenu = new ToolStripMenuItem(blockedLabel)
            {
                Tag = tab.BlockedRequestCount > 0
                    ? (isPrivateMode ? NativeUiTheme.Lavender : NativeUiTheme.Accent)
                    : NativeUiTheme.Text
            };
            blockedMenu.DropDown.Renderer = menuRenderer;
            NativeUiTheme.ApplyMenuWindowFrame(blockedMenu.DropDown, palette.WindowFrameBorder);
            blockedMenu.DropDownItems.Add(new ToolStripMenuItem($"Ads: {tab.BlockedAdCount}") { Enabled = false, Tag = NativeUiTheme.SecondaryText });
            blockedMenu.DropDownItems.Add(new ToolStripMenuItem($"Trackers: {tab.BlockedTrackerCount}") { Enabled = false, Tag = NativeUiTheme.SecondaryText });
            blockedMenu.DropDownItems.Add(new ToolStripMenuItem($"Scripts: {tab.BlockedScriptCount}") { Enabled = false, Tag = NativeUiTheme.SecondaryText });
            if (tab.BlockedOtherCount > 0)
            {
                blockedMenu.DropDownItems.Add(new ToolStripMenuItem($"Other: {tab.BlockedOtherCount}") { Enabled = false, Tag = NativeUiTheme.SecondaryText });
            }
            if (tab.RecentBlockedHosts.Count > 0)
            {
                blockedMenu.DropDownItems.Add(new ToolStripSeparator());
                blockedMenu.DropDownItems.Add(new ToolStripMenuItem("Recent blocked hosts") { Enabled = false, Tag = NativeUiTheme.Text });
                foreach (var blockedHost in tab.RecentBlockedHosts)
                {
                    blockedMenu.DropDownItems.Add(new ToolStripMenuItem(blockedHost) { Enabled = false, Tag = NativeUiTheme.SecondaryText });
                }
            }
            siteInfoMenu.Items.Add(blockedMenu);
        }
        else
        {
            siteInfoMenu.Items.Add(new ToolStripMenuItem(blockedLabel)
            {
                Enabled = false,
                Tag = NativeUiTheme.Text
            });
        }

        if (!uri.IsFile)
        {
            var shieldDisabled = IsExceptionHost(uri.Host);
            var shieldItem = new ToolStripMenuItem(
                shieldDisabled ? "Enable shield for this site" : "Disable shield for this site")
            {
                ToolTipText = "Uses this exact host only and reloads matching tabs"
            };
            shieldItem.Click += (_, _) => ToggleSiteShield(uri.Host);
            siteInfoMenu.Items.Add(shieldItem);
        }

        siteInfoMenu.Items.Add(new ToolStripSeparator());

        var currentZoom = Math.Round(tab.ZoomFactor * 100);
        var zoomSubmenu = new ToolStripMenuItem($"Zoom: {currentZoom:0}%");
        zoomSubmenu.DropDown.Renderer = menuRenderer;
        NativeUiTheme.ApplyMenuWindowFrame(zoomSubmenu.DropDown, palette.WindowFrameBorder);
        var zoomIn = new ToolStripMenuItem("Zoom in") { ShortcutKeyDisplayString = "Ctrl++" };
        zoomIn.Click += (_, _) => ChangeZoom(0.10);
        var zoomOut = new ToolStripMenuItem("Zoom out") { ShortcutKeyDisplayString = "Ctrl+-" };
        zoomOut.Click += (_, _) => ChangeZoom(-0.10);
        var resetZoom = new ToolStripMenuItem("Reset zoom to 100%") { ShortcutKeyDisplayString = "Ctrl+0" };
        resetZoom.Click += (_, _) => ChangeZoom(0, reset: true);
        zoomSubmenu.DropDownItems.Add(zoomIn);
        zoomSubmenu.DropDownItems.Add(zoomOut);
        zoomSubmenu.DropDownItems.Add(resetZoom);
        siteInfoMenu.Items.Add(zoomSubmenu);

        if (uri.IsFile)
        {
            var localReaderItem = new ToolStripMenuItem(
                tab.ReaderModeActive ? "Exit reader mode" : "Enter reader mode")
            {
                Enabled = tab.Core is not null
            };
            localReaderItem.Click += (_, _) => RunUiTask(
                ToggleReaderModeAsync,
                "Reader mode unavailable");
            siteInfoMenu.Items.Add(localReaderItem);
            ApplyMenuPalette(siteInfoMenu, palette);
            siteInfoMenu.Show(
                siteInfoButton,
                new Point(0, siteInfoButton.Height),
                ToolStripDropDownDirection.BelowLeft);
            return;
        }

        var muteItem = new ToolStripMenuItem(IsTabMuted(tab) ? "Unmute site" : "Mute site");
        muteItem.Click += (_, _) => ToggleSiteMute(tab);
        siteInfoMenu.Items.Add(muteItem);
        var keepAwakeItem = new ToolStripMenuItem("Keep active in background")
        {
            Checked = tab.KeepAwake
        };
        keepAwakeItem.Click += (_, _) => ToggleTabKeepAwake(tab);
        siteInfoMenu.Items.Add(keepAwakeItem);
        var darkExcluded = WebsiteThemePolicy.IsHostExcluded(tab.Url, state.DarkModeExcludedHosts);
        var darkExcludeItem = new ToolStripMenuItem("Exclude site from dark mode")
        {
            Checked = darkExcluded
        };
        darkExcludeItem.Click += (_, _) => ToggleSiteDarkModeExclusion(uri.Host);
        siteInfoMenu.Items.Add(darkExcludeItem);
        var cleanLink = new ToolStripMenuItem("Copy clean page link");
        cleanLink.Click += (_, _) => CopyCleanPageLink();
        siteInfoMenu.Items.Add(cleanLink);
        var readerItem = new ToolStripMenuItem(tab.ReaderModeActive ? "Exit reader mode" : "Enter reader mode");
        readerItem.Enabled = tab.Core is not null;
        readerItem.Click += (_, _) => RunUiTask(ToggleReaderModeAsync, "Reader mode unavailable");
        siteInfoMenu.Items.Add(readerItem);
        siteInfoMenu.Items.Add(new ToolStripSeparator());
        var microphonePermission = new ToolStripMenuItem("Microphone: checking\u2026") { Enabled = false };
        var cameraPermission = new ToolStripMenuItem("Camera: checking\u2026") { Enabled = false };
        siteInfoMenu.Items.Add(microphonePermission);
        siteInfoMenu.Items.Add(cameraPermission);
        if (tab.Core is not null)
        {
            RunUiTask(
                () => PopulateMediaPermissionMenusAsync(
                    tab,
                    uri,
                    microphonePermission,
                    cameraPermission),
                "Could not read camera and microphone permissions");
        }
        var windowsPrivacy = new ToolStripMenuItem("Windows camera and microphone settings");
        windowsPrivacy.DropDown.Renderer = menuRenderer;
        NativeUiTheme.ApplyMenuWindowFrame(windowsPrivacy.DropDown, palette.WindowFrameBorder);
        var microphoneSettings = new ToolStripMenuItem("Microphone privacy settings");
        microphoneSettings.Click += (_, _) => OpenWindowsPrivacySettings("microphone");
        var cameraSettings = new ToolStripMenuItem("Camera privacy settings");
        cameraSettings.Click += (_, _) => OpenWindowsPrivacySettings("webcam");
        windowsPrivacy.DropDownItems.Add(microphoneSettings);
        windowsPrivacy.DropDownItems.Add(cameraSettings);
        siteInfoMenu.Items.Add(windowsPrivacy);
        var permissions = new ToolStripMenuItem("Review saved permissions");
        permissions.Click += (_, _) => ShowPermissionManager();
        siteInfoMenu.Items.Add(permissions);
        var voiceAudio = new ToolStripMenuItem("Voice and audio settings\u2026");
        voiceAudio.Click += (_, _) => ShowVoiceAudioSettingsDialog();
        siteInfoMenu.Items.Add(voiceAudio);
        var resetPreferences = new ToolStripMenuItem("Reset site preferences");
        resetPreferences.Click += (_, _) => ResetSitePreferences(uri);
        siteInfoMenu.Items.Add(resetPreferences);
        siteInfoMenu.Items.Add(new ToolStripSeparator());

        var resetPermissions = new ToolStripMenuItem("Reset saved permissions for this site")
        {
            ToolTipText = "Current scheme, host, and port only"
        };
        resetPermissions.Click += (_, _) => RunUiTask(
            () => ResetSitePermissionsAsync(uri),
            "Could not reset saved permissions");
        siteInfoMenu.Items.Add(resetPermissions);
        ApplyMenuPalette(siteInfoMenu, palette);
        siteInfoMenu.Show(siteInfoButton, new Point(0, siteInfoButton.Height), ToolStripDropDownDirection.BelowLeft);
    }

    private async Task PopulateMediaPermissionMenusAsync(
        BrowserTab tab,
        Uri uri,
        ToolStripMenuItem microphoneMenu,
        ToolStripMenuItem cameraMenu)
    {
        var core = tab.Core;
        var origin = NormalizePermissionOrigin(uri.GetLeftPart(UriPartial.Authority));
        if (core is null || origin is null) return;

        var microphoneState = CoreWebView2PermissionState.Default;
        var cameraState = CoreWebView2PermissionState.Default;
        var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
        for (var index = 0; index < settings.Count; index++)
        {
            var setting = settings[index];
            if (!setting.PermissionOrigin.Equals(origin, StringComparison.OrdinalIgnoreCase)) continue;
            if (setting.PermissionKind == CoreWebView2PermissionKind.Microphone)
            {
                microphoneState = setting.PermissionState;
            }
            else if (setting.PermissionKind == CoreWebView2PermissionKind.Camera)
            {
                cameraState = setting.PermissionState;
            }
        }

        if (isClosing
            || tab.IsClosed
            || tab.Core != core
            || activeTab != tab
            || microphoneMenu.Owner != siteInfoMenu
            || cameraMenu.Owner != siteInfoMenu)
        {
            return;
        }

        ConfigureMediaPermissionMenu(
            tab,
            origin,
            microphoneMenu,
            CoreWebView2PermissionKind.Microphone,
            microphoneState,
            tab.MicrophoneAllowedOrigins.Contains(origin));
        ConfigureMediaPermissionMenu(
            tab,
            origin,
            cameraMenu,
            CoreWebView2PermissionKind.Camera,
            cameraState,
            tab.CameraAllowedOrigins.Contains(origin));
    }

    private void ConfigureMediaPermissionMenu(
        BrowserTab tab,
        string origin,
        ToolStripMenuItem menu,
        CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState state,
        bool temporarilyAllowed)
    {
        var name = kind == CoreWebView2PermissionKind.Microphone ? "Microphone" : "Camera";
        var stateLabel = state switch
        {
            CoreWebView2PermissionState.Allow => "Allow",
            CoreWebView2PermissionState.Deny => "Block",
            _ when temporarilyAllowed => "Allow once",
            _ => "Ask"
        };
        menu.Text = $"{name}: {stateLabel}";
        menu.Enabled = true;
        ClearAndDisposeMenuItems(menu.DropDownItems);

        AddSitePermissionChoice(menu, tab, origin, kind, "Allow", CoreWebView2PermissionState.Allow,
            state == CoreWebView2PermissionState.Allow);
        AddSitePermissionChoice(menu, tab, origin, kind, "Ask every time", CoreWebView2PermissionState.Default,
            state == CoreWebView2PermissionState.Default && !temporarilyAllowed);
        AddSitePermissionChoice(menu, tab, origin, kind, "Block", CoreWebView2PermissionState.Deny,
            state == CoreWebView2PermissionState.Deny);
    }

    private void AddSitePermissionChoice(
        ToolStripMenuItem parent,
        BrowserTab tab,
        string origin,
        CoreWebView2PermissionKind kind,
        string label,
        CoreWebView2PermissionState state,
        bool isChecked)
    {
        var item = new ToolStripMenuItem(label) { Checked = isChecked };
        item.Click += (_, _) =>
        {
            siteInfoMenu.Close();
            RunUiTask(
                () => SetSiteMediaPermissionAsync(tab, origin, kind, state),
                $"Could not update {GetPermissionDisplayName(kind)} access");
        };
        parent.DropDownItems.Add(item);
    }

    private async Task SetSiteMediaPermissionAsync(
        BrowserTab tab,
        string origin,
        CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState state)
    {
        var core = tab.Core;
        if (core is null
            || tab.IsClosed
            || !origin.Equals(NormalizePermissionOrigin(core.Source), StringComparison.OrdinalIgnoreCase))
        {
            ShowTransientStatus("The page changed before its permissions could be updated");
            return;
        }

        await core.Profile.SetPermissionStateAsync(kind, origin, state);
        // The profile mutation is shared by every live tab, even if the tab
        // that opened the menu navigated while the asynchronous write ran.
        UpdateLiveTabsForMediaPermission(origin, kind, state);
        if (tab.Core != core
            || tab.IsClosed
            || !origin.Equals(NormalizePermissionOrigin(core.Source), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var permission = GetPermissionDisplayName(kind);
        var message = state switch
        {
            CoreWebView2PermissionState.Allow => $"{permission} allowed for {origin}; retry the call",
            CoreWebView2PermissionState.Deny => $"{permission} blocked for {origin}",
            _ => $"{permission} will ask again for {origin}"
        };
        ShowTransientStatus(message);
    }

    private void OpenWindowsPrivacySettings(string page)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"ms-settings:privacy-{page}") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            stateStore.Log("Could not open Windows privacy settings", error);
            ShowTransientStatus("Open Windows Settings > Privacy & security to check device access");
        }
    }

    private async Task ResetSitePermissionsAsync(Uri uri)
    {
        var core = activeTab?.Core;
        if (core is null)
        {
            ShowTransientStatus("Site permissions are unavailable while the browser is stopped");
            return;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        try
        {
            var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            for (var index = 0; index < settings.Count; index++)
            {
                var setting = settings[index];
                if (setting.PermissionOrigin.Equals(origin, StringComparison.OrdinalIgnoreCase))
                {
                    await core.Profile.SetPermissionStateAsync(
                        setting.PermissionKind,
                        setting.PermissionOrigin,
                        CoreWebView2PermissionState.Default);
                }
            }
            foreach (var tab in tabs.Where(item =>
                         !item.IsClosed && TabContainsMediaOrigin(item, origin)))
            {
                ApplyTabMediaPermissions(tab, origin, microphoneAllowed: false, cameraAllowed: false);
            }
            ShowTransientStatus($"Saved permissions reset for {origin}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not reset saved permissions", error);
            ShowTransientStatus("Saved permissions could not be reset for this site");
        }
    }

    private void ShowVoiceAudioSettingsDialog()
    {
        var activeHost = activeTab is not null && !activeTab.IsStartPage
            ? SitePreferencePolicy.GetHost(activeTab.Url)
            : null;

        using var dialog = new VoiceAudioSettingsDialog(
            state,
            activeHost,
            result =>
            {
                state.AudioOutputPolicy = result.AudioOutputPolicy;
                state.AllowedAudioHosts = result.AllowedAudioHosts.ToList();
                state.MutedHosts = result.MutedHosts.ToList();
                state.AudioInputPolicy = result.AudioInputPolicy;
                state.AllowedMicrophoneHosts = result.AllowedMicrophoneHosts.ToList();
                state.BlockedMicrophoneHosts = result.BlockedMicrophoneHosts.ToList();

                ApplyAudioOutputPolicyToAllTabs();
                ApplyAudioInputPolicyToAllTabs();
                ScheduleStateSave();

                ShowTransientStatus("Voice and audio settings updated");
            });

        dialog.ShowDialog(this);
    }

    private void ApplyAudioOutputPolicyToAllTabs()
    {
        foreach (var liveTab in tabs.Where(item => !item.IsClosed && !item.IsStartPage))
        {
            var shouldMute = isPrivateMode
                ? liveTab.IsMuted
                : SitePreferencePolicy.ShouldMuteAudio(state.AudioOutputPolicy, state.AllowedAudioHosts, state.MutedHosts, liveTab.Url);
            liveTab.IsMuted = shouldMute;
            if (liveTab.Core is not null) liveTab.Core.IsMuted = shouldMute;
            UpdateTabHeader(liveTab);
        }
    }

    private void ApplyAudioInputPolicyToAllTabs()
    {
        foreach (var liveTab in tabs.Where(item => !item.IsClosed && !item.IsStartPage))
        {
            var host = SitePreferencePolicy.GetHost(liveTab.Url);
            if (state.AudioInputPolicy == AudioInputPolicy.BlockAll)
            {
                ClearTabMediaPermissions(liveTab);
            }
            else if (state.AudioInputPolicy == AudioInputPolicy.SpecificSitesOnly)
            {
                if (host is null || !state.AllowedMicrophoneHosts.Any(h => BrowserPolicy.IsExactHost(h, host)))
                {
                    var changed = liveTab.MicrophoneAllowedOrigins.Count > 0;
                    liveTab.MicrophoneAllowedOrigins.Clear();
                    if (changed) OnTabMediaPermissionStateChanged(liveTab);
                }
            }
        }
    }

    private void ShowPermissionManager()
    {
        var core = activeTab?.Core ?? tabs.Select(item => item.Core).FirstOrDefault(item => item is not null);
        if (core is null)
        {
            ShowTransientStatus("Permission management is unavailable until a website is open");
            return;
        }
        permissionManager ??= CreatePermissionManager();
        RunUiTask(
            () => LoadPermissionManagerAsync(core),
            "Could not open the permission manager");
    }

    private PermissionManagerForm CreatePermissionManager()
    {
        var manager = new PermissionManagerForm();
        manager.ActionRequested += (_, action) => RunUiTask(
            () => HandlePermissionManagerActionAsync(action),
            "Could not update saved permissions");
        return manager;
    }

    private async Task LoadPermissionManagerAsync(CoreWebView2 core)
    {
        var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
        var rows = new List<PermissionSettingRow>(settings.Count);
        for (var index = 0; index < settings.Count; index++)
        {
            var setting = settings[index];
            rows.Add(new PermissionSettingRow(
                setting.PermissionOrigin,
                GetPermissionDisplayName(setting.PermissionKind),
                setting.PermissionState == CoreWebView2PermissionState.Allow ? "Allow" : "Block",
                setting));
        }
        permissionManager!.Open(this, rows);
    }

    private async Task HandlePermissionManagerActionAsync(PermissionManagerAction action)
    {
        var core = activeTab?.Core ?? tabs.Select(item => item.Core).FirstOrDefault(item => item is not null);
        var selected = permissionManager?.SelectedRow;
        if (core is null) return;
        if (action == PermissionManagerAction.ResetAll)
        {
            if (!NativeAlertDialog.Confirm(
                    this,
                    "Reset all permissions?",
                    "Reset every saved website permission?",
                    confirmText: "Reset all",
                    cancelText: "Cancel"))
            {
                return;
            }
            var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            for (var index = 0; index < settings.Count; index++)
            {
                var setting = settings[index];
                await core.Profile.SetPermissionStateAsync(
                    setting.PermissionKind,
                    setting.PermissionOrigin,
                    CoreWebView2PermissionState.Default);
            }
        }
        else if (selected?.Token is CoreWebView2PermissionSetting setting)
        {
            await core.Profile.SetPermissionStateAsync(
                setting.PermissionKind,
                setting.PermissionOrigin,
                CoreWebView2PermissionState.Default);
        }
        await RefreshLiveTabMediaPermissionsAsync();
        await LoadPermissionManagerAsync(core);
        ShowTransientStatus("Saved permissions updated");
    }

    private static string? NormalizePermissionOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        var host = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{uri.IdnHost}]"
            : uri.IdnHost;
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{host}{port}";
    }

    private static string GetPermissionDisplayName(CoreWebView2PermissionKind kind)
    {
        return kind switch
        {
            CoreWebView2PermissionKind.Microphone => "microphone",
            CoreWebView2PermissionKind.Camera => "camera",
            CoreWebView2PermissionKind.Geolocation => "location",
            CoreWebView2PermissionKind.Notifications => "notifications",
            CoreWebView2PermissionKind.ClipboardRead => "clipboard read",
            CoreWebView2PermissionKind.MultipleAutomaticDownloads => "multiple downloads",
            CoreWebView2PermissionKind.FileReadWrite => "file access",
            CoreWebView2PermissionKind.Autoplay => "autoplay",
            CoreWebView2PermissionKind.LocalFonts => "local fonts",
            CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "MIDI devices",
            CoreWebView2PermissionKind.WindowManagement => "window management",
            CoreWebView2PermissionKind.PersistentStorage => "persistent storage",
            _ => kind.ToString()
        };
    }

    private void ShowAbout()
    {
        var runtimeVersion = environment?.BrowserVersionString ?? "not running";
        var resourceMode = ultraLightEnabled ? "Ultra-light" : memorySaverEnabled ? "Memory saver" : "Off";
        NativeAlertDialog.ShowInfo(
            this,
            "About MishaWeb",
            $"MishaWeb {Application.ProductVersion}\n\nA resource-light native Windows browser powered by WebView2.\n\nWebView2 Runtime: {runtimeVersion}\nTabs: {tabs.Count}\nResource mode: {resourceMode}\n\nKeyboard help: Ctrl+L, Ctrl+T, Ctrl+W, Ctrl+Shift+T, Ctrl+Tab, Ctrl+F, Ctrl+D, Ctrl+M, Ctrl+H, Ctrl+Shift+O, Ctrl+P, Ctrl+Shift+S, F11.");
    }

    private void ShowSessionManager()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Sessions are unavailable in a private window");
            return;
        }

        sessionManager ??= CreateSessionManager();
        sessionManager.Open(this, state.NamedSessions);
    }

    private SessionManagerForm CreateSessionManager()
    {
        var manager = new SessionManagerForm();
        manager.ActionRequested += (_, request) => HandleSessionManagerRequest(request);
        return manager;
    }

    private void HandleSessionManagerRequest(SessionManagerRequest request)
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Sessions are unavailable in a private window");
            return;
        }

        switch (request.Action)
        {
            case SessionManagerAction.NewFromCurrent:
                SaveCurrentWindowAsSession();
                break;
            case SessionManagerAction.Open when request.Session is not null:
                OpenSavedSession(request.Session, replace: false);
                break;
            case SessionManagerAction.Replace when request.Session is not null:
                OpenSavedSession(request.Session, replace: true);
                break;
            case SessionManagerAction.Update when request.Session is not null:
                UpdateSavedSession(request.Session);
                break;
            case SessionManagerAction.Rename when request.Session is not null:
                RenameSavedSession(request.Session);
                break;
            case SessionManagerAction.Delete when request.Session is not null:
                DeleteSavedSession(request.Session);
                break;
        }
    }

    private void SaveCurrentWindowAsSession()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Sessions are unavailable in a private window");
            return;
        }
        if (!TextPromptDialog.TryShow(this, "Save session", "Session name:", "", out var name)) return;
        SaveCurrentWindowAsSession(name, allowOverwrite: false);
    }

    private void SaveCurrentWindowAsSession(string name, bool allowOverwrite)
    {
        var normalizedName = BrowserStateStore.NormalizeSessionName(name);
        if (normalizedName is null)
        {
            ShowTransientStatus("Enter a nonblank session name up to 80 characters");
            return;
        }

        var session = CreateSessionFromCurrent(normalizedName);
        if (session is null)
        {
            ShowTransientStatus("There are no safe tabs to save");
            return;
        }

        var existingIndex = state.NamedSessions.FindIndex(item =>
            item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0 && !allowOverwrite)
        {
            if (!NativeAlertDialog.Confirm(
                    this,
                    "Replace saved session?",
                    $"Replace the existing '{state.NamedSessions[existingIndex].Name}' session?",
                    confirmText: "Replace",
                    cancelText: "Cancel"))
            {
                return;
            }
        }

        if (existingIndex >= 0) state.NamedSessions.RemoveAt(existingIndex);
        else if (state.NamedSessions.Count >= BrowserStateStore.MaximumNamedSessions)
        {
            ShowTransientStatus("You can save up to 20 named sessions");
            return;
        }
        state.NamedSessions.Insert(0, session);
        BrowserStateStore.NormalizeForPersistence(state);
        ScheduleStateSave();
        sessionManager?.SetSessions(state.NamedSessions);
        ShowTransientStatus($"Session saved: {session.Name}");
    }

    private SavedSessionEntry? CreateSessionFromCurrent(string name)
    {
        var captured = tabs
            .Where(tab => !tab.IsClosed && (tab.IsStartPage || BrowserPolicy.IsHttpUrl(tab.Url)))
            .Take(BrowserStateStore.MaximumSessionTabs)
            .Select(tab => new SavedSessionTab(
                tab.Url,
                tab.IsPinned && !tab.IsStartPage))
            .ToList();
        if (captured.Count == 0) return null;

        var activeIndex = activeTab is null
            ? null
            : tabs
                .Where(tab => !tab.IsClosed && (tab.IsStartPage || BrowserPolicy.IsHttpUrl(tab.Url)))
                .Take(BrowserStateStore.MaximumSessionTabs)
                .ToList()
                .FindIndex(tab => tab == activeTab) is var index && index >= 0
                    ? index
                    : (int?)null;
        return new SavedSessionEntry(name, DateTimeOffset.UtcNow, captured, activeIndex);
    }

    private void OpenSavedSession(SavedSessionEntry session, bool replace)
    {
        if (isPrivateMode) return;
        var normalized = state.NamedSessions.FirstOrDefault(item =>
            item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (normalized is null) return;

        var capacity = replace ? MaximumTabs : MaximumTabs - tabs.Count;
        if (capacity <= 0)
        {
            ShowTransientStatus("There is no room for another session");
            return;
        }

        if (replace)
        {
            var activeDownloadCount = downloads.Count(item => !item.IsTerminal);
            if (activeDownloadCount > 0)
            {
                ShowTransientStatus("Finish or cancel downloads before replacing this window");
                ShowDownloadsPopup();
                return;
            }
            if (!NativeAlertDialog.Confirm(
                    this,
                    "Replace current window?",
                    $"Replace the current {tabs.Count} tabs with '{normalized.Name}'? Current tabs will not be added to Recently closed.",
                    confirmText: "Replace",
                    cancelText: "Cancel"))
            {
                return;
            }
            replacingWindow = true;
            try
            {
                foreach (var tab in tabs.ToArray())
                {
                    CloseTab(tab, remember: false, ensureReplacement: false);
                }
            }
            finally
            {
                replacingWindow = false;
            }
        }

        RunUiTask(
            () => AppendSessionTabsAsync(normalized, capacity),
            "Could not open the saved session");
    }

    private async Task AppendSessionTabsAsync(SavedSessionEntry session, int capacity)
    {
        var selected = new List<BrowserTab>();
        var validTabs = session.Tabs
            .Where(tab => tab is not null && BrowserPolicy.IsSafeTopLevelUrl(tab.Url))
            .Take(capacity)
            .ToList();
        for (var index = 0; index < validTabs.Count; index++)
        {
            if (isClosing) return;
            var saved = validTabs[index];
            var tab = await OpenNewTabAsync(
                saved.Url,
                activate: false,
                isPinned: saved.IsPinned);
            if (tab is not null) selected.Add(tab);
        }

        if (selected.Count == 0)
        {
            await OpenNewTabAsync(StartPage.Url);
            ShowTransientStatus("The saved session had no valid tabs");
            return;
        }

        var target = session.ActiveTabIndex is int active
            && active >= 0
            && active < selected.Count
                ? selected[active]
                : selected[0];
        ActivateTab(target, focusPage: false);
        var skipped = Math.Max(0, session.Tabs.Count - selected.Count);
        ShowTransientStatus(skipped == 0
            ? $"Opened session: {session.Name}"
            : $"Opened session: {session.Name}; skipped {skipped} tab(s) at the limit");
    }

    private void UpdateSavedSession(SavedSessionEntry session)
    {
        var index = state.NamedSessions.FindIndex(item =>
            item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var updated = CreateSessionFromCurrent(state.NamedSessions[index].Name);
        if (updated is null) return;
        state.NamedSessions[index] = updated;
        BrowserStateStore.NormalizeForPersistence(state);
        ScheduleStateSave();
        sessionManager?.SetSessions(state.NamedSessions);
        ShowTransientStatus($"Session updated: {updated.Name}");
    }

    private void RenameSavedSession(SavedSessionEntry session)
    {
        if (!TextPromptDialog.TryShow(this, "Rename session", "New name:", session.Name, out var name)) return;
        var normalizedName = BrowserStateStore.NormalizeSessionName(name);
        if (normalizedName is null)
        {
            ShowTransientStatus("Enter a nonblank session name up to 80 characters");
            return;
        }

        var sourceIndex = state.NamedSessions.FindIndex(item =>
            item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0) return;
        var existingIndex = state.NamedSessions.FindIndex(item =>
            item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)
            && !item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (!NativeAlertDialog.Confirm(
                    this,
                    "Replace saved session?",
                    $"Replace the existing '{state.NamedSessions[existingIndex].Name}' session?",
                    confirmText: "Replace",
                    cancelText: "Cancel"))
            {
                return;
            }
            state.NamedSessions.RemoveAt(existingIndex);
            if (existingIndex < sourceIndex) sourceIndex--;
        }

        state.NamedSessions[sourceIndex] = state.NamedSessions[sourceIndex] with
        {
            Name = normalizedName,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        BrowserStateStore.NormalizeForPersistence(state);
        ScheduleStateSave();
        sessionManager?.SetSessions(state.NamedSessions);
    }

    private void DeleteSavedSession(SavedSessionEntry session)
    {
        if (!NativeAlertDialog.Confirm(
                this,
                "Delete saved session?",
                $"Delete the '{session.Name}' session?",
                confirmText: "Delete",
                cancelText: "Cancel"))
        {
            return;
        }
        state.NamedSessions.RemoveAll(item =>
            item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        ScheduleStateSave();
        sessionManager?.SetSessions(state.NamedSessions);
        ShowTransientStatus("Session deleted");
    }

    private void SetPinnedStartPageLink(StartPageLink link, bool pin)
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Quick links are unavailable in a private window");
            return;
        }

        var existing = state.PinnedStartPageLinks.FindIndex(item => BrowserPolicy.UrlEquals(item.Url, link.Url));
        if (pin)
        {
            if (existing >= 0)
            {
                ShowTransientStatus("This page is already pinned");
                return;
            }
            if (state.PinnedStartPageLinks.Count >= BrowserStateStore.MaximumPinnedStartPageLinks)
            {
                ShowTransientStatus("You can pin up to three quick links");
                return;
            }
            state.PinnedStartPageLinks.Insert(0, new PinnedStartPageLink(link.Title, link.Url));
            ShowTransientStatus("Quick link pinned");
        }
        else if (existing >= 0)
        {
            state.PinnedStartPageLinks.RemoveAt(existing);
            ShowTransientStatus("Quick link unpinned");
        }
        else
        {
            return;
        }

        BrowserStateStore.NormalizeForPersistence(state);
        InvalidateStartPageLinks();
        RefreshNativeStartPages(refreshLinks: true);
        ScheduleStateSave();
    }

    private static void ClearAndDisposeMenuItems(ToolStripItemCollection items, int startIndex = 0)
    {
        for (var index = items.Count - 1; index >= startIndex; index--)
        {
            var item = items[index];
            items.RemoveAt(index);
            if (item is ToolStripDropDownItem dropDownItem && dropDownItem.HasDropDownItems)
            {
                ClearAndDisposeMenuItems(dropDownItem.DropDownItems);
            }
            var image = item.Image;
            if (image is not null)
            {
                item.Image = null;
                image.Dispose();
            }
            item.Dispose();
        }
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

    private void ShowTabListMenu()
    {
        ClearAndDisposeMenuItems(tabListMenu.Items);
        tabListMenu.ShowImageMargin = true;
        var searchBox = new ToolStripTextBox
        {
            AutoSize = false,
            Width = Math.Max(220, tabListButton.Width * 6),
            ToolTipText = "Filter tab titles and addresses",
            AccessibleName = "Filter open tabs"
        };
        var separator = new ToolStripSeparator();
        tabListMenu.Items.Add(searchBox);
        tabListMenu.Items.Add(separator);

        void Populate(string query)
        {
            ClearAndDisposeMenuItems(tabListMenu.Items, startIndex: 2);
            BrowserTab[] matchingTabs;
            if (query.Length == 0)
            {
                matchingTabs = tabs.ToArray();
            }
            else
            {
                var mruRanks = new Dictionary<BrowserTab, int>(tabs.Count);
                var rankIndex = 0;
                foreach (var orderedTab in mruTabs.Order)
                {
                    mruRanks.TryAdd(orderedTab, rankIndex++);
                }

                var candidates = new List<CommandCandidate>(tabs.Count);
                for (var index = 0; index < tabs.Count; index++)
                {
                    var tab = tabs[index];
                    if (tab.IsClosed) continue;
                    candidates.Add(new CommandCandidate(
                        tab.Title,
                        BuildTabDetail(tab),
                        CommandSource.OpenTab,
                        tab.IsStartPage ? null : tab.Url,
                        MruRank: mruRanks.TryGetValue(tab, out var rank) ? rank : int.MaxValue,
                        SourceIndex: index,
                        Target: tab));
                }

                matchingTabs = CommandRankingEngine.Rank(candidates, query, MaximumTabs)
                    .Select(row => row.Candidate.Target)
                    .OfType<BrowserTab>()
                    .Where(tab => !tab.IsClosed)
                    .ToArray();
            }

            if (matchingTabs.Length == 0)
            {
                tabListMenu.Items.Add(new ToolStripMenuItem(
                    query.Length == 0 ? "No open tabs" : "No tabs match this filter") { Enabled = false });
                return;
            }

            var iconSize = ScaleChromeLogical(16);
            foreach (var tab in matchingTabs)
            {
                var stateSuffix = BuildTabMenuStateSuffix(tab);
                var label = TrimMenuText(tab.Title, 38) + stateSuffix;
                var icon = CreateTabMenuIcon(tab, iconSize);
                var item = new ToolStripMenuItem(label, icon)
                {
                    Checked = tab == activeTab,
                    ToolTipText = tab.IsStartPage ? (isPrivateMode ? "Incognito" : "New tab") : BuildTabDetail(tab),
                    AccessibleName = $"{tab.Title} tab"
                };
                item.Click += (_, _) => ActivateTab(tab);
                tabListMenu.Items.Add(item);
            }
        }

        searchBox.TextChanged += (_, _) => Populate(searchBox.Text.Trim());
        Populate(string.Empty);
        ApplyMenuPalette(tabListMenu, ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode));
        tabListMenu.Show(tabListButton, new Point(tabListButton.Width, tabListButton.Height), ToolStripDropDownDirection.BelowLeft);
        searchBox.Focus();
    }

    private string BuildTabMenuStateSuffix(BrowserTab tab)
    {
        var muted = IsTabMuted(tab);
        var parts = new List<string>(3);
        if (tab.IsAudible && !muted) parts.Add("Playing audio");
        else if (muted) parts.Add("Muted");
        if (tab.IsPinned) parts.Add("Pinned");
        if (tab.IsDiscarded) parts.Add("Unloaded");
        else if (tab.IsSuspended) parts.Add("Sleeping");
        return parts.Count == 0 ? string.Empty : " \u00B7 " + string.Join(" \u00B7 ", parts);
    }

    private Bitmap? CreateTabMenuIcon(BrowserTab tab, int targetSize)
    {
        var size = Math.Max(16, targetSize);
        var isWebTab = !tab.IsStartPage && BrowserPolicy.IsHttpUrl(tab.Url);
        if (isWebTab)
        {
            var hostKey = GetFaviconCacheKey(tab.Url);
            if (hostKey is not null)
            {
                using var copy = GetCachedFaviconCopy(hostKey, out _);
                if (copy is not null)
                {
                    return RenderRoundedMenuIcon(copy, size);
                }
            }
        }

        var monogram = isWebTab
            ? BuildFaviconMonogram(tab.Url) ?? "W"
            : (isPrivateMode ? "I" : "M");
        return RenderMonogramMenuIcon(monogram, size);
    }

    private static Bitmap RenderRoundedMenuIcon(Bitmap source, int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var clipPath = NativeUiTheme.CreateRoundedPath(new RectangleF(0f, 0f, size, size), size * 0.25f);
            g.SetClip(clipPath, CombineMode.Intersect);
            g.DrawImage(source, new Rectangle(0, 0, size, size));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private Bitmap RenderMonogramMenuIcon(string monogram, int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var bounds = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
            using var pillPath = NativeUiTheme.CreateRoundedPath(bounds, size * 0.3f);
            if (IsHighContrastActive)
            {
                g.DrawPath(NativeUiTheme.GetSharedPen(SystemColors.MenuText, 1f), pillPath);
            }
            else
            {
                var top = isPrivateMode ? Color.FromArgb(186, 120, 56, 174) : Color.FromArgb(186, 142, 54, 112);
                var bottom = isPrivateMode ? Color.FromArgb(204, 66, 26, 98) : Color.FromArgb(204, 72, 24, 64);
                using var fill = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical);
                g.FillPath(fill, pillPath);
                g.DrawPath(
                    NativeUiTheme.GetSharedPen(Color.FromArgb(155, isPrivateMode ? NativeUiTheme.Lavender : NativeUiTheme.Focus), 1f),
                    pillPath);
            }
            var font = NativeUiTheme.GetSharedFontForDpi("Segoe UI Semibold", 8f, Math.Max(96, DeviceDpi), FontStyle.Bold);
            TextRenderer.DrawText(
                g,
                monogram,
                font,
                new Rectangle(0, 0, size, size),
                IsHighContrastActive ? SystemColors.MenuText : NativeUiTheme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private Image? GetCachedExtensionMenuIcon(string iconFullPath, int targetSize)
    {
        if (extensionMenuIconCache.TryGetValue(iconFullPath, out var cached))
        {
            if (cached.Size == targetSize) return cached.Icon;
            cached.Icon.Dispose();
            extensionMenuIconCache.Remove(iconFullPath);
        }

        if (!File.Exists(iconFullPath)) return null;
        using var decoded = TryLoadUnlockedBitmap(iconFullPath);
        if (decoded is null) return null;

        var scaled = new Bitmap(targetSize, targetSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(scaled);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(decoded, new Rectangle(0, 0, targetSize, targetSize));
        }
        catch
        {
            scaled.Dispose();
            throw;
        }

        extensionMenuIconCache[iconFullPath] = (targetSize, scaled);
        return scaled;
    }

    private void PruneExtensionMenuIconCache(HashSet<string> activePaths, int targetSize)
    {
        if (extensionMenuIconCache.Count == 0) return;
        foreach (var key in extensionMenuIconCache.Keys.ToArray())
        {
            if (activePaths.Contains(key) && extensionMenuIconCache[key].Size == targetSize) continue;
            extensionMenuIconCache[key].Icon.Dispose();
            extensionMenuIconCache.Remove(key);
        }
    }

    private void ClearExtensionMenuIconCache()
    {
        if (extensionMenuIconCache.Count == 0) return;
        foreach (var entry in extensionMenuIconCache.Values)
        {
            entry.Icon.Dispose();
        }
        extensionMenuIconCache.Clear();
    }

    private static bool DoesDownloadFileExist(DownloadEntry download)
    {
        if (string.IsNullOrWhiteSpace(download.FilePath)) return false;
        var nowTick = Environment.TickCount64;
        if (download.IsTerminal
            && download.LastKnownFileExists is { } cached
            && nowTick - download.LastFileExistsCheckedAtTick < 30_000)
        {
            return cached;
        }
        var exists = File.Exists(download.FilePath);
        download.LastKnownFileExists = exists;
        download.LastFileExistsCheckedAtTick = nowTick;
        return exists;
    }

    private void EnsureAppSubmenusLazyWired()
    {
        if (appSubmenusLazyWired) return;
        appSubmenusLazyWired = true;

        favoritesMenu.DropDownOpening += (_, _) =>
        {
            if (!favoritesSubmenuDirty) return;
            favoritesSubmenuDirty = false;
            PopulateFavoritesSubmenu();
        };
        historyMenu.DropDownOpening += (_, _) =>
        {
            if (!historySubmenuDirty) return;
            historySubmenuDirty = false;
            PopulateHistorySubmenu();
        };
        downloadsMenu.DropDownOpening += (_, _) =>
        {
            if (!downloadsSubmenuDirty) return;
            downloadsSubmenuDirty = false;
            PopulateDownloadsSubmenu();
        };
        searchProviderMenu.DropDownOpening += (_, _) =>
        {
            if (!searchProviderSubmenuDirty) return;
            searchProviderSubmenuDirty = false;
            PopulateSearchProviderMenu();
        };
        extensionsMenuItem.DropDownOpening += (_, _) =>
        {
            if (!extensionsSubmenuDirty) return;
            extensionsSubmenuDirty = false;
            PopulateExtensionsSubmenu();
        };
    }

    private static void SeedLazySubmenuPlaceholder(ToolStripMenuItem submenu)
    {
        if (submenu.DropDownItems.Count == 0)
        {
            submenu.DropDownItems.Add(new ToolStripMenuItem("Loading\u2026") { Enabled = false });
        }
    }

    private void PopulateAppMenu()
    {
        if (!extensionIconCacheCleanupWired)
        {
            extensionIconCacheCleanupWired = true;
            appMenu.Disposed += (_, _) => ClearExtensionMenuIconCache();
        }
        EnsureAppSubmenusLazyWired();

        extensionsMenuItem.Enabled = !isPrivateMode;
        extensionsMenuItem.ToolTipText = isPrivateMode
            ? "Extensions are unavailable in private windows"
            : "Install and manage browser extensions";
        if (isPrivateMode)
        {
            foreach (ToolStripItem existingItem in extensionsMenuItem.DropDownItems)
            {
                existingItem.Image = null;
            }
            ClearAndDisposeMenuItems(extensionsMenuItem.DropDownItems);
            extensionsSubmenuDirty = false;
        }
        else
        {
            extensionsSubmenuDirty = true;
            SeedLazySubmenuPlaceholder(extensionsMenuItem);
        }

        searchProviderMenu.Text = $"Search engine: {BrowserPolicy.GetSearchProviderName(searchProviderId)}";
        searchProviderSubmenuDirty = true;
        SeedLazySubmenuPlaceholder(searchProviderMenu);

        favoritesSubmenuDirty = true;
        SeedLazySubmenuPlaceholder(favoritesMenu);

        historySubmenuDirty = true;
        SeedLazySubmenuPlaceholder(historyMenu);

        downloadsSubmenuDirty = true;
        SeedLazySubmenuPlaceholder(downloadsMenu);

        if (zoomMenu.DropDownItems.Count == 0)
        {
            zoomMenu.DropDownItems.Add("Zoom out", null, (_, _) => ChangeZoom(-0.10));
            zoomMenu.DropDownItems.Add("Reset to 100%", null, (_, _) => ChangeZoom(0, true));
            zoomMenu.DropDownItems.Add("Zoom in", null, (_, _) => ChangeZoom(0.10));
        }

        adBlockMenuItem.Checked = adBlockEnabled;
        adBlockMenuItem.Text = adBlockEnabled ? "Ad and tracker blocker: on" : "Ad and tracker blocker: off";
        var resourceMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        resourceOffMenuItem.Checked = resourceMode == TabLifecycleMode.Off;
        memorySaverMenuItem.Checked = resourceMode == TabLifecycleMode.Standard;
        ultraLightMenuItem.Checked = resourceMode == TabLifecycleMode.Ultra;
        resourceModeMenu.Text = resourceMode switch
        {
            TabLifecycleMode.Ultra => "Resource mode: Ultra-light",
            TabLifecycleMode.Standard => "Resource mode: Memory saver",
            _ => "Resource mode: Off"
        };
        resourceOffMenuItem.Text = "Off";
        memorySaverMenuItem.Text = "Memory saver";
        ultraLightMenuItem.Text = "Ultra-light";
        reduceMotionMenuItem.Checked = reduceWebsiteMotionEnabled;
        reduceMotionMenuItem.Text = reduceWebsiteMotionEnabled
            ? "Reduce website motion: on"
            : "Reduce website motion: off";
        siteThemeMenuItem.Checked = darkModeEnabled;
        siteThemeMenuItem.Text = darkModeEnabled ? "Website theme: dark" : "Website theme: light";
        fullScreenMenuItem.Checked = isFullScreen;
        fullScreenMenuItem.Text = isFullScreen ? "Exit fullscreen" : "Fullscreen";
        fullScreenMenuItem.ShortcutKeyDisplayString = "F11";

        favoritesMenu.Enabled = true;
        historyMenu.Enabled = true;
        downloadsMenu.Enabled = true;
    }

    private void PopulateExtensionsSubmenu()
    {
        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        foreach (ToolStripItem existingItem in extensionsMenuItem.DropDownItems)
        {
            existingItem.Image = null;
        }
        ClearAndDisposeMenuItems(extensionsMenuItem.DropDownItems);
        var manageItem = new ToolStripMenuItem("Manage extensions\u2026")
        {
            ToolTipText = "Open the extensions manager"
        };
        manageItem.Click += (_, _) => RunUiTask(
            ShowExtensionsManagerAsync,
            "Could not open the extensions manager");
        extensionsMenuItem.DropDownItems.Add(manageItem);

        if (!isPrivateMode)
        {
            var managed = browserExtensions.GetManagedExtensions();
            var iconTargetSize = ScaleChromeLogical(16);
            var activeIconPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (managed.Count > 0)
            {
                extensionsMenuItem.DropDownItems.Add(new ToolStripSeparator());
                foreach (var ext in managed.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    Image? icon = null;
                    if (ext.IconPath is not null)
                    {
                        var iconFullPath = Path.Combine(ext.FolderPath, ext.IconPath.Replace('/', Path.DirectorySeparatorChar));
                        activeIconPaths.Add(iconFullPath);
                        icon = GetCachedExtensionMenuIcon(iconFullPath, iconTargetSize);
                    }
                    var extItem = new ToolStripMenuItem(TrimMenuText(ext.Name, 42), icon);
                    extItem.DropDown.Renderer = menuRenderer;
                    NativeUiTheme.ApplyMenuWindowFrame(extItem.DropDown, palette.WindowFrameBorder);
                    var hasPopup = ext.PopupPath is not null || ext.LaunchPath is not null;
                    var hasOptions = ext.OptionsPath is not null;
                    if (hasPopup && hasOptions)
                    {
                        var popupSub = new ToolStripMenuItem("Open popup", null, (_, _) =>
                        {
                            var target = ext.PopupPath ?? ext.LaunchPath!;
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{target}", trustedExtensionId: ext.Id), "Could not open extension popup");
                        });
                        var optionsSub = new ToolStripMenuItem("Options", null, (_, _) =>
                        {
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{ext.OptionsPath}", trustedExtensionId: ext.Id), "Could not open extension options");
                        });
                        extItem.DropDownItems.Add(popupSub);
                        extItem.DropDownItems.Add(optionsSub);
                        extItem.Click += (_, _) =>
                        {
                            var target = ext.PopupPath ?? ext.LaunchPath!;
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{target}", trustedExtensionId: ext.Id), "Could not open extension");
                        };
                    }
                    else if (hasPopup)
                    {
                        extItem.Click += (_, _) =>
                        {
                            var target = ext.PopupPath ?? ext.LaunchPath!;
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{target}", trustedExtensionId: ext.Id), "Could not open extension");
                        };
                    }
                    else if (hasOptions)
                    {
                        extItem.Click += (_, _) =>
                        {
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{ext.OptionsPath}", trustedExtensionId: ext.Id), "Could not open extension options");
                        };
                    }
                    extensionsMenuItem.DropDownItems.Add(extItem);
                }
            }
            PruneExtensionMenuIconCache(activeIconPaths, iconTargetSize);
        }
        ApplyMenuPalette(extensionsMenuItem.DropDown, palette);
    }

    private void PopulateFavoritesSubmenu()
    {
        ClearAndDisposeMenuItems(favoritesMenu.DropDownItems);
        if (state.Bookmarks.Count == 0)
        {
            favoritesMenu.DropDownItems.Add(new ToolStripMenuItem("No favorites yet") { Enabled = false });
        }
        else
        {
            foreach (var bookmark in state.Bookmarks.Take(20))
            {
                var item = new ToolStripMenuItem(TrimMenuText(bookmark.Title))
                {
                    ToolTipText = FormatBrowserChromeUrlForDisplay(bookmark.Url)
                };
                item.Click += (_, _) => RunUiTask(() => NavigateActiveAsync(bookmark.Url), "Navigation could not start");
                favoritesMenu.DropDownItems.Add(item);
            }
        }
        favoritesMenu.DropDownItems.Add(new ToolStripSeparator());
        favoritesMenu.DropDownItems.Add("Add or remove this page", null, (_, _) => ToggleFavorite());
        ApplyMenuPalette(favoritesMenu.DropDown, ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode));
    }

    private void PopulateHistorySubmenu()
    {
        ClearAndDisposeMenuItems(historyMenu.DropDownItems);
        if (state.History.Count == 0)
        {
            historyMenu.DropDownItems.Add(new ToolStripMenuItem("No browsing history") { Enabled = false });
        }
        else
        {
            foreach (var history in state.History.Take(20))
            {
                var item = new ToolStripMenuItem(TrimMenuText(history.Title))
                {
                    ToolTipText = FormatBrowserChromeUrlForDisplay(history.Url)
                };
                item.Click += (_, _) => RunUiTask(() => NavigateActiveAsync(history.Url), "Navigation could not start");
                historyMenu.DropDownItems.Add(item);
            }
            historyMenu.DropDownItems.Add(new ToolStripSeparator());
            historyMenu.DropDownItems.Add("Clear history list", null, (_, _) =>
            {
                state.History.Clear();
                InvalidateStartPageLinks();
                RefreshNativeStartPages(refreshLinks: true);
                ScheduleStateSave();
                ShowTransientStatus("History list cleared");
            });
        }
        ApplyMenuPalette(historyMenu.DropDown, ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode));
    }

    private void PopulateDownloadsSubmenu()
    {
        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        ClearAndDisposeMenuItems(downloadsMenu.DropDownItems);
        if (downloads.Count == 0)
        {
            downloadsMenu.DropDownItems.Add(new ToolStripMenuItem("No downloads this session") { Enabled = false });
        }
        else
        {
            var visibleDownloads = downloads
                .Where(download => !download.IsTerminal)
                .Concat(downloads.Where(download => download.IsTerminal).Take(12));
            foreach (var download in visibleDownloads)
            {
                var item = new ToolStripMenuItem($"{download.State} {FormatDownloadProgress(download.BytesReceived, download.TotalBytesToReceive, download.IsTerminal)}: {TrimMenuText(download.FileName, 36)}")
                {
                    ToolTipText = SanitizeDownloadDisplayPath(download.FilePath)
                };
                item.DropDown.Renderer = menuRenderer;
                NativeUiTheme.ApplyMenuWindowFrame(item.DropDown, palette.WindowFrameBorder);
                if (download.State.Equals("Complete", StringComparison.OrdinalIgnoreCase)
                    && DoesDownloadFileExist(download))
                {
                    item.DropDownItems.Add("Open file", null, (_, _) => OpenDownloadedFile(download));
                }
                item.DropDownItems.Add("Show in folder", null, (_, _) => ShowDownloadedFile(download));
                if (!download.IsTerminal && download.CancelAction is not null)
                {
                    item.DropDownItems.Add("Cancel", null, (_, _) => CancelDownload(download));
                }
                item.DropDownItems.Add("Copy source address", null, (_, _) => CopyDownloadAddress(download));
                if (download.IsTerminal)
                {
                    item.DropDownItems.Add(new ToolStripSeparator());
                    item.DropDownItems.Add("Remove from list", null, (_, _) =>
                    {
                        downloads.Remove(download);
                        SyncRecentDownloadsToState();
                        RefreshDownloadsUi();
                        ShowTransientStatus("Download removed from this session's list");
                    });
                }
                downloadsMenu.DropDownItems.Add(item);
            }
        }
        downloadsMenu.DropDownItems.Add(new ToolStripSeparator());
        downloadsMenu.DropDownItems.Add("Open Downloads folder", null, (_, _) => OpenDownloadsFolder());
        ApplyMenuPalette(downloadsMenu.DropDown, palette);
    }

    private void PopulateSearchProviderMenu()
    {
        ClearAndDisposeMenuItems(searchProviderMenu.DropDownItems);
        foreach (var provider in BrowserPolicy.AvailableSearchProviders)
        {
            var item = new ToolStripMenuItem(provider.DisplayName)
            {
                Checked = provider.Id.Equals(searchProviderId, StringComparison.OrdinalIgnoreCase),
                ToolTipText = $"Use {provider.DisplayName} for searches"
            };
            item.Click += (_, _) => SetSearchProvider(provider.Id);
            searchProviderMenu.DropDownItems.Add(item);
        }
        searchProviderMenu.Text = $"Search engine: {BrowserPolicy.GetSearchProviderName(searchProviderId)}";
        ApplyMenuPalette(searchProviderMenu.DropDown, ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode));
    }

    private void ShowSearchProviderMenu(Control anchor)
    {
        PopulateSearchProviderMenu();
        searchProviderMenu.DropDown.Show(
            anchor,
            new Point(0, anchor.Height + 4),
            ToolStripDropDownDirection.BelowRight);
    }

    private void SetSearchProvider(string providerId)
    {
        searchProviderId = BrowserPolicy.NormalizeSearchProviderId(providerId);
        state.SearchProviderId = searchProviderId;
        addressBar.PlaceholderText = $"Search with {BrowserPolicy.GetSearchProviderName(searchProviderId)} or enter address{(isPrivateMode ? " in Incognito" : string.Empty)}";
        foreach (var tab in tabs.Where(item => item.StartPageView is not null && !item.IsClosed))
        {
            tab.StartPageView!.SetSearchProvider(searchProviderId);
        }
        if (addressSuggestionPopup.Visible)
        {
            var query = smartSearchBarEditing && activeSmartSearchBar is not null
                ? activeSmartSearchBar.Text
                : addressBar.Text;
            addressSuggestionPopup.SetSuggestions(
            AddressSuggestionEngine.GetSuggestions(query, state, BuildOpenTabCandidates()));
            PositionAddressSuggestions();
        }
        if (!isPrivateMode) ScheduleStateSave();
        ShowTransientStatus($"Search engine: {BrowserPolicy.GetSearchProviderName(searchProviderId)}");
    }

    private void ShowSavedItemsDialog() => ShowSavedItemsDialog(SavedItemsView.Favorites);

    private void ShowSavedItemsDialog(SavedItemsView initialView)
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Saved items are unavailable in a private window");
            return;
        }

        using var dialog = new SavedItemsDialog(BuildSavedItemRows);
        if (initialView != SavedItemsView.Favorites
            && FindFirstComboBox(dialog) is { } picker
            && (int)initialView is var initialIndex
            && initialIndex >= 0
            && initialIndex < picker.Items.Count)
        {
            picker.SelectedIndex = initialIndex;
        }
        dialog.ActionRequested += (_, action) =>
        {
            HandleSavedItemAction(action);
            dialog.RefreshItems();
        };
        dialog.ShowDialog(this);
    }

    private static ComboBox? FindFirstComboBox(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is ComboBox comboBox) return comboBox;
            var nested = FindFirstComboBox(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void PersistPinnedQuickLinks()
    {
        BrowserStateStore.NormalizeForPersistence(state);
        InvalidateStartPageLinks();
        RefreshNativeStartPages(refreshLinks: true);
        if (!isPrivateMode) ScheduleStateSave();
    }

    private void ShowManageQuickLinksDialog()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Quick links are unavailable in a private window");
            return;
        }

        using var dialog = new Form
        {
            Text = "Manage quick links",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
            MinimumSize = new Size(480, 340),
            Size = new Size(560, 380),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AccessibleRole = AccessibleRole.Dialog,
            AccessibleName = "Manage quick links"
        };
        NativeDialogLayout.EnableDpiScaling(dialog);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 4
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, NativeDialogLayout.WrappedActionRowHeight));

        var subtitle = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"Pinned shortcuts appear first on your New Tab page (up to {BrowserStateStore.MaximumPinnedStartPageLinks})."
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Pinned quick links"
        };
        NativeUiTheme.ConfigureModernListBox(
            list,
            48,
            (index, _) =>
            {
                if (index < 0 || index >= state.PinnedStartPageLinks.Count)
                {
                    return new ModernListRowVisual(string.Empty, string.Empty, string.Empty);
                }
                var item = state.PinnedStartPageLinks[index];
                var title = string.IsNullOrWhiteSpace(item.Title) ? HostFromUrl(item.Url) : item.Title;
                return new ModernListRowVisual(
                    title,
                    FormatBrowserChromeUrlForDisplay(item.Url),
                    (index + 1).ToString(),
                    NativeUiTheme.Accent,
                    "Pinned",
                    NativeUiTheme.Accent);
            });
        var emptyHint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "No pinned quick links yet. Add up to 3 favorites or sites below."
        };

        var actions = NativeDialogLayout.CreateActionBar(wrap: true);
        var addButton = new Button();
        var editButton = new Button();
        var moveUpButton = new Button();
        var moveDownButton = new Button();
        var removeButton = new Button();
        var doneButton = new Button { DialogResult = DialogResult.OK };
        NativeDialogLayout.ConfigureButton(addButton, "Add\u2026", "Add a new quick link");
        NativeDialogLayout.ConfigureButton(editButton, "Edit\u2026", "Edit the selected quick link");
        NativeDialogLayout.ConfigureButton(moveUpButton, "Up", "Move the selected quick link up");
        NativeDialogLayout.ConfigureButton(moveDownButton, "Down", "Move the selected quick link down");
        NativeDialogLayout.ConfigureButton(removeButton, "Remove", "Remove the selected quick link");
        NativeDialogLayout.ConfigureButton(doneButton, "Done", "Close manage quick links");
        actions.Controls.Add(doneButton);
        actions.Controls.Add(removeButton);
        actions.Controls.Add(moveDownButton);
        actions.Controls.Add(moveUpButton);
        actions.Controls.Add(editButton);
        actions.Controls.Add(addButton);

        root.Controls.Add(subtitle, 0, 0);
        root.Controls.Add(list, 0, 1);
        root.Controls.Add(emptyHint, 0, 2);
        root.Controls.Add(actions, 0, 3);
        dialog.Controls.Add(root);
        dialog.AcceptButton = doneButton;
        dialog.CancelButton = doneButton;

        NativeUiTheme.Apply(dialog, addButton);
        foreach (var btn in new[] { editButton, moveUpButton, moveDownButton, removeButton, doneButton })
        {
            NativeUiTheme.ApplySecondaryButton(btn);
        }
        emptyHint.ForeColor = NativeUiTheme.MutedText;

        var dirty = false;

        void RefreshList(int selectedIndex = -1)
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var link in state.PinnedStartPageLinks)
                {
                    list.Items.Add(link.Title);
                }
                if (list.Items.Count > 0)
                {
                    list.SelectedIndex = Math.Clamp(selectedIndex < 0 ? 0 : selectedIndex, 0, list.Items.Count - 1);
                }
            }
            finally
            {
                list.EndUpdate();
            }
            emptyHint.Text = list.Items.Count == 0
                ? $"No pinned quick links yet. Add up to {BrowserStateStore.MaximumPinnedStartPageLinks} favorites or sites below."
                : $"{list.Items.Count} of {BrowserStateStore.MaximumPinnedStartPageLinks} quick link slots used.";
            addButton.Enabled = state.PinnedStartPageLinks.Count < BrowserStateStore.MaximumPinnedStartPageLinks;
            var sel = list.SelectedIndex;
            editButton.Enabled = sel >= 0;
            removeButton.Enabled = sel >= 0;
            moveUpButton.Enabled = sel > 0;
            moveDownButton.Enabled = sel >= 0 && sel < list.Items.Count - 1;
        }

        void EditSelected()
        {
            var index = list.SelectedIndex;
            if (index < 0 || index >= state.PinnedStartPageLinks.Count) return;
            var current = state.PinnedStartPageLinks[index];
            if (!TryEditQuickLink(dialog, "Edit quick link", current.Title, current.Url, out var updated)
                || updated is null)
            {
                return;
            }
            if (state.PinnedStartPageLinks.Where((_, i) => i != index)
                .Any(item => BrowserPolicy.UrlEquals(item.Url, updated.Url)))
            {
                NativeAlertDialog.ShowInfo(dialog, "Duplicate quick link", "That URL is already pinned.");
                return;
            }
            state.PinnedStartPageLinks[index] = updated;
            dirty = true;
            RefreshList(index);
            ShowTransientStatus("Quick link updated");
        }

        void MoveSelectedUp()
        {
            var sel = list.SelectedIndex;
            if (sel <= 0 || sel >= state.PinnedStartPageLinks.Count) return;
            (state.PinnedStartPageLinks[sel - 1], state.PinnedStartPageLinks[sel]) =
                (state.PinnedStartPageLinks[sel], state.PinnedStartPageLinks[sel - 1]);
            dirty = true;
            RefreshList(sel - 1);
        }

        void MoveSelectedDown()
        {
            var sel = list.SelectedIndex;
            if (sel < 0 || sel >= state.PinnedStartPageLinks.Count - 1) return;
            (state.PinnedStartPageLinks[sel], state.PinnedStartPageLinks[sel + 1]) =
                (state.PinnedStartPageLinks[sel + 1], state.PinnedStartPageLinks[sel]);
            dirty = true;
            RefreshList(sel + 1);
        }

        void RemoveSelected()
        {
            var sel = list.SelectedIndex;
            if (sel < 0 || sel >= state.PinnedStartPageLinks.Count) return;
            state.PinnedStartPageLinks.RemoveAt(sel);
            dirty = true;
            RefreshList(Math.Min(sel, state.PinnedStartPageLinks.Count - 1));
            ShowTransientStatus("Quick link removed");
        }

        list.SelectedIndexChanged += (_, _) =>
        {
            var sel = list.SelectedIndex;
            editButton.Enabled = sel >= 0;
            removeButton.Enabled = sel >= 0;
            moveUpButton.Enabled = sel > 0;
            moveDownButton.Enabled = sel >= 0 && sel < list.Items.Count - 1;
        };
        list.DoubleClick += (_, _) => EditSelected();
        list.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && list.SelectedIndex >= 0)
            {
                e.IsInputKey = true;
            }
        };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelected();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode is Keys.F2 or Keys.Enter && list.SelectedIndex >= 0)
            {
                EditSelected();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Up)
            {
                MoveSelectedUp();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Down)
            {
                MoveSelectedDown();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        addButton.Click += (_, _) =>
        {
            if (state.PinnedStartPageLinks.Count >= BrowserStateStore.MaximumPinnedStartPageLinks) return;
            var defaultTitle = activeTab is { IsStartPage: false } && BrowserPolicy.IsHttpUrl(activeTab.Url) && !IsPinned(activeTab.Url)
                ? activeTab.Title
                : string.Empty;
            var defaultUrl = activeTab is { IsStartPage: false } && BrowserPolicy.IsHttpUrl(activeTab.Url) && !IsPinned(activeTab.Url)
                ? activeTab.Url
                : string.Empty;
            if (!TryEditQuickLink(dialog, "Add quick link", defaultTitle, defaultUrl, out var created)
                || created is null)
            {
                return;
            }
            if (IsPinned(created.Url))
            {
                NativeAlertDialog.ShowInfo(dialog, "Already pinned", "That URL is already in your pinned quick links.");
                return;
            }
            state.PinnedStartPageLinks.Add(created);
            dirty = true;
            RefreshList(state.PinnedStartPageLinks.Count - 1);
            ShowTransientStatus("Quick link added");
        };
        editButton.Click += (_, _) => EditSelected();
        moveUpButton.Click += (_, _) => MoveSelectedUp();
        moveDownButton.Click += (_, _) => MoveSelectedDown();
        removeButton.Click += (_, _) => RemoveSelected();

        RefreshList();
        dialog.ShowDialog(this);
        if (dirty)
        {
            PersistPinnedQuickLinks();
        }
    }

    private bool TryEditQuickLink(
        IWin32Window owner,
        string dialogTitle,
        string initialTitle,
        string initialUrl,
        out PinnedStartPageLink? link)
    {
        link = null;
        using var editor = new Form
        {
            Text = dialogTitle,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Size = new Size(440, 224),
            AccessibleRole = AccessibleRole.Dialog,
            AccessibleName = dialogTitle
        };
        NativeDialogLayout.EnableDpiScaling(editor);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleLabel = new Label { Dock = DockStyle.Fill, Text = "Name", TextAlign = ContentAlignment.BottomLeft };
        var titleBox = new TextBox { Dock = DockStyle.Fill, Text = initialTitle, AccessibleName = "Quick link name" };
        var urlLabel = new Label { Dock = DockStyle.Fill, Text = "Web address (https://\u2026)", TextAlign = ContentAlignment.BottomLeft };
        var urlBox = new TextBox { Dock = DockStyle.Fill, Text = initialUrl, AccessibleName = "Quick link address" };

        var buttons = NativeDialogLayout.CreateActionBar(wrap: false);
        var saveButton = new Button();
        var cancelButton = new Button { DialogResult = DialogResult.Cancel };
        NativeDialogLayout.ConfigureButton(saveButton, "Save", "Save quick link");
        NativeDialogLayout.ConfigureButton(cancelButton, "Cancel", "Cancel editing quick link");
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(saveButton);

        PinnedStartPageLink? result = null;
        saveButton.Click += (_, _) =>
        {
            var rawUrl = urlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawUrl)
                || !BrowserPolicy.IsRecognizedAddressInput(rawUrl)
                || BrowserPolicy.ResolveAddress(rawUrl, searchProviderId) is not { Url: { } resolvedUrl }
                || !BrowserPolicy.IsHttpUrl(resolvedUrl))
            {
                NativeAlertDialog.ShowInfo(editor, "Invalid web address", "Enter an http:// or https:// website address.");
                urlBox.Focus();
                urlBox.SelectAll();
                return;
            }

            var title = BrowserStateStore.SanitizeTitle(titleBox.Text);
            if (title.Length == 0)
            {
                title = HostFromUrl(resolvedUrl);
            }
            if (title.Length == 0) title = resolvedUrl;
            result = new PinnedStartPageLink(title, resolvedUrl);
            editor.DialogResult = DialogResult.OK;
            editor.Close();
        };

        layout.Controls.Add(titleLabel, 0, 0);
        layout.Controls.Add(titleBox, 0, 1);
        layout.Controls.Add(urlLabel, 0, 2);
        layout.Controls.Add(urlBox, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        editor.Controls.Add(layout);
        editor.AcceptButton = saveButton;
        editor.CancelButton = cancelButton;

        NativeUiTheme.Apply(editor, saveButton);
        NativeUiTheme.ApplySecondaryButton(cancelButton);

        if (editor.ShowDialog(owner) != DialogResult.OK || result is null) return false;
        link = result;
        return true;
    }

    private async Task ShowNavigationHistoryMenuAsync(Control anchor, bool forward)
    {
        var tab = activeTab;
        var core = tab?.Core;
        if (tab is null || core is null || tab.IsClosed || tab.IsStartPage || isClosing) return;

        string json;
        try
        {
            json = await core.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not inspect tab navigation history", error);
            return;
        }

        if (isClosing || tab.IsClosed || !ReferenceEquals(tab.Core, core) || anchor.IsDisposed || !anchor.Visible)
        {
            return;
        }

        var entries = new List<(int Id, string Title, string Url)>(15);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("currentIndex", out var currentIndexProp)
                || !root.TryGetProperty("entries", out var entriesProp)
                || entriesProp.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var currentIndex = currentIndexProp.GetInt32();
            var total = entriesProp.GetArrayLength();
            if (forward)
            {
                for (var index = currentIndex + 1; index < total && entries.Count < 15; index++)
                {
                    AddHistoryEntry(entriesProp[index]);
                }
            }
            else
            {
                for (var index = currentIndex - 1; index >= 0 && entries.Count < 15; index--)
                {
                    AddHistoryEntry(entriesProp[index]);
                }
            }

            void AddHistoryEntry(JsonElement entry)
            {
                if (!entry.TryGetProperty("id", out var idProp)
                    || !entry.TryGetProperty("url", out var urlProp))
                {
                    return;
                }
                var entryUrl = urlProp.GetString() ?? string.Empty;
                if (entryUrl.Length == 0 || entryUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;
                if (!BrowserPolicy.IsSafeTopLevelUrl(entryUrl, allowFileScheme: true)) return;
                var rawTitle = entry.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                var cleanTitle = BrowserStateStore.SanitizeTitle(rawTitle);
                if (cleanTitle.Length == 0) cleanTitle = HostFromUrl(entryUrl);
                if (cleanTitle.Length == 0) cleanTitle = FormatBrowserChromeUrlForDisplay(entryUrl);
                entries.Add((idProp.GetInt32(), cleanTitle, entryUrl));
            }
        }
        catch (Exception error)
        {
            stateStore.Log("Could not parse navigation history", error);
            return;
        }

        if (entries.Count == 0) return;

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = menuRenderer
        };
        foreach (var entry in entries)
        {
            var entryId = entry.Id;
            var item = new ToolStripMenuItem(TrimMenuText(entry.Title, 44))
            {
                ToolTipText = FormatBrowserChromeUrlForDisplay(entry.Url)
            };
            item.Click += (_, _) => RunUiTask(
                async () =>
                {
                    if (isClosing || tab.IsClosed || !ReferenceEquals(tab.Core, core)) return;
                    await core.CallDevToolsProtocolMethodAsync(
                        "Page.navigateToHistoryEntry",
                        $"{{\"entryId\":{entryId}}}");
                },
                "Could not open that history page");
            menu.Items.Add(item);
        }
        ApplyMenuPalette(menu, ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode));
        menu.Closed += (_, _) =>
        {
            if (!PostUi(() => menu.Dispose())) menu.Dispose();
        };
        menu.Show(anchor, new Point(0, anchor.Height), ToolStripDropDownDirection.BelowRight);
    }

    private async Task TogglePictureInPictureAsync(BrowserTab? targetTab = null)
    {
        var tab = targetTab ?? activeTab;
        if (tab is null || tab.IsClosed || tab.IsStartPage || tab.Core is not { } core)
        {
            ShowTransientStatus("Open a page with video before using Picture-in-Picture");
            return;
        }

        try
        {
            var raw = await core.ExecuteScriptAsync(
                "(() => { try { if (document.pictureInPictureElement) { document.exitPictureInPicture()?.catch(() => {}); return 'exiting'; } const video = document.querySelector('video'); if (!video) return 'none'; if (!document.pictureInPictureEnabled || video.disablePictureInPicture) return 'disabled'; video.requestPictureInPicture()?.catch(() => {}); return 'entering'; } catch (_) { return 'error'; } })()");
            var status = raw.Trim('"');
            ShowTransientStatus(status switch
            {
                "entering" => "Opened Picture-in-Picture",
                "exiting" => "Closed Picture-in-Picture",
                "none" => "No video found on this page",
                "disabled" => "Picture-in-Picture is blocked by this page",
                _ => "Picture-in-Picture is unavailable on this page"
            });
        }
        catch (Exception error)
        {
            stateStore.Log("Picture-in-Picture toggle failed", error);
            ShowTransientStatus("Picture-in-Picture is unavailable");
        }
    }

    private void HardReloadActiveTab()
    {
        var tab = activeTab;
        if (tab is null || tab.IsClosed) return;
        if (tab.IsStartPage)
        {
            ShowStartPage(tab, false);
            return;
        }
        var core = tab.Core;
        if (core is null) return;
        RunUiTask(
            async () =>
            {
                try
                {
                    await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
                }
                catch
                {
                    if (!isClosing && !tab.IsClosed && ReferenceEquals(tab.Core, core))
                    {
                        core.Reload();
                    }
                }
            },
            "Hard reload failed");
    }

    private void HydrateRecentDownloadsFromState()
    {
        if (isPrivateMode || downloads.Count > 0 || state.RecentDownloads.Count == 0) return;
        foreach (var recent in state.RecentDownloads.Take(BrowserStateStore.MaximumRecentDownloads))
        {
            var entry = new DownloadEntry(
                SanitizeDownloadDisplayName(recent.FileName),
                recent.TargetPath,
                recent.SourceUrl,
                recent.CompletedAtUtc.ToLocalTime().DateTime,
                string.IsNullOrWhiteSpace(recent.State) ? "Complete" : recent.State)
            {
                IsTerminal = true,
                CanResume = false
            };
            downloads.Add(entry);
        }
        TrimDownloadRecords();
    }

    private void SyncRecentDownloadsToState(bool scheduleSave = true)
    {
        if (isPrivateMode) return;
        state.RecentDownloads.Clear();
        foreach (var download in downloads
                     .Where(item => item.IsTerminal)
                     .Take(BrowserStateStore.MaximumRecentDownloads))
        {
            var completedUtc = new DateTimeOffset(DateTime.SpecifyKind(download.StartedAt, DateTimeKind.Local))
                .ToUniversalTime();
            state.RecentDownloads.Add(new RecentDownloadEntry(
                download.FileName,
                download.SourceUrl,
                download.FilePath,
                download.State,
                completedUtc));
        }
        if (scheduleSave) ScheduleStateSave();
    }

    private IReadOnlyList<SavedItemRow> BuildSavedItemRows(SavedItemsView view)
    {
        if (isPrivateMode) return Array.Empty<SavedItemRow>();
        return view switch
        {
            SavedItemsView.Favorites => state.Bookmarks
                .Select(item => new SavedItemRow(
                    string.IsNullOrWhiteSpace(item.Title) ? HostFromUrl(item.Url) : item.Title,
                    item.Url,
                    IsPinned(item.Url),
                    "Favorite"))
                .ToArray(),
            SavedItemsView.History => state.History
                .Select(item => new SavedItemRow(
                    string.IsNullOrWhiteSpace(item.Title) ? HostFromUrl(item.Url) : item.Title,
                    item.Url,
                    IsPinned(item.Url),
                    $"Visited {item.VisitedUtc.ToLocalTime():g}"))
                .ToArray(),
            SavedItemsView.QuickLinks => state.PinnedStartPageLinks
                .Select(item => new SavedItemRow(
                    string.IsNullOrWhiteSpace(item.Title) ? HostFromUrl(item.Url) : item.Title,
                    item.Url,
                    true,
                    "Quick link"))
                .ToArray(),
            SavedItemsView.RecentlyClosed => closedTabs
                .Select(item => new SavedItemRow(
                    string.IsNullOrWhiteSpace(item.Title) ? HostFromUrl(item.Url) : item.Title,
                    item.Url,
                    false,
                    $"Closed {item.ClosedUtc.ToLocalTime():g}",
                    item))
                .ToArray(),
            _ => Array.Empty<SavedItemRow>()
        };
    }

    private bool IsPinned(string url)
    {
        return state.PinnedStartPageLinks.Any(item => BrowserPolicy.UrlEquals(item.Url, url));
    }

    internal static bool RemoveSavedItem(BrowserState state, SavedItemsView view, string url)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return view switch
        {
            SavedItemsView.Favorites =>
                state.Bookmarks.RemoveAll(item => BrowserPolicy.UrlEquals(item.Url, url)) > 0,
            SavedItemsView.History =>
                state.History.RemoveAll(item => BrowserPolicy.UrlEquals(item.Url, url)) > 0,
            SavedItemsView.QuickLinks =>
                state.PinnedStartPageLinks.RemoveAll(item => BrowserPolicy.UrlEquals(item.Url, url)) > 0,
            _ => false
        };
    }

    internal static bool RenameSavedItem(
        BrowserState state,
        SavedItemsView view,
        string url,
        string title)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        switch (view)
        {
            case SavedItemsView.Favorites:
                for (var index = 0; index < state.Bookmarks.Count; index++)
                {
                    if (!BrowserPolicy.UrlEquals(state.Bookmarks[index].Url, url)) continue;
                    state.Bookmarks[index] = state.Bookmarks[index] with { Title = title };
                    return true;
                }
                break;
            case SavedItemsView.History:
                for (var index = 0; index < state.History.Count; index++)
                {
                    if (!BrowserPolicy.UrlEquals(state.History[index].Url, url)) continue;
                    state.History[index] = state.History[index] with { Title = title };
                    return true;
                }
                break;
            case SavedItemsView.QuickLinks:
                for (var index = 0; index < state.PinnedStartPageLinks.Count; index++)
                {
                    if (!BrowserPolicy.UrlEquals(state.PinnedStartPageLinks[index].Url, url)) continue;
                    state.PinnedStartPageLinks[index] = state.PinnedStartPageLinks[index] with { Title = title };
                    return true;
                }
                break;
        }

        return false;
    }

    private void HandleSavedItemAction(SavedItemAction action)
    {
        var url = action.Item.Url;
        switch (action.Kind)
        {
            case SavedItemActionKind.Open:
                if (action.View == SavedItemsView.RecentlyClosed
                    && action.Item.Token is ClosedTabEntry closedEntry)
                {
                    RestoreClosedEntry(closedEntry);
                    return;
                }
                RunUiTask(() => NavigateActiveAsync(url), "Navigation could not start");
                return;
            case SavedItemActionKind.Remove:
                if (action.View == SavedItemsView.RecentlyClosed)
                {
                    if (action.Item.Token is ClosedTabEntry closed)
                    {
                        closedTabs.Remove(closed);
                    }
                    else
                    {
                        var matching = closedTabs.FirstOrDefault(item =>
                            BrowserPolicy.UrlEquals(item.Url, url));
                        if (matching is not null) closedTabs.Remove(matching);
                    }
                    if (!isPrivateMode) ScheduleStateSave();
                    ShowTransientStatus("Recently closed item removed");
                    return;
                }
                RemoveSavedItem(state, action.View, url);
                ShowTransientStatus(action.View switch
                {
                    SavedItemsView.Favorites => "Favorite removed",
                    SavedItemsView.History => "History item removed",
                    SavedItemsView.QuickLinks => "Quick link removed",
                    _ => "Saved item removed"
                });
                break;
            case SavedItemActionKind.Rename:
                var title = BrowserStateStore.SanitizeTitle(action.NewTitle);
                if (title.Length == 0)
                {
                    ShowTransientStatus("Enter a title before renaming");
                    return;
                }
                RenameSavedItem(state, action.View, url, title);
                ShowTransientStatus("Saved item renamed");
                break;
            case SavedItemActionKind.TogglePin:
                var pinnedIndex = state.PinnedStartPageLinks.FindIndex(item => BrowserPolicy.UrlEquals(item.Url, url));
                if (pinnedIndex >= 0)
                {
                    state.PinnedStartPageLinks.RemoveAt(pinnedIndex);
                    ShowTransientStatus("Quick link unpinned");
                }
                else
                {
                    if (state.PinnedStartPageLinks.Count >= BrowserStateStore.MaximumPinnedStartPageLinks)
                    {
                        ShowTransientStatus("You can pin up to three quick links");
                        return;
                    }
                    state.PinnedStartPageLinks.Insert(0, new PinnedStartPageLink(action.Item.Title, url));
                    ShowTransientStatus("Quick link pinned");
                }
                break;
            case SavedItemActionKind.MoveUp:
            case SavedItemActionKind.MoveDown:
                var currentIndex = state.PinnedStartPageLinks.FindIndex(item => BrowserPolicy.UrlEquals(item.Url, url));
                var nextIndex = action.Kind == SavedItemActionKind.MoveUp ? currentIndex - 1 : currentIndex + 1;
                if (currentIndex >= 0 && nextIndex >= 0 && nextIndex < state.PinnedStartPageLinks.Count)
                {
                    (state.PinnedStartPageLinks[currentIndex], state.PinnedStartPageLinks[nextIndex]) =
                        (state.PinnedStartPageLinks[nextIndex], state.PinnedStartPageLinks[currentIndex]);
                    ShowTransientStatus("Quick link order updated");
                }
                break;
        }

        BrowserStateStore.NormalizeForPersistence(state);
        InvalidateStartPageLinks();
        RefreshNativeStartPages(refreshLinks: true);
        if (!isPrivateMode) ScheduleStateSave();
    }

    private void ShowDownloadsPopup()
    {
        downloadNotification?.Dismiss();
        downloadsPopup ??= new DownloadsPopupForm(BuildDownloadPopupRows, HandleDownloadPopupAction);
        downloadsPopup.Open(this, downloadsButtonAutoVisible ? downloadsButton : menuButton);
    }

    private void ShowDownloadNotification(DownloadEntry entry)
    {
        notificationDownloadEntry = entry;
        if (isClosing || isWindowMinimized || WindowState == FormWindowState.Minimized) return;
        if (!IsHandleCreated || !Visible || downloadsPopup?.Visible == true) return;
        downloadNotification ??= new DownloadNotificationForm(HandleDownloadPopupAction, ShowDownloadsPopup);
        downloadNotification.ShowOrUpdate(
            this,
            GetDownloadsPopupAnchor(),
            BuildDownloadNotificationState(entry));
    }

    private void UpdateDownloadNotificationIfVisible()
    {
        if (isClosing || downloadNotification?.Visible != true) return;
        var entry = notificationDownloadEntry;
        if (entry is null || !downloads.Contains(entry))
        {
            notificationDownloadEntry = null;
            downloadNotification.Dismiss();
            return;
        }
        downloadNotification.UpdateIfVisible(
            GetDownloadsPopupAnchor(),
            BuildDownloadNotificationState(entry));
    }

    private void RepositionDownloadNotification()
    {
        if (downloadNotification?.Visible == true)
        {
            downloadNotification.Reposition(GetDownloadsPopupAnchor());
        }
    }

    private Control GetDownloadsPopupAnchor() =>
        downloadsButtonAutoVisible && downloadsButton.Visible ? downloadsButton : menuButton;

    private DownloadNotificationState BuildDownloadNotificationState(DownloadEntry download)
    {
        var activeCount = 0;
        for (var index = 0; index < tabs.Count; index++)
        {
            activeCount += tabs[index].ActiveDownloads;
        }
        var percent = download.TotalBytesToReceive > 0
            ? (int?)Math.Clamp(
                (int)Math.Round((double)download.BytesReceived / download.TotalBytesToReceive * 100d),
                0,
                100)
            : null;
        var detail = string.IsNullOrWhiteSpace(download.InterruptReason)
            ? download.EstimatedEndTimeText
            : download.InterruptReason;
        return new DownloadNotificationState(
            download.FileName,
            download.State,
            BuildDownloadProgress(download),
            detail,
            percent,
            activeCount,
            download.IsTerminal && download.State.Equals("Complete", StringComparison.OrdinalIgnoreCase) && DoesDownloadFileExist(download),
            !string.IsNullOrWhiteSpace(download.FilePath),
            !download.IsTerminal && download.CancelAction is not null,
            download);
    }

    private IReadOnlyList<DownloadPopupRow> BuildDownloadPopupRows()
    {
        var rows = new DownloadPopupRow[downloads.Count];
        for (var index = 0; index < downloads.Count; index++)
        {
            var download = downloads[index];
            rows[index] = new DownloadPopupRow(
                download.FileName,
                download.State,
                BuildDownloadProgress(download),
                string.IsNullOrWhiteSpace(download.InterruptReason)
                    ? download.EstimatedEndTimeText
                    : download.InterruptReason,
                CanPauseDownload(download),
                CanResumeDownload(download),
                !download.IsTerminal && download.CancelAction is not null,
                download.IsTerminal && DoesDownloadFileExist(download),
                !string.IsNullOrWhiteSpace(download.FilePath),
                download);
        }
        return rows;
    }

    private void HandleDownloadPopupAction(DownloadPopupAction action, object token)
    {
        if (token is not DownloadEntry download || !downloads.Contains(download)) return;
        switch (action)
        {
            case DownloadPopupAction.Pause:
                PauseDownload(download);
                break;
            case DownloadPopupAction.Resume:
                ResumeDownload(download);
                break;
            case DownloadPopupAction.Cancel:
                CancelDownload(download);
                break;
            case DownloadPopupAction.Open:
                OpenDownloadedFile(download);
                break;
            case DownloadPopupAction.ShowInFolder:
                ShowDownloadedFile(download);
                break;
            case DownloadPopupAction.CopyAddress:
                CopyDownloadAddress(download);
                break;
            case DownloadPopupAction.Remove when download.IsTerminal:
                downloads.Remove(download);
                SyncRecentDownloadsToState();
                if (ReferenceEquals(download, notificationDownloadEntry))
                {
                    notificationDownloadEntry = null;
                    downloadNotification?.Dismiss();
                }
                RefreshDownloadsUi();
                ShowTransientStatus("Download removed from this session's list");
                break;
        }
    }

    private static bool CanPauseDownload(DownloadEntry download)
    {
        return !download.IsTerminal && download.PauseAction is not null
            && download.State.Equals("Downloading", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanResumeDownload(DownloadEntry download)
    {
        return !download.IsTerminal && download.ResumeAction is not null && download.CanResume;
    }

    private void PauseDownload(DownloadEntry download)
    {
        try
        {
            download.PauseAction?.Invoke();
            ShowTransientStatus($"Pausing {download.FileName}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not pause a download", error);
            ShowTransientStatus("Could not pause the download");
        }
    }

    private void ResumeDownload(DownloadEntry download)
    {
        try
        {
            download.ResumeAction?.Invoke();
            ShowTransientStatus($"Resuming {download.FileName}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not resume a download", error);
            ShowTransientStatus("Could not resume the download");
        }
    }

    private void RefreshDownloadsUi()
    {
        if (isClosing) return;
        if (!downloadUiTimer.Enabled) downloadUiTimer.Start();
    }

    private void RefreshDownloadsUiNow()
    {
        downloadUiTimer.Stop();
        var activeCount = 0;
        for (var index = 0; index < tabs.Count; index++)
        {
            activeCount += tabs[index].ActiveDownloads;
        }
        downloadsButton.GlyphState = activeCount > 0 ? "active" : string.Empty;
        if (downloadsPopup?.Visible == true) downloadsPopup.RefreshRows();
        UpdateDownloadNotificationIfVisible();
        UpdateStatus();
    }

    private void BeginDownloadGracePeriod()
    {
        if (isClosing) return;
        downloadsButtonAutoVisible = true;
        downloadGraceTimer.Stop();
        downloadGraceTimer.Start();
        UpdateResponsiveToolbar();
    }

    private void TrimDownloadRecords()
    {
        var active = downloads.Where(item => !item.IsTerminal).ToArray();
        var terminal = downloads
            .Where(item => item.IsTerminal)
            .OrderByDescending(item => item.StartedAt)
            .Take(50)
            .ToArray();
        var kept = active
            .Concat(terminal)
            .OrderByDescending(item => item.StartedAt)
            .ToArray();
        downloads.Clear();
        downloads.AddRange(kept);
    }

    private static void DetachDownloadHandlers(DownloadEntry download)
    {
        var operation = download.Operation;
        if (operation is not null)
        {
            try
            {
                if (download.StateChangedHandler is not null)
                {
                    operation.StateChanged -= download.StateChangedHandler;
                }
                if (download.BytesReceivedChangedHandler is not null)
                {
                    operation.BytesReceivedChanged -= download.BytesReceivedChangedHandler;
                }
                if (download.EstimatedEndTimeChangedHandler is not null)
                {
                    operation.EstimatedEndTimeChanged -= download.EstimatedEndTimeChangedHandler;
                }
            }
            catch (InvalidOperationException)
            {
                // The owning WebView process may already be gone.
            }
            catch (COMException)
            {
                // The owning WebView process may already be gone.
            }
        }
        download.Operation = null;
        download.StateChangedHandler = null;
        download.BytesReceivedChangedHandler = null;
        download.EstimatedEndTimeChangedHandler = null;
        download.CancelAction = null;
        download.PauseAction = null;
        download.ResumeAction = null;
    }

    private static string BuildDownloadProgress(DownloadEntry download)
    {
        var speedSuffix = !download.IsTerminal
            && download.BytesPerSecond >= 1d
            && download.State.Equals("Downloading", StringComparison.OrdinalIgnoreCase)
            ? $" \u00B7 {FormatTransferSpeed(download.BytesPerSecond)}"
            : string.Empty;

        if (download.TotalBytesToReceive > 0)
        {
            var percent = Math.Clamp(
                (double)download.BytesReceived / download.TotalBytesToReceive * 100d,
                0d,
                100d);
            return $"{FormatBytes(download.BytesReceived)} / {FormatBytes(download.TotalBytesToReceive)} ({percent:0}%){speedSuffix}";
        }
        return download.BytesReceived > 0
            ? $"{FormatBytes(download.BytesReceived)} received{speedSuffix}"
            : download.IsTerminal
                ? (download.State.Equals("Interrupted", StringComparison.OrdinalIgnoreCase) ? "Interrupted" : "Saved")
                : "Waiting for size";
    }

    private static string FormatTransferSpeed(double bytesPerSecond)
    {
        var value = Math.Max(0d, bytesPerSecond);
        if (value >= 1024d * 1024d * 1024d)
        {
            return $"{value / (1024d * 1024d * 1024d):0.0} GB/s";
        }
        if (value >= 1024d * 1024d)
        {
            return $"{value / (1024d * 1024d):0.0} MB/s";
        }
        if (value >= 1024d)
        {
            return $"{value / 1024d:0.0} KB/s";
        }
        return $"{value:0} B/s";
    }

    private static string FormatEstimatedEndTime(object? value)
    {
        if (value is null) return string.Empty;
        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("1/1/0001", StringComparison.Ordinal)) return string.Empty;
        return DateTimeOffset.TryParse(text, out var parsed)
            ? $"ETA {parsed.ToLocalTime():t}"
            : string.Empty;
    }

    private static string MapDownloadInterruptReason(CoreWebView2DownloadInterruptReason reason)
    {
        return reason.ToString() switch
        {
            "UserCanceled" => "Canceled by user",
            "UserPaused" => "Paused by user",
            "NetworkInvalidRequest" or "NetworkFailed" or "NetworkTimeout" => "Network error",
            "NetworkServerDown" or "ServerFailed" or "ServerBadContent" => "Server error",
            "FileFailed" or "FileAccessDenied" or "FileNoSpace" => "Could not write the file",
            "FileNameTooLong" => "File name is too long",
            "FileSecurityCheckFailed" => "File security check failed",
            "DownloadProcessCrashed" => "Download process stopped",
            _ => reason == default ? "Download interrupted" : reason.ToString()
        };
    }

    private static void OpenDownloadsFolder()
    {
        var downloadsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        Directory.CreateDirectory(downloadsFolder);
        Process.Start(new ProcessStartInfo(downloadsFolder) { UseShellExecute = true });
    }

    private void OpenDownloadedFile(DownloadEntry download)
    {
        try
        {
            if (!File.Exists(download.FilePath))
            {
                download.LastKnownFileExists = false;
                download.LastFileExistsCheckedAtTick = Environment.TickCount64;
                ShowTransientStatus("The downloaded file is no longer at its saved location");
                return;
            }
            var extension = Path.GetExtension(download.FilePath);
            if (BrowserPolicy.IsHighRiskDownloadExtension(download.FilePath)
                && !NativeAlertDialog.Confirm(
                    this,
                    "Open executable or script file?",
                    $"\"{download.FileName}\" is an executable or script file ({extension}) that may run code on your device.\n\nOnly open files from sources you trust.",
                    confirmText: "Open anyway",
                    cancelText: "Cancel"))
            {
                return;
            }
            Process.Start(new ProcessStartInfo(download.FilePath) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            stateStore.Log("Could not open a downloaded file", error);
            ShowTransientStatus("Could not open the downloaded file");
        }
    }

    private void ShowDownloadedFile(DownloadEntry download)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(download.FilePath) && File.Exists(download.FilePath))
            {
                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                startInfo.ArgumentList.Add($"/select,{download.FilePath}");
                Process.Start(startInfo);
                return;
            }

            var folder = Path.GetDirectoryName(download.FilePath);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                return;
            }
            OpenDownloadsFolder();
        }
        catch (Exception error)
        {
            stateStore.Log("Could not show a downloaded file", error);
            ShowTransientStatus("Could not open the download location");
        }
    }

    private void CancelDownload(DownloadEntry download)
    {
        try
        {
            download.CancelAction?.Invoke();
            download.CancelAction = null;
            ShowTransientStatus($"Canceling {download.FileName}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not cancel a download", error);
            ShowTransientStatus("Could not cancel the download");
        }
    }

    internal static string FormatDownloadProgress(long bytesReceived, long totalBytesToReceive, bool isTerminal = false)
    {
        if (isTerminal) return string.Empty;
        if (totalBytesToReceive > 0)
        {
            var percent = Math.Clamp(
                (double)bytesReceived / totalBytesToReceive * 100d,
                0d,
                100d);
            return $"{percent:0}%";
        }

        return bytesReceived > 0
            ? $"{FormatBytes(bytesReceived)} received"
            : "";
    }

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unitIndex = 0;
        var display = (double)value;
        while (display >= 1024 && unitIndex < units.Length - 1)
        {
            display /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value} B" : $"{display:0.0} {units[unitIndex]}";
    }

    private void CopyDownloadAddress(DownloadEntry download)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(download.SourceUrl)) return;
            Clipboard.SetText(download.SourceUrl);
            ShowTransientStatus("Download address copied");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not copy a download address", error);
            ShowTransientStatus("Could not copy the download address");
        }
    }

    internal void OpenPrivateWindow()
    {
        if (isClosing) return;
        var privateWindow = new MainForm(true, null, null, BrowserMode.Private)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(Location.X + 28, Location.Y + 28)
        };
        Program.RegisterTopLevelWindow(privateWindow);
        privateWindow.Show();
        ShowTransientStatus("Incognito window opened");
    }

}

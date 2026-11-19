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
    private void OnChromeStoreWebMessage(
        BrowserTab tab,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        var channel = tab.ChromeStoreInstallChannel;
        var currentUrl = tab.Core?.Source ?? tab.Url;
        var extensionId = BrowserExtensions.ParseChromeWebStoreListingExtensionId(currentUrl);
        if (channel is null
            || extensionId is null
            || !BrowserExtensions.IsChromeWebStoreOrigin(e.Source))
        {
            return;
        }

        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        var expectedMessage = string.Concat(channel, ":", extensionId);
        if (!message.Equals(expectedMessage, StringComparison.Ordinal)) return;
        QueueChromeStoreExtensionInstall(tab, extensionId);
    }

    private bool TryHandleChromeStoreExtensionDownload(
        BrowserTab tab,
        CoreWebView2DownloadStartingEventArgs e)
    {
        var pageUrl = tab.Core?.Source ?? tab.Url;
        var extensionId = BrowserExtensions.MatchChromeWebStoreInstallRequest(
            pageUrl,
            e.DownloadOperation.Uri);
        if (extensionId is null) return false;

        e.Cancel = true;
        e.Handled = true;
        try { e.DownloadOperation.Cancel(); }
        catch { }
        QueueChromeStoreExtensionInstall(tab, extensionId);
        return true;
    }

    private void QueueChromeStoreExtensionInstall(BrowserTab tab, string extensionId)
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Extensions are unavailable in private windows");
            return;
        }
        if (isClosing
            || closeWhenExtensionOperationsFinish
            || tab.IsClosed
            || tab.Core is null)
        {
            return;
        }
        if (!installingChromeStoreExtensionIds.Add(extensionId))
        {
            ShowTransientStatus("That extension is already being installed");
            return;
        }

        var profile = tab.Core.Profile;
        RunUiTask(
            () => InstallChromeStoreExtensionFromPageAsync(profile, extensionId),
            "Could not install the Chrome Web Store extension");
    }

    private async Task InstallChromeStoreExtensionFromPageAsync(
        CoreWebView2Profile profile,
        string extensionId)
    {
        try
        {
            var cancellationToken = chromeStoreInstallCancellation.Token;
            cancellationToken.ThrowIfCancellationRequested();
            var installedExtensions = await profile.GetBrowserExtensionsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (installedExtensions.Any(extension =>
                extension.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase)))
            {
                ShowTransientStatus("That extension is already installed; use Extensions… to manage it");
                return;
            }

            ShowTransientStatus("Preparing extension…", false);
            await InstallPreparedExtensionAsync(
                profile,
                this,
                cancellationToken,
                () => browserExtensions.DownloadFromChromeWebStoreAsync(
                    extensionId,
                    environment?.BrowserVersionString ?? string.Empty,
                    cancellationToken));
            if (extensionsManager is { IsDisposed: false } manager)
            {
                await manager.RefreshAfterExternalChangeAsync();
            }
            ShowTransientStatus("Extension installed");
        }
        catch (OperationCanceledException)
        {
            if (!isClosing) ShowTransientStatus("Extension installation canceled");
        }
        catch (Exception error)
        {
            if (isClosing) return;
            var friendlyError = CreateFriendlyExtensionError(error);
            stateStore.Log("Chrome Web Store button installation failed", friendlyError);
            ShowTransientStatus("Extension installation failed");
            NativeAlertDialog.ShowInfo(
                this,
                "Extension installation failed",
                friendlyError.Message);
        }
        finally
        {
            installingChromeStoreExtensionIds.Remove(extensionId);
            ReleaseBrowserEnvironmentIfIdle();
        }
    }

    private async Task ShowExtensionsManagerAsync()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Extensions are unavailable in private windows");
            return;
        }
        if (extensionsManager is { IsDisposed: false })
        {
            extensionsManager.PrefillStoreAddress(GetCurrentChromeStoreAddress());
            if (!extensionsManager.Visible) extensionsManager.Show(this);
            extensionsManager.Activate();
            return;
        }
        if (openingExtensionsManager)
        {
            ShowTransientStatus("Opening extensions…");
            return;
        }

        openingExtensionsManager = true;
        CoreWebView2Controller? controller = null;
        ExtensionsManagerForm? manager = null;
        try
        {
            var browserEnvironment = await GetOrCreateEnvironmentAsync();
            if (isClosing || environment != browserEnvironment) return;
            var controllerOptions = browserEnvironment.CreateCoreWebView2ControllerOptions();
            controllerOptions.IsInPrivateModeEnabled = false;
            controller = await browserEnvironment.CreateCoreWebView2ControllerAsync(Handle, controllerOptions);
            if (isClosing || environment != browserEnvironment)
            {
                controller.Close();
                controller = null;
                return;
            }
            controller.IsVisible = false;
            controller.Bounds = Rectangle.Empty;
            extensionProfileController = controller;

            manager = new ExtensionsManagerForm(
                BuildExtensionManagerRowsAsync,
                InstallChromeStoreExtensionAsync,
                InstallExtensionPackageAsync,
                InstallUnpackedExtensionAsync,
                UpdateChromeStoreExtensionAsync,
                HandleExtensionManagerActionAsync,
                OpenManagedExtensionsFolder,
                OpenExtensionsFolder);
            extensionsManager = manager;
            manager.PrefillStoreAddress(GetCurrentChromeStoreAddress());
            manager.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(extensionsManager, manager)) extensionsManager = null;
                DisposeExtensionProfileController(controller);
                ReleaseBrowserEnvironmentIfIdle();
            };
            manager.Show(this);
        }
        catch
        {
            if (ReferenceEquals(extensionsManager, manager)) extensionsManager = null;
            try { manager?.Dispose(); }
            catch { }
            if (controller is not null) DisposeExtensionProfileController(controller);
            throw;
        }
        finally
        {
            openingExtensionsManager = false;
        }
    }

    private void QueueManagedExtensionReconciliation(CoreWebView2Profile profile)
    {
        if (isPrivateMode || managedExtensionReconciliationStarted || isClosing) return;
        managedExtensionReconciliationStarted = true;
        _ = ReconcileManagedExtensionsAfterStartupAsync(profile);
    }

    private async Task ReconcileManagedExtensionsAfterStartupAsync(CoreWebView2Profile profile)
    {
        try
        {
            var installed = await profile.GetBrowserExtensionsAsync();
            if (isClosing) return;
            var installedIds = installed.Select(extension => extension.Id).ToArray();
            await Task.Run(() => browserExtensions.ReconcileManagedExtensions(installedIds));
        }
        catch (Exception error)
        {
            if (isClosing) return;
            managedExtensionReconciliationStarted = false;
            stateStore.Log("Could not reconcile managed extension packages", error);
        }
    }

    private async Task<IReadOnlyList<ExtensionManagerRow>> BuildExtensionManagerRowsAsync()
    {
        var profile = GetExtensionManagerProfile();
        var installed = await profile.GetBrowserExtensionsAsync();
        var installedIds = installed.Select(extension => extension.Id).ToArray();
        var managedExtensions = await Task.Run(() =>
        {
            browserExtensions.ReconcileManagedExtensions(installedIds);
            return browserExtensions.GetManagedExtensions();
        });
        return installed
            .Select(extension =>
            {
                managedExtensions.TryGetValue(extension.Id, out var managed);
                var fullIconPath = managed?.FolderPath is not null && managed.IconPath is not null
                    ? BrowserExtensions.ResolveSafeExtensionFilePath(managed.FolderPath, managed.IconPath)
                    : null;
                return new ExtensionManagerRow(
                    extension.Id,
                    SanitizeExtensionDisplayName(extension.Name),
                    SanitizeExtensionPromptText(managed?.Version ?? "Unknown", 80),
                    extension.IsEnabled,
                    managed?.LaunchPath,
                    managed?.StoreId is not null
                        && managed.StoreId.Equals(extension.Id, StringComparison.OrdinalIgnoreCase),
                    extension,
                    managed?.PopupPath,
                    managed?.OptionsPath,
                    managed?.Description,
                    fullIconPath,
                    managed?.StoreId,
                    managed?.FolderPath,
                    managed?.RequestedCapabilities);
            })
            .ToArray();
    }

    private Task InstallChromeStoreExtensionAsync(string value, CancellationToken cancellationToken) =>
        InstallPreparedExtensionAsync(
            GetExtensionManagerProfile(),
            (IWin32Window?)extensionsManager ?? this,
            cancellationToken,
            () => browserExtensions.DownloadFromChromeWebStoreAsync(
                value,
                environment?.BrowserVersionString ?? string.Empty,
                cancellationToken));

    private Task InstallExtensionPackageAsync(string path, CancellationToken cancellationToken) =>
        InstallPreparedExtensionAsync(
            GetExtensionManagerProfile(),
            (IWin32Window?)extensionsManager ?? this,
            cancellationToken,
            () => browserExtensions.ImportPackageAsync(path, cancellationToken));

    private Task InstallUnpackedExtensionAsync(string path, CancellationToken cancellationToken) =>
        InstallPreparedExtensionAsync(
            GetExtensionManagerProfile(),
            (IWin32Window?)extensionsManager ?? this,
            cancellationToken,
            () => browserExtensions.ImportUnpackedAsync(path, cancellationToken));

    private async Task<string> UpdateChromeStoreExtensionAsync(
        ExtensionManagerRow row,
        CancellationToken cancellationToken)
    {
        if (isPrivateMode)
        {
            throw new InvalidOperationException("Extensions are unavailable in private windows.");
        }
        if (row.Token is not CoreWebView2BrowserExtension installedExtension
            || !installedExtension.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected extension is no longer available.");
        }
        var eligibilityError = GetExtensionUpdateEligibilityError(
            isPrivateMode: false,
            isManagedStoreExtension: row.CanUpdate,
            isEnabled: installedExtension.IsEnabled);
        if (eligibilityError is not null)
        {
            throw new InvalidOperationException(eligibilityError);
        }

        using var extensionMutation = BeginExtensionMutation();
        var profile = GetExtensionManagerProfile();
        var update = await browserExtensions.CheckForChromeWebStoreUpdateAsync(
            row.Id,
            environment?.BrowserVersionString ?? string.Empty,
            cancellationToken);
        if (update is null)
        {
            return $"{SanitizeExtensionDisplayName(row.Name)} is already up to date";
        }

        var owner = (IWin32Window?)extensionsManager ?? this;
        var prepared = update.Prepared;
        CoreWebView2BrowserExtension? replacement = null;
        var committed = false;
        var restored = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfirmPreparedExtensionUpdate(update, owner))
            {
                throw new OperationCanceledException("Extension update canceled.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            // WebView2 keeps only the later package when the same extension ID
            // is added. The old managed folder remains untouched until the new
            // package is live, identity-checked, and recorded.
            await Task.Run(() => browserExtensions.MarkInstallationStarted(prepared));
            replacement = await profile.AddBrowserExtensionAsync(prepared.FolderPath);
            if (!replacement.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase))
            {
                try { await replacement.RemoveAsync(); }
                catch (Exception cleanupError)
                {
                    stateStore.Log("Could not remove a mismatched extension update", cleanupError);
                }
                replacement = null;
                throw new InvalidDataException("The installed update ID did not match the selected extension.");
            }
            await Task.Run(() => browserExtensions.RememberInstalled(row.Id, prepared));
            committed = true;
            return $"Updated {SanitizeExtensionDisplayName(prepared.Name)} to {SanitizeExtensionPromptText(prepared.Version, 80)}";
        }
        catch (Exception error)
        {
            if (replacement is not null && !committed)
            {
                try
                {
                    var previous = await profile.AddBrowserExtensionAsync(update.Current.FolderPath);
                    if (!previous.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The restored extension ID did not match.");
                    }
                    restored = true;
                }
                catch (Exception rollbackError)
                {
                    stateStore.Log("Could not roll back the extension update", rollbackError);
                }
            }

            if (!committed && (replacement is null || restored))
            {
                await Task.Run(() => browserExtensions.DiscardPrepared(prepared));
            }
            else if (!committed)
            {
                stateStore.Log(
                    "Preserved an uncommitted extension update package after rollback failed",
                    error);
            }
            throw CreateFriendlyExtensionError(error);
        }
    }

    internal static string? GetExtensionUpdateEligibilityError(
        bool isPrivateMode,
        bool isManagedStoreExtension,
        bool isEnabled)
    {
        if (isPrivateMode) return "Extensions are unavailable in private windows.";
        if (!isManagedStoreExtension)
        {
            return "Only MishaWeb-managed Chrome Web Store extensions can be updated.";
        }
        return isEnabled
            ? null
            : "Enable this extension before updating it. WebView2 activates a replacement as soon as it is installed.";
    }

    private async Task InstallPreparedExtensionAsync(
        CoreWebView2Profile profile,
        IWin32Window confirmationOwner,
        CancellationToken cancellationToken,
        Func<Task<PreparedBrowserExtension>> prepare)
    {
        using var extensionMutation = BeginExtensionMutation();
        PreparedBrowserExtension? prepared = null;
        CoreWebView2BrowserExtension installed;
        try
        {
            prepared = await prepare();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfirmPreparedExtensionInstall(prepared, confirmationOwner))
            {
                throw new OperationCanceledException("Extension installation canceled.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => browserExtensions.MarkInstallationStarted(prepared));
            installed = await profile.AddBrowserExtensionAsync(prepared.FolderPath);
        }
        catch (Exception error)
        {
            if (prepared is not null)
            {
                await Task.Run(() => browserExtensions.DiscardPrepared(prepared));
            }
            throw CreateFriendlyExtensionError(error);
        }

        try
        {
            // WebView2 extension objects are apartment-threaded. Read the COM
            // property on the UI thread before moving file work off-thread.
            var installedId = installed.Id;
            await Task.Run(() => browserExtensions.RememberInstalled(installedId, prepared));
        }
        catch (Exception error)
        {
            stateStore.Log("Could not record the installed extension package", error);
            var removed = false;
            try
            {
                await installed.RemoveAsync();
                removed = true;
            }
            catch (Exception cleanupError)
            {
                stateStore.Log("Could not roll back an unrecorded extension", cleanupError);
            }
            if (removed)
            {
                await Task.Run(() => browserExtensions.DiscardPrepared(prepared));
            }
            else
            {
                stateStore.Log(
                    "Preserved an unrecorded extension package because WebView2 may still use it",
                    error);
            }
            throw CreateFriendlyExtensionError(
                new IOException(
                    removed
                        ? "The extension could not be saved and was rolled back."
                        : "The extension could not be saved. Its package was retained so the browser can recover it safely.",
                    error));
        }
    }

    private bool ConfirmPreparedExtensionUpdate(
        PreparedBrowserExtensionUpdate update,
        IWin32Window owner)
    {
        const int maximumVisibleCapabilities = 18;
        var prepared = update.Prepared;
        var safeName = SanitizeExtensionPromptText(prepared.Name, 120);
        var safeCurrentVersion = SanitizeExtensionPromptText(update.Current.Version, 80);
        var safeNewVersion = SanitizeExtensionPromptText(prepared.Version, 80);
        if (safeName.Length == 0) safeName = "Unnamed extension";
        if (safeCurrentVersion.Length == 0) safeCurrentVersion = "unknown";
        if (safeNewVersion.Length == 0) safeNewVersion = "unknown";

        var requested = SelectExtensionCapabilitiesForPrompt(
            prepared.RequestedCapabilities,
            maximumVisibleCapabilities);
        var requestedText = requested.Count == 0
            ? "No permissions or site access are declared in its manifest."
            : string.Join(Environment.NewLine, requested.Select(value => "\u2022 " + value));
        var added = SelectExtensionCapabilitiesForPrompt(
            update.AddedCapabilities,
            maximumVisibleCapabilities);
        var addedText = added.Count == 0
            ? "No newly declared access."
            : string.Join(Environment.NewLine, added.Select(value => "\u2022 " + value));
        var message = $"Update {safeName} from {safeCurrentVersion} to {safeNewVersion}?{Environment.NewLine}"
            + $"Chrome Web Store ID: {prepared.StoreId}{Environment.NewLine}{Environment.NewLine}"
            + $"Newly declared access:{Environment.NewLine}{addedText}{Environment.NewLine}{Environment.NewLine}"
            + $"All access requested by the update:{Environment.NewLine}{requestedText}"
            + $"{Environment.NewLine}{Environment.NewLine}The update activates only if you approve it.";
        return NativeAlertDialog.Confirm(
            owner,
            "Update extension",
            message,
            confirmText: "Update",
            cancelText: "Cancel");
    }

    private bool ConfirmPreparedExtensionInstall(
        PreparedBrowserExtension prepared,
        IWin32Window owner)
    {
        const int maximumVisibleCapabilities = 18;
        var safeName = SanitizeExtensionPromptText(prepared.Name, 120);
        var safeVersion = SanitizeExtensionPromptText(prepared.Version, 80);
        if (safeName.Length == 0) safeName = "Unnamed extension";
        if (safeVersion.Length == 0) safeVersion = "unknown version";
        var visibleCapabilities = SelectExtensionCapabilitiesForPrompt(
            prepared.RequestedCapabilities,
            maximumVisibleCapabilities);
        var access = visibleCapabilities.Count == 0
            ? "No permissions or site access are declared in its manifest."
            : string.Join(Environment.NewLine, visibleCapabilities.Select(value => "\u2022 " + value));
        var source = prepared.StoreId is null
            ? "Local package"
            : $"Chrome Web Store ID: {prepared.StoreId}";
        var message = $"Install {safeName} {safeVersion}?{Environment.NewLine}"
            + $"{source}{Environment.NewLine}{Environment.NewLine}Requested access:{Environment.NewLine}{access}"
            + $"{Environment.NewLine}{Environment.NewLine}Only install extensions you trust.";
        return NativeAlertDialog.Confirm(
            owner,
            "Install extension",
            message,
            confirmText: "Install",
            cancelText: "Cancel");
    }

    internal static List<string> SelectExtensionCapabilitiesForPrompt(
        IReadOnlyList<string> capabilities,
        int maximumOrdinaryCapabilities)
    {
        const int maximumScannedCapabilities = 4_096;
        const int maximumVisibleHighRiskCapabilities = 12;
        var sanitized = capabilities
            .Take(maximumScannedCapabilities)
            .Select(value => SanitizeExtensionPromptText(value, 180))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => (Value: value, Risk: GetExtensionCapabilityRisk(value)))
            .ToArray();
        var highRisk = sanitized
            .Where(item => item.Risk > 0)
            .OrderByDescending(item => item.Risk)
            .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ordinary = sanitized
            .Where(item => item.Risk == 0)
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visible = highRisk
            .Take(maximumVisibleHighRiskCapabilities)
            .Select(item => DescribeExtensionCapability(item.Value, item.Risk))
            .ToList();
        if (highRisk.Length > maximumVisibleHighRiskCapabilities)
        {
            visible.Add(
                $"HIGH RISK — {highRisk.Length - maximumVisibleHighRiskCapabilities} additional sensitive access declaration(s) are not expanded");
        }
        var ordinaryLimit = Math.Max(0, maximumOrdinaryCapabilities);
        visible.AddRange(ordinary.Take(ordinaryLimit).Select(item => item.Value));
        if (ordinary.Length > ordinaryLimit)
        {
            visible.Add($"…and {ordinary.Length - ordinaryLimit} additional lower-risk declaration(s)");
        }
        if (capabilities.Count > maximumScannedCapabilities)
        {
            visible.Add(
                $"HIGH RISK — {capabilities.Count - maximumScannedCapabilities} additional manifest declaration(s) require manual review");
        }
        return visible;
    }

    private static int GetExtensionCapabilityRisk(string capability)
    {
        var value = capability.ToLowerInvariant();
        var permission = GetDeclaredExtensionPermission(value);
        if (value.Contains("<all_urls>", StringComparison.Ordinal)
            || value.Contains("*://*/*", StringComparison.Ordinal)
            || value.Contains("https://*/*", StringComparison.Ordinal)
            || value.Contains("http://*/*", StringComparison.Ordinal)
            || permission is "debugger"
                or "desktopcapture"
                or "nativemessaging"
                or "pagecapture"
                or "proxy"
                or "tabcapture"
                or "webauthenticationproxy")
        {
            return 3;
        }
        if (permission is "accessibilityfeatures.modify"
                or "accessibilityfeatures.read"
                or "bookmarks"
                or "browsingdata"
                or "clipboardread"
                or "clipboardwrite"
                or "contentsettings"
                or "cookies"
                or "declarativenetrequest"
                or "declarativenetrequestfeedback"
                or "declarativenetrequestwithhostaccess"
                or "favicon"
                or "geolocation"
                or "history"
                or "identity.email"
                or "management"
                or "notifications"
                or "privacy"
                or "readinglist"
                or "scripting"
                or "system.storage"
                or "tabs"
                or "tabgroups"
                or "topsites"
                or "ttsengine"
                or "webnavigation"
            || permission?.StartsWith("downloads", StringComparison.Ordinal) == true
            || permission?.StartsWith("webrequest", StringComparison.Ordinal) == true
            || value.StartsWith("site access:", StringComparison.Ordinal)
            || value.StartsWith("optional site access:", StringComparison.Ordinal)
            || value.StartsWith("runs on:", StringComparison.Ordinal)
            || value.StartsWith("accepts connections from:", StringComparison.Ordinal))
        {
            return 2;
        }
        return 0;
    }

    private static string DescribeExtensionCapability(string capability, int risk)
    {
        if (risk < 2) return capability;
        var lower = capability.ToLowerInvariant();
        var permission = GetDeclaredExtensionPermission(lower);
        var consequence = lower.Contains("<all_urls>", StringComparison.Ordinal)
            || lower.Contains("*://*/*", StringComparison.Ordinal)
            || lower.Contains("https://*/*", StringComparison.Ordinal)
            || lower.Contains("http://*/*", StringComparison.Ordinal)
                ? "can read and change data on all websites"
            : permission == "nativemessaging"
                ? "can communicate with applications on this computer"
            : permission == "debugger"
                ? "can inspect and control browser tabs"
            : permission == "proxy"
                ? "can route and change browser network traffic"
            : permission is "desktopcapture" or "pagecapture" or "tabcapture"
                ? "can capture screen or webpage contents"
            : permission == "webauthenticationproxy"
                ? "can intercept browser authentication requests"
            : permission == "management"
                ? "can manage installed browser extensions"
            : permission is "privacy" or "contentsettings" or "accessibilityfeatures.modify"
                ? "can change sensitive browser settings"
            : permission == "accessibilityfeatures.read"
                ? "can read accessibility settings"
            : permission is "tabs" or "history" or "webnavigation" or "topsites"
                ? "can read browsing activity"
            : permission == "favicon"
                ? "can read icons of websites you visit"
            : permission is "bookmarks" or "readinglist"
                ? "can read or change saved browsing items"
            : permission is "clipboardread" or "clipboardwrite"
                ? "can read or change clipboard contents"
            : permission is "geolocation" or "identity.email"
                ? "can access identity or physical-location data"
            : permission == "browsingdata"
                ? "can remove browser data"
            : permission == "notifications"
                ? "can display browser notifications"
            : permission == "tabgroups"
                ? "can view and manage tab groups"
            : permission == "ttsengine"
                ? "can read text spoken by synthesized speech"
            : permission == "scripting"
                ? "can run scripts on websites it can access"
            : permission == "system.storage"
                ? "can identify and eject storage devices"
            : permission?.StartsWith("downloads", StringComparison.Ordinal) == true
                ? "can manage downloaded files"
            : permission?.StartsWith("webrequest", StringComparison.Ordinal) == true
                || permission?.StartsWith("declarativenetrequest", StringComparison.Ordinal) == true
                ? "can observe, block, or change network requests"
            : lower.StartsWith("site access:", StringComparison.Ordinal)
                || lower.StartsWith("optional site access:", StringComparison.Ordinal)
                || lower.StartsWith("runs on:", StringComparison.Ordinal)
                ? "can read and change data on the named website"
            : lower.StartsWith("accepts connections from:", StringComparison.Ordinal)
                ? "can receive connections from named websites or extensions"
            : "can access sensitive browser or website data";
        return $"HIGH RISK — {consequence}: {capability}";
    }

    private static string? GetDeclaredExtensionPermission(string capability)
    {
        var separator = capability.IndexOf(':');
        if (separator <= 0) return null;
        var label = capability[..separator].Trim();
        if (label is not ("permission" or "optional permission")) return null;
        var permission = capability[(separator + 1)..].Trim();
        return permission.Length == 0 ? null : permission;
    }

    internal static string SanitizeExtensionPromptText(string value, int maximumLength)
        => TextSafety.SanitizeSingleLine(value, maximumLength);

    internal static string SanitizeDownloadDisplayName(string? fileName)
    {
        var sanitized = SanitizeExtensionPromptText(
            fileName ?? string.Empty,
            MaximumDownloadDisplayNameCharacters);
        return sanitized.Length == 0 ? "download" : sanitized;
    }

    internal static string SanitizeDownloadDisplayPath(string? filePath) =>
        SanitizeExtensionPromptText(
            filePath ?? string.Empty,
            MaximumDownloadDisplayPathCharacters);

    internal static string SanitizeExtensionDisplayName(string? name)
    {
        var sanitized = SanitizeExtensionPromptText(
            name ?? string.Empty,
            MaximumExtensionDisplayNameCharacters);
        return sanitized.Length == 0 ? "Unnamed extension" : sanitized;
    }

    private async Task HandleExtensionManagerActionAsync(
        ExtensionManagerAction action,
        ExtensionManagerRow row)
    {
        if (row.Token is not CoreWebView2BrowserExtension extension)
        {
            throw new InvalidOperationException("The selected extension is no longer available.");
        }

        using var extensionMutation = (action == ExtensionManagerAction.Open
            || action == ExtensionManagerAction.OpenPopup
            || action == ExtensionManagerAction.OpenOptions)
            ? null
            : BeginExtensionMutation();
        try
        {
            switch (action)
            {
                case ExtensionManagerAction.Open:
                case ExtensionManagerAction.OpenPopup:
                    var popupTarget = (action == ExtensionManagerAction.OpenPopup ? row.PopupPath : null)
                        ?? row.LaunchPath
                        ?? row.OptionsPath;
                    if (popupTarget is null) return;
                    var popupUrl = $"chrome-extension://{row.Id}/{popupTarget}";
                    if (await OpenNewTabAsync(popupUrl, trustedExtensionId: row.Id) is null)
                    {
                        throw new InvalidOperationException("The extension page could not be opened.");
                    }
                    break;
                case ExtensionManagerAction.OpenOptions:
                    var optionsTarget = row.OptionsPath ?? row.LaunchPath;
                    if (optionsTarget is null) return;
                    var optionsUrl = $"chrome-extension://{row.Id}/{optionsTarget}";
                    if (await OpenNewTabAsync(optionsUrl, trustedExtensionId: row.Id) is null)
                    {
                        throw new InvalidOperationException("The extension options page could not be opened.");
                    }
                    break;
                case ExtensionManagerAction.ToggleEnabled:
                    await extension.EnableAsync(!row.IsEnabled);
                    break;
                case ExtensionManagerAction.Remove:
                    await extension.RemoveAsync();
                    await Task.Run(() => browserExtensions.ForgetInstalled(row.Id));
                    break;
            }
        }
        catch (Exception error)
        {
            throw CreateFriendlyExtensionError(error);
        }
    }

    private IDisposable BeginExtensionMutation()
    {
        Interlocked.Increment(ref activeExtensionMutationCount);
        return new ExtensionMutationScope(this);
    }

    private void EndExtensionMutation()
    {
        var remaining = Interlocked.Decrement(ref activeExtensionMutationCount);
        if (remaining == 0)
        {
            QueueDeferredCloseAfterExtensionMutation();
            return;
        }
        if (remaining > 0) return;
        Interlocked.Exchange(ref activeExtensionMutationCount, 0);
        stateStore.Log(
            "Extension operation accounting was unbalanced",
            new InvalidOperationException("The active extension operation count became negative."));
    }

    private void QueueDeferredCloseAfterExtensionMutation()
    {
        if (!closeWhenExtensionOperationsFinish
            || isClosing
            || IsDisposed
            || !IsHandleCreated)
        {
            return;
        }
        PostUi(() =>
        {
            if (closeWhenExtensionOperationsFinish
                && !isClosing
                && !IsDisposed
                && Volatile.Read(ref activeExtensionMutationCount) == 0)
            {
                Close();
            }
        });
    }

    private void ResetChromeStoreInstallCancellationAfterCanceledClose()
    {
        if (isClosing
            || closeWhenExtensionOperationsFinish
            || Volatile.Read(ref activeExtensionMutationCount) > 0
            || !chromeStoreInstallCancellation.IsCancellationRequested)
        {
            return;
        }
        var canceled = chromeStoreInstallCancellation;
        chromeStoreInstallCancellation = new CancellationTokenSource();
        canceled.Dispose();
    }

    private sealed class ExtensionMutationScope : IDisposable
    {
        private MainForm? owner;

        internal ExtensionMutationScope(MainForm owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.EndExtensionMutation();
        }
    }

    private CoreWebView2Profile GetExtensionManagerProfile()
    {
        if (isPrivateMode)
        {
            throw new InvalidOperationException("Extensions are unavailable in private windows.");
        }
        return extensionProfileController?.CoreWebView2?.Profile
            ?? throw new InvalidOperationException("The extensions manager is no longer connected to the browser profile.");
    }

    private string? GetCurrentChromeStoreAddress()
    {
        var url = activeTab?.Url;
        if (BrowserExtensions.ParseChromeWebStoreExtensionId(url) is not null) return url;
        return state.History
            .Select(entry => entry.Url)
            .FirstOrDefault(historyUrl =>
                BrowserExtensions.ParseChromeWebStoreExtensionId(historyUrl) is not null);
    }

    private void OpenManagedExtensionsFolder()
    {
        OpenFolder(browserExtensions.ManagedFolderPath, "managed extensions folder");
    }

    private void OpenExtensionsFolder()
    {
        OpenFolder(browserExtensions.FolderPath, "user scripts folder");
    }

    private void OpenFolder(string path, string displayName)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            stateStore.Log($"Could not open the {displayName}", error);
            ShowTransientStatus($"Could not open the {displayName}");
        }
    }

    private static Exception CreateFriendlyExtensionError(Exception error)
    {
        if (error is InvalidDataException
            or FileNotFoundException
            or DirectoryNotFoundException
            or OperationCanceledException)
        {
            return error;
        }
        return error.HResult switch
        {
            unchecked((int)0x80070032) => new InvalidOperationException(
                "Browser extension support is unavailable in this WebView2 Runtime.", error),
            unchecked((int)0x80070002) => new InvalidOperationException(
                "The extension does not contain a valid manifest.json file.", error),
            unchecked((int)0x80070005) => new InvalidOperationException(
                "The extension files could not be accessed. Check their permissions and names.", error),
            _ when error is HttpRequestException => new InvalidOperationException(
                "The extension could not be downloaded. Check your connection and try again.", error),
            _ => new InvalidOperationException(
                string.IsNullOrWhiteSpace(error.Message)
                    ? "The browser extension could not be installed."
                    : error.Message,
                error)
        };
    }

}

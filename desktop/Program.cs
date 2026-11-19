using System;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MishaWeb;

internal static class Program
{
    private const int MaximumIpcRequestBytes = 16 * 1024;
    private const int MaximumQueuedStartupHandoffs = 16;
    private const string RestartAfterPidArgument = "--restart-after-pid";
    private const string RestartAfterStartTicksArgument = "--restart-wait-start-ticks";
    private const long MaximumCrashLogBytes = 256 * 1024;
    private static readonly TimeSpan ClientHandoffTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RestartPredecessorWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrivateOnlyUiHandoffTimeout = TimeSpan.FromMilliseconds(1_250);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly object CrashLogSync = new();
    private static BrowserApplicationContext? currentApplicationContext;
    private static int crashHandlersRegistered;

    [STAThread]
    private static void Main(string[] args)
    {
        var startupArguments = ParseStartupArguments(args);
        if (startupArguments.RestartAfterProcessId is { } predecessorProcessId
            && !WaitForRestartPredecessor(
                predecessorProcessId,
                RestartPredecessorWaitTimeout,
                startupArguments.RestartAfterStartTimeUtcTicks,
                Environment.ProcessPath))
        {
            ShowRestartHandoffFailure();
            return;
        }

        var startupAddress = ResolveStartupAddressCandidates(startupArguments.NavigationArguments);
        var singleInstanceIdentity = GetCurrentUserSingleInstanceIdentity();

        using var mutex = CreateSingleInstanceMutex(singleInstanceIdentity.MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (!SendNavigationToRunningInstance(singleInstanceIdentity.PipeName, startupAddress))
            {
                ShowNavigationHandoffFailure(startupAddress);
            }
            return;
        }

        TryStartManagedStartupOptimization();
        _ = Task.Run(static () =>
        {
            try { WebView2LoaderBootstrap.EnsureLoaded(); }
            catch { }
        });
        RegisterGlobalExceptionHandlers();

        var startupHandoffs = new StartupHandoffRouter(MaximumQueuedStartupHandoffs);
        using var server = new SingleInstanceServer(
            singleInstanceIdentity.PipeName,
            startupHandoffs.TryAccept);
        server.Start();

        ApplicationConfiguration.Initialize();
        var shortcutFilter = new IncognitoShortcutMessageFilter();
        Application.AddMessageFilter(shortcutFilter);
        var form = new MainForm(true, null, startupAddress);
        using var applicationContext = new BrowserApplicationContext();
        Volatile.Write(ref currentApplicationContext, applicationContext);
        try
        {
            RegisterTopLevelWindow(form);
            startupHandoffs.Attach(applicationContext.TryReceiveExternalNavigation);
            form.Show();
            Application.Run(applicationContext);
        }
        finally
        {
            Application.RemoveMessageFilter(shortcutFilter);
            Volatile.Write(ref currentApplicationContext, null);
        }
    }

    internal static void RegisterGlobalExceptionHandlers()
    {
        if (Interlocked.Exchange(ref crashHandlersRegistered, 1) != 0) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            RecordUnhandledException("Application.ThreadException", e.Exception);
            TryEmergencyFlushOpenWindows();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                RecordUnhandledException("AppDomain.UnhandledException", exception);
            }
            else
            {
                RecordUnhandledDiagnostic("AppDomain.UnhandledException", e.ExceptionObject?.ToString() ?? "Unknown fault");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RecordUnhandledException("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    private static void TryEmergencyFlushOpenWindows()
    {
        try
        {
            currentApplicationContext?.FlushOpenSessionsBestEffort();
        }
        catch
        {
            // Crash-path persistence must never throw.
        }
    }

    internal static void RecordUnhandledException(string source, Exception? error)
    {
        RecordUnhandledDiagnostic(source, error?.ToString() ?? "Unknown exception");
    }

    private static void RecordUnhandledDiagnostic(string source, string detail)
    {
        try
        {
            var logsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MishaWeb",
                "Logs");
            var logPath = Path.Combine(logsDirectory, "crash.log");
            lock (CrashLogSync)
            {
                Directory.CreateDirectory(logsDirectory);
                if (File.Exists(logPath) && new FileInfo(logPath).Length > MaximumCrashLogBytes)
                {
                    File.Move(logPath, logPath + ".old", true);
                }
                File.AppendAllText(
                    logPath,
                    $"[{DateTimeOffset.UtcNow:O}] {source}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Crash logging is best-effort.
        }
    }

    internal static void RegisterTopLevelWindow(MainForm window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var applicationContext = Volatile.Read(ref currentApplicationContext)
            ?? throw new InvalidOperationException("The MishaWeb application context is not running.");
        if (!applicationContext.Register(window))
        {
            throw new InvalidOperationException("The browser window could not be registered.");
        }
    }

    internal static BrowserRestartRequestResult RequestBrowserProcessRestart(MainForm requestingWindow)
    {
        ArgumentNullException.ThrowIfNull(requestingWindow);
        var applicationContext = Volatile.Read(ref currentApplicationContext);
        return applicationContext is null
            ? BrowserRestartRequestResult.Unavailable
            : applicationContext.RequestBrowserProcessRestart(requestingWindow);
    }

    private static SingleInstanceIdentity GetCurrentUserSingleInstanceIdentity()
    {
        using var windowsIdentity = WindowsIdentity.GetCurrent();
        var userSid = windowsIdentity.User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new InvalidOperationException("MishaWeb could not determine the current Windows user identity.");
        }

        return CreateSingleInstanceIdentity(userSid);
    }

    internal static SingleInstanceIdentity CreateSingleInstanceIdentity(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(userSid));
        var userKey = Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
        return new SingleInstanceIdentity(
            $"Global\\MishaWeb-BrowserInstance-{userKey}",
            $"MishaWeb-BrowserInstancePipe-{userKey}");
    }

    private static Mutex CreateSingleInstanceMutex(string mutexName, out bool isFirstInstance)
    {
        try
        {
            return new Mutex(true, mutexName, out isFirstInstance);
        }
        catch (UnauthorizedAccessException) when (mutexName.StartsWith("Global\\", StringComparison.Ordinal))
        {
            var localName = "Local\\" + mutexName["Global\\".Length..];
            return new Mutex(true, localName, out isFirstInstance);
        }
    }

    private static void TryStartManagedStartupOptimization()
    {
        // The runtime ignores profile optimization on single-core machines. Skip the
        // directory access too so those systems pay no setup cost for this optional path.
        if (Environment.ProcessorCount < 2)
        {
            return;
        }

        try
        {
            var profileRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MishaWeb",
                "JitProfiles");

            Directory.CreateDirectory(profileRoot);
            System.Runtime.ProfileOptimization.SetProfileRoot(profileRoot);
            System.Runtime.ProfileOptimization.StartProfile("startup.profile");
        }
        catch (Exception)
        {
            // Startup profiling is opportunistic. A locked, read-only, or unavailable
            // profile folder must never prevent the browser from opening normally.
        }
    }

    private static bool SendNavigationToRunningInstance(string pipeName, string? startupAddress)
    {
        try
        {
            return TrySendNavigationToPipeAsync(pipeName, startupAddress, ClientHandoffTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception error)
        {
            Debug.WriteLine($"MishaWeb navigation handoff failed: {error}");
            return false;
        }
    }

    internal static async Task<bool> TrySendNavigationToPipeAsync(
        string pipeName,
        string? startupAddress,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (timeout <= TimeSpan.Zero) return false;

        var normalized = string.IsNullOrWhiteSpace(startupAddress) ? string.Empty : startupAddress;
        try
        {
            if (StrictUtf8.GetByteCount(normalized) > MaximumIpcRequestBytes) return false;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        using var handoffCancellation = new CancellationTokenSource(timeout);
        while (!handoffCancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(750, handoffCancellation.Token).ConfigureAwait(false);

                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    handoffCancellation.Token);
                requestCancellation.CancelAfter(TimeSpan.FromSeconds(2));
                await SendMessageAsync(pipe, normalized, requestCancellation.Token).ConfigureAwait(false);

                var acknowledgement = new byte[1];
                var acknowledgementBytes = await ReadExactlyAsync(
                        pipe,
                        acknowledgement,
                        0,
                        acknowledgement.Length,
                        requestCancellation.Token)
                    .ConfigureAwait(false);
                if (acknowledgementBytes != 1)
                {
                    throw new IOException("The running browser closed the handoff before acknowledging it.");
                }

                return acknowledgement[0] == 1;
            }
            catch (OperationCanceledException) when (handoffCancellation.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception error) when (
                error is IOException
                    or TimeoutException
                    or UnauthorizedAccessException
                    or OperationCanceledException)
            {
                try
                {
                    await Task.Delay(100, handoffCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static async Task SendMessageAsync(
        Stream pipe,
        string normalized,
        CancellationToken cancellationToken)
    {
        var payload = StrictUtf8.GetBytes(normalized);
        if (payload.Length > MaximumIpcRequestBytes)
        {
            throw new InvalidDataException("The navigation handoff is too large.");
        }

        var length = BitConverter.GetBytes(payload.Length);
        await pipe.WriteAsync(length.AsMemory(), cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(offset + totalRead, count - totalRead),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) return totalRead;
            totalRead += read;
        }

        return totalRead;
    }

    private static void ShowNavigationHandoffFailure(string? startupAddress)
    {
        var requestDescription = string.IsNullOrWhiteSpace(startupAddress)
            ? "the new-window request"
            : "the page address";
        var message =
            $"MishaWeb is already running, but it could not pass {requestDescription} "
            + "to the open window after several retries. Switch to the open MishaWeb window "
            + "and try again.";
        try
        {
            MessageBox.Show(
                message,
                "MishaWeb handoff failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception error)
        {
            Debug.WriteLine($"MishaWeb could not display the handoff warning: {error}");
        }
    }

    internal static StartupArguments ParseStartupArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        int? restartAfterProcessId = null;
        long? restartAfterStartTimeUtcTicks = null;
        var navigationArguments = new List<string>(args.Length);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.Equals(RestartAfterPidArgument, StringComparison.Ordinal))
            {
                // Always consume the value paired with this internal switch. A malformed
                // PID must not accidentally become a search/navigation request.
                if (index + 1 >= args.Length) continue;
                var processIdText = args[++index];
                if (restartAfterProcessId is null
                    && int.TryParse(
                        processIdText,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var processId)
                    && processId > 0
                    && processId != Environment.ProcessId)
                {
                    restartAfterProcessId = processId;
                }
                continue;
            }

            if (argument.Equals(RestartAfterStartTicksArgument, StringComparison.Ordinal))
            {
                if (index + 1 >= args.Length) continue;
                var ticksText = args[++index];
                if (restartAfterStartTimeUtcTicks is null
                    && long.TryParse(
                        ticksText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var ticks)
                    && ticks > 0)
                {
                    restartAfterStartTimeUtcTicks = ticks;
                }
                continue;
            }

            navigationArguments.Add(argument);
        }

        return new StartupArguments(restartAfterProcessId, navigationArguments.ToArray(), restartAfterStartTimeUtcTicks);
    }

    internal static bool WaitForRestartPredecessor(
        int processId,
        TimeSpan timeout,
        long? expectedStartTimeUtcTicks = null,
        string? expectedExecutablePath = null)
    {
        if (processId <= 0 || processId == Environment.ProcessId || timeout < TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            using var predecessor = Process.GetProcessById(processId);
            if (predecessor.HasExited) return true;
            if (!MatchesRestartPredecessorIdentity(predecessor, expectedStartTimeUtcTicks, expectedExecutablePath))
            {
                // The OS recycled the PID to an unrelated process; the original predecessor already exited.
                return true;
            }

            var boundedMilliseconds = (int)Math.Min(
                int.MaxValue,
                Math.Ceiling(timeout.TotalMilliseconds));
            return predecessor.WaitForExit(boundedMilliseconds);
        }
        catch (ArgumentException)
        {
            // The predecessor already exited before the successor attached.
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception error)
        {
            Debug.WriteLine($"MishaWeb could not wait for its restart predecessor: {error}");
            return false;
        }
    }

    internal static bool MatchesRestartPredecessorIdentity(
        Process predecessor,
        long? expectedStartTimeUtcTicks,
        string? expectedExecutablePath)
    {
        if (expectedStartTimeUtcTicks is { } expectedTicks)
        {
            try
            {
                var actualTicks = predecessor.StartTime.ToUniversalTime().Ticks;
                if (Math.Abs(actualTicks - expectedTicks) > TimeSpan.TicksPerSecond * 2)
                {
                    return false;
                }
            }
            catch
            {
                // Access denied or exited while inspecting StartTime; fall back cleanly.
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedExecutablePath))
        {
            try
            {
                var actualModulePath = predecessor.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(actualModulePath)
                    && !string.Equals(
                        Path.GetFullPath(actualModulePath),
                        Path.GetFullPath(expectedExecutablePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch
            {
                // Access denied or exited while inspecting MainModule; rely on StartTime/WaitForExit.
            }
        }

        return true;
    }

    internal static string? ResolveStartupAddress(string[] args) =>
        ResolveStartupAddressCandidates(ParseStartupArguments(args).NavigationArguments);

    private static string? ResolveStartupAddressCandidates(string[] args)
    {
        if (args.Length == 0) return null;

        var nonSwitchArgs = args.Where(a => !a.StartsWith('-') && !a.StartsWith('/')).ToArray();
        if (nonSwitchArgs.Length == 0) return null;
        args = nonSwitchArgs;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string>();

        var full = string.Join(' ', args).Trim();
        if (full.Length > 0)
        {
            candidates.Add(full);
            seen.Add(full);
        }

        for (var start = 0; start < args.Length; start++)
        {
            var tail = string.Join(' ', args.Skip(start)).Trim();
            if (tail.Length == 0 || !seen.Add(tail)) continue;
            candidates.Add(tail);
        }

        for (var index = 0; index < args.Length; index++)
        {
            var candidate = args[index].Trim();
            if (candidate.Length == 0 || !seen.Add(candidate)) continue;
            candidates.Add(candidate);
        }

        foreach (var candidate in candidates)
        {
            var normalized = candidate.Trim().Trim('"', '\'');
            if (normalized.Length == 0) continue;
            if (normalized[0] == '-' || normalized[0] == '/') continue;

            var resolution = BrowserPolicy.ResolveAddress(normalized);
            if (resolution.Error is null)
            {
                return normalized;
            }
        }

        return null;
    }

    private static void ShowRestartHandoffFailure()
    {
        MessageBox.Show(
            "The previous MishaWeb process did not finish closing. MishaWeb was not started again so the two browser processes cannot compete for the same profile. Please close the existing window and open MishaWeb again.",
            "MishaWeb restart did not finish",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    internal readonly record struct SingleInstanceIdentity(string MutexName, string PipeName);

    internal readonly record struct StartupArguments(
        int? RestartAfterProcessId,
        string[] NavigationArguments,
        long? RestartAfterStartTimeUtcTicks = null);

    internal readonly record struct BrowserRestartWindowState(
        bool IsPrivate,
        int ActiveDownloadCount,
        int ActiveMediaCaptureCount,
        int ActiveExtensionMutationCount,
        int NonRestorableTabCount);

    internal readonly record struct BrowserRestartGuard(
        int WindowCount,
        int PrivateWindowCount,
        int ActiveDownloadCount,
        int ActiveMediaCaptureCount,
        int ActiveExtensionMutationCount,
        int NonRestorableTabCount)
    {
        internal bool HasActiveWork => ActiveDownloadCount > 0 || ActiveMediaCaptureCount > 0;
        internal bool HasActiveExtensionMutations => ActiveExtensionMutationCount > 0;
        internal bool RequiresConfirmation =>
            WindowCount > 1
            || PrivateWindowCount > 0
            || HasActiveWork
            || NonRestorableTabCount > 0;
    }

    internal enum BrowserRestartRequestResult
    {
        Started,
        Canceled,
        StateChanged,
        ExtensionOperationInProgress,
        SessionSaveFailed,
        LaunchFailed,
        Unavailable
    }

    internal sealed class BrowserApplicationContext : ApplicationContext
    {
        private readonly object sync = new();
        private readonly List<MainForm> windows = new();
        private readonly Func<MainForm, BrowserRestartGuard, bool> confirmRestart;
        private readonly Func<int, bool> launchRestartSuccessor;
        private readonly Action exitThread;
        private readonly Func<MainForm, bool> persistRestartSession;
        private readonly Func<MainForm> createNormalWindow;
        private readonly Action<MainForm> showNormalWindow;
        private bool exiting;
        private bool restartInProgress;
        private int windowRegistryVersion;
        private int exitThreadRequestCount;

        internal BrowserApplicationContext(
            Func<MainForm, BrowserRestartGuard, bool>? confirmRestart = null,
            Func<int, bool>? launchRestartSuccessor = null,
            Action? exitThread = null,
            Func<MainForm, bool>? persistRestartSession = null,
            Func<MainForm>? createNormalWindow = null,
            Action<MainForm>? showNormalWindow = null)
        {
            this.confirmRestart = confirmRestart ?? ConfirmBrowserProcessRestart;
            this.launchRestartSuccessor = launchRestartSuccessor ?? LaunchRestartSuccessor;
            this.exitThread = exitThread ?? ExitThread;
            this.persistRestartSession = persistRestartSession
                ?? (window => window.TryPersistSessionForBrowserProcessRestart());
            this.createNormalWindow = createNormalWindow
                ?? (() => new MainForm(true, null, null));
            this.showNormalWindow = showNormalWindow ?? (window => window.Show());
        }

        internal int WindowCount
        {
            get
            {
                lock (sync) return windows.Count;
            }
        }

        internal int ExitThreadRequestCountForTesting
        {
            get
            {
                lock (sync) return exitThreadRequestCount;
            }
        }

        internal bool Register(MainForm window)
        {
            ArgumentNullException.ThrowIfNull(window);
            lock (sync)
            {
                if (exiting || restartInProgress || window.IsDisposed || window.Disposing) return false;
                if (windows.Contains(window)) return true;

                windows.Add(window);
                windowRegistryVersion++;
                window.FormClosed += OnWindowClosed;
                return true;
            }
        }

        internal BrowserRestartGuard CaptureBrowserRestartGuard()
        {
            MainForm[] candidates;
            lock (sync)
            {
                candidates = windows
                    .Where(window => !window.IsDisposed && !window.Disposing)
                    .ToArray();
            }
            return AggregateBrowserRestartGuard(
                candidates.Select(window => window.CaptureBrowserRestartWindowState()));
        }

        internal void FlushOpenSessionsBestEffort()
        {
            MainForm[] candidates;
            lock (sync)
            {
                candidates = windows
                    .Where(window => !window.IsDisposed && !window.Disposing && !window.IsPrivateBrowserWindow)
                    .ToArray();
            }
            foreach (var window in candidates)
            {
                try { window.TryEmergencySaveState(); }
                catch { }
            }
        }

        internal BrowserRestartRequestResult RequestBrowserProcessRestart(MainForm requestingWindow)
        {
            ArgumentNullException.ThrowIfNull(requestingWindow);
            MainForm[] candidates;
            int registryVersion;
            lock (sync)
            {
                if (exiting
                    || restartInProgress
                    || requestingWindow.IsDisposed
                    || requestingWindow.Disposing
                    || !windows.Contains(requestingWindow))
                {
                    return BrowserRestartRequestResult.Unavailable;
                }

                candidates = windows
                    .Where(window => !window.IsDisposed && !window.Disposing)
                    .ToArray();
                registryVersion = windowRegistryVersion;
            }

            var guard = AggregateBrowserRestartGuard(
                candidates.Select(window => window.CaptureBrowserRestartWindowState()));

            if (guard.HasActiveExtensionMutations)
            {
                return BrowserRestartRequestResult.ExtensionOperationInProgress;
            }

            if (guard.RequiresConfirmation && !confirmRestart(requestingWindow, guard))
            {
                return BrowserRestartRequestResult.Canceled;
            }

            MainForm[] currentCandidates;
            lock (sync)
            {
                if (exiting || restartInProgress || registryVersion != windowRegistryVersion)
                {
                    return BrowserRestartRequestResult.StateChanged;
                }
                currentCandidates = windows
                    .Where(window => !window.IsDisposed && !window.Disposing)
                    .ToArray();
            }

            var currentGuard = AggregateBrowserRestartGuard(
                currentCandidates.Select(window => window.CaptureBrowserRestartWindowState()));
            if (currentGuard.HasActiveExtensionMutations)
            {
                return BrowserRestartRequestResult.ExtensionOperationInProgress;
            }

            lock (sync)
            {
                if (exiting
                    || restartInProgress
                    || registryVersion != windowRegistryVersion
                    || !guard.Equals(currentGuard)
                    || !candidates.SequenceEqual(currentCandidates))
                {
                    return BrowserRestartRequestResult.StateChanged;
                }

                restartInProgress = true;
            }

            foreach (var window in candidates.Where(window => !window.IsPrivateBrowserWindow))
            {
                var saved = false;
                try
                {
                    saved = persistRestartSession(window);
                }
                catch (Exception error)
                {
                    Debug.WriteLine($"MishaWeb could not persist a restart session: {error}");
                }
                if (saved) continue;
                foreach (var candidate in candidates) candidate.CancelPreparedBrowserProcessRestart();
                lock (sync) restartInProgress = false;
                return BrowserRestartRequestResult.SessionSaveFailed;
            }

            if (!launchRestartSuccessor(Environment.ProcessId))
            {
                foreach (var candidate in candidates) candidate.CancelPreparedBrowserProcessRestart();
                lock (sync) restartInProgress = false;
                return BrowserRestartRequestResult.LaunchFailed;
            }

            // Private windows never persist and close first. The normal window closes
            // last so its normal FormClosing path writes the restorable session after
            // every process-wide restart has been approved.
            var anyWindowRefusedClose = false;
            foreach (var window in candidates.OrderBy(window => window.IsPrivateBrowserWindow ? 0 : 1))
            {
                if (window.IsDisposed || window.Disposing) continue;
                window.PrepareForBrowserProcessRestart();
                window.Close();
                if (!window.IsDisposed && !window.Disposing)
                {
                    anyWindowRefusedClose = true;
                }
            }

            if (anyWindowRefusedClose)
            {
                foreach (var candidate in candidates)
                {
                    if (!candidate.IsDisposed && !candidate.Disposing)
                    {
                        candidate.CancelPreparedBrowserProcessRestart();
                    }
                }
                lock (sync) restartInProgress = false;
                return BrowserRestartRequestResult.Canceled;
            }

            return BrowserRestartRequestResult.Started;
        }

        internal static BrowserRestartGuard AggregateBrowserRestartGuard(
            IEnumerable<BrowserRestartWindowState> states)
        {
            ArgumentNullException.ThrowIfNull(states);
            var windowCount = 0;
            var privateWindowCount = 0;
            var activeDownloadCount = 0;
            var activeMediaCaptureCount = 0;
            var activeExtensionMutationCount = 0;
            var nonRestorableTabCount = 0;
            foreach (var state in states)
            {
                windowCount++;
                if (state.IsPrivate) privateWindowCount++;
                activeDownloadCount += Math.Max(0, state.ActiveDownloadCount);
                activeMediaCaptureCount += Math.Max(0, state.ActiveMediaCaptureCount);
                activeExtensionMutationCount += Math.Max(0, state.ActiveExtensionMutationCount);
                nonRestorableTabCount += Math.Max(0, state.NonRestorableTabCount);
            }

            return new BrowserRestartGuard(
                windowCount,
                privateWindowCount,
                activeDownloadCount,
                activeMediaCaptureCount,
                activeExtensionMutationCount,
                nonRestorableTabCount);
        }

        internal bool TryReceiveExternalNavigation(string? address)
        {
            if (!string.IsNullOrWhiteSpace(address)
                && BrowserPolicy.ResolveAddress(address).Error is not null)
            {
                return false;
            }

            MainForm[] candidates;
            lock (sync)
            {
                if (exiting || restartInProgress) return false;
                candidates = windows.ToArray();
            }

            foreach (var candidate in candidates.Where(window => !window.IsPrivateBrowserWindow))
            {
                if (candidate.IsDisposed
                    || candidate.Disposing
                    || candidate.IsClosingForApplicationContext) continue;
                if (candidate.ReceiveExternalNavigation(address)) return true;
            }

            var dispatcher = candidates.FirstOrDefault(window =>
                !window.IsDisposed
                && !window.Disposing
                && !window.IsClosingForApplicationContext
                && window.IsHandleCreated);
            if (dispatcher is null) return false;
            if (!dispatcher.InvokeRequired) return CreateOrRouteNormalWindow(address);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                dispatcher.BeginInvoke((Action)(() =>
                {
                    try { completion.TrySetResult(CreateOrRouteNormalWindow(address)); }
                    catch (Exception error) { completion.TrySetException(error); }
                }));
                if (!completion.Task.Wait(PrivateOnlyUiHandoffTimeout)) return false;
                return completion.Task.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Debug.WriteLine($"MishaWeb could not create a normal handoff window: {error}");
                return false;
            }
        }

        private bool CreateOrRouteNormalWindow(string? address)
        {
            MainForm? existingNormal;
            lock (sync)
            {
                if (exiting || restartInProgress) return false;
                existingNormal = windows.FirstOrDefault(window =>
                    !window.IsPrivateBrowserWindow
                    && !window.IsDisposed
                    && !window.Disposing
                    && !window.IsClosingForApplicationContext);
            }
            if (existingNormal is not null)
            {
                return existingNormal.ReceiveExternalNavigation(address);
            }

            MainForm? created = null;
            var registered = false;
            var accepted = false;
            try
            {
                created = createNormalWindow();
                if (created.IsPrivateBrowserWindow) return false;
                registered = Register(created);
                if (!registered) return false;
                showNormalWindow(created);
                accepted = created.ReceiveExternalNavigation(address);
                return accepted;
            }
            catch (Exception error)
            {
                Debug.WriteLine($"MishaWeb could not open a normal handoff window: {error}");
                return false;
            }
            finally
            {
                if (created is not null && !accepted)
                {
                    if (registered) UnregisterFailedWindow(created);
                    try { created.Dispose(); }
                    catch { }
                }
            }
        }

        private void UnregisterFailedWindow(MainForm window)
        {
            lock (sync)
            {
                window.FormClosed -= OnWindowClosed;
                if (windows.Remove(window)) windowRegistryVersion++;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (sync)
                {
                    exiting = true;
                    foreach (var window in windows)
                    {
                        window.FormClosed -= OnWindowClosed;
                    }
                    windows.Clear();
                    windowRegistryVersion++;
                }
            }

            base.Dispose(disposing);
        }

        private void OnWindowClosed(object? sender, FormClosedEventArgs e)
        {
            var shouldExit = false;
            lock (sync)
            {
                if (sender is MainForm window)
                {
                    window.FormClosed -= OnWindowClosed;
                    if (windows.Remove(window)) windowRegistryVersion++;
                }

                if (!exiting && windows.Count == 0)
                {
                    exiting = true;
                    shouldExit = true;
                }
            }

            if (shouldExit) RequestExitThread();
        }

        private void RequestExitThread()
        {
            lock (sync) exitThreadRequestCount++;
            exitThread();
        }

        private static bool ConfirmBrowserProcessRestart(
            MainForm requestingWindow,
            BrowserRestartGuard guard)
        {
            var details = new List<string>();
            if (guard.WindowCount > 1)
            {
                details.Add($"{guard.WindowCount} browser windows will close");
            }
            if (guard.PrivateWindowCount > 0)
            {
                details.Add(guard.PrivateWindowCount == 1
                    ? "the private window and its tabs cannot be restored"
                    : $"{guard.PrivateWindowCount} private windows and their tabs cannot be restored");
            }
            if (guard.ActiveDownloadCount > 0)
            {
                details.Add(guard.ActiveDownloadCount == 1
                    ? "one active download will be canceled"
                    : $"{guard.ActiveDownloadCount} active downloads will be canceled");
            }
            if (guard.ActiveMediaCaptureCount > 0)
            {
                details.Add(guard.ActiveMediaCaptureCount == 1
                    ? "one tab using the camera or microphone will be interrupted"
                    : $"{guard.ActiveMediaCaptureCount} tabs using the camera or microphone will be interrupted");
            }
            if (guard.NonRestorableTabCount > 0)
            {
                details.Add(guard.NonRestorableTabCount == 1
                    ? "one local-file or extension tab cannot be restored automatically"
                    : $"{guard.NonRestorableTabCount} local-file or extension tabs cannot be restored automatically");
            }

            var message = "MishaWeb must close every browser window to load the WebView2 update.";
            if (details.Count > 0)
            {
                message += Environment.NewLine + Environment.NewLine
                    + string.Join("; ", details) + ".";
            }
            message += Environment.NewLine + Environment.NewLine + "Restart MishaWeb now?";
            return MessageBox.Show(
                requestingWindow,
                message,
                "Restart MishaWeb to update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private static bool LaunchRestartSuccessor(int predecessorProcessId)
        {
            try
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    return false;
                }

                long? predecessorStartTimeUtcTicks = null;
                try
                {
                    using var currentProcess = Process.GetCurrentProcess();
                    predecessorStartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
                }
                catch
                {
                    // Start time is best-effort metadata to guard against PID reuse.
                }

                var startInfo = CreateRestartProcessStartInfo(
                    executablePath,
                    predecessorProcessId,
                    predecessorStartTimeUtcTicks);
                using var successor = Process.Start(startInfo);
                return successor is not null;
            }
            catch (Exception error)
            {
                Debug.WriteLine($"MishaWeb could not launch its restart successor: {error}");
                return false;
            }
        }

        internal static ProcessStartInfo CreateRestartProcessStartInfo(
            string executablePath,
            int predecessorProcessId)
        {
            return CreateRestartProcessStartInfo(executablePath, predecessorProcessId, null);
        }

        internal static ProcessStartInfo CreateRestartProcessStartInfo(
            string executablePath,
            int predecessorProcessId,
            long? predecessorStartTimeUtcTicks)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            if (predecessorProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(predecessorProcessId));
            var exactExecutablePath = Path.GetFullPath(executablePath);
            var startInfo = new ProcessStartInfo(exactExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exactExecutablePath) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add(RestartAfterPidArgument);
            startInfo.ArgumentList.Add(predecessorProcessId.ToString(CultureInfo.InvariantCulture));
            if (predecessorStartTimeUtcTicks is { } startTicks && startTicks > 0)
            {
                startInfo.ArgumentList.Add(RestartAfterStartTicksArgument);
                startInfo.ArgumentList.Add(startTicks.ToString(CultureInfo.InvariantCulture));
            }
            return startInfo;
        }
    }

    internal sealed class StartupHandoffRouter
    {
        private readonly object sync = new();
        private readonly int capacity;
        private readonly Queue<string> pending = new();
        private readonly HashSet<string> pendingSet = new(StringComparer.Ordinal);
        private Func<string?, bool>? receiver;

        internal StartupHandoffRouter(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
        }

        internal int PendingCount
        {
            get
            {
                lock (sync) return pending.Count;
            }
        }

        internal bool TryAccept(string? address)
        {
            var normalized = string.IsNullOrWhiteSpace(address) ? string.Empty : address;
            Func<string?, bool>? activeReceiver;
            lock (sync)
            {
                activeReceiver = receiver;
                if (activeReceiver is null)
                {
                    if (pendingSet.Contains(normalized)) return true;
                    if (pending.Count >= capacity) return false;

                    pending.Enqueue(normalized);
                    pendingSet.Add(normalized);
                    return true;
                }
            }

            return TryDeliver(activeReceiver, normalized);
        }

        internal bool Attach(Func<string?, bool> newReceiver)
        {
            ArgumentNullException.ThrowIfNull(newReceiver);
            List<string> queued;
            lock (sync)
            {
                if (receiver is not null) return false;
                queued = new List<string>(pending);
                pending.Clear();
                pendingSet.Clear();
                receiver = newReceiver;
            }

            foreach (var address in queued)
            {
                _ = TryDeliver(newReceiver, address);
            }
            return true;
        }

        private static bool TryDeliver(Func<string?, bool> target, string address)
        {
            try
            {
                return target(address.Length == 0 ? null : address);
            }
            catch (Exception error)
            {
                Debug.WriteLine($"MishaWeb could not queue a navigation handoff: {error}");
                return false;
            }
        }
    }

    internal sealed class SingleInstanceServer : IDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
        private readonly string pipeName;
        private readonly Func<string?, bool> onNavigation;
        private readonly CancellationTokenSource cancellation = new();
        private Task? serverTask;

        internal SingleInstanceServer(string pipeName, Func<string?, bool> onNavigation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
            this.pipeName = pipeName;
            this.onNavigation = onNavigation;
        }

        public void Start()
        {
            serverTask ??= Task.Run(ListenLoopAsync);
        }

        public void Dispose()
        {
            cancellation.Cancel();
            try { serverTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            cancellation.Dispose();
        }

        private async Task ListenLoopAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    requestCancellation.CancelAfter(RequestTimeout);
                    var request = await ReadMessageAsync(server, requestCancellation.Token).ConfigureAwait(false);
                    var accepted = false;
                    if (request is not null
                        && (request.Length == 0
                            || BrowserPolicy.ResolveAddress(request).Error is null))
                    {
                        try
                        {
                            accepted = onNavigation(request);
                        }
                        catch (Exception error)
                        {
                            Debug.WriteLine($"MishaWeb IPC receiver rejected a handoff: {error}");
                        }
                    }
                    await server.WriteAsync(
                        new byte[] { accepted ? (byte)1 : (byte)0 },
                        requestCancellation.Token).ConfigureAwait(false);
                    await server.FlushAsync(requestCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    // A stalled client only forfeits its own handoff. Keep the
                    // server alive for the next legitimate browser launch.
                }
                catch
                {
                    try
                    {
                        await Task.Delay(120, cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }

        private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
        {
            var lengthBuffer = new byte[4];
            var read = await ReadExactlyAsync(stream, lengthBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
            if (read < 4) return null;

            var length = BitConverter.ToInt32(lengthBuffer, 0);
            if (length < 0 || length > MaximumIpcRequestBytes) return null;
            if (length == 0) return string.Empty;

            var payload = new byte[length];
            read = await ReadExactlyAsync(stream, payload, 0, length, cancellationToken).ConfigureAwait(false);
            if (read < length) return null;
            try
            {
                return StrictUtf8.GetString(payload);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

    }
}

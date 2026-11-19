using System.Runtime.InteropServices;

namespace MishaWeb;

internal readonly record struct SystemMemorySnapshot(
    uint MemoryLoadPercent,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    int LogicalProcessorCount,
    bool IsValid)
{
    private const ulong FourGibibytes = 4UL * 1024 * 1024 * 1024;

    public bool IsTinyMachine => TotalPhysicalBytes is > 0 and <= FourGibibytes;
    public bool IsLowSpecMachine => IsTinyMachine || LogicalProcessorCount is > 0 and <= 2;
}

internal static class SystemResourceInfo
{
    public static SystemMemorySnapshot CaptureMemory()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        var logicalProcessorCount = Environment.ProcessorCount;
        return GlobalMemoryStatusEx(ref status)
            ? new SystemMemorySnapshot(
                status.MemoryLoad,
                status.TotalPhysical,
                status.AvailablePhysical,
                logicalProcessorCount,
                IsValid: true)
            : new SystemMemorySnapshot(
                MemoryLoadPercent: 0,
                TotalPhysicalBytes: 0,
                AvailablePhysicalBytes: 0,
                LogicalProcessorCount: logicalProcessorCount,
                IsValid: false);
    }

    public static bool TrimCurrentProcessWorkingSet()
    {
        try
        {
            return EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            return false;
        }
    }

    public static bool TrimProcessWorkingSet(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            var handle = OpenProcess(ProcessQueryLimitedAndSetQuota, false, processId);
            if (handle == IntPtr.Zero)
            {
                handle = OpenProcess(ProcessQueryAndSetQuota, false, processId);
            }
            if (handle == IntPtr.Zero) return false;
            try
            {
                return EmptyWorkingSet(handle);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies or clears Windows EcoQoS (Efficiency Mode) power throttling on a target process
    /// so background or minimized WebView2 renderers are scheduled onto efficiency cores.
    /// </summary>
    public static bool TrySetProcessEfficiencyMode(int processId, bool enable)
    {
        if (processId <= 0) return false;
        try
        {
            var handle = OpenProcess(ProcessSetInformationAccess, false, processId);
            if (handle == IntPtr.Zero) return false;
            try
            {
                var state = new ProcessPowerThrottlingState
                {
                    Version = ProcessPowerThrottlingCurrentVersion,
                    ControlMask = ProcessPowerThrottlingExecutionSpeed,
                    StateMask = enable ? ProcessPowerThrottlingExecutionSpeed : 0u
                };
                var throttled = SetProcessInformation(
                    handle,
                    ProcessPowerThrottlingInformationClass,
                    ref state,
                    (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
                _ = SetPriorityClass(handle, enable ? IdlePriorityClass : NormalPriorityClass);
                return throttled;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return false;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessSetInformationAccess = 0x0200;
    private const uint ProcessQueryLimitedAndSetQuota = ProcessQueryLimitedInformation | ProcessSetQuota;
    private const uint ProcessQueryAndSetQuota = ProcessQueryInformation | ProcessSetQuota;
    private const int ProcessPowerThrottlingInformationClass = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private const uint NormalPriorityClass = 0x00000020;
    private const uint IdlePriorityClass = 0x00000040;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StartupPorts;

internal static class NativeProcess
{
    internal const uint TerminateAccess = 1;
    internal const uint QueryAccess = 0x1000;
    internal const uint SynchronizeAccess = 0x100000;
    internal const uint WaitSignaled = 0;
    internal const uint WaitTimeout = 258;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process,
        out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process,
        uint flags, StringBuilder path, ref int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process,
        out ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeJobHandle CreateJobObject(nint attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeJobHandle OpenJobObject(uint access, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsProcessInJob(SafeProcessHandle process, SafeJobHandle job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(SafeJobHandle job, int informationClass,
        out JobAccounting information, uint size, nint returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcess(string application, StringBuilder commandLine,
        nint processAttributes, nint threadAttributes, bool inheritHandles, uint creationFlags,
        nint environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeWaitHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32First(SafeWaitHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32Next(SafeWaitHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(nint handle);

    internal static string? ImagePath(SafeProcessHandle process)
    {
        var path = new StringBuilder(1024);
        var length = path.Capacity;
        if (QueryFullProcessImageName(process, 0, path, ref length))
            return path.ToString();
        if (Marshal.GetLastWin32Error() != 122)
            return null;
        path.EnsureCapacity(32768);
        length = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
    }

    internal static Win32Exception Error() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool,
            QuotaPeakNonPagedPool, QuotaNonPagedPool, PagefileUsage, PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaultCount, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedLength;
        public nint ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint ProcessHandle, ThreadHandle;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Executable;
    }
}

internal sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    protected override bool ReleaseHandle() => NativeProcess.CloseHandle(handle);
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static StartupPorts.UiText;

namespace StartupPorts;

internal readonly record struct PowerOptions(bool Sleep, bool Hibernate)
{
    public bool Supports(PowerAction action) => action switch
    {
        PowerAction.Shutdown or PowerAction.Restart => true,
        PowerAction.Sleep => Sleep,
        PowerAction.Hibernate => Hibernate,
        _ => false,
    };
}

internal static class WindowsPower
{
    private const uint Reboot = 0x00000002;
    private const uint PowerOff = 0x00000008;
    private const uint Planned = 0x80000000;
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x08;
    private const uint PrivilegeEnabled = 0x02;
    private const int NotAllAssigned = 1300;
    private const int NotSupported = 50;

    public static PowerOptions Available()
    {
        if (!GetPwrCapabilities(out var caps))
            return new(false, false);
        return new(caps.S1 != 0 || caps.S2 != 0 || caps.S3 != 0 || caps.ModernStandby != 0,
            caps.S4 != 0 && caps.HibernationFile != 0 && caps.HibernationFileType != 1); // 1 is a reduced file used for Fast Startup
    }

    public static int Run(PowerAction action)
    {
        if (!Available().Supports(action))
            return NotSupported;
        var error = EnableShutdownPrivilege();
        if (error != 0)
            return error;

        var accepted = action switch
        {
            PowerAction.Shutdown => ExitWindowsEx(PowerOff, Planned),
            PowerAction.Restart => ExitWindowsEx(Reboot, Planned),
            PowerAction.Sleep => SetSuspendState(false, false, false),
            PowerAction.Hibernate => SetSuspendState(true, false, false),
            _ => false,
        };
        return accepted ? 0 : Math.Max(1, Marshal.GetLastPInvokeError());
    }

    public static string ActionLabel(PowerAction action) => L(action switch
    {
        PowerAction.Shutdown => "PowerActionShutdown",
        PowerAction.Restart => "PowerActionRestart",
        PowerAction.Sleep => "PowerActionSleep",
        _ => "PowerActionHibernate",
    });

    private static int EnableShutdownPrivilege()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var handle = new SafeProcessHandle(process.Handle, ownsHandle: false);
        if (!NativeProcess.OpenProcessToken(handle, TokenAdjustPrivileges | TokenQuery, out var token))
            return Math.Max(1, Marshal.GetLastPInvokeError());
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                return Math.Max(1, Marshal.GetLastPInvokeError());
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };
            var adjusted = AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            var lastError = Marshal.GetLastPInvokeError();
            return !adjusted || lastError == NotAllAssigned ? Math.Max(1, lastError) : 0;
        }
    }

    // SYSTEM_POWER_CAPABILITIES has byte flags followed by battery and wake-state data.
    [StructLayout(LayoutKind.Explicit, Size = 76)]
    private struct PowerCapabilities
    {
        [FieldOffset(3)] public byte S1;
        [FieldOffset(4)] public byte S2;
        [FieldOffset(5)] public byte S3;
        [FieldOffset(6)] public byte S4;
        [FieldOffset(8)] public byte HibernationFile;
        [FieldOffset(20)] public byte ModernStandby;
        [FieldOffset(22)] public byte HibernationFileType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(out PowerCapabilities capabilities);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
}

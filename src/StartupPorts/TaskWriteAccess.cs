using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace StartupPorts;

internal static class TaskWriteAccess
{
    private const int SecurityImpersonation = 2;
    private const uint FileWriteData = 0x2;

    public static bool IsDenied(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        var securityDescriptor = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(securityDescriptor, 0);

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        if (!DuplicateToken(identity.AccessToken, SecurityImpersonation, out var token))
            return false;
        using (token)
        {
            var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200A0, All = 0x1F01FF };
            var privileges = new byte[256];
            var bufferSize = (uint)privileges.Length;
            return AccessCheck(securityDescriptor, token, FileWriteData, ref mapping, privileges, ref bufferSize,
                out _, out var allowed) && !allowed;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint Read;
        public uint Write;
        public uint Execute;
        public uint All;
    }

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateToken(SafeAccessTokenHandle token, int impersonationLevel,
        out SafeAccessTokenHandle duplicate);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AccessCheck(byte[] descriptor, SafeAccessTokenHandle token, uint desiredAccess,
        ref GenericMapping mapping, [Out] byte[] privileges, ref uint bufferSize, out uint grantedAccess,
        [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);
}

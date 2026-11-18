using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

using static StartupPorts.UiText;

namespace StartupPorts;

internal sealed record BoundEndpoint(Protocol Protocol, IPAddress Address, int Port, int ProcessId)
{
    public string Display =>
        Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]:{Port}" : $"{Address}:{Port}";

    public Uri? BrowserAddress
    {
        get
        {
            if (Protocol != Protocol.Tcp)
                return null;
            var address = Address.Equals(IPAddress.Any) ? IPAddress.Loopback :
                Address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : Address;
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
                return null;
            return new UriBuilder("http", address.ToString(), Port).Uri;
        }
    }
}

internal static class NativePorts
{
    private const uint InsufficientBuffer = 122;

    public static IReadOnlyList<BoundEndpoint> Read(Protocol protocol, AddressFamily family)
    {
        var pointer = nint.Zero;
        uint capacity = 0;
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var length = capacity;
                // Owner-PID listener table for TCP, owner-PID endpoint table for UDP.
                var result = protocol == Protocol.Tcp
                    ? GetExtendedTcpTable(pointer, ref length, false, (uint)family, 3, 0)
                    : GetExtendedUdpTable(pointer, ref length, false, (uint)family, 1, 0);
                if (result == 0 && pointer != 0)
                    return Parse(pointer, Math.Min(length, capacity), protocol, family);
                if (result != InsufficientBuffer)
                    throw new Win32Exception((int)result);
                if (length is < 4 or > 67_108_864)
                    throw new InvalidOperationException(L("InvalidPortTableSize"));
                if (pointer != 0)
                    Marshal.FreeHGlobal(pointer);
                pointer = nint.Zero;
                pointer = Marshal.AllocHGlobal((int)length);
                capacity = length;
            }
            throw new InvalidOperationException(L("PortTableChanging"));
        }
        finally
        {
            if (pointer != 0)
                Marshal.FreeHGlobal(pointer);
        }
    }

    private static IReadOnlyList<BoundEndpoint> Parse(nint table, uint length, Protocol protocol, AddressFamily family)
    {
        var ip6 = family == AddressFamily.InterNetworkV6;
        var (rowSize, addressSize, scopeOffset, portOffset, pidOffset) = (protocol, ip6) switch
        {
            (Protocol.Tcp, false) => (24, 4, 0, 8, 20),
            (Protocol.Tcp, true) => (56, 16, 16, 20, 52),
            (Protocol.Udp, false) => (12, 4, 0, 4, 8),
            (Protocol.Udp, true) => (28, 16, 16, 20, 24),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
        var addressOffset = protocol == Protocol.Tcp && !ip6 ? 4 : 0;
        var count = length >= 4 ? Marshal.ReadInt32(table) : -1;
        if (count < 0 || count > (length - 4) / rowSize)
            throw new InvalidOperationException(L("InvalidPortTable"));
        var result = new List<BoundEndpoint>(count);
        for (var index = 0; index < count; index++)
        {
            var row = table + 4 + index * rowSize;
            var bytes = new byte[addressSize];
            Marshal.Copy(row + addressOffset, bytes, 0, addressSize);
            var address = ip6 ? new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(row, scopeOffset))) : new IPAddress(bytes);
            var port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
            var pid = Marshal.ReadInt32(row, pidOffset);
            if (port > 0)
                result.Add(new(protocol, address, port, pid));
        }
        return result;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref uint length, bool order,
        uint family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(nint table, ref uint length, bool order,
        uint family, int tableClass, uint reserved);
}

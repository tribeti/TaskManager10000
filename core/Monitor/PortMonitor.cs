using System.Runtime.InteropServices;
using core.Models;

namespace core.Monitor;

public partial class PortMonitor
{
    private volatile List<ConnectionInfo> _cache = [];

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    private enum TableKind { Tcp, Udp }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(
        IntPtr table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    // Volatile snapshot of the last Refresh (list is replaced, never mutated).
    public List<ConnectionInfo> Connections => _cache;

    public void Refresh(IReadOnlyList<ProcessInfo> processes)
    {
        var names = new Dictionary<int, string>(processes.Count);
        foreach (var p in processes)
        {
            if (p.Id is 0 or 4) continue;
            names[p.Id] = p.Name;
        }

        var rows = new List<ConnectionInfo>();

        ReadTcp(rows, names);
        ReadUdp(rows, names);

        _cache = rows
            .OrderBy(c => c.Protocol)
            .ThenBy(c => c.ProcessName)
            .ThenBy(c => c.LocalPort)
            .ThenBy(c => c.RemotePort)
            .ToList();
    }

    private static void ReadTcp(List<ConnectionInfo> rows, Dictionary<int, string> names)
    {
        IntPtr buffer = GetTable(TableKind.Tcp);
        if (buffer == IntPtr.Zero) return;

        try
        {
            int count = Marshal.ReadInt32(buffer);

            for (int i = 0; i < count; i++)
            {
                IntPtr row = IntPtr.Add(buffer, 4 + i * 24);

                int pid = Marshal.ReadInt32(row, 20);
                if (!names.TryGetValue(pid, out var name)) continue;

                rows.Add(new ConnectionInfo(
                    "TCP",
                    IpToString((uint) Marshal.ReadInt32(row, 4)),
                    Port((uint) Marshal.ReadInt32(row, 8)),
                    IpToString((uint) Marshal.ReadInt32(row, 12)),
                    Port((uint) Marshal.ReadInt32(row, 16)),
                    StateToText(Marshal.ReadInt32(row, 0)),
                    pid,
                    name));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadUdp(List<ConnectionInfo> rows, Dictionary<int, string> names)
    {
        IntPtr buffer = GetTable(TableKind.Udp);
        if (buffer == IntPtr.Zero) return;

        try
        {
            int count = Marshal.ReadInt32(buffer);

            for (int i = 0; i < count; i++)
            {
                IntPtr row = IntPtr.Add(buffer, 4 + i * 12);

                int pid = Marshal.ReadInt32(row, 8);
                if (!names.TryGetValue(pid, out var name)) continue;

                rows.Add(new ConnectionInfo(
                    "UDP",
                    IpToString((uint) Marshal.ReadInt32(row, 0)),
                    Port((uint) Marshal.ReadInt32(row, 4)),
                    "",  // UDP has no remote endpoint
                    0,
                    "",  // and no state
                    pid,
                    name));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr GetTable(TableKind kind)
    {
        int size = 0;
        IntPtr buffer = IntPtr.Zero;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            uint result = kind == TableKind.Tcp
                ? GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0)
                : GetExtendedUdpTable(buffer, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);

            if (result == 0) return buffer;

            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }

            if (result != ERROR_INSUFFICIENT_BUFFER || size <= 0)
                return IntPtr.Zero;

            buffer = Marshal.AllocHGlobal(size);
        }

        if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        return IntPtr.Zero;
    }

    private static string IpToString(uint ip)
    {
        if (ip == 0) return "0.0.0.0";
        return $"{ip & 0xFF}.{(ip >> 8) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 24) & 0xFF}";
    }

    private static int Port(uint raw) =>
        (int) (((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static string StateToText(int state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => $"UNKNOWN({state})"
    };
}

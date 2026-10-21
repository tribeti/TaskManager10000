using core.Models;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace core.Monitor;

public partial class PortMonitor
{
    private volatile List<ConnectionInfo> _cache = [];

    private readonly object _gate = new();
    private byte[] _tcpBuf = GC.AllocateUninitializedArray<byte>(64 * 1024);
    private byte[] _udpBuf = GC.AllocateUninitializedArray<byte>(32 * 1024);
    private readonly Dictionary<uint, string> _ipCache = new(256);
    private int _lastCount = 256;

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UdpRow
    {
        public uint LocalAddr, LocalPort, Pid;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(ref byte table, ref int size, int order, int addressFamily, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(ref byte table, ref int size, int order, int addressFamily, int tableClass, uint reserved);

    private static readonly Comparer<ConnectionInfo> ByEndpoint =
        Comparer<ConnectionInfo>.Create(static (a, b) =>
        {
            int c = string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
            if (c != 0)
                return c;
            c = a.LocalPort.CompareTo(b.LocalPort);
            return c != 0 ? c : a.RemotePort.CompareTo(b.RemotePort);
        });

    public List<ConnectionInfo> Connections => _cache;

    public void Refresh(IReadOnlyList<ProcessInfo> processes)
    {
        lock (_gate)
        {
            var names = new Dictionary<int, string>(processes.Count);
            foreach (var p in processes)
            {
                if (p.Id is 0 or 4)
                    continue;
                names[p.Id] = p.Name;
            }

            var rows = new List<ConnectionInfo>(_lastCount + 32);

            ReadTcp(rows, names);
            int tcpCount = rows.Count;
            ReadUdp(rows, names);

            rows.Sort(0, tcpCount, ByEndpoint);
            rows.Sort(tcpCount, rows.Count - tcpCount, ByEndpoint);

            _lastCount = rows.Count;
            _cache = rows;
        }
    }

    private void ReadTcp(List<ConnectionInfo> rows, Dictionary<int, string> names)
    {
        if (!FillTable(ref _tcpBuf, tcp: true))
            return;

        var buf = _tcpBuf;
        int count = Unsafe.ReadUnaligned<int>(ref buf[0]);
        var table = MemoryMarshal.Cast<byte, TcpRow>(buf.AsSpan(4, count * Unsafe.SizeOf<TcpRow>()));

        foreach (ref readonly var r in table)
        {
            if (!names.TryGetValue((int) r.Pid, out var name))
                continue;

            rows.Add(new ConnectionInfo(
                "TCP",
                Ip(r.LocalAddr), Port(r.LocalPort),
                Ip(r.RemoteAddr), Port(r.RemotePort),
                StateToText((int) r.State),
                (int) r.Pid, name));
        }
    }

    private void ReadUdp(List<ConnectionInfo> rows, Dictionary<int, string> names)
    {
        if (!FillTable(ref _udpBuf, tcp: false))
            return;

        var buf = _udpBuf;
        int count = Unsafe.ReadUnaligned<int>(ref buf[0]);
        var table = MemoryMarshal.Cast<byte, UdpRow>(buf.AsSpan(4, count * Unsafe.SizeOf<UdpRow>()));

        foreach (ref readonly var r in table)
        {
            if (!names.TryGetValue((int) r.Pid, out var name))
                continue;

            rows.Add(new ConnectionInfo(
                "UDP", Ip(r.LocalAddr), Port(r.LocalPort),
                "", 0, "", (int) r.Pid, name));
        }
    }

    private static bool FillTable(ref byte[] buf, bool tcp)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int size = buf.Length;
            uint result = tcp
                ? GetExtendedTcpTable(ref MemoryMarshal.GetArrayDataReference(buf), ref size, 0, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0)
                : GetExtendedUdpTable(ref MemoryMarshal.GetArrayDataReference(buf), ref size, 0, AF_INET, UDP_TABLE_OWNER_PID, 0);

            if (result == 0)
                return true;
            if (result != ERROR_INSUFFICIENT_BUFFER || size <= 0)
                return false;

            buf = GC.AllocateUninitializedArray<byte>(size + size / 4);
        }
        return false;
    }

    private string Ip(uint ip)
    {
        if (_ipCache.TryGetValue(ip, out var s))
            return s;
        if (_ipCache.Count >= 4096)
            _ipCache.Clear();
        return _ipCache[ip] =
            $"{ip & 0xFF}.{(ip >> 8) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 24) & 0xFF}";
    }

    private static int Port(uint raw) => BinaryPrimitives.ReverseEndianness((ushort) raw);

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

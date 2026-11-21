using System.Net;
using System.Runtime.CompilerServices;
using ResMonNext.Native;

namespace ResMonNext.Monitoring;

public sealed record ConnectionSample(int Pid, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State,
    double LatencyMs = double.NaN)
{
    public string Key => $"{Pid}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}";
}

public sealed record ListenerSample(int Pid, string Protocol, string Address, int Port)
{
    public string Key => $"{Pid}|{Protocol}|{Address}|{Port}";
}

/// <summary>
/// Connexions TCP et ports d'écoute TCP/UDP (IPv4 et IPv6) avec le PID propriétaire.
/// Ne nécessite pas de droits administrateur.
/// </summary>
internal static unsafe class NetworkSampler
{
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint MIB_TCP_STATE_LISTEN = 2;
    private const uint MIB_TCP_STATE_ESTABLISHED = 5;

    /// <param name="latency">Mesure la latence (statistiques TCP étendues, droits administrateur requis).</param>
    public static (ConnectionSample[] Connections, ListenerSample[] Listeners) Collect(bool latency = false)
    {
        var connections = new List<ConnectionSample>(512);
        var listeners = new List<ListenerSample>(256);

        WithTable(true, Win32.AF_INET, buffer =>
        {
            uint count = *(uint*)buffer;
            var rows = (MIB_TCPROW_OWNER_PID*)(buffer + 4);
            for (int i = 0; i < count; i++)
            {
                ref var r = ref rows[i];
                string local = FormatV4(r.dwLocalAddr);
                if (r.dwState == MIB_TCP_STATE_LISTEN)
                    listeners.Add(new ListenerSample((int)r.dwOwningPid, "TCP", local, Port(r.dwLocalPort)));
                else if (r.dwOwningPid != 0)
                    connections.Add(new ConnectionSample((int)r.dwOwningPid, local, Port(r.dwLocalPort),
                        FormatV4(r.dwRemoteAddr), Port(r.dwRemotePort), TcpState(r.dwState),
                        latency && r.dwState == MIB_TCP_STATE_ESTABLISHED ? TcpLatency.ForV4((MIB_TCPROW*)Unsafe.AsPointer(ref r)) : double.NaN));
            }
        });

        WithTable(true, Win32.AF_INET6, buffer =>
        {
            uint count = *(uint*)buffer;
            var rows = (MIB_TCP6ROW_OWNER_PID*)(buffer + 4);
            for (int i = 0; i < count; i++)
            {
                var r = &rows[i];
                string local = FormatV6(r->ucLocalAddr, r->dwLocalScopeId);
                if (r->dwState == MIB_TCP_STATE_LISTEN)
                    listeners.Add(new ListenerSample((int)r->dwOwningPid, "TCP", local, Port(r->dwLocalPort)));
                else if (r->dwOwningPid != 0)
                    connections.Add(new ConnectionSample((int)r->dwOwningPid, local, Port(r->dwLocalPort),
                        FormatV6(r->ucRemoteAddr, r->dwRemoteScopeId), Port(r->dwRemotePort), TcpState(r->dwState),
                        latency && r->dwState == MIB_TCP_STATE_ESTABLISHED ? TcpLatency.ForV6(r) : double.NaN));
            }
        });

        WithTable(false, Win32.AF_INET, buffer =>
        {
            uint count = *(uint*)buffer;
            var rows = (MIB_UDPROW_OWNER_PID*)(buffer + 4);
            for (int i = 0; i < count; i++)
                listeners.Add(new ListenerSample((int)rows[i].dwOwningPid, "UDP", FormatV4(rows[i].dwLocalAddr), Port(rows[i].dwLocalPort)));
        });

        WithTable(false, Win32.AF_INET6, buffer =>
        {
            uint count = *(uint*)buffer;
            var rows = (MIB_UDP6ROW_OWNER_PID*)(buffer + 4);
            for (int i = 0; i < count; i++)
                listeners.Add(new ListenerSample((int)rows[i].dwOwningPid, "UDP", FormatV6(rows[i].ucLocalAddr, rows[i].dwLocalScopeId), Port(rows[i].dwLocalPort)));
        });

        return (connections.ToArray(), listeners.ToArray());
    }

    private delegate void TableReader(byte* buffer);

    private static void WithTable(bool tcp, uint family, TableReader reader)
    {
        uint size = 64 * 1024;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                uint result = tcp
                    ? Win32.GetExtendedTcpTable(p, &size, true, family, Win32.TCP_TABLE_OWNER_PID_ALL, 0)
                    : Win32.GetExtendedUdpTable(p, &size, true, family, Win32.UDP_TABLE_OWNER_PID, 0);
                if (result == 0)
                {
                    reader(p);
                    return;
                }
                if (result != ERROR_INSUFFICIENT_BUFFER) return;
            }
            size += 16 * 1024;
        }
    }

    /// <summary>Le port est stocké en ordre réseau dans les 16 bits de poids faible.</summary>
    private static int Port(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));

    internal static string FormatV4(uint address) => address switch
    {
        0 => "IPv4 non spécifiée",
        0x0100007F => "IPv4 de bouclage",
        _ => new IPAddress(address).ToString(),
    };

    internal static string FormatV6(byte* address, uint scopeId)
    {
        var ip = new IPAddress(new ReadOnlySpan<byte>(address, 16), scopeId);
        if (ip.Equals(IPAddress.IPv6Any)) return "IPv6 non spécifiée";
        if (ip.Equals(IPAddress.IPv6Loopback)) return "IPv6 de bouclage";
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
    }

    private static string TcpState(uint state) => state switch
    {
        1 => "Fermée",
        2 => "En écoute",
        3 => "SYN envoyé",
        4 => "SYN reçu",
        5 => "Établie",
        6 => "FIN_WAIT_1",
        7 => "FIN_WAIT_2",
        8 => "Attente de fermeture",
        9 => "Fermeture",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "Suppression",
        _ => "Inconnu",
    };
}

/// <summary>
/// Latence (RTT lissé) par connexion via les statistiques TCP étendues (ESTATS, chemin).
/// La collecte doit être activée connexion par connexion, ce qui exige les droits administrateur.
/// </summary>
internal static unsafe class TcpLatency
{
    // TCP_ESTATS_PATH_ROD_v0 : 40 ULONG ; SmoothedRtt = n° 27, MaxRtt = 29, MinRtt = 30, SumRtt = 31, CountRtt = 32.
    private const int PathRodSize = 160;
    private static readonly HashSet<string> Enabled = new();
    private static bool _broken;

    public static double ForV4(MIB_TCPROW* row)
    {
        if (_broken) return double.NaN;
        string key = $"4|{row->dwLocalAddr}|{row->dwLocalPort}|{row->dwRemoteAddr}|{row->dwRemotePort}";
        byte enable = 1;
        if (Enabled.Add(key) && Etw.SetPerTcpConnectionEStats(row, Etw.TcpConnectionEstatsPath, &enable, 0, 1, 0) != 0) return double.NaN;
        byte* rod = stackalloc byte[PathRodSize];
        uint status = Etw.GetPerTcpConnectionEStats(row, Etw.TcpConnectionEstatsPath, null, 0, 0, null, 0, 0, rod, 0, PathRodSize);
        return Parse(status, rod);
    }

    public static double ForV6(MIB_TCP6ROW_OWNER_PID* owner)
    {
        if (_broken) return double.NaN;
        MIB_TCP6ROW row = default;
        row.State = owner->dwState;
        Buffer.MemoryCopy(owner->ucLocalAddr, row.LocalAddr, 16, 16);
        Buffer.MemoryCopy(owner->ucRemoteAddr, row.RemoteAddr, 16, 16);
        row.dwLocalScopeId = owner->dwLocalScopeId;
        row.dwLocalPort = owner->dwLocalPort;
        row.dwRemoteScopeId = owner->dwRemoteScopeId;
        row.dwRemotePort = owner->dwRemotePort;
        string key = $"6|{Convert.ToHexString(new ReadOnlySpan<byte>(owner->ucLocalAddr, 16))}|{owner->dwLocalPort}|{Convert.ToHexString(new ReadOnlySpan<byte>(owner->ucRemoteAddr, 16))}|{owner->dwRemotePort}";
        byte enable = 1;
        if (Enabled.Add(key) && Etw.SetPerTcp6ConnectionEStats(&row, Etw.TcpConnectionEstatsPath, &enable, 0, 1, 0) != 0) return double.NaN;
        byte* rod = stackalloc byte[PathRodSize];
        uint status = Etw.GetPerTcp6ConnectionEStats(&row, Etw.TcpConnectionEstatsPath, null, 0, 0, null, 0, 0, rod, 0, PathRodSize);
        return Parse(status, rod);
    }

    private static double Parse(uint status, byte* rod)
    {
        if (status == 87) // ERROR_INVALID_PARAMETER : disposition différente de celle attendue → on désactive la mesure.
        {
            _broken = true;
            return double.NaN;
        }
        if (status != 0) return double.NaN;
        var v = (uint*)rod;
        uint smoothed = v[27], max = v[29], min = v[30], sum = v[31], count = v[32];
        if (count == 0 || min > max || max > 120_000) return double.NaN;
        if (smoothed >= min && smoothed <= max) return smoothed;
        double average = (double)sum / count;
        return average >= min && average <= max ? average : double.NaN;
    }

    public static bool IsBroken => _broken;
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResMonNext.Native;

namespace ResMonNext.Monitoring;

public sealed record FileActivity(int Pid, string File, double ReadPerSec, double WritePerSec, string Priority, double ResponseMs);

public sealed record AddressActivity(int Pid, string Address, double SendPerSec, double ReceivePerSec);

/// <summary>Résultat d'un intervalle de trace ETW (débits ramenés à la seconde).</summary>
public sealed class EtwSample
{
    public required Dictionary<int, (double Read, double Write)> DiskByPid { get; init; }
    public required Dictionary<int, (double Send, double Receive)> NetByPid { get; init; }
    public required List<FileActivity> Files { get; init; }
    public required List<AddressActivity> Addresses { get; init; }
    /// <summary>Envois et retransmissions TCP cumulés, clé « pid|portLocal|adresseDistante|portDistant ».</summary>
    public required Dictionary<string, (long Sends, long Retransmits)> TcpStats { get; init; }
    public long Events { get; init; }
}

/// <summary>
/// Session ETW du noyau en temps réel (droits administrateur) : E/S disque par fichier,
/// trafic TCP/UDP par processus et par adresse. Décodage direct des événements MOF du noyau,
/// sans TraceEvent ni réflexion (compatible NativeAOT).
/// </summary>
public sealed unsafe class KernelTrace : IDisposable
{
    private const string SessionName = "ResMonNext Kernel Trace";
    private const string RundownSessionName = "ResMonNext File Rundown";
    private static readonly Guid SessionGuid = new("a3c6f0d2-5e1b-4b8a-9d47-2f6c1e8b7a90");
    private static readonly Guid RundownGuid = new("a3c6f0d2-5e1b-4b8a-9d47-2f6c1e8b7a91");
    private static KernelTrace? _current;
    private static ConcurrentDictionary<ulong, string>? _rundownTarget;

    private readonly string _sessionName;
    private readonly Thread _processThread;
    private ulong _traceHandle;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private long _lastTake = Stopwatch.GetTimestamp();
    private long _events;

    // Uniquement manipulés par le thread ETW.
    private readonly Dictionary<uint, int> _threadOwner = new();
    private readonly Dictionary<ulong, string> _fileNames = new();

    // Noms récupérés par la session de « rundown » (fichiers déjà ouverts avant le démarrage).
    private readonly ConcurrentDictionary<ulong, string> _rundownNames = new();

    /// <summary>Correspondance thread → processus fournie par le collecteur (secours si l'événement de création a été manqué).</summary>
    public volatile Dictionary<uint, int>? SampledThreads;

    private readonly object _gate = new();
    private Dictionary<(int Pid, ulong File), FileAccumulator> _files = new();
    private Dictionary<(int Pid, NetAddress Address), NetAccumulator> _net = new();
    private readonly Dictionary<TcpKey, (long Sends, long Retransmits)> _tcp = new();

    private KernelTrace(string sessionName, ulong traceHandle)
    {
        _sessionName = sessionName;
        _traceHandle = traceHandle;
        _processThread = new Thread(ProcessLoop) { IsBackground = true, Name = "ResMonNext.Etw" };
    }

    public string Name => _sessionName;

    /// <summary>Démarre la trace ; renvoie null et un message si ce n'est pas possible (droits, conflit…).</summary>
    public static KernelTrace? TryStart(out string? error)
    {
        uint flags = Etw.EVENT_TRACE_FLAG_THREAD | Etw.EVENT_TRACE_FLAG_DISK_IO | Etw.EVENT_TRACE_FLAG_DISK_FILE_IO | Etw.EVENT_TRACE_FLAG_NETWORK_TCPIP;

        // 1) Session « system logger » privée (Windows 8+) : ne gêne pas resmon, xperf ou Process Monitor.
        uint status = StartSession(SessionName, SessionGuid, flags, systemLogger: true);
        string name = SessionName;
        // 2) Repli : la session historique « NT Kernel Logger ».
        if (status != 0 && status != 5)
        {
            status = StartSession(Etw.KERNEL_LOGGER_NAME, Etw.SystemTraceControlGuid, flags, systemLogger: false);
            name = Etw.KERNEL_LOGGER_NAME;
        }
        if (status != 0)
        {
            error = status == 5
                ? "Droits administrateur requis pour la trace du noyau."
                : $"Impossible de démarrer la trace du noyau (erreur {status}).";
            return null;
        }

        ulong handle = OpenRealTime(name, &OnEvent);
        if (handle == Etw.INVALID_PROCESSTRACE_HANDLE)
        {
            StopSession(name);
            error = $"Impossible d'ouvrir la trace du noyau (erreur {Marshal.GetLastPInvokeError()}).";
            return null;
        }

        var trace = new KernelTrace(name, handle);
        _current = trace;
        trace._processThread.Start();
        new Thread(trace.HarvestRundown) { IsBackground = true, Name = "ResMonNext.EtwRundown" }.Start();
        error = null;
        return trace;
    }

    private static uint StartSession(string name, Guid guid, uint flags, bool systemLogger)
    {
        int size = sizeof(EVENT_TRACE_PROPERTIES) + (name.Length + 1) * 2 + 16;
        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            var p = (EVENT_TRACE_PROPERTIES*)buffer;
            p->Wnode.BufferSize = (uint)size;
            p->Wnode.Flags = Etw.WNODE_FLAG_TRACED_GUID;
            p->Wnode.ClientContext = 1; // horodatage QPC
            p->Wnode.Guid = guid;
            p->BufferSize = 64;          // Ko
            p->MinimumBuffers = 16;
            p->MaximumBuffers = 128;
            p->FlushTimer = 1;           // s
            p->LogFileMode = Etw.EVENT_TRACE_REAL_TIME_MODE | (systemLogger ? Etw.EVENT_TRACE_SYSTEM_LOGGER_MODE : 0);
            p->EnableFlags = flags;
            p->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);

            ulong handle;
            uint status = Etw.StartTrace(&handle, name, p);
            if (status == 183) // ERROR_ALREADY_EXISTS : session orpheline d'une exécution précédente
            {
                StopSession(name);
                NativeMemory.Clear(buffer, (nuint)size);
                p->Wnode.BufferSize = (uint)size;
                p->Wnode.Flags = Etw.WNODE_FLAG_TRACED_GUID;
                p->Wnode.ClientContext = 1;
                p->Wnode.Guid = guid;
                p->BufferSize = 64;
                p->MinimumBuffers = 16;
                p->MaximumBuffers = 128;
                p->FlushTimer = 1;
                p->LogFileMode = Etw.EVENT_TRACE_REAL_TIME_MODE | (systemLogger ? Etw.EVENT_TRACE_SYSTEM_LOGGER_MODE : 0);
                p->EnableFlags = flags;
                p->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
                status = Etw.StartTrace(&handle, name, p);
            }
            return status;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static void StopSession(string name)
    {
        int size = sizeof(EVENT_TRACE_PROPERTIES) + 1024;
        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            var p = (EVENT_TRACE_PROPERTIES*)buffer;
            p->Wnode.BufferSize = (uint)size;
            p->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
            Etw.ControlTrace(0, name, p, Etw.EVENT_TRACE_CONTROL_STOP);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static ulong OpenRealTime(string name, delegate* unmanaged<EVENT_RECORD*, void> callback)
    {
        EVENT_TRACE_LOGFILEW log = default;
        fixed (char* loggerName = name)
        {
            log.LoggerName = loggerName;
            log.ProcessTraceMode = Etw.PROCESS_TRACE_MODE_REAL_TIME | Etw.PROCESS_TRACE_MODE_EVENT_RECORD;
            log.EventRecordCallback = callback;
            return Etw.OpenTrace(&log);
        }
    }

    private void ProcessLoop()
    {
        ulong handle = _traceHandle;
        Etw.ProcessTrace(&handle, 1, 0, 0);
    }

    /// <summary>
    /// Les noms des fichiers ouverts AVANT le démarrage ne sont émis qu'à l'arrêt d'une session (« rundown ») :
    /// on démarre une seconde session brève, on l'arrête, et on récupère ces noms.
    /// </summary>
    private void HarvestRundown()
    {
        try
        {
            if (StartSession(RundownSessionName, RundownGuid, Etw.EVENT_TRACE_FLAG_DISK_IO | Etw.EVENT_TRACE_FLAG_DISK_FILE_IO, systemLogger: true) != 0) return;
            _rundownTarget = _rundownNames;
            ulong handle = OpenRealTime(RundownSessionName, &OnRundownEvent);
            if (handle == Etw.INVALID_PROCESSTRACE_HANDLE)
            {
                StopSession(RundownSessionName);
                return;
            }
            var reader = new Thread(() =>
            {
                ulong h = handle;
                Etw.ProcessTrace(&h, 1, 0, 0);
            }) { IsBackground = true };
            reader.Start();
            Thread.Sleep(1500);
            StopSession(RundownSessionName);
            reader.Join(5000);
            Etw.CloseTrace(handle);
        }
        catch
        {
            // La trace principale reste fonctionnelle même sans les noms de rundown.
        }
    }

    // ───────────── Décodage des événements (thread ETW) ─────────────

    [UnmanagedCallersOnly]
    private static void OnEvent(EVENT_RECORD* record) => _current?.Handle(record);

    [UnmanagedCallersOnly]
    private static void OnRundownEvent(EVENT_RECORD* record)
    {
        if (record->EventHeader.ProviderId != Etw.FileIoGuid) return;
        byte opcode = record->EventHeader.EventDescriptor.Opcode;
        if (opcode is 0 or 32 or 36 && _rundownTarget is { } target && TryReadFileName(record, out ulong key, out string? name))
            target[key] = name!;
    }

    private void Handle(EVENT_RECORD* record)
    {
        _events++;
        ref var header = ref record->EventHeader;
        byte opcode = header.EventDescriptor.Opcode;
        Guid provider = header.ProviderId;
        int pointerSize = (header.Flags & Etw.EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4 : 8;
        byte* data = record->UserData;
        int length = record->UserDataLength;

        if (provider == Etw.DiskIoGuid)
        {
            if (opcode is 10 or 11) OnDiskIo(record, data, length, pointerSize, write: opcode == 11);
        }
        else if (provider == Etw.TcpIpGuid || provider == Etw.UdpIpGuid)
        {
            OnNetwork(data, length, opcode, tcp: provider == Etw.TcpIpGuid);
        }
        else if (provider == Etw.FileIoGuid)
        {
            if (opcode is 0 or 32 or 36 && TryReadFileName(record, out ulong key, out string? name)) _fileNames[key] = name!;
        }
        else if (provider == Etw.ThreadGuid && length >= 8)
        {
            uint pid = *(uint*)data, tid = *(uint*)(data + 4);
            if (opcode is 1 or 3) _threadOwner[tid] = (int)pid;
            else if (opcode is 2 or 4) _threadOwner.Remove(tid);
        }
    }

    private static bool TryReadFileName(EVENT_RECORD* record, out ulong key, out string? name)
    {
        int pointerSize = (record->EventHeader.Flags & Etw.EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4 : 8;
        key = 0;
        name = null;
        int length = record->UserDataLength;
        if (length <= pointerSize + 2) return false;
        byte* data = record->UserData;
        key = pointerSize == 8 ? *(ulong*)data : *(uint*)data;
        int chars = (length - pointerSize) / 2;
        var text = new ReadOnlySpan<char>(data + pointerSize, chars);
        int end = text.IndexOf('\0');
        if (end >= 0) text = text[..end];
        if (text.IsEmpty) return false;
        name = text.ToString();
        return true;
    }

    private void OnDiskIo(EVENT_RECORD* record, byte* data, int length, int ps, bool write)
    {
        // DiskIo_TypeGroup1 : DiskNumber, IrpFlags, TransferSize, Reserved, ByteOffset, FileObject, Irp,
        //                     HighResResponseTime, IssuingThreadId
        if (length < 24 + 2 * ps + 8) return;
        uint irpFlags = *(uint*)(data + 4);
        uint size = *(uint*)(data + 8);
        ulong file = ps == 8 ? *(ulong*)(data + 24) : *(uint*)(data + 24);
        long response = *(long*)(data + 24 + 2 * ps);
        int pid = -1;
        if (length >= 24 + 2 * ps + 12)
        {
            uint tid = *(uint*)(data + 24 + 2 * ps + 8);
            if (!_threadOwner.TryGetValue(tid, out pid) && !(SampledThreads?.TryGetValue(tid, out pid) ?? false)) pid = -1;
        }
        if (pid < 0 && record->EventHeader.ProcessId != uint.MaxValue) pid = (int)record->EventHeader.ProcessId;
        if (pid < 0) pid = 4; // E/S système non attribuées (cache, pagination)

        _fileNames.TryGetValue(file, out var name);
        lock (_gate)
        {
            ref var acc = ref CollectionsMarshal.GetValueRefOrAddDefault(_files, (pid, file), out _);
            acc ??= new FileAccumulator();
            if (write) acc.Write += size; else acc.Read += size;
            acc.Count++;
            acc.ResponseTicks += response;
            // Le noyau stocke « indice de priorité + 1 » dans les bits 17-19 de l'IRP (0 = non précisé → normale).
            int hint = (int)((irpFlags >> 17) & 7) - 1;
            acc.Priority = hint < 0 ? 2 : hint;
            acc.Name ??= name;
        }
    }

    private void OnNetwork(byte* data, int length, byte opcode, bool tcp)
    {
        // Préfixe commun : PID, size, daddr, saddr, dport, sport (IPv4 : 4 octets ; IPv6 : 16 octets)
        bool v6 = opcode >= 26;
        bool send = opcode is 10 or 26;
        bool receive = opcode is 11 or 27;
        bool retransmit = tcp && opcode is 14 or 30;
        if (!send && !receive && !retransmit) return;
        int portOffset = v6 ? 40 : 16;
        if (length < portOffset + 4) return;

        int pid = (int)*(uint*)data;
        uint size = *(uint*)(data + 4);
        NetAddress remote = v6 ? NetAddress.FromV6(data + 8) : NetAddress.FromV4(*(uint*)(data + 8));
        ushort remotePort = (ushort)((data[portOffset] << 8) | data[portOffset + 1]);
        ushort localPort = (ushort)((data[portOffset + 2] << 8) | data[portOffset + 3]);

        lock (_gate)
        {
            if (send || receive)
            {
                ref var acc = ref CollectionsMarshal.GetValueRefOrAddDefault(_net, (pid, remote), out _);
                acc ??= new NetAccumulator();
                if (send) acc.Send += size; else acc.Receive += size;
            }
            if (tcp && (send || retransmit))
            {
                var key = new TcpKey(pid, localPort, remote, remotePort);
                _tcp.TryGetValue(key, out var stats);
                _tcp[key] = send ? (stats.Sends + 1, stats.Retransmits) : (stats.Sends, stats.Retransmits + 1);
            }
        }
    }

    // ───────────── Lecture des cumuls (thread du collecteur) ─────────────

    public EtwSample Take()
    {
        Dictionary<(int Pid, ulong File), FileAccumulator> files;
        Dictionary<(int Pid, NetAddress Address), NetAccumulator> net;
        Dictionary<TcpKey, (long, long)> tcp;
        lock (_gate)
        {
            files = _files;
            net = _net;
            _files = new(files.Count);
            _net = new(net.Count);
            tcp = new Dictionary<TcpKey, (long, long)>(_tcp);
        }
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Max(0.2, Stopwatch.GetElapsedTime(_lastTake, now).TotalSeconds);
        _lastTake = now;

        var diskByPid = new Dictionary<int, (double, double)>();
        var fileList = new List<FileActivity>(files.Count);
        foreach (var ((pid, file), acc) in files)
        {
            string name = acc.Name ?? (_rundownNames.TryGetValue(file, out var rn) ? rn : "");
            name = name.Length == 0 ? "(fichier ouvert avant le démarrage de la trace)" : DevicePaths.ToDosPath(name);
            fileList.Add(new FileActivity(pid, name, acc.Read / seconds, acc.Write / seconds, PriorityText(acc.Priority),
                acc.Count > 0 ? acc.ResponseTicks * 1000.0 / acc.Count / _qpcFrequency : 0));
            var (r, w) = diskByPid.GetValueOrDefault(pid);
            diskByPid[pid] = (r + acc.Read / seconds, w + acc.Write / seconds);
        }

        var netByPid = new Dictionary<int, (double, double)>();
        var addresses = new List<AddressActivity>(net.Count);
        foreach (var ((pid, address), acc) in net)
        {
            addresses.Add(new AddressActivity(pid, address.ToString(), acc.Send / seconds, acc.Receive / seconds));
            var (s, r) = netByPid.GetValueOrDefault(pid);
            netByPid[pid] = (s + acc.Send / seconds, r + acc.Receive / seconds);
        }

        var tcpStats = new Dictionary<string, (long, long)>(tcp.Count);
        foreach (var (key, value) in tcp)
            tcpStats[$"{key.Pid}|{key.LocalPort}|{key.Remote}|{key.RemotePort}"] = value;

        return new EtwSample
        {
            DiskByPid = diskByPid,
            NetByPid = netByPid,
            Files = fileList,
            Addresses = addresses,
            TcpStats = tcpStats,
            Events = Interlocked.Read(ref _events),
        };
    }

    public int RundownNameCount => _rundownNames.Count;

    private static string PriorityText(int priority) => priority switch
    {
        0 => "Très basse",
        1 => "Basse",
        2 => "Normale",
        3 => "Haute",
        4 => "Critique",
        _ => "Normale",
    };

    public void Dispose()
    {
        StopSession(_sessionName);
        _processThread.Join(3000);
        if (_traceHandle != 0) Etw.CloseTrace(_traceHandle);
        _traceHandle = 0;
        if (ReferenceEquals(_current, this)) _current = null;
    }

    private sealed class FileAccumulator
    {
        public long Read, Write, ResponseTicks;
        public int Count, Priority;
        public string? Name;
    }

    private sealed class NetAccumulator
    {
        public long Send, Receive;
    }

    private readonly record struct TcpKey(int Pid, ushort LocalPort, NetAddress Remote, ushort RemotePort);
}

/// <summary>Adresse IPv4 ou IPv6 brute (sans allocation dans le chemin critique).</summary>
public readonly record struct NetAddress(ulong High, ulong Low, bool V6)
{
    public static NetAddress FromV4(uint address) => new(0, address, false);

    public static unsafe NetAddress FromV6(byte* bytes) => new(*(ulong*)bytes, *(ulong*)(bytes + 8), true);

    public override unsafe string ToString()
    {
        if (!V6) return NetworkSampler.FormatV4((uint)Low);
        byte* bytes = stackalloc byte[16];
        *(ulong*)bytes = High;
        *(ulong*)(bytes + 8) = Low;
        return NetworkSampler.FormatV6(bytes, 0);
    }
}

/// <summary>Convertit « \Device\HarddiskVolume3\… » en « C:\… ».</summary>
internal static unsafe class DevicePaths
{
    private static (string Device, string Drive)[] _map = [];
    private static long _built;

    public static string ToDosPath(string path)
    {
        if (Environment.TickCount64 - _built > 60_000) Build();
        foreach (var (device, drive) in _map)
            if (path.StartsWith(device, StringComparison.OrdinalIgnoreCase) && path.Length > device.Length && path[device.Length] == '\\')
                return drive + path[device.Length..];
        return path;
    }

    private static void Build()
    {
        var map = new List<(string, string)>();
        char* buffer = stackalloc char[1024];
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string drive = letter + ":";
            uint length = Etw.QueryDosDevice(drive, buffer, 1024);
            if (length > 0) map.Add((new string(buffer), drive));
        }
        _map = map.ToArray();
        _built = Environment.TickCount64;
    }
}

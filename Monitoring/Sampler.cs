using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResMonNext.Native;

namespace ResMonNext.Monitoring;

/// <summary>
/// Collecteur : tourne sur son propre thread, lit l'état du système une fois par intervalle
/// et publie un <see cref="Snapshot"/> immuable. L'interface ne fait que l'afficher.
/// </summary>
public sealed unsafe class Sampler : IDisposable
{
    private const int CpuAverageWindowSeconds = 60;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly int _cpuCount = Environment.ProcessorCount;

    private readonly Dictionary<ProcessKey, History> _history = new();
    private readonly Dictionary<ProcessKey, (string? Path, string Description)> _identity = new();
    private readonly ServiceSampler _services = new();
    private readonly PdhQuery _pdh = new();
    private readonly PdhCounter? _frequency, _diskBytes, _diskIdle, _netBytes, _netBandwidth;
    private readonly PdhCounter? _logicalIdle, _logicalQueue, _physicalIdle;
    private readonly PdhCounter? _modified, _standbyCore, _standbyNormal, _standbyReserve, _freeZero;

    private byte[] _processBuffer = new byte[2 * 1024 * 1024];
    private SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] _coreBuffer;
    private long[] _prevCoreIdle = [], _prevCoreTotal = [];
    private long _lastTimestamp;
    private HashSet<int> _hungPids = [];
    private int _tick;
    private ulong _installedBytes;

    private volatile bool _paused;
    private readonly KernelTrace? _trace;
    private Dictionary<uint, int>? _threadMap;

    /// <summary>État de la trace ETW, affiché dans l'interface.</summary>
    public string EtwStatus { get; }

    public bool EtwActive => _trace is not null;

    public event Action<Snapshot>? SnapshotReady;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public Sampler()
    {
        _coreBuffer = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[Math.Max(_cpuCount, 1)];

        _frequency = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _diskBytes = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Bytes/sec");
        _diskIdle = _pdh.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        _netBytes = _pdh.Add(@"\Network Interface(*)\Bytes Total/sec");
        _netBandwidth = _pdh.Add(@"\Network Interface(*)\Current Bandwidth");
        _modified = _pdh.Add(@"\Memory\Modified Page List Bytes");
        _standbyCore = _pdh.Add(@"\Memory\Standby Cache Core Bytes");
        _standbyNormal = _pdh.Add(@"\Memory\Standby Cache Normal Priority Bytes");
        _standbyReserve = _pdh.Add(@"\Memory\Standby Cache Reserve Bytes");
        _freeZero = _pdh.Add(@"\Memory\Free & Zero Page List Bytes");
        _logicalIdle = _pdh.Add(@"\LogicalDisk(*)\% Idle Time");
        _logicalQueue = _pdh.Add(@"\LogicalDisk(*)\Avg. Disk Queue Length");
        _physicalIdle = _pdh.Add(@"\PhysicalDisk(*)\% Idle Time");

        ulong installedKb;
        if (Win32.GetPhysicallyInstalledSystemMemory(&installedKb)) _installedBytes = installedKb * 1024;

        if (SystemActions.IsElevated)
        {
            Privileges.EnableDebugPrivilege();
            _trace = KernelTrace.TryStart(out var error);
            EtwStatus = _trace is not null ? "Trace du noyau active" : error ?? "Trace du noyau indisponible";
        }
        else
        {
            EtwStatus = "Trace du noyau inactive (droits administrateur requis)";
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "ResMonNext.Sampler" };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        // Premier passage immédiat (valeurs de taux à zéro) pour que l'interface se remplisse tout de suite.
        Publish();
        while (!_stop.Wait(Interval))
        {
            if (!_paused) Publish();
        }
    }

    private void Publish()
    {
        try
        {
            SnapshotReady?.Invoke(Collect());
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private Snapshot Collect()
    {
        _tick++;
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        _lastTimestamp = now;

        _pdh.Collect();
        if (_tick % 2 == 1) _hungPids = FindHungProcesses();

        var processes = CollectProcesses(elapsed, out double totalHardFaults);
        var cores = CollectCores(out double cpuTotal);
        var memory = CollectMemory(out int processCount, out int threadCount, out int handleCount);

        double diskIdle = _diskIdle?.Read() ?? double.NaN;
        bool slowTick = _tick % 2 == 1;
        var network = slowTick ? NetworkSampler.Collect(latency: _trace is not null) : default;
        var services = slowTick ? _services.Enumerate() : null;
        if (_trace is not null && _threadMap is not null) _trace.SampledThreads = _threadMap;
        var etw = _trace?.Take();
        double netBytes = _netBytes?.ReadSum() ?? double.NaN;
        double netBandwidthBits = _netBandwidth?.ReadSum() ?? double.NaN;

        return new Snapshot
        {
            Timestamp = DateTime.Now,
            Processes = processes,
            Cores = cores,
            CpuTotal = cpuTotal,
            MaxFrequencyPercent = _frequency?.Read() ?? double.NaN,
            Memory = memory,
            DiskBytesPerSec = _diskBytes?.Read() ?? double.NaN,
            DiskActivePercent = double.IsNaN(diskIdle) ? double.NaN : Math.Clamp(100 - diskIdle, 0, 100),
            NetBytesPerSec = netBytes,
            NetUtilizationPercent = netBandwidthBits > 0 && !double.IsNaN(netBytes) ? Math.Clamp(netBytes * 8 * 100 / netBandwidthBits, 0, 100) : double.NaN,
            HardFaultsPerSec = totalHardFaults,
            ProcessCount = processCount,
            ThreadCount = threadCount,
            HandleCount = handleCount,
            Services = services,
            Connections = network.Connections,
            Listeners = network.Listeners is null ? null
                : FirewallStatus.Annotate(network.Listeners, PathsByPid(processes), ServicesByPid(services)),
            Volumes = slowTick ? CollectVolumes() : null,
            Etw = etw,
            LatencyAvailable = _trace is not null && !TcpLatency.IsBroken,
        };
    }

    // ───────────── Processus ─────────────

    private ProcessSample[] CollectProcesses(double elapsed, out double totalHardFaults)
    {
        totalHardFaults = 0;
        var result = new List<ProcessSample>(400);
        var seen = new HashSet<ProcessKey>();
        var threadMap = _trace is not null ? new Dictionary<uint, int>(8192) : null;

        while (true)
        {
            uint returned;
            int status;
            fixed (byte* p = _processBuffer)
                status = Win32.NtQuerySystemInformation(Win32.SystemProcessInformation, p, (uint)_processBuffer.Length, &returned);
            if (status == Win32.STATUS_INFO_LENGTH_MISMATCH)
            {
                _processBuffer = new byte[Math.Max(returned, (uint)_processBuffer.Length) + 256 * 1024];
                continue;
            }
            if (status < 0) return [];
            break;
        }

        fixed (byte* basePtr = _processBuffer)
        {
            int offset = 0;
            while (true)
            {
                var info = (SYSTEM_PROCESS_INFORMATION*)(basePtr + offset);
                int pid = (int)info->UniqueProcessId;
                if (pid != 0)
                {
                    var key = new ProcessKey(pid, info->CreateTime);
                    seen.Add(key);
                    string name = info->ImageName.Buffer != 0
                        ? new string((char*)info->ImageName.Buffer, 0, info->ImageName.Length / 2)
                        : "";

                    long cpuTime = info->UserTime + info->KernelTime;
                    double cpu = 0, cpuAverage = 0, hardFaults = 0, ioRead = 0, ioWrite = 0;
                    if (_history.TryGetValue(key, out var h))
                    {
                        if (elapsed > 0)
                        {
                            double cpuSeconds = Math.Max(0, cpuTime - h.CpuTime) / 1e7;
                            cpu = Math.Clamp(cpuSeconds / (elapsed * _cpuCount) * 100, 0, 100);
                            hardFaults = unchecked(info->HardFaultCount - h.HardFaults) / elapsed;
                            ioRead = Math.Max(0, info->ReadTransferCount - h.IoRead) / elapsed;
                            ioWrite = Math.Max(0, info->WriteTransferCount - h.IoWrite) / elapsed;
                            h.Window.Enqueue((elapsed, cpuSeconds));
                            h.WindowElapsed += elapsed;
                            h.WindowCpu += cpuSeconds;
                            while (h.WindowElapsed > CpuAverageWindowSeconds && h.Window.Count > 1)
                            {
                                var old = h.Window.Dequeue();
                                h.WindowElapsed -= old.Elapsed;
                                h.WindowCpu -= old.Cpu;
                            }
                            cpuAverage = h.WindowElapsed > 0 ? Math.Clamp(h.WindowCpu / (h.WindowElapsed * _cpuCount) * 100, 0, 100) : 0;
                        }
                    }
                    else
                    {
                        h = new History();
                        _history[key] = h;
                    }
                    h.CpuTime = cpuTime;
                    h.HardFaults = info->HardFaultCount;
                    h.IoRead = info->ReadTransferCount;
                    h.IoWrite = info->WriteTransferCount;
                    totalHardFaults += hardFaults;

                    if (!_identity.TryGetValue(key, out var identity))
                    {
                        identity = ResolveIdentity(pid, name);
                        _identity[key] = identity;
                    }

                    var threads = (SYSTEM_THREAD_INFORMATION*)(info + 1);
                    if (threadMap is not null)
                        for (int t = 0; t < info->NumberOfThreads; t++) threadMap[(uint)threads[t].UniqueThread] = pid;
                    ProcessState state = ProcessState.Running;
                    if (_hungPids.Contains(pid)) state = ProcessState.NotResponding;
                    else if (IsSuspended(threads, info->NumberOfThreads)) state = ProcessState.Suspended;

                    ulong workingSet = info->WorkingSetSize;
                    ulong privateWs = (ulong)Math.Max(0, info->WorkingSetPrivateSize);
                    result.Add(new ProcessSample
                    {
                        Key = key,
                        ParentPid = (int)info->InheritedFromUniqueProcessId,
                        Name = name,
                        Path = identity.Path,
                        Description = identity.Description,
                        Threads = (int)info->NumberOfThreads,
                        Handles = (int)info->HandleCount,
                        Cpu = cpu,
                        CpuAverage = cpuAverage,
                        WorkingSet = workingSet,
                        PrivateWorkingSet = Math.Min(privateWs, workingSet),
                        Commit = info->PrivatePageCount,
                        HardFaultsPerSec = hardFaults,
                        IoReadPerSec = ioRead,
                        IoWritePerSec = ioWrite,
                        State = state,
                    });
                }

                if (info->NextEntryOffset == 0) break;
                offset += (int)info->NextEntryOffset;
            }
        }

        if (threadMap is not null) _threadMap = threadMap;

        // Purge des processus terminés.
        if (_history.Count > seen.Count)
        {
            foreach (var key in _history.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _history.Remove(key);
                _identity.Remove(key);
            }
        }
        return result.ToArray();
    }

    /// <summary>Un processus est « suspendu » quand tous ses threads attendent avec la raison Suspended.</summary>
    private static bool IsSuspended(SYSTEM_THREAD_INFORMATION* threads, uint count)
    {
        if (count == 0) return false;
        for (int i = 0; i < count; i++)
        {
            // ThreadState 5 = Waiting, WaitReason 5 = Suspended
            if (threads[i].ThreadState != 5 || threads[i].WaitReason != 5) return false;
        }
        return true;
    }

    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

    private static (string? Path, string Description) ResolveIdentity(int pid, string name)
    {
        if (pid == 4) return (null, "Noyau NT et système");
        var (path, description) = ResolvePath(pid);
        // Sans droits admin, Windows refuse le chemin des processus système (svchost, lsass…) :
        // la description est alors lue sur l'exécutable de même nom dans System32 (le chemin reste inconnu).
        if (path is null && name.Length > 0 && File.Exists(System.IO.Path.Combine(System32, name)))
            description = FileInfoCache.Get(System.IO.Path.Combine(System32, name)).Description;
        return (path, description);
    }

    private static (string? Path, string Description) ResolvePath(int pid)
    {
        string? path = null;
        nint handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle != 0)
        {
            try
            {
                char* buffer = stackalloc char[1024];
                uint size = 1024;
                if (Win32.QueryFullProcessImageName(handle, 0, buffer, &size)) path = new string(buffer, 0, (int)size);
            }
            finally
            {
                Win32.CloseHandle(handle);
            }
        }
        return (path, FileInfoCache.Get(path).Description);
    }

    // ───────────── Processeurs ─────────────

    private double[] CollectCores(out double total)
    {
        total = 0;
        uint returned;
        int status;
        fixed (SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION* p = _coreBuffer)
            status = Win32.NtQuerySystemInformation(Win32.SystemProcessorPerformanceInformation, p,
                (uint)(_coreBuffer.Length * sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION)), &returned);
        if (status < 0) return [];

        int count = (int)(returned / (uint)sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
        if (_prevCoreIdle.Length != count)
        {
            _prevCoreIdle = new long[count];
            _prevCoreTotal = new long[count];
        }

        var usage = new double[count];
        long sumIdle = 0, sumTotal = 0;
        for (int i = 0; i < count; i++)
        {
            ref var c = ref _coreBuffer[i];
            long idle = c.IdleTime;
            long all = c.KernelTime + c.UserTime; // KernelTime inclut le temps d'inactivité
            long dIdle = idle - _prevCoreIdle[i], dAll = all - _prevCoreTotal[i];
            if (_prevCoreTotal[i] != 0 && dAll > 0)
            {
                usage[i] = Math.Clamp((dAll - dIdle) * 100.0 / dAll, 0, 100);
                sumIdle += dIdle;
                sumTotal += dAll;
            }
            _prevCoreIdle[i] = idle;
            _prevCoreTotal[i] = all;
        }
        total = sumTotal > 0 ? Math.Clamp((sumTotal - sumIdle) * 100.0 / sumTotal, 0, 100) : 0;
        return usage;
    }

    // ───────────── Mémoire ─────────────

    private MemorySample CollectMemory(out int processCount, out int threadCount, out int handleCount)
    {
        MEMORYSTATUSEX status = default;
        status.dwLength = (uint)sizeof(MEMORYSTATUSEX);
        Win32.GlobalMemoryStatusEx(&status);

        PERFORMANCE_INFORMATION perf = default;
        perf.cb = (uint)sizeof(PERFORMANCE_INFORMATION);
        Win32.GetPerformanceInfo(&perf, perf.cb);
        processCount = (int)perf.ProcessCount;
        threadCount = (int)perf.ThreadCount;
        handleCount = (int)perf.HandleCount;
        ulong pageSize = perf.PageSize == 0 ? 4096 : perf.PageSize;

        ulong total = status.ullTotalPhys, available = status.ullAvailPhys;
        ulong installed = Math.Max(_installedBytes, total);

        double modified = _modified?.Read() ?? double.NaN;
        double standby = (_standbyCore?.Read() ?? double.NaN) + (_standbyNormal?.Read() ?? double.NaN) + (_standbyReserve?.Read() ?? double.NaN);
        double free = _freeZero?.Read() ?? double.NaN;
        bool lists = !double.IsNaN(modified) && !double.IsNaN(standby) && !double.IsNaN(free);

        ulong mod = lists ? (ulong)modified : 0;
        ulong stb = lists ? (ulong)standby : available;
        ulong fre = lists ? (ulong)free : 0;
        ulong inUse = total > mod + stb + fre ? total - mod - stb - fre : total - Math.Min(total, available);

        return new MemorySample
        {
            Installed = installed,
            Total = total,
            Available = available,
            HardwareReserved = installed - total,
            InUse = inUse,
            Modified = mod,
            Standby = stb,
            Free = fre,
            CommitTotal = perf.CommitTotal * pageSize,
            CommitLimit = perf.CommitLimit * pageSize,
            ListsAvailable = lists,
        };
    }

    private static readonly string SystemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <summary>Chemin de chaque processus ; sans droits admin, les processus système sont supposés dans System32.</summary>
    private static Dictionary<int, string?> PathsByPid(ProcessSample[] processes)
    {
        var map = new Dictionary<int, string?>(processes.Length);
        foreach (var p in processes)
        {
            string? path = p.Path;
            if (path is null && p.Name.Length > 0)
            {
                string candidate = System.IO.Path.Combine(SystemDirectory, p.Name);
                if (File.Exists(candidate)) path = candidate;
            }
            map[p.Key.Pid] = path;
        }
        return map;
    }

    /// <summary>Services en cours d'exécution hébergés par chaque processus (règles de pare-feu par service).</summary>
    private static Dictionary<int, HashSet<string>> ServicesByPid(ServiceSample[]? services)
    {
        var map = new Dictionary<int, HashSet<string>>();
        foreach (var s in services ?? [])
        {
            if (s.Pid == 0) continue;
            if (!map.TryGetValue(s.Pid, out var set)) map[s.Pid] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(s.Name);
        }
        return map;
    }

    // ───────────── Volumes (onglet Disque › Stockage) ─────────────

    private VolumeSample[] CollectVolumes()
    {
        var idle = (_logicalIdle?.ReadArray() ?? []).ToDictionary(i => i.Name, i => i.Value, StringComparer.OrdinalIgnoreCase);
        var queue = (_logicalQueue?.ReadArray() ?? []).ToDictionary(i => i.Name, i => i.Value, StringComparer.OrdinalIgnoreCase);

        // Instances PhysicalDisk : « 0 C: », « 1 D: E: » → lettre vers numéro de disque.
        var physical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in _physicalIdle?.ReadArray() ?? [])
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out _)) continue;
            foreach (var letter in parts.Skip(1)) physical[letter] = $"Disque {parts[0]}";
        }

        var volumes = new List<VolumeSample>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string name = drive.Name.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                volumes.Add(new VolumeSample
                {
                    Name = name,
                    PhysicalDisk = physical.GetValueOrDefault(name, ""),
                    ActivePercent = idle.TryGetValue(name, out var i) ? Math.Clamp(100 - i, 0, 100) : double.NaN,
                    QueueLength = queue.TryGetValue(name, out var q) ? q : double.NaN,
                    FreeBytes = (ulong)drive.AvailableFreeSpace,
                    TotalBytes = (ulong)drive.TotalSize,
                });
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return volumes.ToArray();
    }

    // ───────────── Fenêtres qui ne répondent pas ─────────────

    private static HashSet<int> FindHungProcesses()
    {
        var set = new HashSet<int>();
        var handle = GCHandle.Alloc(set);
        try
        {
            Win32.EnumWindows(&EnumWindowsCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return set;
    }

    [UnmanagedCallersOnly]
    private static int EnumWindowsCallback(nint hwnd, nint lParam)
    {
        if (Win32.IsWindowVisible(hwnd) && Win32.IsHungAppWindow(hwnd))
        {
            uint pid;
            Win32.GetWindowThreadProcessId(hwnd, &pid);
            ((HashSet<int>)GCHandle.FromIntPtr(lParam).Target!).Add((int)pid);
        }
        return 1;
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(2000);
        _trace?.Dispose();
        _pdh.Dispose();
    }

    private sealed class History
    {
        public long CpuTime;
        public uint HardFaults;
        public long IoRead, IoWrite;
        public readonly Queue<(double Elapsed, double Cpu)> Window = new();
        public double WindowElapsed;
        public double WindowCpu;
    }
}

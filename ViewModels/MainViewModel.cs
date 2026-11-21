using System.Collections.ObjectModel;
using Avalonia.Threading;
using ResMonNext.Controls;
using ResMonNext.Monitoring;

namespace ResMonNext.ViewModels;

public sealed class MainViewModel : ObservableBase, IDisposable
{
    private readonly Sampler _sampler = new();
    private readonly Dictionary<ProcessKey, ProcessRow> _rows = new();
    private readonly List<ProcessRow> _rowList = new();
    private readonly Dictionary<string, ServiceRow> _serviceRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ServiceRow> _serviceList = new();
    private readonly HashSet<int> _checkedPids = new();
    private readonly Dictionary<string, ConnectionRow> _connectionRows = new();
    private readonly Dictionary<string, ListenerRow> _listenerRows = new();
    private readonly Dictionary<string, StorageRow> _storageRows = new(StringComparer.OrdinalIgnoreCase);
    private string _networkSummary = "—";
    private readonly Dictionary<string, FileRow> _fileRows = new();
    private readonly Dictionary<string, AddressRow> _addressRows = new();
    private Dictionary<string, (long Sends, long Retransmits)> _tcpStats = new();
    private string _handleSearchText = "", _handleStatus = "Cochez un processus ou saisissez un nom de fichier, de clé ou d'objet.";
    private CancellationTokenSource? _handleSearch;
    private int _tick;
    private bool _modulesLoading;

    private string _searchText = "";
    private bool _isPaused;
    private string _cpuUsageText = "—", _cpuFrequencyText = "—", _diskText = "—", _diskActiveText = "—";
    private string _netText = "—", _netUtilText = "—", _memoryUsedText = "—", _hardFaultsText = "—", _commitText = "—";
    private string _statusText = "Collecte en cours…", _filterText = "", _modulesHint = "Cochez un ou plusieurs processus pour voir leurs modules.";
    private string _availableText = "—", _cachedText = "—", _totalText = "—", _installedText = "—";
    private IReadOnlyList<MemorySegment> _memorySegments = [];

    public MainViewModel()
    {
        OverviewCpu = new ProcessTable(this, "Cpu");
        CpuProcesses = new ProcessTable(this, "Cpu");
        OverviewMemory = new ProcessTable(this, "HardFaults");
        MemoryProcesses = new ProcessTable(this, "WorkingSet");
        DiskProcesses = new ProcessTable(this, "IoTotal");
        NetworkProcesses = new ProcessTable(this, "TcpConnections");
        foreach (var table in ProcessTables) table.Filter = MatchesSearch;
        // Onglet Disque : processus ayant eu des E/S dans les 60 dernières secondes (comme resmon).
        DiskProcesses.Filter = r => MatchesSearch(r) && _tick - r.LastIoTick < 60;
        // Onglet Réseau : processus possédant des connexions TCP ou des ports d'écoute.
        NetworkProcesses.Filter = r => MatchesSearch(r) && (r.TcpConnections > 0 || r.ListeningPorts > 0 || _tick - r.LastNetTick < 60);
        Files.Filter = f => _checkedPids.Count == 0 || _checkedPids.Contains(f.Pid);
        Addresses.Filter = a => _checkedPids.Count == 0 || _checkedPids.Contains(a.Pid);
        EtwActive = _sampler.EtwActive;
        EtwStatus = _sampler.EtwStatus;
        Connections.Filter = c => _checkedPids.Count == 0 || _checkedPids.Contains(c.Pid);
        Listeners.Filter = l => _checkedPids.Count == 0 || _checkedPids.Contains(l.Pid);
        Services.Filter = s => _checkedPids.Count == 0 || _checkedPids.Contains(s.Pid);

        CpuGraph = new GraphModel(100, 100,
            new GraphSeries(Palette.Cpu, fill: true),
            new GraphSeries(Palette.Frequency, fill: false),
            new GraphSeries(Palette.Filtered, fill: true));
        DiskGraph = new GraphModel(null, 1024 * 1024, new GraphSeries(Palette.Disk, fill: true)) { ScaleFormatter = Fmt.BytesPerSec };
        DiskActiveGraph = new GraphModel(100, 100, new GraphSeries(Palette.DiskActive, fill: true));
        NetGraph = new GraphModel(null, 128 * 1024, new GraphSeries(Palette.Network, fill: true)) { ScaleFormatter = Fmt.BitsPerSec };
        NetUtilGraph = new GraphModel(100, 100, new GraphSeries(Palette.Network, fill: true));
        MemoryGraph = new GraphModel(100, 100, new GraphSeries(Palette.Memory, fill: true));
        CommitGraph = new GraphModel(100, 100, new GraphSeries(Palette.Commit, fill: true));
        HardFaultGraph = new GraphModel(null, 100, new GraphSeries(Palette.HardFaults, fill: true)) { ScaleFormatter = v => Fmt.Count(v) + "/s" };

        MemoryLegend =
        [
            new LegendItem("Réservée au matériel", Palette.HardwareReserved),
            new LegendItem("En cours d'utilisation", Palette.InUse),
            new LegendItem("Modifiée", Palette.Modified),
            new LegendItem("En attente", Palette.Standby),
            new LegendItem("Libre", Palette.Free),
        ];

        _sampler.SnapshotReady += s => Dispatcher.UIThread.Post(() => Apply(s));
        _sampler.Start();
    }

    // ───────────── Trace du noyau (ETW) ─────────────
    public bool EtwActive { get; }
    public string EtwStatus { get; }
    public string DiskProcessesTitle => EtwActive ? "Processus avec activité disque" : "Processus avec activité d'E/S";
    public string DiskProcessesHint => EtwActive ? "Disque réel (trace du noyau) · actifs depuis 60 s" : "Toutes E/S du processus · actifs depuis 60 s";
    public FileTable Files { get; } = new();
    public AddressTable Addresses { get; } = new();
    public HandleTable Handles { get; } = new();

    public string HandleSearchText { get => _handleSearchText; set => Set(ref _handleSearchText, value); }
    public string HandleStatus { get => _handleStatus; private set => Set(ref _handleStatus, value); }

    // ───────────── Tables ─────────────
    public ProcessTable OverviewCpu { get; }
    public ProcessTable OverviewMemory { get; }
    public ProcessTable CpuProcesses { get; }
    public ProcessTable MemoryProcesses { get; }
    public ServiceTable Services { get; } = new();
    public ModuleTable Modules { get; } = new();
    public ProcessTable DiskProcesses { get; }
    public ProcessTable NetworkProcesses { get; }
    public ConnectionTable Connections { get; } = new();
    public ListenerTable Listeners { get; } = new();
    public StorageTable Storage { get; } = new();
    public string NetworkSummary { get => _networkSummary; private set => Set(ref _networkSummary, value); }
    private IEnumerable<ProcessTable> ProcessTables => [OverviewCpu, OverviewMemory, CpuProcesses, MemoryProcesses, DiskProcesses, NetworkProcesses];

    // ───────────── Graphes ─────────────
    public GraphModel CpuGraph { get; }
    public GraphModel DiskGraph { get; }
    public GraphModel DiskActiveGraph { get; }
    public GraphModel NetGraph { get; }
    public GraphModel NetUtilGraph { get; }
    public GraphModel MemoryGraph { get; }
    public GraphModel CommitGraph { get; }
    public GraphModel HardFaultGraph { get; }
    public ObservableCollection<CoreGraph> Cores { get; } = [];

    // ───────────── Indicateurs ─────────────
    public string CpuUsageText { get => _cpuUsageText; private set => Set(ref _cpuUsageText, value); }
    public string CpuFrequencyText { get => _cpuFrequencyText; private set => Set(ref _cpuFrequencyText, value); }
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value); }
    public string DiskActiveText { get => _diskActiveText; private set => Set(ref _diskActiveText, value); }
    public string NetText { get => _netText; private set => Set(ref _netText, value); }
    public string NetUtilText { get => _netUtilText; private set => Set(ref _netUtilText, value); }
    public string MemoryUsedText { get => _memoryUsedText; private set => Set(ref _memoryUsedText, value); }
    public string HardFaultsText { get => _hardFaultsText; private set => Set(ref _hardFaultsText, value); }
    public string CommitText { get => _commitText; private set => Set(ref _commitText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string FilterText { get => _filterText; private set => Set(ref _filterText, value); }
    public string ModulesHint { get => _modulesHint; private set => Set(ref _modulesHint, value); }

    // ───────────── Mémoire physique ─────────────
    public IReadOnlyList<MemorySegment> MemorySegments { get => _memorySegments; private set => Set(ref _memorySegments, value); }
    public ObservableCollection<LegendItem> MemoryLegend { get; }
    public string AvailableText { get => _availableText; private set => Set(ref _availableText, value); }
    public string CachedText { get => _cachedText; private set => Set(ref _cachedText, value); }
    public string TotalText { get => _totalText; private set => Set(ref _totalText, value); }
    public string InstalledText { get => _installedText; private set => Set(ref _installedText, value); }

    public bool IsElevated { get; } = SystemActions.IsElevated;
    public string ElevationText => IsElevated ? "Administrateur" : "Utilisateur standard";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            foreach (var table in ProcessTables) table.Refresh();
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (Set(ref _isPaused, value)) _sampler.Paused = value;
        }
    }

    private bool MatchesSearch(ProcessRow row)
    {
        string q = _searchText.Trim();
        return q.Length == 0
            || row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || row.Pid.ToString() == q;
    }

    // ───────────── Application d'un instantané ─────────────

    private void Apply(Snapshot s)
    {
        _tick++;
        ApplyProcesses(s.Processes, etwActive: s.Etw is not null);
        if (s.Etw is not null) ApplyEtw(s.Etw);
        if (s.Connections is not null) ApplyNetwork(s.Connections, s.Listeners ?? []);
        if (s.Volumes is not null) ApplyVolumes(s.Volumes);
        foreach (var table in ProcessTables) table.Sync(_rowList);
        if (s.Services is not null) ApplyServices(s.Services);
        else UpdateServiceCpu();
        ApplyGraphs(s);
        ApplyMemory(s.Memory);

        if (_checkedPids.Count > 0 && _tick % 5 == 0) RefreshModules();

        StatusText = $"{s.ProcessCount:N0} processus · {s.ThreadCount:N0} threads · {s.HandleCount:N0} handles · Mis à jour à {s.Timestamp:HH:mm:ss}";
    }

    private void ApplyProcesses(ProcessSample[] samples, bool etwActive)
    {
        var alive = new HashSet<ProcessKey>();
        foreach (var sample in samples)
        {
            alive.Add(sample.Key);
            if (!_rows.TryGetValue(sample.Key, out var row))
            {
                row = new ProcessRow(sample.Key, sample.Name);
                row.CheckedChanged += OnCheckedChanged;
                _rows[sample.Key] = row;
                _rowList.Add(row);
            }
            row.Update(sample);
            if (!etwActive && row.IoTotal > 0) row.LastIoTick = _tick;
        }

        bool checkedRemoved = false;
        for (int i = _rowList.Count - 1; i >= 0; i--)
        {
            var row = _rowList[i];
            if (alive.Contains(row.Key)) continue;
            row.CheckedChanged -= OnCheckedChanged;
            checkedRemoved |= row.IsChecked;
            _rows.Remove(row.Key);
            _rowList.RemoveAt(i);
        }
        if (checkedRemoved) RebuildCheckedSet();
    }

    private void ApplyNetwork(ConnectionSample[] connections, ListenerSample[] listeners)
    {
        var names = new Dictionary<int, string>();
        foreach (var row in _rowList) names[row.Pid] = row.Name;
        string Image(int pid) => pid == 0 ? "Processus inactif du système" : names.GetValueOrDefault(pid, $"PID {pid}");

        var tcpCount = new Dictionary<int, int>();
        var aliveConnections = new HashSet<string>();
        foreach (var c in connections)
        {
            if (!aliveConnections.Add(c.Key)) continue;
            tcpCount[c.Pid] = tcpCount.GetValueOrDefault(c.Pid) + 1;
            if (_connectionRows.TryGetValue(c.Key, out var row)) row.State = c.State;
            else _connectionRows[c.Key] = row = new ConnectionRow(c, Image(c.Pid));
            row.SetQuality(c.LatencyMs, _tcpStats.TryGetValue(row.TcpStatsKey, out var stats) ? stats : null);
        }
        foreach (var key in _connectionRows.Keys.Where(k => !aliveConnections.Contains(k)).ToList()) _connectionRows.Remove(key);

        var listenCount = new Dictionary<int, int>();
        var aliveListeners = new HashSet<string>();
        foreach (var l in listeners)
        {
            if (!aliveListeners.Add(l.Key)) continue;
            listenCount[l.Pid] = listenCount.GetValueOrDefault(l.Pid) + 1;
            if (!_listenerRows.ContainsKey(l.Key)) _listenerRows[l.Key] = new ListenerRow(l, Image(l.Pid));
        }
        foreach (var key in _listenerRows.Keys.Where(k => !aliveListeners.Contains(k)).ToList()) _listenerRows.Remove(key);

        foreach (var row in _rowList)
        {
            row.TcpConnections = tcpCount.GetValueOrDefault(row.Pid);
            row.ListeningPorts = listenCount.GetValueOrDefault(row.Pid);
        }
        Connections.Sync(_connectionRows.Values.ToList());
        Listeners.Sync(_listenerRows.Values.ToList());
        NetworkSummary = $"{aliveConnections.Count:N0} connexions TCP · {aliveListeners.Count:N0} ports d'écoute";
    }

    private void ApplyVolumes(VolumeSample[] volumes)
    {
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in volumes)
        {
            alive.Add(v.Name);
            if (!_storageRows.TryGetValue(v.Name, out var row)) _storageRows[v.Name] = row = new StorageRow(v.Name);
            row.Update(v);
        }
        foreach (var key in _storageRows.Keys.Where(k => !alive.Contains(k)).ToList()) _storageRows.Remove(key);
        Storage.Sync(_storageRows.Values.ToList());
    }

    private void ApplyServices(ServiceSample[] samples)
    {
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            alive.Add(sample.Name);
            if (!_serviceRows.TryGetValue(sample.Name, out var row))
            {
                row = new ServiceRow(sample.Name);
                _serviceRows[sample.Name] = row;
                _serviceList.Add(row);
            }
            row.Update(sample);
        }
        _serviceList.RemoveAll(r => !alive.Contains(r.Name) && _serviceRows.Remove(r.Name));
        UpdateServiceCpu();
        Services.Sync(_serviceList);
    }

    /// <summary>Processeur du service = processeur de son processus hôte.</summary>
    private void UpdateServiceCpu()
    {
        var cpuByPid = new Dictionary<int, double>();
        foreach (var row in _rowList) cpuByPid[row.Pid] = row.Cpu;
        foreach (var service in _serviceList)
            service.Cpu = service.IsRunning && cpuByPid.TryGetValue(service.Pid, out var cpu) ? cpu : 0;
    }

    private void ApplyGraphs(Snapshot s)
    {
        double filteredCpu = 0;
        foreach (var row in _rowList)
            if (row.IsChecked) filteredCpu += row.Cpu;

        double frequency = double.IsNaN(s.MaxFrequencyPercent) ? 0 : Math.Min(100, s.MaxFrequencyPercent);
        CpuGraph.Push(s.CpuTotal, frequency, _checkedPids.Count > 0 ? filteredCpu : 0);
        CpuUsageText = Fmt.Percent(s.CpuTotal);
        CpuFrequencyText = double.IsNaN(s.MaxFrequencyPercent) ? "Fréquence : —" : $"Fréquence max. {Fmt.Percent(s.MaxFrequencyPercent)}";

        DiskGraph.Push(s.DiskBytesPerSec);
        DiskActiveGraph.Push(s.DiskActivePercent);
        DiskText = Fmt.BytesPerSec(s.DiskBytesPerSec);
        DiskActiveText = $"Temps actif {Fmt.Percent(s.DiskActivePercent)}";

        NetGraph.Push(s.NetBytesPerSec);
        NetUtilGraph.Push(s.NetUtilizationPercent);
        NetText = Fmt.BitsPerSec(s.NetBytesPerSec);
        NetUtilText = $"Utilisation {Fmt.Percent(s.NetUtilizationPercent)}";

        HardFaultGraph.Push(s.HardFaultsPerSec);
        HardFaultsText = $"{Fmt.Count(s.HardFaultsPerSec)} défauts matériels/s";

        if (Cores.Count != s.Cores.Length)
        {
            Cores.Clear();
            for (int i = 0; i < s.Cores.Length; i++) Cores.Add(new CoreGraph($"Processeur {i}"));
        }
        for (int i = 0; i < s.Cores.Length; i++) Cores[i].Push(s.Cores[i]);
    }

    private void ApplyMemory(MemorySample m)
    {
        MemoryGraph.Push(m.UsedPercent);
        CommitGraph.Push(m.CommitPercent);
        MemoryUsedText = Fmt.Percent(m.UsedPercent);
        CommitText = $"Validation {Fmt.Bytes(m.CommitTotal)} / {Fmt.Bytes(m.CommitLimit)} ({Fmt.Percent(m.CommitPercent)})";

        MemorySegments =
        [
            new MemorySegment(m.HardwareReserved, Palette.HardwareReserved),
            new MemorySegment(m.InUse, Palette.InUse),
            new MemorySegment(m.Modified, Palette.Modified),
            new MemorySegment(m.Standby, Palette.Standby),
            new MemorySegment(m.Free, Palette.Free),
        ];
        MemoryLegend[0].Value = Fmt.Mb(m.HardwareReserved);
        MemoryLegend[1].Value = Fmt.Mb(m.InUse);
        MemoryLegend[2].Value = m.ListsAvailable ? Fmt.Mb(m.Modified) : "—";
        MemoryLegend[3].Value = Fmt.Mb(m.Standby);
        MemoryLegend[4].Value = m.ListsAvailable ? Fmt.Mb(m.Free) : "—";
        AvailableText = Fmt.Mb(m.Available);
        CachedText = Fmt.Mb(m.Cached);
        TotalText = Fmt.Mb(m.Total);
        InstalledText = Fmt.Mb(m.Installed);
    }

    // ───────────── Filtre par cases à cocher ─────────────

    private bool _filterRefreshPending;

    private void OnCheckedChanged(ProcessRow row)
    {
        RebuildCheckedSet();
        // Différé : on ne réordonne pas la grille pendant que la case à cocher est en train d'écrire sa valeur.
        if (_filterRefreshPending) return;
        _filterRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _filterRefreshPending = false;
            foreach (var table in ProcessTables) table.Refresh();
            Services.Refresh();
            Connections.Refresh();
            Files.Refresh();
            Addresses.Refresh();
            if (_handleSearchText.Trim().Length == 0) SearchHandles();
            Listeners.Refresh();
            RefreshModules();
        });
    }

    private void RebuildCheckedSet()
    {
        _checkedPids.Clear();
        foreach (var row in _rowList)
            if (row.IsChecked) _checkedPids.Add(row.Pid);
        FilterText = _checkedPids.Count switch
        {
            0 => "",
            1 => "Filtré sur 1 processus",
            var n => $"Filtré sur {n} processus",
        };
        Raise(nameof(HasFilter));
    }

    public bool HasFilter => _checkedPids.Count > 0;

    public void CheckProcess(int pid)
    {
        foreach (var row in _rowList.Where(r => r.Pid == pid)) row.IsChecked = true;
    }

    public void ClearFilter()
    {
        foreach (var row in _rowList.Where(r => r.IsChecked).ToList()) row.IsChecked = false;
    }

    private void RefreshModules()
    {
        if (_checkedPids.Count == 0)
        {
            Modules.Sync([]);
            ModulesHint = "Cochez un ou plusieurs processus pour voir leurs modules.";
            return;
        }
        if (_modulesLoading) return;
        _modulesLoading = true;
        var targets = _rowList.Where(r => r.IsChecked).Select(r => (r.Pid, r.Name)).ToList();
        Task.Run(() => targets.SelectMany(t => ModuleReader.Read(t.Pid, t.Name)).Select(m => new ModuleRow(m)).ToList())
            .ContinueWith(task => Dispatcher.UIThread.Post(() =>
            {
                _modulesLoading = false;
                var modules = task.IsCompletedSuccessfully ? task.Result : [];
                Modules.Sync(modules);
                ModulesHint = modules.Count == 0
                    ? "Modules inaccessibles pour ce processus (relancez en administrateur)."
                    : $"{modules.Count} modules chargés.";
            }));
    }

    // ───────────── Actions ─────────────

    /// <summary>Le processus et tous ses descendants (enfants créés après lui, pour éviter les PID réutilisés).</summary>
    public List<ProcessRow> GetTree(ProcessRow root)
    {
        var result = new List<ProcessRow> { root };
        for (int i = 0; i < result.Count; i++)
        {
            var parent = result[i];
            result.AddRange(_rowList.Where(r => r.ParentPid == parent.Pid && r.Key.CreateTime > parent.Key.CreateTime && !result.Contains(r)));
        }
        return result;
    }

    // ───────────── Trace du noyau : disque par fichier, réseau par adresse ─────────────

    private void ApplyEtw(EtwSample etw)
    {
        var names = new Dictionary<int, string>();
        foreach (var row in _rowList) names[row.Pid] = row.Name;
        string Image(int pid) => pid == 4 ? "System" : names.GetValueOrDefault(pid, $"PID {pid}");

        foreach (var row in _rowList)
        {
            var (read, write) = etw.DiskByPid.GetValueOrDefault(row.Pid);
            row.SetDisk(read, write);
            if (read + write > 0) row.LastIoTick = _tick;
            var (send, receive) = etw.NetByPid.GetValueOrDefault(row.Pid);
            row.SetNetwork(send, receive);
            if (send + receive > 0) row.LastNetTick = _tick;
        }

        var activeFiles = new HashSet<string>();
        foreach (var f in etw.Files)
        {
            string key = $"{f.Pid}|{f.File}";
            if (!activeFiles.Add(key)) continue;
            if (!_fileRows.TryGetValue(key, out var row)) _fileRows[key] = row = new FileRow(f.Pid, Image(f.Pid), f.File);
            row.Update(f.ReadPerSec, f.WritePerSec, f.Priority, f.ResponseMs);
            row.LastActiveTick = _tick;
        }
        foreach (var (key, row) in _fileRows.ToList())
        {
            if (activeFiles.Contains(key)) continue;
            if (_tick - row.LastActiveTick >= 60) _fileRows.Remove(key);
            else row.Update(0, 0, row.Priority, 0);
        }
        Files.Sync(_fileRows.Values.ToList());

        var activeAddresses = new HashSet<string>();
        foreach (var a in etw.Addresses)
        {
            string key = $"{a.Pid}|{a.Address}";
            if (!activeAddresses.Add(key)) continue;
            if (!_addressRows.TryGetValue(key, out var row)) _addressRows[key] = row = new AddressRow(a.Pid, Image(a.Pid), a.Address);
            row.Update(a.SendPerSec, a.ReceivePerSec);
            row.LastActiveTick = _tick;
        }
        foreach (var (key, row) in _addressRows.ToList())
        {
            if (activeAddresses.Contains(key)) continue;
            if (_tick - row.LastActiveTick >= 60) _addressRows.Remove(key);
            else row.Update(0, 0);
        }
        Addresses.Sync(_addressRows.Values.ToList());

        _tcpStats = etw.TcpStats;
    }

    // ───────────── Handles associés ─────────────

    /// <summary>Recherche des handles : texte saisi (tous les processus ou ceux cochés), sinon tous les handles des processus cochés.</summary>
    public async void SearchHandles()
    {
        _handleSearch?.Cancel();
        string text = _handleSearchText.Trim();
        IReadOnlySet<int>? pids = _checkedPids.Count > 0 ? new HashSet<int>(_checkedPids) : null;
        if (text.Length == 0 && pids is null)
        {
            Handles.Sync([]);
            HandleStatus = "Cochez un processus ou saisissez un nom de fichier, de clé ou d'objet.";
            return;
        }
        var cts = _handleSearch = new CancellationTokenSource();
        var imageOf = ImageLookup();
        HandleStatus = "Recherche en cours…";
        try
        {
            var results = await Task.Run(() => HandleSearch.Search(pids, text, imageOf, 5000, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            Handles.Sync(results.Select(r => new HandleRow(r)).ToList());
            HandleStatus = results.Count switch
            {
                0 => IsElevated ? "Aucun handle trouvé." : "Aucun handle trouvé (relancez en administrateur pour voir tous les processus).",
                >= 5000 => "5 000 handles affichés (limite atteinte, affinez la recherche).",
                var n => $"{n:N0} handles trouvés.",
            };
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Copie figée PID → image, utilisable depuis un autre thread.</summary>
    public Func<int, string> ImageLookup()
    {
        var names = new Dictionary<int, string>();
        foreach (var row in _rowList) names[row.Pid] = row.Name;
        return pid => pid == 4 ? "System" : names.GetValueOrDefault(pid, $"PID {pid}");
    }

    public void Dispose() => _sampler.Dispose();
}

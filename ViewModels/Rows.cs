using ResMonNext.Monitoring;
using ResMonNext.Native;

namespace ResMonNext.ViewModels;

/// <summary>
/// Une ligne de processus. La même instance est partagée par toutes les grilles :
/// cocher une case dans un onglet filtre tous les onglets, comme dans resmon.
/// </summary>
public sealed class ProcessRow(ProcessKey key, string name) : ObservableBase
{
    private string _description = "";
    private string _state = StateText(ProcessState.Running);
    private int _threads, _handles;
    private double _cpu, _cpuAverage, _hardFaults;
    private ulong _commitKb, _workingSetKb, _shareableKb, _privateKb;
    private bool _isChecked;
    private double _ioRead, _ioWrite, _ioTotal;
    private int _tcpConnections, _listeningPorts;
    private double _netSend, _netReceive, _netTotal;

    public ProcessKey Key { get; } = key;
    public int Pid => Key.Pid;
    public string Name { get; } = name;
    public int ParentPid { get; private set; }
    public string? Path { get; private set; }
    public string Description { get => _description; private set => Set(ref _description, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public bool IsSuspended { get; private set; }
    public int Threads { get => _threads; private set => Set(ref _threads, value); }
    public int Handles { get => _handles; private set => Set(ref _handles, value); }
    public double Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public double CpuAverage { get => _cpuAverage; private set => Set(ref _cpuAverage, value); }
    public double HardFaults { get => _hardFaults; private set => Set(ref _hardFaults, value); }
    public ulong CommitKb { get => _commitKb; private set => Set(ref _commitKb, value); }
    public ulong WorkingSetKb { get => _workingSetKb; private set => Set(ref _workingSetKb, value); }
    public ulong ShareableKb { get => _shareableKb; private set => Set(ref _shareableKb, value); }
    public ulong PrivateKb { get => _privateKb; private set => Set(ref _privateKb, value); }
    public double IoRead { get => _ioRead; private set => Set(ref _ioRead, value); }
    public double IoWrite { get => _ioWrite; private set => Set(ref _ioWrite, value); }
    public double IoTotal { get => _ioTotal; private set => Set(ref _ioTotal, value); }
    public int TcpConnections { get => _tcpConnections; set => Set(ref _tcpConnections, value); }
    public int ListeningPorts { get => _listeningPorts; set => Set(ref _listeningPorts, value); }

    public double NetSend { get => _netSend; private set => Set(ref _netSend, value); }
    public double NetReceive { get => _netReceive; private set => Set(ref _netReceive, value); }
    public double NetTotal { get => _netTotal; private set => Set(ref _netTotal, value); }

    /// <summary>Dernier cycle où le processus a eu des E/S (l'onglet Disque garde les processus actifs depuis 60 s).</summary>
    public int LastIoTick { get; set; } = int.MinValue / 2;
    public int LastNetTick { get; set; } = int.MinValue / 2;

    /// <summary>Remplace les compteurs d'E/S génériques par l'activité disque réelle mesurée par ETW.</summary>
    public void SetDisk(double read, double write)
    {
        IoRead = Math.Round(read);
        IoWrite = Math.Round(write);
        IoTotal = IoRead + IoWrite;
    }

    public void SetNetwork(double send, double receive)
    {
        NetSend = Math.Round(send);
        NetReceive = Math.Round(receive);
        NetTotal = NetSend + NetReceive;
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) CheckedChanged?.Invoke(this);
        }
    }

    public event Action<ProcessRow>? CheckedChanged;

    public void Update(ProcessSample s)
    {
        ParentPid = s.ParentPid;
        Path = s.Path;
        Description = s.Description;
        IsSuspended = s.State == ProcessState.Suspended;
        State = StateText(s.State);
        Threads = s.Threads;
        Handles = s.Handles;
        Cpu = Math.Round(s.Cpu, 1);
        CpuAverage = Math.Round(s.CpuAverage, 2);
        HardFaults = Math.Round(s.HardFaultsPerSec);
        CommitKb = s.Commit / 1024;
        WorkingSetKb = s.WorkingSet / 1024;
        PrivateKb = s.PrivateWorkingSet / 1024;
        ShareableKb = (s.WorkingSet - s.PrivateWorkingSet) / 1024;
        IoRead = Math.Round(s.IoReadPerSec);
        IoWrite = Math.Round(s.IoWritePerSec);
        IoTotal = IoRead + IoWrite;
    }

    private static string StateText(ProcessState state) => state switch
    {
        ProcessState.Suspended => "Suspendu",
        ProcessState.NotResponding => "Ne répond pas",
        _ => "En cours d'exécution",
    };
}

public sealed class ServiceRow(string name) : ObservableBase
{
    private string _displayName = "", _state = "", _group = "";
    private int _pid;
    private double _cpu;
    private uint _rawState;

    public string Name { get; } = name;
    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public int Pid { get => _pid; private set => Set(ref _pid, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string Group { get => _group; private set => Set(ref _group, value); }
    public double Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public bool IsRunning => _rawState == Win32.SERVICE_RUNNING;

    public void Update(ServiceSample s)
    {
        DisplayName = s.DisplayName;
        Pid = s.Pid;
        Group = s.Group;
        _rawState = s.State;
        State = s.State switch
        {
            Win32.SERVICE_RUNNING => "En cours d'exécution",
            Win32.SERVICE_STOPPED => "Arrêté",
            Win32.SERVICE_START_PENDING => "Démarrage…",
            Win32.SERVICE_STOP_PENDING => "Arrêt…",
            Win32.SERVICE_PAUSED => "Suspendu",
            Win32.SERVICE_PAUSE_PENDING => "Suspension…",
            Win32.SERVICE_CONTINUE_PENDING => "Reprise…",
            _ => "Inconnu",
        };
        if (!IsRunning) Cpu = 0;
    }
}

public sealed class ModuleRow(ModuleSample sample)
{
    public string Image { get; } = sample.Image;
    public int Pid { get; } = sample.Pid;
    public string Name { get; } = sample.Name;
    public string Version { get; } = sample.Version;
    public string Path { get; } = sample.Path;
}

public sealed class CoreGraph(string title) : ObservableBase
{
    private string _usageText = "0 %";

    public string Title { get; } = title;
    public GraphModel Graph { get; } = new(100, 100, new GraphSeries(Palette.Cpu, fill: true));
    public string UsageText { get => _usageText; private set => Set(ref _usageText, value); }

    public void Push(double usage)
    {
        Graph.Push(usage);
        UsageText = Fmt.Percent(usage);
    }
}

public sealed class LegendItem(string label, Avalonia.Media.Color color) : ObservableBase
{
    private string _value = "";

    public string Label { get; } = label;
    public Avalonia.Media.IBrush Brush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color);
    public string Value { get => _value; set => Set(ref _value, value); }
}

public sealed class ConnectionRow(ConnectionSample sample, string image) : ObservableBase
{
    private string _state = sample.State;
    private double _loss, _latency = double.NaN;
    private string _lossText = "—", _latencyText = "—";

    public double Loss { get => _loss; private set => Set(ref _loss, value); }
    public double Latency { get => _latency; private set => Set(ref _latency, value); }
    public string LossText { get => _lossText; private set => Set(ref _lossText, value); }
    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }

    /// <summary>Clé de rapprochement avec les statistiques TCP de la trace ETW.</summary>
    public string TcpStatsKey => $"{Pid}|{LocalPort}|{RemoteAddress}|{RemotePort}";

    public void SetQuality(double latencyMs, (long Sends, long Retransmits)? stats)
    {
        if (!double.IsNaN(latencyMs))
        {
            Latency = latencyMs;
            LatencyText = latencyMs.ToString("0");
        }
        if (stats is { Sends: > 0 } s)
        {
            Loss = Math.Min(100, s.Retransmits * 100.0 / s.Sends);
            LossText = Loss.ToString("0.0");
        }
    }

    public string Key { get; } = sample.Key;
    public string Image { get; } = image;
    public int Pid { get; } = sample.Pid;
    public string LocalAddress { get; } = sample.LocalAddress;
    public int LocalPort { get; } = sample.LocalPort;
    public string RemoteAddress { get; } = sample.RemoteAddress;
    public int RemotePort { get; } = sample.RemotePort;
    public string State { get => _state; set => Set(ref _state, value); }
}

public sealed class ListenerRow(ListenerSample sample, string image)
{
    public string Key { get; } = sample.Key;
    public string Image { get; } = image;
    public int Pid { get; } = sample.Pid;
    public string Protocol { get; } = sample.Protocol;
    public string Address { get; } = sample.Address;
    public int Port { get; } = sample.Port;
}

public sealed class StorageRow(string name) : ObservableBase
{
    private string _physical = "";
    private double _active, _queue;
    private ulong _freeMb, _totalMb;

    public string Name { get; } = name;
    public string Physical { get => _physical; private set => Set(ref _physical, value); }
    public double Active { get => _active; private set => Set(ref _active, value); }
    public double Queue { get => _queue; private set => Set(ref _queue, value); }
    public ulong FreeMb { get => _freeMb; private set => Set(ref _freeMb, value); }
    public ulong TotalMb { get => _totalMb; private set => Set(ref _totalMb, value); }

    public void Update(VolumeSample v)
    {
        Physical = v.PhysicalDisk;
        Active = double.IsNaN(v.ActivePercent) ? 0 : Math.Round(v.ActivePercent);
        Queue = double.IsNaN(v.QueueLength) ? 0 : Math.Round(v.QueueLength, 2);
        FreeMb = v.FreeBytes / (1024 * 1024);
        TotalMb = v.TotalBytes / (1024 * 1024);
    }
}

/// <summary>Activité disque d'un fichier pour un processus (onglet Disque, trace ETW).</summary>
public sealed class FileRow(int pid, string image, string file) : ObservableBase
{
    private double _read, _write, _total, _response;
    private string _priority = "Normale";

    public string Key { get; } = $"{pid}|{file}";
    public int Pid { get; } = pid;
    public string Image { get; } = image;
    public string File { get; } = file;
    public double Read { get => _read; private set => Set(ref _read, value); }
    public double Write { get => _write; private set => Set(ref _write, value); }
    public double Total { get => _total; private set => Set(ref _total, value); }
    public string Priority { get => _priority; private set => Set(ref _priority, value); }
    public double Response { get => _response; private set => Set(ref _response, value); }
    public int LastActiveTick { get; set; }

    public void Update(double read, double write, string priority, double responseMs)
    {
        Read = Math.Round(read);
        Write = Math.Round(write);
        Total = Read + Write;
        Priority = priority;
        if (responseMs > 0) Response = Math.Round(responseMs, 1);
    }
}

/// <summary>Trafic d'un processus avec une adresse distante (onglet Réseau, trace ETW).</summary>
public sealed class AddressRow(int pid, string image, string address) : ObservableBase
{
    private double _send, _receive, _total;

    public string Key { get; } = $"{pid}|{address}";
    public int Pid { get; } = pid;
    public string Image { get; } = image;
    public string Address { get; } = address;
    public double Send { get => _send; private set => Set(ref _send, value); }
    public double Receive { get => _receive; private set => Set(ref _receive, value); }
    public double Total { get => _total; private set => Set(ref _total, value); }
    public int LastActiveTick { get; set; }

    public void Update(double send, double receive)
    {
        Send = Math.Round(send);
        Receive = Math.Round(receive);
        Total = Send + Receive;
    }
}

public sealed class HandleRow(HandleSample sample)
{
    public string Image { get; } = sample.Image;
    public int Pid { get; } = sample.Pid;
    public string Type { get; } = sample.Type;
    public string Name { get; } = sample.Name;
}

using System.Collections.ObjectModel;

namespace ResMonNext.ViewModels;

/// <summary>
/// Collection triée et filtrée qui se met à jour par différences (insertions, suppressions, déplacements)
/// au lieu de tout reconstruire : les lignes gardent leur identité, la grille ne clignote pas
/// et la sélection est conservée.
/// </summary>
public class RowTable<T> : ObservableBase where T : class
{
    private readonly Dictionary<string, (Comparison<T> Compare, bool DescendingByDefault)> _columns = new();
    private readonly Comparison<T> _tieBreak;
    private IReadOnlyList<T> _source = [];
    private string _sortKey;
    private bool _descending;

    protected RowTable(string defaultSortKey, bool defaultDescending, Comparison<T> tieBreak)
    {
        _sortKey = defaultSortKey;
        _descending = defaultDescending;
        _tieBreak = tieBreak;
    }

    public ObservableCollection<T> Items { get; } = [];

    public string SortKey => _sortKey;

    public bool Descending => _descending;

    /// <summary>Lignes épinglées en tête (processus cochés), quel que soit le tri.</summary>
    public Func<T, bool>? Pinned { get; set; }

    public Func<T, bool>? Filter { get; set; }

    public event Action? SortChanged;

    /// <summary>Encadrent une resynchronisation (la vue y sauvegarde/restaure la ligne sélectionnée).</summary>
    public event Action? Syncing, Synced;

    protected void Column(string key, Comparison<T> compare, bool descendingByDefault = false) =>
        _columns[key] = (compare, descendingByDefault);

    public void SortBy(string? key)
    {
        if (key is null || !_columns.TryGetValue(key, out var column)) return;
        if (_sortKey == key) _descending = !_descending;
        else
        {
            _sortKey = key;
            _descending = column.DescendingByDefault;
        }
        Refresh();
        SortChanged?.Invoke();
    }

    public void Refresh() => Sync(_source);

    public void Sync(IReadOnlyList<T> source)
    {
        _source = source;
        var desired = new List<T>(source.Count);
        foreach (var item in source)
            if (Filter is null || Filter(item) || (Pinned?.Invoke(item) ?? false))
                desired.Add(item);
        desired.Sort(Compare);
        Syncing?.Invoke();

        var keep = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (int i = Items.Count - 1; i >= 0; i--)
            if (!keep.Contains(Items[i])) Items.RemoveAt(i);

        for (int i = 0; i < desired.Count; i++)
        {
            var wanted = desired[i];
            if (i < Items.Count && ReferenceEquals(Items[i], wanted)) continue;
            int current = -1;
            for (int j = i + 1; j < Items.Count; j++)
            {
                if (ReferenceEquals(Items[j], wanted))
                {
                    current = j;
                    break;
                }
            }
            // La DataGrid d'Avalonia ignore les notifications « Move » : on retire puis on réinsère.
            if (current >= 0) Items.RemoveAt(current);
            Items.Insert(i, wanted);
        }
        Synced?.Invoke();
    }

    private int Compare(T a, T b)
    {
        if (Pinned is not null)
        {
            bool pa = Pinned(a), pb = Pinned(b);
            if (pa != pb) return pa ? -1 : 1;
        }
        int result = _columns.TryGetValue(_sortKey, out var column) ? column.Compare(a, b) : 0;
        if (_descending) result = -result;
        return result != 0 ? result : _tieBreak(a, b);
    }
}

public sealed class ProcessTable : RowTable<ProcessRow>
{
    public ProcessTable(MainViewModel owner, string defaultSortKey) : base(defaultSortKey, true, TieBreak)
    {
        Owner = owner;
        Column("Checked", (a, b) => a.IsChecked.CompareTo(b.IsChecked), true);
        Column("Name", (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Column("Pid", (a, b) => a.Pid.CompareTo(b.Pid));
        Column("Description", (a, b) => string.Compare(a.Description, b.Description, StringComparison.CurrentCultureIgnoreCase));
        Column("State", (a, b) => string.CompareOrdinal(a.State, b.State));
        Column("Threads", (a, b) => a.Threads.CompareTo(b.Threads), true);
        Column("Cpu", (a, b) => a.Cpu.CompareTo(b.Cpu), true);
        Column("CpuAverage", (a, b) => a.CpuAverage.CompareTo(b.CpuAverage), true);
        Column("HardFaults", (a, b) => a.HardFaults.CompareTo(b.HardFaults), true);
        Column("Commit", (a, b) => a.CommitKb.CompareTo(b.CommitKb), true);
        Column("WorkingSet", (a, b) => a.WorkingSetKb.CompareTo(b.WorkingSetKb), true);
        Column("Shareable", (a, b) => a.ShareableKb.CompareTo(b.ShareableKb), true);
        Column("Private", (a, b) => a.PrivateKb.CompareTo(b.PrivateKb), true);
        Column("IoRead", (a, b) => a.IoRead.CompareTo(b.IoRead), true);
        Column("IoWrite", (a, b) => a.IoWrite.CompareTo(b.IoWrite), true);
        Column("IoTotal", (a, b) => a.IoTotal.CompareTo(b.IoTotal), true);
        Column("TcpConnections", (a, b) => a.TcpConnections.CompareTo(b.TcpConnections), true);
        Column("ListeningPorts", (a, b) => a.ListeningPorts.CompareTo(b.ListeningPorts), true);
        Column("NetSend", (a, b) => a.NetSend.CompareTo(b.NetSend), true);
        Column("NetReceive", (a, b) => a.NetReceive.CompareTo(b.NetReceive), true);
        Column("NetTotal", (a, b) => a.NetTotal.CompareTo(b.NetTotal), true);
        Pinned = r => r.IsChecked;
    }

    public MainViewModel Owner { get; }

    private static int TieBreak(ProcessRow a, ProcessRow b)
    {
        int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : a.Pid.CompareTo(b.Pid);
    }
}

public sealed class ServiceTable : RowTable<ServiceRow>
{
    public ServiceTable() : base("Name", false, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
    {
        Column("Name", (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Column("Pid", (a, b) => a.Pid.CompareTo(b.Pid));
        Column("Description", (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        Column("State", (a, b) => string.CompareOrdinal(a.State, b.State));
        Column("Group", (a, b) => string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase));
        Column("Cpu", (a, b) => a.Cpu.CompareTo(b.Cpu), true);
    }
}

public sealed class ModuleTable : RowTable<ModuleRow>
{
    public ModuleTable() : base("Name", false, (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase))
    {
        Column("Image", (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase));
        Column("Pid", (a, b) => a.Pid.CompareTo(b.Pid));
        Column("Name", (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Column("Version", (a, b) => string.CompareOrdinal(a.Version, b.Version));
        Column("Path", (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ConnectionTable : RowTable<ConnectionRow>
{
    public ConnectionTable() : base("Image", false, (a, b) => string.CompareOrdinal(a.Key, b.Key))
    {
        Column("Image", (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase));
        Column("Pid", (a, b) => a.Pid.CompareTo(b.Pid));
        Column("LocalAddress", (a, b) => string.CompareOrdinal(a.LocalAddress, b.LocalAddress));
        Column("LocalPort", (a, b) => a.LocalPort.CompareTo(b.LocalPort));
        Column("RemoteAddress", (a, b) => string.CompareOrdinal(a.RemoteAddress, b.RemoteAddress));
        Column("RemotePort", (a, b) => a.RemotePort.CompareTo(b.RemotePort));
        Column("State", (a, b) => string.CompareOrdinal(a.State, b.State));
        Column("Loss", (a, b) => a.Loss.CompareTo(b.Loss), true);
        Column("Latency", (a, b) => (double.IsNaN(a.Latency) ? -1 : a.Latency).CompareTo(double.IsNaN(b.Latency) ? -1 : b.Latency), true);
    }
}

public sealed class ListenerTable : RowTable<ListenerRow>
{
    public ListenerTable() : base("Image", false, (a, b) => string.CompareOrdinal(a.Key, b.Key))
    {
        Column("Image", (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase));
        Column("Pid", (a, b) => a.Pid.CompareTo(b.Pid));
        Column("Address", (a, b) => string.CompareOrdinal(a.Address, b.Address));
        Column("Port", (a, b) => a.Port.CompareTo(b.Port));
        Column("Protocol", (a, b) => string.CompareOrdinal(a.Protocol, b.Protocol));
    }
}

public sealed class StorageTable : RowTable<StorageRow>
{
    public StorageTable() : base("Name", false, (a, b) => string.CompareOrdinal(a.Name, b.Name))
    {
        Column("Name", (a, b) => string.CompareOrdinal(a.Name, b.Name));
        Column("Physical", (a, b) => string.CompareOrdinal(a.Physical, b.Physical));
        Column("Active", (a, b) => a.Active.CompareTo(b.Active), true);
        Column("Free", (a, b) => a.FreeMb.CompareTo(b.FreeMb), true);
        Column("Total", (a, b) => a.TotalMb.CompareTo(b.TotalMb), true);
        Column("Queue", (a, b) => a.Queue.CompareTo(b.Queue), true);
    }
}

public sealed class FileTable : RowTable<FileRow>
{
    public FileTable() : base("Total", true, (a, b) => string.CompareOrdinal(a.Key, b.Key))
    {
        Column("Image", (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase));
        Column("Pid", (a, b) => a.Pid.CompareTo(b.Pid));
        Column("File", (a, b) => string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));
        Column("Read", (a, b) => a.Read.CompareTo(b.Read), true);
        Column("Write", (a, b) => a.Write.CompareTo(b.Write), true);
        Column("Total", (a, b) => a.Total.CompareTo(b.Total), true);
        Column("Priority", (a, b) => string.CompareOrdinal(a.Priority, b.Priority));
        Column("Response", (a, b) => a.Response.CompareTo(b.Response), true);
    }
}

public sealed class AddressTable : RowTable<AddressRow>
{
    public AddressTable() : base("Total", true, (a, b) => string.CompareOrdinal(a.Key, b.Key))
    {
        Column("Image", (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase));
        Column("Pid", (a, b) => a.Pid.CompareTo(b.Pid));
        Column("Address", (a, b) => string.CompareOrdinal(a.Address, b.Address));
        Column("Send", (a, b) => a.Send.CompareTo(b.Send), true);
        Column("Receive", (a, b) => a.Receive.CompareTo(b.Receive), true);
        Column("Total", (a, b) => a.Total.CompareTo(b.Total), true);
    }
}

public sealed class HandleTable : RowTable<HandleRow>
{
    public HandleTable() : base("Image", false, (a, b) => string.CompareOrdinal(a.Name, b.Name))
    {
        Column("Image", (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase));
        Column("Pid", (a, b) => a.Pid.CompareTo(b.Pid));
        Column("Type", (a, b) => string.CompareOrdinal(a.Type, b.Type));
        Column("Name", (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }
}

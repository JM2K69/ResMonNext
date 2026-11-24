namespace ResMonNext.Monitoring;

/// <summary>Identifie un processus de façon unique (le PID seul peut être réutilisé).</summary>
public readonly record struct ProcessKey(int Pid, long CreateTime);

public enum ProcessState { Running, Suspended, NotResponding }

public sealed class ProcessSample
{
    public required ProcessKey Key { get; init; }
    public required int ParentPid { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public string Description { get; init; } = "";
    public int Threads { get; init; }
    public int Handles { get; init; }
    public double Cpu { get; init; }
    public double CpuAverage { get; init; }
    public ulong WorkingSet { get; init; }
    public ulong PrivateWorkingSet { get; init; }
    public ulong Commit { get; init; }
    public double HardFaultsPerSec { get; init; }
    /// <summary>Octets lus/écrits par seconde (toutes E/S du processus : fichiers, disque, réseau, canaux).</summary>
    public double IoReadPerSec { get; init; }
    public double IoWritePerSec { get; init; }
    public ProcessState State { get; init; }
}

public sealed class VolumeSample
{
    public required string Name { get; init; }
    public string PhysicalDisk { get; init; } = "";
    public double ActivePercent { get; init; } = double.NaN;
    public double QueueLength { get; init; } = double.NaN;
    public ulong FreeBytes { get; init; }
    public ulong TotalBytes { get; init; }
}

public sealed class ServiceSample
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public int Pid { get; init; }
    public uint State { get; init; }
    public string Group { get; init; } = "";
}

public sealed class MemorySample
{
    public ulong Installed { get; init; }
    public ulong Total { get; init; }
    public ulong Available { get; init; }
    public ulong HardwareReserved { get; init; }
    public ulong InUse { get; init; }
    public ulong Modified { get; init; }
    public ulong Standby { get; init; }
    public ulong Free { get; init; }
    public ulong CommitTotal { get; init; }
    public ulong CommitLimit { get; init; }
    public bool ListsAvailable { get; init; }
    public double UsedPercent => Total == 0 ? 0 : (Total - Available) * 100.0 / Total;
    public double CommitPercent => CommitLimit == 0 ? 0 : CommitTotal * 100.0 / CommitLimit;
    public ulong Cached => Standby + Modified;
}

public sealed class Snapshot
{
    public required ProcessSample[] Processes { get; init; }
    public required double[] Cores { get; init; }
    public required MemorySample Memory { get; init; }
    public double CpuTotal { get; init; }
    /// <summary>% de la fréquence maximale (compteur « % Processor Performance »), NaN si indisponible.</summary>
    public double MaxFrequencyPercent { get; init; } = double.NaN;
    public double DiskBytesPerSec { get; init; } = double.NaN;
    public double DiskActivePercent { get; init; } = double.NaN;
    public double NetBytesPerSec { get; init; } = double.NaN;
    public double NetUtilizationPercent { get; init; } = double.NaN;
    public double HardFaultsPerSec { get; init; }
    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    /// <summary>Null quand les services n'ont pas été relus à ce cycle.</summary>
    public ServiceSample[]? Services { get; init; }
    /// <summary>Null quand le réseau n'a pas été relu à ce cycle.</summary>
    public ConnectionSample[]? Connections { get; init; }
    public ListenerSample[]? Listeners { get; init; }
    public VolumeSample[]? Volumes { get; init; }
    /// <summary>Données de la trace ETW du noyau (null sans droits administrateur).</summary>
    public EtwSample? Etw { get; init; }
    public bool LatencyAvailable { get; init; }
    public bool EtwActive { get; init; }
    public string EtwStatus { get; init; } = "";
    public DateTime Timestamp { get; init; }
}

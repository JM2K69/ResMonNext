using System.Runtime.InteropServices;

namespace ResMonNext.Native;

/// <summary>Interop ETW (Event Tracing for Windows) : session noyau temps réel, sans bibliothèque externe.</summary>
internal static unsafe partial class Etw
{
    public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    public const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;
    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    public const ushort EVENT_HEADER_FLAG_32_BIT_HEADER = 0x0020;
    public const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    public const uint EVENT_TRACE_FLAG_THREAD = 0x00000002;
    public const uint EVENT_TRACE_FLAG_DISK_IO = 0x00000100;
    public const uint EVENT_TRACE_FLAG_DISK_FILE_IO = 0x00000200;
    public const uint EVENT_TRACE_FLAG_NETWORK_TCPIP = 0x00010000;

    public const string KERNEL_LOGGER_NAME = "NT Kernel Logger";
    public static readonly Guid SystemTraceControlGuid = new("9e814aad-3204-11d2-9a82-006008a86939");

    // Fournisseurs « classiques » du noyau (événements MOF)
    public static readonly Guid ThreadGuid = new("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
    public static readonly Guid DiskIoGuid = new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");
    public static readonly Guid FileIoGuid = new("90cbdc39-4a3e-11d1-84f4-0000f80464e3");
    public static readonly Guid TcpIpGuid = new("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
    public static readonly Guid UdpIpGuid = new("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint StartTrace(ulong* traceHandle, string instanceName, EVENT_TRACE_PROPERTIES* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ControlTrace(ulong traceHandle, string? instanceName, EVENT_TRACE_PROPERTIES* properties, uint controlCode);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW")]
    public static partial ulong OpenTrace(EVENT_TRACE_LOGFILEW* logfile);

    [LibraryImport("advapi32.dll")]
    public static partial uint ProcessTrace(ulong* handleArray, uint handleCount, nint startTime, nint endTime);

    [LibraryImport("advapi32.dll")]
    public static partial uint CloseTrace(ulong traceHandle);

    // ───────────── Statistiques TCP étendues (latence) ─────────────
    public const int TcpConnectionEstatsPath = 3;

    [LibraryImport("iphlpapi.dll")]
    public static partial uint SetPerTcpConnectionEStats(void* row, int estatsType, byte* rw, uint rwVersion, uint rwSize, uint offset);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetPerTcpConnectionEStats(void* row, int estatsType, byte* rw, uint rwVersion, uint rwSize,
        byte* ros, uint rosVersion, uint rosSize, byte* rod, uint rodVersion, uint rodSize);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint SetPerTcp6ConnectionEStats(void* row, int estatsType, byte* rw, uint rwVersion, uint rwSize, uint offset);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetPerTcp6ConnectionEStats(void* row, int estatsType, byte* rw, uint rwVersion, uint rwSize,
        byte* ros, uint rosVersion, uint rosSize, byte* rod, uint rodVersion, uint rodSize);

    // ───────────── Chemins NT → lettres de lecteur ─────────────
    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint QueryDosDevice(string deviceName, char* targetPath, uint max);
}

[StructLayout(LayoutKind.Sequential)]
internal struct WNODE_HEADER
{
    public uint BufferSize;
    public uint ProviderId;
    public ulong HistoricalContext;
    public long TimeStamp;
    public Guid Guid;
    public uint ClientContext;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_TRACE_PROPERTIES
{
    public WNODE_HEADER Wnode;
    public uint BufferSize;
    public uint MinimumBuffers;
    public uint MaximumBuffers;
    public uint MaximumFileSize;
    public uint LogFileMode;
    public uint FlushTimer;
    public uint EnableFlags;
    public int AgeLimit;
    public uint NumberOfBuffers;
    public uint FreeBuffers;
    public uint EventsLost;
    public uint BuffersWritten;
    public uint LogBuffersLost;
    public uint RealTimeBuffersLost;
    public nint LoggerThreadId;
    public uint LogFileNameOffset;
    public uint LoggerNameOffset;
}

/// <summary>Seuls les champs utilisés sont déclarés ; taille x64 = 0x1C0.</summary>
[StructLayout(LayoutKind.Explicit, Size = 448)]
internal unsafe struct EVENT_TRACE_LOGFILEW
{
    [FieldOffset(0)] public char* LogFileName;
    [FieldOffset(8)] public char* LoggerName;
    [FieldOffset(28)] public uint ProcessTraceMode;
    [FieldOffset(424)] public delegate* unmanaged<EVENT_RECORD*, void> EventRecordCallback;
    [FieldOffset(440)] public nint Context;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_DESCRIPTOR
{
    public ushort Id;
    public byte Version;
    public byte Channel;
    public byte Level;
    public byte Opcode;
    public ushort Task;
    public ulong Keyword;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EVENT_HEADER
{
    public ushort Size;
    public ushort HeaderType;
    public ushort Flags;
    public ushort EventProperty;
    public uint ThreadId;
    public uint ProcessId;
    public long TimeStamp;
    public Guid ProviderId;
    public EVENT_DESCRIPTOR EventDescriptor;
    public ulong ProcessorTime;
    public Guid ActivityId;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EVENT_RECORD
{
    public EVENT_HEADER EventHeader;
    public uint BufferContext;
    public ushort ExtendedDataCount;
    public ushort UserDataLength;
    public nint ExtendedData;
    public byte* UserData;
    public nint UserContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_TCPROW
{
    public uint dwState;
    public uint dwLocalAddr;
    public uint dwLocalPort;
    public uint dwRemoteAddr;
    public uint dwRemotePort;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MIB_TCP6ROW
{
    public uint State;
    public fixed byte LocalAddr[16];
    public uint dwLocalScopeId;
    public uint dwLocalPort;
    public fixed byte RemoteAddr[16];
    public uint dwRemoteScopeId;
    public uint dwRemotePort;
}

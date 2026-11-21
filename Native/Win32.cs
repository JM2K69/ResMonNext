using System.Runtime.InteropServices;

namespace ResMonNext.Native;

// Toutes les déclarations utilisent [LibraryImport] (marshalling généré à la compilation)
// et des structures blittables passées par pointeur : compatible NativeAOT, sans réflexion.
internal static unsafe partial class Win32
{
    // ───────────── ntdll ─────────────
    public const int SystemProcessInformation = 5;
    public const int SystemProcessorPerformanceInformation = 8;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint length, uint* returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int NtSuspendProcess(nint processHandle);

    [LibraryImport("ntdll.dll")]
    public static partial int NtResumeProcess(nint processHandle);

    // ───────────── kernel32 ─────────────
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TH32CS_SNAPMODULE = 0x00000008;
    public const uint TH32CS_SNAPMODULE32 = 0x00000010;
    public static readonly nint INVALID_HANDLE_VALUE = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* exeName, uint* size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(MEMORYSTATUSEX* buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPhysicallyInstalledSystemMemory(ulong* totalMemoryInKilobytes);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(PERFORMANCE_INFORMATION* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Module32FirstW(nint snapshot, MODULEENTRY32W* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Module32NextW(nint snapshot, MODULEENTRY32W* entry);

    // ───────────── user32 ─────────────
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsHungAppWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, uint* processId);

    // ───────────── advapi32 (services) ─────────────
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    public const uint SERVICE_QUERY_CONFIG = 0x0001;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const uint SERVICE_START = 0x0010;
    public const uint SERVICE_STOP = 0x0020;
    public const uint SERVICE_WIN32 = 0x00000030;
    public const uint SERVICE_STATE_ALL = 0x00000003;
    public const uint SERVICE_CONTROL_STOP = 0x00000001;
    public const int SC_ENUM_PROCESS_INFO = 0;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public const uint SERVICE_STOPPED = 1;
    public const uint SERVICE_START_PENDING = 2;
    public const uint SERVICE_STOP_PENDING = 3;
    public const uint SERVICE_RUNNING = 4;
    public const uint SERVICE_CONTINUE_PENDING = 5;
    public const uint SERVICE_PAUSE_PENDING = 6;
    public const uint SERVICE_PAUSED = 7;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenService(nint scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumServicesStatusEx(nint scManager, int infoLevel, uint serviceType, uint serviceState,
        byte* services, uint bufSize, uint* bytesNeeded, uint* servicesReturned, uint* resumeHandle, char* groupName);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceConfig(nint service, byte* config, uint bufSize, uint* bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StartService(nint service, uint argc, nint argv);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ControlService(nint service, uint control, SERVICE_STATUS* status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatus(nint service, SERVICE_STATUS* status);

    // ───────────── pdh (compteurs de performance) ─────────────
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW")]
    public static partial int PdhOpenQuery(nint dataSource, nint userData, nint* query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int PdhAddEnglishCounter(nint query, string counterPath, nint userData, nint* counter);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial int PdhGetFormattedCounterValue(nint counter, uint format, uint* type, PDH_FMT_COUNTERVALUE* value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial int PdhGetFormattedCounterArray(nint counter, uint format, uint* bufferSize, uint* itemCount, byte* items);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCloseQuery(nint query);

    // ───────────── iphlpapi (connexions TCP / points de terminaison UDP) ─────────────
    public const uint AF_INET = 2;
    public const uint AF_INET6 = 23;
    public const int TCP_TABLE_OWNER_PID_ALL = 5;
    public const int UDP_TABLE_OWNER_PID = 1;

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedTcpTable(void* table, uint* size, [MarshalAs(UnmanagedType.Bool)] bool order, uint af, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedUdpTable(void* table, uint* size, [MarshalAs(UnmanagedType.Bool)] bool order, uint af, int tableClass, uint reserved);

    // ───────────── Handles ─────────────
    public const int SystemExtendedHandleInformation = 64;
    public const int ObjectNameInformation = 1;
    public const int ObjectTypeInformation = 2;
    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint FILE_TYPE_DISK = 1;
    public const uint FILE_TYPE_CHAR = 2;
    public const uint FILE_TYPE_PIPE = 3;

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryObject(nint handle, int infoClass, void* info, uint length, uint* returnLength);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, nint* targetHandle,
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetFileType(nint file);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(nint file, char* path, uint length, uint flags);

    // ───────────── Threads et chaîne d'attente (Wait Chain Traversal) ─────────────
    public const uint TH32CS_SNAPTHREAD = 0x00000004;
    public const uint WCT_OUT_OF_PROC_ALL = 0x7; // WCT_OUT_OF_PROC_FLAG | COM | CS
    public const int WCT_MAX_NODE_COUNT = 16;
    public const int WctThreadType = 8;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Thread32First(nint snapshot, THREADENTRY32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Thread32Next(nint snapshot, THREADENTRY32* entry);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial nint OpenThreadWaitChainSession(uint flags, nint callback);

    [LibraryImport("advapi32.dll")]
    public static partial void CloseThreadWaitChainSession(nint session);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetThreadWaitChain(nint session, nint context, uint flags, uint threadId, uint* nodeCount,
        WAITCHAIN_NODE_INFO* nodeInfoArray, int* isCycle);

    // ───────────── Privilège de débogage (mode administrateur uniquement) ─────────────
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, nint* token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? systemName, string name, long* luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TOKEN_PRIVILEGES* newState,
        uint bufferLength, nint previousState, nint returnLength);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
{
    public nint Object;
    public nuint UniqueProcessId;
    public nuint HandleValue;
    public uint GrantedAccess;
    public ushort CreatorBackTraceIndex;
    public ushort ObjectTypeIndex;
    public uint HandleAttributes;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct THREADENTRY32
{
    public uint dwSize;
    public uint cntUsage;
    public uint th32ThreadID;
    public uint th32OwnerProcessID;
    public int tpBasePri;
    public int tpDeltaPri;
    public uint dwFlags;
}

/// <summary>Taille x64 : 0x118 (union LockObject / ThreadObject à l'offset 8).</summary>
[StructLayout(LayoutKind.Explicit, Size = 280)]
internal unsafe struct WAITCHAIN_NODE_INFO
{
    [FieldOffset(0)] public int ObjectType;
    [FieldOffset(4)] public int ObjectStatus;
    [FieldOffset(8)] public fixed char ObjectName[128];
    [FieldOffset(8)] public uint ProcessId;
    [FieldOffset(12)] public uint ThreadId;
    [FieldOffset(16)] public uint WaitTime;
    [FieldOffset(20)] public uint ContextSwitches;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public long Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_TCPROW_OWNER_PID
{
    public uint dwState;
    public uint dwLocalAddr;
    public uint dwLocalPort;
    public uint dwRemoteAddr;
    public uint dwRemotePort;
    public uint dwOwningPid;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MIB_TCP6ROW_OWNER_PID
{
    public fixed byte ucLocalAddr[16];
    public uint dwLocalScopeId;
    public uint dwLocalPort;
    public fixed byte ucRemoteAddr[16];
    public uint dwRemoteScopeId;
    public uint dwRemotePort;
    public uint dwState;
    public uint dwOwningPid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_UDPROW_OWNER_PID
{
    public uint dwLocalAddr;
    public uint dwLocalPort;
    public uint dwOwningPid;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MIB_UDP6ROW_OWNER_PID
{
    public fixed byte ucLocalAddr[16];
    public uint dwLocalScopeId;
    public uint dwLocalPort;
    public uint dwOwningPid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
}

// Disposition x64 documentée par phnt (System Informer) : 0x100 octets.
[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_PROCESS_INFORMATION
{
    public uint NextEntryOffset;
    public uint NumberOfThreads;
    public long WorkingSetPrivateSize;
    public uint HardFaultCount;
    public uint NumberOfThreadsHighWatermark;
    public ulong CycleTime;
    public long CreateTime;
    public long UserTime;
    public long KernelTime;
    public UNICODE_STRING ImageName;
    public int BasePriority;
    public nint UniqueProcessId;
    public nint InheritedFromUniqueProcessId;
    public uint HandleCount;
    public uint SessionId;
    public nuint UniqueProcessKey;
    public nuint PeakVirtualSize;
    public nuint VirtualSize;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivatePageCount;
    public long ReadOperationCount;
    public long WriteOperationCount;
    public long OtherOperationCount;
    public long ReadTransferCount;
    public long WriteTransferCount;
    public long OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_THREAD_INFORMATION
{
    public long KernelTime;
    public long UserTime;
    public long CreateTime;
    public uint WaitTime;
    public nint StartAddress;
    public nint UniqueProcess;
    public nint UniqueThread;
    public int Priority;
    public int BasePriority;
    public uint ContextSwitches;
    public uint ThreadState;
    public uint WaitReason;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
{
    public long IdleTime;
    public long KernelTime;
    public long UserTime;
    public long DpcTime;
    public long InterruptTime;
    public uint InterruptCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PERFORMANCE_INFORMATION
{
    public uint cb;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MODULEENTRY32W
{
    public uint dwSize;
    public uint th32ModuleID;
    public uint th32ProcessID;
    public uint GlblcntUsage;
    public uint ProccntUsage;
    public nint modBaseAddr;
    public uint modBaseSize;
    public nint hModule;
    public fixed char szModule[256];
    public fixed char szExePath[260];
}

[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_STATUS_PROCESS
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
    public uint dwProcessId;
    public uint dwServiceFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ENUM_SERVICE_STATUS_PROCESSW
{
    public nint lpServiceName;
    public nint lpDisplayName;
    public SERVICE_STATUS_PROCESS ServiceStatusProcess;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_STATUS
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
}

[StructLayout(LayoutKind.Sequential)]
internal struct QUERY_SERVICE_CONFIGW
{
    public uint dwServiceType;
    public uint dwStartType;
    public uint dwErrorControl;
    public nint lpBinaryPathName;
    public nint lpLoadOrderGroup;
    public uint dwTagId;
    public nint lpDependencies;
    public nint lpServiceStartName;
    public nint lpDisplayName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PDH_FMT_COUNTERVALUE
{
    public uint CStatus;
    public double DoubleValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PDH_FMT_COUNTERVALUE_ITEM_W
{
    public nint szName;
    public PDH_FMT_COUNTERVALUE FmtValue;
}

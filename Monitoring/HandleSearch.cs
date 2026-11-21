using System.Runtime.InteropServices;
using ResMonNext.Native;

namespace ResMonNext.Monitoring;

public sealed record HandleSample(int Pid, string Image, string Type, string Name);

/// <summary>
/// Handles associés (onglet Processeur) : énumère les handles du système, les duplique dans notre
/// processus et lit leur type et leur nom. Les canaux nommés ne sont pas interrogés (NtQueryObject
/// peut s'y bloquer indéfiniment).
/// </summary>
public static unsafe class HandleSearch
{
    private static readonly HashSet<string> SkippedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Process", "Thread", "EtwRegistration", "EtwConsumer", "TpWorkerFactory", "IoCompletion", "WaitCompletionPacket",
        "IRTimer", "Timer", "TmTm", "TmRm", "TmEn", "TmTx", "PcwObject", "DxgkSharedResource", "DxgkSharedSyncObject",
    };

    /// <param name="pids">Processus à examiner ; null = tous.</param>
    /// <param name="filter">Texte recherché dans le nom (insensible à la casse) ; vide = tous les handles nommés.</param>
    public static List<HandleSample> Search(IReadOnlySet<int>? pids, string filter, Func<int, string> imageOf, int maxResults, CancellationToken token)
    {
        var results = new List<HandleSample>();
        byte[] buffer = QueryHandles();
        if (buffer.Length == 0) return results;

        var typeNames = new Dictionary<ushort, string>();
        var processes = new Dictionary<int, nint>();
        nint self = Win32.GetCurrentProcess();
        char* path = stackalloc char[1024];
        byte* info = stackalloc byte[4096];
        try
        {
            fixed (byte* basePtr = buffer)
            {
                long count = (long)*(nuint*)basePtr;
                var entries = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX*)(basePtr + 2 * sizeof(nuint));
                for (long i = 0; i < count && results.Count < maxResults; i++)
                {
                    if ((i & 1023) == 0 && token.IsCancellationRequested) break;
                    ref var entry = ref entries[i];
                    int pid = (int)entry.UniqueProcessId;
                    if (pid <= 4 || (pids is not null && !pids.Contains(pid))) continue;

                    if (!processes.TryGetValue(pid, out nint process))
                    {
                        process = Win32.OpenProcess(Win32.PROCESS_DUP_HANDLE, false, (uint)pid);
                        processes[pid] = process;
                    }
                    if (process == 0) continue;

                    nint duplicate;
                    if (!Win32.DuplicateHandle(process, (nint)entry.HandleValue, self, &duplicate, 0, false, 0)) continue;
                    try
                    {
                        if (!typeNames.TryGetValue(entry.ObjectTypeIndex, out var type))
                        {
                            type = QueryUnicode(duplicate, Win32.ObjectTypeInformation, info) ?? "";
                            typeNames[entry.ObjectTypeIndex] = type;
                        }
                        if (type.Length == 0 || SkippedTypes.Contains(type)) continue;

                        string? name;
                        if (type == "File")
                        {
                            // Seuls les fichiers disque sont nommés : canaux et périphériques caractère peuvent bloquer la requête.
                            if (Win32.GetFileType(duplicate) != Win32.FILE_TYPE_DISK) continue;
                            uint length = Win32.GetFinalPathNameByHandle(duplicate, path, 1024, 0);
                            name = length is > 0 and < 1024 ? new string(path, 0, (int)length) : null;
                            if (name is not null && name.StartsWith(@"\\?\", StringComparison.Ordinal)) name = name[4..];
                        }
                        else
                        {
                            name = QueryUnicode(duplicate, Win32.ObjectNameInformation, info);
                        }
                        if (string.IsNullOrEmpty(name)) continue;
                        name = FriendlyName(type, name);
                        if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                        results.Add(new HandleSample(pid, imageOf(pid), type, name));
                    }
                    finally
                    {
                        Win32.CloseHandle(duplicate);
                    }
                }
            }
        }
        finally
        {
            foreach (var process in processes.Values)
                if (process != 0) Win32.CloseHandle(process);
        }
        return results;
    }

    private static byte[] QueryHandles()
    {
        int size = 4 * 1024 * 1024;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var buffer = new byte[size];
            uint returned;
            int status;
            fixed (byte* p = buffer)
                status = Win32.NtQuerySystemInformation(Win32.SystemExtendedHandleInformation, p, (uint)size, &returned);
            if (status >= 0) return buffer;
            if (status != Win32.STATUS_INFO_LENGTH_MISMATCH) return [];
            size = Math.Max(size * 2, (int)returned + 1024 * 1024);
        }
        return [];
    }

    /// <summary>OBJECT_NAME_INFORMATION et OBJECT_TYPE_INFORMATION commencent tous deux par un UNICODE_STRING.</summary>
    private static string? QueryUnicode(nint handle, int infoClass, byte* buffer)
    {
        uint returned;
        if (Win32.NtQueryObject(handle, infoClass, buffer, 4096, &returned) < 0) return null;
        var text = (UNICODE_STRING*)buffer;
        return text->Buffer == 0 || text->Length == 0 ? null : new string((char*)text->Buffer, 0, text->Length / 2);
    }

    private static string FriendlyName(string type, string name)
    {
        if (type == "Key")
        {
            if (name.StartsWith(@"\REGISTRY\MACHINE", StringComparison.OrdinalIgnoreCase)) return "HKLM" + name[17..];
            if (name.StartsWith(@"\REGISTRY\USER", StringComparison.OrdinalIgnoreCase)) return "HKU" + name[14..];
        }
        return name.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) ? DevicePaths.ToDosPath(name) : name;
    }
}

/// <summary>Analyse de la chaîne d'attente d'un processus (Wait Chain Traversal), comme resmon.</summary>
public static unsafe class WaitChain
{
    private static readonly string[] ObjectTypes =
    [
        "", "section critique", "SendMessage", "mutex", "ALPC", "COM", "attente de thread", "attente de processus",
        "thread", "activation COM", "inconnu", "E/S de socket", "E/S SMB",
    ];

    public sealed record Result(int ThreadsAnalyzed, List<string> Lines, bool Deadlock, List<(int Pid, string Image)> BlockingProcesses);

    public static Result Analyze(int pid, Func<int, string> imageOf)
    {
        var lines = new List<string>();
        var blocking = new Dictionary<int, string>();
        bool deadlock = false;
        int analyzed = 0;
        nint session = Win32.OpenThreadWaitChainSession(0, 0);
        if (session == 0) return new Result(0, [$"Impossible d'ouvrir une session d'analyse (erreur {Marshal.GetLastPInvokeError()})."], false, []);
        try
        {
            var nodes = stackalloc WAITCHAIN_NODE_INFO[Win32.WCT_MAX_NODE_COUNT];
            foreach (uint tid in ThreadsOf(pid))
            {
                analyzed++;
                uint count = Win32.WCT_MAX_NODE_COUNT;
                int cycle;
                if (!Win32.GetThreadWaitChain(session, 0, Win32.WCT_OUT_OF_PROC_ALL, tid, &count, nodes, &cycle)) continue;
                deadlock |= cycle != 0;
                // Une chaîne alterne thread → objet attendu → thread propriétaire → …
                for (int i = 1; i < count; i++)
                {
                    if (nodes[i].ObjectType != Win32.WctThreadType) continue;
                    int ownerPid = (int)nodes[i].ProcessId;
                    if (ownerPid == pid || ownerPid == 0) continue;
                    string objectType = i > 0 && nodes[i - 1].ObjectType is > 0 and < 13 ? ObjectTypes[nodes[i - 1].ObjectType] : "objet";
                    string image = imageOf(ownerPid);
                    blocking[ownerPid] = image;
                    lines.Add($"Le thread {tid} attend ({objectType}) le thread {nodes[i].ThreadId} de {image} (PID {ownerPid}).");
                    break;
                }
            }
        }
        finally
        {
            Win32.CloseThreadWaitChainSession(session);
        }
        return new Result(analyzed, lines, deadlock, blocking.Select(b => (b.Key, b.Value)).ToList());
    }

    private static List<uint> ThreadsOf(int pid)
    {
        var threads = new List<uint>();
        nint snapshot = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPTHREAD, 0);
        if (snapshot == Win32.INVALID_HANDLE_VALUE) return threads;
        try
        {
            THREADENTRY32 entry = default;
            entry.dwSize = (uint)sizeof(THREADENTRY32);
            if (!Win32.Thread32First(snapshot, &entry)) return threads;
            do
            {
                if (entry.th32OwnerProcessID == (uint)pid) threads.Add(entry.th32ThreadID);
            }
            while (Win32.Thread32Next(snapshot, &entry));
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }
        return threads;
    }
}

/// <summary>Active SeDebugPrivilege dans NOTRE processus (uniquement s'il est déjà administrateur), comme resmon / Process Explorer.</summary>
public static unsafe class Privileges
{
    public static bool EnableDebugPrivilege()
    {
        nint token;
        if (!Win32.OpenProcessToken(Win32.GetCurrentProcess(), Win32.TOKEN_ADJUST_PRIVILEGES | Win32.TOKEN_QUERY, &token)) return false;
        try
        {
            long luid;
            if (!Win32.LookupPrivilegeValue(null, "SeDebugPrivilege", &luid)) return false;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = Win32.SE_PRIVILEGE_ENABLED };
            return Win32.AdjustTokenPrivileges(token, false, &privileges, 0, 0, 0) && Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            Win32.CloseHandle(token);
        }
    }
}

using System.Runtime.InteropServices;
using ResMonNext.Native;

namespace ResMonNext.Monitoring;

public sealed record ModuleSample(int Pid, string Image, string Name, string Version, string Path);

/// <summary>Liste les DLL chargées par un processus (onglet Processeur › Modules associés).</summary>
public static unsafe class ModuleReader
{
    public static List<ModuleSample> Read(int pid, string image)
    {
        var result = new List<ModuleSample>();
        nint snapshot = Win32.INVALID_HANDLE_VALUE;
        for (int attempt = 0; attempt < 3 && snapshot == Win32.INVALID_HANDLE_VALUE; attempt++)
        {
            snapshot = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPMODULE | Win32.TH32CS_SNAPMODULE32, (uint)pid);
            // ERROR_BAD_LENGTH (24) : le processus charge des modules pendant la capture, on réessaie.
            if (snapshot == Win32.INVALID_HANDLE_VALUE && Marshal.GetLastPInvokeError() != 24) break;
        }
        if (snapshot == Win32.INVALID_HANDLE_VALUE) return result;

        try
        {
            MODULEENTRY32W entry = default;
            entry.dwSize = (uint)sizeof(MODULEENTRY32W);
            if (!Win32.Module32FirstW(snapshot, &entry)) return result;
            do
            {
                string path = new(entry.szExePath);
                result.Add(new ModuleSample(pid, image, new string(entry.szModule), FileInfoCache.Get(path).Version, path));
            }
            while (Win32.Module32NextW(snapshot, &entry));
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }
        return result;
    }
}

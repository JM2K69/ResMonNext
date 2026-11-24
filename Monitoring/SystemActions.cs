using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResMonNext.Native;

namespace ResMonNext.Monitoring;

/// <summary>Actions du menu contextuel. Chaque méthode renvoie null en cas de succès, sinon un message d'erreur.</summary>
public static unsafe class SystemActions
{
    public static bool IsElevated => Environment.IsPrivilegedProcess;

    public static string? Terminate(int pid)
    {
        nint handle = Win32.OpenProcess(Win32.PROCESS_TERMINATE, false, (uint)pid);
        if (handle == 0) return LastError();
        try
        {
            return Win32.TerminateProcess(handle, 1) ? null : LastError();
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    public static string? Suspend(int pid) => SuspendResume(pid, suspend: true);

    public static string? Resume(int pid) => SuspendResume(pid, suspend: false);

    private static string? SuspendResume(int pid, bool suspend)
    {
        nint handle = Win32.OpenProcess(Win32.PROCESS_SUSPEND_RESUME, false, (uint)pid);
        if (handle == 0) return LastError();
        try
        {
            int status = suspend ? Win32.NtSuspendProcess(handle) : Win32.NtResumeProcess(handle);
            return status >= 0 ? null : $"Échec (NTSTATUS 0x{status:X8}).";
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    public static string? StartService(string name) => WithService(name, Win32.SERVICE_START, h =>
        Win32.StartService(h, 0, 0) ? null : LastError());

    public static string? StopService(string name) => WithService(name, Win32.SERVICE_STOP | Win32.SERVICE_QUERY_STATUS, h =>
    {
        SERVICE_STATUS status;
        return Win32.ControlService(h, Win32.SERVICE_CONTROL_STOP, &status) ? null : LastError();
    });

    /// <summary>Arrête puis redémarre un service (bloquant : à appeler hors du thread d'interface).</summary>
    public static string? RestartService(string name) =>
        WithService(name, Win32.SERVICE_START | Win32.SERVICE_STOP | Win32.SERVICE_QUERY_STATUS, h =>
        {
            SERVICE_STATUS status;
            if (!Win32.ControlService(h, Win32.SERVICE_CONTROL_STOP, &status) && Marshal.GetLastPInvokeError() != 1062 /* déjà arrêté */)
                return LastError();
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (Win32.QueryServiceStatus(h, &status) && status.dwCurrentState == Win32.SERVICE_STOPPED) break;
                Thread.Sleep(250);
            }
            return Win32.StartService(h, 0, 0) ? null : LastError();
        });

    private static string? WithService(string name, uint access, Func<nint, string?> action)
    {
        nint scm = Win32.OpenSCManager(null, null, Win32.SC_MANAGER_CONNECT);
        if (scm == 0) return LastError();
        try
        {
            nint service = Win32.OpenService(scm, name, access);
            if (service == 0) return LastError();
            try
            {
                return action(service);
            }
            finally
            {
                Win32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Win32.CloseServiceHandle(scm);
        }
    }

    public static void SearchOnline(string term) => OpenUrl("https://www.bing.com/search?q=" + Uri.EscapeDataString(term));

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
    }

    /// <summary>Relance l'application avec élévation UAC. Renvoie false si l'utilisateur a refusé.</summary>
    public static bool RestartElevated()
    {
        try
        {
            // Conserve le choix du mode de rendu (--gpu) dans l'instance élevée.
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = Program.GpuRendering ? "--gpu" : "",
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string LastError()
    {
        int error = Marshal.GetLastPInvokeError();
        string hint = error == 5 && !IsElevated ? " Relancez ResMon Next en administrateur." : "";
        return new Win32Exception(error).Message + hint;
    }
}

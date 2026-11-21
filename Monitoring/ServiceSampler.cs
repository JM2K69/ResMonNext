using System.Runtime.InteropServices;
using ResMonNext.Native;

namespace ResMonNext.Monitoring;

/// <summary>Énumère les services Win32 avec leur PID hôte et leur groupe svchost (-k).</summary>
internal sealed unsafe class ServiceSampler
{
    private readonly Dictionary<string, string> _groups = new(StringComparer.OrdinalIgnoreCase);
    private byte[] _buffer = new byte[256 * 1024];

    public ServiceSample[] Enumerate()
    {
        nint scm = Win32.OpenSCManager(null, null, Win32.SC_MANAGER_CONNECT | Win32.SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == 0) return [];
        try
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint needed = 0, returned = 0, resume = 0;
                fixed (byte* p = _buffer)
                {
                    if (Win32.EnumServicesStatusEx(scm, Win32.SC_ENUM_PROCESS_INFO, Win32.SERVICE_WIN32, Win32.SERVICE_STATE_ALL,
                            p, (uint)_buffer.Length, &needed, &returned, &resume, null))
                    {
                        return Parse(scm, (ENUM_SERVICE_STATUS_PROCESSW*)p, (int)returned);
                    }
                }
                if (Marshal.GetLastPInvokeError() != Win32.ERROR_MORE_DATA) return [];
                _buffer = new byte[_buffer.Length + needed + 16 * 1024];
            }
            return [];
        }
        finally
        {
            Win32.CloseServiceHandle(scm);
        }
    }

    private ServiceSample[] Parse(nint scm, ENUM_SERVICE_STATUS_PROCESSW* entries, int count)
    {
        var result = new ServiceSample[count];
        for (int i = 0; i < count; i++)
        {
            string name = new((char*)entries[i].lpServiceName);
            if (!_groups.TryGetValue(name, out var group))
            {
                group = QueryGroup(scm, name);
                _groups[name] = group;
            }
            result[i] = new ServiceSample
            {
                Name = name,
                DisplayName = new string((char*)entries[i].lpDisplayName),
                Pid = (int)entries[i].ServiceStatusProcess.dwProcessId,
                State = entries[i].ServiceStatusProcess.dwCurrentState,
                Group = group,
            };
        }
        return result;
    }

    /// <summary>Extrait le groupe svchost (« -k netsvcs ») de la ligne de commande du service.</summary>
    private static string QueryGroup(nint scm, string name)
    {
        nint service = Win32.OpenService(scm, name, Win32.SERVICE_QUERY_CONFIG);
        if (service == 0) return "";
        try
        {
            uint needed = 0;
            Win32.QueryServiceConfig(service, null, 0, &needed);
            if (needed == 0) return "";
            var buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                if (!Win32.QueryServiceConfig(service, p, needed, &needed)) return "";
                var config = (QUERY_SERVICE_CONFIGW*)p;
                if (config->lpBinaryPathName == 0) return "";
                string path = new((char*)config->lpBinaryPathName);
                int k = path.IndexOf(" -k ", StringComparison.OrdinalIgnoreCase);
                if (k < 0) return "";
                string rest = path[(k + 4)..].TrimStart();
                int end = rest.IndexOf(' ');
                return end < 0 ? rest : rest[..end];
            }
        }
        finally
        {
            Win32.CloseServiceHandle(service);
        }
    }
}

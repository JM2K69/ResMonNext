using System.Collections.Concurrent;
using System.Diagnostics;

namespace ResMonNext.Monitoring;

/// <summary>Cache des métadonnées de fichiers exécutables (description, version), partagé entre threads.</summary>
internal static class FileInfoCache
{
    private static readonly ConcurrentDictionary<string, (string Description, string Version)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static (string Description, string Version) Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return ("", "");
        return Cache.GetOrAdd(path, static p =>
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(p);
                return (info.FileDescription?.Trim() ?? "", info.FileVersion?.Trim() ?? "");
            }
            catch
            {
                return ("", "");
            }
        });
    }
}

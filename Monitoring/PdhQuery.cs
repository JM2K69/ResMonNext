using ResMonNext.Native;

namespace ResMonNext.Monitoring;

/// <summary>
/// Requête de compteurs de performance (PDH). Les chemins sont en anglais
/// (PdhAddEnglishCounter) pour fonctionner sur un Windows en français.
/// </summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    private nint _query;

    public PdhQuery()
    {
        nint q;
        if (Win32.PdhOpenQuery(0, 0, &q) == 0) _query = q;
    }

    public PdhCounter? Add(string englishPath)
    {
        if (_query == 0) return null;
        nint counter;
        return Win32.PdhAddEnglishCounter(_query, englishPath, 0, &counter) == 0 ? new PdhCounter(counter) : null;
    }

    public void Collect()
    {
        if (_query != 0) Win32.PdhCollectQueryData(_query);
    }

    public void Dispose()
    {
        if (_query != 0) Win32.PdhCloseQuery(_query);
        _query = 0;
    }
}

internal sealed unsafe class PdhCounter(nint handle)
{
    private byte[] _arrayBuffer = new byte[4096];

    /// <summary>Valeur d'un compteur simple ; NaN si indisponible (ex. premier échantillon d'un taux).</summary>
    public double Read()
    {
        PDH_FMT_COUNTERVALUE value;
        int status = Win32.PdhGetFormattedCounterValue(handle, Win32.PDH_FMT_DOUBLE | Win32.PDH_FMT_NOCAP100, null, &value);
        return status == 0 && value.CStatus <= 1 ? value.DoubleValue : double.NaN;
    }

    /// <summary>Somme de toutes les instances d'un compteur avec joker « (*) ».</summary>
    public double ReadSum()
    {
        var items = ReadArray();
        return items.Count == 0 ? double.NaN : items.Where(i => i.Name != "_Total").Sum(i => i.Value);
    }

    /// <summary>Valeur de chaque instance d'un compteur avec joker « (*) ».</summary>
    public List<(string Name, double Value)> ReadArray()
    {
        var result = new List<(string, double)>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = (uint)_arrayBuffer.Length, count = 0;
            int status;
            fixed (byte* buffer = _arrayBuffer)
            {
                status = Win32.PdhGetFormattedCounterArray(handle, Win32.PDH_FMT_DOUBLE | Win32.PDH_FMT_NOCAP100, &size, &count, buffer);
                if (status == 0)
                {
                    var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)buffer;
                    for (int i = 0; i < count; i++)
                    {
                        if (items[i].FmtValue.CStatus > 1) continue;
                        result.Add((new string((char*)items[i].szName), items[i].FmtValue.DoubleValue));
                    }
                    return result;
                }
            }
            if (status != Win32.PDH_MORE_DATA) return result;
            _arrayBuffer = new byte[size + 1024];
        }
        return result;
    }
}

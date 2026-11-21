using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ResMonNext.ViewModels;

public static class Palette
{
    public static readonly Color Cpu = Color.Parse("#16A34A");
    public static readonly Color Frequency = Color.Parse("#2563EB");
    public static readonly Color Filtered = Color.Parse("#F59E0B");
    public static readonly Color Disk = Color.Parse("#0EA5E9");
    public static readonly Color DiskActive = Color.Parse("#2563EB");
    public static readonly Color Network = Color.Parse("#F97316");
    public static readonly Color Memory = Color.Parse("#8B5CF6");
    public static readonly Color Commit = Color.Parse("#6366F1");
    public static readonly Color HardFaults = Color.Parse("#16A34A");

    public static readonly Color HardwareReserved = Color.Parse("#9CA3AF");
    public static readonly Color InUse = Color.Parse("#22C55E");
    public static readonly Color Modified = Color.Parse("#F59E0B");
    public static readonly Color Standby = Color.Parse("#3B82F6");
    public static readonly Color Free = Color.Parse("#93C5FD");
}

/// <summary>Série circulaire de 60 points (une minute à 1 s d'intervalle).</summary>
public sealed class GraphSeries
{
    private readonly double[] _values;
    private int _head, _count;

    public GraphSeries(Color color, bool fill, int capacity = 60)
    {
        _values = new double[capacity];
        Fill = fill;
        Stroke = new ImmutablePen(new ImmutableSolidColorBrush(color), 1.5);
        FillBrush = new ImmutableSolidColorBrush(color, 0.18);
    }

    public bool Fill { get; }
    public IPen Stroke { get; }
    public IBrush FillBrush { get; }
    public int Capacity => _values.Length;
    public int Count => _count;

    /// <summary>i = 0 : point le plus ancien.</summary>
    public double this[int i] => _values[(_head - _count + i + _values.Length) % _values.Length];

    public void Push(double value)
    {
        _values[_head] = double.IsNaN(value) ? 0 : value;
        _head = (_head + 1) % _values.Length;
        if (_count < _values.Length) _count++;
    }

    public double Max()
    {
        double max = 0;
        for (int i = 0; i < _count; i++) max = Math.Max(max, this[i]);
        return max;
    }
}

public sealed class GraphModel(double? fixedMax, double minScale, params GraphSeries[] series)
{
    public IReadOnlyList<GraphSeries> Series { get; } = series;

    /// <summary>Formateur de l'échelle affichée en haut à gauche du graphe.</summary>
    public Func<double, string> ScaleFormatter { get; init; } = v => v.ToString("0") + " %";

    public event Action? Changed;

    public void Push(params double[] values)
    {
        for (int i = 0; i < Series.Count && i < values.Length; i++) Series[i].Push(values[i]);
        Changed?.Invoke();
    }

    public double Scale()
    {
        if (fixedMax is { } max) return max;
        double peak = Series.Count == 0 ? 0 : Series.Max(s => s.Max());
        return NiceCeiling(Math.Max(minScale, peak * 1.15));
    }

    private static double NiceCeiling(double value)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double step in new[] { 1, 2, 2.5, 5, 10 })
            if (step * magnitude >= value) return step * magnitude;
        return 10 * magnitude;
    }
}

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ResMonNext.ViewModels;

public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Mise en forme française des grandeurs (octets, débits, pourcentages).</summary>
public static class Fmt
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly string[] ByteUnits = ["o", "Ko", "Mo", "Go", "To"];

    public static string Percent(double value) => double.IsNaN(value) ? "—" : value.ToString("0", Fr) + " %";

    public static string Bytes(double bytes)
    {
        if (double.IsNaN(bytes)) return "—";
        int unit = 0;
        while (bytes >= 1024 && unit < ByteUnits.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return bytes.ToString(unit == 0 || bytes >= 100 ? "0" : "0.0", Fr) + " " + ByteUnits[unit];
    }

    public static string BytesPerSec(double bytes) => double.IsNaN(bytes) ? "—" : Bytes(bytes) + "/s";

    public static string BitsPerSec(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec)) return "—";
        double bits = bytesPerSec * 8;
        string[] units = ["bit/s", "Kbit/s", "Mbit/s", "Gbit/s"];
        int unit = 0;
        while (bits >= 1000 && unit < units.Length - 1)
        {
            bits /= 1000;
            unit++;
        }
        return bits.ToString(unit == 0 || bits >= 100 ? "0" : "0.0", Fr) + " " + units[unit];
    }

    public static string Count(double value) => double.IsNaN(value) ? "—" : value.ToString("N0", Fr);

    public static string Mb(ulong bytes) => (bytes / (1024 * 1024)).ToString("N0", Fr) + " Mo";
}

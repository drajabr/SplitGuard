using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SplitGuard.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class RelayCommand : ICommand
{
    readonly Action<object?> _execute;
    readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

// Sentinel item rendered as the inline "+" add box at the end of a chip list,
// so it wraps together with the chips instead of overflowing.
public sealed class AddSlot { }

// Sentinel item rendered as the DNS input at the head of the domains chip list, so
// domain chips flow beside the DNS box and wrap naturally when the line fills.
public sealed class DnsSlot { }

public static class Format
{
    public static string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1 << 20 => $"{bytesPerSecond / (1 << 20):0.#} MB/s",
        >= 1 << 10 => $"{bytesPerSecond / (1 << 10):0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };

    // Cumulative transferred bytes (per-peer Tx/Rx totals).
    public static string Bytes(ulong b) => b switch
    {
        >= 1UL << 30 => $"{b / (double)(1UL << 30):0.##} GB",
        >= 1UL << 20 => $"{b / (double)(1UL << 20):0.#} MB",
        >= 1UL << 10 => $"{b / (double)(1UL << 10):0} KB",
        _ => $"{b} B",
    };

    // Compact connection-uptime: "45s", "14m", "2h 05m", "3d 4h".
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }

    public static string Ago(DateTime? utc)
    {
        if (utc is null) return "no handshake yet";
        var span = DateTime.UtcNow - utc.Value;
        if (span.TotalSeconds < 90) return $"handshake {Math.Max(0, (int)span.TotalSeconds)}s ago";
        if (span.TotalMinutes < 90) return $"handshake {(int)span.TotalMinutes}m ago";
        return $"handshake {(int)span.TotalHours}h ago";
    }
}

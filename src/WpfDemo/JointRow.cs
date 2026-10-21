using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AxisLab.WpfDemo;

public sealed class JointRow : INotifyPropertyChanged
{
    private double _target;
    private double _current;
    private bool _isEditable = true;

    public required string Name { get; init; }
    public required double Minimum { get; init; }
    public required double Maximum { get; init; }
    public required double MaxVelocity { get; init; }

    public double Target
    {
        get => _target;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(_target - clamped) < 0.0001) return;
            _target = clamped;
            OnPropertyChanged();
        }
    }

    public double Current
    {
        get => _current;
        set
        {
            if (Math.Abs(_current - value) < 0.0001) return;
            _current = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentText));
        }
    }

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (_isEditable == value) return;
            _isEditable = value;
            OnPropertyChanged();
        }
    }

    public string CurrentText => $"{Current:F2}°";
    public string LimitText => $"{Minimum:F0}° — {Maximum:F0}° · {MaxVelocity:F0}°/s";
    public string TargetText
    {
        get => Target.ToString("F2");
        set
        {
            if (double.TryParse(value, out var v)) Target = v;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


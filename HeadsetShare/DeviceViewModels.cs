using System.ComponentModel;
using System.Runtime.CompilerServices;
using HeadsetShare.Audio;

namespace HeadsetShare;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnChanged(name);
        return true;
    }

    protected void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Ein Gerät, wie es in der Liste links, in der Quelle und beim Ziehen erscheint.</summary>
public class DeviceItem : ObservableObject
{
    private string _name;

    public DeviceItem(string id, string name, DeviceKind kind)
    {
        Id = id;
        _name = name;
        Kind = kind;
    }

    public string Id { get; }

    public DeviceKind Kind { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                NameChanged();
            }
        }
    }

    public string Glyph => Kind switch
    {
        DeviceKind.Headset => "",
        DeviceKind.Monitor => "",
        _ => "",
    };

    public string KindLabel => Kind switch
    {
        DeviceKind.Headset => "Headset",
        DeviceKind.Monitor => "Monitor",
        _ => "Lautsprecher",
    };

    protected virtual void NameChanged()
    {
    }
}

/// <summary>Ein Gerät im Hörkreis, mit Reglern und Live-Werten.</summary>
public sealed class TargetCard : DeviceItem
{
    private readonly Action _settingsChanged;
    private double _volumePercent;
    private double _delayMs;
    private bool _muted;
    private double _level;
    private string _status = "Verbinde …";
    private string _statusDetail = string.Empty;
    private TargetState _state = TargetState.Connecting;
    private double _x;
    private double _y;
    private double _scale = 0.86;
    private double _opacity;

    public TargetCard(SavedTarget saved, Action settingsChanged)
        : base(saved.Id, saved.Name, (DeviceKind)saved.Kind)
    {
        _settingsChanged = settingsChanged;
        Settings = new TargetSettings(saved.Id)
        {
            Volume = (float)saved.Volume,
            DelayMs = saved.DelayMs,
            Muted = saved.Muted,
        };
        _volumePercent = saved.Volume * 100;
        _delayMs = saved.DelayMs;
        _muted = saved.Muted;
    }

    public TargetSettings Settings { get; }

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (Set(ref _volumePercent, value))
            {
                Settings.Volume = (float)(value / 100);
                OnChanged(nameof(VolumeLabel));
                _settingsChanged();
            }
        }
    }

    public string VolumeLabel => $"{Math.Round(_volumePercent)} %";

    public double DelayMs
    {
        get => _delayMs;
        set
        {
            if (Set(ref _delayMs, value))
            {
                Settings.DelayMs = (int)Math.Round(value);
                OnChanged(nameof(DelayLabel));
                _settingsChanged();
            }
        }
    }

    public string DelayLabel => $"{Math.Round(_delayMs)} ms";

    public bool Muted
    {
        get => _muted;
        set
        {
            if (Set(ref _muted, value))
            {
                Settings.Muted = value;
                OnChanged(nameof(MuteGlyph));
                _settingsChanged();
            }
        }
    }

    public string MuteGlyph => _muted ? "" : "";

    public double Level
    {
        get => _level;
        set => Set(ref _level, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set => Set(ref _statusDetail, value);
    }

    public TargetState State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    public double X
    {
        get => _x;
        set => Set(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => Set(ref _y, value);
    }

    public double Scale
    {
        get => _scale;
        set => Set(ref _scale, value);
    }

    public double CardOpacity
    {
        get => _opacity;
        set => Set(ref _opacity, value);
    }

    public double TargetX { get; set; }

    public double TargetY { get; set; }

    public double TargetScale { get; set; } = 1;

    public bool HasPosition { get; set; }

    public SavedTarget ToSaved() => new()
    {
        Id = Id,
        Name = Name,
        Kind = (int)Kind,
        Volume = _volumePercent / 100,
        DelayMs = (int)Math.Round(_delayMs),
        Muted = _muted,
    };

    protected override void NameChanged() => _settingsChanged();
}

public enum TargetState
{
    Connecting,
    Synced,
    Warning,
    Offline,
}

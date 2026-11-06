using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HeadsetShare.Audio;

/// <summary>Vom UI änderbare Einstellungen eines Ausgabegeräts. Die Audio-Threads lesen sie laufend.</summary>
public sealed class TargetSettings(string deviceId)
{
    private float _volume = 1f;
    private int _delayMs;
    private bool _muted;

    public string DeviceId { get; } = deviceId;

    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, value);
    }

    public int DelayMs
    {
        get => Volatile.Read(ref _delayMs);
        set => Volatile.Write(ref _delayMs, value);
    }

    public bool Muted
    {
        get => Volatile.Read(ref _muted);
        set => Volatile.Write(ref _muted, value);
    }
}

/// <summary>Ein Ausgabegerät mit eigenem Puffer, eigener Drift-Korrektur, Delay und Lautstärke.</summary>
public sealed class HeadsetOutput : IWaveProvider, IDisposable
{
    /// <summary>Grundreserve im Puffer. Sie fängt unregelmäßig eintreffende Pakete ab.</summary>
    public const int BaseLatencyMs = 60;

    private const int MaxDelaySeconds = 2;

    private readonly TargetSettings _settings;
    private readonly AudioRing _ring;
    private readonly DriftResampler _resampler;
    private readonly WasapiPlayer _player;
    private float _appliedVolume;
    private float _peak;

    public HeadsetOutput(MMDevice device, TargetSettings settings, int sourceRate)
    {
        _settings = settings;
        DeviceId = device.ID;

        int deviceRate;
        using (var client = device.CreateAudioClient())
        {
            deviceRate = client.MixFormat.SampleRate;
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(deviceRate, 2);
        _ring = new AudioRing(sourceRate * (MaxDelaySeconds + 2));
        _resampler = new DriftResampler(_ring, sourceRate, deviceRate, LatencySeconds());

        _player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithLatency(50)
            .WithMmcssThreadPriority("Audio")
            .Build();
        _player.Init(this);
        _player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                Failed?.Invoke(this, e.Exception);
            }
        };
    }

    public event Action<HeadsetOutput, Exception>? Failed;

    public string DeviceId { get; }

    public WaveFormat WaveFormat { get; }

    public DriftResampler Resampler => _resampler;

    public void Start() => _player.Play();

    public void Write(ReadOnlySpan<float> stereo) => _ring.Write(stereo);

    public float TakePeak() => Interlocked.Exchange(ref _peak, 0);

    public int Read(Span<byte> buffer)
    {
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        _resampler.TargetLatencySeconds = LatencySeconds();
        _resampler.Read(samples);

        // Lautstärke über den Block hinweg rampen, damit der Regler nicht knackt.
        var target = _settings.Muted ? 0f : _settings.Volume;
        var volume = _appliedVolume;
        var increment = samples.Length > 0 ? (target - volume) / samples.Length : 0;
        var peak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            volume += increment;
            var value = samples[i] * volume;
            samples[i] = value;
            peak = Math.Max(peak, Math.Abs(value));
        }

        _appliedVolume = target;
        if (peak > Volatile.Read(ref _peak))
        {
            Volatile.Write(ref _peak, peak);
        }

        return buffer.Length;
    }

    public void Dispose()
    {
        try
        {
            _player.Stop();
        }
        catch (Exception)
        {
            // Beim Abstecken ist das Gerät schon weg, Dispose räumt trotzdem auf.
        }

        _player.Dispose();
    }

    private double LatencySeconds()
        => (BaseLatencyMs + Math.Clamp(_settings.DelayMs, 0, MaxDelaySeconds * 1000)) / 1000.0;
}

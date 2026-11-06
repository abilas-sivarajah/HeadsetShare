using System.Collections.Concurrent;
using NAudio.CoreAudioApi;

namespace HeadsetShare.Audio;

/// <summary>Soll-Zustand, den das UI vorgibt. Der Router gleicht die laufenden Streams damit ab.</summary>
public sealed record RouterPlan(string? SourceId, bool Paused, bool MuteSource, IReadOnlyList<TargetSettings> Targets);

public readonly record struct TargetStatus(bool Active, string? Error, double DriftPpm, long Underruns);

/// <summary>
/// Verteilt den Ton der Quelle an alle Ziele. Geräte werden auf einem eigenen Thread geöffnet und
/// geschlossen, weil das dauern kann und die Oberfläche nicht blockieren darf.
/// </summary>
public sealed class AudioRouter : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private readonly ConcurrentDictionary<string, string> _targetErrors = new();

    private volatile HeadsetOutput[] _outputs = [];
    private volatile LoopbackCapture? _capture;
    private volatile string? _sourceError;
    private volatile RouterPlan _plan = new(null, false, false, []);

    private string? _mutedDeviceId;
    private bool _mutedDeviceWasMuted;

    public AudioRouter()
    {
        _worker = new Thread(WorkLoop) { IsBackground = true, Name = "AudioRouter" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public string? SourceError => _sourceError;

    public bool IsCapturing => _capture is not null;

    public void Apply(RouterPlan plan)
    {
        _plan = plan;
        _work.Add(Reconcile);
    }

    public float TakeSourcePeak() => _capture?.TakePeak() ?? 0f;

    public float TakeTargetPeak(string deviceId)
        => Array.Find(_outputs, output => output.DeviceId == deviceId)?.TakePeak() ?? 0f;

    public TargetStatus GetStatus(string deviceId)
    {
        var output = Array.Find(_outputs, candidate => candidate.DeviceId == deviceId);
        if (output is not null)
        {
            return new TargetStatus(true, null, output.Resampler.CorrectionPpm, output.Resampler.Underruns);
        }

        _targetErrors.TryGetValue(deviceId, out var error);
        return new TargetStatus(false, error, 0d, 0);
    }

    public void Dispose()
    {
        _work.Add(() =>
        {
            StopAll();
            ApplySourceMute(null);
        });
        _work.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(3));
    }

    private void WorkLoop()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                _sourceError = exception.Message;
            }
        }
    }

    private void Reconcile()
    {
        var plan = _plan;
        var wantedSource = plan.Paused ? null : plan.SourceId;

        if (_capture?.DeviceId != wantedSource)
        {
            StopAll();
            StartCapture(wantedSource);
        }
        else if (_capture is null && wantedSource is not null)
        {
            StartCapture(wantedSource);
        }

        ApplySourceMute(plan.MuteSource && _capture is not null ? _capture.DeviceId : null);

        if (_capture is null)
        {
            return;
        }

        var wantedIds = plan.Targets.Select(target => target.DeviceId).ToHashSet();
        var keep = new List<HeadsetOutput>();
        foreach (var output in _outputs)
        {
            if (wantedIds.Contains(output.DeviceId))
            {
                keep.Add(output);
            }
            else
            {
                Publish(_outputs.Where(other => other != output).ToArray());
                output.Dispose();
            }
        }

        foreach (var target in plan.Targets)
        {
            if (keep.All(output => output.DeviceId != target.DeviceId))
            {
                var started = StartOutput(target, _capture.SampleRate);
                if (started is not null)
                {
                    keep.Add(started);
                }
            }
        }

        Publish(keep.ToArray());
        foreach (var id in _targetErrors.Keys.Where(id => !wantedIds.Contains(id)))
        {
            _targetErrors.TryRemove(id, out _);
        }
    }

    private void StartCapture(string? deviceId)
    {
        _sourceError = null;
        if (deviceId is null)
        {
            return;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var capture = new LoopbackCapture(enumerator.GetDevice(deviceId));
            capture.Packet += OnPacket;
            capture.Failed += exception => _work.Add(() =>
            {
                if (_capture == capture)
                {
                    StopAll();
                    _sourceError = exception.Message;
                }
            });
            capture.Start();
            _capture = capture;
        }
        catch (Exception exception)
        {
            _sourceError = exception.Message;
        }
    }

    private HeadsetOutput? StartOutput(TargetSettings target, int sourceRate)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var output = new HeadsetOutput(enumerator.GetDevice(target.DeviceId), target, sourceRate);
            output.Failed += (failed, exception) => _work.Add(() =>
            {
                if (_outputs.Contains(failed))
                {
                    Publish(_outputs.Where(other => other != failed).ToArray());
                    failed.Dispose();
                    _targetErrors[failed.DeviceId] = exception.Message;
                }
            });
            output.Start();
            _targetErrors.TryRemove(target.DeviceId, out _);
            return output;
        }
        catch (Exception exception)
        {
            _targetErrors[target.DeviceId] = exception.Message;
            return null;
        }
    }

    private void StopAll()
    {
        var capture = _capture;
        _capture = null;
        capture?.Dispose();

        var outputs = _outputs;
        Publish([]);
        foreach (var output in outputs)
        {
            output.Dispose();
        }
    }

    private void ApplySourceMute(string? deviceId)
    {
        if (_mutedDeviceId == deviceId)
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        if (_mutedDeviceId is not null)
        {
            try
            {
                using var previous = enumerator.GetDevice(_mutedDeviceId);
                previous.AudioEndpointVolume.Mute = _mutedDeviceWasMuted;
            }
            catch (Exception)
            {
                // Das Gerät ist inzwischen abgesteckt, es gibt nichts zurückzustellen.
            }

            _mutedDeviceId = null;
        }

        if (deviceId is not null)
        {
            using var device = enumerator.GetDevice(deviceId);
            _mutedDeviceWasMuted = device.AudioEndpointVolume.Mute;
            device.AudioEndpointVolume.Mute = true;
            _mutedDeviceId = deviceId;
        }
    }

    private void Publish(HeadsetOutput[] outputs) => _outputs = outputs;

    private void OnPacket(ReadOnlySpan<float> stereo)
    {
        foreach (var output in _outputs)
        {
            output.Write(stereo);
        }
    }
}

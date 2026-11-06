namespace HeadsetShare.Audio;

/// <summary>
/// Liest aus dem Ringpuffer und hält dabei den Füllstand auf der Zielverzögerung.
/// Jede Soundkarte hat eine eigene Clock. Ein Regler passt deshalb die Abspielgeschwindigkeit
/// minimal an (höchstens 0,1 %, nicht hörbar), damit die Geräte nicht auseinanderlaufen.
/// </summary>
public sealed class DriftResampler
{
    private const double ProportionalGain = 0.05;
    private const double IntegralGain = 0.001;
    private const double MaxCorrection = 0.001;
    private const double SmoothingSeconds = 0.5;
    private const double JumpThresholdSeconds = 0.08;
    private const double FadeSeconds = 0.005;

    private readonly AudioRing _ring;
    private readonly int _inputRate;
    private readonly double _nominalStep;
    private readonly float _fadeStep;

    private long _index;
    private long _refillBase;
    private double _fraction;
    private bool _running;
    private double _smoothedFill;
    private double _integral;
    private double _appliedTarget;
    private bool _jumpPending;
    private float _fade;

    private double _targetLatency;
    private double _correction;
    private long _underruns;

    public DriftResampler(AudioRing ring, int inputRate, int outputRate, double targetLatencySeconds)
    {
        _ring = ring;
        _inputRate = inputRate;
        _nominalStep = (double)inputRate / outputRate;
        _fadeStep = (float)(1.0 / (FadeSeconds * outputRate));
        _targetLatency = targetLatencySeconds;
        _appliedTarget = targetLatencySeconds;
        OutputRate = outputRate;
    }

    public int OutputRate { get; }

    /// <summary>Für Messungen abschaltbar. Dann läuft der Füllstand mit der echten Drift weg.</summary>
    public bool CorrectionEnabled { get; set; } = true;

    public double TargetLatencySeconds
    {
        get => Volatile.Read(ref _targetLatency);
        set => Volatile.Write(ref _targetLatency, value);
    }

    public long Underruns => Interlocked.Read(ref _underruns);

    public double SmoothedFillSeconds => Volatile.Read(ref _smoothedFill);

    /// <summary>Aktuelle Geschwindigkeitskorrektur in Millionsteln. Eingeschwungen entspricht das der Clock-Drift.</summary>
    public double CorrectionPpm => Volatile.Read(ref _correction) * 1e6;

    /// <summary>Füllt <paramref name="stereo"/> vollständig, notfalls mit Stille.</summary>
    public void Read(Span<float> stereo)
    {
        var write = _ring.WriteFrames;
        var target = TargetLatencySeconds;
        var targetFrames = (long)(target * _inputRate);
        var frames = stereo.Length / 2;

        if (!_running)
        {
            // Erst weiter, wenn wieder genug neuer Ton da ist. Sonst liefe das Ende des alten Tons als Schleife.
            if (write - _refillBase < targetFrames)
            {
                stereo.Clear();
                return;
            }

            StartAt(write - targetFrames, target);
        }

        if (Math.Abs(target - _appliedTarget) >= 0.0005)
        {
            // Der Delay-Regler soll sofort wirken, nicht erst über den langsamen Regler.
            _jumpPending = true;
        }

        var step = UpdateController(write, target, frames);

        for (var i = 0; i < frames; i++)
        {
            if (_jumpPending)
            {
                _fade -= _fadeStep;
                if (_fade <= 0)
                {
                    _fade = 0;
                    StartAt(write - targetFrames, target);
                }
            }
            else if (_fade < 1)
            {
                _fade = Math.Min(1, _fade + _fadeStep);
            }

            if (_index + 2 >= write)
            {
                write = _ring.WriteFrames; // Vielleicht ist seit Blockbeginn ein neues Paket angekommen.
            }

            if (_index + 2 >= write)
            {
                // Puffer leer: Stille ausgeben und neu Vorrat aufbauen.
                stereo[(i * 2)..].Clear();
                _running = false;
                _fade = 0;
                _refillBase = write;
                Interlocked.Increment(ref _underruns);
                return;
            }

            var t = (float)_fraction;
            stereo[i * 2] = _fade * Cubic(
                _ring.Left(_index - 1), _ring.Left(_index), _ring.Left(_index + 1), _ring.Left(_index + 2), t);
            stereo[(i * 2) + 1] = _fade * Cubic(
                _ring.Right(_index - 1), _ring.Right(_index), _ring.Right(_index + 1), _ring.Right(_index + 2), t);

            _fraction += step;
            var whole = (long)_fraction;
            _index += whole;
            _fraction -= whole;
        }
    }

    private double UpdateController(long write, double target, int frames)
    {
        var fill = (write - _index - _fraction) / _inputRate;
        var seconds = (double)frames / OutputRate;
        var alpha = 1 - Math.Exp(-seconds / SmoothingSeconds);
        var smoothed = _smoothedFill + (alpha * (fill - _smoothedFill));
        Volatile.Write(ref _smoothedFill, smoothed);

        if (!CorrectionEnabled)
        {
            Volatile.Write(ref _correction, 0d);
            return _nominalStep;
        }

        var error = smoothed - target;
        if (Math.Abs(error) > JumpThresholdSeconds)
        {
            _jumpPending = true;
            return _nominalStep;
        }

        var correction = (ProportionalGain * error) + (IntegralGain * _integral);
        if (Math.Abs(correction) < MaxCorrection)
        {
            _integral += error * seconds; // Am Anschlag nicht weiter aufsummieren.
        }

        correction = Math.Clamp(correction, -MaxCorrection, MaxCorrection);
        Volatile.Write(ref _correction, correction);
        return _nominalStep * (1 + correction);
    }

    private void StartAt(long index, double target)
    {
        _index = index;
        _fraction = 0;
        _running = true;
        _jumpPending = false;
        _appliedTarget = target;
        Volatile.Write(ref _smoothedFill, target);
    }

    private static float Cubic(float y0, float y1, float y2, float y3, float t)
    {
        var a = (-0.5f * y0) + (1.5f * y1) - (1.5f * y2) + (0.5f * y3);
        var b = y0 - (2.5f * y1) + (2f * y2) - (0.5f * y3);
        var c = (-0.5f * y0) + (0.5f * y2);
        return (((((a * t) + b) * t) + c) * t) + y1;
    }
}

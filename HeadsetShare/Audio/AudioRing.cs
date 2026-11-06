using System.Numerics;

namespace HeadsetShare.Audio;

/// <summary>
/// Ringpuffer für Stereo-Float-Frames mit genau einem Schreiber und einem Leser.
/// Der Leser adressiert über fortlaufende Frame-Nummern, siehe <see cref="DriftResampler"/>.
/// </summary>
public sealed class AudioRing
{
    private readonly float[] _samples;
    private readonly int _mask;
    private long _writeFrames;

    public AudioRing(int minimumFrames)
    {
        var capacity = (int)BitOperations.RoundUpToPowerOf2((uint)minimumFrames);
        _samples = new float[capacity * 2];
        _mask = capacity - 1;
    }

    public int CapacityFrames => _mask + 1;

    public long WriteFrames => Volatile.Read(ref _writeFrames);

    public void Write(ReadOnlySpan<float> stereo)
    {
        var frames = stereo.Length / 2;
        var write = _writeFrames;
        var start = (int)(write & _mask) * 2;
        var firstPart = Math.Min(stereo.Length, _samples.Length - start);
        stereo[..firstPart].CopyTo(_samples.AsSpan(start));
        stereo[firstPart..].CopyTo(_samples);
        Volatile.Write(ref _writeFrames, write + frames);
    }

    public float Left(long frame) => _samples[(int)(frame & _mask) * 2];

    public float Right(long frame) => _samples[((int)(frame & _mask) * 2) + 1];
}

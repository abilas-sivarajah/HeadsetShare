using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HeadsetShare.Audio;

public delegate void StereoPacketHandler(ReadOnlySpan<float> stereo);

/// <summary>
/// Greift den Ton eines Ausgabegeräts ab und liefert ihn als Stereo-Float.
/// Loopback liefert nur Daten, solange das Gerät etwas abspielt. Deshalb läuft dort ein Stille-Stream
/// mit, damit die Pakete auch in Musikpausen durchgehend im Takt des Geräts kommen.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private readonly WasapiRecorder _recorder;
    private readonly WasapiPlayer _keepAlive;
    private readonly int _channels;
    private float[] _stereo = new float[4096];
    private float[] _silence = [];
    private float _peak;

    public LoopbackCapture(MMDevice device)
    {
        DeviceId = device.ID;
        _recorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithLoopbackCapture()
            .WithMmcssThreadPriority("Audio")
            .Build();

        var format = _recorder.WaveFormat;
        if (!IsFloat32(format))
        {
            _recorder.Dispose();
            throw new NotSupportedException($"Das Tonformat der Quelle wird nicht unterstützt: {format}");
        }

        SampleRate = format.SampleRate;
        _channels = format.Channels;

        _keepAlive = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithLatency(100).Build();
        _keepAlive.Init(new SilenceProvider(format));

        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                Failed?.Invoke(e.Exception);
            }
        };
    }

    public event StereoPacketHandler? Packet;

    public event Action<Exception>? Failed;

    public string DeviceId { get; }

    public int SampleRate { get; }

    public void Start()
    {
        _keepAlive.Play();
        _recorder.StartRecording();
    }

    /// <summary>Spitzenpegel seit dem letzten Aufruf.</summary>
    public float TakePeak() => Interlocked.Exchange(ref _peak, 0);

    public void Dispose()
    {
        try
        {
            _recorder.StopRecording();
            _keepAlive.Stop();
        }
        catch (Exception)
        {
            // Beim Abstecken ist das Gerät schon weg, Dispose räumt trotzdem auf.
        }

        _recorder.Dispose();
        _keepAlive.Dispose();
    }

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var input = MemoryMarshal.Cast<byte, float>(data);
        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            // Bei gesetztem Silent-Flag ist der Pufferinhalt laut WASAPI undefiniert.
            if (_silence.Length < input.Length)
            {
                _silence = new float[input.Length];
            }

            input = _silence.AsSpan(0, input.Length);
        }

        var frames = input.Length / _channels;
        if (_stereo.Length < frames * 2)
        {
            _stereo = new float[frames * 2];
        }

        var peak = DownmixToStereo(input, _stereo, _channels);
        if (peak > Volatile.Read(ref _peak))
        {
            Volatile.Write(ref _peak, peak);
        }

        Packet?.Invoke(_stereo.AsSpan(0, frames * 2));
    }

    private static float DownmixToStereo(ReadOnlySpan<float> input, Span<float> stereo, int channels)
    {
        var peak = 0f;
        var frames = input.Length / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var source = input.Slice(frame * channels, channels);
            float left;
            float right;
            if (channels == 1)
            {
                left = right = source[0];
            }
            else
            {
                left = source[0];
                right = source[1];
                if (channels >= 3)
                {
                    // Reihenfolge bei Windows: L, R, Center, LFE, dann Surround-Paare.
                    left += 0.7f * source[2];
                    right += 0.7f * source[2];
                    for (var surround = 4; surround + 1 < channels; surround += 2)
                    {
                        left += 0.5f * source[surround];
                        right += 0.5f * source[surround + 1];
                    }
                }
            }

            stereo[frame * 2] = left;
            stereo[(frame * 2) + 1] = right;
            peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
        }

        return peak;
    }

    private static bool IsFloat32(WaveFormat format)
    {
        if (format.BitsPerSample != 32)
        {
            return false;
        }

        return format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible
                && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
    }

    private sealed class SilenceProvider(WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = format;

        public int Read(Span<byte> buffer)
        {
            buffer.Clear();
            return buffer.Length;
        }
    }
}

using HeadsetShare.Audio;
using Xunit.Abstractions;

namespace HeadsetShare.Tests;

public class DriftResamplerTests(ITestOutputHelper output_)
{
    private const int Rate = 48000;
    private const int BlockFrames = 480;
    private const double MaxDistortionDb = -60;

    [Theory]
    [InlineData(100)]
    [InlineData(-300)]
    [InlineData(900)]
    public void Regler_gleicht_Clock_Drift_aus(double ppm)
    {
        var (ring, resampler) = Create(Rate, Rate);
        var phase = 0.0;
        var written = 0L;
        var output = new float[BlockFrames * 2];

        for (var block = 0; block < 60000; block++)
        {
            // Wie echte Hardware: gleichmäßig etwas mehr oder weniger Frames pro Block.
            var due = (long)((block + 1) * BlockFrames * (1 + (ppm / 1e6)));
            ring.Write(Sine((int)(due - written), 440, Rate, ref phase));
            written = due;
            resampler.Read(output);
        }

        Assert.Equal(ppm, resampler.CorrectionPpm, 0.5);
        Assert.Equal(0.06, resampler.SmoothedFillSeconds, 0.0005);
        Assert.Equal(0, resampler.Underruns);
    }

    [Fact]
    public void Abtastraten_werden_ohne_Tonhoehenfehler_umgerechnet()
    {
        var (ring, resampler) = Create(44100, Rate);
        var phase = 0.0;
        var output = new float[BlockFrames * 2];
        var crossings = 0;
        var counted = 0L;
        var previous = 0f;

        for (var block = 0; block < 6000; block++)
        {
            ring.Write(Sine(441, 1000, 44100, ref phase));
            resampler.Read(output);
            if (block < 3000)
            {
                continue;
            }

            for (var i = 0; i < BlockFrames; i++)
            {
                var sample = output[i * 2];
                if (previous < 0 && sample >= 0)
                {
                    crossings++;
                }

                previous = sample;
                counted++;
            }
        }

        Assert.Equal(1000.0, crossings * (double)Rate / counted, 0.2);
        Assert.Equal(0, resampler.Underruns);
    }

    [Theory]
    [InlineData(1000.0)]
    [InlineData(12000.0)]
    public void Resampler_verzerrt_kaum(double frequency)
    {
        var (ring, resampler) = Create(Rate, Rate);
        var phase = 0.0;
        var written = 0L;
        var output = new float[BlockFrames * 2];
        var captured = new List<float>();

        for (var block = 0; block < 4000; block++)
        {
            // 200 ppm: Der Resampler muss ständig zwischen den Samples interpolieren.
            var due = (long)((block + 1) * BlockFrames * 1.0002);
            ring.Write(Sine((int)(due - written), frequency, Rate, ref phase));
            written = due;
            resampler.Read(output);
            if (block >= 3000)
            {
                for (var i = 0; i < BlockFrames; i++)
                {
                    captured.Add(output[i * 2]);
                }
            }
        }

        var distortion = DistortionDb(captured);
        output_.WriteLine($"{frequency} Hz: Störanteil {distortion:F1} dB");
        Assert.True(distortion < MaxDistortionDb, $"{frequency} Hz: Störanteil {distortion:F1} dB");
    }

    [Fact]
    public void Delay_Regler_wirkt_sofort_und_knackt_nicht()
    {
        var (ring, resampler) = Create(Rate, Rate);
        var input = Constant(0.5f);
        var output = new float[BlockFrames * 2];
        var largestStep = 0f;
        var last = 0.5f;

        for (var block = 0; block < 300; block++)
        {
            ring.Write(input);
            if (block == 200)
            {
                resampler.TargetLatencySeconds = 0.26;
            }

            resampler.Read(output);
            if (block <= 50)
            {
                continue;
            }

            foreach (var sample in output)
            {
                largestStep = Math.Max(largestStep, Math.Abs(sample - last));
                last = sample;
            }
        }

        Assert.Equal(0.26, resampler.SmoothedFillSeconds, 0.01);
        Assert.True(largestStep < 0.01, $"Sample-Sprung {largestStep}");
        Assert.Equal(0, resampler.Underruns);
    }

    [Fact]
    public void Nach_Ausfall_der_Quelle_kommt_kein_alter_Ton_in_der_Schleife()
    {
        var (ring, resampler) = Create(Rate, Rate);
        var input = Constant(0.5f);
        var output = new float[BlockFrames * 2];

        for (var block = 0; block < 100; block++)
        {
            ring.Write(input);
            resampler.Read(output);
        }

        for (var block = 0; block < 50; block++)
        {
            resampler.Read(output);
        }

        Assert.Equal(1, resampler.Underruns);
        Assert.All(output, sample => Assert.Equal(0f, sample));

        for (var block = 0; block < 100; block++)
        {
            ring.Write(input);
            resampler.Read(output);
        }

        Assert.Equal(1, resampler.Underruns);
        Assert.All(output, sample => Assert.Equal(0.5f, sample, 0.001f));
    }

    [Fact]
    public void Vor_dem_ersten_Vorrat_kommt_Stille()
    {
        var (ring, resampler) = Create(Rate, Rate);
        var output = new float[BlockFrames * 2];
        Array.Fill(output, 1f);

        ring.Write(Constant(0.5f));
        resampler.Read(output);

        Assert.All(output, sample => Assert.Equal(0f, sample));
        Assert.Equal(0, resampler.Underruns);
    }

    private static (AudioRing Ring, DriftResampler Resampler) Create(int inputRate, int outputRate)
    {
        var ring = new AudioRing(inputRate * 4);
        return (ring, new DriftResampler(ring, inputRate, outputRate, 0.06));
    }

    private static float[] Constant(float value)
    {
        var samples = new float[BlockFrames * 2];
        Array.Fill(samples, value);
        return samples;
    }

    private static float[] Sine(int frames, double frequency, int rate, ref double phase)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[i * 2] = samples[(i * 2) + 1] = (float)Math.Sin(phase);
            phase += 2 * Math.PI * frequency / rate;
        }

        return samples;
    }

    /// <summary>
    /// Für einen reinen Sinus gilt x[n+1] + x[n-1] = 2 cos(w) x[n]. Was davon abweicht, ist Verzerrung.
    /// Das bleibt auch dann gültig, wenn die Regelung die Frequenz langsam verschiebt.
    /// </summary>
    private static double DistortionDb(List<float> samples)
    {
        double correlation = 0, energy = 0;
        for (var i = 1; i < samples.Count - 1; i++)
        {
            correlation += (double)samples[i] * (samples[i + 1] + samples[i - 1]);
            energy += (double)samples[i] * samples[i];
        }

        var twoCosine = correlation / energy;
        double residual = 0;
        for (var i = 1; i < samples.Count - 1; i++)
        {
            var error = samples[i + 1] + samples[i - 1] - (twoCosine * samples[i]);
            residual += error * error;
        }

        return 10 * Math.Log10(residual / energy);
    }
}

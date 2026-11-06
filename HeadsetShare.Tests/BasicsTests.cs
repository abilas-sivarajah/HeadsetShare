using System.IO;
using HeadsetShare.Audio;

namespace HeadsetShare.Tests;

public class AudioRingTests
{
    [Fact]
    public void Kapazitaet_wird_auf_Zweierpotenz_aufgerundet()
    {
        Assert.Equal(1024, new AudioRing(1000).CapacityFrames);
    }

    [Fact]
    public void Daten_bleiben_ueber_das_Pufferende_hinweg_korrekt()
    {
        var ring = new AudioRing(4);
        ring.Write([1f, 2f, 3f, 4f, 5f, 6f]);
        ring.Write([7f, 8f, 9f, 10f, 11f, 12f]);

        Assert.Equal(6, ring.WriteFrames);
        Assert.Equal(11f, ring.Left(5));
        Assert.Equal(12f, ring.Right(5));
        Assert.Equal(5f, ring.Left(2));
    }
}

public class DeviceNameTests
{
    [Theory]
    [InlineData("Lautsprecher (HyperX Cloud Stinger Core (Wireless) – PS)", "HyperX Cloud Stinger Core (Wireless) – PS")]
    [InlineData("Lautsprecher (2- HyperX Cloud Stinger Core (Wireless) – PS)", "HyperX Cloud Stinger Core (Wireless) – PS")]
    [InlineData("B22W-7 LED (NVIDIA High Definition Audio)", "B22W-7 LED")]
    [InlineData("Lautsprecher (Realtek(R) Audio)", "Realtek Audio")]
    [InlineData("Einfach ein Name", "Einfach ein Name")]
    public void Namen_werden_lesbar(string fullName, string expected)
    {
        Assert.Equal(expected, DeviceService.CleanName(fullName));
    }

    [Theory]
    [InlineData("Lautsprecher (HyperX Cloud Stinger Core (Wireless) – PS)", DeviceKind.Headset)]
    [InlineData("Kopfhörer (Irgendwas)", DeviceKind.Headset)]
    [InlineData("B22W-7 LED (NVIDIA High Definition Audio)", DeviceKind.Monitor)]
    [InlineData("Lautsprecher (Realtek(R) Audio)", DeviceKind.Speaker)]
    public void Geraetetyp_wird_erkannt(string fullName, DeviceKind expected)
    {
        Assert.Equal(expected, DeviceService.GuessKind(fullName));
    }
}

public sealed class AppSettingsTests : IDisposable
{
    private static readonly string TestFile = CreateTestFilePath();

    public void Dispose()
    {
        File.Delete(TestFile);
    }

    [Fact]
    public void Einstellungen_ueberstehen_Speichern_und_Laden()
    {
        new AppSettings
        {
            SourceId = "quelle",
            MuteSource = true,
            Targets = [new SavedTarget { Id = "ziel", Name = "Headset", Kind = 0, Volume = 0.4, DelayMs = 120, Muted = true }],
        }.Save();

        var loaded = AppSettings.Load();

        Assert.Equal("quelle", loaded.SourceId);
        Assert.True(loaded.MuteSource);
        var target = Assert.Single(loaded.Targets);
        Assert.Equal("Headset", target.Name);
        Assert.Equal(0.4, target.Volume);
        Assert.Equal(120, target.DelayMs);
        Assert.True(target.Muted);
    }

    [Fact]
    public void Kaputte_Datei_verhindert_den_Start_nicht()
    {
        File.WriteAllText(TestFile, "{ das ist kein json");

        var loaded = AppSettings.Load();

        Assert.Null(loaded.SourceId);
        Assert.Empty(loaded.Targets);
    }

    // Muss gesetzt sein, bevor AppSettings zum ersten Mal benutzt wird, damit nie echte Einstellungen berührt werden.
    private static string CreateTestFilePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"headsetshare-test-{Guid.NewGuid():N}.json");
        Environment.SetEnvironmentVariable("HEADSETSHARE_SETTINGS", path);
        return path;
    }
}

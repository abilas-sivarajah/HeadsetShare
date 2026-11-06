using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace HeadsetShare.Audio;

public enum DeviceKind
{
    Headset,
    Speaker,
    Monitor,
}

public sealed record AudioDeviceInfo(string Id, string Name, string FullName, DeviceKind Kind, int SampleRate);

/// <summary>Findet Ausgabegeräte und meldet, wenn welche ein- oder ausgesteckt werden.</summary>
public sealed partial class DeviceService : IDisposable
{
    private static readonly string[] GenericEndpointNames =
        ["Lautsprecher", "Speakers", "Speaker", "Kopfhörer", "Headphones", "Headset", "Kopfhörerausgang"];

    private readonly MMDeviceEnumerator _notificationEnumerator = new();
    private readonly MMDeviceNotificationClient _notifications;

    public DeviceService()
    {
        _notifications = _notificationEnumerator.CreateNotificationClient(false);
        _notifications.DeviceAdded += (_, _) => Changed?.Invoke();
        _notifications.DeviceRemoved += (_, _) => Changed?.Invoke();
        _notifications.DeviceStateChanged += (_, _) => Changed?.Invoke();
        _notifications.DefaultDeviceChanged += (_, _) => Changed?.Invoke();
    }

    /// <summary>Kommt von einem Audio-Systemthread. Handler dürfen dort keine Audio-APIs aufrufen.</summary>
    public event Action? Changed;

    public static IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                using (device)
                {
                    using var client = device.CreateAudioClient();
                    var fullName = device.FriendlyName;
                    devices.Add(new AudioDeviceInfo(
                        device.ID,
                        CleanName(fullName),
                        fullName,
                        GuessKind(fullName),
                        client.MixFormat.SampleRate));
                }
            }
            catch (Exception)
            {
                // Ein Gerät, das sich gerade verabschiedet, soll die Liste nicht verhindern.
            }
        }

        return NumberDuplicates(devices)
            .OrderBy(device => device.Kind)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string? GetDefaultDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            return null;
        }

        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }

    public void Dispose()
    {
        _notifications.Dispose();
        _notificationEnumerator.Dispose();
    }

    /// <summary>"Lautsprecher (2- HyperX Cloud)" wird zu "HyperX Cloud", "B22W-7 LED (NVIDIA ...)" zu "B22W-7 LED".</summary>
    public static string CleanName(string fullName)
    {
        var name = fullName.Trim();
        var open = name.IndexOf('(');
        if (open > 0 && name.EndsWith(')'))
        {
            var endpoint = name[..open].Trim();
            var adapter = name[(open + 1)..^1].Trim();
            name = GenericEndpointNames.Contains(endpoint, StringComparer.OrdinalIgnoreCase) ? adapter : endpoint;
        }

        name = LeadingNumber().Replace(name, string.Empty);
        return name.Replace("(R)", string.Empty).Replace("®", string.Empty).Trim();
    }

    public static DeviceKind GuessKind(string fullName)
    {
        var name = fullName.ToLowerInvariant();
        if (name.Contains("headset") || name.Contains("kopfhörer") || name.Contains("headphone")
            || name.Contains("wireless") || name.Contains("hyperx"))
        {
            return DeviceKind.Headset;
        }

        return name.Contains("nvidia") || name.Contains("hdmi") || name.Contains("display")
            ? DeviceKind.Monitor
            : DeviceKind.Speaker;
    }

    private static IEnumerable<AudioDeviceInfo> NumberDuplicates(List<AudioDeviceInfo> devices)
    {
        // Sortiert nach ID, damit "2" über Neustarts hinweg dasselbe Gerät bleibt.
        foreach (var group in devices.GroupBy(device => device.Name))
        {
            var number = 1;
            foreach (var device in group.OrderBy(device => device.Id, StringComparer.Ordinal))
            {
                yield return group.Count() == 1 || number == 1 ? device : device with { Name = $"{device.Name} {number}" };
                number++;
            }
        }
    }

    [GeneratedRegex(@"^\d+-\s*")]
    private static partial Regex LeadingNumber();
}

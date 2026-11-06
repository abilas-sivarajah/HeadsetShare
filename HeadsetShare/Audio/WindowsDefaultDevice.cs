using System.Runtime.InteropServices;

namespace HeadsetShare.Audio;

/// <summary>
/// Stellt das Standard-Ausgabegerät von Windows um. Windows bietet dafür keine offizielle API.
/// IPolicyConfig ist undokumentiert, aber seit Windows 7 stabil und in vielen Audio-Tools im Einsatz.
/// </summary>
public static class WindowsDefaultDevice
{
    /// <summary>Gibt false zurück, falls Windows die Umstellung ablehnt.</summary>
    public static bool TrySet(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                // Rollen: 0 Konsole, 1 Multimedia, 2 Kommunikation.
                for (var role = 0; role < 3; role++)
                {
                    if (policy.SetDefaultEndpoint(deviceId, role) != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            return false;
        }
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient;

    // Die Reihenfolge muss exakt der COM-Schnittstelle entsprechen. Nur die letzte Methode wird benutzt.
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();

        [PreserveSig] int GetDeviceFormat();

        [PreserveSig] int ResetDeviceFormat();

        [PreserveSig] int SetDeviceFormat();

        [PreserveSig] int GetProcessingPeriod();

        [PreserveSig] int SetProcessingPeriod();

        [PreserveSig] int GetShareMode();

        [PreserveSig] int SetShareMode();

        [PreserveSig] int GetPropertyValue();

        [PreserveSig] int SetPropertyValue();

        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }
}

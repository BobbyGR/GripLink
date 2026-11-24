using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace GripLink.Audio;

/// <summary>
/// Windows makes a newly plugged-in controller headset the default sound device.
/// This remembers the defaults before the virtual controller appears and puts them back.
/// </summary>
internal sealed class DefaultDeviceGuard
{
    private readonly Action<string> _log;
    private readonly Dictionary<(DataFlow, Role), string> _saved = new();

    public DefaultDeviceGuard(Action<string> log) => _log = log;

    public void Remember()
    {
        _saved.Clear();
        using var en = new MMDeviceEnumerator();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                try
                {
                    var d = en.GetDefaultAudioEndpoint(flow, role);
                    if (!AudioBridge.IsVirtualController(d)) _saved[(flow, role)] = d.ID;
                }
                catch { /* no device for this role */ }
            }
    }

    /// <summary>Watches for a few seconds after attaching and restores any default that moved.</summary>
    public async Task RestoreAfterAttachAsync(CancellationToken ct)
    {
        for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (RestoreOnce() && i > 6) return;
        }
    }

    /// <summary>Returns true when every saved default is in place.</summary>
    private bool RestoreOnce()
    {
        bool allGood = true;
        using var en = new MMDeviceEnumerator();
        foreach (var ((flow, role), id) in _saved)
        {
            try
            {
                var cur = en.GetDefaultAudioEndpoint(flow, role);
                if (cur.ID == id) continue;
                if (!AudioBridge.IsVirtualController(cur)) continue; // the user picked something else
                var policy = (IPolicyConfig)new CPolicyConfigClient();
                int hr = policy.SetDefaultEndpoint(id, role switch { Role.Console => 0, Role.Multimedia => 1, _ => 2 });
                Marshal.ReleaseComObject(policy);
                if (hr == 0) _log($"Restored your default {(flow == DataFlow.Render ? "speaker" : "microphone")} ({role})");
                else allGood = false;
            }
            catch { allGood = false; }
        }
        return allGood;
    }

    // Undocumented but long-stable Windows interface used by the Sound control panel.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}

using System.Runtime.InteropServices;

namespace BTAudioTray;

/// <summary>
/// Enumerates audio render endpoints and switches the Windows default output
/// device. Switching uses the undocumented IPolicyConfig COM interface (the same
/// mechanism the Sound control panel and tools like SoundSwitch use); there is no
/// public API for setting the default endpoint. The enumeration side uses the
/// fully documented Core Audio MMDevice API.
/// </summary>
internal static class AudioManager
{
    public record AudioEndpoint(string Id, string FriendlyName);

    /// <summary>All active (plugged-in / connected) render endpoints.</summary>
    public static List<AudioEndpoint> GetRenderEndpoints() =>
        GetRenderEndpoints(DEVICE_STATE_ACTIVE);

    /// <summary>
    /// Render endpoints filtered by a DEVICE_STATE mask. Use ACTIVE for things that can
    /// be set as default/displayed; include UNPLUGGED when locating a device whose audio
    /// profile hasn't engaged yet (e.g. AirPods right after a Bluetooth connect).
    /// </summary>
    public static List<AudioEndpoint> GetRenderEndpoints(int stateMask)
    {
        var result = new List<AudioEndpoint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            enumerator.EnumAudioEndpoints(EDataFlow.eRender, stateMask, out var collection);
            collection.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                try
                {
                    device.GetId(out string id);
                    string name = GetFriendlyName(device);
                    result.Add(new AudioEndpoint(id, name));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            Marshal.ReleaseComObject(collection);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    /// <summary>Friendly name of the current default render endpoint, or null.</summary>
    public static string? GetDefaultRenderEndpointName()
    {
        string? id = GetDefaultRenderEndpointId();
        if (id == null) return null;
        return GetRenderEndpoints().FirstOrDefault(e => e.Id == id)?.FriendlyName;
    }

    /// <summary>
    /// Scores how well a Bluetooth device name matches an audio-endpoint name. Both are
    /// normalized (apostrophes straightened, noise words dropped) and we count the
    /// fraction of the device's significant words that appear in the endpoint name.
    /// Returns 0..100; 0 means no usable match.
    ///
    /// Example: BT "Jeremy's AirPods Pro" vs endpoint "Headphones (Jeremy's AirPods
    /// Pro - Find My)" -> all of {jeremys, airpods, pro} present -> high score.
    /// </summary>
    public static int MatchScore(string deviceName, string endpointName)
    {
        var devTokens = SignificantTokens(deviceName);
        if (devTokens.Count == 0) return 0;

        var epTokens = SignificantTokens(endpointName).ToHashSet();
        int hits = devTokens.Count(t => epTokens.Contains(t));
        return (int)(100.0 * hits / devTokens.Count);
    }

    // Words that appear in endpoint descriptors but carry no identity.
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "headphones", "headset", "headphone", "speakers", "speaker", "stereo",
        "hands", "free", "handsfree", "hands-free", "find", "my", "audio", "output",
    };

    private static List<string> SignificantTokens(string name)
    {
        // Straighten curly apostrophes, drop punctuation, split on non-letters/digits.
        string normalized = name.Replace('’', '\'').ToLowerInvariant();
        var raw = normalized.Split(
            new[] { ' ', '\'', '(', ')', '-', '_', ',', '.', '/', '\\', '[', ']', '+' },
            StringSplitOptions.RemoveEmptyEntries);

        return raw.Where(t => t.Length >= 3 && !NoiseWords.Contains(t)).ToList();
    }

    public static string? GetDefaultRenderEndpointId()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (hr != 0 || device == null) return null;
            device.GetId(out string id);
            Marshal.ReleaseComObject(device);
            return id;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Sets the default render endpoint for all three roles (Console, Multimedia,
    /// Communications) so every app — including ones that use the comms role — moves.
    /// </summary>
    public static void SetDefaultRenderEndpoint(string endpointId)
    {
        var policyConfig = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (ERole role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            {
                int hr = policyConfig.SetDefaultEndpoint(endpointId, role);
                if (hr != 0)
                    Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policyConfig);
        }
    }

    /// <summary>
    /// Best-effort match of a Bluetooth device name to an audio endpoint. Endpoint
    /// names are like "Headphones (Jeremy's AirPods Stereo)" so we match on substring
    /// either direction.
    /// </summary>
    public static AudioEndpoint? FindEndpointByDeviceName(string deviceName)
    {
        // Only ACTIVE endpoints can be set as default, so match against those.
        AudioEndpoint? best = null;
        int bestScore = 0;
        foreach (var ep in GetRenderEndpoints())
        {
            int score = MatchScore(deviceName, ep.FriendlyName);
            if (score > bestScore)
            {
                bestScore = score;
                best = ep;
            }
        }
        // Require at least half the device's identity words to match, so unrelated
        // endpoints (e.g. "Speakers") don't win on a single weak token.
        return bestScore >= 50 ? best : null;
    }

    /// <summary>
    /// Like <see cref="FindEndpointByDeviceName"/> but also considers UNPLUGGED
    /// endpoints. A Bluetooth audio device that just connected often has its render
    /// endpoint in the UNPLUGGED state until something routes audio to it; calling
    /// SetDefaultRenderEndpoint on that endpoint id activates it. Used by the connect
    /// flow so we don't give up with "audio output not found".
    /// </summary>
    public static AudioEndpoint? FindConnectableEndpointByDeviceName(string deviceName)
    {
        AudioEndpoint? best = null;
        int bestScore = 0;
        foreach (var ep in GetRenderEndpoints(DEVICE_STATE_ACTIVE_OR_UNPLUGGED))
        {
            int score = MatchScore(deviceName, ep.FriendlyName);
            if (score > bestScore)
            {
                bestScore = score;
                best = ep;
            }
        }
        return bestScore >= 50 ? best : null;
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(STGM_READ, out var store);
        try
        {
            var key = PKEY_Device_FriendlyName;
            store.GetValue(ref key, out var pv);
            try
            {
                return Marshal.PtrToStringUni(pv.pwszVal) ?? "(unknown)";
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    // ---- Core Audio constants ----
    private const int DEVICE_STATE_ACTIVE = 0x00000001;
    private const int DEVICE_STATE_UNPLUGGED = 0x00000008;
    public const int DEVICE_STATE_ACTIVE_OR_UNPLUGGED = DEVICE_STATE_ACTIVE | DEVICE_STATE_UNPLUGGED;
    private const int STGM_READ = 0x00000000;

    // PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
    private static PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    // =====================================================================
    //  COM interop declarations
    // =====================================================================

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int count);
        int GetAt(int index, out PROPERTYKEY key);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        int Commit();
    }

    // ---- IPolicyConfig (undocumented) ----
    // CLSID_CPolicyConfigClient
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    // IPolicyConfig - Win7+ variant. Only SetDefaultEndpoint is needed; the other
    // slots must still be declared (in vtable order) so the offset is correct.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceId, bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceId);
        [PreserveSig] int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceId, bool def, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string deviceId, bool store, ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetPropertyValue(string deviceId, bool store, ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int SetDefaultEndpoint(string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(string deviceId, bool visible);
    }
}

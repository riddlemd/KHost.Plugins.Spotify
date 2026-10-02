using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KHost.Plugins.Spotify.Control;

/// <summary>Spotify's sessions in the Windows mixer, read through Core Audio.</summary>
/// <remarks>Plain COM interop rather than WinRT or a package, so it builds in both targets. Every
/// interface below lists its methods in the header's vtable order, inherited ones first: a
/// method out of place calls the wrong slot, and that is a crash, not an exception.</remarks>
[SupportedOSPlatform("windows")]
internal sealed class CoreAudioSpotifySessions : ISpotifyAudioSessions
{
    private const string SpotifyProcessName = "Spotify";

    private const int RenderFlow = 0;          // EDataFlow.eRender
    private const int DeviceStateActive = 0x1; // DEVICE_STATE_ACTIVE
    private const int ClsCtxAll = 0x17;        // CLSCTX_ALL
    private const int SessionStateActive = 1;  // AudioSessionState.AudioSessionStateActive

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public IReadOnlyList<ISpotifyAudioSession> Open()
    {
        var found = new List<ISpotifyAudioSession>();

        var enumeratorType = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: true)!;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;

        try
        {
            // Every active output, not only the default: Spotify can be sent to any device.
            enumerator.EnumAudioEndpoints(RenderFlow, DeviceStateActive, out var devices);

            try
            {
                devices.GetCount(out var deviceCount);

                for (var d = 0; d < deviceCount; d++)
                {
                    devices.Item(d, out var device);

                    try
                    {
                        AddSpotifySessions(device, found);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devices);
            }
        }
        catch
        {
            foreach (var session in found)
                session.Dispose();

            throw;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return found;
    }

    private static void AddSpotifySessions(IMMDevice device, List<ISpotifyAudioSession> found)
    {
        var managerId = typeof(IAudioSessionManager2).GUID;

        device.Activate(ref managerId, ClsCtxAll, IntPtr.Zero, out var activated);
        var manager = (IAudioSessionManager2)activated;

        try
        {
            manager.GetSessionEnumerator(out var sessions);

            try
            {
                sessions.GetCount(out var sessionCount);

                for (var s = 0; s < sessionCount; s++)
                {
                    sessions.GetSession(s, out var control);

                    var keep = false;

                    try
                    {
                        var control2 = (IAudioSessionControl2)control;

                        control2.GetProcessId(out var processId);

                        if (processId == 0 || !IsSpotify(processId))
                            continue;

                        control2.GetSessionInstanceIdentifier(out var id);

                        found.Add(new Session(id, (ISimpleAudioVolume)control, control));
                        keep = true;
                    }
                    finally
                    {
                        if (!keep)
                            Marshal.ReleaseComObject(control);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private static bool IsSpotify(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);

            return string.Equals(process.ProcessName, SpotifyProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Exited between the mixer listing it and the lookup.
            return false;
        }
    }

    private sealed class Session(string id, ISimpleAudioVolume volume, object comObject) : ISpotifyAudioSession
    {
        public string Id { get; } = id;

        public float Volume
        {
            get
            {
                volume.GetMasterVolume(out var level);
                return level;
            }
            set
            {
                var eventContext = Guid.Empty;
                volume.SetMasterVolume(Math.Clamp(value, 0f, 1f), ref eventContext);
            }
        }

        public bool IsActive
        {
            get
            {
                ((IAudioSessionControl2)comObject).GetState(out var state);
                return state == SessionStateActive;
            }
        }

        public void Dispose() => Marshal.ReleaseComObject(comObject);
    }

    // mmdeviceapi.h

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(
            ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object activated);
        void OpenPropertyStore(int access, out IntPtr properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    // audiopolicy.h

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        void GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
        void GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);

        // IAudioSessionManager2
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        void RegisterSessionNotification(IntPtr sessionNotification);
        void UnregisterSessionNotification(IntPtr sessionNotification);
        void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
        void UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int sessionCount);
        void GetSession(int sessionIndex, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        void GetState(out int state);
        void GetDisplayName(out IntPtr name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
        void GetIconPath(out IntPtr path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
        void GetGroupingParam(out Guid groupingId);
        void SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
        void RegisterAudioSessionNotification(IntPtr client);
        void UnregisterAudioSessionNotification(IntPtr client);

        // IAudioSessionControl2
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid eventContext);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

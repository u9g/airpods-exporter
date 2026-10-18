using System.Runtime.InteropServices;

namespace AirPodsExporter;

enum DataFlow { Render = 0, Capture = 1 }

enum Role { Console = 0, Multimedia = 1, Communications = 2 }

sealed record Endpoint(IMMDevice Device, string Id, string Name, int State);

static class CoreAudio
{
    public const int StateActive = 0x1;
    public const int StateMaskAll = 0xF;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint BufferFlagsDataDiscontinuity = 0x1;
    public const uint BufferFlagsSilent = 0x2;

    const int ClsctxAll = 23;
    const ushort VtLpwstr = 31;
    static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    public static List<Endpoint> List(IMMDeviceEnumerator enumerator, DataFlow flow)
    {
        Check(enumerator.EnumAudioEndpoints(flow, StateMaskAll, out var collection), "EnumAudioEndpoints");
        Check(collection.GetCount(out var count), "IMMDeviceCollection.GetCount");
        var endpoints = new List<Endpoint>(count);
        for (int i = 0; i < count; i++)
        {
            Check(collection.Item(i, out var device), "IMMDeviceCollection.Item");
            Check(device.GetId(out var id), "IMMDevice.GetId");
            Check(device.GetState(out var state), "IMMDevice.GetState");
            endpoints.Add(new Endpoint(device, id, FriendlyName(device), state));
        }
        return endpoints;
    }

    public static string? DefaultRenderId(IMMDeviceEnumerator enumerator)
    {
        if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) < 0 || device is null) return null;
        return device.GetId(out var id) < 0 ? null : id;
    }

    public static T Activate<T>(IMMDevice device) where T : class
    {
        var iid = typeof(T).GUID;
        Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var instance), $"Activate {typeof(T).Name}");
        return (T)instance;
    }

    public static bool IsHandsFree(Endpoint endpoint) => endpoint.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);

    public static string StateName(int state) => state switch
    {
        1 => "Active",
        2 => "Disabled",
        4 => "NotPresent",
        8 => "Unplugged",
        _ => $"0x{state:X}",
    };

    public static void Check(int hr, string what)
    {
        if (hr >= 0) return;
        var meaning = (uint)hr switch
        {
            0x88890004 => " (the audio device went away)",
            0x8889000A => " (the audio device is in exclusive use)",
            _ => "",
        };
        throw new COMException($"{what} failed: 0x{hr:X8}{meaning}", hr);
    }

    static string FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) < 0) return "";
        var key = FriendlyNameKey;
        if (store.GetValue(ref key, out var value) < 0) return "";
        try
        {
            return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) ?? "" : "";
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);
}

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey(Guid formatId, int propertyId)
{
    public Guid FormatId = formatId;
    public int PropertyId = propertyId;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public IntPtr Pointer;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(DataFlow flow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(DataFlow flow, Role role, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr notifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notifications);
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint processId);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, IntPtr eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, IntPtr eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

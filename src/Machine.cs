using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace AirPodsExporter;

enum DisplayState { Unknown = -1, Off = 0, On = 1, Dimmed = 2 }

/// <summary>Cumulative time one CPU has spent in each mode since boot, in seconds.</summary>
sealed record CpuTimes(double Idle, double User, double System, double Dpc, double Interrupt);

/// <summary>Share of all CPUs' time spent busy, in DPCs and in interrupts during the last second.</summary>
sealed record CpuShare(double Busy, double Dpc, double Interrupt);

sealed record WifiSample(string Ssid, string Bssid, int Channel, int SignalPercent, double ReceiveMbps, double TransmitMbps)
{
    // The Intel and Realtek combo cards in most PCs share their antennas between Wi-Fi and Bluetooth,
    // so Wi-Fi on 2.4 GHz competes with the AirPods for the same airtime. (6 GHz channels are not told apart.)
    public string Band => Channel <= 14 ? "2.4 GHz" : "5 GHz";
}

sealed record NetTotal(string Interface, string Type, long Received, long Sent);

sealed record PcSample(DisplayState Display, double IdleSeconds, CpuTimes[] Cpus, CpuShare? Cpu, WifiSample? Wifi, NetTotal[] Network, double NetworkBytesPerSecond, BtDevice[] Bluetooth)
{
    public bool InUse => IdleSeconds < PcMonitor.IdleAfter.TotalSeconds;
}

/// <summary>
/// Samples what else was going on at the PC: whether the display was on, whether anyone was using the
/// keyboard or mouse, CPU time lost to drivers (DPCs and interrupts), Wi-Fi, network traffic, the other
/// Bluetooth devices, and devices being plugged in or removed.
/// </summary>
sealed class PcMonitor(string filter, Action<string> onEvent) : IDisposable
{
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(60);
    static readonly TimeSpan NetworkRefresh = TimeSpan.FromSeconds(30);

    readonly DisplayWatcher _display = new(onEvent);
    readonly Wifi _wifi = new();
    readonly PairedDevices _paired = new(filter, onEvent);
    readonly DeviceChanges _devices = new(onEvent);

    CpuTimes[]? _lastCpus;
    WifiSample? _lastWifi;
    bool _wifiReported, _inUse = true;
    double _lastIdle;
    NetworkInterface[] _nics = [];
    DateTime _nicsTime;
    long? _lastNetBytes;

    public void Start()
    {
        _display.Start();
        _paired.Start();
        _devices.Start();
    }

    public PcSample Sample()
    {
        var cpus = Cpu.Read();
        var share = _lastCpus is { } last && last.Length == cpus.Length ? Cpu.Share(last, cpus) : null;
        _lastCpus = cpus;

        var idle = IdleSeconds();
        ReportIdle(idle);

        var wifi = _wifi.Read();
        ReportWifi(wifi);

        var network = Network();
        long netBytes = network.Sum(n => n.Received + n.Sent);
        double netRate = _lastNetBytes is long lastBytes && netBytes >= lastBytes ? netBytes - lastBytes : 0;
        _lastNetBytes = netBytes;

        return new PcSample(_display.State, idle, cpus, share, wifi, network, netRate, _paired.Snapshot());
    }

    public void Dispose()
    {
        _display.Dispose();
        _wifi.Dispose();
        _paired.Dispose();
        _devices.Dispose();
    }

    void ReportIdle(double idle)
    {
        bool inUse = idle < IdleAfter.TotalSeconds;
        if (inUse && !_inUse) onEvent($"PC in use again: keyboard/mouse input after {Duration(_lastIdle)} without any");
        else if (!inUse && _inUse) onEvent($"PC idle: no keyboard/mouse input for {IdleAfter.TotalSeconds:0}s");
        _inUse = inUse;
        _lastIdle = idle;
    }

    void ReportWifi(WifiSample? now)
    {
        var last = _lastWifi;
        _lastWifi = now;
        if (_wifiReported && now?.Bssid == last?.Bssid && now?.Channel == last?.Channel) return;
        _wifiReported = true;
        onEvent(now is null
            ? "Wi-Fi: not connected"
            : $"Wi-Fi: {now.Ssid} via {now.Bssid}, channel {now.Channel} ({now.Band}), signal {now.SignalPercent}%");
    }

    NetTotal[] Network()
    {
        if (DateTime.Now - _nicsTime > NetworkRefresh)
        {
            _nicsTime = DateTime.Now;
            try
            {
                // Real adapters only: skip the NDIS filter layers that repeat an adapter's counters, and virtual
                // adapters without a gateway (VirtualBox, Hyper-V switches).
                _nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                        && (n.Supports(NetworkInterfaceComponent.IPv4) || n.Supports(NetworkInterfaceComponent.IPv6))
                        && (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || n.GetIPProperties().GatewayAddresses.Count > 0))
                    .ToArray();
            }
            catch (NetworkInformationException)
            {
                _nics = [];
            }
        }
        var totals = new List<NetTotal>();
        foreach (var nic in _nics)
        {
            try
            {
                var stats = nic.GetIPStatistics();
                totals.Add(new NetTotal(nic.Name, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "ethernet", stats.BytesReceived, stats.BytesSent));
            }
            catch (NetworkInformationException)
            {
                _nicsTime = default; // the adapter went away; list them again next second
            }
        }
        return [.. totals];
    }

    static double IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000.0 : double.NaN;
    }

    static string Duration(double seconds) => TimeSpan.FromSeconds(Math.Round(seconds)) switch
    {
        { TotalHours: >= 1 } t => $"{(int)t.TotalHours}h{t.Minutes:00}m",
        { TotalMinutes: >= 1 } t => $"{t.Minutes}m{t.Seconds:00}s",
        var t => $"{t.Seconds}s",
    };

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);
}

/// <summary>Follows the console display turning on, off and dimming, using a power-setting callback (no window needed).</summary>
sealed class DisplayWatcher(Action<string> onEvent) : IDisposable
{
    static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    const uint DeviceNotifyCallback = 2;
    const uint PowerSettingChange = 0x8013; // PBT_POWERSETTINGCHANGE

    DeviceNotifyCallbackRoutine? _callback; // must stay referenced while registered
    IntPtr _registration;
    volatile int _state = (int)DisplayState.Unknown;

    public DisplayState State => (DisplayState)_state;

    public void Start()
    {
        _callback = OnNotify;
        var parameters = new DeviceNotifySubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(_callback) };
        var guid = ConsoleDisplayState;
        uint error = PowerSettingRegisterNotification(ref guid, DeviceNotifyCallback, ref parameters, out _registration);
        if (error != 0) onEvent($"display state unavailable (error {error})");
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero) PowerSettingUnregisterNotification(_registration);
        _registration = IntPtr.Zero;
    }

    uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        // POWERBROADCAST_SETTING: GUID PowerSetting, DWORD DataLength, then the DWORD value.
        if (type != PowerSettingChange || setting == IntPtr.Zero || Marshal.PtrToStructure<Guid>(setting) != ConsoleDisplayState) return 0;
        var state = (DisplayState)Marshal.ReadInt32(setting, 20);
        if ((int)state != Interlocked.Exchange(ref _state, (int)state))
            onEvent($"display {state.ToString().ToLowerInvariant()}");
        return 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    struct DeviceNotifySubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registration);

    [DllImport("powrprof.dll")]
    static extern uint PowerSettingUnregisterNotification(IntPtr registration);
}

/// <summary>Per-CPU time from NtQuerySystemInformation, which unlike GetSystemTimes splits out DPC and interrupt time.</summary>
static class Cpu
{
    const int SystemProcessorPerformanceInformation = 8;
    const double TicksPerSecond = 10_000_000; // the times are in 100 ns units

    public static CpuTimes[] Read()
    {
        var info = new ProcessorPerformance[Math.Max(64, Environment.ProcessorCount)];
        int size = Marshal.SizeOf<ProcessorPerformance>();
        if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, info, info.Length * size, out int returned) < 0) return [];
        return info.Take(returned / size).Select(p => new CpuTimes(
            Idle: p.Idle / TicksPerSecond,
            User: p.User / TicksPerSecond,
            // Kernel time includes idle, DPC and interrupt time.
            System: Math.Max(0, p.Kernel - p.Idle - p.Dpc - p.Interrupt) / TicksPerSecond,
            Dpc: p.Dpc / TicksPerSecond,
            Interrupt: p.Interrupt / TicksPerSecond)).ToArray();
    }

    public static CpuShare? Share(CpuTimes[] before, CpuTimes[] after)
    {
        double Delta(Func<CpuTimes, double> pick) => after.Select(pick).Sum() - before.Select(pick).Sum();
        double idle = Delta(c => c.Idle), dpc = Delta(c => c.Dpc), interrupt = Delta(c => c.Interrupt);
        double total = idle + Delta(c => c.User) + Delta(c => c.System) + dpc + interrupt;
        return total > 0 ? new CpuShare(1 - idle / total, dpc / total, interrupt / total) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessorPerformance
    {
        public long Idle, Kernel, User, Dpc, Interrupt;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int infoClass, [Out] ProcessorPerformance[] info, int length, out int returned);
}

/// <summary>Reads the connected Wi-Fi network's channel, signal and link rates through the native Wi-Fi API.</summary>
sealed class Wifi : IDisposable
{
    const int CurrentConnection = 7, ChannelNumber = 8;
    const int InterfaceStateConnected = 1;
    const int InterfaceInfoSize = 532; // GUID + WCHAR[256] description + state

    readonly IntPtr _handle;

    public Wifi()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out _handle) != 0) _handle = IntPtr.Zero;
    }

    public WifiSample? Read()
    {
        if (_handle == IntPtr.Zero || WlanEnumInterfaces(_handle, IntPtr.Zero, out var list) != 0) return null;
        try
        {
            int count = Marshal.ReadInt32(list);
            for (int i = 0; i < count; i++)
            {
                var item = list + 8 + i * InterfaceInfoSize;
                if (Marshal.ReadInt32(item, InterfaceInfoSize - 4) != InterfaceStateConnected) continue;
                var guid = Marshal.PtrToStructure<Guid>(item);
                return Query(guid);
            }
            return null;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) WlanCloseHandle(_handle, IntPtr.Zero);
    }

    WifiSample? Query(Guid guid)
    {
        if (WlanQueryInterface(_handle, ref guid, CurrentConnection, IntPtr.Zero, out _, out var conn, out _) != 0) return null;
        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: state, mode, WCHAR[256] profile, then WLAN_ASSOCIATION_ATTRIBUTES at 520.
            int ssidLength = Math.Clamp(Marshal.ReadInt32(conn, 520), 0, 32);
            var ssid = new byte[ssidLength];
            Marshal.Copy(conn + 524, ssid, 0, ssidLength);
            var bssid = new byte[6];
            Marshal.Copy(conn + 560, bssid, 0, 6);
            return new WifiSample(
                Encoding.UTF8.GetString(ssid),
                Convert.ToHexString(bssid).ToLowerInvariant(),
                QueryInt(guid, ChannelNumber) ?? 0,
                Marshal.ReadInt32(conn, 576),
                (uint)Marshal.ReadInt32(conn, 580) / 1000.0,
                (uint)Marshal.ReadInt32(conn, 584) / 1000.0);
        }
        finally
        {
            WlanFreeMemory(conn);
        }
    }

    int? QueryInt(Guid guid, int opcode)
    {
        if (WlanQueryInterface(_handle, ref guid, opcode, IntPtr.Zero, out int size, out var data, out _) != 0) return null;
        try
        {
            return size >= 4 ? Marshal.ReadInt32(data) : null;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    [DllImport("wlanapi.dll")]
    static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, out int valueType);

    [DllImport("wlanapi.dll")]
    static extern void WlanFreeMemory(IntPtr memory);
}

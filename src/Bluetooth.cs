using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace AirPodsExporter;

sealed class BleSecond
{
    /// <summary>Upper bounds in dBm of the signal histogram's buckets; a last bucket holds everything stronger.</summary>
    public static readonly int[] RssiBounds = [-90, -85, -80, -75, -70, -65, -60, -55, -50, -45, -40];

    // The status byte's "in ear" flag in Apple's proximity-pairing broadcast. AirPods Max were seen sending 0x2B while
    // worn and 0x29 once taken off, so this is the bit that follows their head sensor.
    const byte InEarBits = 0x02;

    public int Adverts;
    public long RssiSum;
    public readonly int[] RssiBuckets = new int[RssiBounds.Length + 1];
    public byte? Status;
    public string? Battery, Raw;
    public int? AvgRssi => Adverts > 0 ? (int)(RssiSum / Adverts) : null;
    public bool? Worn => IsWorn(Status);

    public static bool? IsWorn(byte? status) => status is byte s ? (s & InEarBits) != 0 : null;

    public static int Bucket(int rssi)
    {
        int i = Array.FindIndex(RssiBounds, bound => rssi <= bound);
        return i < 0 ? RssiBounds.Length : i;
    }
}

/// <summary>
/// Listens for the AirPods' Apple "proximity pairing" BLE broadcasts, which carry battery level,
/// and records how strongly the PC hears them (RSSI).
/// </summary>
sealed class BleMonitor(ushort model, Action<string> onEvent) : IDisposable
{
    const ushort AppleCompanyId = 0x004C;
    const byte ProximityPairingType = 0x07;

    readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Passive };
    readonly object _lock = new();
    static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);

    readonly Dictionary<ushort, int> _otherModels = [];
    BleSecond _current = new();
    string? _lastBattery, _lastFlags, _lastRaw;
    bool? _lastWorn;
    volatile bool _disposed;

    public void Start()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
        TryStart();
    }

    void TryStart()
    {
        if (_disposed) return;
        try
        {
            _watcher.Start();
        }
        catch (Exception ex)
        {
            onEvent($"BLE scanning unavailable ({ex.Message}); retrying in {RestartDelay.TotalSeconds:0}s");
            ScheduleRestart();
        }
    }

    // Windows stops the watcher when the radio resets or is switched off; keep scanning once it comes back.
    void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (_disposed) return;
        onEvent($"BLE scanning stopped ({args.Error}); restarting in {RestartDelay.TotalSeconds:0}s");
        ScheduleRestart();
    }

    void ScheduleRestart() => _ = Task.Delay(RestartDelay).ContinueWith(_ => TryStart(), TaskScheduler.Default);

    public BleSecond Swap()
    {
        lock (_lock)
        {
            var finished = _current;
            _current = new BleSecond();
            return finished;
        }
    }

    public string OtherModelsText()
    {
        lock (_lock)
        {
            return _otherModels.Count == 0
                ? "none"
                : string.Join(", ", _otherModels.OrderByDescending(m => m.Value).Select(m => $"0x{m.Key:X4} ({m.Value})"));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        _watcher.Stop();
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var data in args.Advertisement.ManufacturerData)
        {
            if (data.CompanyId != AppleCompanyId) continue;
            var bytes = new byte[data.Data.Length];
            using (var reader = DataReader.FromBuffer(data.Data)) reader.ReadBytes(bytes);

            // Apple manufacturer data is a list of [type, length, payload...] records.
            for (int i = 0; i + 1 < bytes.Length;)
            {
                int type = bytes[i], length = bytes[i + 1];
                if (i + 2 + length > bytes.Length) break;
                if (type == ProximityPairingType && length >= 6) Handle(args.RawSignalStrengthInDBm, bytes.AsSpan(i + 2, length));
                i += 2 + length;
            }
        }
    }

    void Handle(short rssi, ReadOnlySpan<byte> payload)
    {
        // payload: [0] prefix, [1..2] model (little-endian), [3] status, [4] battery nibbles, [5] charging flags + case battery
        ushort seenModel = (ushort)(payload[1] | payload[2] << 8);
        string? announce = null;
        lock (_lock)
        {
            if (seenModel != model)
            {
                _otherModels[seenModel] = _otherModels.GetValueOrDefault(seenModel) + 1;
                return;
            }
            var battery = DecodeBattery(payload[4], payload[5]);
            byte status = payload[3];
            bool worn = BleSecond.IsWorn(status) == true;
            var raw = Convert.ToHexString(payload[..Math.Min(8, payload.Length)]);
            // Everything but the battery levels, so flags that are not decoded yet still show up as events when they change.
            var flags = $"{status:X2}{(payload.Length > 6 ? Convert.ToHexString(payload[6..Math.Min(8, payload.Length)]) : "")}";
            _current.Adverts++;
            _current.RssiSum += rssi;
            _current.RssiBuckets[BleSecond.Bucket(rssi)]++;
            _current.Status = status;
            _current.Battery = battery;
            _current.Raw = raw;
            if (battery != _lastBattery) announce = $"AirPods broadcast: battery {battery}, signal {rssi} dBm, {(worn ? "worn" : "not worn")} (raw {raw})";
            else if (worn != _lastWorn) announce = $"AirPods {(worn ? "put on" : "taken off")}, signal {rssi} dBm (raw {_lastRaw} -> {raw})";
            else if (flags != _lastFlags) announce = $"AirPods broadcast flags changed, signal {rssi} dBm (raw {_lastRaw} -> {raw})";
            _lastBattery = battery;
            _lastWorn = worn;
            _lastFlags = flags;
            _lastRaw = raw;
        }
        if (announce is not null) onEvent(announce);
    }

    static string DecodeBattery(byte levels, byte flags)
    {
        // Each nibble is a level in tens of percent (0-10); 15 means "not reported".
        // The flags' high nibble has one charging bit per battery (0x1, 0x2, 0x4); 0x8 is not a charging bit.
        var known = new[] { levels >> 4, levels & 0xF }.Where(v => v <= 10).Distinct().Select(v => $"{v * 10}%").ToList();
        var text = known.Count == 0 ? "unknown" : string.Join("/", known);
        return ((flags >> 4) & 0x7) != 0 ? $"{text} charging" : text;
    }
}

/// <summary>Tracks the classic Bluetooth connection (the one that carries the audio).</summary>
sealed class ClassicMonitor
{
    readonly BluetoothDevice? _device;

    ClassicMonitor(BluetoothDevice? device) => _device = device;

    public string Status => _device?.ConnectionStatus.ToString() ?? "unknown";

    public static ClassicMonitor Find(string filter, Action<string> onChange, Action<string> onEvent)
    {
        try
        {
            var device = FindAsync(filter).GetAwaiter().GetResult();
            if (device is null)
            {
                onEvent($"Bluetooth: no paired device named like \"{filter}\"");
                return new ClassicMonitor(null);
            }
            device.ConnectionStatusChanged += (d, _) => onChange($"Bluetooth link: {d.ConnectionStatus}");
            onEvent($"Bluetooth: {device.Name} is {device.ConnectionStatus}");
            return new ClassicMonitor(device);
        }
        catch (Exception ex)
        {
            onEvent($"Bluetooth: lookup failed: {ex.Message}");
            return new ClassicMonitor(null);
        }
    }

    static async Task<BluetoothDevice?> FindAsync(string filter)
    {
        foreach (var info in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)))
        {
            if (!info.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var device = await BluetoothDevice.FromIdAsync(info.Id);
            if (device is not null) return device;
        }
        return null;
    }
}

sealed record BtDevice(string Name, string Transport, bool Connected);

/// <summary>
/// Follows the connection of every paired Bluetooth device. Another device on the PC's radio at the same time
/// (a second headset, a speaker, a controller) takes airtime away from the AirPods' audio.
/// </summary>
sealed class PairedDevices(string filter, Action<string> onEvent) : IDisposable
{
    readonly object _lock = new();
    readonly Dictionary<string, Tracked> _devices = [];
    readonly List<DeviceWatcher> _watchers = [];

    sealed record Tracked(string Name, string Transport, Func<bool> IsConnected, IDisposable Device);

    public void Start()
    {
        Watch(BluetoothDevice.GetDeviceSelectorFromPairingState(true), "classic");
        Watch(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), "le");
    }

    public BtDevice[] Snapshot()
    {
        lock (_lock)
        {
            return _devices.Values.Select(d => new BtDevice(d.Name, d.Transport, d.IsConnected())).OrderBy(d => d.Name).ToArray();
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        lock (_lock)
        {
            foreach (var device in _devices.Values) device.Device.Dispose();
            _devices.Clear();
        }
    }

    void Watch(string selector, string transport)
    {
        var watcher = DeviceInformation.CreateWatcher(selector);
        watcher.Added += (sender, info) => _ = AddAsync(info, transport);
        watcher.Removed += (_, update) =>
        {
            lock (_lock)
            {
                if (_devices.Remove(update.Id, out var old)) old.Device.Dispose();
            }
        };
        watcher.Updated += (_, _) => { }; // the watcher only reports changes after enumeration if all three events are handled
        _watchers.Add(watcher);
        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            onEvent($"Bluetooth: cannot list paired {transport} devices ({ex.Message})");
        }
    }

    async Task AddAsync(DeviceInformation info, string transport)
    {
        try
        {
            Tracked tracked;
            if (transport == "classic")
            {
                var device = await BluetoothDevice.FromIdAsync(info.Id);
                if (device is null) return;
                device.ConnectionStatusChanged += (d, _) => Changed(d.Name, transport, d.ConnectionStatus == BluetoothConnectionStatus.Connected);
                tracked = new Tracked(device.Name, transport, () => device.ConnectionStatus == BluetoothConnectionStatus.Connected, device);
            }
            else
            {
                var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (device is null) return;
                device.ConnectionStatusChanged += (d, _) => Changed(d.Name, transport, d.ConnectionStatus == BluetoothConnectionStatus.Connected);
                tracked = new Tracked(device.Name, transport, () => device.ConnectionStatus == BluetoothConnectionStatus.Connected, device);
            }
            lock (_lock)
            {
                if (_devices.Remove(info.Id, out var old)) old.Device.Dispose();
                _devices[info.Id] = tracked;
            }
        }
        catch (Exception ex)
        {
            onEvent($"Bluetooth: cannot follow {info.Name} ({ex.Message})");
        }
    }

    void Changed(string name, string transport, bool connected)
    {
        // The watched AirPods' own link is already reported by ClassicMonitor.
        if (transport == "classic" && name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
        onEvent($"Bluetooth: {name}{(transport == "le" ? " (LE)" : "")} {(connected ? "connected" : "disconnected")}");
    }
}

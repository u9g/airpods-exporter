using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace AirPodsExporter;

sealed class BleSecond
{
    public int Adverts;
    public long RssiSum;
    public string? Battery, Raw;
    public int? AvgRssi => Adverts > 0 ? (int)(RssiSum / Adverts) : null;
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
    string? _lastBattery;
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
            _current.Adverts++;
            _current.RssiSum += rssi;
            _current.Battery = battery;
            _current.Raw = Convert.ToHexString(payload[..Math.Min(8, payload.Length)]);
            if (battery != _lastBattery) announce = $"AirPods broadcast: battery {battery}, signal {rssi} dBm";
            _lastBattery = battery;
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

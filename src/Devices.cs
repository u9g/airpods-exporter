using Windows.Devices.Enumeration;

namespace AirPodsExporter;

/// <summary>
/// Reports devices appearing and disappearing after startup: USB devices being plugged in, a monitor waking up,
/// a dock reconnecting. USB 3 devices and cables in particular are known to drown out 2.4 GHz Bluetooth nearby.
/// </summary>
sealed class DeviceChanges(Action<string> onEvent) : IDisposable
{
    static readonly TimeSpan CollapseWithin = TimeSpan.FromSeconds(5);

    // Every enabled device interface; one physical device usually has several, which Report() collapses.
    readonly DeviceWatcher _watcher = DeviceInformation.CreateWatcher(
        "System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True", null, DeviceInformationKind.DeviceInterface);
    readonly object _lock = new();
    readonly Dictionary<string, string> _names = [];
    readonly Dictionary<string, DateTime> _reported = [];
    bool _enumerated;

    public void Start()
    {
        _watcher.Added += (_, info) =>
        {
            lock (_lock)
            {
                _names[info.Id] = info.Name;
                if (_enumerated) Report("connected", info.Name);
            }
        };
        _watcher.Removed += (_, update) =>
        {
            lock (_lock)
            {
                if (_names.Remove(update.Id, out var name)) Report("removed", name);
            }
        };
        _watcher.Updated += (_, _) => { }; // the watcher only reports changes after enumeration if all three events are handled
        _watcher.EnumerationCompleted += (_, _) =>
        {
            int count;
            lock (_lock)
            {
                _enumerated = true;
                count = _names.Count;
            }
            onEvent($"devices: watching {count} device interfaces for plug-ins and removals");
        };
        try
        {
            _watcher.Start();
        }
        catch (Exception ex)
        {
            onEvent($"device changes unavailable ({ex.Message})");
        }
    }

    public void Dispose()
    {
        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
    }

    void Report(string what, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var key = $"{what} {name}";
        var now = DateTime.Now;
        if (_reported.TryGetValue(key, out var last) && now - last < CollapseWithin) return;
        if (_reported.Count > 1000)
            foreach (var old in _reported.Where(r => now - r.Value >= CollapseWithin).Select(r => r.Key).ToList()) _reported.Remove(old);
        _reported[key] = now;
        onEvent($"device {what}: {name}");
    }
}

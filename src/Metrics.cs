using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirPodsExporter;

/// <summary>Serves the latest measurements in the Prometheus text format on 127.0.0.1.</summary>
sealed class PrometheusExporter : IDisposable
{
    static readonly TimeSpan SignalStaleAfter = TimeSpan.FromSeconds(30);

    readonly TcpListener _listener;
    readonly string _device;
    readonly ushort _model;
    readonly LokiPusher? _loki;
    readonly object _lock = new();

    Row? _last;
    double _audioSeconds, _gapSeconds;
    long _glitches, _clicks, _clipped, _broadcasts, _rssiSum;
    readonly long[] _rssiBuckets = new long[BleSecond.RssiBounds.Length + 1];
    int _linkChanges;
    int? _rssi, _battery;
    byte? _status;
    DateTime _rssiTime;

    public PrometheusExporter(int port, string device, ushort model, LokiPusher? loki)
    {
        _device = device;
        _model = model;
        _loki = loki;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public void Update(Row row, int linkChanges)
    {
        lock (_lock)
        {
            _last = row;
            _linkChanges = linkChanges;
            if (row.SampleRate > 0)
            {
                _audioSeconds += (double)row.Audio.Frames / row.SampleRate;
                _gapSeconds += (double)row.Audio.GapFrames / row.SampleRate;
            }
            _glitches += row.Audio.Discontinuities;
            _clicks += row.Audio.Clicks;
            _clipped += row.Audio.Clipped;
            _broadcasts += row.Ble.Adverts;
            _rssiSum += row.Ble.RssiSum;
            for (int i = 0; i < _rssiBuckets.Length; i++) _rssiBuckets[i] += row.Ble.RssiBuckets[i];
            if (row.Ble.AvgRssi is int rssi)
            {
                _rssi = rssi;
                _status = row.Ble.Status;
                _rssiTime = row.Time;
            }
            if (Row.BatteryPercent(row.Ble.Battery) is int battery) _battery = battery;
        }
    }

    public void Dispose() => _listener.Stop();

    string Render()
    {
        var m = new MetricWriter();
        lock (_lock)
        {
            m.Gauge("airpods_monitor_info", "What the monitor is watching.", 1, ("device", _device), ("ble_model", $"{_model:X4}"));
            if (_last is { } r)
            {
                if (r.Link is "Connected" or "Disconnected")
                    m.Gauge("airpods_bluetooth_connected", "1 while the classic Bluetooth link (which carries the audio) is connected.", r.Link == "Connected" ? 1 : 0);
                m.Gauge("airpods_output_present", "1 while the AirPods stereo output exists in Windows.", r.Endpoint.Output == "Active" ? 1 : 0);
                m.Gauge("airpods_output_is_default", "1 while the AirPods stereo output is the Windows default output.", r.Endpoint.IsDefault ? 1 : 0);
                m.Gauge("airpods_hands_free_enabled", "1 while the Hands-Free (call mode) devices are enabled in Windows.", r.Endpoint.HandsFree == "standby" || r.Endpoint.CallMode ? 1 : 0);
                m.Gauge("airpods_call_mode", "1 while an app has the AirPods mic open, which forces low-quality call mode.", r.Endpoint.CallMode ? 1 : 0);
                m.Gauge("airpods_audio_sending", "1 if Windows sent any audio to the AirPods in the last second.", r.HasAudio ? 1 : 0);
                m.Gauge("airpods_audio_coverage_ratio", "Audio sent in the last second relative to real time (1 = continuous).", r.HasAudio ? r.CoveragePct / 100 : double.NaN);
                m.Gauge("airpods_audio_peak_dbfs", "Peak level of the audio sent in the last second (-120 = digital silence).", r.HasAudio ? r.PeakDbfs : double.NaN);
                m.Gauge("airpods_audio_rms_dbfs", "RMS level of the audio sent in the last second (-120 = digital silence).", r.HasAudio ? r.RmsDbfs : double.NaN);
                if (r.SampleRate > 0) m.Gauge("airpods_audio_sample_rate_hertz", "Sample rate of the AirPods output in Windows.", r.SampleRate);
                if (r.Endpoint.Volume is double volume) m.Gauge("airpods_output_volume_ratio", "The AirPods output's volume slider in Windows (0-1).", volume);
                if (r.Endpoint.Muted is bool muted) m.Gauge("airpods_output_muted", "1 while the AirPods output is muted in Windows.", muted ? 1 : 0);
                foreach (var app in r.Endpoint.AppAudio)
                    m.Gauge("airpods_app_sending", "1 for each app with an active audio stream to the AirPods.", 1, ("app", app.App));
                foreach (var app in r.Endpoint.AppAudio)
                    m.Gauge("airpods_app_volume_ratio", "Each sending app's volume slider in the Windows mixer (0-1).", app.Volume, ("app", app.App));
                foreach (var app in r.Endpoint.AppAudio)
                    m.Gauge("airpods_app_muted", "1 while a sending app is muted in the Windows mixer (dictation tools do this to other apps).", app.Muted ? 1 : 0, ("app", app.App));
                foreach (var app in r.Endpoint.AppAudio)
                    m.Gauge("airpods_app_peak_dbfs", "Each sending app's current peak level after its mixer volume (-120 = silent).", app.Peak > 0 ? Math.Max(20 * Math.Log10(app.Peak), -120) : -120, ("app", app.App));

                RenderPc(m, r.Pc);
            }

            bool fresh = _rssi is not null && DateTime.Now - _rssiTime < SignalStaleAfter;
            m.Gauge("airpods_signal_rssi_dbm", "Strength of the AirPods' Bluetooth broadcasts as heard by the PC (closer to 0 is stronger).", fresh ? _rssi!.Value : double.NaN);
            if (_rssi is not null) m.Gauge("airpods_signal_age_seconds", "Seconds since an AirPods broadcast was last heard.", (DateTime.Now - _rssiTime).TotalSeconds);
            if (_battery is int battery) m.Gauge("airpods_battery_percent", "Battery level from the AirPods' broadcasts (10% steps).", battery);
            if (fresh && _status is byte status)
            {
                m.Gauge("airpods_worn", "1 while the AirPods report being worn (the in-ear flag of their broadcasts).", BleSecond.IsWorn(status) == true ? 1 : 0);
                m.Gauge("airpods_ble_status", "Raw status byte of the AirPods' broadcasts, for decoding other flags later.", status);
            }

            m.Counter("airpods_audio_seconds_total", "Seconds of audio Windows has sent to the AirPods.", _audioSeconds);
            m.Counter("airpods_audio_gap_seconds_total", "Seconds missing from the audio stream, including normal pauses in playback.", _gapSeconds);
            m.Counter("airpods_audio_glitches_total", "Windows audio-engine glitches (data discontinuities) in the audio sent.", _glitches);
            m.Counter("airpods_audio_clicks_total", "Click-like jumps detected in the audio sent (heuristic).", _clicks);
            m.Counter("airpods_audio_clipped_samples_total", "Samples sent at or above full scale.", _clipped);
            m.Counter("airpods_bluetooth_link_changes_total", "Times the Bluetooth link connected or disconnected.", _linkChanges);
            m.Counter("airpods_ble_broadcasts_total", "AirPods Bluetooth broadcasts heard.", _broadcasts);
            m.Histogram("airpods_broadcast_rssi_dbm", "Signal strength of every AirPods broadcast heard, so short dips between scrapes are kept.",
                BleSecond.RssiBounds, _rssiBuckets, _rssiSum);

            if (_loki is not null)
            {
                m.Gauge("airpods_loki_queued_lines", "Events waiting to be sent to Loki.", _loki.Queued);
                m.Counter("airpods_loki_push_failures_total", "Failed attempts to send events to Loki.", _loki.Failures);
                m.Counter("airpods_loki_dropped_lines_total", "Events dropped because Loki could not be reached for too long.", _loki.Dropped);
            }
        }
        return m.ToString();
    }

    static void RenderPc(MetricWriter m, PcSample pc)
    {
        if (pc.Display != DisplayState.Unknown)
            m.Gauge("airpods_pc_display_state", "The PC's display: 0 = off, 1 = on, 2 = dimmed.", (int)pc.Display);
        if (!double.IsNaN(pc.IdleSeconds))
            m.Gauge("airpods_pc_idle_seconds", "Seconds since the last keyboard or mouse input at the PC.", pc.IdleSeconds);

        const string cpuHelp = "Time each CPU spent per mode. DPC and interrupt time is driver work that can delay Bluetooth audio.";
        for (int i = 0; i < pc.Cpus.Length; i++)
        {
            var cpu = pc.Cpus[i];
            var core = i.ToString(CultureInfo.InvariantCulture);
            m.Counter("airpods_pc_cpu_seconds_total", cpuHelp, cpu.Idle, ("cpu", core), ("mode", "idle"));
            m.Counter("airpods_pc_cpu_seconds_total", cpuHelp, cpu.User, ("cpu", core), ("mode", "user"));
            m.Counter("airpods_pc_cpu_seconds_total", cpuHelp, cpu.System, ("cpu", core), ("mode", "system"));
            m.Counter("airpods_pc_cpu_seconds_total", cpuHelp, cpu.Dpc, ("cpu", core), ("mode", "dpc"));
            m.Counter("airpods_pc_cpu_seconds_total", cpuHelp, cpu.Interrupt, ("cpu", core), ("mode", "interrupt"));
        }

        m.Gauge("airpods_pc_wifi_connected", "1 while the PC is connected to Wi-Fi.", pc.Wifi is null ? 0 : 1);
        if (pc.Wifi is { } wifi)
        {
            m.Gauge("airpods_pc_wifi_info", "The Wi-Fi network and access point the PC is connected to.", 1, ("ssid", wifi.Ssid), ("bssid", wifi.Bssid), ("band", wifi.Band));
            m.Gauge("airpods_pc_wifi_channel", "Wi-Fi channel; 1-14 share the 2.4 GHz band with Bluetooth.", wifi.Channel);
            m.Gauge("airpods_pc_wifi_signal_percent", "Wi-Fi signal quality as Windows reports it.", wifi.SignalPercent);
            m.Gauge("airpods_pc_wifi_receive_rate_mbps", "Wi-Fi link rate for receiving.", wifi.ReceiveMbps);
            m.Gauge("airpods_pc_wifi_transmit_rate_mbps", "Wi-Fi link rate for sending.", wifi.TransmitMbps);
        }
        const string netHelp = "Bytes moved by each network adapter. Heavy Wi-Fi traffic competes with Bluetooth on combo cards.";
        foreach (var nic in pc.Network)
        {
            m.Counter("airpods_pc_network_bytes_total", netHelp, nic.Received, ("interface", nic.Interface), ("type", nic.Type), ("direction", "received"));
            m.Counter("airpods_pc_network_bytes_total", netHelp, nic.Sent, ("interface", nic.Interface), ("type", nic.Type), ("direction", "sent"));
        }

        foreach (var device in pc.Bluetooth.DistinctBy(d => (d.Name, d.Transport)))
            m.Gauge("airpods_pc_bluetooth_device_connected", "1 for each paired Bluetooth device that is connected to the PC.", device.Connected ? 1 : 0,
                ("device", device.Name), ("transport", device.Transport));
    }

    async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                int read = 0;
                while (read < buffer.Length && !HeaderComplete(buffer, read))
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                    if (n == 0) break;
                    read += n;
                }
                var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0].Split(' ');
                bool isGet = requestLine.Length >= 2 && requestLine[0] is "GET" or "HEAD";
                bool found = isGet && (requestLine[1] == "/metrics" || requestLine[1].StartsWith("/metrics?", StringComparison.Ordinal));
                var body = Encoding.UTF8.GetBytes(found ? Render() : "Not found. Metrics are at /metrics\n");
                var header = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\n"
                    + $"Content-Type: {(found ? "text/plain; version=0.0.4; charset=utf-8" : "text/plain; charset=utf-8")}\r\n"
                    + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token);
                if (requestLine[0] != "HEAD") await stream.WriteAsync(body, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // Client went away or was too slow; nothing to report.
            }
        }
    }

    static bool HeaderComplete(byte[] buffer, int length) =>
        length >= 4 && buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0;
}

sealed class MetricWriter
{
    readonly StringBuilder _text = new();
    readonly HashSet<string> _described = [];

    public void Gauge(string name, string help, double value, params (string Name, string Value)[] labels) => Write(name, "gauge", help, value, labels);

    public void Counter(string name, string help, double value, params (string Name, string Value)[] labels) => Write(name, "counter", help, value, labels);

    /// <summary>Writes a histogram from per-bucket (not yet cumulative) counts; the last count is everything above the last bound.</summary>
    public void Histogram(string name, string help, IReadOnlyList<int> bounds, IReadOnlyList<long> counts, double sum)
    {
        _text.Append($"# HELP {name} {help}\n# TYPE {name} histogram\n");
        long cumulative = 0;
        for (int i = 0; i < bounds.Count; i++)
        {
            cumulative += counts[i];
            _text.Append($"{name}_bucket{{le=\"{bounds[i].ToString(CultureInfo.InvariantCulture)}\"}} {cumulative}\n");
        }
        cumulative += counts[bounds.Count];
        _text.Append($"{name}_bucket{{le=\"+Inf\"}} {cumulative}\n{name}_sum {Format(sum)}\n{name}_count {cumulative}\n");
    }

    public override string ToString() => _text.ToString();

    void Write(string name, string type, string help, double value, (string Name, string Value)[] labels)
    {
        if (_described.Add(name)) _text.Append($"# HELP {name} {help}\n# TYPE {name} {type}\n");
        _text.Append(name);
        if (labels.Length > 0) _text.Append('{').AppendJoin(',', labels.Select(l => $"{l.Name}=\"{Escape(l.Value)}\"")).Append('}');
        _text.Append(' ').Append(Format(value)).Append('\n');
    }

    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    static string Format(double value) => double.IsNaN(value) ? "NaN" : value.ToString("R", CultureInfo.InvariantCulture);
}

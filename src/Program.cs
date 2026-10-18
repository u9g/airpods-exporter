using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace AirPodsExporter;

static class Program
{
    static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }
        if (options.Help)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        Console.OutputEncoding = Encoding.UTF8;
        // When serving metrics the monitor runs indefinitely, so the CSV becomes opt-in.
        var csvPath = options.CsvPath ?? (options.MetricsPort > 0 ? null : $"airpods-exporter-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        using var loki = options.LokiUrl is null ? null : new LokiPusher(options.LokiUrl);
        using var output = new Output(csvPath is null ? null : Path.ChangeExtension(csvPath, ".log"), loki);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        if (options.Seconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(options.Seconds));

        output.Line($"Watching \"{options.Name}\" (BLE model 0x{options.Model:X4}).{(csvPath is null ? "" : $" Per-second data: {Path.GetFullPath(csvPath)}")}");
        PrometheusExporter? exporter = null;
        if (options.MetricsPort > 0)
        {
            try
            {
                exporter = new PrometheusExporter(options.MetricsPort, options.Name, options.Model, loki);
            }
            catch (SocketException ex)
            {
                output.Event($"metrics: cannot listen on 127.0.0.1:{options.MetricsPort}: {ex.Message}");
                return 1;
            }
            output.Event($"metrics: serving http://127.0.0.1:{options.MetricsPort}/metrics");
        }
        if (loki is not null) output.Event($"events: sending to Loki at {options.LokiUrl}");
        using var exporterScope = exporter;

        var summary = new Summary();
        var classic = ClassicMonitor.Find(options.Name, text =>
        {
            summary.LinkChanged();
            output.Event(text);
        }, output.Event);
        using var ble = new BleMonitor(options.Model, output.Event);
        using var capture = new LoopbackCapture(options.Name, options.WavPath, output.Event);
        var probe = new EndpointProbe(options.Name, output.Event);
        using var pc = new PcMonitor(options.Name, output.Event);
        ble.Start();
        capture.Start();
        pc.Start();

        using var csv = csvPath is null ? null : new StreamWriter(csvPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        csv?.WriteLine(Row.CsvHeader);

        while (!cts.Token.WaitHandle.WaitOne(1000 - DateTime.Now.Millisecond))
        {
            var row = new Row(DateTime.Now, classic.Status, probe.Sample(), capture.Swap(), capture.SampleRate, ble.Swap(), pc.Sample());
            summary.Add(row);
            exporter?.Update(row, summary.LinkChanges);
            if (!options.Quiet) output.Line(row.ToConsole());
            csv?.WriteLine(row.ToCsv());
        }

        summary.Print(output, ble, options);
        return 0;
    }
}

sealed class Options
{
    public const string Usage = """
        Usage: AirPodsExporter [options]
          --name <text>    Match audio/Bluetooth devices whose name contains this (default "AirPods Max")
          --model <hex>    Apple model ID to read battery and signal from BLE broadcasts (default 202D)
          --seconds <n>    Stop after n seconds (default: run until Ctrl+C)
          --csv <path>     Per-second CSV (default airpods-exporter-<time>.csv, or none with --metrics-port);
                           a .log is written next to it
          --wav <path>     Also save the exact audio sent to the AirPods as a 32-bit float WAV
          --metrics-port <port>  Serve Prometheus metrics on http://127.0.0.1:<port>/metrics
          --loki-url <url> Also send events to Loki at this base URL, e.g. http://127.0.0.1:3100
          --quiet          Don't print a line every second (events and the summary are still printed)
        """;

    public string Name { get; private set; } = "AirPods Max";
    public ushort Model { get; private set; } = 0x202D;
    public int Seconds { get; private set; }
    public string? CsvPath { get; private set; }
    public string? WavPath { get; private set; }
    public int MetricsPort { get; private set; }
    public string? LokiUrl { get; private set; }
    public bool Quiet { get; private set; }
    public bool Help { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--name": options.Name = Next(); break;
                case "--model": options.Model = ushort.Parse(Next().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber); break;
                case "--seconds": options.Seconds = int.Parse(Next()); break;
                case "--csv": options.CsvPath = Next(); break;
                case "--wav": options.WavPath = Next(); break;
                case "--metrics-port": options.MetricsPort = ushort.Parse(Next()); break;
                case "--loki-url": options.LokiUrl = Uri.TryCreate(Next(), UriKind.Absolute, out var url) ? url.ToString() : throw new ArgumentException("--loki-url needs an absolute URL"); break;
                case "--quiet": options.Quiet = true; break;
                case "-h" or "--help": options.Help = true; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return options;
    }
}

sealed class Output(string? logPath, LokiPusher? loki) : IDisposable
{
    readonly StreamWriter? _file = logPath is null ? null : new(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
    readonly object _lock = new();
    bool _disposed;

    public void Line(string text)
    {
        lock (_lock)
        {
            if (_disposed) return;
            Console.WriteLine(text);
            _file?.WriteLine(text);
        }
    }

    public void Event(string text)
    {
        var now = DateTime.Now;
        Line($"{now:yyyy-MM-dd HH:mm:ss.fff}  ! {text}");
        loki?.Push(now, text);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _file?.Dispose();
        }
    }
}

/// <summary>One app's streams to the AirPods: its volume slider in the Windows mixer, whether it is muted there, and its current peak level.</summary>
sealed record AppAudio(string App, double Volume, bool Muted, double Peak);

sealed record EndpointSample(string Output, bool IsDefault, string Default, string HandsFree, AppAudio[] AppAudio, double? Volume, bool? Muted)
{
    public string Apps => string.Join("+", AppAudio.Select(a => a.App));
    public bool CallMode => HandsFree.StartsWith("IN USE", StringComparison.Ordinal);
}

/// <summary>
/// Samples the AirPods' Windows audio endpoints: presence, default status, call mode, the output's volume and mute,
/// and which apps are sending (with each app's own volume, mute and level, since apps like dictation tools mute others).
/// </summary>
sealed class EndpointProbe(string filter, Action<string> onEvent)
{
    const double VolumeChangeToReport = 0.1;

    readonly IMMDeviceEnumerator _enumerator = CoreAudio.CreateEnumerator();
    readonly Dictionary<uint, string> _processNames = [];
    EndpointSample? _last;
    IAudioEndpointVolume? _volume;
    string? _volumeId;

    public EndpointSample Sample()
    {
        EndpointSample sample;
        try
        {
            sample = Read();
        }
        catch (COMException ex)
        {
            sample = new EndpointSample("error", false, "?", "?", [], null, null);
            onEvent($"endpoint check failed: {ex.Message}");
        }
        ReportChanges(sample);
        _last = sample;
        return sample;
    }

    EndpointSample Read()
    {
        var render = CoreAudio.List(_enumerator, DataFlow.Render);
        var capture = CoreAudio.List(_enumerator, DataFlow.Capture);
        bool Matches(Endpoint e) => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var stereo = render.Where(e => Matches(e) && !CoreAudio.IsHandsFree(e))
            .OrderBy(e => e.State == CoreAudio.StateActive ? 0 : 1)
            .FirstOrDefault();
        var defaultId = CoreAudio.DefaultRenderId(_enumerator);

        var handsFree = render.Concat(capture).Where(e => Matches(e) && CoreAudio.IsHandsFree(e)).ToList();
        string handsFreeState;
        if (handsFree.Count == 0) handsFreeState = "none";
        else if (handsFree.All(e => e.State != CoreAudio.StateActive)) handsFreeState = "off";
        else
        {
            var users = handsFree.Where(e => e.State == CoreAudio.StateActive)
                .Select(e => string.Join("+", ActiveApps(e.Device).Select(a => a.App))).Where(a => a.Length > 0).Distinct().ToList();
            handsFreeState = users.Count == 0 ? "standby" : $"IN USE by {string.Join("+", users)}";
        }

        bool active = stereo is { State: CoreAudio.StateActive };
        var (volume, muted) = active ? OutputVolume(stereo!) : (null, null);
        return new EndpointSample(
            stereo is null ? "missing" : CoreAudio.StateName(stereo.State),
            stereo is not null && stereo.Id == defaultId,
            render.FirstOrDefault(e => e.Id == defaultId)?.Name ?? "none",
            handsFreeState,
            active ? ActiveApps(stereo!.Device) : [],
            volume,
            muted);
    }

    AppAudio[] ActiveApps(IMMDevice device)
    {
        var manager = CoreAudio.Activate<IAudioSessionManager2>(device);
        CoreAudio.Check(manager.GetSessionEnumerator(out var sessions), "GetSessionEnumerator");
        CoreAudio.Check(sessions.GetCount(out var count), "IAudioSessionEnumerator.GetCount");
        var apps = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            if (sessions.GetSession(i, out var session) < 0) continue;
            if (session.GetState(out var state) < 0 || state != 1) continue; // 1 = AudioSessionStateActive
            session.GetProcessId(out var pid);
            if (pid == Environment.ProcessId) continue; // our own loopback tap shows up as a session
            var name = ProcessName(pid);
            float volume = 1, peak = 0;
            bool muted = false;
            if (session is ISimpleAudioVolume simple)
            {
                if (simple.GetMasterVolume(out var level) >= 0) volume = level;
                if (simple.GetMute(out var mute) >= 0) muted = mute;
            }
            if (session is IAudioMeterInformation meter && meter.GetPeakValue(out var value) >= 0) peak = value;
            // An app can have several streams; it only counts as muted if all of them are.
            apps[name] = apps.TryGetValue(name, out var other)
                ? new AppAudio(name, Math.Max(other.Volume, volume), other.Muted && muted, Math.Max(other.Peak, peak))
                : new AppAudio(name, volume, muted, peak);
        }
        return apps.Values.OrderBy(a => a.App, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    (double?, bool?) OutputVolume(Endpoint endpoint)
    {
        try
        {
            if (_volumeId != endpoint.Id)
            {
                _volume = CoreAudio.Activate<IAudioEndpointVolume>(endpoint.Device);
                _volumeId = endpoint.Id;
            }
            if (_volume!.GetMasterVolumeLevelScalar(out var level) >= 0 && _volume.GetMute(out var muted) >= 0) return (level, muted);
        }
        catch (COMException)
        {
        }
        _volumeId = null; // the device was reset or went away; activate it again next time
        return (null, null);
    }

    string ProcessName(uint pid)
    {
        if (pid == 0) return "system";
        if (_processNames.TryGetValue(pid, out var name)) return name;
        try
        {
            name = Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            name = $"pid{pid}";
        }
        return _processNames[pid] = name;
    }

    void ReportChanges(EndpointSample now)
    {
        if (_last is null)
        {
            onEvent($"AirPods output {now.Output}{VolumeText(now)}; default output is {now.Default}; Hands-Free (call mode) {now.HandsFree}");
            return;
        }
        if (now.Output != _last.Output) onEvent($"AirPods output: {_last.Output} -> {now.Output}");
        if (now.Default != _last.Default) onEvent($"default output: {_last.Default} -> {now.Default}");
        if (now.HandsFree != _last.HandsFree) onEvent($"Hands-Free (call mode): {_last.HandsFree} -> {now.HandsFree}");
        if (now.Muted is bool muted && _last.Muted is bool wasMuted && muted != wasMuted)
            onEvent($"AirPods output {(muted ? "muted" : "unmuted")} in Windows");
        if (now.Volume is double volume && _last.Volume is double was && Math.Abs(volume - was) >= VolumeChangeToReport)
            onEvent($"AirPods output volume: {Percent(was)} -> {Percent(volume)}");
        if (now.Apps != _last.Apps) onEvent($"apps sending to AirPods: {(now.Apps.Length == 0 ? "none" : now.Apps)}");
        foreach (var app in now.AppAudio)
        {
            var before = _last.AppAudio.FirstOrDefault(a => a.App == app.App);
            if (before is null) continue;
            if (app.Muted != before.Muted) onEvent($"{app.App} {(app.Muted ? "muted" : "unmuted")} in the Windows mixer");
            if (Math.Abs(app.Volume - before.Volume) >= VolumeChangeToReport) onEvent($"{app.App} volume in the Windows mixer: {Percent(before.Volume)} -> {Percent(app.Volume)}");
        }
    }

    static string VolumeText(EndpointSample sample) =>
        sample.Volume is double volume ? $" (volume {Percent(volume)}{(sample.Muted == true ? ", muted" : "")})" : "";

    static string Percent(double ratio) => $"{ratio * 100:0}%";
}

sealed record Row(DateTime Time, string Link, EndpointSample Endpoint, AudioSecond Audio, int SampleRate, BleSecond Ble, PcSample Pc)
{
    public const string CsvHeader = "time,link,output,is_default,hands_free,apps,coverage_pct,peak_dbfs,rms_dbfs,clipped,clicks,glitches,gap_ms,ble_adverts,ble_rssi_dbm,ble_battery,ble_raw,"
        + "worn,output_volume_pct,output_muted,muted_apps,display,pc_idle_s,cpu_busy_pct,cpu_dpc_pct,cpu_interrupt_pct,wifi_channel,wifi_signal_pct,network_kbps,bluetooth_connected";

    public bool HasAudio => Audio.Frames > 0;
    public double CoveragePct => SampleRate > 0 ? 100.0 * Audio.Frames / SampleRate : 0;
    public double GapMs => SampleRate > 0 ? 1000.0 * Audio.GapFrames / SampleRate : 0;
    public double PeakDbfs => Math.Max(PeakDb, SilenceDb);
    public double RmsDbfs => Math.Max(RmsDb, SilenceDb);
    double PeakDb => ToDb(Audio.Peak);
    double RmsDb => Audio.Samples > 0 ? ToDb(Math.Sqrt(Audio.SumSquares / Audio.Samples)) : double.NegativeInfinity;

    const double SilenceDb = -120;

    public static int? BatteryPercent(string? battery) =>
        battery is not null && int.TryParse(battery.Split('%')[0], out var percent) ? percent : null;

    public string ToConsole()
    {
        var line = new StringBuilder($"{Time:HH:mm:ss}  link {Link,-12} out {Endpoint.Output}{(Endpoint.IsDefault ? "" : " (NOT default)")}");
        if (!HasAudio) line.Append("  sent: nothing");
        else if (Audio.Peak == 0) line.Append($"  sent: {CoveragePct,3:0}% digital silence");
        else line.Append($"  sent: {CoveragePct,3:0}%  peak {Db(PeakDb),5} rms {Db(RmsDb),5} dBFS  clip {Audio.Clipped} click {Audio.Clicks} glitch {Audio.Discontinuities} gap {GapMs:0}ms");
        if (Ble.AvgRssi is int rssi) line.Append($"  signal {rssi} dBm, battery {Ble.Battery}{(Ble.Worn is bool worn ? worn ? ", worn" : ", not worn" : "")}");
        if (Endpoint.Muted == true) line.Append("  OUTPUT MUTED");
        if (Endpoint.Apps.Length > 0) line.Append($"  [{string.Join("+", Endpoint.AppAudio.Select(a => a.Muted ? a.App + " (muted)" : a.App))}]");
        if (Endpoint.CallMode) line.Append($"  CALL MODE {Endpoint.HandsFree}");
        return line.ToString();
    }

    public string ToCsv() => string.Join(",",
        Time.ToString("HH:mm:ss"), Link, Endpoint.Output, Endpoint.IsDefault, Endpoint.HandsFree, Endpoint.Apps,
        CoveragePct.ToString("0"), Db(PeakDb), Db(RmsDb), Audio.Clipped, Audio.Clicks, Audio.Discontinuities, GapMs.ToString("0"),
        Ble.Adverts, Ble.AvgRssi?.ToString() ?? "", Ble.Battery ?? "", Ble.Raw ?? "",
        Ble.Worn?.ToString() ?? "", Endpoint.Volume is double v ? (v * 100).ToString("0") : "", Endpoint.Muted?.ToString() ?? "",
        string.Join("+", Endpoint.AppAudio.Where(a => a.Muted).Select(a => a.App)),
        Pc.Display.ToString(), Pc.IdleSeconds.ToString("0"),
        Pc.Cpu is { } cpu ? (cpu.Busy * 100).ToString("0.0") : "", Pc.Cpu is { } c1 ? (c1.Dpc * 100).ToString("0.0") : "", Pc.Cpu is { } c2 ? (c2.Interrupt * 100).ToString("0.0") : "",
        Pc.Wifi?.Channel.ToString() ?? "", Pc.Wifi?.SignalPercent.ToString() ?? "", (Pc.NetworkBytesPerSecond * 8 / 1000).ToString("0"),
        string.Join("+", Pc.Bluetooth.Where(d => d.Connected).Select(d => d.Name)));

    static double ToDb(double value) => value > 0 ? 20 * Math.Log10(value) : double.NegativeInfinity;

    static string Db(double db) => double.IsNegativeInfinity(db) ? "-inf" : db.ToString("0.0");
}

sealed class Summary
{
    int _linkChanges;
    int _seconds, _audioSeconds, _disconnected, _outputMissing, _notDefault, _callMode, _lowCoverage;
    long _clicks, _clipped, _glitches;
    int _rssiCount, _rssiMin = int.MaxValue, _rssiMax = int.MinValue;
    long _rssiSum;
    string? _battery;

    public int LinkChanges => Volatile.Read(ref _linkChanges);

    double RssiAverage => (double)_rssiSum / _rssiCount;

    bool FrequentClicks => _clicks > Math.Max(1, _audioSeconds / 5);

    public void LinkChanged() => Interlocked.Increment(ref _linkChanges);

    public void Add(Row row)
    {
        _seconds++;
        if (row.Link == "Disconnected") _disconnected++;
        if (row.Endpoint.Output != "Active") _outputMissing++;
        else if (!row.Endpoint.IsDefault) _notDefault++;
        if (row.Endpoint.CallMode) _callMode++;
        if (row.HasAudio)
        {
            _audioSeconds++;
            if (row.CoveragePct < 90) _lowCoverage++;
            _clicks += row.Audio.Clicks;
            _clipped += row.Audio.Clipped;
            _glitches += row.Audio.Discontinuities;
        }
        if (row.Ble.AvgRssi is int rssi)
        {
            _rssiCount++;
            _rssiSum += rssi;
            _rssiMin = Math.Min(_rssiMin, rssi);
            _rssiMax = Math.Max(_rssiMax, rssi);
        }
        if (row.Ble.Battery is not null) _battery = row.Ble.Battery;
    }

    public void Print(Output output, BleMonitor ble, Options options)
    {
        output.Line("");
        output.Line($"=== Summary of {_seconds}s ===");
        output.Line($"Bluetooth link: {(_disconnected == 0 ? "never reported disconnected" : $"DISCONNECTED for {_disconnected}s")}, {LinkChanges} status change(s)");
        output.Line($"AirPods output in Windows: {(_outputMissing == 0 ? "present the whole time" : $"missing for {_outputMissing}s")}{(_notDefault > 0 ? $", not the default output for {_notDefault}s" : "")}");
        output.Line($"Call mode (Hands-Free mic in use): {(_callMode == 0 ? "never" : $"{_callMode}s")}");
        output.Line(_audioSeconds == 0
            ? "Audio sent: nothing was sent to the AirPods"
            : $"Audio sent during {_audioSeconds}s: {_glitches} Windows audio-engine glitch(es), {_clicks} click-like jump(s), {_clipped} clipped sample(s), {_lowCoverage}s with under 90% of the expected audio");
        output.Line(_rssiCount == 0
            ? $"AirPods broadcasts: none heard for model 0x{options.Model:X4} (other Apple models heard: {ble.OtherModelsText()})"
            : $"AirPods signal: average {RssiAverage:0} dBm (worst {_rssiMin}, best {_rssiMax}) across {_rssiCount}s; battery {_battery ?? "unknown"}");
        output.Line("");
        foreach (var hint in Hints()) output.Line("-> " + hint);
    }

    IEnumerable<string> Hints()
    {
        if (_disconnected > 0 || LinkChanges > 0) yield return "The Bluetooth link dropped. That alone explains audio cutting out.";
        if (_callMode > 0) yield return "An app opened the AirPods mic, which forces low-quality call mode.";
        if (_glitches > 0) yield return "Windows' audio engine glitched while sending. That is a PC-side problem (CPU, driver), not the radio.";
        if (FrequentClicks) yield return "Frequent click-like jumps were already in the digital audio, so they came from an app or Windows, not Bluetooth. (Occasional ones are usually just sharp sounds in the music.)";
        if (_rssiCount > 0 && RssiAverage < -75) yield return "The AirPods' signal at the PC is weak for headphones this close. Check the PC's antennas, move closer, or clear the path between them.";
        if (Row.BatteryPercent(_battery) <= 20) yield return "The AirPods' battery is low.";
        if (_audioSeconds > 0 && _glitches == 0 && !FrequentClicks && _callMode == 0 && _disconnected == 0)
            yield return "The audio Windows sent was clean. Any crackle or stutter you heard was added over Bluetooth or inside the headphones.";
    }
}

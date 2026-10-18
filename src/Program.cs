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
        using var output = new Output(csvPath is null ? null : Path.ChangeExtension(csvPath, ".log"));
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
                exporter = new PrometheusExporter(options.MetricsPort, options.Name, options.Model);
            }
            catch (SocketException ex)
            {
                output.Event($"metrics: cannot listen on 127.0.0.1:{options.MetricsPort}: {ex.Message}");
                return 1;
            }
            output.Event($"metrics: serving http://127.0.0.1:{options.MetricsPort}/metrics");
        }
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
        ble.Start();
        capture.Start();

        using var csv = csvPath is null ? null : new StreamWriter(csvPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        csv?.WriteLine(Row.CsvHeader);

        while (!cts.Token.WaitHandle.WaitOne(1000 - DateTime.Now.Millisecond))
        {
            var row = new Row(DateTime.Now, classic.Status, probe.Sample(), capture.Swap(), capture.SampleRate, ble.Swap());
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
          --quiet          Don't print a line every second (events and the summary are still printed)
        """;

    public string Name { get; private set; } = "AirPods Max";
    public ushort Model { get; private set; } = 0x202D;
    public int Seconds { get; private set; }
    public string? CsvPath { get; private set; }
    public string? WavPath { get; private set; }
    public int MetricsPort { get; private set; }
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
                case "--quiet": options.Quiet = true; break;
                case "-h" or "--help": options.Help = true; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return options;
    }
}

sealed class Output(string? logPath) : IDisposable
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

    public void Event(string text) => Line($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  ! {text}");

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _file?.Dispose();
        }
    }
}

sealed record EndpointSample(string Output, bool IsDefault, string Default, string HandsFree, string Apps)
{
    public bool CallMode => HandsFree.StartsWith("IN USE", StringComparison.Ordinal);
}

/// <summary>Samples the AirPods' Windows audio endpoints: presence, default status, call mode, and which apps are sending.</summary>
sealed class EndpointProbe(string filter, Action<string> onEvent)
{
    readonly IMMDeviceEnumerator _enumerator = CoreAudio.CreateEnumerator();
    readonly Dictionary<uint, string> _processNames = [];
    EndpointSample? _last;

    public EndpointSample Sample()
    {
        EndpointSample sample;
        try
        {
            sample = Read();
        }
        catch (COMException ex)
        {
            sample = new EndpointSample("error", false, "?", "?", "");
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
            var users = handsFree.Where(e => e.State == CoreAudio.StateActive).Select(e => ActiveApps(e.Device)).Where(a => a.Length > 0).Distinct().ToList();
            handsFreeState = users.Count == 0 ? "standby" : $"IN USE by {string.Join("+", users)}";
        }

        return new EndpointSample(
            stereo is null ? "missing" : CoreAudio.StateName(stereo.State),
            stereo is not null && stereo.Id == defaultId,
            render.FirstOrDefault(e => e.Id == defaultId)?.Name ?? "none",
            handsFreeState,
            stereo is { State: CoreAudio.StateActive } ? ActiveApps(stereo.Device) : "");
    }

    string ActiveApps(IMMDevice device)
    {
        var manager = CoreAudio.Activate<IAudioSessionManager2>(device);
        CoreAudio.Check(manager.GetSessionEnumerator(out var sessions), "GetSessionEnumerator");
        CoreAudio.Check(sessions.GetCount(out var count), "IAudioSessionEnumerator.GetCount");
        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            if (sessions.GetSession(i, out var session) < 0) continue;
            if (session.GetState(out var state) < 0 || state != 1) continue; // 1 = AudioSessionStateActive
            session.GetProcessId(out var pid);
            if (pid == Environment.ProcessId) continue; // our own loopback tap shows up as a session
            apps.Add(ProcessName(pid));
        }
        return string.Join("+", apps);
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
            onEvent($"AirPods output {now.Output}; default output is {now.Default}; Hands-Free (call mode) {now.HandsFree}");
            return;
        }
        if (now.Output != _last.Output) onEvent($"AirPods output: {_last.Output} -> {now.Output}");
        if (now.Default != _last.Default) onEvent($"default output: {_last.Default} -> {now.Default}");
        if (now.HandsFree != _last.HandsFree) onEvent($"Hands-Free (call mode): {_last.HandsFree} -> {now.HandsFree}");
        if (now.Apps != _last.Apps) onEvent($"apps sending to AirPods: {(now.Apps.Length == 0 ? "none" : now.Apps)}");
    }
}

sealed record Row(DateTime Time, string Link, EndpointSample Endpoint, AudioSecond Audio, int SampleRate, BleSecond Ble)
{
    public const string CsvHeader = "time,link,output,is_default,hands_free,apps,coverage_pct,peak_dbfs,rms_dbfs,clipped,clicks,glitches,gap_ms,ble_adverts,ble_rssi_dbm,ble_battery,ble_raw";

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
        if (Ble.AvgRssi is int rssi) line.Append($"  signal {rssi} dBm, battery {Ble.Battery}");
        if (Endpoint.Apps.Length > 0) line.Append($"  [{Endpoint.Apps}]");
        if (Endpoint.CallMode) line.Append($"  CALL MODE {Endpoint.HandsFree}");
        return line.ToString();
    }

    public string ToCsv() => string.Join(",",
        Time.ToString("HH:mm:ss"), Link, Endpoint.Output, Endpoint.IsDefault, Endpoint.HandsFree, Endpoint.Apps,
        CoveragePct.ToString("0"), Db(PeakDb), Db(RmsDb), Audio.Clipped, Audio.Clicks, Audio.Discontinuities, GapMs.ToString("0"),
        Ble.Adverts, Ble.AvgRssi?.ToString() ?? "", Ble.Battery ?? "", Ble.Raw ?? "");

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

using System.Text;
using System.Text.Json;

namespace AirPodsExporter;

/// <summary>
/// Sends the monitor's events to Loki once a second, so Grafana can show them as annotations next to the metrics.
/// Lines that cannot be delivered are kept and retried, up to a limit.
/// </summary>
sealed class LokiPusher : IDisposable
{
    const int MaxQueued = 10_000;
    const int MaxBatch = 1_000;

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    readonly Uri _pushUri;
    readonly object _lock = new();
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    List<(DateTimeOffset Time, string Line)> _queue = [];
    long _failures, _dropped;

    public LokiPusher(string baseUrl)
    {
        _pushUri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "loki/api/v1/push");
        _loop = RunAsync();
    }

    public long Failures => Interlocked.Read(ref _failures);
    public long Dropped => Interlocked.Read(ref _dropped);

    public int Queued
    {
        get
        {
            lock (_lock) return _queue.Count;
        }
    }

    public void Push(DateTime time, string line)
    {
        lock (_lock)
        {
            if (_queue.Count >= MaxQueued)
            {
                _queue.RemoveAt(0);
                Interlocked.Increment(ref _dropped);
            }
            _queue.Add((time, line));
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
            FlushAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Best effort on the way out.
        }
        _http.Dispose();
    }

    async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token)) await FlushAsync(_stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task FlushAsync(CancellationToken token)
    {
        List<(DateTimeOffset Time, string Line)> batch;
        lock (_lock)
        {
            if (_queue.Count == 0) return;
            batch = _queue.Take(MaxBatch).ToList();
            _queue = _queue.Skip(batch.Count).ToList();
        }
        try
        {
            using var content = new ByteArrayContent(Payload(batch));
            content.Headers.ContentType = new("application/json");
            using var response = await _http.PostAsync(_pushUri, content, token);
            // 4xx means Loki rejected these lines (too old, malformed); retrying would not help.
            if ((int)response.StatusCode >= 500) throw new HttpRequestException($"Loki answered {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode) Interlocked.Increment(ref _failures);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Interlocked.Increment(ref _failures);
            lock (_lock)
            {
                int room = Math.Max(0, MaxQueued - _queue.Count);
                if (room < batch.Count) Interlocked.Add(ref _dropped, batch.Count - room);
                _queue.InsertRange(0, batch.Skip(batch.Count - Math.Min(room, batch.Count)));
            }
        }
    }

    static byte[] Payload(List<(DateTimeOffset Time, string Line)> lines)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("streams");
            json.WriteStartObject();
            json.WriteStartObject("stream");
            json.WriteString("job", "airpods");
            json.WriteString("service_name", "airpods-exporter");
            json.WriteEndObject();
            json.WriteStartArray("values");
            foreach (var (time, line) in lines)
            {
                json.WriteStartArray();
                json.WriteStringValue(((time.ToUnixTimeMilliseconds() * 1_000_000) + time.Ticks % TimeSpan.TicksPerMillisecond * 100).ToString());
                json.WriteStringValue(line);
                json.WriteEndArray();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }
}

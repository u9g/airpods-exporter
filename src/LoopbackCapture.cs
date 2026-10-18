using System.Runtime.InteropServices;

namespace AirPodsExporter;

sealed class AudioSecond
{
    public long Frames, Samples, GapFrames;
    public double SumSquares;
    public float Peak;
    public int Packets, SilentPackets, Discontinuities, Clipped, Clicks;
}

/// <summary>
/// Taps the exact mix Windows sends to the AirPods output (WASAPI loopback) and measures it:
/// level, clipping, click-like jumps, audio-engine glitches, and gaps in the stream.
/// </summary>
sealed class LoopbackCapture : IDisposable
{
    const float ClipLevel = 0.999f;
    const float ClickFloor = 0.05f;  // a jump must be at least this big...
    const float ClickRatio = 20f;    // ...and this many times the recent average...
    const int ClickSettleFrames = 10; // ...and the waveform must settle again within this many frames
    const float ClickSettleRatio = 0.25f;
    const int WarmupFrames = 64;     // ignore clicks right after the stream (re)starts

    readonly string _filter;
    readonly string? _wavPath;
    readonly Action<string> _event;
    readonly Thread _thread;
    readonly object _lock = new();
    volatile bool _stop;
    AudioSecond _current = new();
    WavWriter? _wav;

    float[] _prev1 = [], _prev2 = [], _avg = [], _pendingKink = [];
    int[] _pendingAge = [];
    long _frameClock, _lastClickFrame, _warmupUntil;
    ulong? _expectedPosition;

    public int SampleRate { get; private set; }

    public LoopbackCapture(string filter, string? wavPath, Action<string> onEvent)
    {
        _filter = filter;
        _wavPath = wavPath;
        _event = onEvent;
        _thread = new Thread(Run) { IsBackground = true, Name = "loopback", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    public AudioSecond Swap()
    {
        lock (_lock)
        {
            var finished = _current;
            _current = new AudioSecond();
            return finished;
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(2000);
        _wav?.Dispose();
    }

    void Run()
    {
        var enumerator = CoreAudio.CreateEnumerator();
        bool waiting = false;
        while (!_stop)
        {
            try
            {
                var endpoint = CoreAudio.List(enumerator, DataFlow.Render).FirstOrDefault(e =>
                    e.State == CoreAudio.StateActive
                    && e.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                    && !CoreAudio.IsHandsFree(e));
                if (endpoint is null)
                {
                    if (!waiting) _event($"capture: no active \"{_filter}\" output, waiting for it");
                    waiting = true;
                    Thread.Sleep(1000);
                    continue;
                }
                waiting = false;
                Capture(endpoint);
            }
            catch (Exception ex)
            {
                if (_stop) return;
                _event($"capture: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    void Capture(Endpoint endpoint)
    {
        var client = CoreAudio.Activate<IAudioClient>(endpoint.Device);
        CoreAudio.Check(client.GetMixFormat(out var formatPtr), "IAudioClient.GetMixFormat");
        try
        {
            var format = MixFormat.Read(formatPtr);
            CoreAudio.Check(client.Initialize(0, CoreAudio.StreamFlagsLoopback, 10_000_000, 0, formatPtr, IntPtr.Zero), "IAudioClient.Initialize");
            var iid = typeof(IAudioCaptureClient).GUID;
            CoreAudio.Check(client.GetService(ref iid, out var service), "IAudioClient.GetService");
            var capture = (IAudioCaptureClient)service;

            SampleRate = format.SampleRate;
            ResetDetector(format.Channels);
            _expectedPosition = null;
            if (_wavPath is not null) _wav ??= new WavWriter(_wavPath, format.SampleRate, format.Channels);

            CoreAudio.Check(client.Start(), "IAudioClient.Start");
            _event($"capture: measuring what Windows sends to {endpoint.Name} ({format})");
            try
            {
                var samples = Array.Empty<float>();
                bool first = true;
                while (!_stop)
                {
                    Thread.Sleep(5);
                    while (true)
                    {
                        CoreAudio.Check(capture.GetNextPacketSize(out var next), "GetNextPacketSize");
                        if (next == 0) break;
                        CoreAudio.Check(capture.GetBuffer(out var data, out var frames, out var flags, out var position, out _), "GetBuffer");
                        int count = (int)frames * format.Channels;
                        try
                        {
                            if (samples.Length < count) samples = new float[count];
                            if ((flags & CoreAudio.BufferFlagsSilent) != 0) Array.Clear(samples, 0, count);
                            else format.ToFloat(data, samples, count);
                        }
                        finally
                        {
                            capture.ReleaseBuffer(frames);
                        }
                        Measure(samples.AsSpan(0, count), format.Channels, flags, position, first);
                        first = false;
                    }
                }
            }
            finally
            {
                client.Stop();
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPtr);
        }
    }

    void ResetDetector(int channels)
    {
        _prev1 = new float[channels];
        _prev2 = new float[channels];
        _avg = new float[channels];
        _pendingKink = new float[channels];
        _pendingAge = new int[channels];
        _warmupUntil = _frameClock + WarmupFrames;
    }

    void Measure(ReadOnlySpan<float> samples, int channels, uint flags, ulong position, bool first)
    {
        int frames = samples.Length / channels;

        // Loopback only delivers data while something is rendering, so a gap is either a pause or a dropout.
        long gap = 0;
        if (_expectedPosition is ulong expected && position > expected) gap = (long)(position - expected);
        _expectedPosition = position + (ulong)frames;
        bool glitch = !first && (flags & CoreAudio.BufferFlagsDataDiscontinuity) != 0;
        if (gap > 0 || glitch)
        {
            _warmupUntil = _frameClock + WarmupFrames;
            Array.Clear(_pendingAge);
        }

        float peak = 0;
        double sumSquares = 0;
        int clipped = 0, clicks = 0;
        for (int f = 0; f < frames; f++, _frameClock++)
        {
            bool detect = _frameClock >= _warmupUntil;
            bool clickHere = false;
            for (int c = 0; c < channels; c++)
            {
                float x = samples[f * channels + c];
                float magnitude = MathF.Abs(x);
                if (magnitude > peak) peak = magnitude;
                if (magnitude >= ClipLevel) clipped++;
                sumSquares += x * x;

                // A click is an isolated kink in the waveform: a second difference far above its recent
                // average that dies away within a few samples. Drum hits and other sharp sounds stay loud.
                float kink = MathF.Abs(x - 2 * _prev1[c] + _prev2[c]);
                if (_pendingAge[c] > 0)
                {
                    _pendingAge[c]++;
                    if (_pendingAge[c] > 3 && kink > ClickSettleRatio * _pendingKink[c]) _pendingAge[c] = 0;
                    else if (_pendingAge[c] > ClickSettleFrames)
                    {
                        clickHere = true;
                        _pendingAge[c] = 0;
                    }
                }
                else if (detect && kink > ClickFloor && kink > ClickRatio * _avg[c])
                {
                    _pendingKink[c] = kink;
                    _pendingAge[c] = 1;
                }
                _avg[c] += (kink - _avg[c]) * 0.005f;
                _prev2[c] = _prev1[c];
                _prev1[c] = x;
            }
            if (clickHere && _frameClock - _lastClickFrame > SampleRate / 50)
            {
                clicks++;
                _lastClickFrame = _frameClock;
            }
        }

        if (_wav is not null)
        {
            if (gap > 0) _wav.WriteSilence(Math.Min(gap, 5L * SampleRate));
            _wav.Write(samples);
        }

        lock (_lock)
        {
            var s = _current;
            s.Packets++;
            s.Frames += frames;
            s.Samples += samples.Length;
            s.SumSquares += sumSquares;
            s.Peak = MathF.Max(s.Peak, peak);
            s.Clipped += clipped;
            s.Clicks += clicks;
            s.GapFrames += gap;
            if ((flags & CoreAudio.BufferFlagsSilent) != 0) s.SilentPackets++;
            if (glitch) s.Discontinuities++;
        }
    }
}

sealed record MixFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    public static MixFormat Read(IntPtr format)
    {
        ushort tag = (ushort)Marshal.ReadInt16(format, 0);
        int channels = Marshal.ReadInt16(format, 2);
        int sampleRate = Marshal.ReadInt32(format, 4);
        int bits = Marshal.ReadInt16(format, 14);
        bool isFloat = tag == 3;
        if (tag == 0xFFFE)
        {
            var subtype = Marshal.PtrToStructure<Guid>(format + 24);
            isFloat = subtype == SubtypeIeeeFloat;
            if (!isFloat && subtype != SubtypePcm) throw new NotSupportedException($"unsupported mix format subtype {subtype}");
        }
        else if (tag is not (1 or 3))
        {
            throw new NotSupportedException($"unsupported mix format tag 0x{tag:X}");
        }
        if (isFloat ? bits != 32 : bits is not (16 or 32)) throw new NotSupportedException($"unsupported {bits}-bit mix format");
        return new MixFormat(sampleRate, channels, bits, isFloat);
    }

    public void ToFloat(IntPtr source, float[] destination, int count)
    {
        if (IsFloat)
        {
            Marshal.Copy(source, destination, 0, count);
        }
        else if (BitsPerSample == 16)
        {
            var raw = new short[count];
            Marshal.Copy(source, raw, 0, count);
            for (int i = 0; i < count; i++) destination[i] = raw[i] / 32768f;
        }
        else
        {
            var raw = new int[count];
            Marshal.Copy(source, raw, 0, count);
            for (int i = 0; i < count; i++) destination[i] = raw[i] / 2147483648f;
        }
    }

    public override string ToString() => $"{SampleRate} Hz, {Channels} ch, {BitsPerSample}-bit {(IsFloat ? "float" : "PCM")}";
}

sealed class WavWriter : IDisposable
{
    readonly BinaryWriter _writer;
    readonly int _channels;
    long _dataBytes;

    public WavWriter(string path, int sampleRate, int channels)
    {
        _channels = channels;
        _writer = new BinaryWriter(File.Create(path));
        _writer.Write("RIFF"u8);
        _writer.Write(0);
        _writer.Write("WAVEfmt "u8);
        _writer.Write(16);
        _writer.Write((short)3); // IEEE float
        _writer.Write((short)channels);
        _writer.Write(sampleRate);
        _writer.Write(sampleRate * channels * 4);
        _writer.Write((short)(channels * 4));
        _writer.Write((short)32);
        _writer.Write("data"u8);
        _writer.Write(0);
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        _writer.Write(MemoryMarshal.AsBytes(samples));
        _dataBytes += samples.Length * 4L;
    }

    public void WriteSilence(long frames)
    {
        long bytes = frames * _channels * 4;
        var zeros = new byte[(int)Math.Min(bytes, 65536)];
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, zeros.Length);
            _writer.Write(zeros, 0, n);
            bytes -= n;
            _dataBytes += n;
        }
    }

    public void Dispose()
    {
        _writer.Seek(4, SeekOrigin.Begin);
        _writer.Write((int)(36 + _dataBytes));
        _writer.Seek(40, SeekOrigin.Begin);
        _writer.Write((int)_dataBytes);
        _writer.Dispose();
    }
}

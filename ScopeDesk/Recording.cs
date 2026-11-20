using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace ScopeDesk;

internal readonly record struct RecordingEntry(long Offset, double Start, int Count, CaptureSettings Settings,
    Calibration First, Calibration Second, int FirstProbe, int SecondProbe, byte FirstMin, byte FirstMax, byte SecondMin, byte SecondMax)
{
    public double End => Start + (double)(Count - 1) / Settings.Rate.Hertz;
    public double Span(int channel) => channel == 0 ? (FirstMax - FirstMin) * First.VoltsPerCount * FirstProbe
        : (SecondMax - SecondMin) * Second.VoltsPerCount * SecondProbe;
    public ChannelRanges Extrema => new(VoltageRange.FromRaw(First, FirstProbe, FirstMin, FirstMax),
        VoltageRange.FromRaw(Second, SecondProbe, SecondMin, SecondMax));
}

internal sealed class RecordingWriter
{
    internal static ReadOnlySpan<byte> Signature => "SCDREC01"u8;
    internal const int RecordBytes = 64;
    private readonly Channel<(CaptureFrame Frame, int Probes)> queue = Channel.CreateBounded<(CaptureFrame, int)>(32);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly long origin = Stopwatch.GetTimestamp();
    private int saved, dropped;

    public RecordingWriter(string path, bool demo)
    {
        Path = path;
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 131072, FileOptions.SequentialScan);
        Completion = Task.Run(() => Write(stream, demo));
    }

    public string Path { get; }
    public Task Completion { get; }
    public int Saved => Volatile.Read(ref saved);
    public int Dropped => Volatile.Read(ref dropped);
    public TimeSpan Elapsed => clock.Elapsed;

    public void Add(CaptureFrame frame, int probes)
    {
        if (frame.Timestamp < origin || Completion.IsCompleted)
            return;
        frame.Retain();
        if (queue.Writer.TryWrite((frame, probes)))
            return;
        frame.Dispose();
        Interlocked.Increment(ref dropped);
    }

    public Task Finish()
    {
        queue.Writer.TryComplete();
        return Completion;
    }

    public static Task SaveRecent(string path, bool demo, RecentSnapshot snapshot) => Task.Run(() =>
    {
        double origin = snapshot.Frames[0].StartTicks;
        var start = DateTime.UtcNow + TimeSpan.FromSeconds((origin - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
        using var writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
            131072, FileOptions.SequentialScan));
        WriteHeader(writer, demo, start);
        foreach (var frame in snapshot.Frames)
            WriteFrame(writer, frame, origin);
    });

    private async Task Write(FileStream stream, bool demo)
    {
        try
        {
            using (stream)
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, demo, DateTime.UtcNow);
                await foreach (var (frame, probes) in queue.Reader.ReadAllAsync())
                {
                    using (frame)
                    {
                        WriteFrame(writer, new BufferedFrame(frame, probes, 0, frame.TotalSamples), origin);
                        Interlocked.Increment(ref saved);
                    }
                }
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var item))
                item.Frame.Dispose();
        }
    }

    private static void WriteHeader(BinaryWriter writer, bool demo, DateTime utcStart)
    {
        writer.Write(Signature);
        writer.Write(utcStart.Ticks);
        writer.Write(demo);
    }

    private static void WriteFrame(BinaryWriter writer, BufferedFrame part, double origin)
    {
        var frame = part.Frame;
        int offset = (frame.Skip + part.Start) * 2;
        byte min1 = 255, max1 = 0, min2 = 255, max2 = 0;
        int end = offset + part.Count * 2;
        for (int i = offset; i < end; i += 2)
        {
            byte first = frame.Data[i], second = frame.Data[i + 1];
            min1 = Math.Min(min1, first);
            max1 = Math.Max(max1, first);
            min2 = Math.Min(min2, second);
            max2 = Math.Max(max2, second);
        }
        writer.Write((part.StartTicks - origin) / Stopwatch.Frequency);
        writer.Write(part.Count);
        writer.Write(frame.Settings.Rate.Hertz);
        writer.Write(frame.Settings.SecondsPerDivision);
        writer.Write(frame.Settings.First.Index);
        writer.Write(frame.Settings.Second.Index);
        writer.Write((byte)part.Probes);
        writer.Write((byte)frame.Settings.HorizontalDivisions);
        writer.Write(frame.FirstCalibration.Zero);
        writer.Write(frame.FirstCalibration.VoltsPerCount);
        writer.Write(frame.SecondCalibration.Zero);
        writer.Write(frame.SecondCalibration.VoltsPerCount);
        writer.Write(min1);
        writer.Write(max1);
        writer.Write(min2);
        writer.Write(max2);
        writer.Write(frame.Data, offset, part.Count * 2);
    }
}

internal sealed class RecordingFile : IDisposable
{
    private readonly FileStream stream;
    public string Path { get; }
    public DateTime StartTime { get; }
    public bool IsDemo { get; }
    public bool Incomplete { get; }
    public IReadOnlyList<RecordingEntry> Entries { get; }
    public double Duration { get; }
    public double LongestFrame { get; }

    public RecordingFile(string path)
    {
        Path = path;
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 17 || !reader.ReadBytes(8).AsSpan().SequenceEqual(RecordingWriter.Signature))
                throw new InvalidDataException(L10n.Text("BadRecording"));
            long ticks = reader.ReadInt64();
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException(L10n.Text("BadRecordingDate"));
            StartTime = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
            IsDemo = reader.ReadBoolean();
            var entries = new List<RecordingEntry>();
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < RecordingWriter.RecordBytes)
                {
                    Incomplete = true;
                    break;
                }
                double start = reader.ReadDouble();
                int count = reader.ReadInt32();
                int rate = reader.ReadInt32();
                double seconds = reader.ReadDouble();
                byte firstScale = reader.ReadByte(), secondScale = reader.ReadByte();
                byte probes = reader.ReadByte();
                int divisions = reader.ReadByte();
                if (divisions == 0)
                    divisions = CaptureSettings.DefaultHorizontalDivisions;
                var first = new Calibration(reader.ReadDouble(), reader.ReadDouble());
                var second = new Calibration(reader.ReadDouble(), reader.ReadDouble());
                byte min1 = reader.ReadByte(), max1 = reader.ReadByte(), min2 = reader.ReadByte(), max2 = reader.ReadByte();
                var sampleRate = Array.Find(CaptureSettings.Rates, item => item.Hertz == rate);
                if (!double.IsFinite(start) || start < 0 || start > TimeSpan.MaxValue.TotalSeconds - 60 ||
                    (entries.Count > 0 && start < entries[^1].Start) || sampleRate is null ||
                    count is < 3 or > CaptureSettings.MaxVisibleSamples * 2 ||
                    !CaptureSettings.HorizontalDivisionOptions.Contains(divisions) ||
                    !double.IsFinite(seconds) || seconds <= 0 || seconds * rate * divisions > CaptureSettings.MaxVisibleSamples ||
                    firstScale >= CaptureSettings.Scales.Length || secondScale >= CaptureSettings.Scales.Length ||
                    probes > 3 || !Valid(first) || !Valid(second) || min1 > max1 || min2 > max2)
                    throw new InvalidDataException(L10n.Format("BadFrameData", entries.Count + 1));
                long offset = stream.Position;
                if (stream.Length - offset < count * 2L)
                {
                    Incomplete = true;
                    break;
                }
                var settings = new CaptureSettings(sampleRate, seconds, CaptureSettings.Scales[firstScale],
                    CaptureSettings.Scales[secondScale], divisions);
                var entry = new RecordingEntry(offset, start, count, settings, first, second, (probes & 1) == 0 ? 1 : 10,
                    (probes & 2) == 0 ? 1 : 10, min1, max1, min2, max2);
                entries.Add(entry);
                Duration = Math.Max(Duration, entry.End);
                LongestFrame = Math.Max(LongestFrame, entry.End - entry.Start);
                stream.Position = offset + count * 2L;
            }
            if (entries.Count == 0)
                throw new InvalidDataException(L10n.Text("EmptyRecording"));
            Entries = entries;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public CaptureFrame Read(int index) => ReadFrame(stream, Entries[index]);

    public ChannelRanges? ReadTrailingExtrema(double end)
    {
        double start = Math.Max(0, end - ExtremaHistory.Seconds);
        int index = AtOrBefore(Math.Max(0, start - LongestFrame));
        ChannelRanges? extrema = null;
        FileStream? boundary = null;
        try
        {
            for (; index < Entries.Count; index++)
            {
                var entry = Entries[index];
                if (entry.Start > end)
                    break;
                if (entry.End < start)
                    continue;

                ChannelRanges range;
                if (entry.Start >= start && entry.End <= end)
                    range = entry.Extrema;
                else
                {
                    int rate = entry.Settings.Rate.Hertz;
                    int first = (int)Math.Clamp(Math.Ceiling((start - entry.Start) * rate - 1e-7), 0, entry.Count);
                    int last = (int)Math.Clamp(Math.Floor((end - entry.Start) * rate + 1e-7), -1, entry.Count - 1);
                    if (last < first)
                        continue;
                    boundary ??= new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
                    using var frame = ReadFrame(boundary, entry);
                    range = ChannelRanges.Measure(frame, first, last - first + 1, entry.FirstProbe, entry.SecondProbe);
                }
                extrema = extrema is { } previous ? previous.Include(range) : range;
            }
            return extrema;
        }
        finally
        {
            boundary?.Dispose();
        }
    }

    internal static CaptureFrame ReadFrame(FileStream stream, RecordingEntry entry)
    {
        var frame = new CaptureFrame(entry.Settings, 0, entry.First, entry.Second, entry.Count);
        try
        {
            stream.Position = entry.Offset;
            stream.ReadExactly(frame.Data.AsSpan(0, entry.Count * 2));
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public int AtOrBefore(double position)
    {
        int low = 0, high = Entries.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (Entries[mid].Start <= position)
                low = mid + 1;
            else
                high = mid;
        }
        return Math.Max(0, low - 1);
    }

    private static bool Valid(Calibration calibration) =>
        calibration.Zero is >= 0 and <= 255 && calibration.VoltsPerCount is > 0 and <= 1;

    public void Dispose() => stream.Dispose();
}

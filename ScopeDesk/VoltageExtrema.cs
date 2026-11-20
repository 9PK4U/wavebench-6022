using System.Diagnostics;

namespace ScopeDesk;

internal readonly record struct VoltageRange(double Minimum, double Maximum)
{
    public VoltageRange Include(VoltageRange other) => new(Math.Min(Minimum, other.Minimum), Math.Max(Maximum, other.Maximum));
    public VoltageRange ApplyProbe(int probe) => new(Minimum * probe, Maximum * probe);

    public static VoltageRange FromRaw(Calibration calibration, int probe, byte min, byte max) =>
        new(calibration.Convert(min) * probe, calibration.Convert(max) * probe);
}

internal readonly record struct ChannelRanges(VoltageRange First, VoltageRange Second)
{
    public ChannelRanges Include(ChannelRanges other) => new(First.Include(other.First), Second.Include(other.Second));

    public static ChannelRanges Measure(CaptureFrame frame, int start, int count, int firstProbe, int secondProbe)
    {
        var data = frame.Data;
        int offset = (frame.Skip + start) * 2;
        int end = offset + count * 2;
        byte firstMin = 255, firstMax = 0, secondMin = 255, secondMax = 0;
        for (int i = offset; i < end; i += 2)
        {
            byte first = data[i], second = data[i + 1];
            firstMin = Math.Min(firstMin, first);
            firstMax = Math.Max(firstMax, first);
            secondMin = Math.Min(secondMin, second);
            secondMax = Math.Max(secondMax, second);
        }
        return new ChannelRanges(VoltageRange.FromRaw(frame.FirstCalibration, firstProbe, firstMin, firstMax),
            VoltageRange.FromRaw(frame.SecondCalibration, secondProbe, secondMin, secondMax));
    }
}

internal sealed class ExtremaHistory
{
    internal const int Seconds = 5;
    private const int BucketsPerSecond = 10;
    private const int CompletedBuckets = Seconds * BucketsPerSecond;
    private readonly (long Index, ChannelRanges? Range)[] history = new (long, ChannelRanges?)[CompletedBuckets + 1];
    private long? origin;
    private long latestBucket;

    public ChannelRanges Add(CaptureFrame frame)
    {
        origin ??= frame.Timestamp;
        int rate = frame.Settings.Rate.Hertz;
        double start = (double)(frame.Timestamp - origin.Value) / Stopwatch.Frequency;
        double end = start + (double)(frame.TotalSamples - 1) / rate;
        long endBucket = (long)Math.Floor(end * BucketsPerSecond);
        latestBucket = Math.Max(latestBucket, endBucket);
        long firstBucket = Math.Max((long)Math.Floor(start * BucketsPerSecond), latestBucket - CompletedBuckets);
        for (long index = firstBucket; index <= endBucket; index++)
        {
            int from = Boundary(index);
            int to = Boundary(index + 1);
            if (to <= from)
                continue;
            var range = ChannelRanges.Measure(frame, from, to - from, 1, 1);
            ref var bucket = ref history[index % history.Length];
            bucket = (index, bucket.Index == index && bucket.Range is { } old ? old.Include(range) : range);
        }

        ChannelRanges? accumulated = null;
        foreach (var (index, range) in history)
        {
            if (index >= latestBucket - CompletedBuckets && index < latestBucket && range is { } item)
                accumulated = accumulated is { } previous ? previous.Include(item) : item;
        }

        // A first frame can arrive before one complete 100 ms bucket exists.
        return accumulated ?? history[latestBucket % history.Length].Range
            ?? ChannelRanges.Measure(frame, 0, frame.TotalSamples, 1, 1);

        int Boundary(long index) => (int)Math.Clamp(
            Math.Ceiling(((double)index / BucketsPerSecond - start) * rate - 1e-7), 0, frame.TotalSamples);
    }

    public void Clear()
    {
        Array.Clear(history);
        origin = null;
        latestBucket = 0;
    }
}

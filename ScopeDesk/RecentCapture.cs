using System.Diagnostics;

namespace ScopeDesk;

internal readonly record struct BufferedFrame(CaptureFrame Frame, int Probes, int Start, int Count)
{
    public double TickAt(int sample) => Frame.Timestamp + (double)sample * Stopwatch.Frequency / Frame.Settings.Rate.Hertz;
    public double StartTicks => TickAt(Start);
    public double EndTicks => TickAt(Start + Count - 1);

    public BufferedFrame? Within(double from, double to)
    {
        double samplesPerTick = (double)Frame.Settings.Rate.Hertz / Stopwatch.Frequency;
        int first = (int)Math.Clamp(Math.Ceiling((from - Frame.Timestamp) * samplesPerTick), 0, Count);
        int last = (int)Math.Clamp(Math.Floor((to - Frame.Timestamp) * samplesPerTick), -1, Count - 1);
        return last - first >= 2 ? this with { Start = first, Count = last - first + 1 } : null;
    }
}

internal sealed class RecentCapture
{
    private const int MaxBytes = 128 * 1024 * 1024;
    private static readonly long WindowTicks = 10 * Stopwatch.Frequency;
    private readonly Queue<BufferedFrame> frames = new();
    private long bytes;

    public void Add(CaptureFrame frame, int probes)
    {
        var incoming = new BufferedFrame(frame, probes, 0, frame.TotalSamples);
        lock (frames)
        {
            frame.Retain();
            frames.Enqueue(incoming);
            bytes += frame.Data.Length;
            while (bytes > MaxBytes || frames.Peek().EndTicks < incoming.EndTicks - WindowTicks)
                ReleaseOldest();
        }
    }

    public RecentSnapshot Snapshot()
    {
        lock (frames)
        {
            if (frames.Count == 0)
                return new RecentSnapshot([]);
            double end = frames.Last().EndTicks;
            var snapshot = frames.Select(frame => frame.Within(end - WindowTicks, end))
                .OfType<BufferedFrame>().OrderBy(frame => frame.StartTicks).ToArray();
            foreach (var entry in snapshot)
                entry.Frame.Retain();
            return new RecentSnapshot(snapshot);
        }
    }

    private void ReleaseOldest()
    {
        var frame = frames.Dequeue().Frame;
        bytes -= frame.Data.Length;
        frame.Dispose();
    }

    public void Clear()
    {
        lock (frames)
            while (frames.Count > 0)
                ReleaseOldest();
    }
}

internal sealed class RecentSnapshot(BufferedFrame[] frames) : IDisposable
{
    public IReadOnlyList<BufferedFrame> Frames => frames;

    public void Dispose()
    {
        foreach (var frame in frames)
            frame.Frame.Dispose();
    }
}

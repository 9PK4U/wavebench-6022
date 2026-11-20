using System.IO;

namespace ScopeDesk;

internal readonly record struct SearchQuery(int Channel, double Threshold, double? WidthLimitSeconds);

internal readonly record struct SearchHit(int FrameIndex, double Sample, double Time, double? EndSample, double? Duration)
{
    public override string ToString() => Duration is { } duration
        ? $"{Units.Time(Time)}   ·   {Units.Time(duration)}"
        : Units.Time(Time);
}

internal static class RecordingSearch
{
    public const int MaxHits = 5000;

    public static List<SearchHit> Run(RecordingFile file, SearchQuery query, IProgress<int> progress, CancellationToken cancellation)
    {
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        var hits = new List<SearchHit>();
        int lastProgress = -1;
        for (int index = 0; index < file.Entries.Count && hits.Count < MaxHits; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            int percent = (int)(100L * index / file.Entries.Count);
            if (percent != lastProgress)
            {
                progress.Report(percent);
                lastProgress = percent;
            }
            var entry = file.Entries[index];
            var calibration = query.Channel == 0 ? entry.First : entry.Second;
            int probe = query.Channel == 0 ? entry.FirstProbe : entry.SecondProbe;
            double threshold = calibration.Zero + query.Threshold / (calibration.VoltsPerCount * probe);
            if ((query.Channel == 0 ? entry.FirstMax : entry.SecondMax) <= threshold ||
                query.WidthLimitSeconds.HasValue && (query.Channel == 0 ? entry.FirstMin : entry.SecondMin) > threshold)
                continue;

            using var frame = RecordingFile.ReadFrame(stream, entry);
            Scan(frame, index, entry.Start, query, threshold, hits, cancellation);
        }
        return hits.OrderBy(hit => hit.Time).ToList();
    }

    private static void Scan(CaptureFrame frame, int index, double origin, SearchQuery query, double threshold,
        List<SearchHit> hits, CancellationToken cancellation)
    {
        double? rising = null;
        int rate = frame.Settings.Rate.Hertz;
        int previous = frame.Sample(query.Channel, 0);
        bool above = previous > threshold;
        if (above && !query.WidthLimitSeconds.HasValue)
            hits.Add(new SearchHit(index, 0, origin, null, null));
        for (int i = 1; i < frame.TotalSamples && hits.Count < MaxHits; i++)
        {
            if ((i & 16383) == 0)
                cancellation.ThrowIfCancellationRequested();
            int raw = frame.Sample(query.Channel, i);
            bool high = raw > threshold;
            if (high != above)
            {
                double crossing = i - 1 + (threshold - previous) / (raw - previous);
                if (high)
                {
                    rising = crossing;
                    if (!query.WidthLimitSeconds.HasValue)
                        hits.Add(new SearchHit(index, crossing, origin + crossing / rate, null, null));
                }
                else if (rising is { } start && query.WidthLimitSeconds is { } limit)
                {
                    double width = (crossing - start) / rate;
                    if (width < limit)
                        hits.Add(new SearchHit(index, start, origin + start / rate, crossing, width));
                }
                above = high;
            }
            previous = raw;
        }
    }
}

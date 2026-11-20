namespace ScopeDesk;

internal readonly record struct AutoSetup(int Channel, double Level, double? Frequency)
{
    public static AutoSetup? Find(CaptureFrame frame, DisplayOptions display)
    {
        AutoSetup? best = null;
        double bestScore = 0;
        for (int channel = 0; channel < 2; channel++)
        {
            Span<int> histogram = new int[256];
            int step = Math.Max(1, frame.TotalSamples / 12000);
            int samples = 0;
            for (int i = 0; i < frame.TotalSamples; i += step)
            {
                histogram[frame.Sample(channel, i)]++;
                samples++;
            }
            int low = Percentile(histogram, samples / 100), high = Percentile(histogram, samples - 1 - samples / 100);
            if (high - low < 6)
                continue;
            var window = new WaveWindow(0, frame.TotalSamples, null);
            var measurement = window.Measure(frame, channel, display.Probe(channel));
            var calibration = frame.Channel(channel);
            double level = ((low + high) / 2.0 - calibration.Zero) * calibration.VoltsPerCount * display.Probe(channel);
            double score = (high - low) * (measurement.Frequency.HasValue ? 2 : 1);
            if (score > bestScore)
            {
                bestScore = score;
                best = new AutoSetup(channel, level, measurement.Frequency);
            }
        }
        return best;
    }

    private static int Percentile(ReadOnlySpan<int> histogram, int rank)
    {
        for (int i = 0; i < histogram.Length; i++)
        {
            rank -= histogram[i];
            if (rank < 0)
                return i;
        }
        return 255;
    }
}

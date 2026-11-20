using System.IO;
using System.Text;

namespace ScopeDesk;

internal static class CsvExport
{
    public static Task Save(string path, CaptureFrame frame, WaveWindow window, int firstProbe, int secondProbe,
        double timeOffset = 0)
    {
        int rate = frame.Settings.Rate.Hertz;
        var first = frame.FirstCalibration;
        var second = frame.SecondCalibration;
        var bytes = frame.Data.AsSpan((frame.Skip + window.Start) * 2, window.Length * 2).ToArray();
        return Task.Run(() =>
        {
            using var file = new StreamWriter(path, false, new UTF8Encoding(true), 65536);
            file.WriteLine("time_s,ch1_V,ch2_V");
            for (int index = 0; index < window.Length; index++)
            {
                double t = timeOffset + window.TimeAt(index, rate);
                double a = first.Convert(bytes[index * 2]) * firstProbe;
                double b = second.Convert(bytes[index * 2 + 1]) * secondProbe;
                file.WriteLine(FormattableString.Invariant($"{t:G12},{a:G9},{b:G9}"));
            }
        });
    }
}

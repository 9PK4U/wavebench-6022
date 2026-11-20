using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ScopeDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        L10n.Apply(DisplayPreferences.LoadLanguage());
        if (e.Args is ["--probe", var output])
        {
            try
            {
                File.WriteAllLines(output, Probe());
                Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(output, error.ToString());
                Shutdown(1);
            }
            return;
        }
        new MainWindow(e.Args.Contains("--demo")).Show();
    }

    private static IEnumerable<string> Probe()
    {
        using var scope = new HantekSource(CancellationToken.None);
        yield return $"Firmware 0x{scope.Version:X4}";
        foreach (var rate in CaptureSettings.Rates)
        {
            var settings = new CaptureSettings(rate, .0005, CaptureSettings.Scales[5], CaptureSettings.Scales[5]);
            var time = Stopwatch.StartNew();
            int min1 = 255, max1 = 0, min2 = 255, max2 = 0;
            const int captures = 12;
            for (int i = 0; i < captures; i++)
            {
                using var frame = scope.Read(settings, CancellationToken.None);
                for (int index = 0; index < frame.TotalSamples; index++)
                {
                    int first = frame.Sample(0, index), second = frame.Sample(1, index);
                    min1 = Math.Min(min1, first);
                    max1 = Math.Max(max1, first);
                    min2 = Math.Min(min2, second);
                    max2 = Math.Max(max2, second);
                }
            }
            yield return $"{rate.Hertz,9} Sa/s   {captures} × {settings.CaptureSamples} pairs   CH1 {min1}–{max1}   CH2 {min2}–{max2}   {time.ElapsedMilliseconds} ms";
        }
    }
}

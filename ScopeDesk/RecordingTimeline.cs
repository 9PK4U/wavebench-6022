using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScopeDesk;

public sealed class RecordingTimeline : FrameworkElement
{
    private static readonly Brush Back = ColorBrush("#0B1220"), Ink = ColorBrush("#99AFCB");
    private static readonly Brush First = ColorBrush("#49E1FB"), Second = ColorBrush("#FFC474");
    private static readonly Pen Grid = new(ColorBrush("#23354B"), 1), Marker = new(ColorBrush("#EEF4FF"), 1.3);
    private RecordingFile? recording;
    private Rect track;
    private double start, span, position;
    private DrawingGroup? cached;
    private double maximumFirst, maximumSecond;

    internal void RefreshLanguage() => ResetDrawing();

    public RecordingTimeline()
    {
        Focusable = false;
        Cursor = Cursors.Cross;
    }

    internal event Action<double>? Scrubbed;
    internal void Load(RecordingFile file)
    {
        recording = file;
        maximumFirst = Math.Max(.001, file.Entries.Max(entry => entry.Span(0)));
        maximumSecond = Math.Max(.001, file.Entries.Max(entry => entry.Span(1)));
        Fit();
    }

    internal void Fit()
    {
        start = 0;
        span = recording?.Duration ?? 0;
        ResetDrawing();
    }

    internal void Move(double time, bool keepVisible)
    {
        position = time;
        if (keepVisible && recording is { } file && (time < start || time > start + span))
        {
            start = Math.Clamp(time - span / 2, 0, Math.Max(0, file.Duration - span));
            ResetDrawing();
        }
        else
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRoundedRectangle(Back, null, new Rect(RenderSize), 8, 8);
        if (recording is null || ActualWidth < 140 || ActualHeight < 54 || span <= 0)
            return;
        var area = new Rect(59, 8, ActualWidth - 81, ActualHeight - 28);
        if (area != track)
        {
            track = area;
            cached = null;
        }
        if (cached is null)
        {
            cached = new DrawingGroup();
            using (var body = cached.Open())
                DrawTimeline(body);
            cached.Freeze();
        }
        drawing.DrawDrawing(cached);
        if (start <= position && position <= start + span)
        {
            double x = track.Left + (position - start) / span * track.Width;
            drawing.DrawLine(Marker, new Point(x, track.Top), new Point(x, track.Bottom));
        }
    }

    private void DrawTimeline(DrawingContext body)
    {
        int pixels = Math.Max(1, (int)track.Width);
        var first = new float[pixels];
        var second = new float[pixels];
        var file = recording!;
        int begin = file.AtOrBefore(Math.Max(0, start - file.LongestFrame));
        for (int index = begin; index < file.Entries.Count; index++)
        {
            var entry = file.Entries[index];
            if (entry.Start > start + span)
                break;
            if (entry.End < start)
                continue;
            int from = Math.Clamp((int)((entry.Start - start) / span * pixels), 0, pixels - 1);
            int to = Math.Clamp((int)((entry.End - start) / span * pixels), from, pixels - 1);
            float amplitude1 = (float)(entry.Span(0) / maximumFirst), amplitude2 = (float)(entry.Span(1) / maximumSecond);
            for (int column = from; column <= to; column++)
            {
                first[column] = Math.Max(first[column], .15f + .85f * amplitude1);
                second[column] = Math.Max(second[column], .15f + .85f * amplitude2);
            }
        }

        double row = track.Height / 2;
        body.DrawText(Label("CH1", First), new Point(15, track.Top + (row - 13) / 2));
        body.DrawText(Label("CH2", Second), new Point(15, track.Top + row + (row - 13) / 2));
        for (int i = 0; i <= 5; i++)
        {
            double x = track.Left + i * track.Width / 5;
            body.DrawLine(Grid, new Point(x, track.Top), new Point(x, track.Bottom));
            var label = Label(FormatTime(start + i * span / 5), Ink);
            body.DrawText(label, new Point(Math.Min(ActualWidth - label.Width - 4, x - label.Width / 2), track.Bottom + 3));
        }
        for (int i = 0; i < pixels; i++)
        {
            double x = track.Left + i * track.Width / pixels;
            DrawBar(body, x, track.Top + row, (row - 3) * first[i], First);
            DrawBar(body, x, track.Bottom, (row - 3) * second[i], Second);
        }
    }

    private static void DrawBar(DrawingContext canvas, double x, double y, double height, Brush color)
    {
        if (height > 0)
            canvas.DrawRectangle(color, null, new Rect(x, y - height, 1.15, height));
    }

    private FormattedText Label(string text, Brush color) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Consolas"), 11, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    internal static string FormatTime(double seconds)
    {
        if (seconds < 60)
            return Units.Time(seconds);
        var time = TimeSpan.FromSeconds(seconds);
        return time.ToString(time.Days == 0 ? @"hh\:mm\:ss" : @"d\.hh\:mm\:ss");
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (span <= 0)
            return;
        CaptureMouse();
        Scrub(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            Scrub(e.GetPosition(this));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (recording is not { } file || span <= 0 || track.Width <= 0)
            return;
        double fraction = Math.Clamp((e.GetPosition(this).X - track.Left) / track.Width, 0, 1);
        double anchor = start + span * fraction;
        span = Math.Clamp(span * (e.Delta > 0 ? .55 : 1 / .55), Math.Min(.02, file.Duration), file.Duration);
        start = Math.Clamp(anchor - span * fraction, 0, Math.Max(0, file.Duration - span));
        ResetDrawing();
        e.Handled = true;
    }

    private void Scrub(Point point)
    {
        if (track.Width > 0)
            Scrubbed?.Invoke(start + Math.Clamp((point.X - track.Left) / track.Width, 0, 1) * span);
    }

    private void ResetDrawing()
    {
        cached = null;
        InvalidateVisual();
    }

    private static Brush ColorBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ScopeDesk;

internal enum TriggerEdge { Off, Rising, Falling }

internal readonly record struct DisplayOptions(bool FirstVisible, bool SecondVisible, int TriggerChannel,
    TriggerEdge Edge, double Level, int FirstProbe, int SecondProbe, bool Split = false)
{
    public int Probe(int channel) => channel == 0 ? FirstProbe : SecondProbe;
    public bool Visible(int channel) => channel == 0 ? FirstVisible : SecondVisible;
    public bool SeparateLanes => Split && FirstVisible && SecondVisible;
}

internal readonly record struct Measurement(double Minimum, double Maximum, double PeakToPeak, double Rms,
    double? Frequency, bool Clipped)
{
    public VoltageRange Range => new(Minimum, Maximum);
}

internal readonly record struct WaveWindow(int Start, int Length, double? TriggerSample)
{
    public double TimeAt(int index, int rate) => (Start + index - (TriggerSample ?? Start)) / rate;

    public static WaveWindow Select(CaptureFrame frame, DisplayOptions display)
    {
        int length = frame.Settings.VisibleSamples;
        if (display.Edge == TriggerEdge.Off)
            return new WaveWindow(0, length, null);
        int channel = display.TriggerChannel;
        var cal = frame.Channel(channel);
        double level = cal.Zero + display.Level / (cal.VoltsPerCount * display.Probe(channel));
        int sign = display.Edge == TriggerEdge.Rising ? 1 : -1;
        int preTrigger = (int)Math.Round((length - 1.0) * frame.Settings.TriggerDivision / frame.Settings.HorizontalDivisions);
        int limit = frame.TotalSamples - (length - preTrigger);
        int firstCandidate = Math.Max(1, preTrigger);
        bool armed = false;
        int previous = frame.Sample(channel, 0);
        for (int i = 1; i <= limit; i++)
        {
            int sample = frame.Sample(channel, i);
            if ((previous - level) * sign <= -1)
                armed = true;
            if (armed && (sample - level) * sign >= 0 && sample != previous)
            {
                armed = false;
                if (i >= firstCandidate)
                {
                    double crossing = i - 1 + (level - previous) / (sample - previous);
                    return new WaveWindow(i - preTrigger, length, crossing);
                }
            }
            previous = sample;
        }
        return new WaveWindow(0, length, null);
    }

    public Measurement Measure(CaptureFrame frame, int channel, int probe)
    {
        var calibration = frame.Channel(channel);
        var data = frame.Data;
        int offset = (frame.Skip + Start) * 2 + channel;
        int end = offset + 2 * Length;
        int min = 255, max = 0;
        long sum = 0, sumSquares = 0;
        for (int i = offset; i < end; i += 2)
        {
            int raw = data[i];
            min = Math.Min(min, raw);
            max = Math.Max(max, raw);
            sum += raw;
            sumSquares += raw * raw;
        }
        double factor = calibration.VoltsPerCount * probe;
        double meanSquare = (sumSquares - 2 * calibration.Zero * sum) / Length + calibration.Zero * calibration.Zero;
        double? frequency = null;
        if (max - min >= 6)
        {
            double crossingLevel = (min + max) / 2.0, hysteresis = (max - min) * .12;
            bool armed = false;
            double firstEdge = 0, lastEdge = 0;
            int count = 0;
            int previous = data[offset];
            for (int i = 1; i < Length; i++)
            {
                int raw = data[offset + 2 * i];
                if (raw < crossingLevel - hysteresis)
                    armed = true;
                if (armed && raw >= crossingLevel && previous < crossingLevel)
                {
                    double edge = i - 1 + (crossingLevel - previous) / (raw - previous);
                    if (count == 0)
                        firstEdge = edge;
                    lastEdge = edge;
                    count++;
                    armed = false;
                }
                previous = raw;
            }
            if (count >= 2)
                frequency = frame.Settings.Rate.Hertz * (count - 1) / (lastEdge - firstEdge);
        }
        return new Measurement(calibration.Convert((byte)min) * probe, calibration.Convert((byte)max) * probe,
            (max - min) * factor, Math.Sqrt(Math.Max(0, meanSquare)) * factor, frequency, min == 0 || max == 255);
    }
}

public sealed class Waveform : FrameworkElement
{
    private static readonly Brush Background = Brush("#0B1220"), Subtle = Brush("#73849F"), Text = Brush("#A1B3CC");
    private static readonly Brush Cyan = Brush("#49E1FB"), Amber = Brush("#FFC474");
    private static readonly Pen Grid = Pen("#1C293D"), Minor = Pen("#131E30"), Axis = Pen("#344761"),
        Marker = Pen("#6D8CCA", 1, true);
    private static readonly Pen CyanLine = Pen("#49E1FB", 1.6), AmberLine = Pen("#FFC474", 1.6);
    private static readonly Pen CyanGlow = Pen("#3049E1FB", 5), AmberGlow = Pen("#30FFC474", 5);
    private static readonly Pen CursorA = Pen("#A0D1FF", 1.2, true), CursorB = Pen("#E7ABFC", 1.2, true);
    private static readonly Typeface Font = new("Consolas");
    private CaptureFrame? frame;
    private WaveWindow window;
    private DisplayOptions display;
    private CaptureSettings? settings;
    private Geometry? firstGeometry, secondGeometry;
    private Rect area;
    private DrawingGroup? gridDrawing;
    private double dpi;
    private string? gridLanguage;
    private bool review;
    private int? cursorA, cursorB;

    internal void Update(CaptureFrame? capture, WaveWindow visible, DisplayOptions options, CaptureSettings current,
        bool reviewMode = false)
    {
        var before = frame?.Settings ?? settings;
        var after = capture?.Settings ?? current;
        if (before != after || display != options || review != reviewMode || gridLanguage != L10n.Language ||
            (window.TriggerSample is null) != (visible.TriggerSample is null) ||
            reviewMode && (window.Start != visible.Start || window.Length != visible.Length))
            gridDrawing = null;
        gridLanguage = L10n.Language;
        frame = capture;
        window = visible;
        display = options;
        settings = current;
        review = reviewMode;
        firstGeometry = secondGeometry = null;
        InvalidateVisual();
    }

    internal void SetCursors(int? first, int? second)
    {
        cursorA = first;
        cursorB = second;
        InvalidateVisual();
    }

    internal double SampleAt(Point position) => area.Width > 0
        ? window.Start + Math.Clamp((position.X - area.Left) / area.Width, 0, 1) * (window.Length - 1)
        : window.Start;

    internal double PlotWidth => area.Width;

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var bounds = new Rect(RenderSize);
        context.DrawRoundedRectangle(Background, null, bounds, 12, 12);
        if (ActualWidth < 180 || ActualHeight < 100 || settings is null)
            return;
        var plot = new Rect(64, 22, ActualWidth - 128, ActualHeight - 56);
        var scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (plot != area || dpi != scale)
        {
            area = plot;
            dpi = scale;
            gridDrawing = null;
            firstGeometry = secondGeometry = null;
        }

        var current = frame?.Settings ?? settings;
        if (gridDrawing is null)
        {
            gridDrawing = new DrawingGroup();
            using (var canvas = gridDrawing.Open())
                DrawGrid(canvas, current);
            gridDrawing.Freeze();
        }
        context.DrawDrawing(gridDrawing);

        context.PushClip(new RectangleGeometry(area));
        if (frame is not null)
        {
            if (window.TriggerSample is { } trigger)
            {
                double x = TriggerX(trigger);
                context.DrawLine(Marker, new Point(x, area.Top), new Point(x, area.Bottom));
            }
            if (display.FirstVisible)
            {
                firstGeometry ??= BuildTrace(frame, 0, current.First);
                context.DrawGeometry(null, CyanGlow, firstGeometry);
                context.DrawGeometry(null, CyanLine, firstGeometry);
            }
            if (display.SecondVisible)
            {
                secondGeometry ??= BuildTrace(frame, 1, current.Second);
                context.DrawGeometry(null, AmberGlow, secondGeometry);
                context.DrawGeometry(null, AmberLine, secondGeometry);
            }
            DrawCursor(context, cursorA, "A", CursorA);
            DrawCursor(context, cursorB, "B", CursorB);
        }
        context.Pop();
        if (window.TriggerSample is { } triggerSample)
        {
            double x = TriggerX(triggerSample);
            var pointer = new StreamGeometry();
            using (var pen = pointer.Open())
            {
                pen.BeginFigure(new Point(x - 5, area.Top - 8), true, true);
                pen.LineTo(new Point(x + 5, area.Top - 8), true, false);
                pen.LineTo(new Point(x, area.Top - 2), true, false);
            }
            context.DrawGeometry(Text, null, pointer);
        }
    }

    private double TriggerX(double sample) => area.Left + area.Width * (sample - window.Start) / (window.Length - 1);

    private void DrawCursor(DrawingContext context, int? sample, string title, Pen pen)
    {
        if (sample is not { } position || position < window.Start || position >= window.Start + window.Length)
            return;
        double x = TriggerX(position);
        context.DrawLine(pen, new Point(x, area.Top), new Point(x, area.Bottom));
        var text = Label(title, pen.Brush);
        context.DrawRectangle(Background, null, new Rect(x - 8, area.Top + 4, 16, 17));
        context.DrawText(text, new Point(x - text.Width / 2, area.Top + 6));
    }

    private void DrawGrid(DrawingContext canvas, CaptureSettings current)
    {
        int divisions = current.HorizontalDivisions;
        double divisionWidth = area.Width / divisions;
        int ticksPerDivision = divisionWidth >= 24 ? 5 : 1;
        int ticks = divisions * ticksPerDivision;
        int zero = window.TriggerSample is null ? 0 : current.TriggerDivision;
        const double minLabelSpacing = 63;
        int labelStep = Math.Max(divisions >= 8 ? 2 : 1, (int)Math.Ceiling(minLabelSpacing / divisionWidth));
        for (int i = 0; i <= ticks; i++)
        {
            double x = area.Left + area.Width * i / ticks;
            bool major = i % ticksPerDivision == 0;
            canvas.DrawLine(major ? Grid : Minor, new Point(x, area.Top), new Point(x, area.Bottom));
            if (!major)
                continue;
            int division = i / ticksPerDivision;
            bool endpoint = division == 0 || division == divisions || division == zero;
            if (!endpoint && ((division - zero) % labelStep != 0 ||
                Math.Min(x - area.Left, area.Right - x) < minLabelSpacing))
                continue;
            double time = review ? (window.Start + (double)division / divisions * (window.Length - 1)) / current.Rate.Hertz
                : (division - zero) * current.SecondsPerDivision;
            var label = Label(Units.Time(time), Text);
            canvas.DrawText(label, new Point(x - label.Width / 2, area.Bottom + 12));
        }
        if (display.SeparateLanes)
        {
            var first = LaneFor(0);
            var second = LaneFor(1);
            canvas.DrawRectangle(Background, null, new Rect(area.Left, first.Bottom, area.Width, second.Top - first.Bottom));
            DrawVoltageGrid(canvas, first, current, firstAxis: true, secondAxis: false);
            DrawVoltageGrid(canvas, second, current, firstAxis: false, secondAxis: true);
        }
        else
            DrawVoltageGrid(canvas, area, current, display.FirstVisible, display.SecondVisible);

        if (display.Edge != TriggerEdge.Off)
        {
            var triggerScale = display.TriggerChannel == 0 ? current.First : current.Second;
            double range = triggerScale.Value * 8 * display.Probe(display.TriggerChannel);
            var lane = LaneFor(display.TriggerChannel);
            double y = lane.Top + lane.Height * (.5 - display.Level / range);
            if (y >= lane.Top && y <= lane.Bottom)
                canvas.DrawLine(Marker, new Point(lane.Right - 16, y), new Point(lane.Right, y));
        }
    }

    private Rect LaneFor(int channel)
    {
        if (!display.SeparateLanes)
            return area;
        const double gap = 10;
        double height = (area.Height - gap) / 2;
        return new Rect(area.Left, channel == 0 ? area.Top : area.Bottom - height, area.Width, height);
    }

    private void DrawVoltageGrid(DrawingContext canvas, Rect lane, CaptureSettings current, bool firstAxis, bool secondAxis)
    {
        for (int i = 0; i <= 32; i++)
        {
            double y = lane.Top + lane.Height * i / 32;
            canvas.DrawLine(i == 16 ? Axis : i % 4 == 0 ? Grid : Minor,
                new Point(lane.Left, y), new Point(lane.Right, y));
            if (i % 8 != 0)
                continue;
            double division = 4 - i / 4.0;
            if (firstAxis)
            {
                var label = Label(Units.Volts(division * current.First.Value * display.FirstProbe), display.SeparateLanes ? Cyan : Subtle);
                canvas.DrawText(label, new Point(lane.Left - label.Width - 9, y - label.Height / 2));
            }
            if (secondAxis)
            {
                var label = Label(Units.Volts(division * current.Second.Value * display.SecondProbe), display.SeparateLanes ? Amber : Subtle);
                canvas.DrawText(label, new Point(lane.Right + 9, y - label.Height / 2));
            }
        }
    }

    private FormattedText Label(string value, Brush color) =>
        new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Font, 11, color, dpi);

    private Geometry BuildTrace(CaptureFrame capture, int channel, Scale scale)
    {
        var geometry = new StreamGeometry();
        var calibration = capture.Channel(channel);
        var lane = LaneFor(channel);
        int columns = Math.Max(1, (int)Math.Ceiling(area.Width * dpi));
        double xScale = area.Width / (window.Length - 1);
        double yScale = lane.Height / (scale.Value * 8) * calibration.VoltsPerCount;
        double mid = lane.Top + lane.Height / 2;
        var data = capture.Data;
        int origin = (capture.Skip + window.Start) * 2 + channel;
        using (var pen = geometry.Open())
        {
            void PointAt(int index, byte value, bool first)
            {
                var point = new Point(area.Left + index * xScale,
                    Math.Clamp(mid - (value - calibration.Zero) * yScale, lane.Top - 2, lane.Bottom + 2));
                if (first)
                    pen.BeginFigure(point, false, false);
                else
                    pen.LineTo(point, true, false);
            }

            PointAt(0, data[origin], true);
            if (window.Length <= columns * 2)
            {
                for (int i = 1; i < window.Length; i++)
                    PointAt(i, data[origin + i * 2], false);
            }
            else
            {
                for (int column = 0; column < columns; column++)
                {
                    int from = (int)((long)column * window.Length / columns);
                    int to = (int)((long)(column + 1) * window.Length / columns);
                    byte min = 255, max = 0;
                    int minAt = from, maxAt = from;
                    for (int i = from; i < to; i++)
                    {
                        byte value = data[origin + i * 2];
                        if (value < min) { min = value; minAt = i; }
                        if (value > max) { max = value; maxAt = i; }
                    }
                    if (minAt <= maxAt)
                    {
                        PointAt(minAt, min, false);
                        PointAt(maxAt, max, false);
                    }
                    else
                    {
                        PointAt(maxAt, max, false);
                        PointAt(minAt, min, false);
                    }
                }
                PointAt(window.Length - 1, data[origin + (window.Length - 1) * 2], false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(string color, double thickness = 1, bool dashed = false)
    {
        var pen = new Pen(Brush(color), thickness) { LineJoin = PenLineJoin.Round };
        if (dashed)
            pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ScopeDesk;

public partial class ReviewWindow : Window
{
    private readonly RecordingFile file;
    private readonly DispatcherTimer player;
    private readonly Stopwatch clock = new();
    private CaptureFrame? frame;
    private int requested, loaded = -1;
    private int from, length;
    private int? firstCursor, secondCursor;
    private double time, playOrigin, panStart;
    private int panFrom;
    private bool loading, closed, panning, exporting;
    private double extremaPosition = double.NaN;
    private bool loadingExtrema;
    private ChannelRanges? trailingExtrema;
    private CancellationTokenSource? search;
    private SearchHit? pendingHit;
    private int resultChannel;
    private string? searchStatusKey;
    private object[] searchStatusValues = [];

    internal ReviewWindow(RecordingFile recording)
    {
        InitializeComponent();
        file = recording;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        UpdateFileLabel();
        FileLabel.ToolTip = file.Path;
        player = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(28) };
        player.Tick += PlaybackTick;
        Timeline.Load(file);
        Timeline.Scrubbed += value =>
        {
            Pause();
            ShowTime(value);
        };
        Loaded += (_, _) => ShowTime(file.Entries[0].Start);
    }

    internal void RefreshLanguage()
    {
        UpdateFileLabel();
        UpdatePositionLabel();
        Timeline.RefreshLanguage();
        PlayButton.Content = L10n.Text(player.IsEnabled ? "PausePlayback" : "Play");
        FindEventsButton.Content = L10n.Text(search is null ? "Find" : "Cancel");
        if (searchStatusKey is { } key)
            SearchStatus.Text = L10n.Format(key, searchStatusValues);
        SearchResults.Items.Refresh();
        UpdatePlot();
    }

    private void UpdateFileLabel()
    {
        string source = file.IsDemo ? L10n.Text("Demo") : "Hantek 6022BE";
        FileLabel.Text = $"{Path.GetFileName(file.Path)}   ·   {source}   ·   {file.StartTime:g}"
            + (file.Incomplete ? L10n.Text("IncompleteFrame") : "");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    private void ShowTime(double position, SearchHit? hit = null)
    {
        time = position;
        Timeline.Move(time, keepVisible: true);
        requested = hit?.FrameIndex ?? file.AtOrBefore(time);
        pendingHit = hit;
        UpdatePositionLabel();
        PreviousButton.IsEnabled = requested > 0;
        NextButton.IsEnabled = requested < file.Entries.Count - 1;
        RequestExtrema(player.IsEnabled ? Math.Floor(time * 10) / 10 : time);
        if (loaded != requested && !loading)
            LoadLatest();
        else if (loaded == requested && frame is { } data && hit is { } selected)
        {
            FocusHit(data, selected);
            UpdatePlot();
        }
    }

    private void UpdatePositionLabel()
    {
        var entry = file.Entries[requested];
        PositionLabel.Text = $"{RecordingTimeline.FormatTime(time)} / {RecordingTimeline.FormatTime(file.Duration)}"
            + L10n.Format("FramePosition", requested + 1, file.Entries.Count);
        GapLabel.Text = time < entry.Start || time > entry.End ? L10n.Text("CaptureGap") : "";
    }

    private async void LoadLatest()
    {
        loading = true;
        try
        {
            while (!closed && requested != loaded)
            {
                int index = requested;
                var data = await Task.Run(() => file.Read(index));
                if (closed || index != requested)
                {
                    data.Dispose();
                    continue;
                }
                var old = frame;
                bool first = loaded < 0;
                frame = data;
                loaded = index;
                if (first || old?.Settings != data.Settings)
                {
                    from = 0;
                    length = data.TotalSamples;
                }
                else
                {
                    length = Math.Min(length, data.TotalSamples);
                    from = Math.Clamp(from, 0, data.TotalSamples - length);
                }
                if (!player.IsEnabled)
                    firstCursor = secondCursor = null;
                else
                {
                    firstCursor = firstCursor < data.TotalSamples ? firstCursor : null;
                    secondCursor = secondCursor < data.TotalSamples ? secondCursor : null;
                }
                if (pendingHit is { } hit)
                    FocusHit(data, hit);
                UpdatePlot();
                old?.Dispose();
            }
        }
        catch (Exception error)
        {
            Pause();
            if (!closed)
                MessageBox.Show(this, error.Message, L10n.Text("ReadFrameError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            loading = false;
            if (closed)
                file.Dispose();
        }
    }

    private void Step(int delta)
    {
        Pause();
        ShowTime(file.Entries[Math.Clamp(requested + delta, 0, file.Entries.Count - 1)].Start);
    }

    private void PreviousClick(object sender, RoutedEventArgs e) => Step(-1);
    private void NextClick(object sender, RoutedEventArgs e) => Step(1);

    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (player.IsEnabled)
        {
            Pause();
            return;
        }
        if (time >= file.Duration)
            ShowTime(file.Entries[0].Start);
        playOrigin = time;
        clock.Restart();
        player.Start();
        PlayButton.Content = L10n.Text("PausePlayback");
    }

    private void Pause()
    {
        bool wasPlaying = player.IsEnabled;
        player.Stop();
        clock.Stop();
        PlayButton.Content = L10n.Text("Play");
        if (wasPlaying)
            RequestExtrema(time);
    }

    private async void RequestExtrema(double position)
    {
        if (extremaPosition == position || closed)
            return;
        extremaPosition = position;
        if (loadingExtrema)
            return;
        loadingExtrema = true;
        try
        {
            while (!closed)
            {
                double target = extremaPosition;
                var range = await Task.Run(() => file.ReadTrailingExtrema(target));
                if (closed)
                    return;
                if (target != extremaPosition)
                    continue;
                trailingExtrema = range;
                ShowTrailingExtrema();
                return;
            }
        }
        catch (IOException)
        {
            trailingExtrema = null;
            if (!closed)
                ShowTrailingExtrema();
        }
        finally { loadingExtrema = false; }
    }

    private void ShowTrailingExtrema()
    {
        FirstExtrema.ShowHistory(FirstVisible.IsChecked == true ? trailingExtrema?.First : null);
        SecondExtrema.ShowHistory(SecondVisible.IsChecked == true ? trailingExtrema?.Second : null);
    }

    private void PlaybackTick(object? sender, EventArgs e)
    {
        double position = Math.Min(file.Duration, playOrigin + clock.Elapsed.TotalSeconds);
        ShowTime(position);
        if (position >= file.Duration)
            Pause();
    }

    private void FitTimelineClick(object sender, RoutedEventArgs e) => Timeline.Fit();

    private void ToggleSearchClick(object sender, RoutedEventArgs e) =>
        SearchPanel.Visibility = SearchPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void SearchModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidthCondition is not null)
            WidthCondition.Visibility = SearchMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool ParseNumber(string text, out double number) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out number) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) && double.IsFinite(number);

    private async void FindEventsClick(object sender, RoutedEventArgs e)
    {
        if (search is { } running)
        {
            running.Cancel();
            return;
        }
        if (!ParseNumber(ThresholdInput.Text, out double threshold))
        {
            SetSearchStatus("InvalidVolts");
            return;
        }
        double? widthLimit = null;
        if (SearchMode.SelectedIndex == 1)
        {
            if (!ParseNumber(WidthInput.Text, out double width) || width <= 0)
            {
                SetSearchStatus("InvalidWidth");
                return;
            }
            widthLimit = width / 1e6;
        }

        Pause();
        SearchResults.ItemsSource = null;
        resultChannel = SearchChannel.SelectedIndex;
        var query = new SearchQuery(resultChannel, threshold, widthLimit);
        using var operation = new CancellationTokenSource();
        search = operation;
        FindEventsButton.Content = L10n.Text("Cancel");
        SetSearchStatus("Searching");
        var progress = new Progress<int>(percent =>
        {
            if (search == operation && !closed)
                SetSearchStatus("SearchingProgress", percent);
        });
        try
        {
            var hits = await Task.Run(() => RecordingSearch.Run(file, query, progress, operation.Token));
            if (closed)
                return;
            SearchResults.ItemsSource = hits;
            SetSearchStatus(hits.Count == RecordingSearch.MaxHits ? "FirstHits" : "FoundHits", hits.Count);
        }
        catch (OperationCanceledException)
        {
            if (!closed)
                SetSearchStatus("SearchCanceled");
        }
        catch (Exception error)
        {
            if (!closed)
            {
                searchStatusKey = null;
                SearchStatus.Text = error.Message;
            }
        }
        finally
        {
            search = null;
            if (!closed)
                FindEventsButton.Content = L10n.Text("Find");
        }
    }

    private void SetSearchStatus(string key, params object[] values)
    {
        searchStatusKey = key;
        searchStatusValues = values;
        SearchStatus.Text = L10n.Format(key, values);
    }

    private void SearchResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not SearchHit hit)
            return;
        Pause();
        (resultChannel == 0 ? FirstVisible : SecondVisible).IsChecked = true;
        ShowTime(hit.Time, hit);
    }

    private void FocusHit(CaptureFrame data, SearchHit hit)
    {
        int first = (int)Math.Round(hit.Sample);
        int last = hit.EndSample is { } end ? (int)Math.Round(end) : first;
        int section = hit.EndSample.HasValue ? Math.Max(128, (last - first) * 4)
            : Math.Max(128, data.Settings.VisibleSamples / 2);
        length = Math.Clamp(section, 3, data.TotalSamples);
        from = Math.Clamp((first + last - length) / 2, 0, data.TotalSamples - length);
        firstCursor = first;
        secondCursor = hit.EndSample.HasValue ? last : null;
        pendingHit = null;
    }

    private void FitFrameClick(object sender, RoutedEventArgs e)
    {
        if (frame is null)
            return;
        from = 0;
        length = frame.TotalSamples;
        UpdatePlot();
    }

    private void ZoomPlot(object sender, MouseWheelEventArgs e)
    {
        if (frame is null)
            return;
        double anchor = Plot.SampleAt(e.GetPosition(Plot));
        double fraction = (anchor - from) / (length - 1);
        length = Math.Clamp((int)Math.Round(length * (e.Delta > 0 ? .65 : 1 / .65)), 3, frame.TotalSamples);
        from = Math.Clamp((int)Math.Round(anchor - fraction * (length - 1)), 0, frame.TotalSamples - length);
        UpdatePlot();
        e.Handled = true;
    }

    private void PlotPressed(object sender, MouseButtonEventArgs e)
    {
        if (frame is null)
            return;
        Pause();
        Point position = e.GetPosition(Plot);
        int index = (int)Math.Round(Plot.SampleAt(position));
        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                firstCursor = index;
                break;
            case MouseButton.Right:
                secondCursor = index;
                break;
            case MouseButton.Middle:
                panning = Plot.CaptureMouse();
                panStart = position.X;
                panFrom = from;
                break;
            default:
                return;
        }
        UpdateCursors();
        e.Handled = true;
    }

    private void PanPlot(object sender, MouseEventArgs e)
    {
        if (!panning || !Plot.IsMouseCaptured || frame is null || Plot.PlotWidth <= 0)
            return;
        int next = Math.Clamp(panFrom + (int)Math.Round((panStart - e.GetPosition(Plot).X) * (length - 1) / Plot.PlotWidth),
            0, frame.TotalSamples - length);
        if (next != from)
        {
            from = next;
            UpdatePlot();
        }
    }

    private void PlotReleased(object sender, MouseButtonEventArgs e)
    {
        if (panning && e.ChangedButton == MouseButton.Middle)
        {
            panning = false;
            Plot.ReleaseMouseCapture();
        }
    }

    private void ChannelChanged(object sender, RoutedEventArgs e)
    {
        if (frame is not null)
            UpdatePlot();
    }

    private void ResetCursorsClick(object sender, RoutedEventArgs e)
    {
        firstCursor = secondCursor = null;
        UpdateCursors();
    }

    private void UpdatePlot()
    {
        if (frame is not { } data)
            return;
        var entry = file.Entries[loaded];
        var window = new WaveWindow(from, length, null);
        var display = new DisplayOptions(FirstVisible.IsChecked == true, SecondVisible.IsChecked == true, 0, TriggerEdge.Off,
            0, entry.FirstProbe, entry.SecondProbe, SplitChannels.IsChecked == true);
        Plot.Update(data, window, display, data.Settings, reviewMode: true);
        SampleLabel.Text = L10n.Format("PerChannel", data.Settings.Rate);
        FrameInfo.Text = L10n.Format("FrameInfo", RecordingTimeline.FormatTime(entry.Start), length);
        ShowMeasurements(0, FirstMeasures, FirstExtrema);
        ShowMeasurements(1, SecondMeasures, SecondExtrema);
        ShowTrailingExtrema();
        UpdateCursors();

        void ShowMeasurements(int channel, TextBlock stats, ExtremaTable extrema)
        {
            var measurement = display.Visible(channel) ? window.Measure(data, channel, display.Probe(channel)) : (Measurement?)null;
            stats.Text = measurement is { } value ? FormatMeasurement(channel, value) : L10n.Format("HiddenChannel", channel + 1);
            extrema.ShowWindow(measurement?.Range);
        }
    }

    private static string FormatMeasurement(int channel, Measurement measurement) =>
        $"CH{channel + 1}   {Units.Volts(measurement.PeakToPeak)} pp   ·   {Units.Volts(measurement.Rms)} RMS   ·   "
        + (measurement.Frequency is { } hz ? Units.Frequency(hz) : "—")
        + (measurement.Clipped ? L10n.Text("ClippingShort") : "");

    private void UpdateCursors()
    {
        Plot.SetCursors(firstCursor, secondCursor);
        ResetCursorsButton.IsEnabled = firstCursor.HasValue || secondCursor.HasValue;
        bool pair = firstCursor.HasValue && secondCursor.HasValue;
        ExportButton.Content = pair ? "CSV A–B" : L10n.Text("CsvView");
        ExportButton.IsEnabled = frame is not null && !exporting;
        if (frame is not { } data || !pair)
        {
            CursorTime.Text = "Δt —     1/Δt —";
            CursorVoltage.Text = "ΔV CH1 —     ΔV CH2 —";
            return;
        }
        int a = firstCursor!.Value, b = secondCursor!.Value;
        var entry = file.Entries[loaded];
        double dt = (double)(b - a) / entry.Settings.Rate.Hertz;
        double dv1 = (data.Sample(0, b) - data.Sample(0, a)) * entry.First.VoltsPerCount * entry.FirstProbe;
        double dv2 = (data.Sample(1, b) - data.Sample(1, a)) * entry.Second.VoltsPerCount * entry.SecondProbe;
        CursorTime.Text = $"Δt {(dt == 0 ? L10n.Text("ZeroTime") : Units.Time(dt))}     1/Δt {(dt == 0 ? "—" : Units.Frequency(Math.Abs(1 / dt)))}";
        CursorVoltage.Text = $"ΔV CH1 {Units.Volts(dv1)}     ΔV CH2 {Units.Volts(dv2)}";
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        Pause();
        var dialog = new SaveFileDialog
        {
            Title = L10n.Text("SaveRegionCsv"),
            Filter = L10n.Text("CsvFilter"),
            DefaultExt = ".csv",
            FileName = $"{Path.GetFileNameWithoutExtension(file.Path)}-frame-{loaded + 1}.csv"
        };
        if (dialog.ShowDialog(this) != true || frame is not { } data)
            return;
        int first = from, last = from + length - 1;
        if (firstCursor is { } a && secondCursor is { } b)
        {
            first = Math.Min(a, b);
            last = Math.Max(a, b);
        }
        var entry = file.Entries[loaded];
        exporting = true;
        ExportButton.IsEnabled = false;
        try
        {
            await CsvExport.Save(dialog.FileName, data, new WaveWindow(first, last - first + 1, null),
                entry.FirstProbe, entry.SecondProbe, entry.Start + (double)first / entry.Settings.Rate.Hertz);
        }
        catch (Exception error)
        {
            if (!closed)
                MessageBox.Show(this, error.Message, L10n.Text("SaveError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            exporting = false;
            ExportButton.IsEnabled = frame is not null;
        }
    }

    private void ReviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem or ListBoxItem)
            return;
        switch (e.Key)
        {
            case Key.Left: Step(-1); break;
            case Key.Right: Step(1); break;
            case Key.Space when !e.IsRepeat && Keyboard.FocusedElement is not ButtonBase: TogglePlayback(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ReviewClosed(object? sender, EventArgs e)
    {
        closed = true;
        player.Stop();
        search?.Cancel();
        frame?.Dispose();
        frame = null;
        if (!loading)
            file.Dispose();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}

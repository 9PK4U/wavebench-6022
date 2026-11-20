using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ScopeDesk;

public partial class MainWindow : Window
{
    private static readonly double[] TimeSteps =
        [.000005, .00001, .00002, .00005, .0001, .0002, .0005, .001, .002, .005, .01, .02, .05, .1, .2, .5];
    private readonly DispatcherTimer refresh;
    private readonly RecentCapture recent = new();
    private readonly ExtremaHistory extremaHistory = new();
    private CaptureSettings settings = new(CaptureSettings.Rates[5], .0005, CaptureSettings.Scales[5], CaptureSettings.Scales[5]);
    private DisplayOptions options;
    private CancellationTokenSource? cancellation;
    private Task? acquisition;
    private Task stopping = Task.CompletedTask;
    private Task finishing = Task.CompletedTask;
    private Task savingRecent = Task.CompletedTask;
    private CaptureFrame? pending;
    private CaptureFrame? shown;
    private RecordingWriter? recording;
    private int recordingProbes;
    private bool finishingRecording, openingRecording;
    private string? lastRecording;
    private WaveWindow visible;
    private bool ready, busy, updating, closing, allowClose, exporting;
    private ushort? version;
    private string statusKey = "Ready";

    public MainWindow(bool demo)
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        settings = settings with { HorizontalDivisions = DisplayPreferences.LoadHorizontalDivisions() };
        TimeSelect.ItemsSource = TimeSteps.Select(value => L10n.Format("PerDivision", Units.Time(value))).ToArray();
        TimeSelect.SelectedIndex = Array.IndexOf(TimeSteps, settings.SecondsPerDivision);
        DivisionSelect.ItemsSource = CaptureSettings.HorizontalDivisionOptions;
        DivisionSelect.SelectedItem = settings.HorizontalDivisions;
        FirstScale.ItemsSource = SecondScale.ItemsSource = CaptureSettings.Scales;
        FirstScale.SelectedItem = settings.First;
        SecondScale.SelectedItem = settings.Second;
        FirstProbe.SelectedIndex = SecondProbe.SelectedIndex = TriggerSelect.SelectedIndex = EdgeSelect.SelectedIndex = 0;
        SourceSelect.SelectedIndex = !demo && HantekSource.IsPresent() ? 0 : 1;
        LanguageSelect.SelectedIndex = L10n.Language == "ru" ? 1 : 0;
        UpdateRateOptions();
        UpdateTriggerControl();
        ready = true;
        options = ReadOptions();
        RenderFrame();
        refresh = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        refresh.Tick += Refresh;
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || LanguageSelect.SelectedIndex < 0)
            return;
        string language = LanguageSelect.SelectedIndex == 1 ? "ru" : "en";
        if (language == L10n.Language)
            return;

        L10n.Apply(language);
        DisplayPreferences.SaveLanguage(language);
        updating = true;
        int time = TimeSelect.SelectedIndex;
        TimeSelect.ItemsSource = TimeSteps.Select(value => L10n.Format("PerDivision", Units.Time(value))).ToArray();
        TimeSelect.SelectedIndex = time;
        FirstScale.ItemsSource = SecondScale.ItemsSource = CaptureSettings.Scales.ToArray();
        FirstScale.SelectedItem = settings.First;
        SecondScale.SelectedItem = settings.Second;
        updating = false;
        UpdateRateOptions();
        RenderFrame();
        foreach (var review in Application.Current.Windows.OfType<ReviewWindow>().ToArray())
            review.RefreshLanguage();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        refresh.Start();
        StartAcquisition();
    }

    private void StartAcquisition()
    {
        if (acquisition is not null || closing)
            return;
        bool demo = SourceSelect.SelectedIndex == 1;
        version = null;
        SetStatus(demo ? "Demo" : "Connecting", demo ? "Cyan" : "Muted");
        PlotMessageText.SetResourceReference(TextBlock.TextProperty, "WaitingSamples");
        var session = new CancellationTokenSource();
        cancellation = session;
        acquisition = Task.Run(() => Acquire(demo, session));
        UpdateButtons();
    }

    private async Task Acquire(bool demo, CancellationTokenSource session)
    {
        CancellationToken token = session.Token;
        try
        {
            using ICaptureSource source = demo ? new DemoSource() : new HantekSource(token);
            if (source is HantekSource hardware)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (session != cancellation)
                        return;
                    version = hardware.Version;
                    SetStatus("UsbConnected", "Green");
                });
            }
            while (!token.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                int probes = Volatile.Read(ref recordingProbes);
                var frame = source.Read(Volatile.Read(ref settings), token);
                frame.TrailingExtrema = extremaHistory.Add(frame);
                recent.Add(frame, probes);
                Volatile.Read(ref recording)?.Add(frame, probes);
                Interlocked.Exchange(ref pending, frame)?.Dispose();
                var remaining = TimeSpan.FromMilliseconds(32) - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, token);
            }
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested)
                return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (session != cancellation || closing)
                    return;
                Refresh(this, EventArgs.Empty);
                cancellation = null;
                acquisition = null;
                session.Dispose();
                version = null;
                SetStatus("UsbError", "Amber");
                PlotMessageText.Text = error.Message;
                PlotMessage.Visibility = Visibility.Visible;
                UpdateButtons();
            });
        }
    }

    private Task StopAcquisition()
    {
        var session = cancellation;
        var worker = acquisition;
        cancellation = null;
        acquisition = null;
        if (session is not null)
            stopping = StopSession(session, worker!);
        return stopping;
    }

    private async Task StopSession(CancellationTokenSource session, Task worker)
    {
        await session.CancelAsync();
        await worker;
        session.Dispose();
        Interlocked.Exchange(ref pending, null)?.Dispose();
    }

    private async void SourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || busy || closing)
            return;
        busy = true;
        UpdateButtons();
        await StopAcquisition();
        if (closing)
            return;
        recent.Clear();
        extremaHistory.Clear();
        ReplaceShown(null);
        busy = false;
        StartAcquisition();
    }

    private async void RunClick(object sender, RoutedEventArgs e) => await ToggleRunning();

    private async Task ToggleRunning()
    {
        if (busy || closing)
            return;
        if (acquisition is null)
        {
            StartAcquisition();
            RenderFrame();
            return;
        }
        busy = true;
        UpdateButtons();
        await StopAcquisition();
        if (closing)
            return;
        busy = false;
        SetStatus("Pause", "Amber");
        PlotMessageText.SetResourceReference(TextBlock.TextProperty, "CapturePaused");
        RenderFrame();
    }

    private void AcquisitionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updating)
            return;
        if (sender is ComboBox control && (control == TimeSelect || control == DivisionSelect))
            UpdateRateOptions();
        if (RateSelect.SelectedItem is not SampleRate rate || FirstScale.SelectedItem is not Scale first ||
            SecondScale.SelectedItem is not Scale second || TimeSelect.SelectedIndex < 0 ||
            DivisionSelect.SelectedItem is not int divisions)
            return;
        Volatile.Write(ref settings, new CaptureSettings(rate, TimeSteps[TimeSelect.SelectedIndex], first, second, divisions));
        if (sender == DivisionSelect)
            DisplayPreferences.SaveHorizontalDivisions(divisions);
        UpdateTriggerControl();
        options = ReadOptions();
        RenderFrame();
    }

    private void UpdateRateOptions()
    {
        if (TimeSelect.SelectedIndex < 0 || DivisionSelect.SelectedItem is not int divisions)
            return;
        updating = true;
        int oldRate = (RateSelect.SelectedItem as SampleRate ?? settings.Rate).Hertz;
        double span = TimeSteps[TimeSelect.SelectedIndex] * divisions;
        var available = CaptureSettings.Rates.Where(rate =>
            rate.Hertz * span >= 10 && rate.Hertz * span + 1 <= CaptureSettings.MaxVisibleSamples).ToArray();
        RateSelect.ItemsSource = available;
        RateSelect.SelectedItem = available.MinBy(rate => Math.Abs(rate.Hertz - oldRate));
        updating = false;
    }

    private void DisplayChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || updating)
            return;
        if (sender != Level)
            UpdateTriggerControl();
        options = ReadOptions();
        Volatile.Write(ref recordingProbes, (options.FirstProbe == 10 ? 1 : 0) | (options.SecondProbe == 10 ? 2 : 0));
        RenderFrame();
    }

    private void UpdateTriggerControl()
    {
        updating = true;
        int channel = TriggerSelect.SelectedIndex == 1 ? 1 : 0;
        var scale = channel == 0 ? settings.First : settings.Second;
        int probe = (channel == 0 ? FirstProbe : SecondProbe).SelectedIndex == 1 ? 10 : 1;
        double range = Math.Min(scale.Value * 4, 125 / scale.CountsPerVolt) * probe;
        Level.Minimum = -range;
        Level.Maximum = range;
        Level.SmallChange = range / 100;
        Level.LargeChange = range / 10;
        Level.IsEnabled = TriggerSelect.IsEnabled = EdgeSelect.SelectedIndex != 2;
        updating = false;
    }

    private DisplayOptions ReadOptions() =>
        new(FirstSwitch.IsChecked == true, SecondSwitch.IsChecked == true, Math.Max(0, TriggerSelect.SelectedIndex),
            EdgeSelect.SelectedIndex switch { 0 => TriggerEdge.Rising, 1 => TriggerEdge.Falling, _ => TriggerEdge.Off },
            Level.Value, FirstProbe.SelectedIndex == 1 ? 10 : 1, SecondProbe.SelectedIndex == 1 ? 10 : 1,
            SplitChannels.IsChecked == true);

    private void AutoSetupClick(object sender, RoutedEventArgs e)
    {
        if (shown is not { } frame)
            return;
        if (AutoSetup.Find(frame, options) is not { } setup)
        {
            AutoNotice.SetResourceReference(TextBlock.TextProperty, "AutoNoSignal");
            return;
        }
        updating = true;
        TriggerSelect.SelectedIndex = setup.Channel;
        EdgeSelect.SelectedIndex = 0;
        (setup.Channel == 0 ? FirstSwitch : SecondSwitch).IsChecked = true;
        updating = false;
        if (setup.Frequency is { } frequency)
        {
            double target = 5 / (frequency * settings.HorizontalDivisions);
            TimeSelect.SelectedIndex = Array.IndexOf(TimeSteps, TimeSteps.MinBy(step => Math.Abs(Math.Log(step / target))));
        }
        UpdateTriggerControl();
        Level.Value = Math.Clamp(setup.Level, Level.Minimum, Level.Maximum);
        options = ReadOptions();
        if (setup.Frequency.HasValue)
            AutoNotice.Text = "";
        else
            AutoNotice.SetResourceReference(TextBlock.TextProperty, "AutoNoFrequency");
        RenderFrame();
    }

    private void Refresh(object? sender, EventArgs e)
    {
        if (recording is { } active)
        {
            RecordStatus.Text = L10n.Format("LiveRecordCount", active.Elapsed, active.Saved);
            if (active.Dropped > 0)
                RecordStatus.Text += L10n.Format("DroppedFrames", active.Dropped);
        }
        if (Interlocked.Exchange(ref pending, null) is not { } frame)
            return;
        if (frame.Settings == settings)
            ReplaceShown(frame);
        else
            frame.Dispose();
    }

    private void ReplaceShown(CaptureFrame? frame)
    {
        var old = shown;
        shown = frame;
        RenderFrame();
        old?.Dispose();
    }

    private void RenderFrame()
    {
        var current = shown?.Settings ?? settings;
        visible = shown is null ? default : WaveWindow.Select(shown, options);
        Plot.Update(shown, visible, options, current);
        GridScale.Text = $"{L10n.Format("PerDivision", Units.Time(current.SecondsPerDivision))}  ·  {Units.Time(current.SweepSeconds)}";
        TriggerLevelText.Text = Units.Volts(options.Level);
        TriggerStatus.Text = options.Edge == TriggerEdge.Off ? L10n.Text("TriggerOff") : shown is null ? "—" :
            L10n.Text(visible.TriggerSample.HasValue ? "Locked" : "Auto");
        TriggerStatus.Foreground = (Brush)FindResource(visible.TriggerSample.HasValue ? "Green" : "Muted");
        if (acquisition is not null)
        {
            PlotMessage.Visibility = shown is null || !options.FirstVisible && !options.SecondVisible
                ? Visibility.Visible : Visibility.Collapsed;
            PlotMessageText.Text = L10n.Text(shown is null ? "WaitingSamples" : "ShowChannel");
        }
        else if (shown is not null && statusKey != "UsbError")
        {
            PlotMessage.Visibility = options.FirstVisible || options.SecondVisible ? Visibility.Collapsed : Visibility.Visible;
            PlotMessageText.Text = L10n.Text("ShowChannel");
        }

        UpdateMeasurements(0, FirstMeasurements, FirstPp, FirstRms, FirstHz, FirstClipped, FirstExtrema);
        UpdateMeasurements(1, SecondMeasurements, SecondPp, SecondRms, SecondHz, SecondClipped, SecondExtrema);
        Footer.Text = shown is null ? L10n.Text("EmptyFooter")
            : L10n.Format("CaptureFooter", current.Rate, visible.Length)
              + (version is { } fw ? $"   ·   FW {fw >> 8:X}.{fw & 255:X2}" : "");
        UpdateButtons();
    }

    private void UpdateMeasurements(int channel, FrameworkElement card, TextBlock pp, TextBlock rms, TextBlock frequency,
        TextBlock clipped, ExtremaTable extrema)
    {
        bool enabled = options.Visible(channel);
        card.Opacity = enabled ? 1 : .48;
        var measurement = enabled && shown is not null ? visible.Measure(shown, channel, options.Probe(channel)) : (Measurement?)null;
        pp.Text = measurement is { } value ? Units.Volts(value.PeakToPeak) : "—";
        rms.Text = measurement is { } rmsValue ? Units.Volts(rmsValue.Rms) : "—";
        frequency.Text = measurement?.Frequency is { } hz ? Units.Frequency(hz) : "—";
        clipped.Visibility = measurement?.Clipped == true ? Visibility.Visible : Visibility.Collapsed;
        extrema.ShowWindow(measurement?.Range);
        var history = enabled ? shown?.TrailingExtrema : null;
        var range = channel == 0 ? history?.First : history?.Second;
        extrema.ShowHistory(range?.ApplyProbe(options.Probe(channel)));
    }

    private void SetStatus(string key, string color)
    {
        statusKey = key;
        StatusText.SetResourceReference(TextBlock.TextProperty, key);
        StatusDot.Fill = (Brush)FindResource(color);
    }

    private void UpdateButtons()
    {
        bool running = acquisition is not null;
        RunButton.Content = L10n.Text(running ? "Pause" : shown is not null ? "Resume" : "Run");
        RunButton.IsEnabled = !busy;
        SourceSelect.IsEnabled = !busy && recording is null && !finishingRecording;
        TimeSelect.IsEnabled = DivisionSelect.IsEnabled = RateSelect.IsEnabled =
            FirstScale.IsEnabled = SecondScale.IsEnabled = running || shown is null;
        SaveCsv.IsEnabled = shown is not null && !exporting;
        SavePng.IsEnabled = shown is not null && (options.FirstVisible || options.SecondVisible);
        AutoSetupButton.IsEnabled = running && shown is not null && !busy;
        RecentButton.IsEnabled = shown is not null && !busy && savingRecent.IsCompleted;
        RecordButton.Content = L10n.Text(recording is not null ? "StopRecord" : finishingRecording ? "Saving" : "Record");
        RecordButton.Foreground = (Brush)FindResource(recording is null ? "Ink" : "RecordRed");
        RecordButton.IsEnabled = !busy && !finishingRecording && (running || recording is not null);
        OpenRecordingButton.IsEnabled = !openingRecording;
    }

    private void PlotWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TimeSelect.IsEnabled)
            return;
        TimeSelect.SelectedIndex = Math.Clamp(TimeSelect.SelectedIndex - Math.Sign(e.Delta), 0, TimeSteps.Length - 1);
        e.Handled = true;
    }

    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.IsRepeat || Keyboard.FocusedElement is Button or ComboBox or CheckBox or Slider)
            return;
        e.Handled = true;
        await ToggleRunning();
    }

    private static SaveFileDialog ExportDialog(string extension, string filter) =>
        new()
        {
            Title = L10n.Format("SaveWaveform", extension.ToUpperInvariant()),
            Filter = filter, DefaultExt = extension, FileName = $"scope-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}"
        };

    private static SaveFileDialog RecordingDialog(string title, string prefix) => new()
    {
        Title = title,
        Filter = L10n.Text("RecordingFilter"),
        DefaultExt = ".scopedesk",
        FileName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.scopedesk"
    };

    private async void RecentClick(object sender, RoutedEventArgs e)
    {
        using var snapshot = recent.Snapshot();
        if (snapshot.Frames.Count == 0)
            return;
        bool demo = SourceSelect.SelectedIndex == 1;
        var dialog = RecordingDialog(L10n.Text("SaveRecentTitle"), "scope-recent");
        if (dialog.ShowDialog(this) != true)
            return;
        savingRecent = RecordingWriter.SaveRecent(dialog.FileName, demo, snapshot);
        UpdateButtons();
        try
        {
            await savingRecent;
            if (!closing)
                await OpenRecording(dialog.FileName);
        }
        catch (Exception error) { ShowExportError(error); }
        finally { UpdateButtons(); }
    }

    private async void RecordClick(object sender, RoutedEventArgs e)
    {
        if (recording is not null)
        {
            finishing = StopRecording(review: true);
            await finishing;
            return;
        }
        var dialog = RecordingDialog(L10n.Text("SaveRecordingTitle"), "scope");
        if (dialog.ShowDialog(this) != true || acquisition is null)
            return;
        try
        {
            var writer = new RecordingWriter(dialog.FileName, SourceSelect.SelectedIndex == 1);
            Volatile.Write(ref recording, writer);
            RecordStatus.Visibility = Visibility.Visible;
            UpdateButtons();
            await WatchRecording(writer);
        }
        catch (Exception error) { ShowExportError(error); }
    }

    private async Task WatchRecording(RecordingWriter writer)
    {
        try { await writer.Completion; }
        catch (Exception error)
        {
            if (recording != writer)
                return;
            Volatile.Write(ref recording, null);
            RecordStatus.Visibility = Visibility.Collapsed;
            UpdateButtons();
            ShowExportError(error);
        }
    }

    private async Task StopRecording(bool review)
    {
        var writer = Interlocked.Exchange(ref recording, null);
        if (writer is null)
            return;
        finishingRecording = true;
        RecordStatus.SetResourceReference(TextBlock.TextProperty, "SavingRecording");
        UpdateButtons();
        try
        {
            await writer.Finish();
            lastRecording = writer.Path;
            if (review && !closing && writer.Saved > 0)
                await OpenRecording(writer.Path);
        }
        catch (Exception error) { ShowExportError(error); }
        finally
        {
            finishingRecording = false;
            RecordStatus.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
    }

    private async void OpenRecordingClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L10n.Text("OpenRecording"),
            Filter = L10n.Text("RecordingFilter"),
            FileName = lastRecording ?? ""
        };
        if (dialog.ShowDialog(this) == true)
            await OpenRecording(dialog.FileName);
    }

    private async Task OpenRecording(string path)
    {
        if (openingRecording || closing)
            return;
        openingRecording = true;
        UpdateButtons();
        RecordingFile? file = null;
        try
        {
            file = await Task.Run(() => new RecordingFile(path));
            if (closing)
                return;
            var window = new ReviewWindow(file) { Owner = this };
            window.Show();
            file = null;
            lastRecording = path;
        }
        catch (Exception error)
        {
            if (!closing)
                MessageBox.Show(this, error.Message, L10n.Text("OpenRecordingError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            file?.Dispose();
            openingRecording = false;
            UpdateButtons();
        }
    }

    private async void SaveCsvClick(object sender, RoutedEventArgs e)
    {
        var dialog = ExportDialog("csv", L10n.Text("CsvFilter"));
        if (dialog.ShowDialog(this) != true || shown is not { } frame)
            return;
        exporting = true;
        UpdateButtons();
        try
        {
            await CsvExport.Save(dialog.FileName, frame, visible, options.FirstProbe, options.SecondProbe);
        }
        catch (Exception error) { ShowExportError(error); }
        finally
        {
            exporting = false;
            UpdateButtons();
        }
    }

    private void SavePngClick(object sender, RoutedEventArgs e)
    {
        var dialog = ExportDialog("png", L10n.Text("PngFilter"));
        if (dialog.ShowDialog(this) != true || shown is null)
            return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(Plot);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Plot.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(Plot.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(Plot);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(dialog.FileName);
            encoder.Save(file);
        }
        catch (Exception error) { ShowExportError(error); }
    }

    private void ShowExportError(Exception error)
    {
        if (!closing)
            MessageBox.Show(this, error.Message, L10n.Text("SaveError"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
            return;
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        refresh.Stop();
        // Defer Close so this Closing event can return first, even when no session is active.
        await Dispatcher.Yield(DispatcherPriority.Background);
        await StopAcquisition();
        recent.Clear();
        await StopRecording(review: false);
        try { await savingRecent; }
        catch (Exception) { }
        await finishing;
        ReplaceShown(null);
        allowClose = true;
        Close();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}

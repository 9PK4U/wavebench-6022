using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ScopeDesk;

internal record SampleRate(int Hertz, byte Code)
{
    public override string ToString() => Hertz >= 1e6 ? L10n.Format("MegaSamples", Hertz / 1e6)
        : L10n.Format("KiloSamples", Hertz / 1e3);
}

internal record Scale(double Value, byte Gain, byte Index, double CountsPerVolt)
{
    public override string ToString() => L10n.Format("PerDivision", Units.Volts(Value));
}

internal static class Units
{
    public static string Volts(double value) => value != 0 && Math.Abs(value) < 1
        ? L10n.Format("MilliVolts", value * 1000) : L10n.Format("Volts", value);
    public static string Time(double seconds) => seconds == 0 ? "0" : Math.Abs(seconds) < 0.001
        ? L10n.Format("Microseconds", seconds * 1e6) : Math.Abs(seconds) < 1
        ? L10n.Format("Milliseconds", seconds * 1e3) : L10n.Format("Seconds", seconds);
    public static string Frequency(double hertz) => hertz >= 1e6 ? L10n.Format("Megahertz", hertz / 1e6)
        : hertz >= 1000 ? L10n.Format("Kilohertz", hertz / 1000) : L10n.Format("Hertz", hertz);
}

internal record CaptureSettings(SampleRate Rate, double SecondsPerDivision, Scale First, Scale Second,
    int HorizontalDivisions = CaptureSettings.DefaultHorizontalDivisions)
{
    public const int DefaultHorizontalDivisions = 10;
    public static readonly int[] HorizontalDivisionOptions = [4, 6, 8, 10, 12, 16, 20, 30, 40, 50];
    public static readonly SampleRate[] Rates =
    [
        new(20_000, 102), new(100_000, 110), new(500_000, 150), new(1_000_000, 1),
        new(2_000_000, 2), new(5_000_000, 5), new(10_000_000, 10), new(12_000_000, 12), new(15_000_000, 15)
    ];

    public static readonly Scale[] Scales =
    [
        new(.02, 10, 0, 250), new(.05, 10, 1, 250), new(.1, 10, 2, 250), new(.2, 5, 3, 126.25),
        new(.5, 2, 4, 49.50), new(1, 1, 5, 24.75), new(2, 1, 6, 24.75), new(5, 1, 7, 24.75)
    ];

    public const int MaxVisibleSamples = 950_000;
    public double SweepSeconds => SecondsPerDivision * HorizontalDivisions;
    public int TriggerDivision => (int)Math.Round(HorizontalDivisions / 5.0);
    public int VisibleSamples => Math.Max(3, (int)Math.Round(Rate.Hertz * SweepSeconds) + 1);
    public int CaptureSamples => Math.Max(VisibleSamples * 2, Math.Clamp(Rate.Hertz / 150, 256, 8192));
}

internal readonly record struct Calibration(double Zero, double VoltsPerCount)
{
    public double Convert(byte value) => (value - Zero) * VoltsPerCount;
}

internal sealed class CaptureFrame(CaptureSettings settings, int skip, Calibration first, Calibration second,
    int? recordedSamples = null) : IDisposable
{
    private int owners = 1;
    public CaptureSettings Settings { get; } = settings;
    public int Skip { get; } = skip;
    public int TotalSamples { get; } = recordedSamples ?? settings.CaptureSamples;
    public long Timestamp { get; set; }
    public ChannelRanges? TrailingExtrema { get; set; }
    public Calibration FirstCalibration { get; } = first;
    public Calibration SecondCalibration { get; } = second;
    public byte[] Data { get; } = ArrayPool<byte>.Shared.Rent((skip + (recordedSamples ?? settings.CaptureSamples)) * 2);
    public byte Sample(int channel, int index) => Data[(Skip + index) * 2 + channel];
    public Calibration Channel(int channel) => channel == 0 ? FirstCalibration : SecondCalibration;

    // The USB worker retains its reference until it has published the frame to both consumers.
    public void Retain() => Interlocked.Increment(ref owners);
    public void Dispose()
    {
        if (Interlocked.Decrement(ref owners) == 0)
            ArrayPool<byte>.Shared.Return(Data);
    }
}

internal interface ICaptureSource : IDisposable
{
    CaptureFrame Read(CaptureSettings settings, CancellationToken cancellation);
}

internal sealed class HantekSource : ICaptureSource
{
    private const string ActiveId = "VID_04B5&PID_6022";
    private const string BootId = "VID_04B4&PID_6022";
    private const byte Sampling = 0xE3;
    private static readonly byte[] Start = [1], Stop = [0];
    private readonly WinUsbDevice usb;
    private readonly byte[] calibration = new byte[80];
    private CaptureSettings? last;
    public ushort Version { get; }

    public static bool IsPresent() => WinUsbDevice.Find(ActiveId) != null || WinUsbDevice.Find(BootId) != null;

    public HantekSource(CancellationToken cancellation)
    {
        usb = Connect(cancellation);
        try
        {
            Version = usb.FirmwareVersion();
            usb.PrepareBulk();
            usb.Read(0xA2, 8, calibration);
            usb.Write(0xE4, 0, [2]);
            usb.Write(0xE5, 0, [0x11]);
        }
        catch
        {
            usb.Dispose();
            throw;
        }
    }

    private static WinUsbDevice Connect(CancellationToken cancellation)
    {
        if (WinUsbDevice.Find(ActiveId) is { } active)
            return OpenActive(active);

        var boot = WinUsbDevice.Find(BootId) ??
            throw new IOException(L10n.Text("MissingScope"));
        IOException? startupError;
        using (var loader = new WinUsbDevice(boot))
            startupError = UploadFirmware(loader, cancellation);

        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 9000)
        {
            cancellation.ThrowIfCancellationRequested();
            if (WinUsbDevice.Find(ActiveId) is { } path)
                return OpenActive(path);
            cancellation.WaitHandle.WaitOne(150);
        }
        throw new IOException(startupError is null
            ? L10n.Text("DriverAfterFirmware")
            : L10n.Format("FirmwareDidNotStart", startupError.Message), startupError);
    }

    private static WinUsbDevice OpenActive(string path)
    {
        var result = new WinUsbDevice(path);
        try
        {
            if (result.FirmwareVersion() < 0x0200)
                throw new IOException(L10n.Text("OtherFirmware"));
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static IOException? UploadFirmware(WinUsbDevice loader, CancellationToken cancellation)
    {
        using var stream = typeof(HantekSource).Assembly.GetManifestResourceStream("ScopeDesk.Firmware.dso6022be-firmware.hex")!;
        using var reader = new StreamReader(stream);
        var records = new List<(ushort Address, byte[] Data)>();
        bool complete = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] != ':')
                throw new InvalidDataException(L10n.Text("BadFirmwareHex"));
            var bytes = Convert.FromHexString(line.AsSpan(1));
            if (bytes.Length < 5 || bytes[0] != bytes.Length - 5 || bytes.Sum(value => (int)value) % 256 != 0)
                throw new InvalidDataException(L10n.Text("BadFirmwareChecksum"));
            if (bytes[3] == 1)
            {
                complete = true;
                break;
            }
            var address = (ushort)(bytes[1] << 8 | bytes[2]);
            if (bytes[3] != 0 || address + bytes[0] > 0x4000)
                throw new InvalidDataException(L10n.Text("FirmwareOutOfRam"));
            records.Add((address, bytes[4..^1]));
        }
        if (!complete || records.Count == 0)
            throw new InvalidDataException(L10n.Text("FirmwareIncomplete"));

        cancellation.ThrowIfCancellationRequested();
        // Finish the short RAM upload once reset is asserted so the scope can enumerate again.
        loader.Write(0xA0, 0xE600, [1]);
        foreach (var record in records)
            loader.Write(0xA0, record.Address, record.Data);
        try
        {
            loader.Write(0xA0, 0xE600, [0]);
            return null;
        }
        catch (IOException error)
        {
            // The boot device can disappear before WinUSB reports the last transfer complete.
            return error;
        }
    }

    private Calibration CalibrationFor(Scale scale, int channel)
    {
        int position = scale.Index * 2 + channel;
        byte coarse = calibration[position], fine = calibration[48 + position], multiplier = calibration[32 + position];
        double zero = coarse is >= 96 and <= 160 ? coarse : 128;
        if (coarse is >= 96 and <= 160 && fine is > 0 and < 255)
            zero += (fine - 128) / 250.0;
        double gain = multiplier is 0 or 255 ? 1 : 1 + (multiplier - 128) / 500.0;
        return new Calibration(zero, gain / scale.CountsPerVolt);
    }

    public CaptureFrame Read(CaptureSettings settings, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (last?.Rate != settings.Rate)
            usb.Write(0xE2, 0, [settings.Rate.Code, 0]);
        if (last?.First != settings.First)
            usb.Write(0xE0, 0, [settings.First.Gain, settings.First.Index]);
        if (last?.Second != settings.Second)
            usb.Write(0xE1, 0, [settings.Second.Gain, settings.Second.Index]);
        last = settings;

        int total = (settings.CaptureSamples + 3071) / 512 * 512;
        var frame = new CaptureFrame(settings, total - settings.CaptureSamples,
            CalibrationFor(settings.First, 0), CalibrationFor(settings.Second, 1));
        try
        {
            using var abort = cancellation.Register(usb.CancelRead);
            usb.Flush();
            usb.Write(Sampling, 0, Start);
            frame.Timestamp = Stopwatch.GetTimestamp() + (long)((double)frame.Skip / settings.Rate.Hertz * Stopwatch.Frequency);
            uint timeout = (uint)(750 + 2000.0 * total / settings.Rate.Hertz);
            usb.Capture(frame.Data, total * 2, timeout, cancellation);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { usb.Write(Sampling, 0, Stop); }
        catch (IOException) { }
        finally { usb.Dispose(); }
    }
}

internal sealed class DemoSource : ICaptureSource
{
    private readonly Stopwatch time = Stopwatch.StartNew();

    public CaptureFrame Read(CaptureSettings settings, CancellationToken cancellation)
    {
        var first = new Calibration(128, 1 / settings.First.CountsPerVolt);
        var second = new Calibration(128, 1 / settings.Second.CountsPerVolt);
        var frame = new CaptureFrame(settings, 0, first, second) { Timestamp = Stopwatch.GetTimestamp() };
        double phase = time.Elapsed.TotalSeconds * .19;
        double step = 1.0 / settings.Rate.Hertz;
        double firstFactor = 2 * Math.PI * 1000, secondFactor = 2 * Math.PI * 500;
        for (int i = 0; i < frame.TotalSamples; i++)
        {
            double t = i * step + phase;
            double carrier = firstFactor * t;
            double channel1 = 1.8 * Math.Sin(carrier) + .17 * Math.Sin(3 * carrier + .3) + .025 * Math.Sin(19 * carrier);
            double channel2 = .98 * Math.Tanh(8 * Math.Sin(secondFactor * t - .45)) + .03 * Math.Sin(4 * carrier);
            frame.Data[i * 2] = Quantize(channel1, first);
            frame.Data[i * 2 + 1] = Quantize(channel2, second);
        }
        return frame;
    }

    private static byte Quantize(double value, Calibration calibration) =>
        (byte)Math.Clamp((int)Math.Round(calibration.Zero + value / calibration.VoltsPerCount), 0, 255);

    public void Dispose() { }
}

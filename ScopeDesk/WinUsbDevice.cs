using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ScopeDesk;

internal sealed class WinUsbDevice : IDisposable
{
    private static readonly Guid UsbInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private readonly SafeFileHandle file;
    private readonly SafeWinUsbHandle usb;
    private const byte Endpoint = 0x86;

    public WinUsbDevice(string path)
    {
        file = CreateFile(path, 0xC0000000, 3, 0, 3, 0x40000080, 0);
        if (file.IsInvalid)
        {
            var error = Error(L10n.Text("OpenUsb"));
            file.Dispose();
            throw error;
        }

        if (!WinUsb_Initialize(file, out usb))
        {
            var error = Error(L10n.Text("InitWinUsb"));
            usb.Dispose();
            file.Dispose();
            throw error;
        }
    }

    public static string? Find(string hardwareId)
    {
        var guids = new HashSet<Guid> { UsbInterface };
        using var instances = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\USB\{hardwareId}");
        foreach (var instance in instances?.GetSubKeyNames() ?? [])
        {
            using var parameters = instances!.OpenSubKey($@"{instance}\Device Parameters");
            foreach (var key in new[] { "DeviceInterfaceGUIDs", "DeviceInterfaceGUID" })
            {
                string[] values = parameters?.GetValue(key) switch
                {
                    string[] many => many,
                    string one => [one],
                    _ => []
                };
                foreach (var value in values)
                    if (Guid.TryParse(value, out var guid))
                        guids.Add(guid);
            }
        }

        foreach (var guid in guids)
        {
            var searchGuid = guid;
            var set = SetupDiGetClassDevs(ref searchGuid, 0, 0, 0x12);
            if (set == new IntPtr(-1))
                continue;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new DeviceInterface { Size = Marshal.SizeOf<DeviceInterface>() };
                    if (!SetupDiEnumDeviceInterfaces(set, 0, ref searchGuid, index, ref data))
                        break;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out var size, 0);
                    if (size < 8)
                        continue;

                    var detail = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(detail, 8);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, 0))
                        {
                            var path = Marshal.PtrToStringUni(detail + 4);
                            if (path?.Contains(hardwareId + "#", StringComparison.OrdinalIgnoreCase) == true)
                                return path;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }
        return null;
    }

    public ushort FirmwareVersion()
    {
        byte[] descriptor = new byte[18];
        if (!WinUsb_GetDescriptor(usb, 1, 0, 0, descriptor, 18, out var read) || read != 18)
            throw Error(L10n.Text("UsbDescriptor"));
        return (ushort)(descriptor[12] | descriptor[13] << 8);
    }

    public void PrepareBulk()
    {
        if (!WinUsb_SetCurrentAlternateSetting(usb, 0))
            throw Error(L10n.Text("UsbInterface"));
        Flush();
    }

    public void Flush()
    {
        if (!WinUsb_FlushPipe(usb, Endpoint))
            throw Error(L10n.Text("UsbFlush"));
    }

    public void Write(byte request, ushort value, byte[] data) => Transfer(0x40, request, value, data);
    public void Read(byte request, ushort value, byte[] data) => Transfer(0xC0, request, value, data);

    private void Transfer(byte type, byte request, ushort value, byte[] data)
    {
        var packet = new SetupPacket(type, request, value, 0, checked((ushort)data.Length));
        if (!WinUsb_ControlTransfer(usb, packet, data, data.Length, out var transferred, 0))
            throw Error(L10n.Format("UsbCommand", request));
        if (transferred != data.Length)
            throw new IOException(L10n.Format("UsbShortCommand", request, transferred, data.Length));
    }

    public unsafe void Capture(byte[] data, int count, uint timeout, CancellationToken cancellation)
    {
        if (!WinUsb_SetPipePolicy(usb, Endpoint, 3, 4, ref timeout))
            throw Error(L10n.Text("UsbTimeout"));

        int offset = 0;
        fixed (byte* pointer = data)
        {
            while (offset < count)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!WinUsb_ReadPipe(usb, Endpoint, pointer + offset, count - offset, out var read, 0))
                    throw Error(L10n.Text("UsbRead"));
                if (read == 0)
                    throw new IOException(L10n.Text("UsbEmptyPacket"));
                offset += read;
            }
        }
    }

    public void CancelRead() => WinUsb_AbortPipe(usb, Endpoint);

    public void Dispose()
    {
        usb.Dispose();
        file.Dispose();
    }

    private static IOException Error(string operation)
    {
        int code = Marshal.GetLastWin32Error();
        return new IOException($"{operation} · {new Win32Exception(code).Message} (Win32 {code})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterface
    {
        public int Size;
        public Guid Class;
        public uint Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct SetupPacket(byte Type, byte Request, ushort Value, ushort Index, ushort Length);

    private sealed class SafeWinUsbHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => WinUsb_Free(handle);
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid guid, nint enumerator, nint hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref DeviceInterface data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterface data, nint detail, int size, out int required, nint info);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle file, out SafeWinUsbHandle usb);
    [DllImport("winusb.dll")]
    private static extern bool WinUsb_Free(nint usb);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_GetDescriptor(SafeWinUsbHandle usb, byte type, byte index, ushort language, [Out] byte[] data, int length, out int read);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetCurrentAlternateSetting(SafeWinUsbHandle usb, byte setting);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_FlushPipe(SafeWinUsbHandle usb, byte pipe);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(SafeWinUsbHandle usb, byte pipe, uint policy, uint size, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ControlTransfer(SafeWinUsbHandle usb, SetupPacket packet, [In, Out] byte[] data, int length, out int count, nint overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern unsafe bool WinUsb_ReadPipe(SafeWinUsbHandle usb, byte pipe, byte* data, int length, out int count, nint overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_AbortPipe(SafeWinUsbHandle usb, byte pipe);
}

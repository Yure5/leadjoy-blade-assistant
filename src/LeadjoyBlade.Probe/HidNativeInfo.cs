using System.Runtime.InteropServices;

namespace LeadjoyBlade.Probe;

/// <summary>用 SetupAPI 直接打开 BLE HID 集合，读取 HID 报告描述符与能力。</summary>
internal static class HidNativeInfo
{
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_HID_GET_REPORT_DESCRIPTOR = 0x000B0007;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr device, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr data);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, ref HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int detailSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint controlCode, IntPtr inBuffer, int inSize, byte[] outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, byte[] buffer, int bytesToRead, out int bytesRead, IntPtr overlapped);

    public static void RunRead(int seconds)
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero) { Console.WriteLine("SetupDiGetClassDevs 失败"); return; }

        try
        {
            for (var index = 0; ; index++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data)) break;
                var path = GetPath(set, ref data);
                if (path == null || !(path.Contains("023537") && path.Contains("1119"))) continue;
                if (!path.Contains("col01", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains("col02", StringComparison.OrdinalIgnoreCase)) continue;

                Console.WriteLine($"\n=== {path}");
                var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle == new IntPtr(-1))
                {
                    Console.WriteLine($"  打开失败：{Marshal.GetLastWin32Error()}");
                    continue;
                }

                var token = new CancellationTokenSource();
                var reader = Task.Run(() =>
                {
                    var buffer = new byte[64];
                    while (!token.IsCancellationRequested)
                    {
                        if (!ReadFile(handle, buffer, buffer.Length, out var read, IntPtr.Zero)) break;
                        if (read > 0) Console.WriteLine($"  [in] {Convert.ToHexString(buffer.AsSpan(0, read))}");
                    }
                });
                Console.WriteLine($"  读取 {seconds} 秒，请按键…");
                Thread.Sleep(seconds * 1000);
                token.Cancel();
                CloseHandle(handle);
                try { reader.Wait(1000); } catch { }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    public static void Run()
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero) { Console.WriteLine("SetupDiGetClassDevs 失败"); return; }

        try
        {
            for (var index = 0; ; index++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data)) break;

                var path = GetPath(set, ref data);
                if (path == null || !(path.Contains("023537") && path.Contains("1119"))) continue;

                Console.WriteLine($"\n=== {path}");
                var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle == new IntPtr(-1))
                {
                    Console.WriteLine($"  打开失败：{Marshal.GetLastWin32Error()}");
                    continue;
                }

                try
                {
                    var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (HidD_GetAttributes(handle, ref attributes))
                        Console.WriteLine($"  VID={attributes.VendorID:X4} PID={attributes.ProductID:X4} ver={attributes.VersionNumber:X4}");

                    if (HidD_GetPreparsedData(handle, out var preparsed))
                    {
                        var caps = new HIDP_CAPS { Reserved = new ushort[17] };
                        if (HidP_GetCaps(preparsed, ref caps) == 0x00110000)
                            Console.WriteLine($"  usagePage=0x{caps.UsagePage:X4} usage=0x{caps.Usage:X4} in={caps.InputReportByteLength} out={caps.OutputReportByteLength} feat={caps.FeatureReportByteLength} buttons={caps.NumberInputButtonCaps}");
                        HidD_FreePreparsedData(preparsed);
                    }

                    var buffer = new byte[4096];
                    if (DeviceIoControl(handle, IOCTL_HID_GET_REPORT_DESCRIPTOR, IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero))
                    {
                        Console.WriteLine($"  报告描述符 {returned} 字节：");
                        Console.WriteLine(Convert.ToHexString(buffer.AsSpan(0, returned)));
                    }
                    else Console.WriteLine($"  读描述符失败：{Marshal.GetLastWin32Error()}");
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? GetPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required <= 0) return null;
        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

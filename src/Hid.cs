using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NickFanControl;

/// <summary>Beschreibung einer HID-Schnittstelle (eine "Top-Level-Collection").</summary>
public sealed class HidInfo
{
    public string Path { get; init; } = "";
    public ushort Vid { get; init; }
    public ushort Pid { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public int Interface { get; init; } = -1;
    public int InputLen { get; init; }
    public int OutputLen { get; init; }
    public int FeatureLen { get; init; }
    public string Product { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Serial { get; init; } = "";

    public override string ToString() =>
        $"{Vid:X4}:{Pid:X4}  if={Interface,2}  usage={UsagePage:X4}/{Usage:X4}  in={InputLen,3} out={OutputLen,3} feat={FeatureLen,3}  {Manufacturer} {Product}";
}

/// <summary>Geöffnetes HID-Gerät (Lesen/Schreiben von Reports, Feature-Reports).</summary>
public sealed class HidDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly bool _canReadWrite;
    private FileStream? _streamField;
    public HidInfo Info { get; }

    private FileStream _stream
    {
        get
        {
            if (!_canReadWrite) throw new IOException("Schnittstelle nur für Feature-Reports geöffnet");
            return _streamField ??= new FileStream(_handle, FileAccess.ReadWrite, 0, true);
        }
    }

    private HidDevice(HidInfo info, SafeFileHandle h, bool rw)
    {
        Info = info;
        _handle = h;
        _canReadWrite = rw;
    }

    public static HidDevice Open(HidInfo info)
    {
        var h = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (!h.IsInvalid) return new HidDevice(info, h, true);

        // Tastatur/Maus: Windows gibt keinen Lese-/Schreibzugriff – Feature-Reports gehen trotzdem (wie bei hidapi)
        int err = Marshal.GetLastWin32Error();
        h.Dispose();
        h = Native.CreateFile(info.Path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(err, "HID öffnen fehlgeschlagen");
        return new HidDevice(info, h, false);
    }

    /// <summary>Output-Report schreiben. data[0] = Report-ID. Wird auf die Report-Länge aufgefüllt.</summary>
    public void Write(byte[] data)
    {
        // wie hidapi: immer genau die Länge des Output-Reports schreiben (auffüllen bzw. kürzen)
        int len = Info.OutputLen > 0 ? Info.OutputLen : data.Length;
        var buf = new byte[len];
        Array.Copy(data, buf, Math.Min(data.Length, len));
        var t = _stream.WriteAsync(buf, 0, buf.Length);
        if (!t.Wait(1000)) { Native.CancelIoEx(_handle, IntPtr.Zero); throw new TimeoutException("HID-Schreiben hängt"); }
    }

    /// <summary>Input-Report lesen (mit Timeout). Gibt null bei Timeout zurück.</summary>
    public byte[]? Read(int timeoutMs = 500)
    {
        if (Info.InputLen <= 0) return null;
        var buf = new byte[Info.InputLen];
        var t = _stream.ReadAsync(buf, 0, buf.Length);
        try
        {
            if (!t.Wait(timeoutMs))
            {
                Native.CancelIoEx(_handle, IntPtr.Zero);
                try { t.Wait(200); } catch { }
                return null;
            }
            int n = t.Result;
            if (n <= 0) return null;
            // wie hidapi: Report-ID 0 (= "keine Report-ID") wird entfernt
            if (buf[0] == 0) return buf.AsSpan(1, n - 1).ToArray();
            return n == buf.Length ? buf : buf.AsSpan(0, n).ToArray();
        }
        catch (AggregateException) { return null; }
    }

    /// <summary>Feature-Report senden. data[0] = Report-ID.</summary>
    public bool SetFeature(byte[] data)
    {
        int len = Info.FeatureLen > 0 ? Info.FeatureLen : data.Length;
        var buf = new byte[len];
        Array.Copy(data, buf, Math.Min(data.Length, len));
        return Native.HidD_SetFeature(_handle, buf, buf.Length);
    }

    /// <summary>Feature-Report lesen. reportId wird in Byte 0 gesetzt.</summary>
    public byte[]? GetFeature(byte reportId, int length = 0)
    {
        int len = length > 0 ? length : Info.FeatureLen;
        if (len <= 0) return null;
        var buf = new byte[len];
        buf[0] = reportId;
        return Native.HidD_GetFeature(_handle, buf, buf.Length) ? buf : null;
    }

    public void Dispose()
    {
        try { _streamField?.Dispose(); } catch { }
        try { _handle.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ Aufzählen

    public static List<HidInfo> Enumerate()
    {
        var list = new List<HidInfo>();
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return list;
        try
        {
            var ifData = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
            {
                Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, needed, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    var info = Query(path);
                    if (info != null) list.Add(info);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    private static HidInfo? Query(string path)
    {
        // Zugriff 0 = nur Attribute lesen; klappt auch bei Tastatur/Maus, die Windows exklusiv hält
        using var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        if (!Native.HidD_GetAttributes(h, ref attr)) return null;

        ushort up = 0, u = 0; int inLen = 0, outLen = 0, featLen = 0;
        if (Native.HidD_GetPreparsedData(h, out var pre))
        {
            try
            {
                if (Native.HidP_GetCaps(pre, out var caps) == Native.HIDP_STATUS_SUCCESS)
                {
                    up = caps.UsagePage; u = caps.Usage;
                    inLen = caps.InputReportByteLength; outLen = caps.OutputReportByteLength; featLen = caps.FeatureReportByteLength;
                }
            }
            finally { Native.HidD_FreePreparsedData(pre); }
        }

        int iface = -1;
        var lower = path.ToLowerInvariant();
        int mi = lower.IndexOf("&mi_", StringComparison.Ordinal);
        if (mi >= 0 && mi + 6 <= lower.Length && int.TryParse(lower.AsSpan(mi + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out var n)) iface = n;

        return new HidInfo
        {
            Path = path, Vid = attr.VendorID, Pid = attr.ProductID, UsagePage = up, Usage = u, Interface = iface,
            InputLen = inLen, OutputLen = outLen, FeatureLen = featLen,
            Product = GetString(h, Native.HidD_GetProductString),
            Manufacturer = GetString(h, Native.HidD_GetManufacturerString),
            Serial = GetString(h, Native.HidD_GetSerialNumberString)
        };
    }

    private delegate bool StringGetter(SafeFileHandle h, byte[] buf, int len);

    private static string GetString(SafeFileHandle h, StringGetter f)
    {
        var buf = new byte[256];
        try
        {
            if (!f(h, buf, buf.Length)) return "";
            var s = Encoding.Unicode.GetString(buf);
            int z = s.IndexOf('\0');
            return (z >= 0 ? s[..z] : s).Trim();
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ Win32

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buf, int len);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll")]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
    }
}

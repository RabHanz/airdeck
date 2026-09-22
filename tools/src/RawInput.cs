// Shared Raw Input / HID plumbing for hidtool and remote-mapper.
// Built with the .NET Framework csc (C# 5), so no modern syntax.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static class Native
{
    public const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b;
    public const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_DEVNOTIFY = 0x00002000, RIDEV_PAGEONLY = 0x00000020;
    public const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    public struct USAGE_AND_PAGE { public ushort Usage; public ushort UsagePage; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList(IntPtr pList, ref uint count, uint cbSize);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint cbSize);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint cbHeader);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);
    [DllImport("hid.dll")]
    public static extern int HidP_GetButtonCaps(int reportType, byte[] caps, ref ushort len, IntPtr preparsed);
    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort len, IntPtr preparsed);
    [DllImport("hid.dll")]
    public static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);
    [DllImport("hid.dll")]
    public static extern int HidP_GetUsagesEx(int reportType, ushort link, [Out] USAGE_AND_PAGE[] list, ref uint len, IntPtr preparsed, byte[] report, uint reportLen);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    public static extern bool HidD_GetProductString(IntPtr h, StringBuilder buf, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    public static extern bool HidD_GetManufacturerString(IntPtr h, StringBuilder buf, int len);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}

class HidDevice
{
    public IntPtr Handle;
    public uint Type;
    public string Path = "";
    public int Vid = -1, Pid = -1;
    public string Interface = "", Collection = "";
    public int UsagePage, Usage;
    public int InputReportLength;
    public string Product = "", Manufacturer = "";
    public IntPtr Preparsed = IntPtr.Zero;   // owned unmanaged buffer (HID only)
    public bool UsesReportIds;
    public uint MaxUsages;

    public string TypeName { get { return Type == Native.RIM_TYPEMOUSE ? "mouse" : Type == Native.RIM_TYPEKEYBOARD ? "keyboard" : "hid"; } }

    public string Category
    {
        get
        {
            if (Type == Native.RIM_TYPEMOUSE) return "mouse";
            if (Type == Native.RIM_TYPEKEYBOARD) return "keyboard";
            if (UsagePage == 0x0C) return "consumer-control";
            if (UsagePage == 0x01 && Usage == 0x80) return "system-control";
            if (UsagePage == 0x01 && Usage == 0x0C) return "wireless-radio";
            if (UsagePage >= 0xFF00) return "vendor-specific";
            return "hid";
        }
    }

    public string InterfaceId { get { return Interface + (Collection == "" ? "" : "&" + Collection); } }

    public string Short
    {
        get { return string.Format("{0:X4}:{1:X4} {2} {3}", Vid, Pid, InterfaceId, Category); }
    }
}

static class Devices
{
    public static List<HidDevice> Enumerate()
    {
        var result = new List<HidDevice>();
        uint count = 0;
        uint entry = (uint)(IntPtr.Size * 2);
        Native.GetRawInputDeviceList(IntPtr.Zero, ref count, entry);
        IntPtr buf = Marshal.AllocHGlobal((int)(count * entry));
        try
        {
            uint got = Native.GetRawInputDeviceList(buf, ref count, entry);
            for (int i = 0; i < (int)got; i++)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + i * entry);
                var d = new HidDevice();
                d.Handle = Marshal.ReadIntPtr(p);
                d.Type = (uint)Marshal.ReadInt32(p, IntPtr.Size);
                Fill(d);
                result.Add(d);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    static void Fill(HidDevice d)
    {
        uint size = 0;
        Native.GetRawInputDeviceInfo(d.Handle, Native.RIDI_DEVICENAME, IntPtr.Zero, ref size);
        IntPtr nameBuf = Marshal.AllocHGlobal((int)(size * 2 + 2));
        try
        {
            Native.GetRawInputDeviceInfo(d.Handle, Native.RIDI_DEVICENAME, nameBuf, ref size);
            d.Path = Marshal.PtrToStringUni(nameBuf) ?? "";
        }
        finally { Marshal.FreeHGlobal(nameBuf); }

        var m = Regex.Match(d.Path, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            d.Vid = Convert.ToInt32(m.Groups[1].Value, 16);
            d.Pid = Convert.ToInt32(m.Groups[2].Value, 16);
        }
        m = Regex.Match(d.Path, @"&(MI_[0-9A-F]{2})", RegexOptions.IgnoreCase);
        if (m.Success) d.Interface = m.Groups[1].Value.ToUpper();
        m = Regex.Match(d.Path, @"&(COL[0-9A-F]{2})", RegexOptions.IgnoreCase);
        if (m.Success) d.Collection = m.Groups[1].Value.ToUpper();

        size = 32;
        IntPtr info = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(info, 32);
            if (Native.GetRawInputDeviceInfo(d.Handle, Native.RIDI_DEVICEINFO, info, ref size) != unchecked((uint)-1))
            {
                if (d.Type == Native.RIM_TYPEHID)
                {
                    d.Vid = Marshal.ReadInt32(info, 8);
                    d.Pid = Marshal.ReadInt32(info, 12);
                    d.UsagePage = (ushort)Marshal.ReadInt16(info, 20);
                    d.Usage = (ushort)Marshal.ReadInt16(info, 22);
                }
                else if (d.Type == Native.RIM_TYPEKEYBOARD) { d.UsagePage = 1; d.Usage = 6; }
                else { d.UsagePage = 1; d.Usage = 2; }
            }
        }
        finally { Marshal.FreeHGlobal(info); }

        if (d.Type == Native.RIM_TYPEHID) LoadPreparsed(d);

        // Product strings are best effort: zero-access handles work even for keyboards/mice.
        IntPtr h = Native.CreateFile(d.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h != new IntPtr(-1))
        {
            var sb = new StringBuilder(256);
            if (Native.HidD_GetProductString(h, sb, 512)) d.Product = sb.ToString();
            sb = new StringBuilder(256);
            if (Native.HidD_GetManufacturerString(h, sb, 512)) d.Manufacturer = sb.ToString();
            Native.CloseHandle(h);
        }
    }

    static void LoadPreparsed(HidDevice d)
    {
        uint size = 0;
        Native.GetRawInputDeviceInfo(d.Handle, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return;
        d.Preparsed = Marshal.AllocHGlobal((int)size);
        if (Native.GetRawInputDeviceInfo(d.Handle, Native.RIDI_PREPARSEDDATA, d.Preparsed, ref size) == unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(d.Preparsed);
            d.Preparsed = IntPtr.Zero;
            return;
        }
        var caps = new byte[64];
        if (Native.HidP_GetCaps(d.Preparsed, caps) == Native.HIDP_STATUS_SUCCESS)
        {
            d.InputReportLength = BitConverter.ToUInt16(caps, 4);
            int numButtonCaps = BitConverter.ToUInt16(caps, 46); // NumberInputButtonCaps
            int numValueCaps = BitConverter.ToUInt16(caps, 48);  // NumberInputValueCaps
            d.UsesReportIds = AnyReportId(d, numButtonCaps, true) || AnyReportId(d, numValueCaps, false);
        }
        d.MaxUsages = Native.HidP_MaxUsageListLength(0, 0, d.Preparsed);
    }

    public static Dictionary<string, object> Describe(HidDevice d)
    {
        return new Dictionary<string, object>
        {
            { "vid", "0x" + d.Vid.ToString("X4") }, { "pid", "0x" + d.Pid.ToString("X4") },
            { "interface", d.InterfaceId }, { "raw_input_type", d.TypeName }, { "category", d.Category },
            { "usage_page", "0x" + d.UsagePage.ToString("X4") }, { "usage", "0x" + d.Usage.ToString("X4") },
            { "input_report_length", d.InputReportLength }, { "product", d.Product }, { "manufacturer", d.Manufacturer },
            { "path", d.Path },
        };
    }

    // HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS are both 72 bytes; ReportID is the byte at offset 2.
    static bool AnyReportId(HidDevice d, int n, bool buttons)
    {
        if (n <= 0) return false;
        ushort len = (ushort)n;
        var buf = new byte[72 * n];
        int st = buttons ? Native.HidP_GetButtonCaps(0, buf, ref len, d.Preparsed)
                         : Native.HidP_GetValueCaps(0, buf, ref len, d.Preparsed);
        if (st != Native.HIDP_STATUS_SUCCESS) return false;
        for (int i = 0; i < len; i++) if (buf[i * 72 + 2] != 0) return true;
        return false;
    }
}

class InputEvent
{
    public double Ms;          // stopwatch milliseconds
    public HidDevice Device;
    public string Kind;        // key, mouse, wheel, move, hid, usage, device
    public string Key;         // identity of the control (for down/up pairing), null for reports/moves
    public int Edge;           // 1 = down, -1 = up, 0 = no edge
    public string Text;
}

// Message-only window that receives WM_INPUT for keyboards, mice, consumer/system control
// and every HID top-level collection accepted by registerFilter. Events are delivered on the
// thread that created the listener.
class RawListener : NativeWindow
{
    readonly Dictionary<IntPtr, HidDevice> devices = new Dictionary<IntPtr, HidDevice>();
    readonly Dictionary<string, HashSet<uint>> usageState = new Dictionary<string, HashSet<uint>>();
    readonly Action<InputEvent> sink;
    readonly Stopwatch clock;

    public RawListener(Stopwatch clock, Func<HidDevice, bool> registerFilter, Action<InputEvent> sink)
    {
        this.clock = clock; this.sink = sink;
        foreach (var d in Devices.Enumerate()) devices[d.Handle] = d;

        var cp = new CreateParams();
        cp.Parent = new IntPtr(-3); // HWND_MESSAGE
        CreateHandle(cp);

        var pairs = new HashSet<uint>();
        pairs.Add(0x00010002); pairs.Add(0x00010006); pairs.Add(0x000C0001); pairs.Add(0x00010080);
        foreach (var d in devices.Values)
            if (registerFilter(d) && d.UsagePage != 0) pairs.Add(((uint)d.UsagePage << 16) | (uint)d.Usage);
        foreach (var pu in pairs)
        {
            ushort usage = (ushort)(pu & 0xFFFF);
            var one = new[] { new Native.RAWINPUTDEVICE
            {
                usUsagePage = (ushort)(pu >> 16),
                usUsage = usage,
                // A usage of 0 (common for vendor collections) can only be registered page-wide.
                dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY | (usage == 0 ? Native.RIDEV_PAGEONLY : 0),
                hwndTarget = Handle
            } };
            if (!Native.RegisterRawInputDevices(one, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                Warnings.Add(string.Format("could not register page 0x{0:X4} usage 0x{1:X4} (err {2})", pu >> 16, usage, Marshal.GetLastWin32Error()));
            else
                Registered.Add(string.Format("0x{0:X4}/0x{1:X4}", pu >> 16, usage));
        }
    }

    public readonly List<string> Warnings = new List<string>();
    public readonly List<string> Registered = new List<string>();
    public IEnumerable<HidDevice> KnownDevices { get { return devices.Values; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_INPUT) HandleInput(m.LParam);
        else if (m.Msg == Native.WM_INPUT_DEVICE_CHANGE)
        {
            devices.Clear();
            foreach (var d in Devices.Enumerate()) devices[d.Handle] = d;
            sink(new InputEvent { Ms = clock.Elapsed.TotalMilliseconds, Kind = "device", Text = m.WParam.ToInt32() == 1 ? "device arrived" : "device removed" });
        }
        base.WndProc(ref m);
    }

    void Emit(HidDevice dev, double ms, string kind, string key, int edge, string text)
    {
        sink(new InputEvent { Ms = ms, Device = dev, Kind = kind, Key = key, Edge = edge, Text = text });
    }

    void HandleInput(IntPtr hRaw)
    {
        int header = 8 + IntPtr.Size * 2;
        uint size = 0;
        Native.GetRawInputData(hRaw, Native.RID_INPUT, IntPtr.Zero, ref size, (uint)header);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, (uint)header) == unchecked((uint)-1)) return;
            uint type = (uint)Marshal.ReadInt32(buf, 0);
            IntPtr hDev = Marshal.ReadIntPtr(buf, 8);
            HidDevice dev;
            if (!devices.TryGetValue(hDev, out dev))
                dev = new HidDevice { Handle = hDev, Type = type, Path = "(unattributed/injected)" };
            double ms = clock.Elapsed.TotalMilliseconds;
            IntPtr body = new IntPtr(buf.ToInt64() + header);

            if (type == Native.RIM_TYPEKEYBOARD)
            {
                int make = (ushort)Marshal.ReadInt16(body, 0);
                int flags = (ushort)Marshal.ReadInt16(body, 2);
                int vk = (ushort)Marshal.ReadInt16(body, 6);
                bool up = (flags & 1) != 0;
                string sc = ((flags & 2) != 0 ? "E0 " : "") + ((flags & 4) != 0 ? "E1 " : "") + make.ToString("X2");
                Emit(dev, ms, "key", "vk" + vk.ToString("X2") + "/" + sc, up ? -1 : 1,
                     string.Format("KEY {0} vk=0x{1:X2} ({2}) sc={3}", up ? "UP" : "DOWN", vk, (Keys)vk, sc));
            }
            else if (type == Native.RIM_TYPEMOUSE)
            {
                int bflags = (ushort)Marshal.ReadInt16(body, 4);
                int bdata = Marshal.ReadInt16(body, 6);
                int dx = Marshal.ReadInt32(body, 12), dy = Marshal.ReadInt32(body, 16);
                string[] names = { "Left", "Right", "Middle", "X1", "X2" };
                for (int i = 0; i < 5; i++)
                {
                    if ((bflags & (1 << (i * 2))) != 0) Emit(dev, ms, "mouse", "mouse" + names[i], 1, "MOUSE " + names[i] + " DOWN");
                    if ((bflags & (2 << (i * 2))) != 0) Emit(dev, ms, "mouse", "mouse" + names[i], -1, "MOUSE " + names[i] + " UP");
                }
                if ((bflags & 0x0400) != 0) Emit(dev, ms, "wheel", null, 0, "MOUSE WHEEL " + bdata);
                if ((bflags & 0x0800) != 0) Emit(dev, ms, "wheel", null, 0, "MOUSE HWHEEL " + bdata);
                if (bflags == 0 && (dx != 0 || dy != 0)) Emit(dev, ms, "move", null, 0, string.Format("MOVE dx={0} dy={1}", dx, dy));
            }
            else
            {
                int sizeHid = Marshal.ReadInt32(body, 0);
                int count = Marshal.ReadInt32(body, 4);
                for (int i = 0; i < count; i++)
                {
                    var report = new byte[sizeHid];
                    Marshal.Copy(new IntPtr(body.ToInt64() + 8 + i * sizeHid), report, 0, sizeHid);
                    Emit(dev, ms, "hid", null, 0, "HID " + BitConverter.ToString(report).Replace("-", " "));
                    DecodeUsages(dev, report, ms);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // Diff the set of active button usages to produce USAGE DOWN/UP events.
    void DecodeUsages(HidDevice dev, byte[] report, double ms)
    {
        if (dev.Preparsed == IntPtr.Zero || dev.MaxUsages == 0) return;
        var list = new Native.USAGE_AND_PAGE[dev.MaxUsages];
        uint len = dev.MaxUsages;
        if (Native.HidP_GetUsagesEx(0, 0, list, ref len, dev.Preparsed, report, (uint)report.Length) != Native.HIDP_STATUS_SUCCESS) return;
        var now = new HashSet<uint>();
        for (int i = 0; i < len; i++) now.Add(((uint)list[i].UsagePage << 16) | list[i].Usage);

        string key = dev.Handle.ToString() + "/" + (dev.UsesReportIds && report.Length > 0 ? report[0] : 0);
        HashSet<uint> prev;
        if (!usageState.TryGetValue(key, out prev)) prev = new HashSet<uint>();
        foreach (var u in now.Where(u => !prev.Contains(u)).OrderBy(u => u))
            Emit(dev, ms, "usage", "usage" + u.ToString("X8"), 1, "USAGE DOWN " + UsageName(u));
        foreach (var u in prev.Where(u => !now.Contains(u)).OrderBy(u => u))
            Emit(dev, ms, "usage", "usage" + u.ToString("X8"), -1, "USAGE UP " + UsageName(u));
        usageState[key] = now;
    }

    static readonly Dictionary<uint, string> names = new Dictionary<uint, string>
    {
        { 0x000C00CF, "Voice Command" }, { 0x000C0223, "AC Home / Browser_Home" }, { 0x000C0224, "AC Back / Browser_Back" },
        { 0x000C0225, "AC Forward" }, { 0x000C0040, "Menu" }, { 0x000C0041, "Menu Pick" }, { 0x000C0042, "Menu Up" },
        { 0x000C0043, "Menu Down" }, { 0x000C0044, "Menu Left" }, { 0x000C0045, "Menu Right" }, { 0x000C0046, "Menu Escape" },
        { 0x000C00E9, "Volume Up" }, { 0x000C00EA, "Volume Down" }, { 0x000C00E2, "Mute" }, { 0x000C00CD, "Play/Pause" },
        { 0x000C00B5, "Next Track" }, { 0x000C00B6, "Prev Track" }, { 0x000C00B7, "Stop" }, { 0x000C0030, "Power" },
        { 0x000C0221, "AC Search" }, { 0x000C0189, "AL Internet Browser" }, { 0x000C0192, "AL Calculator" },
        { 0x000C018A, "AL Email" }, { 0x000C0183, "AL Media Select" }, { 0x000C0194, "AL My Computer" },
        { 0x00010081, "System Power Down" }, { 0x00010082, "System Sleep" }, { 0x00010083, "System Wake Up" },
    };

    public static string UsageName(uint u)
    {
        string n;
        string baseName = string.Format("page=0x{0:X4} usage=0x{1:X4}", u >> 16, u & 0xFFFF);
        return names.TryGetValue(u, out n) ? baseName + " (" + n + ")" : baseName;
    }
}

// Where the Interception keyboard filter is attached.
//
// Interception's own installer adds it as a filter on the whole Keyboard class and the whole Mouse
// class, so every keyboard and mouse on the PC goes through it. The driver hands out 10 keyboard
// and 10 mouse slots and only frees them when Windows restarts; each time Windows re-creates a
// device (waking from hibernation, re-plugging a receiver) it takes a new one. Once they run out,
// a keyboard or mouse that arrives gets no input at all.
//
// Airdeck attaches the filter only to the devices it maps: the remotes, plus any other keyboard
// the user adds in Settings (data\driver-devices.json). Every other keyboard and mouse never passes
// through the driver, so it can never be left without a slot.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

static class DriverScope
{
    public static readonly Guid KeyboardClass = new Guid("4d36e96b-e325-11ce-bfc1-08002be10318");
    public static readonly Guid MouseClass = new Guid("4d36e96f-e325-11ce-bfc1-08002be10318");
    public const string KeyboardFilter = "keyboard", MouseFilter = "mouse"; // Interception's service names

    // ---- class-wide filters (registry) ---------------------------------------------------

    static string ClassKey(Guid g) { return @"SYSTEM\CurrentControlSet\Control\Class\{" + g.ToString() + "}"; }

    public static List<string> ClassFilters(Guid g)
    {
        using (var k = Registry.LocalMachine.OpenSubKey(ClassKey(g)))
            return ((k == null ? null : k.GetValue("UpperFilters") as string[]) ?? new string[0]).ToList();
    }

    static void SetClassFilters(Guid g, List<string> filters)
    {
        using (var k = Registry.LocalMachine.OpenSubKey(ClassKey(g), true))
            k.SetValue("UpperFilters", filters.ToArray(), RegistryValueKind.MultiString);
    }

    public static bool ServiceInstalled
    {
        get { using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + KeyboardFilter)) return k != null; }
    }

    // True when the filter still sits on every keyboard (Interception's default install).
    public static bool ClassWide { get { return ClassFilters(KeyboardClass).Any(f => f.Equals(KeyboardFilter, StringComparison.OrdinalIgnoreCase)); } }

    // ---- remotes' keyboard interfaces -------------------------------------------------------

    // "HID\VID_4842&PID_0001&MI_02&COL01\" for each remote in remote-devices.json.
    public static List<string> RemotePrefixes(string root)
    {
        var list = new List<string>();
        var path = Path.Combine(root, "remote-devices.json");
        if (!File.Exists(path)) return list;
        var d = (Dictionary<string, object>)Json.Read(File.ReadAllText(path));
        foreach (var r in ((Dictionary<string, object>)d["remotes"]).Values.Cast<Dictionary<string, object>>())
        {
            int vid = Convert.ToInt32(((string)r["vid"]).Replace("0x", ""), 16), pid = Convert.ToInt32(((string)r["pid"]).Replace("0x", ""), 16);
            var ifaces = ((Dictionary<string, object>)r["buttons"]).Values.Cast<Dictionary<string, object>>()
                .Where(b => (b["source"] as string) == "keyboard" && b.ContainsKey("interface")).Select(b => (string)b["interface"]).Distinct();
            foreach (var i in ifaces) list.Add(string.Format(@"HID\VID_{0:X4}&PID_{1:X4}&{2}\", vid, pid, i).ToUpperInvariant());
        }
        return list;
    }

    // Other keyboards the user chose to map (Settings), as instance-id prefixes like the above.
    public static List<string> ExtraPrefixes(string root)
    {
        var path = Path.Combine(root, "data", "driver-devices.json");
        if (!File.Exists(path)) return new List<string>();
        var d = (Dictionary<string, object>)Json.Read(File.ReadAllText(path));
        object list;
        return d.TryGetValue("devices", out list) && list is object[] ? ((object[])list).OfType<string>().Select(s => s.ToUpperInvariant()).ToList() : new List<string>();
    }

    public static List<string> DevicePrefixes(string root) { return RemotePrefixes(root).Concat(ExtraPrefixes(root)).Distinct().ToList(); }

    public static bool IsRemote(string instanceId, List<string> prefixes)
    {
        return prefixes.Any(p => instanceId.ToUpperInvariant().StartsWith(p));
    }

    // Same receiver, any interface: remotes' own mice are left alone too (the air mouse keeps working).
    static bool SameReceiver(string instanceId, List<string> prefixes)
    {
        string id = instanceId.ToUpperInvariant();
        return prefixes.Any(p => { int mi = p.IndexOf("&MI_"); return id.StartsWith(mi > 0 ? p.Substring(0, mi) : p); });
    }

    // ---- SetupAPI ------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct SP_CLASSINSTALL_HEADER { public uint cbSize; public uint InstallFunction; }
    [StructLayout(LayoutKind.Sequential)]
    struct SP_PROPCHANGE_PARAMS { public SP_CLASSINSTALL_HEADER Header; public uint StateChange, Scope, HwProfile; }

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint prop, out uint regType, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint prop, byte[] buffer, uint size);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiSetClassInstallParams(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS p, int size);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiCallClassInstaller(uint dif, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    const uint DIGCF_PRESENT = 0x2, SPDRP_UPPERFILTERS = 0x11, DIF_PROPERTYCHANGE = 0x12, DICS_PROPCHANGE = 3, DICS_FLAG_GLOBAL = 1;

    public class Dev { public string Id; public bool Present; public List<string> Filters = new List<string>(); }

    // Every device of a class, present or not (a remote that is unplugged right now still gets its
    // filter set, so it works when it comes back).
    public static List<Dev> Devices(Guid cls)
    {
        var present = new HashSet<string>(Enumerate(cls, DIGCF_PRESENT, (set, d, id) => id), StringComparer.OrdinalIgnoreCase);
        return Enumerate(cls, 0, (set, d, id) => new Dev { Id = id, Present = present.Contains(id), Filters = GetFilters(set, ref d) });
    }

    delegate T Visit<T>(IntPtr set, SP_DEVINFO_DATA d, string id);

    static List<T> Enumerate<T>(Guid cls, uint flags, Visit<T> visit)
    {
        var result = new List<T>();
        IntPtr set = SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, flags);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
            {
                var sb = new StringBuilder(512);
                int req;
                if (SetupDiGetDeviceInstanceId(set, ref d, sb, sb.Capacity, out req)) result.Add(visit(set, d, sb.ToString()));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    static List<string> GetFilters(IntPtr set, ref SP_DEVINFO_DATA d)
    {
        var buf = new byte[1024];
        uint type, req;
        if (!SetupDiGetDeviceRegistryProperty(set, ref d, SPDRP_UPPERFILTERS, out type, buf, (uint)buf.Length, out req)) return new List<string>();
        return Encoding.Unicode.GetString(buf, 0, (int)Math.Min(req, (uint)buf.Length)).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    // Apply `change` to the device with this instance id (filters and/or restart).
    static bool OnDevice(Guid cls, string instanceId, Func<IntPtr, SP_DEVINFO_DATA, bool> change)
    {
        bool done = false;
        Enumerate(cls, 0, (set, d, id) =>
        {
            if (!done && id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) done = change(set, d);
            return 0;
        });
        return done;
    }

    public static bool SetFilters(Guid cls, string instanceId, List<string> filters)
    {
        return OnDevice(cls, instanceId, (set, d) =>
        {
            var bytes = filters.Count == 0 ? null : Encoding.Unicode.GetBytes(string.Join("\0", filters) + "\0\0");
            return SetupDiSetDeviceRegistryProperty(set, ref d, SPDRP_UPPERFILTERS, bytes, bytes == null ? 0u : (uint)bytes.Length);
        });
    }

    // Rebuild a device's driver stack (like disabling and re-enabling it), so it picks up new filters.
    public static bool Restart(Guid cls, string instanceId)
    {
        return OnDevice(cls, instanceId, (set, d) =>
        {
            var p = new SP_PROPCHANGE_PARAMS
            {
                Header = new SP_CLASSINSTALL_HEADER { cbSize = (uint)Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER)), InstallFunction = DIF_PROPERTYCHANGE },
                StateChange = DICS_PROPCHANGE, Scope = DICS_FLAG_GLOBAL,
            };
            return SetupDiSetClassInstallParams(set, ref d, ref p, Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS)))
                && SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref d);
        });
    }

    // ---- the two setups ----------------------------------------------------------------------------

    // Remove the class-wide filters, put the keyboard filter on each remote's keyboard interface,
    // and rebuild the user's own USB/Bluetooth keyboards and mice so they run without the driver
    // right away (the built-in keyboard keeps working as it is and drops the driver at the next
    // restart). restartRemotes rebuilds remotes that are not being served yet.
    public static List<string> RemotesOnly(string root, bool restartRemotes)
    {
        var log = new List<string>();
        var prefixes = DevicePrefixes(root);
        if (prefixes.Count == 0) { log.Add("No remotes in remote-devices.json - nothing changed."); return log; }

        foreach (var cls in new[] { KeyboardClass, MouseClass })
        {
            string name = cls == KeyboardClass ? KeyboardFilter : MouseFilter;
            var filters = ClassFilters(cls);
            if (filters.RemoveAll(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                SetClassFilters(cls, filters);
                log.Add(string.Format("{0} class: removed the driver (filters now: {1})", cls == KeyboardClass ? "Keyboard" : "Mouse", string.Join(", ", filters)));
            }
        }

        // Devices taken off the list lose the filter again.
        foreach (var dev in Devices(KeyboardClass).Where(d => !IsRemote(d.Id, prefixes) && d.Filters.Any(f => f.Equals(KeyboardFilter, StringComparison.OrdinalIgnoreCase))))
        {
            SetFilters(KeyboardClass, dev.Id, dev.Filters.Where(f => !f.Equals(KeyboardFilter, StringComparison.OrdinalIgnoreCase)).ToList());
            log.Add("filter removed from " + dev.Id);
            if (dev.Present) Restart(KeyboardClass, dev.Id);
        }

        foreach (var dev in Devices(KeyboardClass).Where(d => IsRemote(d.Id, prefixes)))
        {
            if (dev.Filters.Any(f => f.Equals(KeyboardFilter, StringComparison.OrdinalIgnoreCase))) continue;
            var filters = new List<string>(dev.Filters) { KeyboardFilter };
            log.Add((SetFilters(KeyboardClass, dev.Id, filters) ? "remote: filter set on " : "remote: FAILED to set filter on ") + dev.Id);
            if (dev.Present && restartRemotes) log.Add((Restart(KeyboardClass, dev.Id) ? "remote: restarted " : "remote: could not restart ") + dev.Id);
        }

        foreach (var cls in new[] { KeyboardClass, MouseClass })
            foreach (var dev in Devices(cls).Where(d => d.Present && d.Id.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0 && !SameReceiver(d.Id, prefixes)))
                log.Add((Restart(cls, dev.Id) ? "restarted " : "could not restart ") + dev.Id);
        return log;
    }

    // Back to Interception's default: the filter on every keyboard and mouse (restart Windows after).
    public static List<string> ClassWideAgain(string root)
    {
        var log = new List<string>();
        foreach (var cls in new[] { KeyboardClass, MouseClass })
        {
            string name = cls == KeyboardClass ? KeyboardFilter : MouseFilter, below = cls == KeyboardClass ? "kbdclass" : "mouclass";
            var filters = ClassFilters(cls);
            if (filters.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            int at = filters.FindIndex(f => f.Equals(below, StringComparison.OrdinalIgnoreCase));
            filters.Insert(at < 0 ? 0 : at, name);
            SetClassFilters(cls, filters);
            log.Add("class filters restored: " + string.Join(", ", filters));
        }
        var prefixes = DevicePrefixes(root);
        foreach (var dev in Devices(KeyboardClass).Where(d => IsRemote(d.Id, prefixes) && d.Filters.Any(f => f.Equals(KeyboardFilter, StringComparison.OrdinalIgnoreCase))))
        {
            SetFilters(KeyboardClass, dev.Id, dev.Filters.Where(f => !f.Equals(KeyboardFilter, StringComparison.OrdinalIgnoreCase)).ToList());
            log.Add("remote filter removed: " + dev.Id);
        }
        return log;
    }

    public static List<string> Status(string root)
    {
        var prefixes = DevicePrefixes(root);
        var log = new List<string>
        {
            "Keyboard class filters: " + string.Join(", ", ClassFilters(KeyboardClass)),
            "Mouse class filters:    " + string.Join(", ", ClassFilters(MouseClass)),
            ClassWide ? "Mode: every keyboard and mouse (Interception default)" : "Mode: remotes only",
        };
        foreach (var dev in Devices(KeyboardClass).Where(d => IsRemote(d.Id, prefixes)))
            log.Add(string.Format("remote {0} {1}: filters = {2}", dev.Present ? "present" : "absent ", dev.Id, string.Join(", ", dev.Filters)));
        return log;
    }

    // Remote keyboard interfaces that are plugged in but don't have the filter (a receiver in a USB
    // port it hasn't used before) - only meaningful in remotes-only mode. Readable without admin.
    public static List<string> RemotesMissingFilter(string root)
    {
        var prefixes = DevicePrefixes(root);
        return Devices(KeyboardClass).Where(d => d.Present && IsRemote(d.Id, prefixes) && !d.Filters.Any(f => f.Equals(KeyboardFilter, StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.Id).ToList();
    }
}

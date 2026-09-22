// Optional bridge to the Interception keyboard filter driver (oblitum/Interception).
//
// Only keyboard devices whose hardware id belongs to a known remote are filtered; the user's
// real keyboards are never intercepted. Each stroke from a remote is offered to a decide()
// callback: true swallows it (the remote button ran an action), false passes it on unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class InterceptionNative
{
    public const ushort FILTER_KEY_ALL = 0xFFFF, FILTER_KEY_NONE = 0;
    public const ushort KEY_UP = 0x01, KEY_E0 = 0x02, KEY_E1 = 0x04;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int Predicate(int device);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr interception_create_context();
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void interception_destroy_context(IntPtr ctx);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void interception_set_filter(IntPtr ctx, Predicate predicate, ushort filter);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int interception_wait_with_timeout(IntPtr ctx, uint ms);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int interception_receive(IntPtr ctx, int device, byte[] stroke, uint n);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int interception_send(IntPtr ctx, int device, byte[] stroke, uint n);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern uint interception_get_hardware_id(IntPtr ctx, int device, byte[] buffer, uint size);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int interception_is_keyboard(int device);
}

class InterceptionBridge : IDisposable
{
    public delegate bool Decide(string hardwareId, string scan, bool up);

    readonly Decide decide;
    readonly Func<string, bool> isRemote;
    IntPtr context;
    Thread thread;
    volatile bool running;
    readonly Dictionary<int, string> filtered = new Dictionary<int, string>();
    InterceptionNative.Predicate predicate; // keep the delegate alive while the driver holds it

    public bool Active { get { return context != IntPtr.Zero; } }
    public int FilteredCount { get { lock (filtered) return filtered.Count; } }

    public InterceptionBridge(Func<string, bool> isRemote, Decide decide)
    {
        this.isRemote = isRemote;
        this.decide = decide;
    }

    // Returns false when the DLL is missing or the driver is not loaded yet. The driver attaches to a
    // device when its stack is (re)built, so re-plugging a receiver activates it without a reboot;
    // callers simply retry.
    public bool Start(bool quiet = false)
    {
        if (context != IntPtr.Zero) return true;
        try { context = InterceptionNative.interception_create_context(); }
        catch (DllNotFoundException) { if (!quiet) Log.Write("interception.dll not found - remote keyboard keys stay unmapped"); return false; }
        if (context == IntPtr.Zero) { if (!quiet) Log.Write("Interception driver not loaded yet (install it, then re-plug the receivers or reboot)"); return false; }
        Log.Write("Interception driver active");
        Rescan();
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "interception", Priority = ThreadPriority.Highest };
        thread.Start();
        return true;
    }

    // When the driver was loaded without a reboot (receivers re-plugged) it attaches fine but cannot
    // report hardware ids. In that case every keyboard slot is watched, strokes pass straight
    // through, and a slot is linked to a remote the first time Raw Input reports the same key from
    // that remote (see Observe). Slots proven to be other keyboards are released again.
    bool learning;
    readonly HashSet<int> released = new HashSet<int>();
    readonly HashSet<int> seenSlots = new HashSet<int>(); // interception thread only
    readonly List<Tuple<int, string, bool, DateTime>> recent = new List<Tuple<int, string, bool, DateTime>>();
    string lastSummary = "";

    public bool Learning { get { return learning; } }

    public bool Covers(string hardwareTag)
    {
        lock (filtered) return filtered.Values.Any(v => v.IndexOf(hardwareTag, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // Re-evaluate which keyboard slots belong to remotes (call after device arrival/removal).
    public void Rescan()
    {
        if (context == IntPtr.Zero) return;
        lock (filtered)
        {
            var found = new Dictionary<int, string>();
            bool anyId = false;
            for (int dev = 1; dev <= 10; dev++)
            {
                string id = HardwareId(dev);
                if (id == null) continue;
                anyId = true;
                if (isRemote(id)) found[dev] = id;
            }
            learning = !anyId;
            // Slot numbers can change when devices come and go, so learned links start over too.
            filtered.Clear();
            released.Clear();
            foreach (var kv in found) filtered[kv.Key] = kv.Value;
            ApplyFilter();
            string summary = learning ? "learning mode (driver cannot report device ids until the next reboot)"
                                      : filtered.Count + " remote keyboard interface(s): " + string.Join("; ", filtered.Values);
            if (summary != lastSummary) Log.Write("Interception: {0}", summary);
            lastSummary = summary;
        }
    }

    void ApplyFilter()
    {
        predicate = d => { lock (filtered) return (learning ? !released.Contains(d) : filtered.ContainsKey(d)) ? 1 : 0; };
        InterceptionNative.interception_set_filter(context, InterceptionNative.interception_is_keyboard, InterceptionNative.FILTER_KEY_NONE);
        InterceptionNative.interception_set_filter(context, predicate, InterceptionNative.FILTER_KEY_ALL);
    }

    // Called with every Raw Input keystroke (hardwareTag = the remote it came from, or null for
    // any other keyboard). Returns true when a remote slot was newly linked.
    public bool Observe(string hardwareTag, string scan, bool up)
    {
        if (!learning || context == IntPtr.Zero) return false;
        int slot = -1;
        lock (recent)
        {
            var now = DateTime.UtcNow;
            var hit = recent.LastOrDefault(r => r.Item2 == scan && r.Item3 == up && (now - r.Item4).TotalMilliseconds < 400);
            if (hit == null) return false;
            recent.Remove(hit);
            slot = hit.Item1;
        }
        lock (filtered)
        {
            if (hardwareTag != null)
            {
                if (filtered.ContainsKey(slot)) return false;
                filtered[slot] = hardwareTag;
                Log.Write("Interception: linked keyboard slot {0} to {1}", slot, hardwareTag);
                return true;
            }
            if (!filtered.ContainsKey(slot) && released.Add(slot))
            {
                Log.Write("Interception: slot {0} is another keyboard - no longer filtered", slot);
                ApplyFilter();
            }
        }
        return false;
    }

    string HardwareId(int device)
    {
        var buf = new byte[1024];
        uint n = InterceptionNative.interception_get_hardware_id(context, device, buf, (uint)buf.Length);
        if (n == 0) return null;
        return Encoding.Unicode.GetString(buf, 0, (int)Math.Min(n, (uint)buf.Length)).Split('\0').FirstOrDefault(s => s.Length > 0);
    }

    void Loop()
    {
        var stroke = new byte[20];
        while (running)
        {
            int device = InterceptionNative.interception_wait_with_timeout(context, 250);
            if (device <= 0) continue;
            if (InterceptionNative.interception_receive(context, device, stroke, 1) <= 0) continue;

            string id;
            lock (filtered) filtered.TryGetValue(device, out id);
            ushort code = BitConverter.ToUInt16(stroke, 0);
            ushort state = BitConverter.ToUInt16(stroke, 2);
            string scan = ((state & InterceptionNative.KEY_E0) != 0 ? "E0 " : "") + ((state & InterceptionNative.KEY_E1) != 0 ? "E1 " : "") + code.ToString("X2");
            bool up = (state & InterceptionNative.KEY_UP) != 0;
            bool swallow = false;
            if (id != null)
            {
                try { swallow = decide(id, scan, up); }
                catch (Exception ex) { Log.Write("interception decide failed: {0}", ex.Message); }
            }
            else if (learning)
            {
                // Unknown slot: pass it on and remember it so Raw Input can tell us whose it was.
                if (seenSlots.Add(device)) Log.Write("Interception: first keystroke on unlinked slot {0} ({1})", device, scan);
                lock (recent)
                {
                    recent.Add(Tuple.Create(device, scan, up, DateTime.UtcNow));
                    if (recent.Count > 40) recent.RemoveAt(0);
                }
            }
            if (!swallow) InterceptionNative.interception_send(context, device, stroke, 1);
        }
    }

    public void Dispose()
    {
        running = false;
        if (thread != null) thread.Join(1000);
        if (context != IntPtr.Zero) InterceptionNative.interception_destroy_context(context);
        context = IntPtr.Zero;
    }
}

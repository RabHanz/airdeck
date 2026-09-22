// hidtool - command-line HID inspector.
//
//   hidtool enum    [--vid 4842,1915] [--json out.json]
//   hidtool monitor [--vid 4842,1915] [--log file.txt] [--moves]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class HidTool
{
    static HashSet<int> vids = new HashSet<int>();

    static bool Match(HidDevice d) { return vids.Count == 0 || vids.Contains(d.Vid); }

    static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("usage: hidtool enum|monitor [--vid 4842,1915] [--json f] [--log f] [--moves]"); return 1; }
        string v = Arg(args, "--vid");
        if (v != null) foreach (var s in v.Split(',')) vids.Add(Convert.ToInt32(s.Trim().Replace("0x", ""), 16));

        if (args[0] == "enum") return Enum(args);
        if (args[0] == "monitor") return Monitor(args);
        if (args[0] == "interception") return InterceptionProbe(args);
        Console.Error.WriteLine("unknown command " + args[0]);
        return 1;
    }

    static int Enum(string[] args)
    {
        var list = Devices.Enumerate().Where(Match).OrderBy(d => d.Vid).ThenBy(d => d.Pid).ThenBy(d => d.InterfaceId).ToList();
        foreach (var d in list)
            Console.WriteLine("{0,-44} page=0x{1:X4} usage=0x{2:X4} inLen={3,-3} {4} {5}", d.Short, d.UsagePage, d.Usage, d.InputReportLength, d.Manufacturer, d.Product);
        string json = Arg(args, "--json");
        if (json != null) File.WriteAllText(json, Json.Write(list.Select(Devices.Describe).ToList()));
        return 0;
    }

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr interception_create_context();
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] static extern void interception_destroy_context(IntPtr ctx);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] static extern uint interception_get_hardware_id(IntPtr ctx, int device, byte[] buf, uint size);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] static extern void interception_set_filter(IntPtr ctx, IcPredicate p, ushort filter);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] static extern int interception_wait_with_timeout(IntPtr ctx, uint ms);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] static extern int interception_receive(IntPtr ctx, int device, byte[] stroke, uint n);
    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)] static extern int interception_send(IntPtr ctx, int device, byte[] stroke, uint n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IcPredicate(int device);

    // hidtool interception [seconds]: list Interception device slots; optionally watch (and pass through)
    // keyboard strokes for a few seconds to see which slot a key arrives on.
    static int InterceptionProbe(string[] args)
    {
        IntPtr ctx = interception_create_context();
        if (ctx == IntPtr.Zero) { Console.WriteLine("no context: the Interception driver is not loaded"); return 1; }
        for (int d = 1; d <= 20; d++)
        {
            var buf = new byte[1024];
            uint n = interception_get_hardware_id(ctx, d, buf, (uint)buf.Length);
            Console.WriteLine("{0,2} {1,-8} {2}", d, d <= 10 ? "keyboard" : "mouse", n == 0 ? "-" : Encoding.Unicode.GetString(buf, 0, (int)Math.Min(n, 1024)).Split('\0')[0]);
        }
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 0;
        if (seconds > 0)
        {
            IcPredicate kb = d => d >= 1 && d <= 10 ? 1 : 0;
            interception_set_filter(ctx, kb, 0xFFFF);
            Console.WriteLine("watching keyboard strokes for {0} s (all keys pass through)...", seconds);
            var stroke = new byte[20];
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end)
            {
                int dev = interception_wait_with_timeout(ctx, 200);
                if (dev <= 0 || interception_receive(ctx, dev, stroke, 1) <= 0) continue;
                interception_send(ctx, dev, stroke, 1);
                Console.WriteLine("slot {0}: code=0x{1:X2} state=0x{2:X2}", dev, BitConverter.ToUInt16(stroke, 0), BitConverter.ToUInt16(stroke, 2));
            }
            interception_set_filter(ctx, kb, 0);
        }
        interception_destroy_context(ctx);
        return 0;
    }

    static int Monitor(string[] args)
    {
        var clock = Stopwatch.StartNew();
        string log = Arg(args, "--log");
        bool moves = args.Contains("--moves");
        StreamWriter w = log != null ? new StreamWriter(log, true) { AutoFlush = true } : null;
        new RawListener(clock, Match, e =>
        {
            if (e.Device != null && !Match(e.Device)) return;
            if (e.Kind == "move" && !moves) return;
            string line = string.Format("{0,10:0.0} ms  {1,-44} {2}", e.Ms, e.Device == null ? "-" : e.Device.Short, e.Text);
            Console.WriteLine(line);
            if (w != null) w.WriteLine(line);
        });
        Console.WriteLine("Monitoring raw input. Ctrl+C to stop.");
        Application.Run();
        return 0;
    }
}

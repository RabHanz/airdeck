// hidtool - command-line HID inspector.
//
//   hidtool enum    [--vid 4842,1915] [--json out.json]
//   hidtool monitor [--vid 4842,1915] [--log file.txt] [--moves]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

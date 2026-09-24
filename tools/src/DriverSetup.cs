using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// airdeck-driver.exe remotes-only [--restart-remotes] | class-wide | status
// Needs administrator rights for the first two. Writes what it did to logs\driver-setup.log.
static class DriverSetupProgram
{
    static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        if (!File.Exists(Path.Combine(root, "remote-devices.json"))) root = Path.GetFullPath(Path.Combine(root, "..", ".."));
        string mode = args.Length > 0 ? args[0] : "status";
        List<string> log;
        int code = 0;
        try
        {
            log = mode == "remotes-only" ? DriverScope.RemotesOnly(root, args.Contains("--restart-remotes"))
                : mode == "class-wide" ? DriverScope.ClassWideAgain(root)
                : DriverScope.Status(root);
        }
        catch (Exception ex) { log = new List<string> { "failed: " + ex.Message }; code = 1; }
        string text = string.Join(Environment.NewLine, log);
        Console.WriteLine(text);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            File.AppendAllText(Path.Combine(root, "logs", "driver-setup.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + mode + Environment.NewLine + text + Environment.NewLine + Environment.NewLine);
        }
        catch { }
        return code;
    }
}

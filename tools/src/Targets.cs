// Input spots: remembered input boxes across apps (VS Code terminal, a browser chat box, ...)
// that the remote can jump between. A spot is captured from whatever has keyboard focus and is
// re-found later with UI Automation; if that fails we click the remembered position instead.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

class InputSpot
{
    public string Label = "", Process = "", TitleContains = "", Browser = "";
    public string ElementName = "", AutomationId = "", ClassName = "", ControlType = "";
    public double OffsetX, OffsetY;      // element centre relative to the window's top-left (px)
    public double FracX, FracY;          // same, as a fraction of the window size (survives resizes)
    public string AfterKeys = "";        // optional chord sent after focusing (e.g. "ctrl+`")
    public string Method = "element";    // element | click | window

    public Dictionary<string, object> ToJson()
    {
        return new Dictionary<string, object>
        {
            { "label", Label }, { "process", Process }, { "titleContains", TitleContains }, { "browser", Browser },
            { "elementName", ElementName }, { "automationId", AutomationId }, { "className", ClassName }, { "controlType", ControlType },
            { "offsetX", OffsetX }, { "offsetY", OffsetY }, { "fracX", FracX }, { "fracY", FracY },
            { "afterKeys", AfterKeys }, { "method", Method },
        };
    }

    public static InputSpot FromJson(Dictionary<string, object> d)
    {
        Func<string, string> s = k => { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; };
        Func<string, double> n = k => { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : 0; };
        return new InputSpot
        {
            Label = s("label"), Process = s("process"), TitleContains = s("titleContains"), Browser = s("browser"),
            ElementName = s("elementName"), AutomationId = s("automationId"), ClassName = s("className"), ControlType = s("controlType"),
            OffsetX = n("offsetX"), OffsetY = n("offsetY"), FracX = n("fracX"), FracY = n("fracY"),
            AfterKeys = s("afterKeys"), Method = s("method") == "" ? "element" : s("method"),
        };
    }
}

static class Spots
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);

    static readonly string[] browsers = { "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "arc" };

    public static string ProcessName(IntPtr hwnd)
    {
        int pid;
        GetWindowThreadProcessId(hwnd, out pid);
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { return ""; }
    }

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string FriendlyApp(string process)
    {
        switch (process.ToLowerInvariant())
        {
            case "code": return "VS Code";
            case "msedge": return "Edge";
            case "chrome": return "Chrome";
            case "firefox": return "Firefox";
            case "windowsterminal": return "Terminal";
            case "claude": return "Claude";
            case "cursor": return "Cursor";
            default: return process;
        }
    }

    // Snapshot whatever currently has keyboard focus.
    public static InputSpot CaptureFocused()
    {
        IntPtr hwnd = GetAncestor(GetForegroundWindow(), 2); // GA_ROOT
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("No window is focused.");
        var spot = new InputSpot { Process = ProcessName(hwnd) };
        string title = Title(hwnd);
        bool isBrowser = browsers.Contains(spot.Process.ToLowerInvariant());
        if (isBrowser)
        {
            // Browser titles are "<tab title> - <profile> - Microsoft Edge"; the tab title is what identifies the page.
            spot.Browser = spot.Process;
            spot.TitleContains = title.Split(new[] { " - ", " — " }, StringSplitOptions.None)[0].Trim();
        }
        else if (spot.Process.Equals("Code", StringComparison.OrdinalIgnoreCase))
        {
            // "file.ts - project - Visual Studio Code": the project (workspace) is the stable part.
            var parts = title.Split(new[] { " - " }, StringSplitOptions.None);
            spot.TitleContains = parts.Length >= 3 ? parts[parts.Length - 2].Trim() : "";
        }

        RECT wr;
        GetWindowRect(hwnd, out wr);
        AutomationElement el = null;
        try { el = AutomationElement.FocusedElement; } catch (Exception ex) { Log.Write("UIA focus lookup failed: {0}", ex.Message); }
        if (el != null)
        {
            try
            {
                var c = el.Current;
                spot.ElementName = c.Name ?? "";
                spot.AutomationId = c.AutomationId ?? "";
                spot.ClassName = c.ClassName ?? "";
                spot.ControlType = c.ControlType != null ? c.ControlType.ProgrammaticName.Replace("ControlType.", "") : "";
                var r = c.BoundingRectangle;
                if (!r.IsEmpty && r.Width > 0)
                {
                    double cx = r.X + Math.Min(r.Width / 2, 60);   // near the start of wide boxes, where text goes
                    double cy = r.Y + r.Height / 2;
                    spot.OffsetX = cx - wr.L;
                    spot.OffsetY = cy - wr.T;
                }
            }
            catch (ElementNotAvailableException) { }
        }
        if (spot.OffsetX == 0 && spot.OffsetY == 0)
        {
            POINT p;
            GetCursorPos(out p);
            spot.OffsetX = p.X - wr.L;
            spot.OffsetY = p.Y - wr.T;
            spot.Method = "click";
        }
        double w = Math.Max(1, wr.R - wr.L), h = Math.Max(1, wr.B - wr.T);
        spot.FracX = spot.OffsetX / w;
        spot.FracY = spot.OffsetY / h;

        string what = spot.ElementName.Length > 0 && spot.ElementName.Length < 40 ? spot.ElementName
                    : spot.ControlType == "Edit" || spot.ControlType == "Document" ? "input" : spot.ControlType.ToLowerInvariant();
        spot.Label = FriendlyApp(spot.Process) + (spot.TitleContains != "" ? " · " + spot.TitleContains : "") + (what != "" ? " · " + what : "");
        if (spot.Label.Length > 70) spot.Label = spot.Label.Substring(0, 70);
        Log.Write("captured spot: {0} [{1} '{2}' id='{3}' class='{4}']", spot.Label, spot.ControlType, spot.ElementName, spot.AutomationId, spot.ClassName);
        return spot;
    }

    static List<IntPtr> TopWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            // Visible, not a tool window, has a title.
            if (IsWindowVisible(h) && (GetWindowLong(h, -20) & 0x80) == 0 && Title(h).Length > 0) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list; // z-order, topmost first
    }

    static IntPtr FindWindow(InputSpot s)
    {
        foreach (var h in TopWindows())
        {
            if (!ProcessName(h).Equals(s.Process, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.TitleContains != "" && Title(h).IndexOf(s.TitleContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            return h;
        }
        return IntPtr.Zero;
    }

    // For browser spots whose tab is not the active one: find the tab by title and select it.
    static IntPtr SelectBrowserTab(InputSpot s)
    {
        foreach (var h in TopWindows().Where(h => ProcessName(h).Equals(s.Process, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var win = AutomationElement.FromHandle(h);
                var tabs = win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                foreach (AutomationElement tab in tabs)
                {
                    if (tab.Current.Name.IndexOf(s.TitleContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object pattern;
                    if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) ((SelectionItemPattern)pattern).Select();
                    else if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) ((InvokePattern)pattern).Invoke();
                    Thread.Sleep(150);
                    return h;
                }
            }
            catch (Exception ex) { Log.Write("tab search failed: {0}", ex.Message); }
        }
        return IntPtr.Zero;
    }

    public static IntPtr Foreground { get { return GetAncestor(GetForegroundWindow(), 2); } }

    public static bool Matches(InputSpot s, IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && ProcessName(hwnd).Equals(s.Process, StringComparison.OrdinalIgnoreCase)
            && (s.TitleContains == "" || Title(hwnd).IndexOf(s.TitleContains, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
        // Windows only lets the process that received the last input change the foreground window.
        // Our own tagged Alt tap satisfies that rule.
        Output.Tap(new List<ushort> { 0xA4 });
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
    }

    static AutomationElement FindElement(IntPtr hwnd, InputSpot s)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var conds = new List<Condition>();
        if (s.AutomationId != "") conds.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, s.AutomationId));
        else
        {
            if (s.ElementName != "") conds.Add(new PropertyCondition(AutomationElement.NameProperty, s.ElementName));
            if (s.ClassName != "") conds.Add(new PropertyCondition(AutomationElement.ClassNameProperty, s.ClassName));
        }
        if (conds.Count == 0) return null;
        var cond = conds.Count == 1 ? conds[0] : new AndCondition(conds.ToArray());
        return root.FindFirst(TreeScope.Descendants, cond);
    }

    static void ClickAt(IntPtr hwnd, InputSpot s)
    {
        RECT wr;
        GetWindowRect(hwnd, out wr);
        double w = wr.R - wr.L, h = wr.B - wr.T;
        int x = (int)(wr.L + (s.FracX > 0 ? s.FracX * w : s.OffsetX));
        int y = (int)(wr.T + (s.FracY > 0 ? s.FracY * h : s.OffsetY));
        POINT before;
        GetCursorPos(out before);
        SetCursorPos(x, y);
        Output.Mouse(Win32.MOUSEEVENTF_LEFTDOWN);
        Output.Mouse(Win32.MOUSEEVENTF_LEFTUP);
        Thread.Sleep(30);
        SetCursorPos(before.X, before.Y);
    }

    // Brings the spot's window forward and puts the caret in its input. Runs off the UI thread.
    public static string Jump(InputSpot s)
    {
        var sw = Stopwatch.StartNew();
        IntPtr hwnd = FindWindow(s);
        if (hwnd == IntPtr.Zero && s.Browser != "" && s.TitleContains != "") hwnd = SelectBrowserTab(s);
        if (hwnd == IntPtr.Zero) return "window not found (" + s.Process + (s.TitleContains != "" ? ", \"" + s.TitleContains + "\"" : "") + ")";

        Activate(hwnd);
        Thread.Sleep(60);
        string how = "window";
        if (s.Method == "element")
        {
            AutomationElement el = null;
            var find = Task.Run(() => { try { return FindElement(hwnd, s); } catch { return null; } });
            if (find.Wait(1500)) el = find.Result;
            if (el != null)
            {
                try { el.SetFocus(); how = "element"; }
                catch (Exception) { el = null; }
            }
            if (el == null) { ClickAt(hwnd, s); how = "click (element not found)"; }
        }
        else if (s.Method == "click") { ClickAt(hwnd, s); how = "click"; }

        if (s.AfterKeys != "")
        {
            try { Output.Tap(Output.ParseChord(s.AfterKeys)); }
            catch (FormatException ex) { Log.Write("spot {0}: {1}", s.Label, ex.Message); }
        }
        Log.Write("jumped to {0} via {1} in {2} ms", s.Label, how, sw.ElapsedMilliseconds);
        return null;
    }
}

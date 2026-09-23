// airdeck - device-specific controller for air-mouse remotes (tray app).
//
// Each remote (identified by VID/PID) runs one profile from profiles\*.json. A profile maps
// logical buttons (ids from remote-devices.json) to actions; unmapped buttons pass through, and
// the "stock" profile leaves the remote completely untouched.
//
// How remote-only remapping works without a driver: consumer-control buttons (Home, Back, Voice,
// volume, media) reach Windows as a raw HID report from the remote first, and Windows then
// synthesises the matching virtual key (Browser_Home, Volume_Up...) a few ms later. The raw
// report tells us which device it came from, so the low-level keyboard hook blocks only the
// synthesised key that follows a mapped remote report. The same key from any other keyboard
// has no remote report in front of it and passes through untouched.
//
// Hotkeys (normal keyboard): Ctrl+Alt+Shift+F12 exit, F11 pause (all stock), F10 next profile.
// Command line: --exit  signals a running instance (even an elevated one) to quit.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

static class Win32
{
    public const int WH_KEYBOARD_LL = 13, HC_ACTION = 0;
    public const uint LLKHF_UP = 0x80;
    public const int WM_HOTKEY = 0x0312, PM_REMOVE = 1;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
    public const uint MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4, MOUSEEVENTF_RIGHTDOWN = 8, MOUSEEVENTF_RIGHTUP = 0x10,
                      MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40;
    public static readonly IntPtr Marker = new IntPtr(0x41525243); // "ARRC": tags our own injected input

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    // INPUT is 40 bytes on x64: type at 0, union at 8.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public int mx;            // MOUSEINPUT.dx
        [FieldOffset(12)] public int my;           // MOUSEINPUT.dy
        [FieldOffset(16)] public uint mouseData;
        [FieldOffset(20)] public uint mflags;
        [FieldOffset(8)] public ushort wVk;        // KEYBDINPUT
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint kflags;
        [FieldOffset(32)] public IntPtr mExtra;    // MOUSEINPUT.dwExtraInfo
        [FieldOffset(24)] public IntPtr kExtra;    // KEYBDINPUT.dwExtraInfo
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
}

static class Log
{
    static string path;
    static readonly object gate = new object();

    public static void Init(string dir)
    {
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "airdeck.log");
        if (File.Exists(path) && new FileInfo(path).Length > 2000000) File.Delete(path);
    }

    public static string FilePath { get { return path; } }

    public static void Write(string format, params object[] args)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + (args.Length > 0 ? string.Format(format, args) : format);
        Debug.WriteLine(line);
        lock (gate) { try { File.AppendAllText(path, line + Environment.NewLine); } catch (IOException) { } }
    }
}

// ------------------------------------------------------------------ key output

static class Output
{
    static readonly HashSet<ushort> extended = new HashSet<ushort>
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,
    };

    // Keys we are currently holding down on behalf of a remote button (for safe release).
    public static readonly HashSet<ushort> Held = new HashSet<ushort>();

    static Win32.INPUT Key(ushort vk, bool up)
    {
        var i = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
        i.wVk = vk;
        i.wScan = (ushort)Win32.MapVirtualKey(vk, 0);
        i.kflags = (up ? Win32.KEYEVENTF_KEYUP : 0) | (extended.Contains(vk) ? Win32.KEYEVENTF_EXTENDEDKEY : 0);
        i.kExtra = Win32.Marker;
        return i;
    }

    static void Send(List<Win32.INPUT> list)
    {
        if (list.Count == 0) return;
        uint sent = Win32.SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(Win32.INPUT)));
        if (sent != list.Count) Log.Write("SendInput sent {0}/{1} (err {2}) - an elevated window may be focused; run airdeck as admin to reach it", sent, list.Count, Marshal.GetLastWin32Error());
    }

    public static void Down(IList<ushort> chord)
    {
        var list = new List<Win32.INPUT>();
        lock (Held) foreach (var vk in chord) { list.Add(Key(vk, false)); Held.Add(vk); }
        Send(list);
    }

    const ushort Guard = 0xE8; // unassigned virtual key

    // For shortcuts like Win+Ctrl+Right: hold a neutral key first so the moment Win and Ctrl are
    // both down never looks like a bare Win+Ctrl (Wispr Flow's push-to-talk) to other apps.
    public static bool NeedsGuard(IList<ushort> chord)
    {
        bool win = chord.Any(v => v == 0x5B || v == 0x5C);
        bool other = chord.Any(v => v >= 0xA0 && v <= 0xA5 || v >= 0x10 && v <= 0x12);
        return win && other;
    }

    public static void GuardedDown(IList<ushort> chord)
    {
        if (!NeedsGuard(chord)) { Down(chord); return; }
        Down(new List<ushort> { Guard }.Concat(chord).ToList());
    }

    public static void GuardedUp(IList<ushort> chord)
    {
        if (!NeedsGuard(chord)) { Up(chord); return; }
        // Release the chord (key, then modifiers) while the guard is still down, then the guard.
        // The guard being pressed during Win also stops the Start menu opening on Win's release.
        var list = new List<Win32.INPUT>();
        lock (Held)
        {
            foreach (var vk in chord.Reverse()) { list.Add(Key(vk, true)); Held.Remove(vk); }
            list.Add(Key(Guard, true));
            Held.Remove(Guard);
        }
        Send(list);
    }

    public static void Up(IList<ushort> chord)
    {
        var list = new List<Win32.INPUT>();
        // Releasing a Win key on its own opens the Start menu; a masking key (unassigned vk 0xE8) prevents that.
        if (chord.Any(v => v == 0x5B || v == 0x5C || v == 0x12 || v == 0xA4 || v == 0xA5))
        {
            list.Add(Key(0xE8, false));
            list.Add(Key(0xE8, true));
        }
        lock (Held) foreach (var vk in chord.Reverse()) { list.Add(Key(vk, true)); Held.Remove(vk); }
        Send(list);
    }

    public static void Tap(IList<ushort> chord) { Down(chord); Up(chord); }

    public static void ReleaseAll()
    {
        List<ushort> held;
        lock (Held) held = Held.ToList();
        if (held.Count == 0) return;
        Log.Write("releasing held keys: {0}", string.Join(",", held.Select(v => "0x" + v.ToString("X2"))));
        Up(held);
    }

    public static void Text(string text)
    {
        var list = new List<Win32.INPUT>();
        foreach (char c in text)
        {
            foreach (bool up in new[] { false, true })
            {
                var i = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
                i.wScan = c;
                i.kflags = Win32.KEYEVENTF_UNICODE | (up ? Win32.KEYEVENTF_KEYUP : 0);
                i.kExtra = Win32.Marker;
                list.Add(i);
            }
        }
        Send(list);
    }

    public static void Mouse(uint flags)
    {
        var i = new Win32.INPUT { type = Win32.INPUT_MOUSE };
        i.mflags = flags;
        i.mExtra = Win32.Marker;
        Send(new List<Win32.INPUT> { i });
    }

    // "ctrl+shift+t", "win+1", "escape", "media_play_pause" -> ordered virtual-key list.
    public static List<ushort> ParseChord(string spec)
    {
        var aliases = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
        {
            { "ctrl", Keys.LControlKey }, { "control", Keys.LControlKey }, { "lctrl", Keys.LControlKey }, { "rctrl", Keys.RControlKey },
            { "shift", Keys.LShiftKey }, { "lshift", Keys.LShiftKey }, { "rshift", Keys.RShiftKey },
            { "alt", Keys.LMenu }, { "lalt", Keys.LMenu }, { "ralt", Keys.RMenu }, { "win", Keys.LWin }, { "lwin", Keys.LWin }, { "rwin", Keys.RWin },
            { "esc", Keys.Escape }, { "enter", Keys.Return }, { "backspace", Keys.Back }, { "del", Keys.Delete }, { "ins", Keys.Insert },
            { "pgup", Keys.PageUp }, { "pgdn", Keys.PageDown }, { "pagedown", Keys.PageDown }, { "menu", Keys.Apps },
            { "media_play_pause", Keys.MediaPlayPause }, { "media_next", Keys.MediaNextTrack }, { "media_prev", Keys.MediaPreviousTrack },
            { "volume_up", Keys.VolumeUp }, { "volume_down", Keys.VolumeDown }, { "volume_mute", Keys.VolumeMute },
            { "browser_back", Keys.BrowserBack }, { "browser_forward", Keys.BrowserForward }, { "browser_home", Keys.BrowserHome },
        };
        var result = new List<ushort>();
        foreach (var raw in spec.Split('+'))
        {
            string name = raw.Trim();
            if (name == "") continue;
            Keys k;
            if (aliases.TryGetValue(name, out k)) { }
            else if (name.Length == 1 && char.IsDigit(name[0])) k = Keys.D0 + (name[0] - '0');
            else if (!Enum.TryParse(name, true, out k)) throw new FormatException("unknown key '" + name + "' in \"" + spec + "\"");
            result.Add((ushort)k);
        }
        return result;
    }
}

// ------------------------------------------------------------------ Wispr Flow shortcuts

static class Flow
{
    public static List<ushort> Ptt = new List<ushort> { 0xA2, 0x5B };             // LCtrl + LWin
    public static List<ushort> HandsFree = new List<ushort> { 0xA2, 0x20, 0x5B };  // LCtrl + Space + LWin (Win last: see Load)
    public static List<ushort> Command = new List<ushort> { 0xA2, 0xA4, 0x5B };    // LCtrl + LAlt + LWin
    public static List<ushort> PasteLast = new List<ushort> { 0xA0, 0xA4, 0x5A };  // LShift + LAlt + Z

    static bool IsWin(ushort vk) { return vk == 0x5B || vk == 0x5C; }
    static bool IsModifier(ushort vk) { return (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C || (vk >= 0x10 && vk <= 0x12); }

    // Reads the user's actual Flow shortcuts (e.g. "162+91": "ptt") from %APPDATA%\Wispr Flow\config.json.
    public static void Load()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", "config.json");
        try
        {
            if (!File.Exists(path)) { Log.Write("Flow config not found; using default shortcuts"); return; }
            var map = FindShortcuts(Json.Read(File.ReadAllText(path)));
            if (map == null) { Log.Write("Flow shortcuts not found in config; using defaults"); return; }
            foreach (var kv in map)
            {
                var codes = kv.Key.Split('+').Select(s => { int v; return int.TryParse(s, out v) ? v : -1; }).ToList();
                if (codes.Any(c => c <= 0 || c > 0xFE)) continue; // skip mouse-button shortcuts
                // Order: Ctrl/Alt/Shift, then other keys, then Win last. Pressing Win before Space would
                // trigger Windows' own Win+Space "switch input language" shortcut.
                var chord = codes.Select(c => (ushort)c).OrderBy(v => IsWin(v) ? 2 : IsModifier(v) ? 0 : 1).ToList();
                string name = kv.Value as string;
                if (name == "ptt") Ptt = chord;
                else if (name == "popo") HandsFree = chord;
                else if (name == "lens") Command = chord;
                else if (name == "paste_last_text") PasteLast = chord;
            }
        }
        catch (Exception ex) { Log.Write("could not read Flow config: {0}", ex.Message); }
        Log.Write("Flow shortcuts: ptt={0} hands-free={1} command={2} paste-last={3}", Describe(Ptt), Describe(HandsFree), Describe(Command), Describe(PasteLast));
    }

    static Dictionary<string, object> FindShortcuts(object node)
    {
        var d = node as Dictionary<string, object>;
        if (d != null)
        {
            object v;
            if (d.TryGetValue("shortcuts", out v))
            {
                var s = v as Dictionary<string, object>;
                if (s != null && s.Values.Any(x => "ptt".Equals(x))) return s;
            }
            foreach (var child in d.Values) { var r = FindShortcuts(child); if (r != null) return r; }
        }
        var arr = node as object[];
        if (arr != null) foreach (var child in arr) { var r = FindShortcuts(child); if (r != null) return r; }
        return null;
    }

    public static string Describe(IEnumerable<ushort> chord) { return string.Join("+", chord.Select(v => ((Keys)v).ToString())); }
}

// ------------------------------------------------------------------ actions

abstract class RemoteAction
{
    public string Description = "";
    public virtual void Down() { }
    public virtual void Up() { }
    public virtual void Cancel() { }

    // A button spec may add gestures: { "action": ..., "hold": { "action": ... }, "double": { "action": ... } }.
    public static RemoteAction Create(Dictionary<string, object> spec)
    {
        var hold = spec.ContainsKey("hold") ? spec["hold"] as Dictionary<string, object> : null;
        var dbl = spec.ContainsKey("double") ? spec["double"] as Dictionary<string, object> : null;
        var tap = CreateSingle(spec);
        if (hold == null && dbl == null) return tap;
        var g = new GestureAction(tap, hold != null ? CreateSingle(hold) : null, dbl != null ? CreateSingle(dbl) : null);
        if (g.Tap == null && g.Hold == null && g.Double == null) return null;
        return g;
    }

    static RemoteAction CreateSingle(Dictionary<string, object> spec)
    {
        string action = spec.ContainsKey("action") ? (string)spec["action"] : "passthrough";
        Func<string, string> arg = k => spec.ContainsKey(k) ? spec[k] as string : null;
        switch (action)
        {
            case "passthrough": return null;
            case "block": return new BlockAction { Description = "blocked" };
            case "flow_ptt": return new HoldChord(() => Flow.Ptt) { Description = "Flow push-to-talk (hold)" };
            case "flow_command": return new HoldChord(() => Flow.Command) { Description = "Flow command mode (hold)" };
            case "flow_handsfree": return new TapChord(() => Flow.HandsFree) { Description = "Flow hands-free toggle" };
            case "flow_cancel": return new TapChord(() => new List<ushort> { (ushort)Keys.Escape }) { Description = "Flow cancel (Esc)" };
            case "keys":
                var chord = Output.ParseChord(arg("keys") ?? "");
                return new HoldChord(() => chord, true) { Description = "keys " + arg("keys") };
            case "text": return new TextAction(arg("text") ?? "") { Description = "type text" };
            case "left_click": return new ClickAction(Win32.MOUSEEVENTF_LEFTDOWN, Win32.MOUSEEVENTF_LEFTUP) { Description = "left click" };
            case "right_click": return new ClickAction(Win32.MOUSEEVENTF_RIGHTDOWN, Win32.MOUSEEVENTF_RIGHTUP) { Description = "right click" };
            case "middle_click": return new ClickAction(Win32.MOUSEEVENTF_MIDDLEDOWN, Win32.MOUSEEVENTF_MIDDLEUP) { Description = "middle click" };
            case "run": return new RunAction(arg("run"), arg("args")) { Description = "run " + arg("run") };
            case "spot_next": return new CallAction(() => Workflow.Step(+1)) { Description = "next input spot" };
            case "spot_prev": return new CallAction(() => Workflow.Step(-1)) { Description = "previous input spot" };
            case "spot_goto":
                int n;
                if (!int.TryParse(arg("spot") ?? Convert.ToString(spec.ContainsKey("spot") ? spec["spot"] : ""), out n) || n < 1)
                    throw new FormatException("spot_goto needs \"spot\": 1, 2, ...");
                return new CallAction(() => Workflow.Goto(n - 1)) { Description = "input spot " + n };
            case "desktop_next": return new DesktopAction(+1) { Description = "next desktop" };
            case "desktop_prev": return new DesktopAction(-1) { Description = "previous desktop" };
            case "flow_paste_last": return new TapChord(() => Flow.PasteLast) { Description = "Flow paste last transcript" };
            case "spot_capture": return new CallAction(() => Workflow.Capture()) { Description = "save input as spot" };
            case "profile_next": return new CallAction(() => Workflow.NextProfile()) { Description = "next profile" };
            case "app_next": return new CallAction(() => Workflow.SwitchApp(+1)) { Description = "next app" };
            case "app_prev": return new CallAction(() => Workflow.SwitchApp(-1)) { Description = "previous app" };
            case "screen_focus":
            {
                string dir = (arg("dir") ?? "").ToLowerInvariant();
                if (!new[] { "left", "right", "up", "down" }.Contains(dir)) throw new FormatException("screen_focus needs \"dir\": left, right, up or down");
                return new CallAction(() => Workflow.Screen("focus", dir)) { Description = "go to screen " + dir };
            }
            case "window_to_screen":
            {
                string dir = (arg("dir") ?? "next").ToLowerInvariant();
                if (!new[] { "next", "left", "right", "up", "down" }.Contains(dir)) throw new FormatException("window_to_screen needs \"dir\": next, left, right, up or down");
                return new CallAction(() => Workflow.Screen("move", dir)) { Description = "move window to " + (dir == "next" ? "next screen" : "screen " + dir) };
            }
        }
        throw new FormatException("unknown action '" + action + "'");
    }
}

// Hooks into the controller for actions that need app-level state (input spots, notices).
static class Workflow
{
    public static Action<int> Step = d => { };
    public static Action<int> Goto = i => { };
    public static Action Capture = () => { };
    public static Action<int> DesktopSwitched = d => { };
    public static Action NextProfile = () => { };
    public static Action<int> SwitchApp = d => { };
    public static Action<string, string> Screen = (what, dir) => { };
}

// Reads which virtual desktop is current (and its name) from Explorer's registry state.
static class VirtualDesktops
{
    public static Tuple<string, string> Current()
    {
        try
        {
            const string root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root))
            {
                var ids = k == null ? null : k.GetValue("VirtualDesktopIDs") as byte[];
                var cur = k == null ? null : k.GetValue("CurrentVirtualDesktop") as byte[];
                if (cur == null) // Windows 10 keeps it per session
                    using (var s = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\" + Process.GetCurrentProcess().SessionId + @"\VirtualDesktops"))
                        cur = s == null ? null : s.GetValue("CurrentVirtualDesktop") as byte[];
                if (ids == null || cur == null || cur.Length != 16) return null;
                int count = ids.Length / 16, index = -1;
                for (int i = 0; i < count && index < 0; i++)
                {
                    bool same = true;
                    for (int j = 0; j < 16 && same; j++) same = ids[i * 16 + j] == cur[j];
                    if (same) index = i;
                }
                if (index < 0) return null;
                string name = null;
                using (var d = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\Desktops\" + new Guid(cur).ToString("B").ToUpperInvariant()))
                    if (d != null) name = d.GetValue("Name") as string;
                return Tuple.Create("Desktop " + (index + 1) + " of " + count, string.IsNullOrEmpty(name) ? "" : name);
            }
        }
        catch (Exception ex) { Log.Write("desktop lookup failed: {0}", ex.Message); return null; }
    }
}

class DesktopAction : RemoteAction
{
    readonly int direction;
    public DesktopAction(int direction) { this.direction = direction; }
    public override void Down()
    {
        var chord = Output.ParseChord(direction > 0 ? "ctrl+win+right" : "ctrl+win+left");
        Output.GuardedDown(chord);
        Output.GuardedUp(chord);
        Workflow.DesktopSwitched(direction);
    }
}

// Tap / hold / double-tap on one button. With only a tap action the button reacts instantly
// elsewhere; here the tap waits until release (and, with a double-tap action, briefly after it).
class GestureAction : RemoteAction
{
    public const int HoldMs = 450, DoubleMs = 280;
    public RemoteAction Tap;                  // may be filled in with the button's own key (see ApplyProfile)
    public readonly RemoteAction Hold, Double;
    readonly System.Windows.Forms.Timer holdTimer = new System.Windows.Forms.Timer { Interval = HoldMs };
    readonly System.Windows.Forms.Timer doubleTimer = new System.Windows.Forms.Timer { Interval = DoubleMs };
    bool pressed, holding, waitingSecond, secondPress;

    public GestureAction(RemoteAction tap, RemoteAction hold, RemoteAction dbl)
    {
        Tap = tap; Hold = hold; Double = dbl;
        Description = (tap != null ? tap.Description : "default")
            + (hold != null ? " · hold: " + hold.Description : "") + (dbl != null ? " · double: " + dbl.Description : "");
        holdTimer.Tick += (s, e) =>
        {
            holdTimer.Stop();
            if (!pressed || Hold == null) return;
            holding = true;
            Hold.Down();
        };
        doubleTimer.Tick += (s, e) =>
        {
            doubleTimer.Stop();
            waitingSecond = false;
            Fire(Tap);
        };
    }

    static void Fire(RemoteAction a) { if (a != null) { a.Down(); a.Up(); } }

    public override void Down()
    {
        pressed = true;
        if (waitingSecond && Double != null)
        {
            doubleTimer.Stop();
            waitingSecond = false;
            secondPress = true;
            Double.Down();
            return;
        }
        if (Hold != null) holdTimer.Start();
    }

    public override void Up()
    {
        pressed = false;
        holdTimer.Stop();
        if (secondPress) { secondPress = false; Double.Up(); return; }
        if (holding) { holding = false; Hold.Up(); return; }
        if (Double != null) { waitingSecond = true; doubleTimer.Start(); return; }
        Fire(Tap);
    }

    public override void Cancel()
    {
        holdTimer.Stop(); doubleTimer.Stop();
        if (holding) Hold.Cancel();
        if (secondPress) Double.Cancel();
        pressed = holding = waitingSecond = secondPress = false;
    }
}

class CallAction : RemoteAction
{
    readonly Action call;
    public CallAction(Action call) { this.call = call; }
    public override void Down() { call(); }
}

class BlockAction : RemoteAction { }

// Holds a chord for as long as the remote button is held (push-to-talk style).
// `guarded` is for ordinary shortcuts; Flow's own chords must go out exactly as Flow expects them.
class HoldChord : RemoteAction
{
    readonly Func<List<ushort>> chord;
    readonly bool guarded;
    List<ushort> down;
    public HoldChord(Func<List<ushort>> chord, bool guarded = false) { this.chord = chord; this.guarded = guarded; }
    public override void Down()
    {
        if (down != null) return;
        down = chord();
        if (guarded) Output.GuardedDown(down); else Output.Down(down);
    }
    public override void Up()
    {
        if (down == null) return;
        if (guarded) Output.GuardedUp(down); else Output.Up(down);
        down = null;
    }
    public override void Cancel() { Up(); }
}

class TapChord : RemoteAction
{
    readonly Func<List<ushort>> chord;
    readonly bool guarded;
    public TapChord(Func<List<ushort>> chord, bool guarded = false) { this.chord = chord; this.guarded = guarded; }
    public override void Down()
    {
        var c = chord();
        if (guarded) { Output.GuardedDown(c); Output.GuardedUp(c); } else Output.Tap(c);
    }
}

class TextAction : RemoteAction
{
    readonly string text;
    public TextAction(string text) { this.text = text; }
    public override void Down() { Output.Text(text); }
}

class ClickAction : RemoteAction
{
    readonly uint downFlag, upFlag;
    bool isDown;
    public ClickAction(uint downFlag, uint upFlag) { this.downFlag = downFlag; this.upFlag = upFlag; }
    public override void Down() { if (isDown) return; isDown = true; Output.Mouse(downFlag); }
    public override void Up() { if (!isDown) return; isDown = false; Output.Mouse(upFlag); }
    public override void Cancel() { Up(); }
}

class RunAction : RemoteAction
{
    readonly string file, args;
    public RunAction(string file, string args) { this.file = file; this.args = args; }
    public override void Down()
    {
        try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("run {0} failed: {1}", file, ex.Message); }
    }
}


// Multi-monitor actions for a pointer that has to travel far: jump to the screen in a direction
// (pointer + focus follow) and move the current window to another screen.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Screens
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT { public int length, flags, showCmd; public Point min, max; public RECT normal; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    static Point Center(Rectangle r) { return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2); }

    // "Main", "Top", "Right"... relative to the primary screen, so people recognise them.
    public static string Name(Screen s)
    {
        if (s.Primary) return "Main screen";
        var p = Center(Screen.PrimaryScreen.Bounds);
        var c = Center(s.Bounds);
        int dx = c.X - p.X, dy = c.Y - p.Y;
        if (Math.Abs(dy) >= Math.Abs(dx)) return dy < 0 ? "Top screen" : "Bottom screen";
        return dx < 0 ? "Left screen" : "Right screen";
    }

    // Nearest screen whose centre lies in the given direction (off-axis distance counts double).
    public static Screen Neighbor(Screen from, string dir)
    {
        var o = Center(from.Bounds);
        Screen best = null;
        double bestScore = double.MaxValue;
        foreach (var s in Screen.AllScreens)
        {
            if (s.DeviceName == from.DeviceName) continue;
            var c = Center(s.Bounds);
            double along, across;
            switch (dir)
            {
                case "right": along = c.X - o.X; across = Math.Abs(c.Y - o.Y); break;
                case "left": along = o.X - c.X; across = Math.Abs(c.Y - o.Y); break;
                case "up": along = o.Y - c.Y; across = Math.Abs(c.X - o.X); break;
                default: along = c.Y - o.Y; across = Math.Abs(c.X - o.X); break; // down
            }
            if (along <= 0) continue;
            double score = along + 2 * across;
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    // Screens in reading order (top-to-bottom, then left-to-right) for "next screen" cycling.
    static List<Screen> Ordered()
    {
        return Screen.AllScreens.OrderBy(s => Center(s.Bounds).Y / 400).ThenBy(s => Center(s.Bounds).X).ToList();
    }

    static Screen Current()
    {
        IntPtr fg = Spots.Foreground;
        return fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.FromPoint(Cursor.Position);
    }

    // Pointer to the middle of the screen in that direction, and focus the app in front there.
    public static Tuple<string, string> Focus(string dir)
    {
        if (Screen.AllScreens.Length < 2) return Tuple.Create("One screen only", "");
        var target = Neighbor(Current(), dir);
        if (target == null) return Tuple.Create("No screen " + (dir == "up" ? "above" : dir == "down" ? "below" : "to the " + dir), Name(Current()));
        var c = Center(target.WorkingArea);
        SetCursorPos(c.X, c.Y);
        IntPtr top = Spots.AppWindows().FirstOrDefault(h => Screen.FromHandle(h).DeviceName == target.DeviceName);
        if (top != IntPtr.Zero) Spots.BringToFront(top);
        return Tuple.Create(Name(target), top != IntPtr.Zero ? Spots.FriendlyName(Spots.ProcessName(top)) : "no app open there");
    }

    // Move the window in front to another screen, keeping its relative position and size
    // (maximised windows stay maximised). The pointer follows it.
    public static Tuple<string, string> MoveWindow(string dir)
    {
        IntPtr h = Spots.Foreground;
        if (h == IntPtr.Zero) return Tuple.Create("No window to move", "");
        if (Screen.AllScreens.Length < 2) return Tuple.Create("One screen only", "");
        var from = Screen.FromHandle(h);
        Screen to;
        if (dir == "next")
        {
            var order = Ordered();
            to = order[(order.FindIndex(s => s.DeviceName == from.DeviceName) + 1) % order.Count];
        }
        else to = Neighbor(from, dir);
        if (to == null) return Tuple.Create("No screen that way", Name(from));

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
        GetWindowPlacement(h, ref wp);
        bool maximized = wp.showCmd == 3;
        if (maximized) ShowWindow(h, 9); // restore, move, re-maximise on the new screen
        RECT r;
        GetWindowRect(h, out r);
        Rectangle a = from.WorkingArea, b = to.WorkingArea;
        int w = Math.Min(r.R - r.L, b.Width), hgt = Math.Min(r.B - r.T, b.Height);
        int x = b.Left + (int)((r.L - a.Left) * (double)b.Width / a.Width);
        int y = b.Top + (int)((r.T - a.Top) * (double)b.Height / a.Height);
        x = Math.Max(b.Left, Math.Min(x, b.Right - w));
        y = Math.Max(b.Top, Math.Min(y, b.Bottom - hgt));
        SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
        if (maximized) ShowWindow(h, 3);
        var c = Center(to.WorkingArea);
        SetCursorPos(c.X, c.Y);
        return Tuple.Create("Moved to " + Name(to).ToLowerInvariant(), Spots.FriendlyName(Spots.ProcessName(h)));
    }
}

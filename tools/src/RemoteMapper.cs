// remote-mapper - guided, visual button discovery for air-mouse remotes.
//
// Shows a digital remote built from remote-templates\<id>.json (one file per physical remote),
// highlights one button at a time and records whatever the physical remote sends (keyboard,
// mouse, consumer usages, raw vendor HID). Each capture is saved to data\remote-capture.json.
//
// Commands come only from keyboards that are NOT the remote:
//   Space = no signal / skip, X = not on this remote, R = redo, Backspace = previous step.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

// Shapes: circle (x,y,r) | rrect (x,y,w,h, r = corner) | band (ring segment around x,y between
// radii r2..r and angles a0..a1, GDI+ degrees clockwise from +x) | dpad_up/right/down/left (x,y,r,r2).
class TButton { public string Id, Label, Glyph, Text, Shape; public float X, Y, R, R2, W, H, A0, A1; }
class TStep { public string Id, Button, Title, Instruction; public int HoldMs; public bool Warn; }

class RemoteLayout
{
    public string Id, Name, Note, File;
    public int Vid, Pid, Order;
    public float Width, Height, TopCorner, BottomCorner;
    public List<PointF> Dots = new List<PointF>();
    public List<TButton> Buttons = new List<TButton>();
    public List<TStep> Steps = new List<TStep>();

    static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
    static float F(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : 0f; }
    static bool B(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }

    static IEnumerable<Dictionary<string, object>> Items(Dictionary<string, object> d, string k)
    {
        object v;
        if (!d.TryGetValue(k, out v) || v == null) return new Dictionary<string, object>[0];
        return ((object[])v).Cast<Dictionary<string, object>>();
    }

    public static RemoteLayout Load(string path)
    {
        var d = (Dictionary<string, object>)Json.Read(System.IO.File.ReadAllText(path));
        var t = new RemoteLayout
        {
            Id = S(d, "id"), Name = S(d, "name"), Note = S(d, "note"), File = path, Order = (int)F(d, "order"),
            Vid = Convert.ToInt32(S(d, "vid"), 16), Pid = Convert.ToInt32(S(d, "pid"), 16),
            Width = F(d, "width"), Height = F(d, "height"), TopCorner = F(d, "topCorner"), BottomCorner = F(d, "bottomCorner"),
        };
        foreach (var p in Items(d, "dots")) t.Dots.Add(new PointF(F(p, "x"), F(p, "y")));
        foreach (var b in Items(d, "buttons"))
            t.Buttons.Add(new TButton
            {
                Id = S(b, "id"), Label = S(b, "label"), Glyph = S(b, "glyph"), Text = S(b, "text"), Shape = S(b, "shape"),
                X = F(b, "x"), Y = F(b, "y"), R = F(b, "r"), R2 = F(b, "r2"), W = F(b, "w"), H = F(b, "h"), A0 = F(b, "a0"), A1 = F(b, "a1")
            });
        foreach (var s in Items(d, "steps"))
            t.Steps.Add(new TStep { Id = S(s, "id"), Button = S(s, "button"), Title = S(s, "title"), Instruction = S(s, "instruction"), HoldMs = (int)F(s, "holdMs"), Warn = B(s, "warn") });
        return t;
    }

    public static List<RemoteLayout> LoadAll(string dir)
    {
        return Directory.GetFiles(dir, "*.json").Select(Load).OrderBy(l => l.Order).ThenBy(l => l.Name).ToList();
    }
}

class CapEvent
{
    public double T;             // ms since capture start
    public string Device, Iface, Category, Kind, Key, Text;
    public int Edge;
    public bool Late;            // arrived after the capture window closed (e.g. a delayed release)

    public Dictionary<string, object> ToJson()
    {
        return new Dictionary<string, object>
        {
            { "t_ms", Math.Round(T, 1) }, { "device", Device }, { "interface", Iface }, { "category", Category },
            { "kind", Kind }, { "key", Key }, { "edge", Edge }, { "late", Late }, { "text", Text },
        };
    }

    public static CapEvent FromJson(Dictionary<string, object> d)
    {
        Func<string, string> s = k => { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : null; };
        return new CapEvent
        {
            T = Convert.ToDouble(d["t_ms"], CultureInfo.InvariantCulture), Device = s("device"), Iface = s("interface"),
            Category = s("category"), Kind = s("kind"), Key = s("key"), Text = s("text"),
            Edge = Convert.ToInt32(d["edge"]), Late = d.ContainsKey("late") && (bool)d["late"],
        };
    }

    public string Line { get { return string.Format("{0,8:0} ms  {1,-12} {2,-17} {3}{4}", T, Iface, Category, Text, Late ? "   (late)" : ""); } }
}

class Capture
{
    public TStep Step;
    public string Status = "pending";   // pending | captured | no_event | absent
    public string CapturedAt;
    public List<CapEvent> Events = new List<CapEvent>();
    public int Moves, HidReports;

    public string Primary = "", PrimaryInterface = "", Semantics = "";
    public double? HoldMs;
    public int Repeats;
    public List<string> Signatures = new List<string>();

    public void Analyze()
    {
        Primary = PrimaryInterface = Semantics = "";
        HoldMs = null;
        Repeats = 0;
        Signatures = new List<string>();
        if (Status != "captured") return;

        foreach (var e in Events.Where(e => e.Edge == 1))
        {
            string sig = e.Iface + " " + e.Category + ": " + e.Text.Replace(" DOWN", "");
            if (!Signatures.Contains(sig)) Signatures.Add(sig);
        }
        var first = Events.FirstOrDefault(e => e.Edge == 1);
        if (first != null)
        {
            Primary = first.Text.Replace(" DOWN", "");
            PrimaryInterface = first.Iface + " " + first.Category;
            var up = Events.FirstOrDefault(e => e.Key == first.Key && e.Device == first.Device && e.Edge == -1 && e.T >= first.T);
            if (up != null) HoldMs = up.T - first.T;
            Repeats = Events.Count(e => e.Key == first.Key && e.Device == first.Device && e.Edge == 1);
        }
        else
        {
            var hid = Events.Where(e => e.Kind == "hid").ToList();
            if (hid.Count > 0)
            {
                Primary = "raw report only: " + hid[0].Text;
                PrimaryInterface = hid[0].Iface + " " + hid[0].Category;
                HoldMs = hid[hid.Count - 1].T - hid[0].T;
                foreach (var h in hid.Select(h => h.Iface + " " + h.Category + ": raw reports").Distinct()) Signatures.Add(h);
            }
            else Primary = "(no edge events)";
        }

        if (first == null) Semantics = Events.Any(e => e.Kind == "hid") ? "raw-only" : "unknown";
        else if (Step.HoldMs >= 1000)
        {
            if (HoldMs == null) Semantics = "no-release";
            else if (HoldMs >= Math.Max(400, Step.HoldMs * 0.5)) Semantics = "hold";
            else if (HoldMs < 250) Semantics = "instant";
            else Semantics = "short";
        }
        else Semantics = HoldMs == null ? "no-release" : "tap";
    }

    public Dictionary<string, object> ToJson()
    {
        var d = new Dictionary<string, object>
        {
            { "title", Step.Title }, { "button", Step.Button }, { "status", Status }, { "captured_at", CapturedAt },
            { "instructed_hold_ms", Step.HoldMs },
            { "measured_hold_ms", HoldMs.HasValue ? (object)Math.Round(HoldMs.Value) : null },
            { "semantics", Semantics }, { "primary", Primary }, { "primary_interface", PrimaryInterface },
            { "signatures", Signatures }, { "key_down_count", Repeats },
            { "pointer_moves_ignored", Moves }, { "hid_reports", HidReports },
            { "events", Events.Select(e => (object)e.ToJson()).ToList() },
        };
        return d;
    }
}

class Canvas : Panel
{
    public Canvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }
}

class MapperForm : Form
{
    enum Phase { Armed, Capturing, Settling, Done }

    // Palette
    static readonly Color Bg = Color.FromArgb(22, 24, 29), Panel2 = Color.FromArgb(30, 33, 40), Fg = Color.FromArgb(230, 232, 236);
    static readonly Color Muted = Color.FromArgb(150, 156, 168), Amber = Color.FromArgb(245, 165, 36), Green = Color.FromArgb(38, 166, 96);
    static readonly Color Red = Color.FromArgb(214, 84, 84), BtnIdle = Color.FromArgb(56, 60, 69), Body = Color.FromArgb(38, 41, 48);

    readonly List<RemoteLayout> layouts;
    readonly string templateDir, outPath;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<string, Dictionary<string, Capture>> captures = new Dictionary<string, Dictionary<string, Capture>>();
    readonly float S;
    RawListener listener;

    RemoteLayout remote;
    int stepIndex;
    Phase phase = Phase.Armed;
    Capture active;
    double captureStart, lastEventMs, settleSince, armedSince, lastFlashMs = -9999, lastRemoteClickMs = -9999, otherRemoteMs = -9999;
    string otherRemoteName = "";
    readonly HashSet<string> held = new HashSet<string>();
    readonly HashSet<string> commandKeysDown = new HashSet<string>();
    readonly Dictionary<string, Queue<double>> idleReportTimes = new Dictionary<string, Queue<double>>();
    readonly HashSet<string> noisyReports = new HashSet<string>();
    string lastSaved = "";
    bool tabsDirty;

    Canvas canvas;
    FlowLayoutPanel tabs;
    Label title, instruction, phaseLabel, footer;
    TextBox log;
    ListView results;
    Timer timer;
    string iconFont;

    public MapperForm(string templateDir, string outPath, string initialRemote)
    {
        this.templateDir = templateDir;
        this.outPath = outPath;
        layouts = RemoteLayout.LoadAll(templateDir);
        using (var g = CreateGraphics()) S = g.DpiX / 96f;
        iconFont = new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        foreach (var r in layouts)
        {
            var map = new Dictionary<string, Capture>();
            foreach (var s in r.Steps) map[s.Id] = new Capture { Step = s };
            captures[r.Id] = map;
        }
        LoadExisting();

        BuildUi();
        remote = layouts.FirstOrDefault(r => r.Id == initialRemote) ?? layouts.FirstOrDefault(r => Connected(r)) ?? layouts[0];
        stepIndex = FirstPending(0);
        Arm();
    }

    int P(float v) { return (int)Math.Round(v * S); }

    // ---------------------------------------------------------------- UI construction

    void BuildUi()
    {
        Text = "Airdeck Mapper";
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(P(1200), P(820));
        MinimumSize = new Size(P(1000), P(700));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        KeyDown += (s, e) => { e.Handled = true; e.SuppressKeyPress = true; }; // commands are read from raw input instead

        canvas = new Canvas { Location = new Point(0, 0), Size = new Size(P(340), ClientSize.Height), BackColor = Bg, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom };
        canvas.Paint += PaintRemote;
        canvas.MouseClick += CanvasClick;
        Controls.Add(canvas);

        int x = P(360), w = ClientSize.Width - x - P(24);
        tabs = new FlowLayoutPanel { Location = new Point(x, P(16)), Size = new Size(w, P(44)), BackColor = Bg, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(tabs);

        title = new Label { Location = new Point(x, P(70)), Size = new Size(w, P(42)), Font = new Font("Segoe UI Semibold", 20f), ForeColor = Fg, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        instruction = new Label { Location = new Point(x, P(114)), Size = new Size(w, P(48)), Font = new Font("Segoe UI", 11.5f), ForeColor = Muted, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        phaseLabel = new Label { Location = new Point(x, P(166)), Size = new Size(w, P(34)), Font = new Font("Segoe UI Semibold", 13f), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(title);
        Controls.Add(instruction);
        Controls.Add(phaseLabel);

        var actions = new FlowLayoutPanel { Location = new Point(x, P(206)), Size = new Size(w, P(36)), BackColor = Bg, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        actions.Controls.Add(ActionChip("Space", "No signal / skip", () => Mark("no_event")));
        actions.Controls.Add(ActionChip("X", "Not on this remote", () => Mark("absent")));
        actions.Controls.Add(ActionChip("R", "Redo", Redo));
        actions.Controls.Add(ActionChip("Backspace", "Previous", Previous));
        Controls.Add(actions);

        log = new TextBox
        {
            Location = new Point(x, P(250)), Size = new Size(w, P(190)), Multiline = true, ReadOnly = true, TabStop = false,
            ScrollBars = ScrollBars.Vertical, WordWrap = false, BackColor = Panel2, ForeColor = Color.FromArgb(200, 205, 214),
            BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(log);

        results = new ListView
        {
            Location = new Point(x, P(452)), Size = new Size(w, ClientSize.Height - P(452) - P(40)), View = View.Details, FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.None, TabStop = false,
            Font = new Font("Segoe UI", 9f), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };
        results.Columns.Add("Step", P(190));
        results.Columns.Add("Status", P(90));
        results.Columns.Add("Received", P(330));
        results.Columns.Add("Hold", P(70));
        results.Columns.Add("Type", P(90));
        results.MouseDoubleClick += (s, e) =>
        {
            if (clock.Elapsed.TotalMilliseconds - lastRemoteClickMs < 600 || results.SelectedIndices.Count == 0) return;
            GoTo(results.SelectedIndices[0]);
        };
        Controls.Add(results);

        footer = new Label { Location = new Point(x, ClientSize.Height - P(32)), Size = new Size(w, P(24)), ForeColor = Muted, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
        Controls.Add(footer);

        timer = new Timer { Interval = 40 };
        timer.Tick += Tick;
    }

    Control ActionChip(string key, string text, Action action)
    {
        var l = new Label
        {
            AutoSize = true, Text = key + "   " + text, Padding = new Padding(P(10), P(6), P(10), P(6)), Margin = new Padding(0, 0, P(8), 0),
            BackColor = Panel2, ForeColor = Fg, Cursor = Cursors.Hand
        };
        l.Click += (s, e) => { if (!RemoteClickRecently()) action(); };
        return l;
    }

    void RefreshTabs()
    {
        tabs.Controls.Clear();
        foreach (var r in layouts)
        {
            var rr = r;
            var done = captures[r.Id].Values.Count(c => c.Status != "pending");
            bool on = Connected(r);
            var l = new Label
            {
                AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(0, 0, P(8), 0), Padding = new Padding(P(12), P(8), P(12), P(8)),
                Text = string.Format("{0}   {1:X4}:{2:X4}   {3}   {4}/{5}", r.Name, r.Vid, r.Pid, on ? "\u25CF connected" : "\u25CB not connected", done, r.Steps.Count),
                BackColor = r == remote ? Amber : Panel2, ForeColor = r == remote ? Color.FromArgb(25, 25, 25) : (on ? Fg : Muted),
                Font = new Font("Segoe UI Semibold", 9.5f)
            };
            l.Click += (s, e) => { if (!RemoteClickRecently()) SwitchRemote(rr); };
            tabs.Controls.Add(l);
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        listener = new RawListener(clock, d => layouts.Any(r => r.Vid == d.Vid && r.Pid == d.Pid), OnRaw);
        foreach (var w in listener.Warnings) AppendLog("warning: " + w);
        AppendLog("listening on usage pages " + string.Join(", ", listener.Registered));
        RefreshAll();
        timer.Start();
    }

    // Swallow WM_APPCOMMAND so remote media/browser keys do not launch apps while mapping.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0319) { m.Result = new IntPtr(1); return; }
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- state

    IEnumerable<HidDevice> AllDevices { get { return listener != null ? listener.KnownDevices : Devices.Enumerate(); } }
    bool Connected(RemoteLayout r) { return AllDevices.Any(d => d.Vid == r.Vid && d.Pid == r.Pid); }
    bool RemoteClickRecently() { return clock.Elapsed.TotalMilliseconds - lastRemoteClickMs < 600; }
    TStep Step { get { return stepIndex >= 0 && stepIndex < remote.Steps.Count ? remote.Steps[stepIndex] : null; } }
    Dictionary<string, Capture> Current { get { return captures[remote.Id]; } }

    int FirstPending(int from)
    {
        for (int i = from; i < remote.Steps.Count; i++) if (Current[remote.Steps[i].Id].Status == "pending") return i;
        for (int i = 0; i < from && i < remote.Steps.Count; i++) if (Current[remote.Steps[i].Id].Status == "pending") return i;
        return -1;
    }

    void Arm()
    {
        active = null;
        held.Clear();
        armedSince = clock.Elapsed.TotalMilliseconds;
        phase = Step == null ? Phase.Done : Phase.Armed;
        RefreshAll();
    }

    void Advance()
    {
        stepIndex = FirstPending(stepIndex < 0 ? 0 : stepIndex + 1);
        Arm();
    }

    void GoTo(int index)
    {
        stepIndex = index;
        Current[remote.Steps[index].Id] = new Capture { Step = remote.Steps[index] };
        Save();
        Arm();
    }

    void Mark(string status)
    {
        if (Step == null) return;
        Current[Step.Id] = new Capture { Step = Step, Status = status, CapturedAt = DateTime.Now.ToString("s") };
        Save();
        AppendLog(string.Format("{0}: marked {1}", Step.Title, status == "absent" ? "not on this remote" : "no signal"));
        Advance();
    }

    void Redo() { if (Step != null) GoTo(stepIndex); }

    void Previous()
    {
        int i = (stepIndex < 0 ? remote.Steps.Count : stepIndex) - 1;
        if (i >= 0) GoTo(i);
    }

    void SwitchRemote(RemoteLayout r)
    {
        remote = r;
        stepIndex = FirstPending(0);
        noisyReports.Clear();
        idleReportTimes.Clear();
        AppendLog("---- switched to " + r.Name + " ----");
        Arm();
    }

    // ---------------------------------------------------------------- input

    void OnRaw(InputEvent e)
    {
        double now = e.Ms;
        // Registration fires one notification per existing device; coalesce them into a single refresh.
        if (e.Kind == "device") { tabsDirty = true; return; }
        var dev = e.Device;
        var src = layouts.FirstOrDefault(r => r.Vid == dev.Vid && r.Pid == dev.Pid);

        if (src == null)
        {
            bool unattributed = dev.Vid == -1;
            // Keys Windows synthesises without a source device (e.g. from consumer collections) still count as evidence.
            if (unattributed && e.Kind == "key" && active != null && (phase == Phase.Capturing || phase == Phase.Settling))
                Record(e, "(unattributed)");
            else if (!unattributed && e.Kind == "key") Command(e);
            return;
        }

        if (src != remote)
        {
            if (e.Kind != "move" && e.Kind != "hid") { otherRemoteMs = now; otherRemoteName = src.Name; }
            return;
        }

        if (e.Kind == "mouse") lastRemoteClickMs = now;
        if (e.Kind == "move") { if (active != null) active.Moves++; return; }

        string reportKey = dev.Handle + "/" + (e.Text.Length > 7 ? e.Text.Substring(4, 2) : "");
        if (e.Kind == "hid")
        {
            if (phase == Phase.Armed || phase == Phase.Done)
            {
                // A report stream that runs while nothing is pressed is background noise, not a button.
                Queue<double> q;
                if (!idleReportTimes.TryGetValue(reportKey, out q)) idleReportTimes[reportKey] = q = new Queue<double>();
                q.Enqueue(now);
                while (q.Count > 0 && now - q.Peek() > 1000) q.Dequeue();
                if (q.Count > 8 && noisyReports.Add(reportKey)) AppendLog("ignoring continuous report stream from " + dev.InterfaceId);
            }
            if (noisyReports.Contains(reportKey))
            {
                if (active != null) active.HidReports++;
                return;
            }
        }

        if (e.Kind != "hid" || phase != Phase.Armed) lastFlashMs = now;
        AppendLog(string.Format("{0,-12} {1,-17} {2}", dev.InterfaceId, dev.Category, e.Text));

        if (phase == Phase.Armed)
        {
            active = new Capture { Step = Step };
            captureStart = now;
            phase = Phase.Capturing;
            RefreshPhase();
        }
        if (active != null && (phase == Phase.Capturing || phase == Phase.Settling)) Record(e, null);
    }

    void Record(InputEvent e, string deviceOverride)
    {
        double now = e.Ms;
        if (e.Kind == "hid")
        {
            active.HidReports++;
            if (active.HidReports > 60) { lastEventMs = now; return; }
        }
        if (active.Events.Count >= 500) return;
        var dev = e.Device;
        active.Events.Add(new CapEvent
        {
            T = now - captureStart, Device = deviceOverride ?? string.Format("{0:X4}:{1:X4}", dev.Vid, dev.Pid),
            Iface = deviceOverride != null ? "-" : dev.InterfaceId, Category = deviceOverride != null ? "keyboard" : dev.Category,
            Kind = e.Kind, Key = e.Key, Edge = e.Edge, Text = e.Text, Late = phase == Phase.Settling
        });
        if (e.Key != null)
        {
            string k = (deviceOverride ?? dev.Handle.ToString()) + "/" + e.Key;
            if (e.Edge == 1) held.Add(k);
            else if (e.Edge == -1) held.Remove(k);
        }
        lastEventMs = now;
    }

    void Command(InputEvent e)
    {
        if (Form.ActiveForm != this) return;
        string id = e.Device.Handle + "/" + e.Key;
        if (e.Edge == -1) { commandKeysDown.Remove(id); return; }
        if (!commandKeysDown.Add(id)) return; // ignore auto-repeat
        switch (e.Key.Substring(0, 4))
        {
            case "vk20": Mark("no_event"); break;
            case "vk58": Mark("absent"); break;
            case "vk52": Redo(); break;
            case "vk08": Previous(); break;
        }
    }

    void Tick(object sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (phase == Phase.Capturing)
        {
            bool quiet = now - lastEventMs >= 800;
            bool longEnough = now - captureStart >= Step.HoldMs * 0.9;
            if ((held.Count == 0 && quiet && longEnough) || now - captureStart > Step.HoldMs + 6000)
            {
                phase = Phase.Settling;
                settleSince = now;
                RefreshPhase();
            }
        }
        else if (phase == Phase.Settling)
        {
            if (now - Math.Max(lastEventMs, settleSince) >= 1200 || now - settleSince > 6000) Finish();
        }
        else if (phase == Phase.Armed && now - armedSince > 8000)
        {
            RefreshPhase();
        }
        if (now - otherRemoteMs < 3200) RefreshPhase();
        if (tabsDirty) { tabsDirty = false; RefreshTabs(); }
        canvas.Invalidate();
    }

    void Finish()
    {
        active.Status = "captured";
        active.CapturedAt = DateTime.Now.ToString("s");
        active.Analyze();
        Current[Step.Id] = active;
        Save();
        AppendLog(string.Format(">>> saved {0}: {1} [{2}{3}]", Step.Title, active.Primary, active.Semantics,
            active.HoldMs.HasValue ? ", " + Math.Round(active.HoldMs.Value) + " ms" : ""));
        Advance();
    }

    // ---------------------------------------------------------------- persistence

    void LoadExisting()
    {
        if (!File.Exists(outPath)) return;
        try
        {
            var root = (Dictionary<string, object>)Json.Read(File.ReadAllText(outPath));
            var remotes = (Dictionary<string, object>)root["remotes"];
            foreach (var r in layouts)
            {
                object ro;
                if (!remotes.TryGetValue(r.Id, out ro)) continue;
                var steps = (Dictionary<string, object>)((Dictionary<string, object>)ro)["steps"];
                foreach (var s in r.Steps)
                {
                    object so;
                    if (!steps.TryGetValue(s.Id, out so)) continue;
                    var sd = (Dictionary<string, object>)so;
                    var c = new Capture { Step = s, Status = (string)sd["status"], CapturedAt = sd["captured_at"] as string };
                    foreach (var ev in (object[])sd["events"]) c.Events.Add(CapEvent.FromJson((Dictionary<string, object>)ev));
                    c.Moves = Convert.ToInt32(sd["pointer_moves_ignored"]);
                    c.HidReports = Convert.ToInt32(sd["hid_reports"]);
                    c.Analyze();
                    captures[r.Id][s.Id] = c;
                }
            }
        }
        catch (Exception ex)
        {
            string backup = outPath + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(outPath, backup, true);
            MessageBox.Show("Could not read " + outPath + " (" + ex.Message + "). Starting fresh; old file kept as " + backup);
        }
    }

    void Save()
    {
        var all = AllDevices.ToList();
        var remotes = new Dictionary<string, object>();
        foreach (var r in layouts)
        {
            var rr = r;
            var steps = new Dictionary<string, object>();
            foreach (var s in r.Steps) steps[s.Id] = captures[r.Id][s.Id].ToJson();
            remotes[r.Id] = new Dictionary<string, object>
            {
                { "name", r.Name }, { "vid", "0x" + r.Vid.ToString("X4") }, { "pid", "0x" + r.Pid.ToString("X4") }, { "note", r.Note },
                { "template_file", r.File },
                { "interfaces", all.Where(d => d.Vid == rr.Vid && d.Pid == rr.Pid).OrderBy(d => d.InterfaceId).Select(d => (object)Devices.Describe(d)).ToList() },
                { "steps", steps },
            };
        }
        var root = new Dictionary<string, object>
        {
            { "template_dir", templateDir }, { "updated_at", DateTime.Now.ToString("s") }, { "remotes", remotes },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        string tmp = outPath + ".tmp";
        File.WriteAllText(tmp, Json.Write(root));
        if (File.Exists(outPath)) File.Replace(tmp, outPath, null); else File.Move(tmp, outPath);
        lastSaved = DateTime.Now.ToString("HH:mm:ss");
    }

    // ---------------------------------------------------------------- rendering

    void RefreshAll()
    {
        RefreshTabs();
        RefreshPhase();
        RefreshResults();
        canvas.Invalidate();
    }

    void RefreshPhase()
    {
        double now = clock.Elapsed.TotalMilliseconds;
        var st = Step;
        title.Text = st == null ? remote.Name + " - all steps done" : string.Format("{0}   ({1}/{2})", st.Title, stepIndex + 1, remote.Steps.Count);
        instruction.Text = st == null ? "Everything is saved. Click any button on the remote picture (or double-click a row) to redo it, or switch remotes above." : st.Instruction;
        instruction.ForeColor = st != null && st.Warn ? Red : Muted;

        string text; Color color;
        if (now - otherRemoteMs < 3000)
        {
            text = "Input from the " + otherRemoteName + " receiver ignored - click its tab above to map that remote.";
            color = Red;
        }
        else switch (phase)
        {
            case Phase.Armed:
                text = now - armedSince > 8000
                    ? "Nothing received yet. If this button sends nothing, press Space (no signal)."
                    : "Waiting for the " + remote.Name + " button...";
                color = now - armedSince > 8000 ? Red : Amber;
                break;
            case Phase.Capturing: text = "\u25CF Recording... release the button when done."; color = Red; break;
            case Phase.Settling: text = "Release the button - saving..."; color = Amber; break;
            default: text = "\u2713 Done"; color = Green; break;
        }
        if (phaseLabel.Text != text) phaseLabel.Text = text;
        phaseLabel.ForeColor = color;
        footer.Text = "Autosaves to " + outPath + (lastSaved == "" ? "" : "   (last saved " + lastSaved + ")");
    }

    void RefreshResults()
    {
        results.BeginUpdate();
        results.Items.Clear();
        for (int i = 0; i < remote.Steps.Count; i++)
        {
            var s = remote.Steps[i];
            var c = Current[s.Id];
            string status = i == stepIndex ? "\u25B6 current" : c.Status == "captured" ? "\u2713 saved" : c.Status == "no_event" ? "no signal" : c.Status == "absent" ? "not on remote" : "";
            string received = c.Status == "captured" ? (c.PrimaryInterface + "  " + c.Primary) : "";
            var item = new ListViewItem(new[] { s.Title, status, received, c.HoldMs.HasValue ? Math.Round(c.HoldMs.Value) + " ms" : "", c.Semantics });
            item.ForeColor = i == stepIndex ? Amber : c.Status == "captured" ? Color.FromArgb(120, 220, 160) : c.Status == "no_event" ? Red : c.Status == "absent" ? Muted : Fg;
            results.Items.Add(item);
        }
        results.EndUpdate();
        if (stepIndex >= 0) results.EnsureVisible(stepIndex);
    }

    void AppendLog(string line)
    {
        if (log.TextLength > 60000) log.Text = log.Text.Substring(log.TextLength - 30000);
        log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
    }

    GraphicsPath ButtonPath(TButton b)
    {
        var p = new GraphicsPath();
        if (b.Shape.StartsWith("dpad_"))
        {
            float start = b.Shape == "dpad_up" ? 225 : b.Shape == "dpad_right" ? 315 : b.Shape == "dpad_down" ? 45 : 135;
            p.AddArc(b.X - b.R, b.Y - b.R, 2 * b.R, 2 * b.R, start + 2, 86);
            p.AddArc(b.X - b.R2, b.Y - b.R2, 2 * b.R2, 2 * b.R2, start + 88, -86);
            p.CloseFigure();
        }
        else if (b.Shape == "band")
        {
            p.AddArc(b.X - b.R, b.Y - b.R, 2 * b.R, 2 * b.R, b.A0, b.A1 - b.A0);
            p.AddArc(b.X - b.R2, b.Y - b.R2, 2 * b.R2, 2 * b.R2, b.A1, b.A0 - b.A1);
            p.CloseFigure();
        }
        else if (b.Shape == "rrect")
        {
            float corner = b.R > 0 ? b.R : 10;
            p.AddPath(RoundRect(new RectangleF(b.X - b.W / 2, b.Y - b.H / 2, b.W, b.H), corner, corner), false);
        }
        else p.AddEllipse(b.X - b.R, b.Y - b.R, 2 * b.R, 2 * b.R);
        return p;
    }

    PointF GlyphCenter(TButton b)
    {
        double deg;
        if (b.Shape.StartsWith("dpad_")) deg = b.Shape == "dpad_up" ? 270 : b.Shape == "dpad_right" ? 0 : b.Shape == "dpad_down" ? 90 : 180;
        else if (b.Shape == "band") deg = (b.A0 + b.A1) / 2;
        else return new PointF(b.X, b.Y);
        float rad = (b.R + b.R2) / 2;
        return new PointF(b.X + (float)Math.Cos(deg * Math.PI / 180) * rad, b.Y + (float)Math.Sin(deg * Math.PI / 180) * rad);
    }

    float GlyphSize(TButton b)
    {
        if (b.Shape.StartsWith("dpad_") || b.Shape == "band") return 20;
        if (b.Shape == "rrect") return Math.Min(b.W, b.H) * 0.46f;
        return b.R * 0.85f;
    }

    // Remote coordinates -> canvas pixels: scale to fit the canvas, centred horizontally.
    float Zoom { get { return Math.Min((canvas.Width - P(40)) / remote.Width, (canvas.Height - P(40)) / remote.Height); } }
    PointF Origin { get { return new PointF((canvas.Width - remote.Width * Zoom) / 2, P(20)); } }

    void PaintRemote(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.TranslateTransform(Origin.X, Origin.Y);
        g.ScaleTransform(Zoom, Zoom);

        double now = clock.Elapsed.TotalMilliseconds;
        var bodyRect = new RectangleF(0, 0, remote.Width, remote.Height);
        using (var body = RoundRect(bodyRect, remote.TopCorner, remote.BottomCorner))
        using (var fill = new LinearGradientBrush(bodyRect, Color.FromArgb(46, 50, 58), Color.FromArgb(30, 32, 38), 90f))
        using (var pen = new Pen(Color.FromArgb(70, 75, 86), 1.5f))
        {
            g.FillPath(fill, body);
            g.DrawPath(pen, body);
        }
        using (var br = new SolidBrush(Color.FromArgb(14, 15, 18)))
            foreach (var d in remote.Dots) g.FillEllipse(br, d.X - 3, d.Y - 3, 6, 6);
        using (var f = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel))
        using (var br = new SolidBrush(Color.FromArgb(110, 116, 128)))
        {
            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(remote.Name + "   " + remote.Vid.ToString("X4") + ":" + remote.Pid.ToString("X4"), f, br, remote.Width / 2, remote.Height - 42, sf);
        }

        var current = Step;
        float pulse = (float)(0.5 + 0.5 * Math.Sin(now / 180.0));
        foreach (var b in remote.Buttons)
        {
            var stepsFor = remote.Steps.Where(s => s.Button == b.Id).Select(s => Current[s.Id]).ToList();
            bool isCurrent = current != null && current.Button == b.Id;
            Color fillC = BtnIdle, glyphC = Color.FromArgb(215, 219, 226), edge = Color.FromArgb(80, 86, 98);
            bool dashed = false;
            if (stepsFor.Count > 0 && stepsFor.All(c => c.Status == "absent")) { fillC = Color.FromArgb(34, 36, 42); glyphC = Color.FromArgb(80, 84, 92); dashed = true; }
            else if (stepsFor.Count > 0 && stepsFor.All(c => c.Status == "no_event")) { fillC = Color.FromArgb(96, 40, 40); glyphC = Color.FromArgb(240, 170, 170); }
            else if (stepsFor.Count > 0 && stepsFor.All(c => c.Status == "captured")) { fillC = Green; glyphC = Color.White; }
            else if (stepsFor.Any(c => c.Status == "captured")) { fillC = Color.FromArgb(30, 110, 90); glyphC = Color.White; }

            if (isCurrent)
            {
                fillC = phase == Phase.Capturing || phase == Phase.Settling ? Red : Blend(Color.FromArgb(120, 80, 20), Amber, pulse);
                glyphC = Color.FromArgb(25, 25, 25);
                edge = Amber;
                if (now - lastFlashMs < 160) fillC = Color.White;
            }

            using (var path = ButtonPath(b))
            {
                if (isCurrent)
                    using (var glow = new Pen(Color.FromArgb((int)(90 + 120 * pulse), Amber), 7f) { LineJoin = LineJoin.Round })
                        g.DrawPath(glow, path);
                using (var br = new SolidBrush(fillC)) g.FillPath(br, path);
                using (var pen = new Pen(edge, isCurrent ? 2f : 1.2f)) { if (dashed) pen.DashStyle = DashStyle.Dash; g.DrawPath(pen, path); }
            }

            var c0 = GlyphCenter(b);
            var sfc = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var br = new SolidBrush(glyphC))
            {
                if (b.Glyph != "")
                    using (var f = new Font(iconFont, GlyphSize(b), GraphicsUnit.Pixel))
                        g.DrawString(((char)Convert.ToInt32(b.Glyph, 16)).ToString(), f, br, c0.X, c0.Y + 1, sfc);
                else
                    using (var f = new Font("Segoe UI Semibold", Math.Max(12f, GlyphSize(b) * 0.8f), GraphicsUnit.Pixel))
                        g.DrawString(b.Text != "" ? b.Text : b.Label, f, br, c0.X, c0.Y, sfc);
            }
            // Icon-only round keys get a caption underneath so the user knows what they are.
            if (b.Shape == "circle" && b.Glyph != "")
                using (var f = new Font("Segoe UI", 10.5f, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(isCurrent ? Amber : Color.FromArgb(140, 146, 158)))
                    g.DrawString(b.Label, f, br, b.X, b.Y + b.R + 2, new StringFormat { Alignment = StringAlignment.Center });
        }
    }

    static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    static GraphicsPath RoundRect(RectangleF r, float top, float bottom)
    {
        var p = new GraphicsPath();
        top = Math.Max(0.5f, Math.Min(top, r.Width / 2));
        bottom = Math.Max(0.5f, Math.Min(bottom, r.Width / 2));
        p.AddArc(r.X, r.Y, top * 2, top * 2, 180, 90);
        p.AddArc(r.Right - top * 2, r.Y, top * 2, top * 2, 270, 90);
        p.AddArc(r.Right - bottom * 2, r.Bottom - bottom * 2, bottom * 2, bottom * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - bottom * 2, bottom * 2, bottom * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    void CanvasClick(object sender, MouseEventArgs e)
    {
        if (RemoteClickRecently()) return; // the air mouse's own click should not select buttons
        var pt = new PointF((e.X - Origin.X) / Zoom, (e.Y - Origin.Y) / Zoom);
        // OK sits inside the d-pad ring; test it first.
        foreach (var b in remote.Buttons.OrderBy(b => b.Id == "ok" ? 0 : 1))
        {
            using (var path = ButtonPath(b))
            {
                if (!path.IsVisible(pt)) continue;
                var idx = Enumerable.Range(0, remote.Steps.Count).Where(i => remote.Steps[i].Button == b.Id).ToList();
                if (idx.Count == 0) return;
                // Clicking the same button again cycles through its steps (e.g. mic hold / mic tap).
                int pos = idx.IndexOf(stepIndex);
                GoTo(idx[(pos + 1) % idx.Count]);
                return;
            }
        }
    }
}

static class RemoteMapperProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.ToString(), "remote-mapper error");

        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (var dir = new DirectoryInfo(root); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "remote-templates"))) { root = dir.FullName; break; }

        string templates = Path.Combine(root, "remote-templates");
        string output = Path.Combine(root, "data", "remote-capture.json");
        string initial = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--templates") templates = Path.GetFullPath(args[i + 1]);
            if (args[i] == "--out") output = Path.GetFullPath(args[i + 1]);
            if (args[i] == "--remote") initial = args[i + 1];
        }
        Application.Run(new MapperForm(templates, output, initial));
    }
}

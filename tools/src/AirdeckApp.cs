// airdeck application: configuration, the controller that routes remote input to profile
// actions, the JSON API behind the UI, and the tray/app-window shell.

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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

class Profile
{
    public string Id, Name, Description;
    public Dictionary<string, Dictionary<string, object>> Buttons = new Dictionary<string, Dictionary<string, object>>();

    public static Profile Load(string path)
    {
        var d = (Dictionary<string, object>)Json.Read(File.ReadAllText(path));
        var p = new Profile
        {
            Id = Path.GetFileNameWithoutExtension(path),
            Name = d.ContainsKey("name") ? (string)d["name"] : Path.GetFileNameWithoutExtension(path),
            Description = d.ContainsKey("description") ? (string)d["description"] : "",
        };
        object b;
        if (d.TryGetValue("buttons", out b) && b is Dictionary<string, object>)
            foreach (var kv in (Dictionary<string, object>)b)
            {
                var spec = kv.Value as Dictionary<string, object>;
                if (kv.Key.StartsWith("_") || spec == null) continue;
                RemoteAction.Create(spec); // validate now so a typo is reported at load time
                p.Buttons[kv.Key] = spec;
            }
        return p;
    }

    public Dictionary<string, object> ToJson()
    {
        var buttons = new Dictionary<string, object>();
        foreach (var kv in Buttons) buttons[kv.Key] = kv.Value;
        return new Dictionary<string, object> { { "id", Id }, { "name", Name }, { "description", Description }, { "buttons", buttons } };
    }
}

class ButtonSig
{
    public string Id, Source;   // consumer | keyboard | none
    public uint Usage;          // page << 16 | usage
    public ushort Vk;
    public string Scan, Semantics;
}

class RemoteDef
{
    public string Id, Name;
    public int Vid, Pid;
    public List<ButtonSig> Buttons = new List<ButtonSig>();
    public string HardwareTag { get { return string.Format("VID_{0:X4}&PID_{1:X4}", Vid, Pid); } }

    // Runtime state
    public Profile Profile;
    public Dictionary<string, RemoteAction> Actions = new Dictionary<string, RemoteAction>();
    public bool Connected;
    public double LastUsedMs;
    public readonly HashSet<string> Pressed = new HashSet<string>();
}

class Controller : IDisposable
{
    // Consumer usages Windows turns into virtual keys (only these need hook suppression).
    static readonly Dictionary<uint, ushort> consumerVk = new Dictionary<uint, ushort>
    {
        { 0x000C0223, 0xAC }, { 0x000C0224, 0xA6 }, { 0x000C0225, 0xA7 }, { 0x000C0227, 0xA8 }, { 0x000C0221, 0xAA },
        { 0x000C022A, 0xAB }, { 0x000C00E2, 0xAD }, { 0x000C00EA, 0xAE }, { 0x000C00E9, 0xAF }, { 0x000C00B5, 0xB0 },
        { 0x000C00B6, 0xB1 }, { 0x000C00B7, 0xB2 }, { 0x000C00CD, 0xB3 }, { 0x000C018A, 0xB4 }, { 0x000C0183, 0xB5 },
        { 0x000C0194, 0xB6 }, { 0x000C0192, 0xB7 },
    };

    public readonly string Root;
    public readonly List<RemoteDef> Remotes = new List<RemoteDef>();
    public List<Profile> Profiles = new List<Profile>();
    public List<InputSpot> SpotList = new List<InputSpot>();
    public bool Paused;
    public event Action Changed;
    public event Action<string> Notify;
    public WebServer Web;
    public SynchronizationContext Ui;

    readonly Stopwatch clock = Stopwatch.StartNew();
    // Input thread only: one entry per mapped consumer press still waiting for its synthesised key.
    readonly Dictionary<ushort, Queue<double>> pendingBlock = new Dictionary<ushort, Queue<double>>();
    readonly HashSet<string> warned = new HashSet<string>();
    readonly Dictionary<string, RemoteAction> active = new Dictionary<string, RemoteAction>(); // held buttons
    readonly Dictionary<string, double> activeSince = new Dictionary<string, double>();
    RawListener listener;
    InterceptionBridge interception;
    IntPtr hook;
    Win32.LowLevelProc hookProc; // keep the delegate alive
    System.Windows.Forms.Timer watchdog;
    int lastSpot = -1;
    int jumping;

    public Controller(string root)
    {
        Root = root;
        LoadDevices();
        LoadProfiles();
        LoadState();
        LoadSpots();
        Workflow.Step = StepSpot;
        Workflow.Goto = JumpSpot;
    }

    string DataPath(string name) { return Path.Combine(Root, "data", name); }
    public string ProfilesDir { get { return Path.Combine(Root, "profiles"); } }
    public bool InterceptionActive { get { return interception != null && interception.Active; } }
    public bool InterceptionInstalled { get { return File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "keyboard.sys")); } }

    void Raise(string toast)
    {
        if (toast != null && Notify != null) Notify(toast);
        if (Changed != null) Changed();
        if (Web != null) Web.Broadcast("state", new Dictionary<string, object> { { "reason", toast ?? "" } });
    }

    void LoadDevices()
    {
        var d = (Dictionary<string, object>)Json.Read(File.ReadAllText(Path.Combine(Root, "remote-devices.json")));
        foreach (var kv in (Dictionary<string, object>)d["remotes"])
        {
            var r = (Dictionary<string, object>)kv.Value;
            var def = new RemoteDef
            {
                Id = kv.Key, Name = (string)r["name"],
                Vid = Convert.ToInt32(((string)r["vid"]).Replace("0x", ""), 16),
                Pid = Convert.ToInt32(((string)r["pid"]).Replace("0x", ""), 16),
            };
            foreach (var bkv in (Dictionary<string, object>)r["buttons"])
            {
                var b = (Dictionary<string, object>)bkv.Value;
                var sig = new ButtonSig { Id = bkv.Key, Source = (string)b["source"], Semantics = b.ContainsKey("semantics") ? b["semantics"] as string : null };
                if (sig.Source == "consumer")
                    sig.Usage = (Convert.ToUInt32(((string)b["page"]).Replace("0x", ""), 16) << 16) | Convert.ToUInt32(((string)b["usage"]).Replace("0x", ""), 16);
                else if (sig.Source == "keyboard")
                {
                    sig.Vk = Convert.ToUInt16(((string)b["vk"]).Replace("0x", ""), 16);
                    sig.Scan = (string)b["sc"];
                }
                def.Buttons.Add(sig);
            }
            Remotes.Add(def);
        }
    }

    public void LoadProfiles()
    {
        var list = new List<Profile>();
        foreach (var f in Directory.GetFiles(ProfilesDir, "*.json").OrderBy(f => f))
        {
            try { list.Add(Profile.Load(f)); }
            catch (Exception ex)
            {
                Log.Write("profile {0} not loaded: {1}", Path.GetFileName(f), ex.Message);
                if (Notify != null) Notify("Profile " + Path.GetFileName(f) + " has an error: " + ex.Message);
            }
        }
        if (!list.Any(p => p.Id == "stock")) list.Insert(0, new Profile { Id = "stock", Name = "Stock", Description = "Default remote behaviour." });
        Profiles = list.OrderBy(p => p.Id == "stock" ? 0 : 1).ThenBy(p => p.Name).ToList();
        foreach (var r in Remotes) ApplyProfile(r, r.Profile == null ? "stock" : r.Profile.Id);
        Log.Write("loaded profiles: {0}", string.Join(", ", Profiles.Select(p => p.Id)));
    }

    void LoadState()
    {
        try
        {
            if (!File.Exists(DataPath("state.json"))) return;
            var d = (Dictionary<string, object>)Json.Read(File.ReadAllText(DataPath("state.json")));
            object profiles;
            if (d.TryGetValue("profiles", out profiles))
                foreach (var kv in (Dictionary<string, object>)profiles)
                {
                    var r = Remotes.FirstOrDefault(x => x.Id == kv.Key);
                    if (r != null) ApplyProfile(r, (string)kv.Value);
                }
            Paused = d.ContainsKey("paused") && (bool)d["paused"];
        }
        catch (Exception ex) { Log.Write("state.json ignored: {0}", ex.Message); }
    }

    void SaveState()
    {
        var profiles = new Dictionary<string, object>();
        foreach (var r in Remotes) profiles[r.Id] = r.Profile.Id;
        WriteData("state.json", new Dictionary<string, object> { { "profiles", profiles }, { "paused", Paused } });
    }

    void LoadSpots()
    {
        try
        {
            if (!File.Exists(DataPath("spots.json"))) return;
            var d = (Dictionary<string, object>)Json.Read(File.ReadAllText(DataPath("spots.json")));
            SpotList = ((object[])d["spots"]).Select(o => InputSpot.FromJson((Dictionary<string, object>)o)).ToList();
        }
        catch (Exception ex) { Log.Write("spots.json ignored: {0}", ex.Message); }
    }

    void SaveSpots()
    {
        WriteData("spots.json", new Dictionary<string, object> { { "spots", SpotList.Select(s => (object)s.ToJson()).ToList() } });
    }

    void WriteData(string name, object value)
    {
        Directory.CreateDirectory(Path.Combine(Root, "data"));
        string path = DataPath(name), tmp = path + ".tmp";
        File.WriteAllText(tmp, Json.Write(value));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    void ApplyProfile(RemoteDef r, string profileId)
    {
        ReleaseRemote(r);
        var p = Profiles.FirstOrDefault(x => x.Id == profileId) ?? Profiles.First(x => x.Id == "stock");
        r.Profile = p;
        // Built aside and swapped in whole: the input thread reads this map without locking.
        var actions = new Dictionary<string, RemoteAction>();
        foreach (var kv in p.Buttons)
        {
            if (!r.Buttons.Any(b => b.Id == kv.Key)) continue; // this remote does not have that button
            try
            {
                var action = RemoteAction.Create(kv.Value);
                if (action != null) actions[kv.Key] = action;
            }
            catch (FormatException ex) { Log.Write("{0}/{1}: {2}", p.Id, kv.Key, ex.Message); }
        }
        r.Actions = actions;
    }

    public void SetProfile(RemoteDef r, string profileId)
    {
        ApplyProfile(r, profileId);
        Log.Write("{0} -> profile {1}", r.Name, r.Profile.Name);
        SaveState();
        Raise(r.Name + "  →  " + r.Profile.Name);
    }

    public void CycleProfile(RemoteDef r)
    {
        int i = Profiles.IndexOf(r.Profile);
        SetProfile(r, Profiles[(i + 1) % Profiles.Count].Id);
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        foreach (var r in Remotes) ReleaseRemote(r);
        SaveState();
        Log.Write(paused ? "paused: all remotes stock" : "resumed");
        Raise(paused ? "All remotes paused — stock behaviour" : "Remote profiles active again");
    }

    public RemoteDef FocusRemote
    {
        get { return Remotes.Where(r => r.Connected).OrderByDescending(r => r.LastUsedMs).FirstOrDefault() ?? Remotes.FirstOrDefault(); }
    }

    // Raw Input and the low-level keyboard hook live on their own thread. Windows silently removes a
    // low-level hook whose thread is too slow to answer, so this thread never does UI or file work:
    // it decides what to swallow and hands everything else to the UI thread.
    Thread inputThread;
    SynchronizationContext inputCtx;

    public void Start()
    {
        var ready = new ManualResetEvent(false);
        inputThread = new Thread(() =>
        {
            using (var anchor = new Control()) anchor.CreateControl(); // installs a sync context for this thread
            inputCtx = SynchronizationContext.Current;
            listener = new RawListener(clock, d => Remotes.Any(r => r.Vid == d.Vid && r.Pid == d.Pid), OnRawInput);
            foreach (var w in listener.Warnings) Log.Write("raw input: {0}", w);
            InstallHook();
            // Belt and braces: re-arm the hook every minute (new hook first, so there is no gap).
            var rearm = new System.Windows.Forms.Timer { Interval = 60000 };
            rearm.Tick += (s, e) => InstallHook();
            rearm.Start();
            ready.Set();
            Application.Run();
            rearm.Dispose();
        }) { IsBackground = true, Name = "input", Priority = ThreadPriority.Highest };
        inputThread.SetApartmentState(ApartmentState.STA);
        inputThread.Start();
        ready.WaitOne();
        RefreshConnected();
        Task.Delay(1500).ContinueWith(_ => SelfTest());

        interception = new InterceptionBridge(
            id => Remotes.Any(r => id.IndexOf(r.HardwareTag, StringComparison.OrdinalIgnoreCase) >= 0),
            (id, scan, up) =>
            {
                bool swallow = false;
                Ui.Send(_ => swallow = OnInterceptedKey(id, scan, up), null);
                return swallow;
            });
        interception.Start();

        // Safety net: never leave a chord (e.g. Ctrl+Win) held if a release report goes missing.
        watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
        int ticks = 0;
        watchdog.Tick += (s, e) =>
        {
            // Pick up the Interception driver as soon as it loads (after install + receiver re-plug).
            if (++ticks % 3 == 0 && !interception.Active && InterceptionInstalled) TryInterception();
            double now = clock.Elapsed.TotalMilliseconds;
            foreach (var key in activeSince.Where(kv => now - kv.Value > 120000).Select(kv => kv.Key).ToList())
            {
                Log.Write("watchdog: {0} held for 2 minutes, releasing", key);
                active[key].Cancel();
                active.Remove(key);
                activeSince.Remove(key);
            }
        };
        watchdog.Start();
    }

    void TryInterception()
    {
        if (interception.Start(true))
        {
            warned.Clear();
            Raise("Keyboard-key driver active — arrows, digits and Pg± now follow your profiles");
        }
    }

    void RefreshConnected()
    {
        foreach (var r in Remotes) r.Connected = listener.KnownDevices.Any(d => d.Vid == r.Vid && d.Pid == r.Pid);
    }

    void ReleaseRemote(RemoteDef r)
    {
        foreach (var key in active.Keys.Where(k => k.StartsWith(r.Id + "/")).ToList())
        {
            active[key].Cancel();
            active.Remove(key);
            activeSince.Remove(key);
        }
    }

    // Common path for every remote button edge. Returns true when a profile action handled it
    // (so the original input must be suppressed).
    string lastFocusId;

    bool HandleButton(RemoteDef r, ButtonSig sig, int edge)
    {
        r.LastUsedMs = clock.Elapsed.TotalMilliseconds;
        if (lastFocusId != r.Id) { lastFocusId = r.Id; if (Changed != null) Changed(); } // tray tooltip follows the remote in hand
        string key = r.Id + "/" + sig.Id;
        bool repeat = edge == 1 && !r.Pressed.Add(sig.Id);
        if (edge == -1) r.Pressed.Remove(sig.Id);

        RemoteAction action = null;
        bool mapped = !Paused && r.Actions.TryGetValue(sig.Id, out action);
        if (!repeat && Web != null)
            Web.Broadcast("press", new Dictionary<string, object>
            {
                { "remote", r.Id }, { "button", sig.Id }, { "edge", edge }, { "action", mapped ? action.Description : null },
            });
        if (!mapped) return false;

        if (edge == 1)
        {
            if (repeat || active.ContainsKey(key)) return true; // auto-repeat of a held key
            Log.Write("{0} {1} down -> {2}", r.Name, sig.Id, action.Description);
            action.Down();
            active[key] = action;
            activeSince[key] = r.LastUsedMs;
        }
        else
        {
            RemoteAction a;
            if (active.TryGetValue(key, out a)) a.Up();
            active.Remove(key);
            activeSince.Remove(key);
        }
        return true;
    }

    ButtonSig Resolve(RemoteDef r, InputEvent e)
    {
        if (e.Kind == "usage")
        {
            uint usage = Convert.ToUInt32(e.Key.Substring(5), 16);
            return r.Buttons.FirstOrDefault(b => b.Source == "consumer" && b.Usage == usage);
        }
        if (e.Kind == "key")
        {
            string[] parts = e.Key.Split('/');
            ushort vk = Convert.ToUInt16(parts[0].Substring(2), 16);
            return r.Buttons.FirstOrDefault(b => b.Source == "keyboard" && b.Vk == vk && b.Scan == parts[1]);
        }
        return null;
    }

    void OnDeviceChange()
    {
        RefreshConnected();
        if (interception != null)
        {
            if (interception.Active) interception.Rescan();
            else if (InterceptionInstalled) TryInterception();
        }
        Raise(null);
    }

    // ---- input thread ----------------------------------------------------------------------

    readonly Dictionary<string, double> lastUpAt = new Dictionary<string, double>();
    readonly HashSet<string> bounced = new HashSet<string>();
    const double BounceMs = 120; // these remotes sometimes repeat a press ~30 ms later

    // Every raw event lands here, on the input thread. Suppression bookkeeping happens right away;
    // actions and UI updates are posted to the UI thread in order.
    void OnRawInput(InputEvent e)
    {
        if (e.Kind == "device") { Ui.Post(_ => OnDeviceChange(), null); return; }
        if (e.Edge == 0 || e.Device == null) return;
        var r = Remotes.FirstOrDefault(x => x.Vid == e.Device.Vid && x.Pid == e.Device.Pid);

        // Driver loaded without a reboot: every keystroke tells the bridge which slot is which keyboard.
        if (e.Kind == "key" && e.Device.Vid != -1 && InterceptionActive && interception.Learning
            && interception.Observe(r != null ? r.HardwareTag : null, e.Key.Split('/')[1], e.Edge == -1))
        {
            var linked = r;
            Ui.Post(_ => Raise(linked.Name + " keyboard keys linked — they now follow your profile"), null);
        }

        if (r == null) return;
        var sig = Resolve(r, e);
        if (sig == null) return;
        int edge = e.Edge;

        if (sig.Source == "keyboard")
        {
            if (InterceptionActive && interception.Covers(r.HardwareTag)) return; // handled (and possibly swallowed) on the driver path
            Ui.Post(_ => OnUnfilteredKey(r, sig, edge), null);
            return;
        }

        // Consumer key: every mapped press must swallow exactly one synthesised virtual key, even a bounce.
        bool mapped = !Paused && r.Actions.ContainsKey(sig.Id);
        ushort vk;
        if (mapped && edge == 1 && consumerVk.TryGetValue(sig.Usage, out vk))
        {
            Queue<double> q;
            if (!pendingBlock.TryGetValue(vk, out q)) pendingBlock[vk] = q = new Queue<double>();
            q.Enqueue(e.Ms);
        }

        // Debounce: a repeat press within BounceMs of the release is the remote stuttering, not the user.
        string key = r.Id + "/" + sig.Id;
        if (edge == 1)
        {
            double up;
            if (lastUpAt.TryGetValue(key, out up) && e.Ms - up < BounceMs) { bounced.Add(key); return; }
        }
        else
        {
            if (bounced.Remove(key)) return;
            lastUpAt[key] = e.Ms;
        }
        Ui.Post(_ => HandleButton(r, sig, edge), null);
    }

    // Remote keyboard keys the driver does not cover (not installed, or remote not linked yet).
    void OnUnfilteredKey(RemoteDef r, ButtonSig sig, int edge)
    {
        // Without the driver the key cannot be told apart from the main keyboard, so a mapped
        // action would double up with the original key: report it and leave it alone.
        bool wouldMap = !Paused && r.Actions.ContainsKey(sig.Id);
        if (Web != null) Web.Broadcast("press", new Dictionary<string, object> { { "remote", r.Id }, { "button", sig.Id }, { "edge", edge }, { "action", null } });
        if (wouldMap && !InterceptionActive && warned.Add(r.Id + "/" + sig.Id))
        {
            Log.Write("{0} {1}: mapped in {2} but needs the Interception driver - passing through", r.Name, sig.Id, r.Profile.Name);
            Raise(r.Name + " " + sig.Id + " needs the Interception driver (Settings)");
        }
    }

    bool OnInterceptedKey(string hardwareId, string scan, bool up)
    {
        var r = Remotes.FirstOrDefault(x => hardwareId.IndexOf(x.HardwareTag, StringComparison.OrdinalIgnoreCase) >= 0);
        if (r == null) return false;
        var sig = r.Buttons.FirstOrDefault(b => b.Source == "keyboard" && b.Scan == scan);
        if (sig == null) return false;
        return HandleButton(r, sig, up ? -1 : 1);
    }

    void InstallHook()
    {
        IntPtr old = hook;
        hookProc = HookCallback;
        IntPtr fresh = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, hookProc, Win32.GetModuleHandle(null), 0);
        if (fresh == IntPtr.Zero) { Log.Write("keyboard hook failed: {0}", Marshal.GetLastWin32Error()); return; }
        hook = fresh;
        if (old != IntPtr.Zero) Win32.UnhookWindowsHookEx(old);
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == Win32.HC_ACTION)
        {
            var k = (Win32.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Win32.KBDLLHOOKSTRUCT));
            if (k.dwExtraInfo != Win32.Marker && ShouldBlock((ushort)k.vkCode, (k.flags & Win32.LLKHF_UP) != 0))
                return new IntPtr(1);
        }
        return Win32.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // Counts of synthesised key-downs we swallowed, so the matching key-ups go too.
    readonly Dictionary<ushort, int> blockedDowns = new Dictionary<ushort, int>();

    bool ShouldBlock(ushort vk, bool up)
    {
        if (vk < 0xA6 || vk > 0xB7) return false; // only browser/media/volume keys are synthesised from consumer reports
        // The remote's raw report may still be queued behind this hook call; process it first.
        Win32.MSG msg;
        while (Win32.PeekMessage(out msg, listener.Handle, (uint)Native.WM_INPUT, (uint)Native.WM_INPUT, Win32.PM_REMOVE)) Win32.DispatchMessage(ref msg);

        int downs;
        if (up)
        {
            if (!blockedDowns.TryGetValue(vk, out downs) || downs == 0) return false;
            blockedDowns[vk] = downs - 1;
            return true;
        }
        Queue<double> q;
        double now = clock.Elapsed.TotalMilliseconds;
        if (pendingBlock.TryGetValue(vk, out q))
        {
            while (q.Count > 0 && now - q.Peek() > 400) q.Dequeue(); // stale: its key never came
            if (q.Count > 0)
            {
                q.Dequeue();
                blockedDowns.TryGetValue(vk, out downs);
                blockedDowns[vk] = downs + 1;
                return true;
            }
        }
        return false;
    }

    // Proves the hook is installed and blocking: queue a block for Browser Stop (harmless when it
    // leaks), inject one untagged, and check the hook ate it. Re-installs the hook if not.
    public string SelfTest()
    {
        if (inputCtx == null) return "input thread not running";
        string result = null;
        inputCtx.Send(_ =>
        {
            const ushort BrowserStop = 0xA9;
            Queue<double> q;
            if (!pendingBlock.TryGetValue(BrowserStop, out q)) pendingBlock[BrowserStop] = q = new Queue<double>();
            q.Enqueue(clock.Elapsed.TotalMilliseconds);
            int before;
            blockedDowns.TryGetValue(BrowserStop, out before);
            var down = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
            down.wVk = BrowserStop;
            down.kflags = Win32.KEYEVENTF_EXTENDEDKEY;
            var upKey = down;
            upKey.kflags |= Win32.KEYEVENTF_KEYUP;
            Win32.SendInput(2, new[] { down, upKey }, Marshal.SizeOf(typeof(Win32.INPUT)));
            // Let the hook run (it is called on this thread while we pump messages).
            var until = clock.Elapsed.TotalMilliseconds + 300;
            while (clock.Elapsed.TotalMilliseconds < until) { Application.DoEvents(); Thread.Sleep(5); }
            int after;
            blockedDowns.TryGetValue(BrowserStop, out after);
            bool ok = pendingBlock[BrowserStop].Count == 0 && after == before;
            if (!ok) { pendingBlock[BrowserStop].Clear(); InstallHook(); }
            result = ok ? "keyboard hook OK" : "keyboard hook was not blocking - reinstalled";
        }, null);
        Log.Write("self-test: {0}", result);
        return result;
    }

    // ------------------------------------------------------------ input spots

    void StepSpot(int delta)
    {
        int n = SpotList.Count;
        if (n == 0) { Raise("No input spots yet — add some in Airdeck"); return; }
        var fg = Spots.Foreground;
        int cur = lastSpot >= 0 && lastSpot < n && Spots.Matches(SpotList[lastSpot], fg) ? lastSpot : SpotList.FindIndex(s => Spots.Matches(s, fg));
        JumpSpot(cur < 0 ? (delta > 0 ? 0 : n - 1) : ((cur + delta) % n + n) % n);
    }

    public void JumpSpot(int index)
    {
        if (index < 0 || index >= SpotList.Count) { Raise("Input spot " + (index + 1) + " does not exist"); return; }
        if (Interlocked.CompareExchange(ref jumping, 1, 0) != 0) return;
        lastSpot = index;
        var spot = SpotList[index];
        if (Notify != null) Notify("→  " + (index + 1) + ".  " + spot.Label);
        if (Web != null) Web.Broadcast("spot", new Dictionary<string, object> { { "index", index } });
        Task.Run(() =>
        {
            string error = null;
            try { error = Spots.Jump(spot); }
            catch (Exception ex) { error = ex.Message; }
            Interlocked.Exchange(ref jumping, 0);
            if (error != null) Ui.Post(_ => Raise(spot.Label + ": " + error), null);
        });
    }

    public InputSpot CaptureSpot()
    {
        var s = Spots.CaptureFocused();
        Ui.Send(_ => { SpotList.Add(s); SaveSpots(); Raise("Saved input spot " + SpotList.Count + ":  " + s.Label); }, null);
        return s;
    }

    // ------------------------------------------------------------ settings

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool StartWithWindows
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("Airdeck") != null; }
        set
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value) k.SetValue("Airdeck", "\"" + Application.ExecutablePath + "\" --tray");
                else k.DeleteValue("Airdeck", false);
            }
        }
    }

    public static bool Elevated { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }

    // ------------------------------------------------------------ API for the UI

    Dictionary<string, object> StateJson()
    {
        var templates = new Dictionary<string, object>();
        string tdir = Path.Combine(Root, "remote-templates");
        if (Directory.Exists(tdir))
            foreach (var f in Directory.GetFiles(tdir, "*.json")) templates[Path.GetFileNameWithoutExtension(f)] = Json.Read(File.ReadAllText(f));

        return new Dictionary<string, object>
        {
            { "remotes", Remotes.Select(r => (object)new Dictionary<string, object>
                {
                    { "id", r.Id }, { "name", r.Name }, { "vid", r.Vid.ToString("X4") }, { "pid", r.Pid.ToString("X4") },
                    { "connected", r.Connected }, { "profile", r.Profile.Id },
                    { "buttons", r.Buttons.Select(b => (object)new Dictionary<string, object>
                        {
                            { "id", b.Id }, { "source", b.Source }, { "semantics", b.Semantics },
                            { "usage", b.Source == "consumer" ? "0x" + (b.Usage & 0xFFFF).ToString("X4") : null },
                            { "key", b.Source == "keyboard" ? ((Keys)b.Vk).ToString() : null },
                        }).ToList() },
                }).ToList() },
            { "profiles", Profiles.Select(p => (object)p.ToJson()).ToList() },
            { "templates", templates },
            { "paused", Paused },
            { "spots", SpotList.Select(s => (object)s.ToJson()).ToList() },
            { "flow", new Dictionary<string, object> { { "ptt", Flow.Describe(Flow.Ptt) }, { "handsfree", Flow.Describe(Flow.HandsFree) }, { "command", Flow.Describe(Flow.Command) } } },
            { "interception", new Dictionary<string, object>
                {
                    { "installed", InterceptionInstalled }, { "active", InterceptionActive },
                    { "filtered", interception != null ? interception.FilteredCount : 0 },
                    { "learning", interception != null && interception.Learning },
                    { "linked", Remotes.Where(r => InterceptionActive && interception.Covers(r.HardwareTag)).Select(r => (object)r.Id).ToList() },
                } },
            { "settings", new Dictionary<string, object> { { "startWithWindows", StartWithWindows }, { "elevated", Elevated }, { "root", Root } } },
        };
    }

    static string Slug(string name)
    {
        string s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s == "" ? "profile" : s;
    }

    // Runs on a web worker thread; anything touching controller state is marshalled to the UI thread.
    public HttpResult Api(string method, string path, string body)
    {
        Dictionary<string, object> req = null;
        if (body != "") req = Json.Read(body) as Dictionary<string, object>;
        Func<string, string> str = k => req != null && req.ContainsKey(k) && req[k] != null ? req[k].ToString() : null;
        try
        {
            if (path == "/api/spot-capture")
            {
                int delay = Math.Min(10000, Math.Max(0, int.Parse(str("delayMs") ?? "0")));
                Thread.Sleep(delay);
                return HttpResult.Json(CaptureSpot().ToJson());
            }
            HttpResult result = null;
            Ui.Send(_ => { result = ApiOnUi(method, path, req, str); }, null);
            return result;
        }
        catch (Exception ex)
        {
            Log.Write("api {0} failed: {1}", path, ex.Message);
            return HttpResult.Error(400, ex.Message);
        }
    }

    HttpResult ApiOnUi(string method, string path, Dictionary<string, object> req, Func<string, string> str)
    {
        switch (path)
        {
            case "/api/state":
                return HttpResult.Json(StateJson());

            case "/api/remote-profile":
            {
                var r = Remotes.First(x => x.Id == str("remote"));
                SetProfile(r, str("profile"));
                return HttpResult.Json(StateJson());
            }

            case "/api/pause":
                SetPaused(str("paused") == "True");
                return HttpResult.Json(StateJson());

            case "/api/profile":
            {
                string id = str("id");
                if (id == "stock") throw new InvalidOperationException("The Stock profile cannot be edited. Duplicate it first.");
                string name = (str("name") ?? "").Trim();
                if (name == "") throw new InvalidOperationException("A profile needs a name.");
                if (string.IsNullOrEmpty(id))
                {
                    id = Slug(name);
                    for (int i = 2; File.Exists(Path.Combine(ProfilesDir, id + ".json")); i++) id = Slug(name) + "-" + i;
                }
                var doc = new Dictionary<string, object>
                {
                    { "name", name }, { "description", str("description") ?? "" },
                    { "buttons", req.ContainsKey("buttons") ? req["buttons"] : new Dictionary<string, object>() },
                };
                // Validate every action before touching the file.
                foreach (var kv in (Dictionary<string, object>)doc["buttons"])
                    if (!kv.Key.StartsWith("_")) RemoteAction.Create((Dictionary<string, object>)kv.Value);
                File.WriteAllText(Path.Combine(ProfilesDir, id + ".json"), Json.Write(doc));
                LoadProfiles();
                Raise(null);
                return HttpResult.Json(new Dictionary<string, object> { { "id", id }, { "state", StateJson() } });
            }

            case "/api/profile-delete":
            {
                string id = str("id");
                if (id == "stock") throw new InvalidOperationException("The Stock profile cannot be deleted.");
                string file = Path.Combine(ProfilesDir, id + ".json");
                if (File.Exists(file))
                {
                    Directory.CreateDirectory(Path.Combine(Root, "data", "deleted-profiles"));
                    File.Move(file, Path.Combine(Root, "data", "deleted-profiles", id + "-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".json"));
                }
                foreach (var r in Remotes.Where(x => x.Profile.Id == id)) ApplyProfile(r, "stock");
                LoadProfiles();
                SaveState();
                Raise(null);
                return HttpResult.Json(StateJson());
            }

            case "/api/spots":
                SpotList = ((object[])req["spots"]).Select(o => InputSpot.FromJson((Dictionary<string, object>)o)).ToList();
                SaveSpots();
                Raise(null);
                return HttpResult.Json(StateJson());

            case "/api/spot-jump":
                JumpSpot(int.Parse(str("index")));
                return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });

            case "/api/settings":
                if (str("startWithWindows") != null) StartWithWindows = str("startWithWindows") == "True";
                Raise(null);
                return HttpResult.Json(StateJson());

            case "/api/restart-admin":
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--wait-for " + Process.GetCurrentProcess().Id) { UseShellExecute = true, Verb = "runas" });
                Ui.Post(_ => Application.Exit(), null);
                return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });

            case "/api/open":
            {
                string what = str("what");
                string target = what == "profiles" ? ProfilesDir : what == "log" ? Log.FilePath : what == "driver" ? Path.Combine(Root, "tools") : Root;
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return HttpResult.Json(new Dictionary<string, object> { { "ok", true } });
            }

            case "/api/selftest":
            {
                string hook = null;
                var t = new Thread(() => hook = SelfTest()); // SelfTest waits on the input thread; never block the UI thread's pump on it
                t.Start();
                while (t.IsAlive) { Application.DoEvents(); Thread.Sleep(10); }
                return HttpResult.Json(new Dictionary<string, object>
                {
                    { "hook", hook }, { "interception", InterceptionActive ? (interception.Learning ? "restart Windows to finish" : "active") : InterceptionInstalled ? "installed, not loaded" : "not installed" },
                    { "remotes", Remotes.Select(r => (object)(r.Name + (r.Connected ? " connected" : " not connected"))).ToList() },
                });
            }

            case "/api/reload":
                LoadProfiles();
                LoadSpots();
                Raise("Reloaded");
                return HttpResult.Json(StateJson());
        }
        return HttpResult.Error(404, "unknown endpoint " + path);
    }

    public void Dispose()
    {
        foreach (var a in active.Values) a.Cancel();
        active.Clear();
        Output.ReleaseAll();
        if (interception != null) interception.Dispose();
        if (watchdog != null) watchdog.Dispose();
        // The hook and listener window belong to the input thread; tear them down there.
        if (inputCtx != null)
            inputCtx.Send(_ =>
            {
                if (hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
                if (listener != null) listener.DestroyHandle();
                Application.ExitThread();
            }, null);
    }
}

// ------------------------------------------------------------------ shell

class Toast : Form
{
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2200 };
    string text = "";
    readonly Font titleFont = new Font("Bahnschrift SemiBold", 12.5f);

    public Toast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(22, 23, 26);
        ForeColor = Color.FromArgb(236, 232, 224);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        timer.Tick += (s, e) => { timer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // NOACTIVATE | TOOLWINDOW | TOPMOST
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var accent = new SolidBrush(Color.FromArgb(255, 178, 36))) e.Graphics.FillRectangle(accent, 0, 0, 4, Height);
        using (var edge = new Pen(Color.FromArgb(52, 54, 60))) e.Graphics.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        var pad = TextRenderer.MeasureText("M", titleFont);
        TextRenderer.DrawText(e.Graphics, text, titleFont, new Point(pad.Width + 4, pad.Height / 2), ForeColor);
    }

    public void ShowMessage(string message)
    {
        text = message;
        var size = TextRenderer.MeasureText(text, titleFont);
        var pad = TextRenderer.MeasureText("M", titleFont);
        var wa = Screen.PrimaryScreen.WorkingArea;
        Bounds = new Rectangle(wa.Right - (size.Width + pad.Width * 2 + 4) - 24, wa.Bottom - (size.Height + pad.Height) - 24,
                               size.Width + pad.Width * 2 + 4, size.Height + pad.Height);
        if (!Visible) Show();
        Invalidate();
        timer.Stop();
        timer.Start();
    }
}

class HotkeyWindow : NativeWindow
{
    public event Action<int> Pressed;
    public HotkeyWindow() { CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
        base.WndProc(ref m);
    }
}

class TrayApp : ApplicationContext
{
    const int HotExit = 1, HotPause = 2, HotNext = 3, HotCapture = 4;

    readonly Controller controller;
    readonly NotifyIcon tray;
    readonly Toast toast = new Toast();
    readonly HotkeyWindow hotkeys = new HotkeyWindow();
    readonly List<RegisteredWaitHandle> waits = new List<RegisteredWaitHandle>();
    readonly SynchronizationContext ui;
    readonly WebServer web;

    public TrayApp(Controller controller, EventWaitHandle exitSignal, EventWaitHandle showSignal, bool openWindow)
    {
        this.controller = controller;
        ui = SynchronizationContext.Current;
        controller.Ui = ui;
        toast.CreateControl();

        tray = new NotifyIcon { Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        tray.ContextMenuStrip.Opening += (s, e) => BuildMenu();
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenWindow(); };

        controller.Changed += () => ui.Post(_ => UpdateTray(), null);
        controller.Notify += text => ui.Post(_ => toast.ShowMessage(text), null);

        web = new WebServer(Path.Combine(controller.Root, "ui"), controller.Api);
        web.Start(47800);
        controller.Web = web;
        controller.Start();

        uint mods = Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT;
        if (!Win32.RegisterHotKey(hotkeys.Handle, HotExit, mods, (uint)Keys.F12)) Log.Write("Ctrl+Alt+Shift+F12 is taken by another app");
        Win32.RegisterHotKey(hotkeys.Handle, HotPause, mods, (uint)Keys.F11);
        Win32.RegisterHotKey(hotkeys.Handle, HotNext, mods, (uint)Keys.F10);
        Win32.RegisterHotKey(hotkeys.Handle, HotCapture, mods, (uint)Keys.F9);
        hotkeys.Pressed += id =>
        {
            if (id == HotExit) ExitThread();
            else if (id == HotPause) controller.SetPaused(!controller.Paused);
            else if (id == HotNext && controller.FocusRemote != null) controller.CycleProfile(controller.FocusRemote);
            else if (id == HotCapture) Task.Run(() => { try { controller.CaptureSpot(); } catch (Exception ex) { ui.Post(_ => toast.ShowMessage("Capture failed: " + ex.Message), null); } });
        };

        waits.Add(ThreadPool.RegisterWaitForSingleObject(exitSignal, (s, t) => ui.Post(_ => ExitThread(), null), null, -1, true));
        waits.Add(ThreadPool.RegisterWaitForSingleObject(showSignal, (s, t) => { showSignal.Reset(); ui.Post(_ => OpenWindow(), null); }, null, -1, false));
        UpdateTray();
        if (openWindow) OpenWindow();
        else toast.ShowMessage("Airdeck is running\n" + Summary());
    }

    string Summary()
    {
        if (controller.Paused) return "Paused — all remotes stock";
        return string.Join("\n", controller.Remotes.Select(r => r.Name + ":  " + r.Profile.Name + (r.Connected ? "" : "  (not connected)")));
    }

    DateTime lastLaunch = DateTime.MinValue;

    // One app window only: focus the existing one; don't launch again while one is still starting.
    // Short, glanceable tray tooltip (Windows caps it at 63 characters):
    //   Airdeck / <what the remote in hand is doing> / Click to open
    string Tooltip()
    {
        string state;
        var connected = controller.Remotes.Where(r => r.Connected).ToList();
        if (controller.Paused) state = "Paused — remotes are stock";
        else if (connected.Count == 0) state = "No remote connected";
        else
        {
            var r = controller.FocusRemote;
            string profile = r.Profile.Name.Length > 28 ? r.Profile.Name.Substring(0, 27) + "…" : r.Profile.Name;
            state = profile + " · " + r.Name;
            if (connected.Count > 1 && connected.Any(x => x.Profile != r.Profile)) state += " +" + (connected.Count - 1);
        }
        string tip = "Airdeck\n" + state + "\nClick to open";
        return tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    void OpenWindow()
    {
        IntPtr existing = Spots.FindWindowByTitle("msedge", "Airdeck");
        if (existing != IntPtr.Zero) { Spots.BringToFront(existing); return; }
        if ((DateTime.Now - lastLaunch).TotalSeconds < 6) return;
        lastLaunch = DateTime.Now;
        string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        if (!File.Exists(edge)) edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe");
        try
        {
            if (File.Exists(edge))
            {
                // A private profile keeps the app window separate from the user's normal browsing.
                string profile = Path.Combine(controller.Root, "data", "app-window");
                // Shell-execute so Edge inherits none of our handles (it would otherwise keep the UI port open after we exit).
                Process.Start(new ProcessStartInfo(edge, string.Format("--app={0} --user-data-dir=\"{1}\" --window-size=1480,940 --no-first-run --disable-features=Translate", web.Url, profile)) { UseShellExecute = true });
            }
            else Process.Start(web.Url);
        }
        catch (Exception ex) { Log.Write("could not open window: {0}", ex.Message); Process.Start(web.Url); }
    }

    void UpdateTray()
    {
        tray.Text = Tooltip();
        var old = tray.Icon;
        tray.Icon = MakeIcon(!controller.Paused);
        if (old != null) { Win32.DestroyIcon(old.Handle); old.Dispose(); }
    }

    // The app icon; greyed out while every remote is paused.
    Icon MakeIcon(bool active)
    {
        var size = SystemInformation.SmallIconSize;
        string ico = Path.Combine(controller.Root, "assets", "airdeck.ico");
        using (var src = File.Exists(ico) ? new Icon(ico, size) : Icon.ExtractAssociatedIcon(Application.ExecutablePath))
        using (var bmp = src.ToBitmap())
        {
            if (!active)
            {
                var gray = new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new float[] { .3f, .3f, .3f, 0, 0 }, new float[] { .59f, .59f, .59f, 0, 0 }, new float[] { .11f, .11f, .11f, 0, 0 },
                    new float[] { 0, 0, 0, .8f, 0 }, new float[] { 0, 0, 0, 0, 1 },
                });
                using (var attrs = new System.Drawing.Imaging.ImageAttributes())
                using (var copy = new Bitmap(bmp))
                using (var g = Graphics.FromImage(bmp))
                {
                    attrs.SetColorMatrix(gray);
                    g.Clear(Color.Transparent);
                    g.DrawImage(copy, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, copy.Width, copy.Height, GraphicsUnit.Pixel, attrs);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    void BuildMenu()
    {
        var m = tray.ContextMenuStrip;
        m.Items.Clear();
        m.Items.Add(new ToolStripMenuItem("Open Airdeck", null, (s, e) => OpenWindow()) { Font = new Font(m.Font, FontStyle.Bold) });
        m.Items.Add(new ToolStripSeparator());
        foreach (var r in controller.Remotes)
        {
            var rr = r;
            var sub = new ToolStripMenuItem(string.Format("{0}   ·   {1}{2}", r.Name, r.Profile.Name, r.Connected ? "" : "   (not connected)"));
            foreach (var p in controller.Profiles)
            {
                var pp = p;
                var item = new ToolStripMenuItem(p.Name) { Checked = r.Profile == p, ToolTipText = p.Description };
                item.Click += (s, e) => controller.SetProfile(rr, pp.Id);
                sub.DropDownItems.Add(item);
            }
            m.Items.Add(sub);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Pause all remotes (stock)", null, (s, e) => controller.SetPaused(!controller.Paused))
        {
            Checked = controller.Paused, ShortcutKeyDisplayString = "Ctrl+Alt+Shift+F11"
        });
        m.Items.Add(new ToolStripMenuItem("Save focused input as spot", null, (s, e) => toast.ShowMessage("Click into an input box, then press Ctrl+Alt+Shift+F9"))
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+Shift+F9"
        });
        string mapper = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "airdeck-mapper.exe");
        if (File.Exists(mapper))
            m.Items.Add(new ToolStripMenuItem("Button Mapper (new remotes)", null, (s, e) => Process.Start(mapper)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitThread()) { ShortcutKeyDisplayString = "Ctrl+Alt+Shift+F12" });
    }

    protected override void ExitThreadCore()
    {
        Log.Write("exiting");
        foreach (var w in waits) w.Unregister(null);
        controller.Dispose();
        web.Dispose();
        tray.Visible = false;
        tray.Dispose();
        hotkeys.DestroyHandle();
        base.ExitThreadCore();
    }
}

static class AirdeckProgram
{
    const string ExitEventName = "Local\\AirdeckController.Exit", ShowEventName = "Local\\AirdeckController.Show";

    static EventWaitHandle CreateEvent(string name)
    {
        // Any local user may signal it, so "--exit" and "open window" work against an elevated instance too.
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        bool created;
        return new EventWaitHandle(false, EventResetMode.ManualReset, name, out created, security);
    }

    static bool Signal(string name)
    {
        EventWaitHandle ev;
        if (!EventWaitHandle.TryOpenExisting(name, EventWaitHandleRights.Modify, out ev)) return false;
        ev.Set();
        ev.Dispose();
        return true;
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--exit")) return Signal(ExitEventName) ? 0 : 1;

        int waitIdx = Array.IndexOf(args, "--wait-for");
        if (waitIdx >= 0 && waitIdx + 1 < args.Length)
        {
            try { Process.GetProcessById(int.Parse(args[waitIdx + 1])).WaitForExit(10000); } catch (ArgumentException) { }
        }

        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (var dir = new DirectoryInfo(root); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "remote-devices.json"))) { root = dir.FullName; break; }

        bool first;
        using (var mutex = new Mutex(true, "Local\\AirdeckController", out first))
        {
            if (!first)
            {
                // Already running: bring up its window instead of starting a second controller.
                if (!args.Contains("--tray")) Signal(ShowEventName);
                return 0;
            }
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Log.Init(Path.Combine(root, "logs"));
            Log.Write("starting from {0} (elevated: {1})", root, Controller.Elevated);
            Application.ThreadException += (s, e) => Log.Write("error: {0}", e.Exception);

            using (var exitSignal = CreateEvent(ExitEventName))
            using (var showSignal = CreateEvent(ShowEventName))
            {
                exitSignal.Reset();
                showSignal.Reset();
                Flow.Load();
                Controller controller;
                try { controller = new Controller(root); }
                catch (Exception ex)
                {
                    Log.Write("startup failed: {0}", ex);
                    MessageBox.Show("Airdeck could not start:\n\n" + ex.Message, "Airdeck");
                    return 1;
                }
                // WindowsFormsSynchronizationContext must exist before the tray app captures it.
                using (var anchor = new Control()) { anchor.CreateControl(); }
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Application.Run(new TrayApp(controller, exitSignal, showSignal, !args.Contains("--tray")));
            }
        }
        return 0;
    }
}

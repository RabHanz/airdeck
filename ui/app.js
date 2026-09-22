// Airdeck UI. Talks to airdeck.exe over /api/* and listens to /api/events for live presses.
"use strict";

const S = {
  state: null,
  remote: null,        // remote id shown on the Remote view
  editProfile: null,   // profile being edited when it is not the remote's active one
  selected: null,      // selected button id
  view: "live",
  presses: [],         // timeline entries
  recording: false,    // shortcut recorder is listening
  typedKeys: false,    // shortcut entered as text instead
};

const $ = (sel, root = document) => root.querySelector(sel);
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const glyphChar = (hex) => String.fromCharCode(parseInt(hex, 16));

async function api(path, body) {
  const opts = body === undefined ? {} : {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Airdeck": "1" },
    body: JSON.stringify(body),
  };
  const res = await fetch(path, opts);
  const json = await res.json();
  if (!res.ok) throw new Error(json.error || res.statusText);
  return json;
}

let toastTimer;
function toast(msg, err = false) {
  const t = $("#toast");
  t.textContent = msg;
  t.classList.toggle("err", err);
  t.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove("show"), 2600);
}
const fail = (e) => toast(e.message || String(e), true);

// ------------------------------------------------------------------ model helpers

const remoteById = (id) => S.state.remotes.find((r) => r.id === id);
const profileById = (id) => S.state.profiles.find((p) => p.id === id);
const tplFor = (id) => S.state.templates[id];
const currentRemote = () => remoteById(S.remote);
const editingProfile = () => profileById(S.editProfile || currentRemote().profile);

function buttonLabel(id, remoteId) {
  const order = remoteId ? [remoteId, ...Object.keys(S.state.templates)] : Object.keys(S.state.templates);
  for (const rid of order) {
    const b = tplFor(rid)?.buttons.find((x) => x.id === id);
    if (b) return b.label;
  }
  return id.replace(/_/g, " ");
}

const ACTIONS = [
  { group: "Wispr Flow", items: [
    { id: "flow_ptt", name: "Push-to-talk", hint: "Hold to dictate, release to insert", hold: true },
    { id: "flow_handsfree", name: "Hands-free", hint: "Tap to start, tap again to stop" },
    { id: "flow_command", name: "Command mode", hint: "Hold and speak an instruction", hold: true },
    { id: "flow_cancel", name: "Cancel", hint: "Esc - discard the dictation" },
  ]},
  { group: "Workflow", items: [
    { id: "spot_next", name: "Next input spot", hint: "Hop to the next saved input" },
    { id: "spot_prev", name: "Previous spot", hint: "Hop back one input" },
    { id: "spot_goto", name: "Go to spot...", hint: "Jump straight to one input", param: true },
    { id: "desktop_next", name: "Next desktop", hint: "Win+Ctrl+Right" },
    { id: "desktop_prev", name: "Previous desktop", hint: "Win+Ctrl+Left" },
  ]},
  { group: "Keyboard", items: [
    { id: "keys", name: "Shortcut...", hint: "Any key combination", param: true, hold: true },
    { id: "text", name: "Type text...", hint: "Insert a saved snippet", param: true },
  ]},
  { group: "Mouse", items: [
    { id: "left_click", name: "Left click", hint: "Hold to drag", hold: true },
    { id: "right_click", name: "Right click", hint: "Context menu", hold: true },
    { id: "middle_click", name: "Middle click", hint: "Open in new tab", hold: true },
  ]},
  { group: "System", items: [
    { id: "run", name: "Open app / file...", hint: "Launch anything", param: true },
    { id: "block", name: "Disable button", hint: "Do nothing at all" },
  ]},
];
const ACTION_INFO = Object.fromEntries(ACTIONS.flatMap((g) => g.items.map((i) => [i.id, i])));

const KEY_NAMES = {
  ctrl: "Ctrl", control: "Ctrl", lctrl: "Ctrl", rctrl: "RCtrl", shift: "Shift", lshift: "Shift", rshift: "RShift",
  alt: "Alt", lalt: "Alt", ralt: "AltGr", win: "Win", lwin: "Win", rwin: "Win", esc: "Esc", escape: "Esc",
  enter: "Enter", return: "Enter", right: "\u2192", left: "\u2190", up: "\u2191", down: "\u2193", oemtilde: "`",
  space: "Space", tab: "Tab", back: "Backspace", backspace: "Backspace", delete: "Del", del: "Del",
  pageup: "PgUp", pagedown: "PgDn", next: "PgDn", oemminus: "-", oemplus: "=", oemcomma: ",", oemperiod: ".",
  oemquestion: "/", oemsemicolon: ";", oemquotes: "'", oemopenbrackets: "[", oemclosebrackets: "]", oempipe: "\\",
};
const prettyKey = (k) => KEY_NAMES[k.toLowerCase()] ?? (k.length === 1 ? k.toUpperCase() : k[0].toUpperCase() + k.slice(1));
const prettyChord = (spec) => (spec || "").split("+").filter(Boolean).map(prettyKey).join(" + ");
const keycaps = (spec) => (spec || "").split("+").filter(Boolean).map((k) => `<span class="keycap">${esc(prettyKey(k))}</span>`).join('<span class="plus">+</span>');

function actionName(spec) {
  if (!spec || spec.action === "passthrough") return "Default";
  switch (spec.action) {
    case "flow_ptt": return "Flow push-to-talk";
    case "flow_handsfree": return "Flow hands-free";
    case "flow_command": return "Flow command mode";
    case "flow_cancel": return "Cancel dictation";
    case "spot_goto": {
      const spot = S.state.spots[(spec.spot | 0) - 1];
      return spot ? `Spot ${spec.spot} \u00B7 ${spot.label}` : `Input spot ${spec.spot}`;
    }
    case "keys": return prettyChord(spec.keys) || "Shortcut";
    case "text": return `Type \u201C${(spec.text || "").slice(0, 18)}${(spec.text || "").length > 18 ? "\u2026" : ""}\u201D`;
    case "run": return "Open " + ((spec.run || "").split(/[\\/]/).pop() || "app");
    default: return ACTION_INFO[spec.action]?.name.replace("...", "") ?? spec.action;
  }
}

function buttonCaps(remote, btnId) {
  const sig = remote.buttons.find((b) => b.id === btnId);
  if (!sig || sig.source === "none") return { dead: true };
  const tapOnly = btnId === "mic";
  const needsDriver = sig.source === "keyboard" && !S.state.interception.active;
  return { sig, tapOnly, needsDriver };
}

// ------------------------------------------------------------------ remote drawing

const pt = (cx, cy, r, deg) => [cx + r * Math.cos(deg * Math.PI / 180), cy + r * Math.sin(deg * Math.PI / 180)];

function ringPath(cx, cy, r, r2, a0, a1) {
  const [x1, y1] = pt(cx, cy, r, a0), [x2, y2] = pt(cx, cy, r, a1);
  const [x3, y3] = pt(cx, cy, r2, a1), [x4, y4] = pt(cx, cy, r2, a0);
  const large = a1 - a0 > 180 ? 1 : 0;
  return `M${x1},${y1} A${r},${r} 0 ${large} 1 ${x2},${y2} L${x3},${y3} A${r2},${r2} 0 ${large} 0 ${x4},${y4} Z`;
}

function roundRectPath(x, y, w, h, top, bottom) {
  top = Math.min(top, w / 2); bottom = Math.min(bottom, w / 2);
  return `M${x},${y + top} A${top},${top} 0 0 1 ${x + top},${y} H${x + w - top} A${top},${top} 0 0 1 ${x + w},${y + top}
          V${y + h - bottom} A${bottom},${bottom} 0 0 1 ${x + w - bottom},${y + h} H${x + bottom} A${bottom},${bottom} 0 0 1 ${x},${y + h - bottom} Z`;
}

const DPAD = { dpad_up: 225, dpad_right: 315, dpad_down: 45, dpad_left: 135 };
const DPAD_MID = { dpad_up: 270, dpad_right: 0, dpad_down: 90, dpad_left: 180 };

function shapeSvg(b) {
  if (DPAD[b.shape] !== undefined) { const s = DPAD[b.shape]; return `<path class="shape" d="${ringPath(b.x, b.y, b.r, b.r2, s + 2, s + 88)}"/>`; }
  if (b.shape === "band") return `<path class="shape" d="${ringPath(b.x, b.y, b.r, b.r2, b.a0, b.a1)}"/>`;
  if (b.shape === "rrect") return `<rect class="shape" x="${b.x - b.w / 2}" y="${b.y - b.h / 2}" width="${b.w}" height="${b.h}" rx="${b.r || 10}"/>`;
  return `<circle class="shape" cx="${b.x}" cy="${b.y}" r="${b.r}"/>`;
}

function glyphCenter(b) {
  let deg = null;
  if (DPAD_MID[b.shape] !== undefined) deg = DPAD_MID[b.shape];
  else if (b.shape === "band") deg = (b.a0 + b.a1) / 2;
  if (deg === null) return [b.x, b.y];
  return pt(b.x, b.y, (b.r + b.r2) / 2, deg);
}

function glyphSize(b) {
  if (DPAD[b.shape] !== undefined || b.shape === "band") return 20;
  if (b.shape === "rrect") return Math.min(b.w, b.h) * 0.46;
  return b.r * 0.85;
}

function remoteSvg(remote, tpl, profile) {
  const W = tpl.width, H = tpl.height, G = 310;
  const mapped = (id) => { const a = profile.buttons[id]; return a && a.action !== "passthrough"; };

  const keys = tpl.buttons.map((b) => {
    const caps = buttonCaps(remote, b.id);
    const cls = ["bkey", caps.dead ? "dead" : "", mapped(b.id) && !caps.dead ? "mapped" : "", S.selected === b.id ? "sel" : ""].join(" ");
    const [gx, gy] = glyphCenter(b);
    const size = glyphSize(b);
    const label = b.glyph
      ? `<text class="glyph" x="${gx}" y="${gy + 1}" font-size="${size}">${glyphChar(b.glyph)}</text>`
      : `<text class="ktext" x="${gx}" y="${gy + 1}" font-size="${Math.max(12, size * 0.8)}">${esc(b.text || b.label)}</text>`;
    const cap = b.shape === "circle" && b.glyph ? `<text class="cap" x="${b.x}" y="${b.y + b.r + 12}">${esc(b.label)}</text>` : "";
    const title = caps.dead ? `<title>${esc(b.label)}: sends nothing to the PC</title>` : `<title>${esc(b.label)}</title>`;
    return `<g class="${cls}" data-b="${b.id}">${title}${shapeSvg(b)}${label}${cap}</g>`;
  }).join("");

  // Callouts: every mapped button gets a leader line out to a label in the side gutter.
  const items = tpl.buttons.filter((b) => mapped(b.id) && !buttonCaps(remote, b.id).dead).map((b) => {
    const [ax, ay] = glyphCenter(b);
    return { b, ax, ay, side: ax < W / 2 - 14 ? "L" : ax > W / 2 + 14 ? "R" : null };
  });
  for (const it of items.filter((i) => !i.side)) {
    const l = items.filter((i) => i.side === "L").length, r = items.filter((i) => i.side === "R").length;
    it.side = l <= r ? "L" : "R";
  }
  const GAP = 46;
  const callouts = ["L", "R"].map((side) => {
    const list = items.filter((i) => i.side === side).sort((a, b) => a.ay - b.ay);
    let prev = -Infinity;
    for (const it of list) { it.ly = Math.max(it.ay, prev + GAP); prev = it.ly; }
    let next = H - 20;
    for (const it of [...list].reverse()) { it.ly = Math.min(it.ly, next); next = it.ly - GAP; }
    return list.map((it, n) => {
      const spec = profile.buttons[it.b.id];
      const caps = buttonCaps(remote, it.b.id);
      const info = ACTION_INFO[spec.action] || {};
      let trig = info.hold ? "hold" : "press", warn = false;
      if (caps.needsDriver) { trig = "needs driver"; warn = true; }
      else if (caps.tapOnly && info.hold) { trig = "tap-only button"; warn = true; }
      const L = side === "L";
      const ex = L ? -12 : W + 12, lx = L ? -46 : W + 46, tx = L ? -54 : W + 54;
      const d = `M${it.ax},${it.ay} L${ex},${it.ay} L${lx},${it.ly} L${tx - (L ? -2 : 2)},${it.ly}`;
      const delay = (n * 0.06).toFixed(2);
      return `<g class="callout${S.selected === it.b.id ? " sel" : ""}" data-b="${it.b.id}">
        <path class="halo" d="${d}" style="animation-delay:${delay}s"/><path d="${d}" style="animation-delay:${delay}s"/>
        <circle cx="${it.ax}" cy="${it.ay}" r="3.2"/>
        <text class="ca" x="${tx}" y="${it.ly - 2}" text-anchor="${L ? "end" : "start"}" style="animation-delay:${(0.25 + n * 0.06).toFixed(2)}s">${esc(actionName(spec))}</text>
        <text class="cb${warn ? " warn" : ""}" x="${tx}" y="${it.ly + 14}" text-anchor="${L ? "end" : "start"}" style="animation-delay:${(0.3 + n * 0.06).toFixed(2)}s">${esc(it.b.label)} \u00B7 ${trig}</text>
      </g>`;
    }).join("");
  }).join("");

  const dots = (tpl.dots || []).map((d) => `<circle class="rdot" cx="${d.x}" cy="${d.y}" r="3"/>`).join("");
  return `<svg viewBox="${-G} -16 ${W + 2 * G} ${H + 32}" preserveAspectRatio="xMidYMid meet">
    <defs>
      <linearGradient id="bodyGrad" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2a2b30"/><stop offset="1" stop-color="#151619"/></linearGradient>
      <radialGradient id="sheen" cx=".25" cy=".08" r=".7"><stop offset="0" stop-color="#fff" stop-opacity=".07"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></radialGradient>
      <pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="6" height="6" fill="#18191c"/><line x1="0" y1="0" x2="0" y2="6" stroke="#26272b" stroke-width="2"/></pattern>
      <filter id="glow" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="5" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
      <filter id="drop" x="-20%" y="-10%" width="140%" height="130%"><feDropShadow dx="0" dy="18" stdDeviation="18" flood-color="#000" flood-opacity=".55"/></filter>
    </defs>
    <path class="rbody" filter="url(#drop)" d="${roundRectPath(0, 0, W, H, tpl.topCorner, tpl.bottomCorner)}"/>
    <path class="rsheen" d="${roundRectPath(0, 0, W, H, tpl.topCorner, tpl.bottomCorner)}"/>
    ${dots}
    <text class="rbrand" x="${W / 2}" y="${H - 38}">${esc(remote.name)}</text>
    ${callouts}
    ${keys}
  </svg>`;
}

// ------------------------------------------------------------------ rail

function renderRail() {
  const st = S.state;
  $("#railRemotes").innerHTML = `<div class="rail-label">Remotes</div>` + st.remotes.map((r) => `
    <button class="rcard${r.id === S.remote ? " on" : ""}" data-remote="${r.id}">
      <div class="rcard-top"><span class="rcard-name">${esc(r.name)}</span><span class="led${r.connected ? " on" : ""}" title="${r.connected ? "Connected" : "Not connected"}"></span></div>
      <div class="rcard-id">${r.vid}:${r.pid} \u00B7 ${r.connected ? "connected" : "not connected"}</div>
      <div class="rcard-profile"><span class="led${st.paused ? "" : r.profile === "stock" ? "" : " amber"}"></span><b>${esc(st.paused ? "Paused (stock)" : profileById(r.profile)?.name ?? r.profile)}</b></div>
    </button>`).join("");
  const kill = $("#kill");
  kill.classList.toggle("paused", st.paused);
  $("#killBox").checked = !st.paused;
  kill.querySelector("b").textContent = st.paused ? "All remotes stock" : "Profiles live";
  kill.querySelector("small").textContent = st.paused ? "Flip to turn profiles back on" : "Flip to make every remote stock";
}

$("#railRemotes").addEventListener("click", (e) => {
  const card = e.target.closest("[data-remote]");
  if (!card) return;
  S.remote = card.dataset.remote;
  S.editProfile = null;
  S.selected = null;
  showView("live");
  renderAll();
});

$("#killBox").addEventListener("change", (e) => {
  api("/api/pause", { paused: !e.target.checked }).then((st) => { S.state = st; renderAll(); }).catch(fail);
});

// ------------------------------------------------------------------ live view

function renderLive() {
  const r = currentRemote(), tpl = tplFor(r.id), prof = editingProfile();
  const editingOther = S.editProfile && S.editProfile !== r.profile;
  $("#viewLive").innerHTML = `
    <div class="live">
      <div class="stage-wrap">
        <div class="stage-head">
          <div>
            <div class="eyebrow">Remote \u00B7 ${r.vid}:${r.pid} \u00B7 ${r.connected ? "connected" : "not connected"}</div>
            <h1 class="title">${esc(r.name)}</h1>
          </div>
          <div class="profile-strip">
            ${S.state.profiles.map((p) => `<button class="pchip${p.id === r.profile ? " on" : ""}${p.id === prof.id ? " editing" : ""}" data-use="${p.id}" title="${esc(p.description)}">${esc(p.name)}</button>`).join("")}
          </div>
        </div>
        ${editingOther ? `<div class="edit-banner"><span>Editing <b>${esc(prof.name)}</b> \u2014 not active on the ${esc(r.name)}. Changes save as you go.</span>
          <span class="row"><button class="btn sm" data-use="${prof.id}">Use on ${esc(r.name)}</button><button class="btn sm ghost" id="stopEdit">Done</button></span></div>` : ""}
        <div class="stage" id="stage">${tpl ? remoteSvg(r, tpl, prof) : `<div class="empty"><b>No layout for this remote</b>Add remote-templates/${esc(r.id)}.json</div>`}</div>
        <div class="timeline"><span class="timeline-label">Signal</span><canvas id="tl"></canvas>
          <div class="timeline-empty" id="tlEmpty">Press any button on a remote to see its signal \u2014 held buttons draw long bars, taps draw ticks</div></div>
      </div>
      <aside class="inspector" id="insp"></aside>
    </div>`;
  renderInspector();
  drawTimeline();
}

$("#viewLive").addEventListener("click", (e) => {
  const use = e.target.closest("[data-use]");
  if (use) {
    api("/api/remote-profile", { remote: S.remote, profile: use.dataset.use })
      .then((st) => { S.state = st; S.editProfile = null; renderAll(); })
      .catch(fail);
    return;
  }
  if (e.target.closest("#stopEdit")) { S.editProfile = null; renderAll(); return; }
  const key = e.target.closest(".bkey, .callout");
  if (key && key.closest("#stage")) { select(key.dataset.b); return; }
  if (e.target.closest("#stage") && !e.target.closest(".bkey")) { select(null); }
});

function select(id) {
  S.selected = id;
  S.recording = false;
  S.typedKeys = false;
  document.querySelectorAll("#stage [data-b]").forEach((el) => el.classList.toggle("sel", el.dataset.b === id));
  renderInspector();
}

function renderInspector() {
  const el = $("#insp");
  if (!el) return;
  const r = currentRemote(), tpl = tplFor(r.id), prof = editingProfile();
  const readOnly = prof.id === "stock";

  if (!S.selected || !tpl) {
    const rows = Object.entries(prof.buttons).filter(([id]) => r.buttons.some((b) => b.id === id));
    el.innerHTML = `
      <div class="insp-head">
        <div class="insp-kicker">${prof.id === r.profile ? "Active profile" : "Editing profile"}</div>
        <div class="insp-name"><b>${esc(prof.name)}</b></div>
        <p class="sub" style="margin-top:10px">${esc(prof.description)}</p>
      </div>
      <div class="insp-body">
        ${rows.length ? `<div class="summary-list">${rows.map(([id, spec]) => `<button class="summary-row" data-pick="${id}"><span>${esc(buttonLabel(id, r.id))}</span><b>${esc(actionName(spec))}</b></button>`).join("")}</div>`
          : `<p class="empty-hint">${readOnly ? "Stock leaves every button exactly as the remote made it." : "Nothing mapped yet."}</p>`}
        <p class="empty-hint" style="margin-top:22px">Click a button on the remote \u2014 or simply <b>press it</b> \u2014 to choose what it does.
        ${readOnly ? "<br><br>Stock is read-only; picking an action creates your own copy." : ""}</p>
      </div>`;
    return;
  }

  const b = tpl.buttons.find((x) => x.id === S.selected);
  const caps = buttonCaps(r, S.selected);
  const spec = prof.buttons[S.selected] || { action: "passthrough" };
  const sends = caps.dead ? `<span class="chip red">Sends nothing to the PC</span>`
    : caps.sig.source === "consumer" ? `<code>Consumer ${esc(caps.sig.usage)}</code>`
    : `<code>Key ${esc(caps.sig.key)}</code>`;
  const capChip = caps.dead ? "" : caps.tapOnly ? `<span class="chip red">Tap only</span>`
    : caps.needsDriver ? `<span class="chip amber">Keyboard key</span>` : `<span class="chip green">True hold</span>`;

  let notes = "";
  if (caps.dead) notes += `<p class="insp-note warn">This button is handled inside the remote (or by infrared) and never reaches the computer, so it can't be mapped.</p>`;
  if (caps.tapOnly) notes += `<p class="insp-note">The Voice button sends one short pulse however long you hold it. Use a tap action here \u2014 like Flow hands-free.</p>`;
  if (caps.needsDriver) notes += `<p class="insp-note amber">This is an ordinary keyboard key. Its new action takes effect once the Interception driver is installed \u2014 until then it keeps working as ${esc(caps.sig.key)}. <button class="linkish" data-goto="settings">How to install</button></p>`;
  if (readOnly && !caps.dead) notes += `<p class="insp-note">Stock is read-only. Choosing an action creates your own profile and switches the ${esc(r.name)} to it.</p>`;

  const groups = caps.dead ? "" : `
    <div class="agroup"><div class="atiles"><button class="atile wide${spec.action === "passthrough" ? " on" : ""}" data-act="passthrough"><b>Default behaviour</b><small>Leave it as ${esc(caps.sig.source === "consumer" ? "the remote sends it" : caps.sig.key)}</small></button></div></div>
    ${ACTIONS.map((g) => `
      <div class="agroup"><h4>${g.group}</h4>
        <div class="atiles">${g.items.map((a) => `<button class="atile${spec.action === a.id ? " on" : ""}" data-act="${a.id}"><b>${esc(a.name)}</b><small>${esc(a.hint)}</small></button>`).join("")}</div>
      </div>
      ${g.items.some((a) => a.id === spec.action && a.param) ? paramEditor(spec) : ""}`).join("")}`;

  el.innerHTML = `
    <div class="insp-head">
      <div class="insp-kicker">${esc(r.name)} \u00B7 ${esc(prof.name)}</div>
      <div class="insp-name">
        <span class="insp-glyph${b.glyph ? "" : " t"}">${b.glyph ? glyphChar(b.glyph) : esc(b.text || b.label)}</span>
        <b>${esc(b.label)}</b>
      </div>
      <div class="insp-sends">${sends}${capChip}</div>
    </div>
    <div class="insp-body">${notes}${groups}</div>`;
}

function paramEditor(spec) {
  switch (spec.action) {
    case "keys":
      if (S.typedKeys) return `<div class="param"><label>Shortcut</label><input class="field mono" id="keysText" value="${esc(spec.keys || "")}" placeholder="ctrl+win+right"><button class="linkish" id="recordMode">Record it instead</button></div>`;
      return `<div class="param"><label>Shortcut</label>
        <div class="recorder${S.recording ? " rec" : ""}" id="recorder">${S.recording ? `<span class="hint">Press the shortcut now\u2026 (Esc cancels)</span>` : spec.keys ? keycaps(spec.keys) : `<span class="hint">Click, then press keys</span>`}</div>
        <div class="presets">${[["ctrl+win+right", "Desktop \u2192"], ["ctrl+win+left", "Desktop \u2190"], ["alt+tab", "Alt+Tab"], ["win+d", "Win+D"], ["ctrl+oemtilde", "Ctrl+`"], ["enter", "Enter"], ["ctrl+enter", "Ctrl+Enter"], ["esc", "Esc"], ["ctrl+v", "Ctrl+V"], ["ctrl+z", "Ctrl+Z"]]
          .map(([k, n]) => `<button data-preset="${k}">${esc(n)}</button>`).join("")}</div>
        <button class="linkish" id="typeMode">Type it instead (for Win-key combos)</button></div>`;
    case "text":
      return `<div class="param"><label>Text to type</label><textarea class="field" id="textParam" placeholder="e.g. Please review this and suggest improvements.">${esc(spec.text || "")}</textarea></div>`;
    case "run":
      return `<div class="param"><label>Program, file or URL</label><input class="field mono" id="runParam" value="${esc(spec.run || "")}" placeholder="C:\\Program Files\\...\\app.exe"></div>`;
    case "spot_goto": {
      const spots = S.state.spots;
      if (!spots.length) return `<div class="param"><label>Input spot</label><p class="empty-hint">No spots saved yet. <button class="linkish" data-goto="spots">Add input spots</button></p></div>`;
      return `<div class="param"><label>Input spot</label><select class="field" id="spotParam">${spots.map((s, i) => `<option value="${i + 1}"${(spec.spot | 0) === i + 1 ? " selected" : ""}>${i + 1}. ${esc(s.label)}</option>`).join("")}</select></div>`;
    }
  }
  return "";
}

$("#viewLive").addEventListener("click", (e) => {
  const pick = e.target.closest("[data-pick]");
  if (pick) { select(pick.dataset.pick); return; }
  const go = e.target.closest("[data-goto]");
  if (go) { showView(go.dataset.goto); return; }
  const act = e.target.closest("[data-act]");
  if (act) {
    const id = act.dataset.act;
    const cur = editingProfile().buttons[S.selected];
    if (cur && cur.action === id) return;
    if (id === "passthrough") return setAction(S.selected, null);
    const spec = { action: id };
    if (id === "keys") { spec.keys = ""; S.recording = true; }
    if (id === "spot_goto") spec.spot = 1;
    if (id === "text") spec.text = "";
    if (id === "run") spec.run = "";
    return setAction(S.selected, spec);
  }
  if (e.target.closest("#recorder")) { S.recording = true; renderInspector(); return; }
  if (e.target.closest("#typeMode")) { S.typedKeys = true; S.recording = false; renderInspector(); return; }
  if (e.target.closest("#recordMode")) { S.typedKeys = false; renderInspector(); return; }
  const preset = e.target.closest("[data-preset]");
  if (preset) { S.recording = false; setAction(S.selected, { action: "keys", keys: preset.dataset.preset }); }
});

$("#viewLive").addEventListener("change", (e) => {
  const sel = S.selected;
  if (e.target.id === "textParam") setAction(sel, { action: "text", text: e.target.value });
  if (e.target.id === "runParam") setAction(sel, { action: "run", run: e.target.value });
  if (e.target.id === "spotParam") setAction(sel, { action: "spot_goto", spot: +e.target.value });
  if (e.target.id === "keysText") setAction(sel, { action: "keys", keys: e.target.value.trim().toLowerCase() });
});

const CODE_KEYS = {
  Backquote: "oemtilde", Minus: "oemminus", Equal: "oemplus", BracketLeft: "oemopenbrackets", BracketRight: "oemclosebrackets",
  Semicolon: "oemsemicolon", Quote: "oemquotes", Comma: "oemcomma", Period: "oemperiod", Slash: "oemquestion", Backslash: "oempipe",
  ArrowLeft: "left", ArrowRight: "right", ArrowUp: "up", ArrowDown: "down", Enter: "enter", NumpadEnter: "enter", Escape: "esc",
  Space: "space", Tab: "tab", Backspace: "backspace", Delete: "delete", Insert: "insert", Home: "home", End: "end",
  PageUp: "pageup", PageDown: "pagedown",
};

document.addEventListener("keydown", (e) => {
  if (!S.recording) return;
  e.preventDefault();
  if (["ControlLeft", "ControlRight", "ShiftLeft", "ShiftRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight"].includes(e.code)) return;
  if (e.code === "Escape" && !e.ctrlKey && !e.altKey && !e.shiftKey && !e.metaKey) { S.recording = false; renderInspector(); return; }
  let key = CODE_KEYS[e.code];
  if (!key && /^Key[A-Z]$/.test(e.code)) key = e.code.slice(3).toLowerCase();
  if (!key && /^Digit\d$/.test(e.code)) key = e.code.slice(5);
  if (!key && /^F\d{1,2}$/.test(e.code)) key = e.code.toLowerCase();
  if (!key && /^Numpad\d$/.test(e.code)) key = "numpad" + e.code.slice(6);
  if (!key) return;
  const mods = [e.ctrlKey && "ctrl", e.metaKey && "win", e.altKey && "alt", e.shiftKey && "shift"].filter(Boolean);
  S.recording = false;
  setAction(S.selected, { action: "keys", keys: [...mods, key].join("+") });
});

// Writes one button's action into the edited profile (copying Stock first if needed).
async function setAction(btnId, spec) {
  try {
    const r = currentRemote();
    let prof = editingProfile();
    const buttons = { ...prof.buttons };
    if (spec) buttons[btnId] = spec; else delete buttons[btnId];
    if (spec && spec.action === "keys" && !spec.keys) { // wait for the recorder before saving
      prof.buttons = buttons;
      renderInspector();
      return;
    }
    if (prof.id === "stock") {
      const n = S.state.profiles.filter((p) => p.name.startsWith("My " + r.name)).length;
      const res = await api("/api/profile", { name: `My ${r.name} profile${n ? " " + (n + 1) : ""}`, description: `Custom mapping for the ${r.name}.`, buttons });
      S.state = await api("/api/remote-profile", { remote: r.id, profile: res.id });
      S.editProfile = null;
      toast(`Created \u201C${profileById(res.id).name}\u201D and switched the ${r.name} to it`);
    } else {
      const res = await api("/api/profile", { id: prof.id, name: prof.name, description: prof.description, buttons });
      S.state = res.state;
    }
    renderLive();
    renderRail();
  } catch (e) { fail(e); }
}

// ------------------------------------------------------------------ live presses + timeline

const litTimers = {};
function onPress(p) {
  const now = performance.now();
  const key = p.remote + "/" + p.button;
  if (p.edge === 1) {
    S.presses.push({ key, remote: p.remote, button: p.button, t0: now, t1: null, action: p.action });
    if (S.presses.length > 60) S.presses.shift();
  } else {
    const open = [...S.presses].reverse().find((x) => x.key === key && x.t1 === null);
    if (open) open.t1 = now;
  }

  const typing = document.activeElement && /INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName);
  if (p.edge === 1 && p.remote !== S.remote && S.view === "live" && !S.recording && !typing) {
    S.remote = p.remote; S.editProfile = null; S.selected = p.button;
    renderAll();
  }
  const card = document.querySelector(`.rcard[data-remote="${p.remote}"]`);
  if (card && p.edge === 1) { card.classList.remove("pulse"); void card.offsetWidth; card.classList.add("pulse"); }

  if (p.remote === S.remote) {
    const el = document.querySelector(`#stage .bkey[data-b="${p.button}"]`);
    if (el) {
      clearTimeout(litTimers[key]);
      if (p.edge === 1) el.classList.add("lit");
      else litTimers[key] = setTimeout(() => el.classList.remove("lit"), 140);
    }
    if (p.edge === 1 && S.view === "live" && !S.recording && !typing && S.selected !== p.button) select(p.button);
  }
  drawTimeline();
}

let tlRaf = null;
function drawTimeline() {
  if (tlRaf) return;
  tlRaf = requestAnimationFrame(() => { tlRaf = null; paintTimeline(); });
}

function paintTimeline() {
  const canvas = $("#tl");
  if (!canvas) return;
  const now = performance.now(), WINDOW = 12000;
  const recent = S.presses.filter((p) => (p.t1 ?? now) > now - WINDOW);
  $("#tlEmpty").style.display = recent.length ? "none" : "";
  const dpr = devicePixelRatio || 1, w = canvas.clientWidth, h = canvas.clientHeight;
  if (canvas.width !== w * dpr || canvas.height !== h * dpr) { canvas.width = w * dpr; canvas.height = h * dpr; }
  const g = canvas.getContext("2d");
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);

  const labelW = 150, top = 30, laneH = 17;
  const lanes = [];
  for (const p of [...recent].reverse()) if (!lanes.includes(p.key) && lanes.length < 4) lanes.push(p.key);
  const x = (t) => labelW + (w - labelW - 16) * (1 - (now - t) / WINDOW);

  g.strokeStyle = "rgba(255,255,255,.04)";
  g.lineWidth = 1;
  for (let s = 0; s <= 12; s++) {
    const xx = Math.round(x(now - s * 1000)) + 0.5;
    g.beginPath(); g.moveTo(xx, top - 6); g.lineTo(xx, h - 8); g.stroke();
  }
  g.font = "10px 'Cascadia Mono'";
  g.fillStyle = "#4d4b47";
  g.fillText("now", w - 40, 16);
  g.fillText("-12s", labelW, 16);

  lanes.forEach((key, i) => {
    const y = top + i * (laneH + 4);
    const [rid, bid] = key.split("/");
    g.font = "600 12px Bahnschrift";
    g.fillStyle = "#8a867e";
    g.fillText(`${remoteById(rid)?.name ?? rid} \u00B7 ${buttonLabel(bid, rid)}`, 14, y + 12);
    for (const p of recent.filter((q) => q.key === key)) {
      const t1 = p.t1 ?? now, dur = t1 - p.t0;
      const x0 = Math.max(labelW, x(p.t0)), x1 = x(t1);
      if (dur < 90 && p.t1 !== null) {
        g.fillStyle = "#ffb224";
        g.beginPath(); g.arc(x0, y + 8, 4, 0, Math.PI * 2); g.fill();
        continue;
      }
      const open = p.t1 === null;
      g.fillStyle = open ? "#ff4d3d" : "#ffb224";
      g.shadowColor = open ? "rgba(255,77,61,.8)" : "rgba(255,178,36,.4)";
      g.shadowBlur = open ? 14 : 6;
      g.fillRect(x0, y + 3, Math.max(3, x1 - x0), laneH - 6);
      g.shadowBlur = 0;
      if (dur > 400) {
        g.font = "10px 'Cascadia Mono'";
        g.fillStyle = open ? "#ffd2cc" : "#17130a";
        const label = (dur / 1000).toFixed(1) + "s";
        if (x1 - x0 > 34) g.fillText(label, x1 - 30, y + 12);
      }
    }
  });
  if (recent.length) drawTimeline();
}

// ------------------------------------------------------------------ profiles view

function renderProfiles() {
  const st = S.state;
  const usedOn = (pid) => st.remotes.filter((r) => r.profile === pid);
  $("#viewProfiles").innerHTML = `
    <div class="head">
      <div><div class="eyebrow">Profiles</div><h1 class="title">A mode for every job</h1>
      <p class="sub">Each remote runs one profile. Stock hands the remote back exactly as it came; everything else is yours to shape. Switch from here, the remote view, the tray, or Ctrl+Alt+Shift+F10.</p></div>
    </div>
    <div class="pgrid">
      ${st.profiles.map((p) => {
        const maps = Object.entries(p.buttons);
        const live = usedOn(p.id);
        const ro = p.id === "stock";
        return `<article class="pcard${live.length ? " live-on" : ""}" data-pid="${p.id}">
          <div class="row" style="justify-content:space-between;flex-wrap:nowrap">
            <input class="pcard-name" value="${esc(p.name)}" ${ro ? "readonly" : ""} data-field="name" spellcheck="false">
            ${ro ? `<span class="lock" title="Read-only">\uE72E</span>` : ""}
          </div>
          <textarea class="pcard-desc" data-field="description" ${ro ? "readonly" : ""} spellcheck="false">${esc(p.description)}</textarea>
          <div class="pcard-maps">
            ${maps.length ? maps.slice(0, 7).map(([id, spec]) => `<div><span>${esc(buttonLabel(id))}</span><b>${esc(actionName(spec))}</b></div>`).join("") : `<div><span>Every button</span><b>as the remote sends it</b></div>`}
            ${maps.length > 7 ? `<div><span></span><b>+${maps.length - 7} more</b></div>` : ""}
          </div>
          <div class="pcard-foot">
            ${st.remotes.map((r) => r.profile === p.id
              ? `<span class="chip amber"><span class="led amber"></span>Live on ${esc(r.name)}</span>`
              : `<button class="btn sm" data-apply="${r.id}">Use on ${esc(r.name)}</button>`).join("")}
            <span class="spacer"></span>
            ${ro ? "" : `<button class="btn sm ghost" data-edit title="Edit on the remote view">Edit</button>`}
            <button class="btn sm ghost" data-dup>Duplicate</button>
            ${ro ? "" : `<button class="btn sm ghost danger" data-del>Delete</button>`}
          </div>
        </article>`;
      }).join("")}
      <article class="pcard new" id="newProfile"><span class="plusmark">+</span><b>New profile</b><small>Starts empty \u2014 every button stock</small></article>
    </div>`;
}

$("#viewProfiles").addEventListener("click", async (e) => {
  const card = e.target.closest("[data-pid]");
  try {
    if (e.target.closest("#newProfile")) {
      const res = await api("/api/profile", { name: "New profile", description: "Describe what this mode is for.", buttons: {} });
      S.state = res.state;
      S.editProfile = res.id; S.selected = null;
      showView("live"); renderAll();
      toast("New profile created \u2014 click buttons on the remote to map them");
      return;
    }
    if (!card) return;
    const p = profileById(card.dataset.pid);
    const apply = e.target.closest("[data-apply]");
    if (apply) { S.state = await api("/api/remote-profile", { remote: apply.dataset.apply, profile: p.id }); renderAll(); return; }
    if (e.target.closest("[data-edit]")) { S.editProfile = p.id; S.selected = null; showView("live"); renderAll(); return; }
    if (e.target.closest("[data-dup]")) {
      const res = await api("/api/profile", { name: p.name + " copy", description: p.description, buttons: p.buttons });
      S.state = res.state; renderAll(); toast("Duplicated " + p.name); return;
    }
    if (e.target.closest("[data-del]")) {
      if (!confirmInline(e.target.closest("[data-del]"))) return;
      S.state = await api("/api/profile-delete", { id: p.id });
      if (S.editProfile === p.id) S.editProfile = null;
      renderAll(); toast("Deleted " + p.name + " (a backup is kept in data\\deleted-profiles)");
    }
  } catch (err) { fail(err); }
});

// Two-step delete without a modal: first click arms the button.
function confirmInline(btn) {
  if (btn.dataset.armed) return true;
  btn.dataset.armed = "1";
  btn.textContent = "Click again to delete";
  setTimeout(() => { if (btn.isConnected) { delete btn.dataset.armed; btn.textContent = "Delete"; } }, 3000);
  return false;
}

$("#viewProfiles").addEventListener("change", async (e) => {
  const card = e.target.closest("[data-pid]");
  if (!card || !e.target.dataset.field) return;
  const p = profileById(card.dataset.pid);
  const next = { ...p, [e.target.dataset.field]: e.target.value.trim() };
  try { S.state = (await api("/api/profile", { id: p.id, name: next.name, description: next.description, buttons: p.buttons })).state; renderRail(); toast("Saved"); }
  catch (err) { fail(err); }
});

// ------------------------------------------------------------------ input spots view

let openAdvanced = new Set();
function renderSpots() {
  const st = S.state;
  const bindings = [];
  for (const r of st.remotes) {
    const p = profileById(r.profile);
    for (const [id, spec] of Object.entries(p.buttons)) {
      if (!/^spot_/.test(spec.action) || !r.buttons.some((b) => b.id === id)) continue;
      bindings.push(`<div><b>${esc(r.name)} \u00B7 ${esc(buttonLabel(id, r.id))}</b> \u2192 ${esc(actionName(spec))}</div>`);
    }
  }
  $("#viewSpots").innerHTML = `
    <div class="head">
      <div><div class="eyebrow">Input spots</div><h1 class="title">Hop between inputs</h1>
      <p class="sub">Your dictation destinations, in order \u2014 a VS Code terminal, a chat box in the browser, anything with a text field. The remote jumps between them, focuses the box and you just talk.</p></div>
    </div>
    <div class="spots-layout">
      <div class="spot-list" id="spotList">
        ${st.spots.length ? st.spots.map((s, i) => `
          <div class="spot" draggable="true" data-i="${i}">
            <div class="spot-num" title="Drag to reorder">${i + 1}</div>
            <div>
              <input class="spot-label" value="${esc(s.label)}" data-f="label" spellcheck="false">
              <div class="spot-meta">
                <span class="chip">${s.method === "element" ? "Finds the input" : s.method === "click" ? "Clicks the spot" : "Window only"}</span>
                <code>${esc(s.process)}</code>${s.titleContains ? `<span>window contains \u201C${esc(s.titleContains)}\u201D</span>` : ""}
                ${s.afterKeys ? `<span>then ${keycaps(s.afterKeys)}</span>` : ""}
              </div>
            </div>
            <div class="spot-actions">
              <button class="btn sm primary" data-jump><span class="ic">\uE768</span>Jump</button>
              <button class="btn sm ghost" data-adv>${openAdvanced.has(i) ? "Less" : "Tune"}</button>
              <button class="btn sm ghost danger" data-del>Remove</button>
            </div>
            ${openAdvanced.has(i) ? `<div class="spot-adv">
              <div><label>Window title contains</label><input class="field" data-f="titleContains" value="${esc(s.titleContains)}" placeholder="any window of ${esc(s.process)}"></div>
              <div><label>How to focus</label><select class="field" data-f="method">
                <option value="element"${s.method === "element" ? " selected" : ""}>Find the input (UI Automation)</option>
                <option value="click"${s.method === "click" ? " selected" : ""}>Click the saved position</option>
                <option value="window"${s.method === "window" ? " selected" : ""}>Just bring the window forward</option></select></div>
              <div><label>Then press (optional)</label><input class="field mono" data-f="afterKeys" value="${esc(s.afterKeys)}" placeholder="e.g. ctrl+oemtilde"></div>
            </div>` : ""}
          </div>`).join("")
        : `<div class="empty"><b>No input spots yet</b>Capture the inputs you dictate into most \u2014 your terminal, your AI chat \u2014 and hop between them from the couch.</div>`}
      </div>
      <aside class="capture-card">
        <div class="eyebrow">Add a spot</div>
        <h3 style="margin-top:8px">Capture an input box</h3>
        <ol>
          <li>Press <b>Capture</b>.</li>
          <li>Within 4 seconds, click into the input you want \u2014 a terminal, a chat box, a search field.</li>
          <li>It's saved as spot ${st.spots.length + 1}.</li>
        </ol>
        <button class="btn primary" id="capture"><span class="ic">\uE710</span>Capture in 4 s</button>
        <div class="alt">Faster, from anywhere: click into an input and press <span class="keycap">Ctrl</span><span class="plus">+</span><span class="keycap">Alt</span><span class="plus">+</span><span class="keycap">Shift</span><span class="plus">+</span><span class="keycap">F9</span></div>
        <div class="bindings">${bindings.length ? `<div class="eyebrow" style="color:var(--muted);margin-bottom:4px">Remote buttons using spots</div>${bindings.join("")}`
          : `<div>No active profile uses spots yet. The <b>AI dictation workflow</b> profile maps Left/Right and 1\u20139.</div>`}</div>
      </aside>
    </div>`;
}

async function saveSpots(spots, msg) {
  try { S.state = await api("/api/spots", { spots }); renderSpots(); if (msg) toast(msg); }
  catch (e) { fail(e); }
}

$("#viewSpots").addEventListener("click", async (e) => {
  if (e.target.closest("#capture")) return captureSpot();
  const row = e.target.closest("[data-i]");
  if (!row) return;
  const i = +row.dataset.i;
  const spots = S.state.spots.map((s) => ({ ...s }));
  if (e.target.closest("[data-jump]")) { api("/api/spot-jump", { index: i }).catch(fail); return; }
  if (e.target.closest("[data-adv]")) { openAdvanced.has(i) ? openAdvanced.delete(i) : openAdvanced.add(i); renderSpots(); return; }
  if (e.target.closest("[data-del]")) {
    if (!confirmInline(e.target.closest("[data-del]"))) return;
    spots.splice(i, 1); openAdvanced.clear();
    saveSpots(spots, "Spot removed");
  }
});

$("#viewSpots").addEventListener("change", (e) => {
  const row = e.target.closest("[data-i]"), f = e.target.dataset.f;
  if (!row || !f) return;
  const spots = S.state.spots.map((s) => ({ ...s }));
  spots[+row.dataset.i][f] = e.target.value.trim();
  saveSpots(spots, "Saved");
});

let dragFrom = null;
$("#viewSpots").addEventListener("dragstart", (e) => { const row = e.target.closest("[data-i]"); if (row) { dragFrom = +row.dataset.i; e.dataTransfer.effectAllowed = "move"; } });
$("#viewSpots").addEventListener("dragover", (e) => {
  const row = e.target.closest("[data-i]");
  if (row && dragFrom !== null) { e.preventDefault(); document.querySelectorAll(".spot.drag-over").forEach((x) => x.classList.remove("drag-over")); row.classList.add("drag-over"); }
});
$("#viewSpots").addEventListener("drop", (e) => {
  const row = e.target.closest("[data-i]");
  if (!row || dragFrom === null) return;
  e.preventDefault();
  const to = +row.dataset.i, spots = S.state.spots.map((s) => ({ ...s }));
  const [moved] = spots.splice(dragFrom, 1);
  spots.splice(to, 0, moved);
  dragFrom = null; openAdvanced.clear();
  saveSpots(spots, "Order saved");
});
$("#viewSpots").addEventListener("dragend", () => { dragFrom = null; document.querySelectorAll(".spot.drag-over").forEach((x) => x.classList.remove("drag-over")); });

async function captureSpot() {
  const overlay = $("#overlay");
  const SECONDS = 4;
  overlay.hidden = false;
  overlay.innerHTML = `<div class="count"><div class="count-ring"><svg viewBox="0 0 200 200"><circle class="bg" cx="100" cy="100" r="90"/><circle class="fg" id="ring" cx="100" cy="100" r="90"/></svg><div class="count-num" id="countNum">${SECONDS}</div></div>
    <b>Click into the input box now</b><p>Switch to the app and click where you'd type. Airdeck grabs it when the ring closes.</p></div>`;
  const ring = $("#ring"), num = $("#countNum"), start = performance.now();
  const tick = () => {
    const t = (performance.now() - start) / 1000;
    ring.style.strokeDashoffset = String(565.5 * Math.min(1, t / SECONDS));
    num.textContent = Math.max(1, Math.ceil(SECONDS - t));
    if (t < SECONDS && !overlay.hidden) requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
  try {
    const spot = await api("/api/spot-capture", { delayMs: SECONDS * 1000 });
    S.state = await api("/api/state");
    // Clicking nowhere else captures this window; undo that.
    if (/Airdeck/.test(spot.titleContains || "") || /^Airdeck/.test(spot.label)) {
      const spots = S.state.spots.slice(0, -1);
      await saveSpots(spots);
      toast("That captured Airdeck itself \u2014 click into another app during the countdown", true);
    } else toast(`Saved spot ${S.state.spots.length}: ${spot.label}`);
  } catch (e) { fail(e); }
  overlay.hidden = true;
  renderAll();
}

function flashSpot(i) {
  document.querySelectorAll(".spot").forEach((el) => el.classList.toggle("jumped", +el.dataset.i === i));
}

// ------------------------------------------------------------------ settings view

function renderSettings() {
  const st = S.state, ic = st.interception, set = st.settings;
  const hot = [["F12", "Exit Airdeck \u2014 every remote goes back to stock"], ["F11", "Pause / resume all profiles"], ["F10", "Next profile for the remote you used last"], ["F9", "Save the focused input box as an input spot"]];
  $("#viewSettings").innerHTML = `
    <div class="head"><div><div class="eyebrow">Settings</div><h1 class="title">Under the hood</h1>
      <p class="sub">Everything Airdeck needs to reach every app, start with Windows and read every button.</p></div></div>
    <div class="sgrid">
      <section class="scard">
        <h3>Start with Windows <button class="switch${set.startWithWindows ? " on" : ""}" id="startup" aria-label="Start with Windows"></button></h3>
        <p>Launch quietly into the tray when you sign in, with the profiles you last used.</p>
      </section>
      <section class="scard">
        <h3>Administrator mode ${set.elevated ? `<span class="chip green">Running as admin</span>` : `<span class="chip">Standard</span>`}</h3>
        <p>Windows blocks keystrokes from reaching apps that run as administrator (an elevated terminal, for example) unless Airdeck runs as administrator too.</p>
        ${set.elevated ? "" : `<button class="btn" id="elevate"><span class="ic">\uE7EF</span>Restart as administrator</button>`}
      </section>
      <section class="scard">
        <h3>Keyboard-key driver ${ic.active ? `<span class="chip green">Active \u00B7 ${ic.filtered} remote interface${ic.filtered === 1 ? "" : "s"}</span>` : ic.installed ? `<span class="chip amber">Installed \u2014 re-plug receivers</span>` : `<span class="chip">Not installed</span>`}</h3>
        <p>Arrows, digits, Pg+/Pg-, DEL, Menu (and the G10S OK) reach Windows as ordinary keyboard keys. The open-source Interception driver lets Airdeck remap them on the remote only \u2014 your real keyboard is never filtered.</p>
        ${ic.active ? "" : `<ol class="steps"><li>Open the tools folder.</li><li>Right-click <code>install-interception.cmd</code> \u2192 <b>Run as administrator</b>.</li><li>Unplug and re-plug each remote's USB receiver (or reboot). Airdeck picks the driver up within seconds. Undo any time with <code>uninstall-interception.cmd</code>.</li></ol>
          <button class="btn" data-open="driver"><span class="ic">\uE838</span>Open tools folder</button>`}
      </section>
      <section class="scard">
        <h3>Wispr Flow</h3>
        <p>Read from Flow's own settings, so remote actions always match your shortcuts.</p>
        <div class="kv">
          <span>Push-to-talk</span><div>${keycaps(st.flow.ptt.replace(/LControlKey/g, "ctrl").replace(/LWin/g, "win").replace(/LMenu/g, "alt").replace(/Space/g, "space"))}</div>
          <span>Hands-free</span><div>${keycaps(st.flow.handsfree.replace(/LControlKey/g, "ctrl").replace(/LWin/g, "win").replace(/LMenu/g, "alt").replace(/Space/g, "space"))}</div>
          <span>Command mode</span><div>${keycaps(st.flow.command.replace(/LControlKey/g, "ctrl").replace(/LWin/g, "win").replace(/LMenu/g, "alt").replace(/Space/g, "space"))}</div>
        </div>
      </section>
      <section class="scard">
        <h3>Hotkeys</h3>
        <p>On your normal keyboard, from anywhere.</p>
        <div class="kv hot">${hot.map(([k, d]) => `<div>${keycaps("ctrl+alt+shift+" + k.toLowerCase())}</div><span style="font-family:var(--body)">${d}</span>`).join("")}</div>
      </section>
      <section class="scard">
        <h3>Files</h3>
        <p>Profiles are plain JSON in <code style="font-family:var(--mono)">profiles\\</code>; copy one to start a new mode.</p>
        <div class="row">
          <button class="btn" data-open="profiles"><span class="ic">\uE8B7</span>Profiles folder</button>
          <button class="btn" data-open="log"><span class="ic">\uE9F9</span>Open log</button>
          <button class="btn ghost" id="reload"><span class="ic">\uE72C</span>Reload</button>
        </div>
      </section>
    </div>`;
}

$("#viewSettings").addEventListener("click", async (e) => {
  try {
    if (e.target.closest("#startup")) { S.state = await api("/api/settings", { startWithWindows: !S.state.settings.startWithWindows }); renderSettings(); toast(S.state.settings.startWithWindows ? "Airdeck will start with Windows" : "Won't start with Windows"); }
    if (e.target.closest("#elevate")) { await api("/api/restart-admin", {}); toast("Approve the Windows prompt \u2014 Airdeck restarts as administrator"); }
    if (e.target.closest("#reload")) { S.state = await api("/api/reload", {}); renderAll(); }
    const open = e.target.closest("[data-open]");
    if (open) await api("/api/open", { what: open.dataset.open });
  } catch (err) { fail(err); }
});

// ------------------------------------------------------------------ shell

function showView(v) {
  S.view = v;
  S.recording = false;
  document.querySelectorAll("#nav button").forEach((b) => b.classList.toggle("on", b.dataset.view === v));
  document.querySelectorAll(".view").forEach((el) => el.classList.toggle("on", el.dataset.view === v));
  if (v === "live") drawTimeline();
}

$("#nav").addEventListener("click", (e) => {
  const b = e.target.closest("[data-view]");
  if (b) showView(b.dataset.view);
});

function renderAll() {
  if (!S.state) return;
  if (!remoteById(S.remote)) S.remote = (S.state.remotes.find((r) => r.connected) || S.state.remotes[0]).id;
  if (S.editProfile && !profileById(S.editProfile)) S.editProfile = null;
  renderRail();
  renderLive();
  renderProfiles();
  renderSpots();
  renderSettings();
}

async function boot() {
  try {
    S.state = await api("/api/state");
    renderAll();
    const hash = location.hash.slice(1);
    if (["live", "profiles", "spots", "settings"].includes(hash)) showView(hash);
    const pick = new URLSearchParams(location.search).get("select");
    if (pick) select(pick);
  } catch (e) {
    document.body.innerHTML = `<div class="empty" style="margin:80px auto;max-width:520px"><b>Airdeck isn't running</b>Start airdeck.exe and reload this window.</div>`;
    return;
  }
  if (location.search.includes("nosse")) return; // static render (screenshots)
  const events = new EventSource("/api/events");
  events.addEventListener("press", (e) => onPress(JSON.parse(e.data)));
  events.addEventListener("spot", (e) => flashSpot(JSON.parse(e.data).index));
  let pending = null;
  events.addEventListener("state", () => {
    clearTimeout(pending);
    pending = setTimeout(async () => {
      const typing = document.activeElement && /INPUT|TEXTAREA/.test(document.activeElement.tagName);
      if (typing || S.recording) return;
      S.state = await api("/api/state");
      renderAll();
    }, 120);
  });
}

window.addEventListener("resize", drawTimeline);
boot();

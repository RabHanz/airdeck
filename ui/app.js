// Airdeck UI. Talks to airdeck.exe over /api/* and listens to /api/events for live presses.
"use strict";

const S = {
  state: null,
  remote: null,        // remote id shown on the Remote view
  editProfile: null,   // profile being edited when it is not the one in effect
  selected: null,      // selected button id
  view: "live",
  presses: [],         // timeline entries
  recording: null,     // which shortcut field is recording: "tap" | "hold" | "double" | null
  typedKeys: false,    // shortcut entered as text instead
  test: { remote: null, results: {}, extra: {} },
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
  toastTimer = setTimeout(() => t.classList.remove("show"), 2800);
}
const fail = (e) => toast(e.message || String(e), true);

// ------------------------------------------------------------------ model helpers

const remoteById = (id) => S.state.remotes.find((r) => r.id === id);
const profileById = (id) => S.state.profiles.find((p) => p.id === id);
const tplFor = (id) => S.state.templates[id];
const currentRemote = () => remoteById(S.remote);
const editingProfile = () => profileById(S.editProfile || currentRemote().baseProfile);
const choosable = () => S.state.profiles.filter((p) => !p.hidden);
const variantsOf = (p) => Object.entries(p.apps || {}).reduce((acc, [app, vid]) => {
  (acc[vid] = acc[vid] || []).push(app);
  return acc;
}, {});
const parentOf = (p) => (p && p.extends ? profileById(p.extends) : null);

function buttonLabel(id, remoteId) {
  const order = remoteId ? [remoteId, ...Object.keys(S.state.templates)] : Object.keys(S.state.templates);
  for (const rid of order) {
    const b = tplFor(rid)?.buttons.find((x) => x.id === id);
    if (b) return b.label;
  }
  return id.replace(/_/g, " ");
}

const APP_NAMES = { code: "VS Code", cursor: "Cursor", chrome: "Chrome", msedge: "Edge", firefox: "Firefox", brave: "Brave", opera: "Opera", vivaldi: "Vivaldi", arc: "Arc", windowsterminal: "Terminal", explorer: "File Explorer", spotify: "Spotify", slack: "Slack", discord: "Discord", powerpnt: "PowerPoint", winword: "Word", excel: "Excel" };
const appName = (p) => APP_NAMES[(p || "").toLowerCase()] || p;

const ACTIONS = [
  { group: "Wispr Flow", items: [
    { id: "flow_ptt", name: "Push-to-talk", hint: "Hold to dictate, release to insert", hold: true },
    { id: "flow_handsfree", name: "Hands-free", hint: "Tap to start, tap again to stop" },
    { id: "flow_command", name: "Command mode", hint: "Hold and speak an instruction", hold: true },
    { id: "flow_cancel", name: "Cancel", hint: "Esc \u2014 discard the dictation" },
    { id: "flow_paste_last", name: "Paste last transcript", hint: "When the text didn't land" },
  ]},
  { group: "Input spots", items: [
    { id: "spot_next", name: "Next input spot", hint: "Hop to the next saved input" },
    { id: "spot_prev", name: "Previous spot", hint: "Hop back one input" },
    { id: "spot_goto", name: "Go to spot\u2026", hint: "Jump straight to one input", param: "spot" },
    { id: "spot_capture", name: "Save input as spot", hint: "Remember the box you're in" },
  ]},
  { group: "Windows & screens", items: [
    { id: "app_next", name: "Next app", hint: "Like Alt+Tab, forward" },
    { id: "app_prev", name: "Previous app", hint: "Like Alt+Tab, back" },
    { id: "desktop_next", name: "Next desktop", hint: "The desktop to the right" },
    { id: "desktop_prev", name: "Previous desktop", hint: "The desktop to the left" },
    { id: "screen_focus", name: "Go to screen\u2026", hint: "Pointer + focus jump there", param: "dir4" },
    { id: "window_to_screen", name: "Move window to\u2026", hint: "Send it to another screen", param: "dir5" },
  ]},
  { group: "Keyboard & mouse", items: [
    { id: "keys", name: "Shortcut\u2026", hint: "Any key combination", param: "keys", hold: true },
    { id: "text", name: "Type text\u2026", hint: "Insert a saved snippet", param: "text" },
    { id: "left_click", name: "Left click", hint: "Hold to drag", hold: true },
    { id: "right_click", name: "Right click", hint: "Context menu", hold: true },
  ]},
  { group: "Airdeck & system", items: [
    { id: "profile_next", name: "Next profile", hint: "Cycle your profiles" },
    { id: "run", name: "Open app / file\u2026", hint: "Launch anything", param: "run" },
    { id: "middle_click", name: "Middle click", hint: "Open in new tab", hold: true },
    { id: "block", name: "Disable button", hint: "Do nothing at all" },
  ]},
];
const ACTION_INFO = Object.fromEntries(ACTIONS.flatMap((g) => g.items.map((i) => [i.id, i])));
const DIRS = { left: "\u2190 Left", right: "Right \u2192", up: "\u2191 Up", down: "\u2193 Down", next: "Next screen" };

const KEY_NAMES = {
  ctrl: "Ctrl", control: "Ctrl", lctrl: "Ctrl", rctrl: "RCtrl", shift: "Shift", lshift: "Shift", rshift: "RShift",
  alt: "Alt", lalt: "Alt", ralt: "AltGr", win: "Win", lwin: "Win", rwin: "Win", esc: "Esc", escape: "Esc",
  enter: "Enter", return: "Enter", right: "\u2192", left: "\u2190", up: "\u2191", down: "\u2193", oemtilde: "`",
  space: "Space", tab: "Tab", back: "Backspace", backspace: "Backspace", delete: "Del", del: "Del",
  pageup: "PgUp", pagedown: "PgDn", next: "PgDn", oemminus: "-", oemplus: "=", oemcomma: ",", oemperiod: ".",
  oemquestion: "/", oemsemicolon: ";", oemquotes: "'", oemopenbrackets: "[", oemclosebrackets: "]", oempipe: "\\",
};
const prettyKey = (k) => KEY_NAMES[k.toLowerCase()] ?? (k.length === 1 ? k.toUpperCase() : k[0].toUpperCase() + k.slice(1));
const prettyChord = (spec) => (spec || "").split("+").filter(Boolean).map(prettyKey).join("+");
const keycaps = (spec) => (spec || "").split("+").filter(Boolean).map((k) => `<span class="keycap">${esc(prettyKey(k))}</span>`).join('<span class="plus">+</span>');

function actionName(spec, short = false) {
  if (!spec || spec.action === "passthrough") return short ? "normal key" : "Its normal key";
  switch (spec.action) {
    case "flow_ptt": return short ? "Flow talk" : "Flow push-to-talk";
    case "flow_handsfree": return short ? "hands-free" : "Flow hands-free";
    case "flow_command": return short ? "Flow command" : "Flow command mode";
    case "flow_cancel": return short ? "cancel" : "Cancel dictation";
    case "flow_paste_last": return short ? "re-paste" : "Paste last transcript";
    case "spot_goto": {
      const spot = S.state.spots[(spec.spot | 0) - 1];
      return short ? `spot ${spec.spot}` : spot ? `Spot ${spec.spot} \u00B7 ${spot.label}` : `Input spot ${spec.spot}`;
    }
    case "spot_capture": return short ? "save spot" : "Save input as spot";
    case "screen_focus": return short ? `screen ${spec.dir}` : `Go to screen ${DIRS[spec.dir] || spec.dir}`;
    case "window_to_screen": return short ? "move window" : spec.dir === "next" ? "Move window to next screen" : `Move window ${DIRS[spec.dir] || spec.dir}`;
    case "keys": return prettyChord(spec.keys) || "Shortcut";
    case "text": return `Type \u201C${(spec.text || "").slice(0, 18)}${(spec.text || "").length > 18 ? "\u2026" : ""}\u201D`;
    case "run": return "Open " + ((spec.run || "").split(/[\\/]/).pop() || "app");
    default: return short ? (ACTION_INFO[spec.action]?.name || spec.action).toLowerCase() : ACTION_INFO[spec.action]?.name.replace("\u2026", "") ?? spec.action;
  }
}

function buttonCaps(remote, btnId) {
  const sig = remote.buttons.find((b) => b.id === btnId);
  if (!sig || sig.source === "none") return { dead: true };
  const ic = S.state.interception;
  const tapOnly = btnId === "mic";
  const needsDriver = sig.source === "keyboard" && !ic.active;
  const needsLink = sig.source === "keyboard" && ic.active && !(ic.linked || []).includes(remote.id);
  const osReads = sig.source === "consumer" && ["0x00E9", "0x00EA", "0x00E2", "0x00B5", "0x00B6", "0x00CD", "0x00B7"].includes(sig.usage);
  return { sig, tapOnly, needsDriver, needsLink, osReads };
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

// Where a leader line leaves the button: its outer edge on the side the label sits.
function edgePoint(b, side, tpl) {
  const [gx, gy] = glyphCenter(b);
  const s = side === "L" ? -1 : 1;
  if (b.shape === "circle") {
    // OK sits inside the d-pad ring: leave from the ring's outer edge so the line never crosses keys.
    const ring = tpl.buttons.find((o) => DPAD[o.shape] !== undefined && Math.hypot(o.x - b.x, o.y - b.y) < 2);
    return ring ? [b.x + s * ring.r, b.y] : [b.x + s * b.r, b.y];
  }
  if (b.shape === "rrect") return [b.x + s * b.w / 2, b.y];
  if (DPAD[b.shape] !== undefined || b.shape === "band") {
    const dy = gy - b.y;
    return [b.x + s * Math.sqrt(Math.max(0, b.r * b.r - dy * dy)), gy];
  }
  return [gx, gy];
}

// Ring-shaped keys (d-pad segments, Back / Home arcs) can start their line anywhere along their
// outer edge: pick the point level with the label so the line stays perfectly horizontal.
function ringStart(b, side, ly) {
  let a0, a1;
  if (DPAD[b.shape] !== undefined) { a0 = DPAD[b.shape] + 8; a1 = DPAD[b.shape] + 82; }
  else if (b.shape === "band") { a0 = b.a0 + 6; a1 = b.a1 - 6; }
  else return null;
  const k = (ly - b.y) / b.r;
  if (Math.abs(k) > 0.98) return null;
  const phi = Math.asin(k) * 180 / Math.PI;
  const theta = side === "L" ? 180 - phi : (phi + 360) % 360;
  const inRange = [theta, theta + 360, theta - 360].some((t) => t >= a0 && t <= a1);
  return inRange ? pt(b.x, b.y, b.r, theta) : null;
}

function inRingKey(o, x, y) {
  let a0, a1;
  if (DPAD[o.shape] !== undefined) { a0 = DPAD[o.shape] + 2; a1 = DPAD[o.shape] + 88; }
  else if (o.shape === "band") { a0 = o.a0; a1 = o.a1; }
  else return false;
  const d = Math.hypot(x - o.x, y - o.y);
  if (d < o.r2 || d > o.r) return false;
  const t = (Math.atan2(y - o.y, x - o.x) * 180 / Math.PI + 360) % 360;
  return [t, t + 360].some((v) => v >= a0 && v <= a1);
}

// True when a level line from (x, y) out to the remote's edge would run over another ring key.
function levelCrosses(tpl, self, x, y, side) {
  const end = side === "L" ? 0 : tpl.width;
  for (let px = x + (side === "L" ? -3 : 3); side === "L" ? px > end : px < end; px += side === "L" ? -3 : 3)
    if (tpl.buttons.some((o) => o !== self && inRingKey(o, px, y))) return true;
  return false;
}

// Axis-aligned box of a key (plus its caption), used to see whether a straight line would cross it.
function keyBox(b) {
  if (b.shape === "rrect") return { x0: b.x - b.w / 2, x1: b.x + b.w / 2, y0: b.y - b.h / 2, y1: b.y + b.h / 2 };
  if (b.shape === "circle") return { x0: b.x - b.r, x1: b.x + b.r, y0: b.y - b.r, y1: b.y + b.r + (b.glyph ? 14 : 0) };
  return null; // ring parts: a line leaving the ring's outer edge never crosses them
}

// A key boxed in on that side (OK inside the ring, Down under the Back / Home arcs, Voice between
// the volume keys, 0 between DEL and Menu) gets no line: its label carries a copy of the key instead.
function lineBlocked(b, side, tpl, ex, ey) {
  if (b.shape === "circle" && tpl.buttons.some((o) => DPAD[o.shape] !== undefined && Math.hypot(o.x - b.x, o.y - b.y) < 2)) return true;
  if (levelCrosses(tpl, b, ex, ey, side)) return true; // e.g. Down: any level line runs over Back or Home
  const pad = 4;
  return tpl.buttons.some((o) => {
    if (o === b) return false;
    const k = keyBox(o);
    if (!k || ey < k.y0 - pad || ey > k.y1 + pad) return false;
    return side === "L" ? k.x1 <= ex + 1 : k.x0 >= ex - 1;
  });
}

// Small copy of the key itself, shown next to its label.
function keyChip(b) {
  return b.glyph ? { glyph: glyphChar(b.glyph), w: 32 } : (() => { const t = b.text || b.label; return { text: t, w: Math.max(32, t.length * 9.4 + 16) }; })();
}

function chipSvg(chip, x, y) {
  const inner = chip.glyph ? `<text class="chip-g" x="${x + chip.w / 2}" y="${y + 1}">${chip.glyph}</text>`
    : `<text class="chip-t" x="${x + chip.w / 2}" y="${y + 1}">${esc(chip.text)}</text>`;
  return `<rect class="chip-r" x="${x}" y="${y - 14}" width="${chip.w}" height="28" rx="7"/>${inner}`;
}

function gestureNote(spec) {
  const parts = [];
  if (spec.hold) parts.push(`hold \u2192 ${actionName(spec.hold, true)}`);
  if (spec.double) parts.push(`2\u00D7 \u2192 ${actionName(spec.double, true)}`);
  return parts.join("  \u00B7  ");
}

function remoteSvg(remote, tpl, profile) {
  const W = tpl.width, H = tpl.height, G = 330;
  const specOf = (id) => profile.buttons[id];
  const mapped = (id) => { const a = specOf(id); return a && (a.action !== "passthrough" || a.hold || a.double); };

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

  // Number pad: when 1-9 all do "the same thing for n", one bracket stands for all nine.
  // Otherwise the digits are listed in order (1-5 on the left, 6-0 on the right) with a copy of
  // each key, instead of nine lines fanning out from a grid.
  const digits = [1, 2, 3, 4, 5, 6, 7, 8, 9].map((n) => tpl.buttons.find((b) => b.id === `num_${n}`));
  let group = null;
  if (digits.every(Boolean)) {
    const specs = digits.map((b) => specOf(b.id));
    const plain = (s) => s && !s.hold && !s.double;
    const spotRun = specs.every((s, i) => plain(s) && s.action === "spot_goto" && (s.spot | 0) === i + 1);
    const ctrlRun = specs.every((s, i) => plain(s) && s.action === "keys" && s.keys === `ctrl+${i + 1}`);
    if (spotRun || ctrlRun) group = { ids: digits.map((b) => b.id), text: spotRun ? "Jump to input spot 1–9" : "Jump to tab 1–9" };
  }

  const items = tpl.buttons
    .filter((b) => mapped(b.id) && !buttonCaps(remote, b.id).dead && !(group && group.ids.includes(b.id)))
    .map((b) => ({ b, x: glyphCenter(b)[0], y: glyphCenter(b)[1], spec: specOf(b.id) }));

  if (group) {
    const bs = group.ids.map((id) => tpl.buttons.find((b) => b.id === id));
    const top = Math.min(...bs.map((b) => b.y - b.h / 2)), bottom = Math.max(...bs.map((b) => b.y + b.h / 2));
    const left = Math.min(...bs.map((b) => b.x - b.w / 2));
    items.push({ group, b: { id: "num_1", text: "1–9" }, x: left, y: (top + bottom) / 2, top, bottom, side: "L", spec: specOf("num_1") });
  }

  // Digits listed in order when they don't form one run.
  const listed = ["num_1", "num_2", "num_3", "num_4", "num_5", "num_6", "num_7", "num_8", "num_9", "num_0"]
    .map((id) => items.find((it) => it.b.id === id)).filter(Boolean);
  if (!group && listed.length > 1) {
    // Centred on the 1-9 grid so the keys around it (DEL, Menu) keep straight lines.
    const grid = listed.filter((it) => it.b.id !== "num_0").map((it) => it.y);
    const half = Math.ceil(listed.length / 2), step = 38;
    const start = (Math.min(...grid) + Math.max(...grid)) / 2 - ((half - 1) * step) / 2 - 4;
    listed.forEach((it, i) => {
      it.list = true;
      it.side = i < half ? "L" : "R";
      it.y = start + (i < half ? i : i - half) * step;
    });
  }

  // Sides: left half -> left column, right half -> right column; a centre key takes the side that
  // is free on its row, else the one with fewer labels near its height (alternating on a tie).
  const rowKey = (y) => Math.round(y / 16);
  for (const it of items) if (!it.side) it.side = it.x < W / 2 - 14 ? "L" : it.x > W / 2 + 14 ? "R" : null;
  // Keys with a line choose first; keys that get no line (boxed in on both sides) can sit
  // anywhere, so they fill in last where there is room.
  const free = (it) => !(lineBlocked(it.b, "L", tpl, ...edgePoint(it.b, "L", tpl)) && lineBlocked(it.b, "R", tpl, ...edgePoint(it.b, "R", tpl)));
  let lastTie = "R";
  for (const it of items.filter((i) => !i.side).sort((a, b) => (free(b) - free(a)) || a.y - b.y)) {
    const row = items.filter((o) => o !== it && o.side && !o.list && rowKey(o.y) === rowKey(it.y));
    const l = row.filter((o) => o.side === "L").length, r = row.filter((o) => o.side === "R").length;
    if (l !== r) { it.side = l < r ? "L" : "R"; continue; }
    const near = (s) => items.filter((o) => o !== it && o.side === s && Math.abs(o.y - it.y) <= 70).length;
    const total = (s) => items.filter((o) => o !== it && o.side === s).length;
    const nl = near("L"), nr = near("R"), tl = total("L"), tr = total("R");
    if (nl !== nr) it.side = nl < nr ? "L" : "R";
    else if (!free(it) && tl !== tr) it.side = tl < tr ? "L" : "R"; // line-less keys even out the columns
    else it.side = lastTie = lastTie === "L" ? "R" : "L";
  }

  const callouts = [];
  for (const side of ["L", "R"]) {
    const list = items.filter((i) => i.side === side);
    for (const it of list) {
      it.twoLine = !!(gestureNote(it.spec) || calloutWarning(remote, it.b.id, it.spec));
      it.below = it.twoLine ? 30 : 14;
      if (!it.group && !it.list) it.y = edgePoint(it.b, side, tpl)[1];
    }
    // A listed digit block stays together: other labels that fall inside it move below it.
    const block = list.filter((i) => i.list);
    if (block.length) {
      const b0 = Math.min(...block.map((i) => i.y)), b1 = Math.max(...block.map((i) => i.y));
      for (const it of list) if (!it.list && it.y >= b0 - 20 && it.y <= b1) it.y = b1 + 1;
    }
    // Labels with a line are stacked first, as level with their key as they can be (each label
    // spans 14 above its line to 14 or 30 below). Labels without a line then take the nearest free
    // gap, so they never push a lined label out of level.
    const GAP = 10;
    const lineless = list.filter((it) => !it.group && !it.list && lineBlocked(it.b, side, tpl, ...edgePoint(it.b, side, tpl)));
    const fixed = list.filter((it) => !lineless.includes(it)).sort((a, b) => a.y - b.y);
    let prev = null;
    for (const it of fixed) { it.ly = prev ? Math.max(it.y, prev.ly + prev.below + 14 + GAP) : it.y; prev = it; }
    let next = null;
    for (const it of [...fixed].reverse()) { it.ly = next ? Math.min(it.ly, next.ly - it.below - 14 - GAP) : Math.min(it.ly, H - 24 - it.below); next = it; }
    const placed = [...fixed];
    for (const f of lineless.sort((a, b) => a.y - b.y)) {
      const fits = (y) => y - 14 >= -8 && y + f.below <= H - 8 &&
        placed.every((o) => (y >= o.ly ? y - o.ly >= o.below + 14 + GAP : o.ly - y >= f.below + 14 + GAP));
      let at = null;
      for (let d = 0; d <= 260 && at === null; d += 2) at = fits(f.y + d) ? f.y + d : fits(f.y - d) ? f.y - d : null;
      f.ly = at !== null ? at : Math.max(...placed.map((o) => o.ly + o.below)) + 14 + GAP;
      placed.push(f);
    }
    list.sort((a, b) => a.ly - b.ly);
    list.forEach((it, n) => callouts.push(calloutSvg(remote, tpl, it, side, n, W)));
  }

  const dots = (tpl.dots || []).map((d) => `<circle class="rdot" cx="${d.x}" cy="${d.y}" r="3"/>`).join("");
  return `<svg viewBox="${-G} -16 ${W + 2 * G} ${H + 32}" preserveAspectRatio="xMidYMid meet">
    <defs>
      <linearGradient id="bodyGrad" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2b2c31"/><stop offset="1" stop-color="#141518"/></linearGradient>
      <radialGradient id="sheen" cx=".25" cy=".06" r=".75"><stop offset="0" stop-color="#fff" stop-opacity=".08"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></radialGradient>
      <pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="6" height="6" fill="#18191c"/><line x1="0" y1="0" x2="0" y2="6" stroke="#26272b" stroke-width="2"/></pattern>
      <filter id="glow" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="5" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
      <filter id="drop" x="-20%" y="-10%" width="140%" height="130%"><feDropShadow dx="0" dy="22" stdDeviation="22" flood-color="#000" flood-opacity=".6"/></filter>
    </defs>
    <path class="rbody" filter="url(#drop)" d="${roundRectPath(0, 0, W, H, tpl.topCorner, tpl.bottomCorner)}"/>
    <path class="rsheen" d="${roundRectPath(0, 0, W, H, tpl.topCorner, tpl.bottomCorner)}"/>
    ${dots}
    <text class="rbrand" x="${W / 2}" y="${H - 38}">${esc(remote.name)}</text>
    ${callouts.join("")}
    ${keys}
  </svg>`;
}

function calloutWarning(remote, id, spec) {
  const caps = buttonCaps(remote, id);
  const info = ACTION_INFO[spec.action] || {};
  if (caps.needsDriver) return "needs driver";
  if (caps.needsLink) return S.state.interception.learning ? "restart windows" : "not identified";
  if (caps.tapOnly && info.hold) return "tap-only button";
  if (caps.osReads) return "windows also reacts";
  return "";
}

// One label: a copy of the key, what a tap does, and (smaller) its hold / double-tap or a warning.
// Lines are always one straight stroke from the key's outer edge to the label; keys boxed in by
// their neighbours get no line and are recognised by the key copy instead.
function calloutSvg(remote, tpl, it, side, n, W) {
  const L = side === "L", spec = it.spec;
  const chip = it.group ? { text: "1–9", w: 46 } : keyChip(it.b);
  // Text sits in a fixed column so every title lines up; the key copy sits against the text and
  // the line runs from the key to the copy.
  const tx = L ? -74 : W + 74;
  const chipX = L ? tx + 10 : tx - 10 - chip.w;
  const anchor = L ? "end" : "start";
  const ly = it.ly, lineEnd = L ? chipX + chip.w + 5 : chipX - 5;

  let line = "";
  if (it.group) {
    const bx = it.x - 12;
    const d = `M${bx + 6},${it.top} H${bx} V${it.bottom} H${bx + 6} M${bx},${it.y} L${lineEnd},${ly}`;
    line = `<path class="halo" d="${d}"/><path d="${d}"/>`;
  } else if (!it.list) {
    let [ex, ey] = edgePoint(it.b, side, tpl);
    if (!lineBlocked(it.b, side, tpl, ex, ey)) {
      const level = Math.abs(ly - ey) > 1 && ringStart(it.b, side, ly);
      if (level && !levelCrosses(tpl, it.b, level[0], level[1], side)) [ex, ey] = level;
      const d = `M${ex},${ey} L${lineEnd},${ly}`;
      line = `<path class="halo" d="${d}"/><path d="${d}"/><circle cx="${ex}" cy="${ey}" r="3"/>`;
    }
  }

  const warn = calloutWarning(remote, it.b.id, spec);
  const gest = gestureNote(spec);
  const title = it.group ? it.group.text : spec.action === "passthrough" ? "Normal key" : actionName(spec);
  const clip = (s, k) => (s.length > k ? s.slice(0, k - 1) + "…" : s);
  const sub = warn || gest;
  const delay = (0.2 + n * 0.05).toFixed(2);
  return `<g class="callout${S.selected === it.b.id || (it.group && it.group.ids.includes(S.selected)) ? " sel" : ""}" data-b="${it.b.id}">
    ${line}
    <g class="chip">${chipSvg(chip, chipX, ly)}</g>
    <text class="ca" x="${tx}" y="${ly + 6}" text-anchor="${anchor}" style="animation-delay:${delay}s">${esc(clip(title, 30))}</text>
    ${sub ? `<text class="cb${warn ? " warn" : " gest"}" x="${tx}" y="${ly + 25}" text-anchor="${anchor}" style="animation-delay:${delay}s">${esc(clip(sub, 46))}</text>` : ""}
  </g>`;
}

// ------------------------------------------------------------------ rail

function renderRail() {
  const st = S.state;
  $("#railRemotes").innerHTML = `<div class="rail-label">Remotes</div>` + st.remotes.map((r) => {
    const base = profileById(r.baseProfile), eff = profileById(r.profile);
    return `<button class="rcard${r.id === S.remote ? " on" : ""}" data-remote="${r.id}">
      <div class="rcard-top"><span class="rcard-name">${esc(r.name)}</span><span class="led${r.connected ? " on" : ""}" title="${r.connected ? "Connected" : "Not connected"}"></span></div>
      <div class="rcard-id">${r.vid}:${r.pid} \u00B7 ${r.connected ? "connected" : "not connected"}</div>
      <div class="rcard-profile"><span class="led${st.paused || r.profile === "stock" ? "" : " amber"}"></span><b>${esc(st.paused ? "Paused (stock)" : base?.name ?? r.profile)}</b></div>
      ${!st.paused && r.autoApp && r.connected ? `<div class="rcard-auto">for ${esc(appName(r.autoApp))}: ${esc((eff?.name || "").split("\u00B7").pop().trim())} variant</div>` : ""}
    </button>`;
  }).join("");
  const kill = $("#kill");
  kill.classList.toggle("paused", st.paused);
  $("#killBox").checked = !st.paused;
  kill.querySelector("b").textContent = st.paused ? "All remotes stock" : "Profiles live";
  kill.querySelector("small").textContent = st.paused ? "Flip to turn profiles back on" : "Flip to make every remote stock";
  $("#testBadge").hidden = !st.testMode;
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
  const base = profileById(r.baseProfile);
  const effective = profileById(r.profile);
  const isVariantLive = r.autoApp && effective && effective.hidden;
  const editingOther = S.editProfile && S.editProfile !== r.baseProfile;
  const variantBanner = prof.hidden
    ? `<div class="edit-banner"><span><span class="chip amber">Variant</span> Editing <b>${esc(prof.name)}</b> \u2014 it takes over from <b>${esc(parentOf(prof)?.name || "its parent")}</b> while ${esc((Object.entries(parentOf(prof)?.apps || {}).filter(([, v]) => v === prof.id).map(([a]) => appName(a)).join(", ")) || "its app")} is in front. Only differences are saved.</span>
        ${editingOther ? `<span class="row"><button class="btn sm ghost" id="stopEdit">Done</button></span>` : ""}</div>`
    : editingOther ? `<div class="edit-banner"><span>Editing <b>${esc(prof.name)}</b> \u2014 not active on the ${esc(r.name)}. Changes save as you go.</span>
          <span class="row"><button class="btn sm" data-use="${prof.id}">Use on ${esc(r.name)}</button><button class="btn sm ghost" id="stopEdit">Done</button></span></div>` : "";

  $("#viewLive").innerHTML = `
    <div class="live">
      <div class="stage-wrap">
        <div class="stage-head">
          <div>
            <div class="eyebrow">Remote \u00B7 ${r.vid}:${r.pid} \u00B7 ${r.connected ? "connected" : "not connected"}</div>
            <h1 class="title">${esc(r.name)}</h1>
            <div class="stage-sub">${isVariantLive ? `<button class="chip amber" data-editvariant="${effective.id}" title="Edit this variant"><span class="led amber"></span>${esc(appName(r.autoApp))} in front \u2192 ${esc(effective.name)} is active \u00B7 edit</button>` : `<span class="chip">${esc(base?.name || "")}</span>`}</div>
          </div>
          <div class="profile-strip">
            ${choosable().map((p) => `<button class="pchip${p.id === r.baseProfile ? " on" : ""}${p.id === prof.id ? " editing" : ""}" data-use="${p.id}" title="${esc(p.description)}">${esc(p.name)}</button>`).join("")}
          </div>
        </div>
        ${variantBanner}
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
  if (e.target.closest("#stage") && !e.target.closest(".bkey")) { select(null); return; }

  const pick = e.target.closest("[data-pick]");
  if (pick) { select(pick.dataset.pick); return; }
  const go = e.target.closest("[data-goto]");
  if (go) { showView(go.dataset.goto); return; }
  const act = e.target.closest("[data-act]");
  if (act) { chooseTap(act.dataset.act); return; }
  const rec = e.target.closest("[data-record]");
  if (rec) { S.recording = rec.dataset.record; renderInspector(); return; }
  if (e.target.closest("#typeMode")) { S.typedKeys = true; S.recording = null; renderInspector(); return; }
  if (e.target.closest("#recordMode")) { S.typedKeys = false; renderInspector(); return; }
  const preset = e.target.closest("[data-preset]");
  if (preset) { S.recording = null; updateSpec(preset.dataset.slot, { action: "keys", keys: preset.dataset.preset }); }
});

function select(id) {
  S.selected = id;
  S.recording = null;
  S.typedKeys = false;
  document.querySelectorAll("#stage [data-b]").forEach((el) => el.classList.toggle("sel", el.dataset.b === id));
  renderInspector();
}

// The spec of the selected button, split into its three slots.
function slots() {
  const spec = editingProfile().buttons[S.selected] || { action: "passthrough" };
  return {
    tap: { action: spec.action, ...pick(spec, ["keys", "text", "run", "spot", "dir"]) },
    hold: spec.hold || null,
    double: spec.double || null,
  };
}
const pick = (o, keys) => Object.fromEntries(keys.filter((k) => o[k] !== undefined).map((k) => [k, o[k]]));

function composeSpec(s) {
  const spec = { ...s.tap };
  if (s.hold) spec.hold = s.hold;
  if (s.double) spec.double = s.double;
  const empty = spec.action === "passthrough" && !spec.hold && !spec.double;
  return empty ? null : spec;
}

function defaultsFor(id) {
  const spec = { action: id };
  if (id === "keys") spec.keys = "";
  if (id === "spot_goto") spec.spot = 1;
  if (id === "text") spec.text = "";
  if (id === "run") spec.run = "";
  if (id === "screen_focus") spec.dir = "right";
  if (id === "window_to_screen") spec.dir = "next";
  return spec;
}

function chooseTap(id) {
  const s = slots();
  if (s.tap.action === id) return;
  if (id === "keys") S.recording = "tap";
  updateSpec("tap", defaultsFor(id));
}

// Write one slot (tap / hold / double) of the selected button, then save the profile.
function updateSpec(slot, value) {
  const s = slots();
  if (slot === "tap") s.tap = value || { action: "passthrough" };
  else s[slot] = value && value.action !== "none" ? value : null;
  const waiting = [s.tap, s.hold, s.double].some((x) => x && x.action === "keys" && !x.keys);
  setAction(S.selected, composeSpec(s), waiting);
}

function renderInspector() {
  const el = $("#insp");
  if (!el) return;
  const r = currentRemote(), tpl = tplFor(r.id), prof = editingProfile();
  const readOnly = prof.id === "stock";

  if (!S.selected || !tpl) {
    // In the order the keys sit on the remote, top to bottom.
    const order = (tpl ? tpl.buttons : r.buttons).map((b) => b.id);
    let rows = Object.entries(prof.buttons).filter(([id]) => r.buttons.some((b) => b.id === id))
      .sort(([a], [b]) => (order.indexOf(a) + 1 || 999) - (order.indexOf(b) + 1 || 999));
    // 1-9 doing "the same thing for n" read as one row, like the bracket on the drawing.
    const digitSpecs = [1, 2, 3, 4, 5, 6, 7, 8, 9].map((n) => prof.buttons[`num_${n}`]);
    const spotRun = digitSpecs.every((s, i) => s && s.action === "spot_goto" && (s.spot | 0) === i + 1 && !s.hold && !s.double);
    const ctrlRun = digitSpecs.every((s, i) => s && s.action === "keys" && s.keys === `ctrl+${i + 1}` && !s.hold && !s.double);
    if (spotRun || ctrlRun) {
      const at = rows.findIndex(([id]) => /^num_[1-9]$/.test(id));
      rows = rows.filter(([id]) => !/^num_[1-9]$/.test(id));
      rows.splice(at, 0, ["num_1", { action: "passthrough", label: spotRun ? "Jump to input spot 1–9" : "Jump to tab 1–9", rowLabel: "1 – 9" }]);
    }
    const vmap = variantsOf(prof);
    el.innerHTML = `
      <div class="insp-head">
        <div class="insp-kicker">${prof.id === r.baseProfile ? "Your profile" : "Editing"}${prof.hidden ? " \u00B7 app variant" : ""}</div>
        <div class="insp-name"><b>${esc(prof.name)}</b></div>
        <p class="insp-desc">${esc(prof.description)}</p>
        ${Object.keys(vmap).length ? `<div class="variant-row">${Object.entries(vmap).map(([vid, apps]) => `<button class="chip amber" data-editvariant="${vid}" title="Edit this variant">${esc(apps.map(appName).join(", "))} \u2192 variant</button>`).join("")}</div>` : ""}
      </div>
      <div class="insp-body">
        ${rows.length ? `<div class="summary-list">${rows.map(([id, spec]) => `<button class="summary-row" data-pick="${id}"><span>${esc(spec.rowLabel || buttonLabel(id, r.id))}</span><b>${esc(spec.label || (spec.action === "passthrough" ? "Normal key" : actionName(spec)))}${gestureNote(spec) ? `<em>${esc(gestureNote(spec))}</em>` : ""}</b></button>`).join("")}</div>`
          : `<p class="empty-hint">${readOnly ? "Stock leaves every button exactly as the remote made it." : "Nothing mapped yet."}</p>`}
        <p class="empty-hint" style="margin-top:22px">Click a button on the remote \u2014 or simply <b>press it</b> \u2014 to choose what it does on a tap, a hold and a double-tap.
        ${readOnly ? "<br><br>Stock is read-only; picking an action creates your own copy." : ""}</p>
      </div>`;
    return;
  }

  const b = tpl.buttons.find((x) => x.id === S.selected);
  const caps = buttonCaps(r, S.selected);
  const s = slots();
  const sends = caps.dead ? `<span class="chip red">Sends nothing to the PC</span>`
    : caps.sig.source === "consumer" ? `<code>Consumer ${esc(caps.sig.usage)}</code>` : `<code>Key ${esc(caps.sig.key)}</code>`;
  const capChip = caps.dead ? "" : caps.tapOnly ? `<span class="chip red">Tap only</span>`
    : caps.needsDriver || caps.needsLink ? `<span class="chip amber">Keyboard key</span>` : `<span class="chip green">True hold</span>`;

  let notes = "";
  if (caps.dead) notes += `<p class="insp-note warn">This button is handled inside the remote (or by infrared) and never reaches the computer, so it can't be mapped.</p>`;
  if (caps.tapOnly) notes += `<p class="insp-note">The Voice button sends one short pulse however long you hold it, so it has taps and double-taps but no hold.</p>`;
  if (caps.osReads) notes += `<p class="insp-note amber">Windows reads this media/volume key straight from the remote, so it keeps doing its normal job even when remapped. Best left as is.</p>`;
  if (caps.needsLink) notes += S.state.interception.learning ? `<p class="insp-note warn">Restart Windows to finish installing the keyboard-key driver. Until then this key may not respond.</p>` : `<p class="insp-note amber">The keyboard-key driver is running but the ${esc(r.name)} was not identified \u2014 it was probably plugged in after startup. Restart Windows to include it.</p>`;
  if (caps.needsDriver) notes += `<p class="insp-note amber">This is an ordinary keyboard key. Its new action takes effect once the keyboard-key driver is installed \u2014 until then it keeps working as ${esc(caps.sig.key)}. <button class="linkish" data-goto="settings">How to install</button></p>`;
  if (readOnly && !caps.dead) notes += `<p class="insp-note">Stock is read-only. Choosing an action creates your own profile and switches the ${esc(r.name)} to it.</p>`;
  if (prof.hidden) {
    const inherited = parentOf(prof)?.buttons[S.selected];
    const own = prof.own && prof.own[S.selected];
    notes += `<p class="insp-note">${own ? "This variant overrides the button." : `Inherited from ${esc(parentOf(prof)?.name || "the parent")}${inherited ? "" : " (normal key)"}. Changing it here only affects this variant.`}</p>`;
  }

  const groups = caps.dead ? "" : `
    <div class="slot-title"><span>Tap</span><small>${s.hold || s.double ? "fires when released" : "fires instantly"}</small></div>
    <div class="agroup"><div class="atiles"><button class="atile wide${s.tap.action === "passthrough" ? " on" : ""}" data-act="passthrough"><b>Normal key</b><small>Leave it as ${esc(caps.sig.source === "consumer" ? "the remote sends it" : caps.sig.key)}</small></button></div></div>
    ${ACTIONS.map((g) => `
      <div class="agroup"><h4>${g.group}</h4>
        <div class="atiles">${g.items.map((a) => `<button class="atile${s.tap.action === a.id ? " on" : ""}" data-act="${a.id}"><b>${esc(a.name)}</b><small>${esc(a.hint)}</small></button>`).join("")}</div>
      </div>
      ${g.items.some((a) => a.id === s.tap.action && a.param) ? paramEditor("tap", s.tap) : ""}`).join("")}
    ${gestureEditor("hold", "Hold", "Press and keep it down (0.45 s)", s.hold, caps.tapOnly)}
    ${gestureEditor("double", "Double-tap", "Two quick presses", s.double, false)}`;

  el.innerHTML = `
    <div class="insp-head">
      <div class="insp-kicker">${esc(r.name)} \u00B7 ${esc(prof.name)}</div>
      <div class="insp-name">
        <span class="insp-glyph${b.glyph ? "" : " t"}">${b.glyph ? glyphChar(b.glyph) : esc(b.text || b.label)}</span>
        <b>${esc(b.label)}</b>
      </div>
      <div class="insp-sends">${sends}${capChip}</div>
      ${caps.dead ? "" : `<div class="insp-summary">
        <div><span>Tap</span><b>${esc(actionName(s.tap))}</b></div>
        <div><span>Hold</span><b class="${s.hold ? "" : "none"}">${esc(s.hold ? actionName(s.hold) : caps.tapOnly ? "n/a" : "—")}</b></div>
        <div><span>Double-tap</span><b class="${s.double ? "" : "none"}">${esc(s.double ? actionName(s.double) : "—")}</b></div>
      </div>`}
    </div>
    <div class="insp-body">${notes}${groups}</div>`;
}

function actionOptions(current) {
  return `<option value="none"${!current ? " selected" : ""}>Nothing</option>` + ACTIONS.map((g) => `<optgroup label="${esc(g.group)}">${g.items
    .filter((a) => !["flow_ptt", "flow_command"].includes(a.id) || true)
    .map((a) => `<option value="${a.id}"${current && current.action === a.id ? " selected" : ""}>${esc(a.name.replace("\u2026", ""))}</option>`).join("")}</optgroup>`).join("");
}

function gestureEditor(slot, title, hint, spec, disabled) {
  if (disabled) return `<div class="slot-title muted"><span>${title}</span><small>not available on this button</small></div>`;
  return `<div class="slot-title"><span>${title}</span><small>${hint}</small></div>
    <div class="gesture">
      <select class="field" data-gesture="${slot}">${actionOptions(spec)}</select>
      ${spec && ACTION_INFO[spec.action]?.param ? paramEditor(slot, spec) : ""}
    </div>`;
}

function paramEditor(slot, spec) {
  const kind = ACTION_INFO[spec.action]?.param;
  switch (kind) {
    case "keys": {
      const recording = S.recording === slot;
      if (S.typedKeys) return `<div class="param"><label>Shortcut</label><input class="field mono" data-keystext="${slot}" value="${esc(spec.keys || "")}" placeholder="ctrl+win+right"><button class="linkish" id="recordMode">Record it instead</button></div>`;
      return `<div class="param"><label>Shortcut</label>
        <div class="recorder${recording ? " rec" : ""}" data-record="${slot}">${recording ? `<span class="hint">Press the shortcut now\u2026 (Esc cancels)</span>` : spec.keys ? keycaps(spec.keys) : `<span class="hint">Click, then press keys</span>`}</div>
        <div class="presets">${[["ctrl+enter", "Ctrl+Enter"], ["alt+tab", "Alt+Tab"], ["win+d", "Win+D"], ["ctrl+oemtilde", "Ctrl+`"], ["enter", "Enter"], ["esc", "Esc"], ["ctrl+z", "Ctrl+Z"], ["ctrl+backspace", "Ctrl+\u232B"], ["ctrl+tab", "Ctrl+Tab"], ["ctrl+w", "Ctrl+W"]]
          .map(([k, n]) => `<button data-slot="${slot}" data-preset="${k}">${esc(n)}</button>`).join("")}</div>
        <button class="linkish" id="typeMode">Type it instead (for Win-key combos)</button></div>`;
    }
    case "text":
      return `<div class="param"><label>Text to type</label><textarea class="field" data-param="${slot}:text" placeholder="e.g. Please review this and suggest improvements.">${esc(spec.text || "")}</textarea></div>`;
    case "run":
      return `<div class="param"><label>Program, file or URL</label><input class="field mono" data-param="${slot}:run" value="${esc(spec.run || "")}" placeholder="C:\\Program Files\\...\\app.exe"></div>`;
    case "spot": {
      const spots = S.state.spots;
      if (!spots.length) return `<div class="param"><label>Input spot</label><p class="empty-hint">No spots saved yet. <button class="linkish" data-goto="spots">Add input spots</button></p></div>`;
      return `<div class="param"><label>Input spot</label><select class="field" data-param="${slot}:spot">${spots.map((s, i) => `<option value="${i + 1}"${(spec.spot | 0) === i + 1 ? " selected" : ""}>${i + 1}. ${esc(s.label)}</option>`).join("")}</select></div>`;
    }
    case "dir4": case "dir5": {
      const dirs = kind === "dir5" ? ["next", "left", "right", "up", "down"] : ["left", "right", "up", "down"];
      return `<div class="param"><label>Which screen</label><div class="seg">${dirs.map((d) => `<button data-dir="${slot}:${d}" class="${spec.dir === d ? "on" : ""}">${DIRS[d]}</button>`).join("")}</div></div>`;
    }
  }
  return "";
}

$("#viewLive").addEventListener("change", (e) => {
  const g = e.target.dataset.gesture;
  if (g) {
    const id = e.target.value;
    if (id === "keys") { S.recording = g; S.typedKeys = false; }
    updateSpec(g, id === "none" ? null : defaultsFor(id));
    return;
  }
  const p = e.target.dataset.param;
  if (p) {
    const [slot, field] = p.split(":");
    const cur = slots()[slot] || {};
    updateSpec(slot, { ...cur, [field]: field === "spot" ? +e.target.value : e.target.value });
    return;
  }
  const kt = e.target.dataset.keystext;
  if (kt) updateSpec(kt, { action: "keys", keys: e.target.value.trim().toLowerCase() });
});

$("#viewLive").addEventListener("click", (e) => {
  const d = e.target.closest("[data-dir]");
  if (d) {
    const [slot, dir] = d.dataset.dir.split(":");
    updateSpec(slot, { ...(slots()[slot] || {}), dir });
  }
  const v = e.target.closest("[data-editvariant]");
  if (v) { S.editProfile = v.dataset.editvariant; S.selected = null; renderAll(); }
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
  const slot = S.recording;
  if (e.code === "Escape" && !e.ctrlKey && !e.altKey && !e.shiftKey && !e.metaKey) { S.recording = null; renderInspector(); return; }
  let key = CODE_KEYS[e.code];
  if (!key && /^Key[A-Z]$/.test(e.code)) key = e.code.slice(3).toLowerCase();
  if (!key && /^Digit\d$/.test(e.code)) key = e.code.slice(5);
  if (!key && /^F\d{1,2}$/.test(e.code)) key = e.code.toLowerCase();
  if (!key && /^Numpad\d$/.test(e.code)) key = "numpad" + e.code.slice(6);
  if (!key) return;
  const mods = [e.ctrlKey && "ctrl", e.metaKey && "win", e.altKey && "alt", e.shiftKey && "shift"].filter(Boolean);
  S.recording = null;
  updateSpec(slot, { action: "keys", keys: [...mods, key].join("+") });
});

// Writes one button's spec into the edited profile (copying Stock first if needed).
async function setAction(btnId, spec, waitForKeys) {
  try {
    const r = currentRemote();
    const prof = editingProfile();
    const buttons = { ...prof.buttons };
    if (spec) buttons[btnId] = spec; else delete buttons[btnId];
    if (waitForKeys) { prof.buttons = buttons; renderInspector(); return; } // save once the recorder has keys
    if (prof.id === "stock") {
      const n = S.state.profiles.filter((p) => p.name.startsWith("My " + r.name)).length;
      const res = await api("/api/profile", { name: `My ${r.name} profile${n ? " " + (n + 1) : ""}`, description: `Custom mapping for the ${r.name}.`, buttons });
      S.state = await api("/api/remote-profile", { remote: r.id, profile: res.id });
      S.editProfile = null;
      toast(`Created \u201C${profileById(res.id).name}\u201D and switched the ${r.name} to it`);
    } else {
      S.state = (await saveProfile(prof, { buttons })).state;
    }
    renderLive();
    renderRail();
    renderProfiles();
  } catch (e) { fail(e); }
}

function saveProfile(p, changes = {}) {
  const next = { ...p, ...changes };
  return api("/api/profile", { id: p.id, name: next.name, description: next.description, buttons: next.buttons, extends: next.extends || undefined, hidden: !!next.hidden, apps: next.apps || {} });
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
  if (S.view === "test") onTestPress(p);

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

  const labelW = 160, top = 32, laneH = 16;
  const lanes = [];
  for (const p of [...recent].reverse()) if (!lanes.includes(p.key) && lanes.length < 4) lanes.push(p.key);
  const x = (t) => labelW + (w - labelW - 18) * (1 - (now - t) / WINDOW);

  g.strokeStyle = "rgba(255,255,255,.035)";
  g.lineWidth = 1;
  for (let s = 0; s <= 12; s++) {
    const xx = Math.round(x(now - s * 1000)) + 0.5;
    g.beginPath(); g.moveTo(xx, top - 8); g.lineTo(xx, h - 8); g.stroke();
  }
  g.font = "10px 'Cascadia Mono'";
  g.fillStyle = "#4d4b47";
  g.fillText("now", w - 42, 17);
  g.fillText("\u221212 s", labelW, 17);

  lanes.forEach((key, i) => {
    const y = top + i * (laneH + 5);
    const [rid, bid] = key.split("/");
    g.font = "600 12px Bahnschrift";
    g.fillStyle = "#8a867e";
    g.fillText(`${remoteById(rid)?.name ?? rid} \u00B7 ${buttonLabel(bid, rid)}`, 16, y + 12);
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
      const bw = Math.max(3, x1 - x0);
      g.beginPath(); g.roundRect ? g.roundRect(x0, y + 3, bw, laneH - 6, 3) : g.rect(x0, y + 3, bw, laneH - 6); g.fill();
      g.shadowBlur = 0;
      if (dur > 400 && x1 - x0 > 34) {
        g.font = "10px 'Cascadia Mono'";
        g.fillStyle = open ? "#ffd2cc" : "#17130a";
        g.fillText((dur / 1000).toFixed(1) + "s", x1 - 30, y + 12);
      }
    }
  });
  if (recent.length) drawTimeline();
}

// ------------------------------------------------------------------ profiles view

function renderProfiles() {
  const st = S.state;
  const usedOn = (pid) => st.remotes.filter((r) => r.baseProfile === pid);
  $("#viewProfiles").innerHTML = `
    <div class="head">
      <div><div class="eyebrow">Profiles</div><h1 class="title">A mode for every job</h1>
      <p class="sub">Each remote runs one profile; press <b>Menu</b> on the remote to cycle them. A profile can carry <b>app variants</b> that take over while that app is in front and only store what differs. Stock hands the remote back exactly as it came.</p></div>
    </div>
    <div class="pgrid">
      ${choosable().map((p) => {
        const maps = Object.entries(p.buttons);
        const live = usedOn(p.id);
        const ro = p.id === "stock";
        const vmap = variantsOf(p);
        return `<article class="pcard${live.length ? " live-on" : ""}" data-pid="${p.id}">
          <div class="row" style="justify-content:space-between;flex-wrap:nowrap">
            <input class="pcard-name" value="${esc(p.name)}" ${ro ? "readonly" : ""} data-field="name" spellcheck="false">
            ${ro ? `<span class="lock" title="Read-only">\uE72E</span>` : ""}
          </div>
          <textarea class="pcard-desc" data-field="description" ${ro ? "readonly" : ""} spellcheck="false">${esc(p.description)}</textarea>
          <div class="pcard-maps">
            ${maps.length ? maps.slice(0, 6).map(([id, spec]) => `<div><span>${esc(buttonLabel(id))}</span><b>${esc(spec.action === "passthrough" ? "Hold: " + actionName(spec.hold || spec.double) : actionName(spec))}</b></div>`).join("") : `<div><span>Every button</span><b>as the remote sends it</b></div>`}
            ${maps.length > 6 ? `<div><span></span><b>+${maps.length - 6} more</b></div>` : ""}
          </div>
          ${ro ? "" : `<div class="variants">
            <div class="variants-head"><span>App variants</span><button class="linkish" data-addvariant>+ Add app</button></div>
            ${Object.keys(vmap).length ? Object.entries(vmap).map(([vid, apps]) => {
              const v = profileById(vid);
              return `<div class="variant"><div><b>${esc(apps.map(appName).join(", "))}</b><small>${esc(v ? Object.keys(v.own || {}).length + " change" + (Object.keys(v.own || {}).length === 1 ? "" : "s") + " \u00B7 " + v.name : "missing profile")}</small></div>
                <span class="row"><button class="btn sm ghost" data-editvariant="${vid}">Edit</button><button class="btn sm ghost danger" data-dropvariant="${vid}">Remove</button></span></div>`;
            }).join("") : `<p class="empty-hint">None yet \u2014 add one to change a few buttons only inside a specific app.</p>`}
          </div>`}
          <div class="pcard-foot">
            ${st.remotes.map((r) => r.baseProfile === p.id
              ? `<span class="chip amber"><span class="led amber"></span>Live on ${esc(r.name)}</span>`
              : `<button class="btn sm" data-apply="${r.id}">Use on ${esc(r.name)}</button>`).join("")}
            <span class="spacer"></span>
            ${ro ? "" : `<button class="btn sm ghost" data-edit>Edit</button>`}
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
    const ev = e.target.closest("[data-editvariant]");
    if (ev) { S.editProfile = ev.dataset.editvariant; S.selected = null; showView("live"); renderAll(); return; }
    if (!card) return;
    const p = profileById(card.dataset.pid);
    const apply = e.target.closest("[data-apply]");
    if (apply) { S.state = await api("/api/remote-profile", { remote: apply.dataset.apply, profile: p.id }); renderAll(); return; }
    if (e.target.closest("[data-edit]")) { S.editProfile = p.id; S.selected = null; showView("live"); renderAll(); return; }
    if (e.target.closest("[data-addvariant]")) { addVariant(p); return; }
    const dv = e.target.closest("[data-dropvariant]");
    if (dv) {
      if (!confirmInline(dv, "Remove")) return;
      const apps = Object.fromEntries(Object.entries(p.apps || {}).filter(([, v]) => v !== dv.dataset.dropvariant));
      S.state = (await saveProfile(p, { apps })).state;
      if (!Object.values(apps).includes(dv.dataset.dropvariant)) S.state = await api("/api/profile-delete", { id: dv.dataset.dropvariant });
      renderAll(); toast("Variant removed");
      return;
    }
    if (e.target.closest("[data-dup]")) {
      const res = await api("/api/profile", { name: p.name + " copy", description: p.description, buttons: p.buttons });
      S.state = res.state; renderAll(); toast("Duplicated " + p.name); return;
    }
    if (e.target.closest("[data-del]")) {
      if (!confirmInline(e.target.closest("[data-del]"), "Delete")) return;
      S.state = await api("/api/profile-delete", { id: p.id });
      for (const vid of new Set(Object.values(p.apps || {}))) S.state = await api("/api/profile-delete", { id: vid });
      if (S.editProfile === p.id) S.editProfile = null;
      renderAll(); toast("Deleted " + p.name + " (a backup is kept in data\\deleted-profiles)");
    }
  } catch (err) { fail(err); }
});

// Two-step destructive buttons without a modal: first click arms the button.
function confirmInline(btn, label) {
  if (btn.dataset.armed) return true;
  btn.dataset.armed = "1";
  btn.textContent = "Click again";
  setTimeout(() => { if (btn.isConnected) { delete btn.dataset.armed; btn.textContent = label; } }, 3000);
  return false;
}

// App picker: choose a running app (or type a process name), create a variant that inherits
// everything, then open it for editing.
async function addVariant(parent) {
  let apps = [];
  try { apps = (await api("/api/apps")).apps; } catch (e) { /* offline list is optional */ }
  const taken = new Set(Object.keys(parent.apps || {}).map((a) => a.toLowerCase()));
  const overlay = $("#overlay");
  overlay.hidden = false;
  overlay.innerHTML = `<div class="picker">
    <div class="eyebrow">New app variant \u00B7 ${esc(parent.name)}</div>
    <h3>Which app?</h3>
    <p class="sub">The variant starts as an exact copy and takes over while this app is in front. Change only the buttons that should behave differently there.</p>
    <div class="app-list">${apps.filter((a) => !taken.has(a.process.toLowerCase())).map((a) => `<button data-app="${esc(a.process)}"><b>${esc(appName(a.process) === a.process ? a.name : appName(a.process))}</b><small>${esc(a.process)}.exe</small></button>`).join("") || `<p class="empty-hint">No other apps open right now \u2014 type one below.</p>`}</div>
    <div class="row" style="margin-top:14px"><input class="field mono" id="appTyped" placeholder="process name, e.g. WindowsTerminal" style="flex:1"><button class="btn" id="appTypedGo">Add</button></div>
    <div class="row" style="justify-content:flex-end;margin-top:16px"><button class="btn ghost" id="pickerClose">Cancel</button></div>
  </div>`;
  const close = () => { overlay.hidden = true; overlay.innerHTML = ""; };
  const create = async (proc) => {
    proc = proc.replace(/\.exe$/i, "").trim();
    if (!proc) return;
    close();
    try {
      const res = await api("/api/profile", { name: `${parent.name} \u00B7 ${appName(proc)}`, description: `Takes over from ${parent.name} while ${appName(proc)} is in front.`, extends: parent.id, hidden: true, buttons: parent.buttons });
      S.state = res.state;
      const fresh = profileById(parent.id);
      S.state = (await saveProfile(fresh, { apps: { ...(fresh.apps || {}), [proc]: res.id } })).state;
      S.editProfile = res.id; S.selected = null;
      showView("live"); renderAll();
      toast(`Variant for ${appName(proc)} created \u2014 change the buttons that should differ there`);
    } catch (e) { fail(e); }
  };
  overlay.onclick = (e) => {
    if (e.target === overlay || e.target.closest("#pickerClose")) close();
    const b = e.target.closest("[data-app]");
    if (b) create(b.dataset.app);
    if (e.target.closest("#appTypedGo")) create($("#appTyped").value);
  };
}

$("#viewProfiles").addEventListener("change", async (e) => {
  const card = e.target.closest("[data-pid]");
  if (!card || !e.target.dataset.field) return;
  const p = profileById(card.dataset.pid);
  try { S.state = (await saveProfile(p, { [e.target.dataset.field]: e.target.value.trim() })).state; renderRail(); toast("Saved"); }
  catch (err) { fail(err); }
});

// ------------------------------------------------------------------ input spots view

let openAdvanced = new Set();
function renderSpots() {
  const st = S.state;
  const bindings = [];
  for (const r of st.remotes) {
    const p = profileById(r.baseProfile);
    if (!p) continue;
    for (const [id, spec] of Object.entries(p.buttons)) {
      if (!r.buttons.some((b) => b.id === id)) continue;
      for (const [how, s] of [["", spec], ["hold ", spec.hold], ["2\u00D7 ", spec.double]]) {
        if (s && /^spot_/.test(s.action) && !(s.action === "spot_goto" && s.spot > 1)) bindings.push(`<div><b>${esc(r.name)} \u00B7 ${how}${esc(buttonLabel(id, r.id))}</b> \u2192 ${esc(s.action === "spot_goto" ? "spot 1\u20139 (by number)" : actionName(s))}</div>`);
      }
    }
  }
  $("#viewSpots").innerHTML = `
    <div class="head">
      <div><div class="eyebrow">Input spots</div><h1 class="title">Hop between inputs</h1>
      <p class="sub">Your dictation destinations, in order \u2014 a terminal, a chat box in the browser, anything with a text field. The remote jumps between them, focuses the box and you just talk. Spots on other desktops or screens work too.</p></div>
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
          <li><b>From the remote:</b> click into the input and <b>hold Menu</b>.</li>
          <li><b>From the keyboard:</b> click into it and press <span class="keycap">Ctrl</span><span class="plus">+</span><span class="keycap">Alt</span><span class="plus">+</span><span class="keycap">Shift</span><span class="plus">+</span><span class="keycap">F9</span>.</li>
          <li><b>From here:</b> press Capture, then click into the input within 4 seconds.</li>
        </ol>
        <button class="btn primary" id="capture"><span class="ic">\uE710</span>Capture in 4 s</button>
        <div class="bindings">${bindings.length ? `<div class="eyebrow" style="color:var(--muted);margin-bottom:4px">Remote buttons using spots</div>${bindings.join("")}`
          : `<div>No active profile uses spots yet. The <b>AI dictation workflow</b> profile maps Right/Left and 1\u20139.</div>`}</div>
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
    if (!confirmInline(e.target.closest("[data-del]"), "Remove")) return;
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
    <b>Click into the input box now</b><p>Switch to the app and click where you'd type. Airdeck grabs it when the ring closes.</p>
    <button class="btn ghost" id="cancelCapture" style="margin-top:22px">Cancel</button></div>`;
  let cancelled = false;
  $("#cancelCapture").onclick = () => { cancelled = true; overlay.hidden = true; };
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
    if (cancelled) { await saveSpots(S.state.spots.slice(0, -1)); toast("Capture cancelled"); }
    else if (/Airdeck/.test(spot.titleContains || "") || /^Airdeck/.test(spot.label)) {
      await saveSpots(S.state.spots.slice(0, -1));
      toast("That captured Airdeck itself \u2014 click into another app during the countdown", true);
    } else toast(`Saved spot ${S.state.spots.length}: ${spot.label}`);
  } catch (e) { fail(e); }
  overlay.hidden = true;
  renderAll();
}

function flashSpot(i) {
  document.querySelectorAll(".spot").forEach((el) => el.classList.toggle("jumped", +el.dataset.i === i));
}

// ------------------------------------------------------------------ test view

function testRemote() { return remoteById(S.test.remote) || currentRemote(); }

// The things to verify: every mapped button of the profile in effect, plus each of its gestures.
function testItems() {
  const r = testRemote(), p = profileById(r.profile);
  const items = [];
  for (const [id, spec] of Object.entries(p.buttons)) {
    const caps = buttonCaps(r, id);
    if (caps.dead || !r.buttons.some((b) => b.id === id)) continue;
    items.push({ id, spec, caps });
  }
  const order = (tplFor(r.id)?.buttons || []).map((b) => b.id);
  return items.sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id));
}

function renderTest() {
  const st = S.state, r = testRemote(), p = profileById(r.profile);
  if (!S.test.remote) S.test.remote = r.id;
  const items = testItems();
  const done = items.filter((i) => S.test.results[i.id]).length;
  const extra = Object.keys(S.test.extra);
  $("#viewTest").innerHTML = `
    <div class="head">
      <div><div class="eyebrow">Test</div><h1 class="title">Button check</h1>
      <p class="sub">Press every button below on the remote. Each one ticks off when Airdeck recognises it as the profile says. In <b>dry run</b> nothing actually happens \u2014 no dictation, no jumps \u2014 so you can test anywhere.</p></div>
    </div>
    <div class="test-bar">
      <div class="seg">${st.remotes.map((x) => `<button data-testremote="${x.id}" class="${x.id === r.id ? "on" : ""}">${esc(x.name)}${x.connected ? "" : " (off)"}</button>`).join("")}</div>
      <label class="dry"><button class="switch${st.testMode ? " on" : ""}" id="dryRun" aria-label="Dry run"></button><span><b>Dry run</b><small>${st.testMode ? "On \u2014 buttons do nothing" : "Off \u2014 buttons act for real"}</small></span></label>
      <div class="progress"><div style="width:${items.length ? (100 * done / items.length).toFixed(0) : 0}%"></div><span>${done} / ${items.length}</span></div>
      <button class="btn ghost" id="testReset"><span class="ic">\uE72C</span>Reset</button>
    </div>
    <p class="test-profile">Checking <b>${esc(r.name)}</b> with <b>${esc(p.name)}</b>${r.autoApp ? ` (the ${esc(appName(r.autoApp))} variant \u2014 switch apps to test the base profile)` : ""}. Holds and double-taps show under each button.</p>
    <div class="test-grid">
      ${items.map((i) => {
        const res = S.test.results[i.id];
        const g = gestureNote(i.spec);
        const note = i.caps.needsDriver ? "needs the keyboard-key driver" : i.caps.needsLink ? "remote not identified by the driver" : i.caps.osReads ? "Windows also reacts" : "";
        return `<div class="test-item${res ? " ok" : ""}${note ? " warnish" : ""}">
          <span class="tick">${res ? "\u2713" : ""}</span>
          <div><b>${esc(buttonLabel(i.id, r.id))}</b><small>${esc(i.spec.action === "passthrough" ? "normal key" : actionName(i.spec))}${g ? " \u00B7 " + esc(g) : ""}</small>${note ? `<em>${esc(note)}</em>` : ""}</div>
          <span class="when">${res ? res : "press it"}</span>
        </div>`;
      }).join("")}
    </div>
    ${extra.length ? `<p class="test-extra">Also pressed (normal keys, not mapped): ${extra.map((b) => esc(buttonLabel(b, r.id))).join(", ")}</p>` : ""}`;
}

function onTestPress(p) {
  if (p.edge !== 1 || p.remote !== testRemote().id) return;
  if (p.action) S.test.results[p.button] = new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  else S.test.extra[p.button] = true;
  renderTest();
}

$("#viewTest").addEventListener("click", async (e) => {
  try {
    const tr = e.target.closest("[data-testremote]");
    if (tr) { S.test = { remote: tr.dataset.testremote, results: {}, extra: {} }; renderTest(); return; }
    if (e.target.closest("#dryRun")) { S.state = await api("/api/testmode", { on: !S.state.testMode }); renderRail(); renderTest(); return; }
    if (e.target.closest("#testReset")) { S.test.results = {}; S.test.extra = {}; renderTest(); }
  } catch (err) { fail(err); }
});

// ------------------------------------------------------------------ settings view

function renderSettings() {
  const st = S.state, ic = st.interception, set = st.settings;
  const hot = [["F12", "Exit Airdeck \u2014 every remote goes back to stock"], ["F11", "Pause / resume all profiles"], ["F10", "Next profile for the remote you used last"], ["F9", "Save the focused input box as an input spot"]];
  const flowKeys = (s) => keycaps(s.replace(/LControlKey/g, "ctrl").replace(/LWin/g, "win").replace(/LMenu/g, "alt").replace(/LShiftKey/g, "shift").replace(/Space/g, "space"));
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
        <h3>Keyboard-key driver ${ic.active && ic.learning ? `<span class="chip red">Restart Windows to finish</span>` : ic.active ? `<span class="chip green">Active \u00B7 ${ic.linked.length ? ic.linked.map((id) => esc(remoteById(id)?.name ?? id)).join(" + ") : "no remote"} filtered</span>` : ic.installed ? `<span class="chip amber">Installed \u2014 restart Windows</span>` : `<span class="chip">Not installed \u00B7 optional</span>`}</h3>
        <p>Arrows, digits, Pg+/Pg-, DEL and Menu reach Windows as ordinary keyboard keys; the open-source Interception driver lets Airdeck remap them on the remote only. It is loaded at startup, so a receiver plugged in later needs a restart before its keys can be remapped.</p>
        ${ic.active ? "" : `<ol class="steps"><li>Open the tools folder.</li><li>Right-click <code>install-interception.cmd</code> \u2192 <b>Run as administrator</b>.</li><li>Restart Windows. Undo any time with <code>uninstall-interception.cmd</code>.</li></ol>
          <button class="btn" data-open="driver"><span class="ic">\uE838</span>Open tools folder</button>`}
      </section>
      <section class="scard">
        <h3>Wispr Flow</h3>
        <p>Read from Flow's own settings, so remote actions always match your shortcuts.</p>
        <div class="kv">
          <span>Push-to-talk</span><div>${flowKeys(st.flow.ptt)}</div>
          <span>Hands-free</span><div>${flowKeys(st.flow.handsfree)}</div>
          <span>Command mode</span><div>${flowKeys(st.flow.command)}</div>
          <span>Paste last</span><div>${flowKeys(st.flow.pasteLast || "")}</div>
        </div>
      </section>
      <section class="scard">
        <h3>Hotkeys</h3>
        <p>On your normal keyboard, from anywhere.</p>
        <div class="kv hot">${hot.map(([k, d]) => `<div>${keycaps("ctrl+alt+shift+" + k.toLowerCase())}</div><span style="font-family:var(--body)">${d}</span>`).join("")}</div>
      </section>
      <section class="scard">
        <h3>Files &amp; health</h3>
        <p>Profiles are plain JSON in <code style="font-family:var(--mono)">profiles\\</code>; the self-test checks that the key blocker is alive.</p>
        <div class="row">
          <button class="btn" data-open="profiles"><span class="ic">\uE8B7</span>Profiles folder</button>
          <button class="btn" data-open="log"><span class="ic">\uE9F9</span>Open log</button>
          <button class="btn ghost" id="reload"><span class="ic">\uE72C</span>Reload</button>
          <button class="btn ghost" id="selftest"><span class="ic">\uE9D9</span>Self-test</button>
        </div>
      </section>
    </div>`;
}

$("#viewSettings").addEventListener("click", async (e) => {
  try {
    if (e.target.closest("#startup")) { S.state = await api("/api/settings", { startWithWindows: !S.state.settings.startWithWindows }); renderSettings(); toast(S.state.settings.startWithWindows ? "Airdeck will start with Windows" : "Won't start with Windows"); }
    if (e.target.closest("#elevate")) { await api("/api/restart-admin", {}); toast("Approve the Windows prompt \u2014 Airdeck restarts as administrator"); }
    if (e.target.closest("#reload")) { S.state = await api("/api/reload", {}); renderAll(); }
    if (e.target.closest("#selftest")) { const t = await api("/api/selftest", {}); toast(`${t.hook} \u00B7 driver: ${t.interception} \u00B7 ${t.remotes.join(", ")}`, !/OK/.test(t.hook)); }
    const open = e.target.closest("[data-open]");
    if (open) await api("/api/open", { what: open.dataset.open });
  } catch (err) { fail(err); }
});

// ------------------------------------------------------------------ shell

async function showView(v) {
  // Leaving the Test view always switches dry run off, so buttons never stay silently inert.
  if (S.view === "test" && v !== "test" && S.state?.testMode) {
    try { S.state = await api("/api/testmode", { on: false }); renderRail(); toast("Dry run off \u2014 buttons act again"); } catch (e) { fail(e); }
  }
  S.view = v;
  S.recording = null;
  document.querySelectorAll("#nav button").forEach((b) => b.classList.toggle("on", b.dataset.view === v));
  document.querySelectorAll(".view").forEach((el) => el.classList.toggle("on", el.dataset.view === v));
  // Re-render on entry so a view never shows state from before a background change.
  ({ live: renderLive, profiles: renderProfiles, spots: renderSpots, test: renderTest, settings: renderSettings })[v]();
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
  renderTest();
  renderSettings();
}

async function boot() {
  try {
    S.state = await api("/api/state");
    S.uiVersion = S.state.uiVersion;
    const showRemote = new URLSearchParams(location.search).get("remote"); // for screenshots
    if (showRemote && S.state.remotes.some((x) => x.id === showRemote)) S.remote = showRemote;
    const showProfile = new URLSearchParams(location.search).get("profile"); // for screenshots
    if (showProfile && profileById(showProfile)) {
      // Render as if the remote were running it (no "not active" banner in screenshots).
      const rr = remoteById(S.remote) || S.state.remotes.find((x) => x.connected) || S.state.remotes[0];
      if (rr) { rr.profile = rr.baseProfile = showProfile; rr.autoApp = null; }
    }
    renderAll();
    const hash = location.hash.slice(1);
    if (["live", "profiles", "spots", "test", "settings"].includes(hash)) showView(hash);
    const pickBtn = new URLSearchParams(location.search).get("select");
    if (pickBtn) select(pickBtn);
  } catch (e) {
    document.body.innerHTML = `<div class="empty" style="margin:80px auto;max-width:520px"><b>Airdeck isn't running</b>Start Airdeck from the Start menu and reload this window.</div>`;
    return;
  }
  if (location.search.includes("nosse")) return; // static render (screenshots)
  const events = new EventSource("/api/events");
  // After Airdeck restarts the stream reconnects on its own; resync so nothing is stale.
  let opened = false;
  // Airdeck was updated while this window was open: load the new UI instead of running the old one.
  const stale = (v) => v && S.uiVersion && v !== S.uiVersion;
  events.onopen = async () => {
    if (opened) { S.state = await api("/api/state"); if (stale(S.state.uiVersion)) return location.reload(); renderAll(); }
    opened = true;
  };
  events.addEventListener("reload", (e) => { if (stale(JSON.parse(e.data).uiVersion)) location.reload(); });
  events.addEventListener("press", (e) => onPress(JSON.parse(e.data)));
  events.addEventListener("spot", (e) => flashSpot(JSON.parse(e.data).index));
  let pending = null;
  events.addEventListener("state", () => {
    clearTimeout(pending);
    pending = setTimeout(async () => {
      const typing = document.activeElement && /INPUT|TEXTAREA/.test(document.activeElement.tagName);
      if (typing || S.recording) return;
      S.state = await api("/api/state");
      renderRail();
      if (S.view === "live") renderLive(); else if (S.view === "profiles") renderProfiles(); else if (S.view === "spots") renderSpots();
      else if (S.view === "test") renderTest(); else renderSettings();
    }, 150);
  });
}

window.addEventListener("resize", drawTimeline);
boot();

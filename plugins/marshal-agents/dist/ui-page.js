// The local status page (`GET /ui?k=<key>`, server.ts): one self-contained HTML file, inline CSS and JS, nothing
// loaded from anywhere else, so it works offline and under the page's strict CSP. It polls `/ui/state` once a
// second and builds every text node with textContent (agent names come from hooks: untrusted display text).
// The key is not in this file: the script reads it from the page's own address.
// Look: an airfield at night. Asphalt, one taxi-line yellow as the only accent, red only for stop, green for go.
import { PRODUCT_NAME } from "./product.js";
export const UI_PAGE = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<meta name="color-scheme" content="dark">
<title>${PRODUCT_NAME}</title>
<style>
:root {
  --asphalt: #17191C;
  --raised: #212428;
  --inset: #0E0F11;
  --ink: #F3F1EA;
  --dim: #9a9890;
  --taxi: #F6CB3C;
  --stop: #FF4A3A;
  --go: #55E0A2;
  --rule: #2c3035;
}
* { box-sizing: border-box; }
html { background: var(--asphalt); }
body {
  margin: 0;
  background: var(--asphalt);
  color: var(--ink);
  font-family: system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
  font-size: 17px;
  line-height: 1.5;
  -webkit-font-smoothing: antialiased;
}
main { max-width: 720px; padding: 56px 24px 48px; }
.brand { display: flex; align-items: center; gap: 10px; color: var(--dim); font-size: 14px; letter-spacing: 0.04em; margin: 0 0 40px; }
.brand::before { content: ""; width: 18px; height: 3px; background: var(--taxi); border-radius: 2px; }
h1 { font-size: clamp(28px, 6vw, 40px); line-height: 1.15; font-weight: 600; letter-spacing: -0.01em; margin: 0 0 12px; }
h1.stop { color: var(--stop); }
.lead { color: var(--dim); margin: 0 0 40px; max-width: 34em; }
section { margin: 0 0 40px; }
h2 { font-size: 14px; font-weight: 600; color: var(--dim); letter-spacing: 0.04em; margin: 0 0 12px; }
.pair { display: flex; flex-wrap: wrap; gap: 32px; align-items: flex-start; }
.qr { background: #fff; padding: 12px; border-radius: 6px; line-height: 0; width: 264px; max-width: 100%; }
.qr img { width: 100%; height: auto; image-rendering: pixelated; display: block; }
.code-box { min-width: 220px; flex: 1 1 220px; }
.code { font-size: clamp(44px, 12vw, 64px); font-weight: 600; font-variant-numeric: tabular-nums; letter-spacing: 0.06em; line-height: 1; margin: 4px 0 20px; }
.code .gap { display: inline-block; width: 0.35em; }
.meter { height: 3px; background: var(--inset); border-radius: 2px; overflow: hidden; margin: 0 0 8px; max-width: 280px; }
.meter > div { height: 100%; background: var(--taxi); width: 100%; transition: width 1s linear; }
.small { font-size: 14px; color: var(--dim); }
button {
  font: inherit; font-size: 15px; color: var(--ink); background: var(--raised);
  border: 1px solid var(--taxi); border-radius: 6px; padding: 10px 18px; margin-top: 20px; cursor: pointer;
}
button:hover { background: #2a2e33; }
button:disabled { opacity: 0.6; cursor: default; }
:focus-visible { outline: 2px solid var(--taxi); outline-offset: 3px; }
.addr { font-variant-numeric: tabular-nums; color: var(--ink); background: var(--inset); padding: 1px 6px; border-radius: 4px; white-space: nowrap; }
code { font-family: ui-monospace, "Cascadia Mono", Consolas, Menlo, monospace; font-size: 0.92em; background: var(--inset); padding: 1px 6px; border-radius: 4px; }
ul.rows { list-style: none; margin: 0; padding: 0; border-top: 1px solid var(--rule); }
ul.rows li { display: flex; flex-wrap: wrap; gap: 4px 16px; align-items: baseline; padding: 12px 0; border-bottom: 1px solid var(--rule); }
.name { font-weight: 600; min-width: 7em; }
.src { color: var(--dim); min-width: 8em; }
.state { margin-left: auto; display: inline-flex; align-items: center; gap: 8px; }
.dot { width: 8px; height: 8px; border-radius: 50%; background: var(--dim); flex: none; }
.dot.go { background: var(--go); }
.dot.stop { background: var(--stop); }
.dot.wait { background: var(--taxi); }
p { margin: 0 0 8px; }
footer { border-top: 1px solid var(--rule); padding-top: 20px; margin-top: 56px; }
footer p { font-size: 14px; color: var(--dim); }
[hidden] { display: none !important; }
@media (prefers-reduced-motion: reduce) {
  * { transition: none !important; animation: none !important; }
}
@media (max-width: 480px) {
  main { padding: 32px 16px 32px; }
  .pair { gap: 24px; }
  .qr { width: 100%; }
}
</style>
</head>
<body>
<main>
  <p class="brand" id="brand">${PRODUCT_NAME}</p>
  <h1 id="headline" aria-live="polite">Connecting to ${PRODUCT_NAME}…</h1>
  <p class="lead" id="lead"></p>

  <section id="pairing" hidden aria-labelledby="pair-h">
    <h2 id="pair-h">Pair the headset</h2>
    <div class="pair">
      <div class="qr" id="qr" hidden><img id="qr-img" alt="Pairing QR code for the headset"></div>
      <div class="code-box">
        <div id="code-live">
          <div class="small">Pairing code</div>
          <div class="code" id="code" aria-label=""></div>
          <div class="meter" aria-hidden="true"><div id="meter"></div></div>
          <div class="small" id="expires"></div>
        </div>
        <p id="code-none" class="small" hidden>No pairing code is active. Choose New code to get one.</p>
        <button type="button" id="new-code">New code</button>
      </div>
    </div>
    <p class="small" id="manual" style="margin-top:28px"></p>
    <p class="small" id="no-qr" hidden></p>
  </section>

  <section id="agents-sec" hidden aria-labelledby="agents-h">
    <h2 id="agents-h">Agents</h2>
    <ul class="rows" id="agents"></ul>
    <p class="small" id="no-agents" hidden>No agents yet. Start Claude Code, Codex CLI, Gemini CLI or Muse as usual; they show up here.</p>
  </section>

  <section id="status-sec" hidden aria-labelledby="status-h">
    <h2 id="status-h">Status</h2>
    <p id="pending"></p>
    <p id="explain"></p>
    <p id="tools" hidden></p>
  </section>

  <footer>
    <p>Close this page any time; ${PRODUCT_NAME} keeps running in the terminal. Ctrl+C there stops it.</p>
    <p>To undo the setup: <code>npx marshal-agents disconnect</code></p>
  </footer>
</main>
<script>
(function () {
  "use strict";
  // The key in the address worked once, for loading this page; from now on a cookie set by that load counts.
  // Drop the key from the address bar and history: it is spent.
  try { history.replaceState(null, "", location.pathname); } catch (e) { /* not important */ }
  var q = "";
  var $ = function (id) { return document.getElementById(id); };
  var SOURCES = { claude: "Claude Code", muse: "Muse", codex: "Codex CLI", gemini: "Gemini CLI", opencode: "OpenCode" };
  var STATES = {
    working: ["Working", "go"], reading: ["Reading", "go"], editing: ["Editing", "go"], testing: ["Running tests", "go"],
    blocked: ["Waiting for approval", "wait"], waiting: ["Waiting for input", "wait"], done: ["Done", ""], failed: ["Failed", "stop"]
  };
  var last = null;
  var busy = false;

  function text(id, s) { var el = $(id); if (el.textContent !== s) el.textContent = s; }
  function show(id, on) { $(id).hidden = !on; }

  function remaining(s) {
    if (s.codeExpiresAt === null) return 0;
    return Math.max(0, s.codeExpiresAt - Date.now());
  }

  function tick() {
    if (!last || last.code === null) return;
    var ms = remaining(last);
    var secs = Math.ceil(ms / 1000);
    var m = Math.floor(secs / 60), sec = secs % 60;
    text("expires", ms > 0 ? "Works once, for " + m + ":" + (sec < 10 ? "0" : "") + sec + " more." : "This code has expired.");
    $("meter").style.width = (Math.min(1, ms / 300000) * 100).toFixed(2) + "%";
  }

  function renderCode(s) {
    var live = s.code !== null;
    show("code-live", live);
    show("code-none", !live);
    if (live) {
      var el = $("code");
      if (el.getAttribute("data-code") !== s.code) {
        el.setAttribute("data-code", s.code);
        el.setAttribute("aria-label", "Pairing code " + s.code.split("").join(" "));
        el.textContent = "";
        el.appendChild(document.createTextNode(s.code.slice(0, 3)));
        var gap = document.createElement("span");
        gap.className = "gap";
        el.appendChild(gap);
        el.appendChild(document.createTextNode(s.code.slice(3)));
      }
    }
    var img = $("qr-img");
    if (s.qrSvg) {
      var src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(s.qrSvg);
      if (img.getAttribute("src") !== src) img.setAttribute("src", src);
    }
    show("qr", !!s.qrSvg);
    var addr = s.addresses.length > 0 ? s.addresses[0] + ":" + s.wsPort : null;
    var manual = $("manual");
    manual.textContent = "";
    if (addr) {
      manual.appendChild(document.createTextNode("Or choose Connect to my PC in the headset and type the address "));
      var a = document.createElement("span");
      a.className = "addr";
      a.textContent = addr;
      manual.appendChild(a);
      manual.appendChild(document.createTextNode(" and the code."));
      if (s.addresses.length > 1) {
        manual.appendChild(document.createTextNode(" If that does not work, try " + s.addresses.slice(1).map(function (x) { return x + ":" + s.wsPort; }).join(", ") + "."));
      }
    } else {
      manual.textContent = "No network address found on this PC. Connect the PC and the headset to the same Wi-Fi.";
    }
    var why = live && !s.qrSvg && s.qrHost === null && s.addresses.length > 0;
    show("no-qr", why);
    if (why) text("no-qr", "No QR code: none of these addresses is a private home or office network address. Type the address instead.");
    tick();
  }

  function renderAgents(s) {
    var ul = $("agents");
    ul.textContent = "";
    s.agents.forEach(function (a) {
      var li = document.createElement("li");
      var n = document.createElement("span"); n.className = "name"; n.textContent = a.name;
      var src = document.createElement("span"); src.className = "src"; src.textContent = SOURCES[a.source] || a.source;
      var st = document.createElement("span"); st.className = "state";
      var info = STATES[a.state] || [a.state, ""];
      var dot = document.createElement("span"); dot.className = "dot" + (info[1] ? " " + info[1] : ""); dot.setAttribute("aria-hidden", "true");
      st.appendChild(dot);
      st.appendChild(document.createTextNode(info[0]));
      li.appendChild(n); li.appendChild(src); li.appendChild(st);
      ul.appendChild(li);
    });
    show("no-agents", s.agents.length === 0);
  }

  function render(s) {
    last = s;
    text("brand", "${PRODUCT_NAME} " + s.version);
    var h = $("headline");
    h.className = s.halted ? "stop" : "";
    if (s.halted) {
      text("headline", "All agents are stopped.");
      text("lead", "To go on, press Continue in the headset, or type continue in the terminal and press Enter.");
    } else if (s.paired) {
      text("headline", "Headset connected.");
      text("lead", "Work as usual. Requests that need your approval appear in the headset.");
    } else {
      text("headline", "Put on your Quest and open ${PRODUCT_NAME}.");
      text("lead", s.code !== null
        ? "Look at the QR code below with the headset, or type the code."
        : "The headset is not connected. If it was paired before, it reconnects by itself when you open ${PRODUCT_NAME}.");
    }
    show("pairing", !s.paired);
    if (!s.paired) renderCode(s);
    show("agents-sec", s.paired || s.agents.length > 0);
    renderAgents(s);
    show("status-sec", true);
    text("pending", s.pendingRequests === 0 ? "No requests are waiting." : s.pendingRequests === 1 ? "1 request is waiting for you in the headset." : s.pendingRequests + " requests are waiting for you in the headset.");
    text("explain", s.explain === "on" ? "Explanations: on. Each request comes with a short explanation from Claude." : "Explanations: off. Requests show the rule-based explanation only.");
    show("tools", s.tools.length > 0);
    if (s.tools.length > 0) text("tools", "Set up for: " + s.tools.map(function (t) { return t.name + " (" + t.status + ")"; }).join(", ") + ".");
  }

  function lost() {
    last = null;
    $("headline").className = "";
    text("headline", "${PRODUCT_NAME} is not running.");
    text("lead", "The bridge in the terminal has stopped. Start it again to see the status here.");
    show("pairing", false);
    show("agents-sec", false);
    show("status-sec", false);
  }

  function poll() {
    fetch("/ui/state" + q, { cache: "no-store", credentials: "same-origin" })
      .then(function (r) { if (!r.ok) throw new Error(String(r.status)); return r.json(); })
      .then(render, lost);
  }

  $("new-code").addEventListener("click", function () {
    if (busy) return;
    busy = true;
    $("new-code").disabled = true;
    fetch("/ui/code" + q, { method: "POST", cache: "no-store", credentials: "same-origin" })
      .then(function (r) { if (!r.ok) throw new Error(String(r.status)); return r.json(); })
      .then(render, lost)
      .then(function () { busy = false; $("new-code").disabled = false; });
  });

  poll();
  setInterval(poll, 1000);
  setInterval(tick, 250);
})();
</script>
</body>
</html>
`;

// Test client: pairs with the bridge, prints every message, answers permission requests.
// Fails closed: allows only safe/network requests that are not truncated, denies everything else.
// Usage: node scripts/fake-quest.mjs <code> [host=127.0.0.1]
import { createRequire } from "node:module";
const require = createRequire(new URL("../bridge/package.json", import.meta.url));
const WebSocket = require("ws");

const code = process.argv[2];
const host = process.argv[3] ?? "127.0.0.1";
if (!/^\d{6}$/.test(code ?? "")) {
  console.error("usage: node scripts/fake-quest.mjs <6-digit code> [host]");
  process.exit(2);
}
const ws = new WebSocket(`ws://${host}:47821`);
const t0 = Date.now();
const log = (dir, m) => console.log(`[+${Date.now() - t0}ms] ${dir} ${JSON.stringify(m)}`);
const send = (m) => {
  log("->", m);
  ws.send(JSON.stringify(m));
};

ws.on("open", () => send({ type: "hello", code }));
ws.on("message", (raw) => {
  const m = JSON.parse(raw.toString());
  log("<-", m);
  if (m.type !== "perm_open") return;
  const r = m.request;
  const risk = r.risk?.risk;
  const allow = (risk === "safe" || risk === "network") && r.truncated === false;
  if (allow) {
    send({ type: "perm_decision", decision: { id: r.id, behavior: "allow", confirm: "pinch-hold" } });
  } else {
    send({ type: "perm_decision", decision: { id: r.id, behavior: "deny", message: "Marshal: denied (test)" } });
  }
});
ws.on("close", () => {
  console.log("closed");
  process.exit(0);
});
ws.on("error", (e) => console.error("ws error", e.message));

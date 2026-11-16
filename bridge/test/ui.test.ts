// The local status page (server.ts `ui`, ui-page.ts, open-browser.ts): who may reach it, what its state says, and
// that it leaves the token gate of the hook routes exactly as it was.
import { afterEach, describe, expect, it, vi } from "vitest";
import { request as httpRequest } from "node:http";
import { WebSocket } from "ws";
import type { ServerHandle, UiState, UiView } from "../src/server.js";
import { CODE, bootLive, pairHeadset, type Headset } from "./live-helpers.js";
import { encodeQr, renderQrSvg } from "../src/qr.js";
import { pairingPayload } from "../src/pair-qr.js";
import { UI_PAGE } from "../src/ui-page.js";
import { browserCommand, shouldOpenBrowser } from "../src/open-browser.js";

const KEY = "0123456789abcdef0123456789abcdef";
const TOKEN = "t".repeat(64);
const HOST = "192.168.1.20";

let handle: ServerHandle | undefined;
/** The status page cookie that boot() got by loading the page once with the key. */
let cookie = "";
const headsets: Headset[] = [];
afterEach(async () => {
  for (const h of headsets.splice(0)) h.close();
  await handle?.close();
  handle = undefined;
});

function view(over: Partial<UiView> = {}): UiView {
  return { version: "9.9.9", addresses: [HOST, "10.0.0.5"], qrHost: HOST, explain: "off", tools: [{ name: "Claude Code", status: "hooks installed" }], ...over };
}

async function boot(o: { view?: () => UiView; ui?: false; onCodeReplaced?: (c: string) => void; now?: () => number } = {}): Promise<ServerHandle> {
  const v = o.view ?? (() => view());
  handle = await bootLive({
    ingestToken: TOKEN,
    ...(o.ui === false ? {} : { ui: { key: KEY, view: v } }),
    ...(o.onCodeReplaced ? { onCodeReplaced: o.onCodeReplaced } : {}),
    ...(o.now ? { now: o.now } : {}),
  });
  cookie = "";
  if (o.ui !== false) {
    const page = await raw(`/ui?k=${KEY}`);
    expect(page.status).toBe(200);
    cookie = String(page.headers["set-cookie"]).split(";")[0];
  }
  return handle;
}

interface Res {
  status: number;
  headers: Record<string, string | string[] | undefined>;
  body: string;
}

/** node:http, so Host and Origin can be anything. */
function raw(path: string, o: { method?: string; headers?: Record<string, string>; body?: string } = {}): Promise<Res> {
  const h = handle as ServerHandle;
  return new Promise((resolve, reject) => {
    const headers = { ...(cookie && !(o.headers && "Cookie" in o.headers) ? { Cookie: cookie } : {}), ...o.headers };
    for (const [name, v] of Object.entries(headers)) if (v === "") delete headers[name];
    const req = httpRequest({ host: "127.0.0.1", port: h.httpPort, path, method: o.method ?? "GET", headers }, (res) => {
      let body = "";
      res.on("data", (d) => (body += d));
      res.on("end", () => resolve({ status: res.statusCode ?? 0, headers: res.headers, body }));
    });
    req.on("error", reject);
    req.end(o.body);
  });
}

const k = (key = KEY) => `?k=${key}`;
const state = async (): Promise<UiState> => {
  const r = await raw(`/ui/state${k()}`);
  expect(r.status).toBe(200);
  return JSON.parse(r.body) as UiState;
};

describe("status page: who may reach it", () => {
  it("serves the page and the state to 127.0.0.1 and localhost with the key, without X-Lotse-Token", async () => {
    const h = await boot();
    for (const host of [`127.0.0.1:${h.httpPort}`, `localhost:${h.httpPort}`]) {
      const page = await raw(`/ui${k()}`, { headers: { Host: host } });
      expect(page.status).toBe(200);
      expect(page.headers["content-type"]).toBe("text/html; charset=utf-8");
      expect(page.body).toBe(UI_PAGE);
      const s = await raw(`/ui/state${k()}`, { headers: { Host: host } });
      expect(s.status).toBe(200);
      expect(s.headers["content-type"]).toBe("application/json");
    }
  });

  it("sends the security headers and never a CORS header", async () => {
    const h = await boot();
    const origin = `http://127.0.0.1:${h.httpPort}`;
    for (const r of [await raw(`/ui${k()}`), await raw(`/ui/state${k()}`), await raw(`/ui/code${k()}`, { method: "POST", headers: { Origin: origin } })]) {
      expect(r.status).toBe(200);
      expect(r.headers["cache-control"]).toBe("no-store");
      expect(r.headers["x-frame-options"]).toBe("DENY");
      expect(r.headers["referrer-policy"]).toBe("no-referrer");
      const csp = String(r.headers["content-security-policy"]);
      expect(csp).toContain("default-src 'none'");
      expect(csp).toContain("connect-src 'self'");
      expect(csp).toContain("img-src data:");
      for (const name of Object.keys(r.headers)) expect(name.startsWith("access-control-")).toBe(false);
    }
  });

  it("answers a wrong or missing key exactly like an unknown path (404)", async () => {
    await boot();
    const unknown = await raw("/nothing-here");
    expect(unknown.status).toBe(404);
    for (const p of ["/ui", "/ui/state", "/ui/code"]) {
      for (const q of ["", "?k=", `?k=${KEY.slice(1)}`, `?k=${KEY}0`, `?k=${KEY.toUpperCase()}`, `?key=${KEY}`]) {
        for (const method of ["GET", "POST"]) {
          const r = await raw(`${p}${q}`, { method, headers: { Cookie: "" } });
          expect(r.status).toBe(404);
          expect(r.body).toBe(unknown.body);
          expect(r.headers["content-security-policy"]).toBeUndefined();
        }
      }
    }
  });

  it("the URL key works once, only to load the page; then the cookie counts, and the key alone gets nothing", async () => {
    const h = await boot();
    const page = await raw(`/ui?k=${KEY}`, { headers: { Cookie: "" } });
    expect(page.status).toBe(404);
    for (const p of ["/ui/state", "/ui/code"]) expect((await raw(`${p}?k=${KEY}`, { method: p === "/ui/code" ? "POST" : "GET", headers: { Cookie: "" } })).status).toBe(404);
    expect((await raw("/ui/state")).status).toBe(200);
    expect((await raw("/ui")).status).toBe(200);
    expect(cookie.startsWith(`marshal_ui_${h.httpPort}=`)).toBe(true);
    for (const bad of [`${cookie}0`, cookie.replace(/=.*/, "=x"), `marshal_ui_${h.httpPort + 1}=${cookie.split("=")[1]}`, `x=${cookie.split("=")[1]}`]) {
      expect((await raw("/ui/state", { headers: { Cookie: bad } })).status).toBe(404);
    }
  });

  it("the cookie is HttpOnly, SameSite=Strict, for /ui only, and set exactly once", async () => {
    await boot();
    const second = await raw("/ui");
    expect(second.headers["set-cookie"]).toBeUndefined();
  });

  it("a key load from a wrong Host or Origin does not spend the key", async () => {
    const h = await boot({ ui: false });
    await handle?.close();
    handle = await bootLive({ ingestToken: TOKEN, ui: { key: KEY, view: () => view() } });
    cookie = "";
    expect((await raw(`/ui?k=${KEY}`, { headers: { Host: `evil.example:${handle.httpPort}` } })).status).toBe(403);
    expect((await raw(`/ui?k=${KEY}`, { headers: { Origin: "https://evil.example" } })).status).toBe(403);
    const ok = await raw(`/ui?k=${KEY}`);
    expect(ok.status).toBe(200);
    expect(String(ok.headers["set-cookie"])).toMatch(/^marshal_ui_\d+=[0-9a-f]{64}; Path=\/ui; HttpOnly; SameSite=Strict$/);
    void h;
  });

  it("refuses any other Host with 403 (DNS rebinding)", async () => {
    const h = await boot();
    for (const host of ["evil.example", `evil.example:${h.httpPort}`, "127.0.0.1", `127.0.0.1:${h.httpPort + 1}`, `[::1]:${h.httpPort}`, `127.0.0.1.nip.io:${h.httpPort}`, `LOCALHOST:${h.httpPort}`]) {
      expect((await raw(`/ui${k()}`, { headers: { Host: host } })).status).toBe(403);
      expect((await raw(`/ui/state${k()}`, { headers: { Host: host } })).status).toBe(403);
      expect((await raw(`/ui/code${k()}`, { method: "POST", headers: { Host: host } })).status).toBe(403);
    }
  });

  it("POST /ui/code: Origin absent or this page's own; anything else is 403 and issues nothing", async () => {
    const replaced = vi.fn();
    const h = await boot({ onCodeReplaced: replaced });
    for (const origin of ["https://evil.example", "null", `http://127.0.0.1:${h.httpPort + 1}`, `https://127.0.0.1:${h.httpPort}`, `http://localhost:${h.httpPort}/`, `http://evil.example:${h.httpPort}`]) {
      expect((await raw(`/ui/code${k()}`, { method: "POST", headers: { Origin: origin } })).status).toBe(403);
    }
    expect(replaced).not.toHaveBeenCalled();
    expect((await state()).code).toBe(CODE);
    for (const headers of [{}, { Origin: `http://127.0.0.1:${h.httpPort}` }, { Origin: `http://localhost:${h.httpPort}`, Host: `localhost:${h.httpPort}` }]) {
      expect((await raw(`/ui/code${k()}`, { method: "POST", headers })).status).toBe(200);
    }
    expect(replaced).toHaveBeenCalledTimes(3);
  });

  it("a GET from another origin is refused too", async () => {
    await boot();
    expect((await raw(`/ui/state${k()}`, { headers: { Origin: "https://evil.example" } })).status).toBe(403);
  });

  it("one method per route (405 with Allow)", async () => {
    await boot();
    const a = await raw(`/ui/state${k()}`, { method: "POST" });
    expect(a.status).toBe(405);
    expect(a.headers.allow).toBe("GET");
    const b = await raw(`/ui/code${k()}`);
    expect(b.status).toBe(405);
    expect(b.headers.allow).toBe("POST");
    expect((await raw(`/ui${k()}`, { method: "DELETE" })).status).toBe(405);
  });

  it("without the ui option the routes do not exist", async () => {
    await boot({ ui: false });
    for (const p of ["/ui", "/ui/state"]) expect((await raw(`${p}${k()}`)).status).toBe(404);
    expect((await raw(`/ui/code${k()}`, { method: "POST" })).status).toBe(404);
  });
});

describe("status page: the hook routes keep their gate", () => {
  const json = { "Content-Type": "application/json" };
  const ev = JSON.stringify({ session_id: "s1", hook_event_name: "PreToolUse", tool_name: "Read" });

  it("the key never stands in for X-Lotse-Token, and Origin/Host checks stay", async () => {
    const h = await boot();
    for (const p of ["/ev", "/perm", "/status"]) {
      expect((await raw(p, { method: "POST", headers: json, body: ev })).status).toBe(401);
      expect((await raw(`${p}${k()}`, { method: "POST", headers: json, body: ev })).status).toBe(401);
      expect((await raw(`${p}${k()}`, { method: "POST", headers: { ...json, "X-Lotse-Token": TOKEN, Origin: `http://127.0.0.1:${h.httpPort}` }, body: ev })).status).toBe(403);
      expect((await raw(p, { method: "POST", headers: { ...json, "X-Lotse-Token": TOKEN, Host: `evil.example:${h.httpPort}` }, body: ev })).status).toBe(403);
    }
    expect((await raw(`/reply?sid=s1&cwd=x&k=${KEY}`)).status).toBe(401);
    expect((await raw("/ev", { method: "POST", headers: { ...json, "X-Lotse-Token": TOKEN }, body: ev })).status).toBe(200);
  });

  it("other paths under /ui stay 404", async () => {
    await boot();
    for (const p of ["/ui/", "/ui/state/", "/ui/x", "/UI", "/ui.html"]) expect((await raw(`${p}${k()}`)).status).toBe(404);
  });
});

describe("status page: state", () => {
  it("has exactly the documented fields and never the token", async () => {
    const h = await boot();
    const r = await raw(`/ui/state${k()}`);
    const s = JSON.parse(r.body) as UiState;
    expect(Object.keys(s).sort()).toEqual(
      ["version", "paired", "code", "codeExpiresAt", "wsPort", "addresses", "qrHost", "qrSvg", "halted", "pendingRequests", "agents", "explain", "tools"].sort(),
    );
    expect(s).toMatchObject({ version: "9.9.9", paired: false, code: CODE, wsPort: h.wsPort, addresses: [HOST, "10.0.0.5"], qrHost: HOST, halted: false, pendingRequests: 0, agents: [], explain: "off", tools: [{ name: "Claude Code", status: "hooks installed" }] });
    expect(typeof s.codeExpiresAt).toBe("number");
    expect(r.body).not.toContain(TOKEN);
    expect(r.body).not.toContain(KEY);
  });

  it("qrSvg is the pairing QR code of exactly pairingPayload(host, wsPort, code)", async () => {
    const h = await boot();
    const s = await state();
    expect(s.qrSvg).toBe(renderQrSvg(encodeQr(pairingPayload(HOST, h.wsPort, CODE))));
    expect(s.qrSvg).toMatch(/^<svg xmlns="http:\/\/www\.w3\.org\/2000\/svg" viewBox="0 0 \d+ \d+"/);
  });

  it("qrSvg is null without a host (or with one that is not an IPv4 address), the code still shows", async () => {
    let v = view({ qrHost: null });
    await boot({ view: () => v });
    expect(await state()).toMatchObject({ code: CODE, qrHost: null, qrSvg: null });
    v = view({ qrHost: "my-pc.local" });
    expect(await state()).toMatchObject({ code: CODE, qrSvg: null });
  });

  it("the code disappears once used; paired, agents and pending requests follow the bridge", async () => {
    const h = await boot();
    const hs = await pairHeadset(h);
    headsets.push(hs);
    let s = await state();
    expect(s).toMatchObject({ paired: true, code: null, codeExpiresAt: null, qrSvg: null });
    const ok = await fetch(`http://127.0.0.1:${h.httpPort}/ev`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Lotse-Token": TOKEN },
      body: JSON.stringify({ session_id: "s1", hook_event_name: "PreToolUse", tool_name: "Read", tool_input: { file_path: "a.ts" }, cwd: "/tmp/p" }),
    });
    expect(ok.status).toBe(200);
    s = await state();
    expect(s.agents).toEqual([{ name: "Agent 1", source: "claude", state: "reading" }]);
    // A permission request held open for the headset.
    const perm = fetch(`http://127.0.0.1:${h.httpPort}/perm`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Lotse-Token": TOKEN },
      body: JSON.stringify({ session_id: "s1", hook_event_name: "PermissionRequest", tool_name: "Bash", tool_input: { command: "ls" }, cwd: "/tmp/p" }),
    });
    const open = await hs.next((m) => m.type === "perm_open");
    expect((await state()).pendingRequests).toBe(1);
    hs.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
    await perm;
    expect((await state()).pendingRequests).toBe(0);
    hs.send({ type: "halt", on: true });
    await hs.next((m) => m.type === "halt_state" && m.on === true);
    expect((await state()).halted).toBe(true);
    hs.send({ type: "halt", on: false });
    await hs.next((m) => m.type === "halt_state" && m.on === false);
  });

  it("an expired code is gone", async () => {
    let t = 1_000_000;
    await boot({ now: () => t });
    expect((await state()).code).toBe(CODE);
    t += 5 * 60_000;
    expect(await state()).toMatchObject({ code: null, codeExpiresAt: null, qrSvg: null });
  });

  it("POST /ui/code replaces the code: the old one stops working, the new one pairs, the terminal hears of it", async () => {
    const replaced = vi.fn();
    const h = await boot({ onCodeReplaced: replaced });
    const r = await raw(`/ui/code${k()}`, { method: "POST" });
    expect(r.status).toBe(200);
    const s = JSON.parse(r.body) as UiState;
    expect(s.code).toMatch(/^\d{6}$/);
    expect(replaced).toHaveBeenCalledWith(s.code);
    expect(s.qrSvg).toBe(renderQrSvg(encodeQr(pairingPayload(HOST, h.wsPort, s.code as string))));
    expect((await state()).code).toBe(s.code);
    if (s.code !== CODE) {
      const old = new WebSocket(`ws://127.0.0.1:${h.wsPort}`);
      await new Promise((res) => old.once("open", res));
      const closed = new Promise<number>((res) => old.on("close", (c) => res(c)));
      old.send(JSON.stringify({ type: "hello", code: CODE }));
      expect(await closed).toBe(4001);
    }
    const ws = new WebSocket(`ws://127.0.0.1:${h.wsPort}`);
    await new Promise((res) => ws.once("open", res));
    const paired = new Promise<void>((res) => ws.on("message", (d) => JSON.parse(d.toString()).type === "paired" && res()));
    ws.send(JSON.stringify({ type: "hello", code: s.code }));
    await paired;
    ws.terminate();
  });
});

describe("status page: the HTML", () => {
  it("is self-contained: nothing loaded from elsewhere, no key in it", () => {
    expect(UI_PAGE.startsWith("<!doctype html>")).toBe(true);
    expect(UI_PAGE).not.toMatch(/<link\b|@import|url\(|\bsrc="http|\bhref="http|<iframe|<object/i);
    expect(UI_PAGE).not.toMatch(/https?:\/\/(?!www\.w3\.org)/);
    expect(UI_PAGE).not.toContain("innerHTML");
    expect(UI_PAGE).toContain("prefers-reduced-motion");
    expect(UI_PAGE).toContain(":focus-visible");
    expect(UI_PAGE).toContain("npx marshal-agents disconnect");
    expect(UI_PAGE).toContain("Marshal");
    expect(UI_PAGE).not.toContain("${");
  });
});

describe("renderQrSvg", () => {
  it("draws exactly the dark modules, inside a white square with the quiet zone", () => {
    const q = encodeQr(pairingPayload(HOST, 47821, "123456"));
    const svg = renderQrSvg(q);
    const n = q.size + 8;
    expect(svg).toContain(`viewBox="0 0 ${n} ${n}"`);
    expect(svg).toContain(`<rect width="${n}" height="${n}" fill="#fff"/>`);
    const grid = Array.from({ length: q.size }, () => new Array<boolean>(q.size).fill(false));
    for (const m of svg.matchAll(/M(\d+) (\d+)h(\d+)v1h-(\d+)z/g)) {
      const [x, y, len, back] = [Number(m[1]), Number(m[2]), Number(m[3]), Number(m[4])];
      expect(back).toBe(len);
      for (let i = 0; i < len; i++) grid[y - 4][x - 4 + i] = true;
    }
    expect(grid).toEqual(q.modules);
  });
});

describe("open-browser", () => {
  const url = "http://127.0.0.1:47820/ui?k=0123456789abcdef0123456789abcdef";
  it("one command per platform", () => {
    expect(browserCommand(url, "win32")).toEqual({ command: "cmd.exe", args: ["/c", "start", '""', `"${url}"`], windowsVerbatimArguments: true });
    expect(browserCommand(url, "darwin")).toEqual({ command: "open", args: [url] });
    expect(browserCommand(url, "linux")).toEqual({ command: "xdg-open", args: [url] });
    expect(browserCommand(url, "freebsd")).toEqual({ command: "xdg-open", args: [url] });
  });
  it("refuses anything but a plain http URL of letters, digits and :/?=.", () => {
    for (const bad of [`${url}&calc`, `${url}" & calc & "`, `${url}|x`, `${url}^`, `${url}%20`, "file:///etc/passwd", "https://127.0.0.1/", "javascript:alert(1)", `${url} x`, ""]) {
      for (const p of ["win32", "darwin", "linux"] as const) expect(browserCommand(bad, p)).toBeNull();
    }
  });
  it("opens only on a terminal, without --no-browser, outside CI", () => {
    expect(shouldOpenBrowser({ noBrowser: false, isTTY: true, env: {} })).toBe(true);
    expect(shouldOpenBrowser({ noBrowser: true, isTTY: true, env: {} })).toBe(false);
    expect(shouldOpenBrowser({ noBrowser: false, isTTY: false, env: {} })).toBe(false);
    expect(shouldOpenBrowser({ noBrowser: false, isTTY: true, env: { CI: "true" } })).toBe(false);
    expect(shouldOpenBrowser({ noBrowser: false, isTTY: true, env: { CI: "1" } })).toBe(false);
  });
});

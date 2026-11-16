// Halt does not fail open (rulings task-p2-fix-1.md section B, review I3): the server refuses a pre-tool event and
// a permission request from the headers alone, and a hook that times out against a slow bridge looks for the
// halt marker `~/.lotse/halt` (here always in a temporary directory, never the real home).
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { createServer, request, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import { afterEach, beforeAll, describe, expect, inject, it } from "vitest";
import { HALT_MESSAGE } from "../src/halt.js";
import { haltMarkerLive } from "../src/halt-marker.js";
import { TIMEOUT, UNREACHABLE } from "../src/hook-fallback.js";
import { runShim } from "../src/hook-shim.js";
import type { ServerHandle } from "../src/server.js";
import { bootLive, pairHeadset } from "./live-helpers.js";
import type { Headset } from "./live-helpers.js";
import { tempDir } from "./tmp.js";

const fixture = (name: string) => readFileSync(join(import.meta.dirname, "fixtures", "agents", name), "utf8");
const PRE_TOOL_HALT = { hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: HALT_MESSAGE } };
const HALT_DENY = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message: HALT_MESSAGE } } };
const TOKEN = "f".repeat(64);

let handle: ServerHandle | undefined;
let headset: Headset | undefined;
afterEach(async () => {
  headset?.close();
  headset = undefined;
  await handle?.close();
  handle = undefined;
});

async function halted(over: Parameters<typeof bootLive>[0] = {}): Promise<ServerHandle> {
  handle = await bootLive({ ingestToken: TOKEN, log: () => {}, ...over });
  headset = await pairHeadset(handle);
  headset.send({ type: "halt", on: true });
  await headset.next((m) => m.type === "halt_state" && m.on === true);
  return handle;
}

/**
 * POSTs headers plus only the first `sent` bytes of a body announced as `length` bytes, never finishing it.
 * Resolves the status and JSON of a reply that arrives anyway, or null after `ms`.
 */
function partialPost(h: ServerHandle, path: string, headers: Record<string, string>, length: number, sent: number, ms = 2000): Promise<{ status: number; body: unknown } | null> {
  return new Promise((resolve) => {
    const req = request({ host: "127.0.0.1", port: h.httpPort, path, method: "POST", headers: { "Content-Type": "application/json", "X-Lotse-Token": TOKEN, "Content-Length": String(length), ...headers } }, (res) => {
      let text = "";
      res.setEncoding("utf8");
      res.on("data", (c: string) => (text += c));
      res.on("end", () => {
        clearTimeout(timer);
        req.destroy();
        try {
          resolve({ status: res.statusCode ?? 0, body: JSON.parse(text) });
        } catch {
          resolve({ status: res.statusCode ?? 0, body: text });
        }
      });
    });
    const timer = setTimeout(() => {
      req.destroy();
      resolve(null);
    }, ms);
    req.on("error", () => {});
    req.write("x".repeat(sent));
  });
}

const bigBody = (n: number) => JSON.stringify({ session_id: "c1", hook_event_name: "PreToolUse", cwd: "/tmp/shop", tool_name: "Write", tool_input: { file_path: "/tmp/shop/a", content: "x".repeat(n) } });

describe("server: refusal from the headers alone while halted", () => {
  it("a pre-tool /ev (X-Lotse-Hook: PreToolUse) is refused before its body arrives", async () => {
    const h = await halted();
    expect(await partialPost(h, "/ev", { "X-Lotse-Hook": "PreToolUse" }, 2 * 1024 * 1024, 64 * 1024)).toEqual({ status: 200, body: PRE_TOOL_HALT });
  });

  it("a /perm is refused before its body arrives", async () => {
    const h = await halted();
    expect(await partialPost(h, "/perm", {}, 2 * 1024 * 1024, 64 * 1024)).toEqual({ status: 200, body: HALT_DENY });
  });

  it("a complete 2 MB pre-tool body gets the refusal, not a 413 that lets the tool run; with or without the header", async () => {
    const h = await halted();
    const body = bigBody(2 * 1024 * 1024);
    for (const extra of [{ "X-Lotse-Hook": "PreToolUse" }, {}]) {
      const r = await fetch(`http://127.0.0.1:${h.httpPort}/ev`, { method: "POST", headers: { "Content-Type": "application/json", "X-Lotse-Token": TOKEN, ...extra }, body });
      expect(r.status).toBe(200);
      expect(await r.json()).toEqual(PRE_TOOL_HALT);
    }
    const bad = await fetch(`http://127.0.0.1:${h.httpPort}/ev`, { method: "POST", headers: { "Content-Type": "application/json", "X-Lotse-Token": TOKEN }, body: "{not json" });
    expect(await bad.json()).toEqual(PRE_TOOL_HALT);
  });

  it("not halted: the header changes nothing, an oversized body is still 413, a wrong token still 401", async () => {
    handle = await bootLive({ ingestToken: TOKEN, log: () => {} });
    const ev = (headers: Record<string, string>, body: string) => fetch(`http://127.0.0.1:${handle!.httpPort}/ev`, { method: "POST", headers: { "Content-Type": "application/json", "X-Lotse-Token": TOKEN, ...headers }, body });
    expect(await (await ev({ "X-Lotse-Hook": "PreToolUse" }, bigBody(10))).json()).toEqual({});
    expect((await ev({ "X-Lotse-Hook": "PreToolUse" }, bigBody(2 * 1024 * 1024))).status).toBe(413);
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state" && m.on === true);
    expect((await ev({ "X-Lotse-Hook": "PreToolUse", "X-Lotse-Token": "wrong" }, bigBody(10))).status).toBe(401);
  });
});

describe("the halt marker file (bridge side)", () => {
  it("is written while halted (pid and start time), removed on release and on a clean shutdown", async () => {
    const marker = join(tempDir("lotse-halt-"), ".lotse", "halt");
    handle = await bootLive({ ingestToken: TOKEN, log: () => {}, haltMarker: marker });
    expect(existsSync(marker)).toBe(false);
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state" && m.on === true);
    const info = JSON.parse(readFileSync(marker, "utf8"));
    expect(info.pid).toBe(process.pid);
    expect(typeof info.start).toBe("number");
    expect(info.start).toBeLessThanOrEqual(Date.now());
    headset.send({ type: "halt", on: false });
    await headset.next((m) => m.type === "halt_state" && m.on === false);
    expect(existsSync(marker)).toBe(false);
    headset.send({ type: "halt", on: true });
    await headset.next(() => headset!.msgs.filter((m) => m.type === "halt_state" && m.on === true).length === 2);
    expect(existsSync(marker)).toBe(true);
    headset.close();
    headset = undefined;
    await handle.close();
    handle = undefined;
    expect(existsSync(marker)).toBe(false);
  });

  it("a stale marker overwritten on halt; one of a dead process is removed at start", async () => {
    const dir = join(tempDir("lotse-halt-"), ".lotse");
    mkdirSync(dir, { recursive: true });
    const marker = join(dir, "halt");
    writeFileSync(marker, JSON.stringify({ pid: await deadPid(), start: 1 }));
    handle = await bootLive({ ingestToken: TOKEN, log: () => {}, haltMarker: marker });
    expect(existsSync(marker)).toBe(false);
    writeFileSync(marker, "garbage");
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state" && m.on === true);
    expect(JSON.parse(readFileSync(marker, "utf8")).pid).toBe(process.pid);
  });
});

/** The pid of a process that has exited. */
async function deadPid(): Promise<number> {
  const child = spawn(process.execPath, ["-e", ""], { stdio: "ignore" });
  await new Promise((r) => child.on("exit", r));
  return child.pid as number;
}

describe("the halt marker file (hook side)", () => {
  it("live only when it is a small regular file naming a running process", async () => {
    const dir = join(tempDir("lotse-halt-"), ".lotse");
    mkdirSync(dir, { recursive: true });
    const marker = join(dir, "halt");
    expect(haltMarkerLive(marker)).toBe(false);
    writeFileSync(marker, JSON.stringify({ pid: process.pid, start: Date.now() }));
    expect(haltMarkerLive(marker)).toBe(true);
    writeFileSync(marker, JSON.stringify({ pid: await deadPid(), start: Date.now() }));
    expect(haltMarkerLive(marker)).toBe(false);
    for (const bad of ["", "{", "[]", '{"pid":"1"}', '{"pid":0}', '{"pid":-5}', '{"pid":1.5}', JSON.stringify({ pid: process.pid, start: 1, pad: "x".repeat(600) })]) {
      writeFileSync(marker, bad);
      expect(haltMarkerLive(marker), bad.slice(0, 20)).toBe(false);
    }
  });

  it("a marker that is a link, or a ~/.lotse that is a link or junction, is not followed", () => {
    const root = tempDir("lotse-halt-");
    const real = join(root, "real");
    mkdirSync(real);
    writeFileSync(join(real, "halt"), JSON.stringify({ pid: process.pid, start: 1 }));
    try {
      symlinkSync(real, join(root, ".lotse"), process.platform === "win32" ? "junction" : "dir");
    } catch {
      return;
    }
    expect(haltMarkerLive(join(root, ".lotse", "halt"))).toBe(false);
  });

  it("shim: a timeout with a live marker refuses (pre-tool and permission); a dead marker does not", async () => {
    const pre = fixture("claude-pre-tool-use-read.json");
    const perm = fixture("claude-permission-request-bash.json");
    const timeout = async () => TIMEOUT;
    expect(await runShim("claude", pre, timeout, { evMs: 400, permMs: 1000, haltMarker: () => true })).toBe(JSON.stringify(PRE_TOOL_HALT));
    expect(await runShim("gemini", fixture("gemini-before-tool-read.json"), timeout, { evMs: 400, permMs: 1000, haltMarker: () => true })).toBe(JSON.stringify({ decision: "deny", reason: HALT_MESSAGE }));
    expect(await runShim("claude", perm, timeout, { evMs: 400, permMs: 1000, haltMarker: () => true })).toBe(JSON.stringify(HALT_DENY));
    expect(await runShim("claude", pre, timeout, { evMs: 400, permMs: 1000, haltMarker: () => false })).toBe("");
    expect(await runShim("claude", perm, timeout, { evMs: 400, permMs: 1000, haltMarker: () => false })).toBe("");
    // the budget already spent before the post counts as a timeout
    expect(await runShim("claude", pre, async () => ({}), { evMs: 0, permMs: 1000, haltMarker: () => true })).toBe(JSON.stringify(PRE_TOOL_HALT));
  });

  it("shim: connection refused, any answer, or a non-pre-tool event never looks at the marker", async () => {
    let looked = 0;
    const marker = () => (looked++, true);
    // (undefined, a non-200 or unreadable answer, is no answer: it does look; halt-silent.test.ts)
    for (const reply of [UNREACHABLE, {}, { x: 1 }]) {
      expect(await runShim("claude", fixture("claude-pre-tool-use-read.json"), async () => reply, { evMs: 400, permMs: 1000, haltMarker: marker })).toBe("");
      await runShim("claude", fixture("claude-permission-request-bash.json"), async () => reply, { evMs: 400, permMs: 1000, haltMarker: marker });
    }
    await runShim("claude", fixture("claude-session-start.json"), async () => TIMEOUT, { evMs: 400, permMs: 1000, haltMarker: marker });
    expect(looked).toBe(0);
  });

  it("shim: a pre-tool post carries X-Lotse-Hook: PreToolUse; other events do not", async () => {
    const seen: [string, Record<string, string> | undefined][] = [];
    const post = async (path: string, _b: string, _ms: number, headers?: Record<string, string>) => (seen.push([path, headers]), {});
    await runShim("claude", fixture("claude-pre-tool-use-read.json"), post, { evMs: 400, permMs: 1000 });
    await runShim("gemini", fixture("gemini-before-tool-read.json"), post, { evMs: 400, permMs: 1000 });
    await runShim("claude", fixture("claude-session-start.json"), post, { evMs: 400, permMs: 1000 });
    expect(seen).toEqual([
      ["/ev", { "X-Lotse-Hook": "PreToolUse" }],
      ["/ev", { "X-Lotse-Hook": "PreToolUse" }],
      ["/ev", undefined],
    ]);
  });
});

// ---- spawn level: the real `lotse hook` process against a bridge that never answers ----

describe("lotse hook against a slow bridge (spawned)", () => {
  let out: string;
  beforeAll(() => {
    out = inject("cliBuild");
  });

  async function hangingBridge(): Promise<{ port: number; close: () => Promise<void> }> {
    const s: Server = createServer(() => {
      /* never answers */
    });
    await new Promise<void>((r) => s.listen(0, "127.0.0.1", () => r()));
    return { port: (s.address() as AddressInfo).port, close: () => new Promise((r) => { s.closeAllConnections(); s.close(() => r()); }) };
  }

  async function closedPort(): Promise<number> {
    const s = createServer();
    await new Promise<void>((r) => s.listen(0, "127.0.0.1", () => r()));
    const p = (s.address() as AddressInfo).port;
    await new Promise((r) => s.close(r));
    return p;
  }

  function hook(port: number, home: string, stdin: string): Promise<string> {
    const env: NodeJS.ProcessEnv = { ...process.env, LOTSE_TOKEN: TOKEN, LOTSE_HTTP_PORT: String(port), HOME: home, USERPROFILE: home };
    delete env.MUSE_SESSION_ID;
    const child = spawn(process.execPath, [join(out, "cli.js"), "hook", "--source", "claude"], { env, stdio: ["pipe", "pipe", "pipe"] });
    let stdout = "";
    child.stdout.on("data", (c) => (stdout += c));
    child.stdin.end(stdin);
    return new Promise((r) => child.on("close", () => r(stdout.trim())));
  }

  function homeWithMarker(pid: number): string {
    const home = tempDir("lotse-hook-home-");
    mkdirSync(join(home, ".lotse"));
    writeFileSync(join(home, ".lotse", "halt"), JSON.stringify({ pid, start: Date.now() }));
    return home;
  }

  it("a timeout with a live marker refuses; with a dead one it prints nothing; connection refused ignores a live marker", async () => {
    const bridge = await hangingBridge();
    try {
      const pre = fixture("claude-pre-tool-use-read.json");
      expect(await hook(bridge.port, homeWithMarker(process.pid), pre)).toBe(JSON.stringify(PRE_TOOL_HALT));
      expect(await hook(bridge.port, homeWithMarker(await deadPid()), pre)).toBe("");
      expect(await hook(await closedPort(), homeWithMarker(process.pid), pre)).toBe("");
    } finally {
      await bridge.close();
    }
  }, 30_000);
});

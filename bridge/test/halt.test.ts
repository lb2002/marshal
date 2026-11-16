// Halt-all (contract 2026-11-12-contract-impact-halt.md, part B): one switch stops every agent before its next step.
import { readFileSync } from "node:fs";
import { PassThrough } from "node:stream";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { WebSocket } from "ws";
import { PermissionBroker } from "../src/broker.js";
import { ENFORCEMENT, HALT_MESSAGE } from "../src/halt.js";
import { runShim } from "../src/hook-shim.js";
import type { ShimSource } from "../src/adapters.js";
import { parseClientMessage } from "../src/protocol.js";
import type { ServerHandle } from "../src/server.js";
import { hookOutputs } from "./hook-baseline.js";
import { bootLive, pairHeadset, settle } from "./live-helpers.js";
import type { Headset, Msg } from "./live-helpers.js";

const FIX = join(import.meta.dirname, "fixtures");
const fixture = (name: string) => readFileSync(join(FIX, "agents", name), "utf8");
const HALT_DENY = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message: HALT_MESSAGE } } };
const PRE_TOOL_HALT = { hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: HALT_MESSAGE } };

describe("the halt message and the enforcement table", () => {
  it("uses the contract's sentence", () => {
    expect(HALT_MESSAGE).toBe("The user has halted all agents. Run nothing further and wait for their instruction.");
  });
  it("claims every-step only where a pre-tool hook is asked before each tool call and can refuse", () => {
    expect(ENFORCEMENT).toEqual({ claude: "every-step", codex: "every-step", gemini: "every-step", muse: "requests-only", opencode: "every-step" });
  });
  it("parses the client message strictly", () => {
    expect(parseClientMessage(JSON.stringify({ type: "halt", on: true }))).toEqual({ type: "halt", on: true });
    expect(parseClientMessage(JSON.stringify({ type: "halt", on: false }))).toEqual({ type: "halt", on: false });
    for (const on of ["true", 1, null, undefined]) expect(parseClientMessage(JSON.stringify({ type: "halt", on }))).toBeNull();
  });
});

// ---- broker ----

describe("broker while halted", () => {
  const mk = (hasClient = true) => new PermissionBroker({ holdMs: 90_000, hasClient: () => hasClient });

  it("closes every open request with the halt deny (reason halt)", async () => {
    const b = mk();
    const closed: [string, string][] = [];
    b.onClose((id, reason) => closed.push([id, reason]));
    const a = b.open("s1", "Bash", { command: "rm -rf x" }, "/p");
    const c = b.open("s2", "Read", { file_path: "/p/a" }, "/p");
    b.halt(HALT_MESSAGE);
    expect(await a.answer).toEqual(HALT_DENY);
    expect(await c.answer).toEqual(HALT_DENY);
    expect(closed).toEqual([[a.request.id, "halt"], [c.request.id, "halt"]]);
    expect(b.pending()).toEqual([]);
  });

  it("answers a new request with the deny at once, opens nothing, with or without a headset", async () => {
    for (const hasClient of [true, false]) {
      const b = mk(hasClient);
      const opened: string[] = [];
      b.onOpen((r) => opened.push(r.id));
      b.halt(HALT_MESSAGE);
      const r = b.open("s1", "Read", { file_path: "/p/a" }, "/p");
      expect(await r.answer).toEqual(HALT_DENY);
      expect(opened).toEqual([]);
      expect(b.pending()).toEqual([]);
    }
  });

  it("a decision for a request the halt closed is rejected; no allow while halted", () => {
    const b = mk();
    const a = b.open("s1", "Read", { file_path: "/p/a" }, "/p");
    b.halt(HALT_MESSAGE);
    expect(b.decide({ id: a.request.id, behavior: "allow", confirm: "two-hand" }).ok).toBe(false);
    expect(b.decide({ id: a.request.id, behavior: "deny" }).ok).toBe(false);
  });

  it("after resume, requests open and decide as before", async () => {
    const b = mk();
    b.halt(HALT_MESSAGE);
    b.resume();
    const a = b.open("s1", "Read", { file_path: "/p/a" }, "/p");
    expect(b.pending()).toHaveLength(1);
    expect(b.decide({ id: a.request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: true });
    expect(await a.answer).toEqual({ hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "allow" } } });
  });
});

// ---- shim ----

describe("pre-tool hooks while halted (shim)", () => {
  const cases: [ShimSource, string, string][] = [
    ["claude", "claude-pre-tool-use-read.json", JSON.stringify(PRE_TOOL_HALT)],
    ["codex", "codex-pre-tool-use-bash.json", JSON.stringify(PRE_TOOL_HALT)],
    ["muse", "muse-pre-tool-use-shell.json", JSON.stringify(PRE_TOOL_HALT)],
    ["gemini", "gemini-before-tool-read.json", JSON.stringify({ decision: "deny", reason: HALT_MESSAGE })],
  ];
  for (const [source, file, want] of cases) {
    it(`${source}: refuses with the halt message, at once`, async () => {
      const posted: string[] = [];
      const post = async (path: string) => {
        posted.push(path);
        return PRE_TOOL_HALT;
      };
      for (const platform of ["win32", "linux"] as const) {
        const out = await runShim(source, fixture(file), post, { evMs: 400, permMs: 100_000, platform, approval: source === "gemini" });
        expect(out, platform).toBe(want);
      }
      expect(posted.every((p) => p === "/ev")).toBe(true);
    });
  }

  it("a non-pre-tool event or a PermissionRequest-shaped /ev reply never refuses", async () => {
    expect(await runShim("claude", fixture("claude-session-start.json"), async () => PRE_TOOL_HALT, { evMs: 400, permMs: 1000 })).toBe("");
    expect(await runShim("claude", fixture("claude-pre-tool-use-read.json"), async () => HALT_DENY, { evMs: 400, permMs: 1000 })).toBe("");
  });

  it("nothing changes when not halted: every hook output equals the one captured before this task", async () => {
    const before = JSON.parse(readFileSync(join(FIX, "hook-output-baseline.json"), "utf8"));
    expect(await hookOutputs()).toEqual(before);
  });
});

// ---- live server ----

let handle: ServerHandle | undefined;
let headset: Headset | undefined;
afterEach(async () => {
  headset?.close();
  headset = undefined;
  await handle?.close();
  handle = undefined;
});

const TOKEN = "t".repeat(64);
const perm = (command: string, sid = "c1", extra: Record<string, unknown> = {}) => ({ session_id: sid, hook_event_name: "PermissionRequest", cwd: "/tmp/shop", tool_name: "Bash", tool_input: { command }, ...extra });
const pre = (sid = "c1", extra: Record<string, unknown> = {}) => ({ session_id: sid, hook_event_name: "PreToolUse", cwd: "/tmp/shop", tool_name: "Read", tool_input: { file_path: "/tmp/shop/a.ts" }, ...extra });
const post = (h: ServerHandle, path: string, body: unknown, token: string | null = TOKEN) =>
  fetch(`http://127.0.0.1:${h.httpPort}${path}`, { method: "POST", headers: { "Content-Type": "application/json", ...(token ? { "X-Lotse-Token": token } : {}) }, body: JSON.stringify(body) });

async function reconnect(h: ServerHandle, session: string): Promise<{ ws: WebSocket; snapshot: Msg }> {
  const ws = new WebSocket(`ws://127.0.0.1:${h.wsPort}`);
  const snapshot = await new Promise<Msg>((res, rej) => {
    ws.on("message", (d) => {
      const m = JSON.parse(d.toString()) as Msg;
      if (m.type === "snapshot") res(m);
    });
    ws.once("open", () => ws.send(JSON.stringify({ type: "hello", session })));
    ws.once("error", rej);
    setTimeout(() => rej(new Error("no snapshot")), 3000);
  });
  return { ws, snapshot };
}

describe("live server: halt-all", () => {
  it("halt from the headset: halt_state, open requests denied with the message (perm_closed halt)", async () => {
    const lines: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, log: (l) => lines.push(l) });
    headset = await pairHeadset(handle);
    await post(handle, "/ev", { session_id: "c1", hook_event_name: "SessionStart", cwd: "/tmp/shop" });
    await post(handle, "/ev", { session_id: "m1", hook_event_name: "SessionStart", cwd: "/tmp/shop", lotse_source: "muse" });
    const pending = post(handle, "/perm", perm("rm -rf build"));
    const open = await headset.next((m) => m.type === "perm_open");
    const t0 = Date.now();
    headset.send({ type: "halt", on: true });
    const st = await headset.next((m) => m.type === "halt_state");
    expect(st).toMatchObject({ type: "halt_state", on: true, enforcement: { claude: "every-step", muse: "requests-only" } });
    expect(st.since).toBeGreaterThanOrEqual(t0);
    const closed = await headset.next((m) => m.type === "perm_closed");
    expect(closed).toEqual({ type: "perm_closed", id: open.request.id, reason: "halt" });
    expect(await (await pending).json()).toEqual(HALT_DENY);
    // a decision for the request the halt closed is rejected
    headset.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "allow", confirm: "two-hand" } });
    expect(await headset.next((m) => m.type === "error")).toEqual({ type: "error", error: "halted" });
    expect(lines).toContain("Halted. To go on: press Continue in the headset, or restart the bridge.");
  });

  it("while halted: a new request is denied at once and not opened; pre-tool hooks get the refusal; state keeps flowing", async () => {
    handle = await bootLive({ ingestToken: TOKEN, log: () => {} });
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state");
    const t0 = Date.now();
    const r = await post(handle, "/perm", perm("ls"));
    expect(await r.json()).toEqual(HALT_DENY);
    expect(Date.now() - t0).toBeLessThan(1000);
    // malformed and oversized permission bodies are denied too (never "no decision")
    const bad = await fetch(`http://127.0.0.1:${handle.httpPort}/perm`, { method: "POST", headers: { "Content-Type": "application/json", "X-Lotse-Token": TOKEN }, body: "{not json" });
    expect(await bad.json()).toEqual(HALT_DENY);
    expect(await (await post(handle, "/ev", pre())).json()).toEqual(PRE_TOOL_HALT);
    expect(await (await post(handle, "/ev", { session_id: "c1", hook_event_name: "PostToolUse", cwd: "/tmp/shop", tool_name: "Read" })).json()).toEqual({});
    await post(handle, "/ev", { session_id: "c1", hook_event_name: "Stop", cwd: "/tmp/shop", last_assistant_message: "ok" });
    await headset.next((m) => m.type === "agent" && m.agent.status === "done");
    await settle(headset);
    expect(headset.msgs.some((m) => m.type === "perm_open")).toBe(false);
  });

  it("not halted: /ev answers {} for a pre-tool hook, exactly as before", async () => {
    handle = await bootLive({ ingestToken: TOKEN });
    expect(await (await post(handle, "/ev", pre())).json()).toEqual({});
  });

  it("survives a headset disconnect, and a reconnecting headset sees it in the snapshot; the reminder runs", async () => {
    const lines: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, log: (l) => lines.push(l), haltReminderMs: 40 });
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state");
    const session = (headset.msgs.find((m) => m.type === "paired") as Msg).session as string;
    headset.close();
    headset = undefined;
    await new Promise((r) => setTimeout(r, 150));
    expect(lines.filter((l) => l === "Halted. To go on: press Continue in the headset, or restart the bridge.").length).toBeGreaterThanOrEqual(2);
    expect(await (await post(handle, "/perm", perm("ls"))).json()).toEqual(HALT_DENY);
    const { ws, snapshot } = await reconnect(handle, session);
    expect(snapshot.halt).toMatchObject({ on: true });
    expect(typeof snapshot.halt.since).toBe("number");
    const count = lines.length;
    await new Promise((r) => setTimeout(r, 150));
    // no reminder while a headset is connected
    expect(lines.slice(count).filter((l) => l.startsWith("Angehalten."))).toEqual([]);
    ws.terminate();
  });

  it("release from the headset", async () => {
    const lines: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, log: (l) => lines.push(l) });
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state" && m.on === true);
    headset.send({ type: "halt", on: false });
    const off = await headset.next((m) => m.type === "halt_state" && m.on === false);
    expect("since" in off).toBe(false);
    void post(handle, "/perm", perm("ls"));
    await headset.next((m) => m.type === "perm_open");
    expect(lines.length).toBe(2);
  });

  it("no HTTP route lifts the halt: POST /resume with a valid token is 404, and every route leaves it on", async () => {
    handle = await bootLive({ ingestToken: TOKEN, log: () => {} });
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state" && m.on === true);
    expect((await post(handle, "/resume", {})).status).toBe(404);
    const bodies = [{}, { type: "halt", on: false }, { on: false }, { halt: false }, { session_id: "c1", hook_event_name: "Stop", cwd: "/tmp" }];
    for (const path of ["/ev", "/perm", "/status", "/resume", "/halt", "/unhalt", "/", "/reply"]) {
      for (const body of bodies) await post(handle, path, body).then((r) => r.text());
      await fetch(`http://127.0.0.1:${handle.httpPort}${path}?on=false&sid=nobody&cwd=/tmp`, { headers: { "X-Lotse-Token": TOKEN } }).then((r) => r.text()).catch(() => "");
    }
    await settle(headset);
    expect(headset.msgs.filter((m) => m.type === "halt_state" && m.on === false)).toEqual([]);
    expect(await (await post(handle, "/perm", perm("ls"))).json()).toEqual(HALT_DENY);
  });

  it("the console: 'continue' (or 'weiter') + Enter on a TTY releases; other input does not; nothing is read when not halted", async () => {
    const input = new PassThrough();
    const lines: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, log: (l) => lines.push(l), console: { input, isTTY: true } });
    headset = await pairHeadset(handle);
    expect(input.listenerCount("data")).toBe(0); // not halted: not read
    input.write("weiter\n");
    input.write("continue\n");
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state" && m.on === true);
    expect(lines).toContain("Halted. To go on: press Continue in the headset, or type continue here and press Enter.");
    expect(input.listenerCount("data")).toBe(1);
    for (const junk of ["weiter bitte\n", "Weiter\n", "Continue\n", "continue please\n", "resume\n", "wei", "ter-\n", "\n"]) input.write(junk);
    await new Promise((r) => setTimeout(r, 30));
    expect(await (await post(handle, "/perm", perm("ls"))).json()).toEqual(HALT_DENY);
    input.write("  weiter  \r\n");
    await headset.next((m) => m.type === "halt_state" && m.on === false);
    expect(lines.at(-1)).toBe("Marshal: halt released (console).");
    expect(input.listenerCount("data")).toBe(0);
    // the English word releases a second halt the same way
    const count = (on: boolean) => headset!.msgs.filter((m) => m.type === "halt_state" && m.on === on).length;
    const ons = count(true);
    const offs = count(false);
    headset.send({ type: "halt", on: true });
    await headset.next(() => count(true) > ons);
    expect(input.listenerCount("data")).toBe(1);
    input.write(" continue \r\n");
    await headset.next(() => count(false) > offs);
    expect(lines.at(-1)).toBe("Marshal: halt released (console).");
    expect(input.listenerCount("data")).toBe(0);
  });

  it("the console: a stdin that is not a TTY is never read, and the line says so", async () => {
    const input = new PassThrough();
    const lines: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, log: (l) => lines.push(l), console: { input, isTTY: false }, haltReminderMs: 30 });
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state" && m.on === true);
    expect(input.listenerCount("data")).toBe(0);
    input.write("weiter\n");
    headset.close();
    headset = undefined;
    await new Promise((r) => setTimeout(r, 120));
    expect(lines.filter((l) => l === "Halted. To go on: press Continue in the headset, or restart the bridge.").length).toBeGreaterThanOrEqual(2);
    expect(await (await post(handle, "/perm", perm("ls"))).json()).toEqual(HALT_DENY);
  });

  it("a bridge restart releases the halt (the state lives only in memory)", async () => {
    handle = await bootLive({ ingestToken: TOKEN, log: () => {} });
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state" && m.on === true);
    headset.close();
    await handle.close();
    handle = await bootLive({ ingestToken: TOKEN, log: () => {} });
    headset = await pairHeadset(handle);
    const snap = headset.msgs.find((m) => m.type === "snapshot") as Msg;
    expect(snap.halt).toEqual({ on: false, enforcement: {} });
  });

  it("halt from an unpaired socket is refused", async () => {
    handle = await bootLive({ ingestToken: TOKEN, log: () => {} });
    headset = await pairHeadset(handle);
    const ws = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`);
    const got: Msg[] = [];
    ws.on("message", (d) => got.push(JSON.parse(d.toString())));
    await new Promise((r) => ws.once("open", r));
    ws.send(JSON.stringify({ type: "halt", on: true }));
    await new Promise((r) => setTimeout(r, 100));
    expect(got).toContainEqual({ type: "error", error: "not-paired" });
    ws.terminate();
    await settle(headset);
    expect(headset.msgs.some((m) => m.type === "halt_state")).toBe(false);
    void post(handle, "/perm", perm("ls"));
    await headset.next((m) => m.type === "perm_open");
  });

  it("while halted, a source that starts a session updates enforcement", async () => {
    handle = await bootLive({ ingestToken: TOKEN, log: () => {} });
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state" && m.on === true);
    await post(handle, "/ev", { session_id: "g1", hook_event_name: "SessionStart", cwd: "/tmp/shop", lotse_source: "gemini" });
    const st = await headset.next((m) => m.type === "halt_state" && m.enforcement.gemini !== undefined);
    expect(st.enforcement).toEqual({ gemini: "every-step" });
  });
});

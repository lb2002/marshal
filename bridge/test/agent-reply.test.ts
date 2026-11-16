import { afterEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { request } from "node:http";
import { parseClientMessage } from "../src/protocol.js";
import { ReplyHub, ReplySessions, cleanReply, MAX_REPLY_TEXT } from "../src/replies.js";
import { Recorder } from "../src/recorder.js";
import type { ServerHandle, ServerOptions } from "../src/server.js";
import { bootLive, pairHeadset, settle, type Headset } from "./live-helpers.js";
import { tempDir } from "./tmp.js";

describe("cleanReply and the agent_reply message", () => {
  it("keeps newlines, turns CRLF into a newline, strips other control characters and trims", () => {
    expect(cleanReply("  Bitte\r\nnur die Preise.\u0007\t ")).toBe("Bitte\nnur die Preise.");
    expect(cleanReply("a\u0000b\u001bc\u009fd")).toBe("abcd");
  });
  it("accepts at most 1000 characters and nothing empty", () => {
    expect(MAX_REPLY_TEXT).toBe(1000);
    expect(parseClientMessage(JSON.stringify({ type: "agent_reply", sid: "s", text: "ä".repeat(1000) }))).toEqual({ type: "agent_reply", sid: "s", text: "ä".repeat(1000) });
    for (const bad of [
      { type: "agent_reply", sid: "s", text: "x".repeat(1001) },
      { type: "agent_reply", sid: "s", text: " \u0007 " },
      { type: "agent_reply", sid: "s" },
      { type: "agent_reply", sid: "", text: "x" },
      { type: "agent_reply", sid: "x".repeat(201), text: "x" },
      { type: "agent_reply", sid: "s", text: 5 },
    ]) {
      expect(parseClientMessage(JSON.stringify(bad)), JSON.stringify(bad).slice(0, 60)).toBeNull();
    }
    expect(parseClientMessage(JSON.stringify({ type: "agent_reply", sid: "s", text: "Hallo\r\nWelt\u0007" }))).toEqual({ type: "agent_reply", sid: "s", text: "Hallo\nWelt" });
  });
});

/** A writer that records what it was given and reports `ok`. */
function writer(ok = true): { write: (t: string | null) => Promise<boolean>; got: (string | null)[] } {
  const got: (string | null)[] = [];
  return { got, write: async (t) => (got.push(t), ok) };
}

describe("ReplyHub", () => {
  it("hands a reply to the waiting listener; delivered only when the write succeeded", async () => {
    const hub = new ReplyHub({ waitMs: 5000 });
    expect(await hub.deliver("s", "x")).toBe(false);
    const w = writer();
    expect(hub.listen("s", "c:/w", w.write)).toBe("ok");
    expect(hub.waiting("s")).toBe(true);
    expect(await hub.deliver("s", "Bitte nur die Preise")).toBe(true);
    expect(w.got).toEqual(["Bitte nur die Preise"]);
    expect(hub.waiting("s")).toBe(false);
    const failing = writer(false);
    hub.listen("s", "c:/w", failing.write);
    expect(await hub.deliver("s", "x")).toBe(false);
    const throwing = async () => {
      throw new Error("socket gone");
    };
    hub.listen("s", "c:/w", throwing);
    expect(await hub.deliver("s", "x")).toBe(false);
  });
  it("a listener with the same key replaces the old one (which gets nothing); another key is a conflict", async () => {
    const hub = new ReplyHub({ waitMs: 5000 });
    const a = writer();
    const b = writer();
    const c = writer();
    hub.listen("s", "c:/w", a.write);
    expect(hub.listen("s", "c:/other", c.write)).toBe("conflict");
    expect(hub.listen("s", "c:/w", b.write)).toBe("ok");
    expect(a.got).toEqual([null]);
    expect(await hub.deliver("s", "x")).toBe(true);
    expect(b.got).toEqual(["x"]);
    expect(c.got).toEqual([]);
  });
  it("at most `max` listeners in all; replacing one is still allowed at the cap", () => {
    const hub = new ReplyHub({ waitMs: 5000, max: 2 });
    expect(hub.listen("a", "k", writer().write)).toBe("ok");
    expect(hub.listen("b", "k", writer().write)).toBe("ok");
    expect(hub.listen("c", "k", writer().write)).toBe("full");
    expect(hub.listen("a", "k", writer().write)).toBe("ok");
    expect(hub.size()).toBe(2);
    hub.closeAll();
  });
  it("times out with nothing; release, closeAll and cancel end listeners", async () => {
    const hub = new ReplyHub({ waitMs: 50 });
    const t = writer();
    hub.listen("s", "k", t.write);
    await new Promise((r) => setTimeout(r, 120));
    expect(t.got).toEqual([null]);
    expect(hub.waiting("s")).toBe(false);
    const long = new ReplyHub({ waitMs: 60_000 });
    const a = writer();
    const b = writer();
    long.listen("a", "k", a.write);
    long.listen("b", "k", b.write);
    long.release("a");
    expect(a.got).toEqual([null]);
    long.closeAll();
    expect(b.got).toEqual([null]);
    const c = writer();
    const d = writer();
    long.listen("c", "k", c.write);
    long.cancel("c", d.write); // not this listener: ignored
    expect(long.waiting("c")).toBe(true);
    long.cancel("c", c.write); // withdrawn without a write (its connection is gone)
    expect(long.waiting("c")).toBe(false);
    expect(c.got).toEqual([]);
  });
});

describe("ReplySessions (who may listen)", () => {
  it("knows a session after a session start, prompt, notification, stop or tool event within 2 h", () => {
    const g = new ReplySessions();
    g.note("s", { kind: "status", cwd: "/w/shop", transcript: "s.jsonl", t: 0 });
    expect(g.check("s", { cwd: "/w/shop" }, 1)).toBe("unknown");
    for (const kind of ["stop", "prompt", "notify"]) {
      const k = new ReplySessions();
      k.note("x", { kind, cwd: "/w/shop", t: 0 });
      expect(k.check("x", { cwd: "/w/shop" }, 1), kind).toBe("ok");
    }
    g.note("s", { kind: "tool_start", t: 10 });
    expect(g.check("s", { cwd: "/w/shop" }, 20)).toBe("ok");
    expect(g.check("s", { transcript: "s.jsonl" }, 20)).toBe("ok");
    expect(g.check("s", { cwd: "/w/shop" }, 10 + 2 * 3600_000 + 1)).toBe("unknown");
    g.note("t", { kind: "session_start", cwd: "/w/x", t: 0 });
    expect(g.check("t", { cwd: "/w/x" }, 1)).toBe("ok");
    expect(g.check("nobody", { cwd: "/w/x" }, 1)).toBe("unknown");
  });
  it("the listener must name the session's cwd or transcript; anything else is a mismatch", () => {
    const g = new ReplySessions();
    g.note("s", { kind: "session_start", cwd: "/w/shop", transcript: "s.jsonl", t: 0 });
    expect(g.check("s", { cwd: "/w/other" }, 1)).toBe("mismatch");
    expect(g.check("s", { transcript: "x.jsonl" }, 1)).toBe("mismatch");
    expect(g.check("s", {}, 1)).toBe("mismatch");
  });
  it("forgets a session at its end and keeps at most 200", () => {
    const g = new ReplySessions();
    g.note("s", { kind: "session_start", cwd: "/w", t: 0 });
    g.forget("s");
    expect(g.check("s", { cwd: "/w" }, 1)).toBe("unknown");
    for (let i = 0; i < 250; i++) g.note(`s${i}`, { kind: "session_start", cwd: "/w", t: i });
    expect(g.size()).toBeLessThanOrEqual(200);
    expect(g.check("s249", { cwd: "/w" }, 300)).toBe("ok");
  });
});

// ------------------------------------------------------------------ live

const TOKEN = "reply-test-token";

function getReply(handle: ServerHandle, query: string, headers: Record<string, string> = {}): Promise<{ status: number; body: unknown }> {
  return new Promise((resolve, reject) => {
    const req = request({ host: "127.0.0.1", port: handle.httpPort, path: `/reply${query}`, method: "GET", headers: { "X-Lotse-Token": TOKEN, ...headers } }, (res) => {
      let text = "";
      res.setEncoding("utf8").on("data", (c: string) => (text += c));
      res.on("end", () => resolve({ status: res.statusCode ?? 0, body: text === "" ? undefined : JSON.parse(text) }));
    });
    req.on("error", reject);
    req.end();
  });
}

const authed = (handle: ServerHandle, path: string, body: unknown) =>
  fetch(`http://127.0.0.1:${handle.httpPort}${path}`, { method: "POST", headers: { "Content-Type": "application/json", "X-Lotse-Token": TOKEN }, body: JSON.stringify(body) });

describe("live: replying from the headset", () => {
  let handle: ServerHandle | undefined;
  let h: Headset | undefined;
  afterEach(async () => {
    h?.close();
    await handle?.close();
    handle = undefined;
    vi.restoreAllMocks();
  });
  async function boot(over: Partial<ServerOptions> = {}): Promise<{ listening: string[] }> {
    const listening: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, onReplyListen: (sid) => listening.push(sid), ...over });
    h = await pairHeadset(handle);
    return { listening };
  }
  /** The session starts (so it is known), then stops: what a real Claude session sends before its listener. */
  const stop = async (sid = "s1", extra: object = {}) => {
    await authed(handle!, "/ev", { hook_event_name: "SessionStart", session_id: sid, cwd: "/w/shop", transcript_path: `/h/.claude/projects/p/${sid}.jsonl`, ...extra });
    return authed(handle!, "/ev", { hook_event_name: "Stop", session_id: sid, cwd: "/w/shop", ...extra });
  };
  const ME = "&cwd=%2Fw%2Fshop";
  const until = async (ok: () => boolean) => {
    for (const end = Date.now() + 3000; !ok(); await new Promise((r) => setTimeout(r, 10))) if (Date.now() > end) throw new Error("timeout");
  };

  it("/reply has the same gate as the other endpoints", async () => {
    await boot();
    expect((await getReply(handle!, "?sid=s1" + ME, { Origin: "http://evil" })).status).toBe(403);
    expect((await getReply(handle!, "?sid=s1" + ME, { Host: "evil:1" })).status).toBe(403);
    expect((await getReply(handle!, "?sid=s1" + ME, { "X-Lotse-Token": "wrong" })).status).toBe(401);
    expect((await getReply(handle!, "")).status).toBe(400);
    expect((await getReply(handle!, "?sid=" + "x".repeat(201))).status).toBe(400);
    expect((await authed(handle!, "/reply?sid=s1", {})).status).toBe(404);
  });

  it("delivers the headset's text to the waiting listener and acks delivered:true; the agent shows 'Reply sent'", async () => {
    const { listening } = await boot();
    await stop();
    const waiting = getReply(handle!, "?sid=s1" + ME);
    await until(() => listening.includes("s1"));
    h!.send({ type: "agent_reply", sid: "s1", text: "Bitte nur die Preise anpassen." });
    expect(await h!.next((m) => m.type === "agent_reply_ack")).toEqual({ type: "agent_reply_ack", sid: "s1", delivered: true });
    expect(await waiting).toEqual({ status: 200, body: { text: "Bitte nur die Preise anpassen." } });
    const agent = await h!.next((m) => m.type === "agent" && m.agent.lastAction === "Reply sent");
    expect(agent.agent.status).toBe("working");
  });

  it("acks delivered:false without a listener, for an agent that is not done or waiting, an unknown agent, and other tools", async () => {
    const { listening } = await boot();
    await stop();
    h!.send({ type: "agent_reply", sid: "s1", text: "a" });
    expect(await h!.next((m) => m.type === "agent_reply_ack")).toEqual({ type: "agent_reply_ack", sid: "s1", delivered: false });
    h!.send({ type: "agent_reply", sid: "nobody", text: "a" });
    expect(await h!.next((m) => m.type === "agent_reply_ack" && m.sid === "nobody")).toMatchObject({ delivered: false });
    // a working agent without a listener
    await authed(handle!, "/ev", { hook_event_name: "PreToolUse", session_id: "w1", tool_name: "Read", tool_input: { file_path: "/w/a" } });
    h!.send({ type: "agent_reply", sid: "w1", text: "a" });
    expect(await h!.next((m) => m.type === "agent_reply_ack" && m.sid === "w1")).toMatchObject({ delivered: false });
    // Muse: never delivered, and its listener is not answered
    await stop("m1", { lotse_source: "muse" });
    const museWait = getReply(handle!, "?sid=m1" + ME);
    await until(() => listening.includes("m1"));
    h!.send({ type: "agent_reply", sid: "m1", text: "a" });
    expect(await h!.next((m) => m.type === "agent_reply_ack" && m.sid === "m1")).toMatchObject({ delivered: false });
    await handle!.close();
    expect((await museWait).body).toEqual({});
    handle = undefined;
  });

  it("only the paired socket may reply, and at most once per agent every 5 s", async () => {
    let t = 1_000_000;
    const { listening } = await boot({ now: () => t });
    const { WebSocket } = await import("ws");
    const other = new WebSocket(`ws://127.0.0.1:${handle!.wsPort}`);
    const got: any[] = [];
    other.on("message", (x) => got.push(JSON.parse(x.toString())));
    await new Promise<void>((r) => other.once("open", () => r()));
    await stop();
    const waiting = getReply(handle!, "?sid=s1" + ME);
    await until(() => listening.includes("s1"));
    other.send(JSON.stringify({ type: "agent_reply", sid: "s1", text: "fremd" }));
    await until(() => got.length > 0);
    other.terminate();
    expect(got).toEqual([{ type: "error", error: "not-paired" }]);
    h!.send({ type: "agent_reply", sid: "s1", text: "eins" });
    await h!.next((m) => m.type === "agent_reply_ack");
    expect((await waiting).body).toEqual({ text: "eins" });
    h!.send({ type: "agent_reply", sid: "s1", text: "zwei" });
    await h!.next((m) => m.type === "error" && m.error === "rate-limited");
    t += 5000;
    h!.send({ type: "agent_reply", sid: "s1", text: "drei" });
    await h!.next((m) => m.type === "agent_reply_ack" && m.delivered === false);
  });

  it("the long poll ends with {} after its wait, when replaced, at session end and on shutdown", async () => {
    const { listening } = await boot({ replyWaitMs: 200 });
    await stop();
    expect((await getReply(handle!, "?sid=s1" + ME)).body).toEqual({});
    await handle!.close();
    handle = undefined;
    const second = await boot({ replyWaitMs: 60_000 });
    await stop();
    const a = getReply(handle!, "?sid=s1" + ME);
    await until(() => second.listening.length === 1);
    const b = getReply(handle!, "?sid=s1" + ME);
    expect((await a).body).toEqual({});
    await authed(handle!, "/ev", { hook_event_name: "SessionEnd", session_id: "s1" });
    expect((await b).body).toEqual({});
    await stop("s2");
    const c = getReply(handle!, "?sid=s2" + ME);
    await until(() => second.listening.includes("s2"));
    await handle!.close();
    handle = undefined;
    expect((await c).body).toEqual({});
    expect(listening).toEqual(["s1"]);
  });

  it("never logs or records the text: the recording only says that a reply was sent", async () => {
    const dir = tempDir("lotse-reply-");
    const file = join(dir, "r.ndjson");
    const rec = new Recorder(file, { repoRoot: dir, homeDir: join(dir, "home") });
    const log = vi.spyOn(console, "log");
    const err = vi.spyOn(console, "error");
    const { listening } = await boot({ onEvent: (e) => rec.write(e) });
    await stop();
    const waiting = getReply(handle!, "?sid=s1" + ME);
    await until(() => listening.includes("s1"));
    h!.send({ type: "agent_reply", sid: "s1", text: "GEHEIMER-ANTWORTTEXT" });
    await waiting;
    await settle(h!);
    await rec.close();
    const text = readFileSync(file, "utf8");
    expect(text).not.toContain("GEHEIMER");
    expect(text.trim().split("\n").map((l) => JSON.parse(l).kind)).toContain("reply");
    for (const spy of [log, err]) expect(spy.mock.calls.flat().join(" ")).not.toContain("GEHEIMER");
  });
});

describe("live: listeners are bound, matched and acks honest (review B3-1)", () => {
  let handle: ServerHandle | undefined;
  let h: Headset | undefined;
  afterEach(async () => {
    h?.close();
    await handle?.close();
    handle = undefined;
  });
  const until = async (ok: () => boolean) => {
    for (const end = Date.now() + 3000; !ok(); await new Promise((r) => setTimeout(r, 10))) if (Date.now() > end) throw new Error("timeout");
  };
  const start = (sid: string, cwd = "/w/shop") =>
    authed(handle!, "/ev", { hook_event_name: "SessionStart", session_id: sid, cwd, transcript_path: `/h/.claude/projects/p/${sid}.jsonl` });
  const stopOnly = (sid: string) => authed(handle!, "/ev", { hook_event_name: "Stop", session_id: sid, cwd: "/w/shop" });

  it("without an ingest token /reply is refused entirely", async () => {
    handle = await bootLive();
    const r = await new Promise<number>((resolve) => {
      const req = request({ host: "127.0.0.1", port: handle!.httpPort, path: "/reply?sid=s1&cwd=%2Fw", method: "GET" }, (res) => {
        res.resume();
        resolve(res.statusCode ?? 0);
      });
      req.end();
    });
    expect(r).toBe(403);
  });

  it("an unknown session (no event of it in 2 h) gets 404 and no listener", async () => {
    const listening: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, onReplyListen: (sid) => listening.push(sid) });
    expect((await getReply(handle, "?sid=ghost&cwd=%2Fw%2Fshop")).status).toBe(404);
    expect(listening).toEqual([]);
  });

  it("a chat-only session whose SessionStart the bridge missed can listen after its Stop", async () => {
    const listening: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, onReplyListen: (sid) => listening.push(sid) });
    h = await pairHeadset(handle);
    await stopOnly("late"); // the bridge started after this session's SessionStart: only its Stop arrives
    const waiting = getReply(handle, "?sid=late&cwd=%2Fw%2Fshop");
    await until(() => listening.length === 1);
    h.send({ type: "agent_reply", sid: "late", text: "Weiter so." });
    expect(await h.next((m) => m.type === "agent_reply_ack")).toMatchObject({ delivered: true });
    expect((await waiting).body).toEqual({ text: "Weiter so." });
  });

  it("a listener must name the session's cwd or transcript; a mismatch is 409 and the existing listener stays", async () => {
    const listening: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, onReplyListen: (sid) => listening.push(sid) });
    h = await pairHeadset(handle);
    await start("s1");
    expect((await getReply(handle, "?sid=s1")).status).toBe(400);
    expect((await getReply(handle, "?sid=s1&cwd=%2Fevil")).status).toBe(409);
    const real = getReply(handle, "?sid=s1&transcript=s1.jsonl");
    await until(() => listening.length === 1);
    expect((await getReply(handle, "?sid=s1&cwd=%2Fevil")).status).toBe(409);
    await stopOnly("s1");
    h.send({ type: "agent_reply", sid: "s1", text: "echt" });
    expect(await h.next((m) => m.type === "agent_reply_ack")).toMatchObject({ delivered: true });
    expect((await real).body).toEqual({ text: "echt" });
  });

  it("at most 32 listeners at once: the 33rd gets 429", async () => {
    handle = await bootLive({ ingestToken: TOKEN });
    for (let i = 0; i < 33; i++) await start(`s${i}`);
    const polls = Array.from({ length: 32 }, (_, i) => getReply(handle!, `?sid=s${i}&cwd=%2Fw%2Fshop`));
    await new Promise((r) => setTimeout(r, 200));
    expect((await getReply(handle, "?sid=s32&cwd=%2Fw%2Fshop")).status).toBe(429);
    await handle.close();
    handle = undefined;
    for (const p of await Promise.all(polls)) expect(p.body).toEqual({});
  });

  it("a listener whose connection is gone gets nothing, and the ack says delivered:false", async () => {
    const listening: string[] = [];
    handle = await bootLive({ ingestToken: TOKEN, onReplyListen: (sid) => listening.push(sid) });
    h = await pairHeadset(handle);
    await start("s1");
    await stopOnly("s1");
    const req = request({ host: "127.0.0.1", port: handle.httpPort, path: "/reply?sid=s1&cwd=%2Fw%2Fshop", method: "GET", headers: { "X-Lotse-Token": TOKEN } });
    req.on("error", () => {});
    req.end();
    await until(() => listening.length === 1);
    req.destroy();
    await new Promise((r) => setTimeout(r, 100));
    h.send({ type: "agent_reply", sid: "s1", text: "zu spät" });
    expect(await h.next((m) => m.type === "agent_reply_ack")).toMatchObject({ delivered: false });
  });

  it("the 5 s rate limit counts only delivered replies", async () => {
    const listening: string[] = [];
    let t = 1_000_000;
    handle = await bootLive({ ingestToken: TOKEN, now: () => t, onReplyListen: (sid) => listening.push(sid) });
    h = await pairHeadset(handle);
    await start("s1");
    await stopOnly("s1");
    h.send({ type: "agent_reply", sid: "s1", text: "eins" });
    expect(await h.next((m) => m.type === "agent_reply_ack" && m.delivered === false)).toBeTruthy();
    h.send({ type: "agent_reply", sid: "s1", text: "zwei" });
    expect(await h.next((m) => m.type === "agent_reply_ack" && m.delivered === false && h!.msgs.filter((x) => x.type === "agent_reply_ack").length === 2)).toBeTruthy();
    const waiting = getReply(handle, "?sid=s1&cwd=%2Fw%2Fshop");
    await until(() => listening.length === 1);
    h.send({ type: "agent_reply", sid: "s1", text: "drei" });
    await h.next((m) => m.type === "agent_reply_ack" && m.delivered === true);
    expect((await waiting).body).toEqual({ text: "drei" });
    h.send({ type: "agent_reply", sid: "s1", text: "vier" });
    await h.next((m) => m.type === "error" && m.error === "rate-limited");
    t += 5000;
    h.send({ type: "agent_reply", sid: "s1", text: "fünf" });
    await h.next((m) => m.type === "agent_reply_ack" && m.delivered === false && h!.msgs.filter((x) => x.type === "agent_reply_ack").length === 4);
  });
});

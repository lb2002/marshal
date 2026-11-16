// The feed poller: snapshot on subscribe, appended entries only, at most 5 subscriptions, bounded reads,
// and nothing logged. Plus the live server: paired socket only, ends on disconnect.
import { afterEach, describe, expect, it, vi } from "vitest";
import { appendFileSync, mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { FeedHub, FEED_MAX_LINE, FEED_MAX_READ, FEED_SNAPSHOT, type FeedMessage, type FeedHubDeps } from "../src/feed-hub.js";
import type { AgentSource } from "../src/events.js";
import { bootLive, pairHeadset, postTo, settle, type Msg } from "./live-helpers.js";
import { WebSocket } from "ws";
import { tempDir } from "./tmp.js";

const line = (uuid: string, text: string) => JSON.stringify({ type: "assistant", uuid, message: { content: [{ type: "text", text }] } }) + "\n";

interface Rig {
  hub: FeedHub;
  sent: FeedMessage[];
  files: Map<string, string | null>;
  reads: number[];
  dir: string;
}

function rig(over: Partial<FeedHubDeps> = {}): Rig {
  const dir = tempDir("lotse-feed-");
  const sent: FeedMessage[] = [];
  const files = new Map<string, string | null>();
  const reads: number[] = [];
  const hub = new FeedHub({
    source: (sid) => (files.has(sid) ? ("claude" as AgentSource) : undefined),
    logFile: (sid) => files.get(sid) ?? null,
    send: (m) => sent.push(m),
    homeDir: "/home/x",
    now: () => 5,
    intervalMs: 60_000,
    onRead: (n) => reads.push(n),
    ...over,
  });
  return { hub, sent, files, reads, dir };
}

const hubs: FeedHub[] = [];
afterEach(() => {
  for (const h of hubs.splice(0)) h.clear();
  vi.restoreAllMocks();
});

function withFile(r: Rig, sid: string, content: string): string {
  const f = join(r.dir, `${sid}.jsonl`);
  writeFileSync(f, content);
  r.files.set(sid, f);
  hubs.push(r.hub);
  return f;
}

describe("FeedHub", () => {
  it("subscribe sends a snapshot of the last 30 entries, oldest first", async () => {
    const r = rig();
    withFile(r, "s1", Array.from({ length: 40 }, (_, i) => line(`u${i}`, `msg ${i}`)).join(""));
    expect(r.hub.subscribe("s1")).toBe("ok");
    await r.hub.settled();
    expect(r.sent).toHaveLength(1);
    const snap = r.sent[0] as Extract<FeedMessage, { type: "feed_snapshot" }>;
    expect(snap.type).toBe("feed_snapshot");
    expect(snap.sid).toBe("s1");
    expect(snap.entries).toHaveLength(FEED_SNAPSHOT);
    expect(snap.entries[0].text).toBe("msg 10");
    expect(snap.entries.at(-1)!.text).toBe("msg 39");
  });

  it("appended lines give feed_append with only the new entries; a half-written line waits", async () => {
    const r = rig();
    const f = withFile(r, "s1", line("a", "first"));
    r.hub.subscribe("s1");
    await r.hub.settled();
    const half = line("b", "second");
    appendFileSync(f, half.slice(0, 20));
    await r.hub.pollNow();
    expect(r.sent).toHaveLength(1);
    appendFileSync(f, half.slice(20) + "not json\n" + line("c", "third"));
    await r.hub.pollNow();
    expect(r.sent[1]).toEqual({ type: "feed_append", sid: "s1", entries: [expect.objectContaining({ text: "second" }), expect.objectContaining({ text: "third" })] });
    await r.hub.pollNow();
    expect(r.sent).toHaveLength(2);
  });

  it("unsubscribe stops sending and the timer", async () => {
    const r = rig();
    const f = withFile(r, "s1", line("a", "first"));
    r.hub.subscribe("s1");
    await r.hub.settled();
    expect(r.hub.active()).toBe(true);
    r.hub.unsubscribe("s1");
    expect(r.hub.active()).toBe(false);
    appendFileSync(f, line("b", "second"));
    await r.hub.pollNow();
    expect(r.sent).toHaveLength(1);
  });

  it("keeps at most 5 subscriptions: the sixth drops the oldest", async () => {
    const r = rig();
    const sids = ["s1", "s2", "s3", "s4", "s5", "s6"];
    for (const s of sids) withFile(r, s, line(`${s}a`, s));
    for (const s of sids) r.hub.subscribe(s);
    await r.hub.settled();
    expect(r.hub.subscriptions()).toEqual(["s2", "s3", "s4", "s5", "s6"]);
    appendFileSync(r.files.get("s1")!, line("x", "late"));
    await r.hub.pollNow();
    expect(r.sent.filter((m) => m.sid === "s1" && m.type === "feed_append")).toEqual([]);
  });

  it("refuses an unknown agent", () => {
    const r = rig();
    hubs.push(r.hub);
    expect(r.hub.subscribe("nobody")).toBe("unknown-agent");
    expect(r.hub.subscriptions()).toEqual([]);
  });

  it("reads at most 64 KB per poll and only the appended bytes", async () => {
    const r = rig();
    const f = withFile(r, "s1", line("a", "x".repeat(100)));
    r.hub.subscribe("s1");
    await r.hub.settled();
    r.reads.length = 0;
    appendFileSync(f, Array.from({ length: 2000 }, (_, i) => line(`n${i}`, "y".repeat(100))).join(""));
    await r.hub.pollNow();
    expect(r.reads).toHaveLength(1);
    expect(r.reads[0]).toBeLessThanOrEqual(FEED_MAX_READ);
    // The rest arrives with the next polls, never more than 64 KB at once.
    for (let i = 0; i < 10; i++) await r.hub.pollNow();
    expect(Math.max(...r.reads)).toBeLessThanOrEqual(FEED_MAX_READ);
    const texts = r.sent.filter((m) => m.type === "feed_append").flatMap((m) => m.entries);
    expect(new Set(texts.map((e) => e.id)).size).toBe(texts.length);
  });

  it("skips a single line longer than 1 MB and goes on after it", async () => {
    const r = rig();
    const f = withFile(r, "s1", line("a", "first"));
    r.hub.subscribe("s1");
    await r.hub.settled();
    appendFileSync(f, line("big", "z".repeat(FEED_MAX_LINE + 10)) + line("c", "after"));
    for (let i = 0; i < 40; i++) await r.hub.pollNow();
    const got = r.sent.filter((m) => m.type === "feed_append").flatMap((m) => m.entries.map((e) => e.text));
    expect(got).toEqual(["after"]);
    expect(Math.max(...r.reads)).toBeLessThanOrEqual(FEED_MAX_READ);
  });

  it("buffers a big line across polls (64 KB per read): a 200 KB Claude tool_result arrives once, capped", async () => {
    const r = rig();
    const f = withFile(r, "s1", line("a", "first"));
    r.hub.subscribe("s1");
    await r.hub.settled();
    r.reads.length = 0;
    const big = JSON.stringify({ type: "user", uuid: "tr", message: { content: [{ type: "tool_result", content: "r".repeat(200_000) }] } }) + "\n";
    appendFileSync(f, big + line("c", "after"));
    for (let i = 0; i < 6; i++) await r.hub.pollNow();
    const got = r.sent.filter((m) => m.type === "feed_append").flatMap((m) => m.entries);
    expect(got.map((e) => [e.kind, e.text.length <= 400])).toEqual([["result", true], ["text", true]]);
    expect(Math.max(...r.reads)).toBeLessThanOrEqual(FEED_MAX_READ);
  });

  it("Gemini: growing $set lines over 64 KB give each message once; the snapshot reads a big last line", async () => {
    const r = rig({ source: (sid) => (r.files.has(sid) ? ("gemini" as AgentSource) : undefined) });
    const msg = (id: string, text: string) => ({ id, timestamp: "2026-11-12T08:00:00.000Z", type: id.startsWith("u") ? "user" : "gemini", content: text });
    const set = (...m: object[]) => JSON.stringify({ $set: { messages: m } }) + "\n";
    const pad = "p".repeat(90_000);
    const m1 = msg("u1", `first ${pad}`);
    const f = withFile(r, "s1", JSON.stringify({ sessionId: "g", projectHash: "h" }) + "\n" + set(m1));
    r.hub.subscribe("s1");
    await r.hub.settled();
    expect((r.sent[0] as { entries: { text: string }[] }).entries.map((e) => e.text.slice(0, 5))).toEqual(["first"]);
    appendFileSync(f, set(m1, msg("g2", `second ${pad}`)));
    for (let i = 0; i < 6; i++) await r.hub.pollNow();
    appendFileSync(f, set(m1, msg("g2", `second ${pad}`), msg("u3", "third")));
    for (let i = 0; i < 8; i++) await r.hub.pollNow();
    const got = r.sent.filter((m) => m.type === "feed_append").flatMap((m) => m.entries.map((e) => e.text.slice(0, 6)));
    expect(got).toEqual(["second", "third"]);
    expect(Math.max(...r.reads)).toBeLessThanOrEqual(FEED_MAX_READ);
  });

  it("a burst of more than 30 new entries is sent in chunks of 30, nothing dropped", async () => {
    const r = rig();
    const f = withFile(r, "s1", line("a", "first"));
    r.hub.subscribe("s1");
    await r.hub.settled();
    appendFileSync(f, Array.from({ length: 75 }, (_, i) => line(`b${i}`, `n${i}`)).join(""));
    await r.hub.pollNow();
    const appends = r.sent.filter((m) => m.type === "feed_append");
    expect(appends.map((m) => m.entries.length)).toEqual([30, 30, 15]);
    expect(appends.flatMap((m) => m.entries.map((e) => e.text))).toEqual(Array.from({ length: 75 }, (_, i) => `n${i}`));
  });

  it("a timer tick is skipped while a poll is still in flight", async () => {
    const r = rig();
    const f = withFile(r, "s1", line("a", "first"));
    r.hub.subscribe("s1");
    await r.hub.settled();
    appendFileSync(f, line("b", "second"));
    r.reads.length = 0;
    r.hub.tick();
    r.hub.tick();
    r.hub.tick();
    await r.hub.settled();
    expect(r.reads).toHaveLength(1);
  });

  it("a log that appears later is picked up; a missing one gives an empty snapshot", async () => {
    const r = rig();
    hubs.push(r.hub);
    r.files.set("s1", null);
    r.hub.subscribe("s1");
    await r.hub.settled();
    expect(r.sent).toEqual([{ type: "feed_snapshot", sid: "s1", entries: [] }]);
    const f = join(r.dir, "late.jsonl");
    writeFileSync(f, line("a", "hello"));
    r.files.set("s1", f);
    await r.hub.pollNow();
    expect(r.sent[1]).toEqual({ type: "feed_append", sid: "s1", entries: [expect.objectContaining({ text: "hello" })] });
  });

  it("logs nothing, even with secrets and broken files", async () => {
    const spies = (["log", "error", "warn", "info", "debug"] as const).map((k) => vi.spyOn(console, k).mockImplementation(() => {}));
    const out = vi.spyOn(process.stdout, "write");
    const err = vi.spyOn(process.stderr, "write");
    const r = rig();
    const f = withFile(r, "s1", line("a", "token ghp_" + "b".repeat(36)) + "{broken\n");
    r.hub.subscribe("s1");
    await r.hub.settled();
    appendFileSync(f, "\u0000\u0001garbage\n" + line("b", "ok"));
    await r.hub.pollNow();
    r.files.set("s1", join(r.dir, "gone.jsonl"));
    await r.hub.pollNow();
    for (const s of [...spies, out, err]) expect(s).not.toHaveBeenCalled();
    expect(JSON.stringify(r.sent)).not.toContain("ghp_bbbb");
  });
});

describe("feed over the live server", () => {
  it("only the paired socket may subscribe; snapshot and appends arrive; they end on disconnect", async () => {
    const home = tempDir("lotse-feed-home-");
    const dir = join(home, ".claude", "projects", "shop");
    mkdirSync(dir, { recursive: true });
    const file = join(dir, "s1.jsonl");
    writeFileSync(file, line("a", "hello headset"));
    const handle = await bootLive({ homeDir: home, feedIntervalMs: 50 });
    try {
      const h = await pairHeadset(handle);
      await postTo(handle, "/ev", { hook_event_name: "SessionStart", session_id: "s1", cwd: "/w/shop", transcript_path: file, source: "startup" });
      await h.next((m) => m.type === "agent" && m.agent.sid === "s1");

      // An unpaired socket is refused.
      const other = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`);
      const got: Msg[] = [];
      other.on("message", (d) => got.push(JSON.parse(d.toString())));
      await new Promise((res) => other.once("open", res));
      other.send(JSON.stringify({ type: "feed_subscribe", sid: "s1" }));
      await new Promise((res) => setTimeout(res, 150));
      expect(got).toEqual([{ type: "error", error: "not-paired" }]);
      other.terminate();

      h.send({ type: "feed_subscribe", sid: "s1" });
      const snap = await h.next((m) => m.type === "feed_snapshot");
      expect(snap).toMatchObject({ sid: "s1", entries: [{ kind: "text", label: "says", text: "hello headset" }] });
      appendFileSync(file, line("b", "and more"));
      const app = await h.next((m) => m.type === "feed_append");
      expect(app.entries.map((e: Msg) => e.text)).toEqual(["and more"]);

      h.send({ type: "feed_subscribe", sid: "nobody" });
      await h.next((m) => m.type === "error" && m.error === "unknown-agent");
      h.send({ type: "feed_subscribe", sid: "" });
      await h.next((m) => m.type === "error" && m.error === "bad-message");

      h.send({ type: "feed_unsubscribe", sid: "s1" });
      await settle(h);
      appendFileSync(file, line("c", "not sent"));
      await new Promise((res) => setTimeout(res, 200));
      expect(h.msgs.filter((m) => m.type === "feed_append")).toHaveLength(1);

      // Re-subscribe, then disconnect: a new headset gets nothing until it subscribes itself.
      h.send({ type: "feed_subscribe", sid: "s1" });
      await h.next((m) => m.type === "feed_snapshot" && m.entries.length === 3);
      h.close();
      await new Promise((res) => setTimeout(res, 100));
      const h2 = await pairHeadsetWithSession(handle);
      appendFileSync(file, line("d", "after reconnect"));
      await new Promise((res) => setTimeout(res, 200));
      expect(h2.filter((m) => m.type === "feed_append" || m.type === "feed_snapshot")).toEqual([]);
    } finally {
      await handle.close();
    }
  });
});

/** A second headset with a fresh pairing code; returns its message list. */
async function pairHeadsetWithSession(handle: Awaited<ReturnType<typeof bootLive>>): Promise<Msg[]> {
  const code = handle.newPairingCode();
  const ws = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`);
  const msgs: Msg[] = [];
  ws.on("message", (d) => msgs.push(JSON.parse(d.toString())));
  await new Promise((res) => ws.once("open", res));
  ws.send(JSON.stringify({ type: "hello", code }));
  for (let i = 0; i < 50 && !msgs.some((m) => m.type === "paired"); i++) await new Promise((res) => setTimeout(res, 20));
  expect(msgs.some((m) => m.type === "paired")).toBe(true);
  return msgs;
}

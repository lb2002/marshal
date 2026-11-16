import { afterEach, describe, expect, it, vi } from "vitest";
import { request as httpRequest } from "node:http";
import { createConnection, createServer as createNetServer } from "node:net";
import type { AddressInfo } from "node:net";
import os from "node:os";
import path from "node:path";
import { WebSocket } from "ws";
import type { ClientOptions } from "ws";
import { startServer } from "../src/server.js";
import { Explainer } from "../src/explain.js";
import { replay } from "../src/replay.js";
import { Recorder, readRecording } from "../src/recorder.js";
import { tempDir } from "./tmp.js";

type Handle = Awaited<ReturnType<typeof startServer>>;
type Msg = Record<string, any>;

const CODE = "123456";
const allow = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "allow" } } };

let handle: Handle;
const sockets: WebSocket[] = [];

afterEach(async () => {
  for (const s of sockets.splice(0)) s.terminate();
  await handle?.close();
});

async function boot(over: Partial<Parameters<typeof startServer>[0]> = {}) {
  handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: CODE, holdMs: 30_000, budgetUsd: 5, ...over });
  return handle;
}

const post = (path: string, body: unknown, raw = false) =>
  fetch(`http://127.0.0.1:${handle.httpPort}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: raw ? (body as string) : JSON.stringify(body),
  });

interface Client {
  ws: WebSocket;
  msgs: Msg[];
  closed: Promise<number>;
  session?: string;
  send(m: unknown): void;
  next(pred: (m: Msg) => boolean, ms?: number): Promise<Msg>;
}

async function connect(opts: ClientOptions = {}): Promise<Client> {
  const ws = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`, opts);
  sockets.push(ws);
  const msgs: Msg[] = [];
  const waiters: Array<() => void> = [];
  ws.on("message", (d) => {
    msgs.push(JSON.parse(d.toString()));
    waiters.splice(0).forEach((w) => w());
  });
  const closed = new Promise<number>((res) => ws.on("close", (code) => res(code)));
  await new Promise<void>((res, rej) => {
    ws.once("open", () => res());
    ws.once("error", rej);
  });
  return {
    ws,
    msgs,
    closed,
    send: (m) => ws.send(typeof m === "string" ? m : JSON.stringify(m)),
    async next(pred, ms = 2000) {
      const deadline = Date.now() + ms;
      let from = 0;
      for (;;) {
        for (; from < msgs.length; from++) if (pred(msgs[from])) return msgs[from];
        const left = deadline - Date.now();
        if (left <= 0) throw new Error(`timeout waiting for message; got ${JSON.stringify(msgs)}`);
        await new Promise<void>((res) => {
          waiters.push(res);
          setTimeout(res, left);
        });
      }
    },
  };
}

async function paired(): Promise<Client> {
  const c = await connect();
  c.send({ type: "hello", code: CODE });
  await c.next((m) => m.type === "snapshot");
  c.session = (await c.next((m) => m.type === "paired")).session;
  return c;
}

interface RawRes {
  status: number;
  headers: Record<string, string | string[] | undefined>;
  body: string;
}

/** Plain node:http request so tests can set Host, Origin, Content-Type and method freely. */
function raw(p: string, o: { method?: string; headers?: Record<string, string>; body?: string } = {}): Promise<RawRes> {
  return new Promise((resolve, reject) => {
    const req = httpRequest(
      { host: "127.0.0.1", port: handle.httpPort, path: p, method: o.method ?? "POST", headers: o.headers },
      (res) => {
        let body = "";
        res.on("data", (d) => (body += d));
        res.on("end", () => resolve({ status: res.statusCode ?? 0, headers: res.headers, body }));
      },
    );
    req.on("error", reject);
    req.end(o.body ?? "{}");
  });
}
const JSON_HDR = { "Content-Type": "application/json" };
const evBody = JSON.stringify({ session_id: "s1", hook_event_name: "PreToolUse", tool_name: "Read" });

const permPayload = (command = "ls") => ({
  session_id: "s1",
  hook_event_name: "PermissionRequest",
  tool_name: "Bash",
  tool_input: { command },
  cwd: process.cwd(),
});

describe("server", () => {
  it("POST /ev returns {} and a paired client sees the working agent", async () => {
    await boot();
    const c = await paired();
    const res = await post("/ev", { session_id: "s1", hook_event_name: "PreToolUse", tool_name: "Bash", tool_input: { command: "ls" } });
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({});
    const m = await c.next((x) => x.type === "agent");
    expect(m.agent.status).toBe("working");
  });

  it("unpaired perm_decision gets not-paired; wrong code closes 4001", async () => {
    await boot();
    const c = await connect();
    c.send({ type: "perm_decision", decision: { id: "x", behavior: "allow", confirm: "two-hand" } });
    expect(await c.next((m) => m.type === "error")).toEqual({ type: "error", error: "not-paired" });
    c.send({ type: "hello", code: "000000" });
    expect(await c.closed).toBe(4001);
  });

  it("unpaired clients receive no agent data", async () => {
    await boot();
    const c = await connect();
    await post("/ev", { session_id: "s1", hook_event_name: "PreToolUse", tool_name: "Read" });
    await new Promise((r) => setTimeout(r, 100));
    expect(c.msgs).toEqual([]);
  });

  it("sends a snapshot first after a correct hello", async () => {
    await boot();
    const c = await connect();
    c.send({ type: "hello", code: CODE });
    await c.next((m) => m.type === "snapshot");
    expect(c.msgs[0]).toMatchObject({ type: "snapshot", agents: [], pending: [] });
    expect((await c.next((m) => m.type === "paired")).session).toMatch(/^[A-Za-z0-9_-]{43}$/);
  });

  it("POST /perm: allow with pinch-hold answers the hook", async () => {
    await boot();
    const c = await paired();
    const pending = post("/perm", permPayload());
    const open = await c.next((m) => m.type === "perm_open");
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "allow", confirm: "pinch-hold" } });
    const res = await pending;
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual(allow);
    const closed = await c.next((m) => m.type === "perm_closed");
    expect(closed.id).toBe(open.request.id);
    // blocked status was broadcast before the request opened
    expect(c.msgs.find((m) => m.type === "agent")?.agent.status).toBe("blocked");
  });

  it("replies with the gesture error when decide() refuses", async () => {
    await boot();
    const c = await paired();
    const pending = post("/perm", permPayload("rm -rf /"));
    const open = await c.next((m) => m.type === "perm_open");
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "allow", confirm: "pinch-hold" } });
    expect(await c.next((m) => m.type === "error")).toEqual({ type: "error", error: "needs-two-hand" });
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
    expect(await (await pending).json()).toMatchObject({ hookSpecificOutput: { decision: { behavior: "deny" } } });
  });

  it("POST /perm without a paired client returns {} quickly", async () => {
    await boot();
    const t = Date.now();
    const res = await post("/perm", permPayload());
    expect(await res.json()).toEqual({});
    expect(Date.now() - t).toBeLessThan(500);
  });

  it("client disconnect answers pending /perm with {}", async () => {
    await boot();
    const c = await paired();
    const pending = post("/perm", permPayload());
    await c.next((m) => m.type === "perm_open");
    const t = Date.now();
    c.ws.close();
    expect(await (await pending).json()).toEqual({});
    expect(Date.now() - t).toBeLessThan(500);
  });

  it("POST /status updates cost and emits a budget incident", async () => {
    await boot({ budgetUsd: 2 });
    const c = await paired();
    await post("/status", { session_id: "s1", cost: { total_cost_usd: 2.5 }, context_window: { used_percentage: 40 } });
    const inc = await c.next((m) => m.type === "incident");
    expect(inc.incident.kind).toBe("budget");
    const agent = await c.next((m) => m.type === "agent");
    expect(agent.agent.costUsd).toBe(2.5);
  });

  it("malformed JSON gives 400 and the server keeps running", async () => {
    await boot();
    for (const p of ["/ev", "/perm", "/status"]) {
      expect((await post(p, "{nope", true)).status).toBe(400);
    }
    const ok = await post("/ev", { session_id: "s1", hook_event_name: "PreToolUse", tool_name: "Read" });
    expect(ok.status).toBe(200);
  });

  it("rejects other methods/paths with 404 and oversized bodies with 413", async () => {
    await boot();
    expect((await fetch(`http://127.0.0.1:${handle.httpPort}/ev`)).status).toBe(404);
    expect((await post("/nope", {})).status).toBe(404);
    const big = JSON.stringify({ session_id: "s1", pad: "x".repeat(1024 * 1024 + 10) });
    expect((await post("/ev", big, true)).status).toBe(413);
    expect((await post("/ev", { session_id: "s1", hook_event_name: "PreToolUse" })).status).toBe(200);
  });

  it("answers {} for /perm payloads it cannot use", async () => {
    await boot();
    const c = await paired();
    const res = await post("/perm", { session_id: "s1", hook_event_name: "PermissionRequest" });
    expect(await res.json()).toEqual({});
    expect(c.msgs.some((m) => m.type === "perm_open")).toBe(false);
  });

  it("answers bad-message for malformed frames without changing state", async () => {
    await boot();
    const c = await paired();
    for (const bad of ["not json", "[]", "null", JSON.stringify({ type: "nope" }), JSON.stringify({ type: "hello", code: 5 }),
      JSON.stringify({ type: "perm_decision", decision: { id: 1, behavior: "allow" } })]) {
      c.msgs.length = 0;
      c.send(bad);
      expect(await c.next((m) => m.type === "error")).toEqual({ type: "error", error: "bad-message" });
    }
  });

  it("closes with 1009 on frames over 64 KiB", async () => {
    await boot();
    const c = await paired();
    c.send(JSON.stringify({ type: "hello", code: "x".repeat(70 * 1024) }));
    expect(await c.closed).toBe(1009);
  });

  it("locks out hello for 60 s after 5 wrong codes", async () => {
    await boot();
    for (let i = 0; i < 5; i++) {
      const c = await connect();
      c.send({ type: "hello", code: "999999" });
      expect(await c.closed).toBe(4001);
    }
    const c = await connect();
    c.send({ type: "hello", code: CODE });
    expect(await c.closed).toBe(4029);
  });

  it("a new successful hello replaces the old client, which must not cancel requests", async () => {
    await boot();
    const a = await paired();
    const pending = post("/perm", permPayload());
    await a.next((m) => m.type === "perm_open");
    const b = await connect();
    b.send({ type: "hello", session: a.session });
    expect(await a.closed).toBe(4000);
    const snap = await b.next((m) => m.type === "snapshot");
    expect(snap.pending).toHaveLength(1);
    b.send({ type: "perm_decision", decision: { id: snap.pending[0].id, behavior: "allow", confirm: "pinch-hold" } });
    expect(await (await pending).json()).toEqual(allow);
  });

  it("inject processes like /ev, opens a broker request for permission events and calls onEvent", async () => {
    const seen: string[] = [];
    await boot({ onEvent: (e) => seen.push(e.kind) });
    const c = await paired();
    handle.inject({ sid: "d1", t: 1, kind: "permission", tool: "Bash", command: "npm test" });
    const agent = await c.next((m) => m.type === "agent");
    expect(agent.agent.status).toBe("blocked");
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.request).toMatchObject({ sid: "d1", tool: "Bash", command: "npm test" });
    expect(seen).toEqual(["permission"]);
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
    await c.next((m) => m.type === "perm_closed");
  });

  it("close() resolves pending requests with {} and leaves no listeners", async () => {
    await boot();
    const c = await paired();
    const pending = post("/perm", permPayload());
    await c.next((m) => m.type === "perm_open");
    await handle.close();
    expect(await (await pending).json()).toEqual({});
  });
});

describe("fix round 1", () => {
  it("inject maps the <repo> placeholder onto demoRoot", async () => {
    const root = path.join(os.tmpdir(), "lotse-demo-root");
    await boot({ demoRoot: root });
    const c = await paired();
    handle.inject({ sid: "d1", t: 1, kind: "permission", cwd: "<repo>", tool: "Write", path: "<repo>/src/a.ts" });
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.request.path).toBe(path.join(root, "src", "a.ts"));
    expect(open.request.risk.risk).not.toBe("destructive");
  });

  it("exposes the bound hosts and really binds HTTP to loopback only", async () => {
    await boot();
    expect([handle.httpHost, handle.wsHost]).toEqual(["127.0.0.1", "0.0.0.0"]);
    await expect(raw("/ev", { headers: JSON_HDR, body: evBody })).resolves.toMatchObject({ status: 200 });
    const other = await new Promise<string>((resolve) => {
      const req = httpRequest({ host: "127.0.0.2", port: handle.httpPort, path: "/ev", method: "POST" }, () => resolve("connected"));
      req.on("error", (e: NodeJS.ErrnoException) => resolve(e.code ?? "error"));
      req.end("{}");
    });
    expect(other).toBe("ECONNREFUSED");
  });

  describe("http hardening", () => {
    it("403 on any Origin header, without touching state", async () => {
      await boot();
      const c = await paired();
      const r = await raw("/ev", { headers: { ...JSON_HDR, Origin: "http://evil.example" }, body: evBody });
      expect(r.status).toBe(403);
      await new Promise((x) => setTimeout(x, 50));
      expect(c.msgs.some((m) => m.type === "agent")).toBe(false);
    });

    it("403 unless Host is 127.0.0.1:<port> or localhost:<port>", async () => {
      await boot();
      const port = handle.httpPort;
      for (const host of ["evil.example", `evil.example:${port}`, "127.0.0.1", `127.0.0.1:${port + 1}`, "localhost", `[::1]:${port}`]) {
        expect((await raw("/ev", { headers: { ...JSON_HDR, Host: host }, body: evBody })).status, host).toBe(403);
      }
      for (const host of [`127.0.0.1:${port}`, `localhost:${port}`]) {
        expect((await raw("/ev", { headers: { ...JSON_HDR, Host: host }, body: evBody })).status, host).toBe(200);
      }
    });

    it("415 unless Content-Type starts with application/json", async () => {
      await boot();
      expect((await raw("/ev", { headers: { "Content-Type": "text/plain" }, body: evBody })).status).toBe(415);
      expect((await raw("/perm", { body: evBody })).status).toBe(415);
      expect((await raw("/ev", { headers: { "Content-Type": "application/json; charset=utf-8" }, body: evBody })).status).toBe(200);
    });

    it("OPTIONS gets 404 without CORS headers", async () => {
      await boot();
      const r = await raw("/ev", { method: "OPTIONS", headers: { Origin: "http://evil.example" } });
      expect(r.status).toBe(404);
      expect(Object.keys(r.headers).some((h) => h.startsWith("access-control"))).toBe(false);
    });

    it("requires X-Lotse-Token when ingestToken is set", async () => {
      await boot({ ingestToken: "s3cret-token" });
      expect((await raw("/ev", { headers: JSON_HDR, body: evBody })).status).toBe(401);
      expect((await raw("/ev", { headers: { ...JSON_HDR, "X-Lotse-Token": "wrong-token!" }, body: evBody })).status).toBe(401);
      expect((await raw("/ev", { headers: { ...JSON_HDR, "X-Lotse-Token": "s3cret-token" }, body: evBody })).status).toBe(200);
    });
  });

  describe("websocket hardening", () => {
    it("rejects upgrades that carry an Origin header", async () => {
      await boot();
      const ws = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`, { origin: "http://evil.example" });
      sockets.push(ws);
      const err = await new Promise<Error>((res) => ws.once("error", res));
      expect(err.message).toMatch(/403/);
    });

    it("closes a socket that never sends a valid hello", async () => {
      await boot({ helloTimeoutMs: 100 });
      const c = await connect();
      expect(await c.closed).toBe(4008);
    });

    it("keeps a paired socket past the hello timeout", async () => {
      await boot({ helloTimeoutMs: 100 });
      const c = await paired();
      await new Promise((x) => setTimeout(x, 250));
      expect(c.ws.readyState).toBe(WebSocket.OPEN);
    });

    it("evicts the oldest unpaired socket (1013) at the cap, so a session hello still pairs", async () => {
      await boot();
      const a = await paired();
      const idle: Client[] = [];
      for (let i = 0; i < 8; i++) idle.push(await connect({ localAddress: `127.0.0.${2 + Math.floor(i / 3)}` }));
      const viaSession = await connect({ localAddress: "127.0.0.10" });
      expect(await idle[0].closed).toBe(1013);
      viaSession.send({ type: "hello", session: a.session });
      await viaSession.next((m) => m.type === "snapshot");
    });

    it("refuses a 4th unpaired socket from the same address", async () => {
      await boot();
      for (let i = 0; i < 3; i++) await connect();
      const ws = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`);
      sockets.push(ws);
      const err = await new Promise<Error>((res) => ws.once("error", res));
      expect(err.message).toMatch(/503/);
    });

    it("terminates a client that stops answering pings, answering its requests with {}", async () => {
      await boot({ heartbeatMs: 50 });
      const first = await paired();
      const mute = await connect({ autoPong: false });
      mute.send({ type: "hello", session: first.session });
      await mute.next((m) => m.type === "snapshot");
      const pending = post("/perm", permPayload());
      await mute.next((m) => m.type === "perm_open");
      const t = Date.now();
      expect(await (await pending).json()).toEqual({});
      expect(Date.now() - t).toBeLessThan(500);
      await mute.closed;
    });

    it("keeps a client that does answer pings", async () => {
      await boot({ heartbeatMs: 50 });
      const c = await paired();
      await new Promise((x) => setTimeout(x, 300));
      expect(c.ws.readyState).toBe(WebSocket.OPEN);
    });
  });

  describe("startup", () => {
    it("rejects a pairing code that is not 6 digits", async () => {
      for (const bad of ["12345", "1234567", "abcdef", "12 456", ""]) {
        await expect(startServer({ httpPort: 0, wsPort: 0, pairingCode: bad, holdMs: 1000, budgetUsd: 1 })).rejects.toThrow(/pairing/i);
      }
    });

    it("closes the HTTP server when the WS port cannot be bound", async () => {
      const blocker = createNetServer();
      await new Promise<void>((r) => blocker.listen(0, "0.0.0.0", () => r()));
      const taken = (blocker.address() as AddressInfo).port;
      const probe = createNetServer();
      await new Promise<void>((r) => probe.listen(0, "127.0.0.1", () => r()));
      const free = (probe.address() as AddressInfo).port;
      await new Promise<void>((r) => probe.close(() => r()));
      try {
        await expect(startServer({ httpPort: free, wsPort: taken, pairingCode: CODE, holdMs: 1000, budgetUsd: 1 })).rejects.toThrow();
        const code = await new Promise<string>((resolve) => {
          const req = httpRequest({ host: "127.0.0.1", port: free, path: "/ev", method: "POST" }, () => resolve("connected"));
          req.on("error", (e: NodeJS.ErrnoException) => resolve(e.code ?? "error"));
          req.end("{}");
        });
        expect(code).toBe("ECONNREFUSED");
      } finally {
        await new Promise<void>((r) => blocker.close(() => r()));
      }
    });
  });

  describe("stale prompts and isolation", () => {
    it("cancels the broker request when the hook connection goes away", async () => {
      await boot();
      const c = await paired();
      const req = httpRequest({ host: "127.0.0.1", port: handle.httpPort, path: "/perm", method: "POST", headers: JSON_HDR });
      req.on("error", () => {});
      req.end(JSON.stringify(permPayload()));
      const open = await c.next((m) => m.type === "perm_open");
      req.destroy();
      const closed = await c.next((m) => m.type === "perm_closed");
      expect(closed).toMatchObject({ id: open.request.id, reason: "hook-gone" });
    });

    it("a hook that disconnects while its body is being read never leaves a pending prompt", async () => {
      await boot();
      const c = await paired();
      const body = JSON.stringify(permPayload());
      for (let i = 0; i < 20; i++) {
        await new Promise<void>((resolve) => {
          const sock = createConnection({ host: "127.0.0.1", port: handle.httpPort }, () => {
            sock.write(
              `POST /perm HTTP/1.1\r\nHost: 127.0.0.1:${handle.httpPort}\r\nContent-Type: application/json\r\nContent-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`,
              () => {
                sock.destroy();
                resolve();
              },
            );
          });
          sock.on("error", () => {});
        });
      }
      await new Promise((x) => setTimeout(x, 300));
      const probe = await connect({ localAddress: "127.0.0.2" });
      probe.send({ type: "hello", session: c.session });
      const snap = await probe.next((m) => m.type === "snapshot");
      expect(snap.pending).toEqual([]);
    });

    it("an unpaired socket never sees agent, incident or perm traffic while a client is paired", async () => {
      await boot({ budgetUsd: 1 });
      const spy = await connect();
      const c = await paired();
      await post("/ev", { session_id: "s1", hook_event_name: "PreToolUse", tool_name: "Read" });
      await post("/status", { session_id: "s1", cost: { total_cost_usd: 3 } });
      const pending = post("/perm", permPayload());
      const open = await c.next((m) => m.type === "perm_open");
      c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
      await pending;
      await c.next((m) => m.type === "incident");
      expect(spy.msgs).toEqual([]);
    });

    it("refuses a replaced client's late perm_decision", async () => {
      await boot();
      const a = await paired();
      const pending = post("/perm", permPayload());
      const open = await a.next((m) => m.type === "perm_open");
      // Stall a's socket so it has not yet processed the server's close frame.
      (a.ws as any)._socket.pause();
      const b = await connect();
      b.send({ type: "hello", session: a.session });
      await b.next((m) => m.type === "snapshot");
      a.ws.send(JSON.stringify({ type: "perm_decision", decision: { id: open.request.id, behavior: "allow", confirm: "pinch-hold" } }));
      await new Promise((x) => setTimeout(x, 100));
      (a.ws as any)._socket.resume();
      // The server is already closing a, so it cannot reply; the allow must simply have had no effect.
      expect(await a.closed).toBe(4000);
      b.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
      expect(await (await pending).json()).toMatchObject({ hookSpecificOutput: { decision: { behavior: "deny" } } });
    });
  });

  describe("pairing", () => {
    it("the code is single-use; a session reconnects; newPairingCode issues a fresh code", async () => {
      await boot();
      const a = await paired();
      const second = await connect();
      second.send({ type: "hello", code: CODE });
      expect(await second.closed).toBe(4001);

      const viaSession = await connect();
      viaSession.send({ type: "hello", session: a.session });
      await viaSession.next((m) => m.type === "snapshot");
      expect(await a.closed).toBe(4000);

      const fresh = handle.newPairingCode();
      expect(fresh).toMatch(/^\d{6}$/);
      const d = await connect();
      d.send({ type: "hello", code: fresh });
      await d.next((m) => m.type === "snapshot");
      const newSession = (await d.next((m) => m.type === "paired")).session;
      expect(newSession).not.toBe(a.session);
      const stale = await connect();
      stale.send({ type: "hello", session: a.session });
      expect(await stale.closed).toBe(4001);
    });

    it("a wrong session fails with 4001", async () => {
      await boot();
      const c = await connect();
      c.send({ type: "hello", session: "x".repeat(43) });
      expect(await c.closed).toBe(4001);
    });

    it("locks one address after 5 failures and unlocks after 60 s", async () => {
      let now = 1_000_000;
      await boot({ now: () => now });
      for (let i = 0; i < 5; i++) {
        const c = await connect();
        c.send({ type: "hello", code: "999999" });
        expect(await c.closed).toBe(4001);
      }
      const locked = await connect();
      locked.send({ type: "hello", code: CODE });
      expect(await locked.closed).toBe(4029);
      now += 61_000;
      const ok = await connect();
      ok.send({ type: "hello", code: CODE });
      await ok.next((m) => m.type === "snapshot");
    });

    it("global ceiling invalidates the code; sessions still pair; newPairingCode restores pairing", async () => {
      await boot();
      const a = await paired();
      const code = handle.newPairingCode();
      for (let addr = 2; addr < 8; addr++) {
        for (let i = 0; i < 5; i++) {
          const c = await connect({ localAddress: `127.0.0.${addr}` });
          c.send({ type: "hello", code: "000000" });
          expect(await c.closed).toBe(4001);
        }
      }
      const fresh = await connect({ localAddress: "127.0.0.20" });
      fresh.send({ type: "hello", code }); // the right code, but it was invalidated
      expect(await fresh.closed).toBe(4001);
      const viaSession = await connect({ localAddress: "127.0.0.21" });
      viaSession.send({ type: "hello", session: a.session });
      await viaSession.next((m) => m.type === "snapshot");
      const code2 = handle.newPairingCode();
      const again = await connect({ localAddress: "127.0.0.22" });
      again.send({ type: "hello", code: code2 });
      await again.next((m) => m.type === "snapshot");
    });

    it("the code expires after 5 minutes until newPairingCode is called", async () => {
      let now = 5_000_000;
      await boot({ now: () => now });
      now += 5 * 60_000 - 1;
      const early = await connect();
      early.send({ type: "hello", code: CODE });
      await early.next((m) => m.type === "snapshot"); // still valid just before expiry
      now += 1;
      const code = handle.newPairingCode(); // fresh code, fresh 5 minutes
      now += 5 * 60_000 - 1;
      const ok = await connect({ localAddress: "127.0.0.2" });
      ok.send({ type: "hello", code });
      await ok.next((m) => m.type === "snapshot");
      const code2 = handle.newPairingCode();
      now += 5 * 60_000;
      const late = await connect({ localAddress: "127.0.0.3" });
      late.send({ type: "hello", code: code2 });
      expect(await late.closed).toBe(4001);
      const code3 = handle.newPairingCode();
      const restored = await connect({ localAddress: "127.0.0.4" });
      restored.send({ type: "hello", code: code3 });
      await restored.next((m) => m.type === "snapshot");
    });

    it("lockout of one address does not stop another address pairing", async () => {
      await boot();
      for (let i = 0; i < 5; i++) {
        const c = await connect();
        c.send({ type: "hello", code: "999999" });
        await c.closed;
      }
      const other = await connect({ localAddress: "127.0.0.2" });
      other.send({ type: "hello", code: CODE });
      await other.next((m) => m.type === "snapshot");
    });
  });
});

describe("final wave", () => {
  it("F3: onPaired fires once on the first successful hello; a replay started there emits nothing before pairing", async () => {
    const seen: string[] = [];
    let pairedCalls = 0;
    await boot({
      onEvent: (e) => seen.push(e.sid),
      onPaired: () => {
        pairedCalls++;
        replay([{ sid: "demo-1", t: 0, kind: "prompt" }], (e) => handle.inject(e), { speed: 1 });
      },
    });
    const early = await connect();
    await new Promise((x) => setTimeout(x, 100));
    expect(pairedCalls).toBe(0);
    expect(seen).toEqual([]);
    early.ws.terminate();
    const c = await paired();
    const agent = await c.next((m) => m.type === "agent");
    expect(agent.agent.sid).toBe("demo-1");
    expect(seen).toEqual(["demo-1"]);
    const again = await connect({ localAddress: "127.0.0.2" });
    again.send({ type: "hello", session: c.session });
    await again.next((m) => m.type === "snapshot");
    expect(pairedCalls).toBe(1);
  });

  it("F8: a frame arriving on a socket that is already closing is ignored and does not burn the code", async () => {
    await boot();
    const c = await connect();
    c.send({ type: "hello", code: "999999" }); // rejected: the server starts closing this socket
    c.send({ type: "hello", code: CODE }); // arrives while the server side is CLOSING
    expect(await c.closed).toBe(4001);
    expect(c.msgs.some((m) => m.type === "snapshot")).toBe(false);
    const fresh = await connect({ localAddress: "127.0.0.2" });
    fresh.send({ type: "hello", code: CODE });
    await fresh.next((m) => m.type === "paired");
  });

  it("F10: errors on the HTTP server and the WebSocket server after listening are logged, never thrown", async () => {
    await boot();
    const servers = (process as any)._getActiveHandles().filter(
      (h: any) => typeof h?.address === "function" && typeof h?.listen === "function",
    );
    const byPort = (p: number) => servers.find((s: any) => s.address()?.port === p);
    const httpSrv = byPort(handle.httpPort);
    const wsSrv = byPort(handle.wsPort); // ws forwards its internal server's errors to the WebSocketServer
    expect(httpSrv && wsSrv).toBeTruthy();
    const errors: string[] = [];
    const spy = vi.spyOn(console, "error").mockImplementation((...a: unknown[]) => void errors.push(a.join(" ")));
    try {
      for (let i = 0; i < 2; i++) {
        expect(() => httpSrv.emit("error", new Error("boom-http"))).not.toThrow();
        expect(() => wsSrv.emit("error", new Error("boom-ws"))).not.toThrow();
      }
    } finally {
      spy.mockRestore();
    }
    expect(errors.filter((e) => e.includes("boom-http")).length).toBeGreaterThanOrEqual(1);
    expect(errors.filter((e) => e.includes("boom-ws")).length).toBeGreaterThanOrEqual(1);
    expect(errors.every((e) => !e.includes("\n"))).toBe(true);
    expect((await post("/ev", { session_id: "s1", hook_event_name: "Stop" })).status).toBe(200);
  });
});

describe("final wave F4", () => {
  it("a recorded in-project Write replayed via inject with demoRoot classifies safe", async () => {
    const W = (p: string) => p.replace(/\//g, "\\");
    const sandbox = W("C:/Users/linus/Documents/Lotse/sandbox");
    const file = path.join(tempDir("rec-"), "r.ndjson");
    // The CLI runs from bridge/, the recorded session from the sandbox.
    const rec = new Recorder(file, { repoRoot: W("C:/Users/linus/Documents/Lotse/bridge"), homeDir: W("C:/Users/linus") });
    rec.write({ sid: "s", t: 0, kind: "permission", tool: "Write", path: W(`${sandbox}/hello.txt`), cwd: sandbox });
    await rec.close();
    const [e] = await readRecording(file);
    expect(e).toMatchObject({ path: "<repo>/hello.txt", cwd: "<repo>" });
    await boot({ demoRoot: tempDir("lotse-demo-") });
    const c = await paired();
    handle.inject(e);
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.request.risk.risk).toBe("safe");
  });
});

describe("final wave F6", () => {
  it("broadcasts budget_near as an incident message", async () => {
    await boot({ budgetUsd: 15 });
    const c = await paired();
    await post("/status", { session_id: "s1", cost: { total_cost_usd: 10.5 } });
    const inc = await c.next((m) => m.type === "incident");
    expect(inc.incident).toMatchObject({ kind: "budget_near", sids: ["s1"] });
    await post("/status", { session_id: "s1", cost: { total_cost_usd: 15 } });
    expect((await c.next((m) => m.type === "incident" && m.incident.kind === "budget")).incident.sids).toEqual(["s1"]);
  });
});

describe("residual R1: <abs> placeholder", () => {
  it("a recorded Write to <abs>/hosts with cwd <repo> is destructive after inject", async () => {
    await boot({ demoRoot: tempDir("lotse-demo-") });
    const c = await paired();
    handle.inject({ sid: "d1", t: 1, kind: "permission", cwd: "<repo>", tool: "Write", path: "<abs>/hosts" });
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.request.risk.risk).toBe("destructive");
    expect(open.request.path).toBe(path.join(path.parse(os.tmpdir()).root, "__lotse_abs__", "hosts"));
  });

  it("a cwd of <abs>/proj does not make a <repo> Write safe", async () => {
    await boot({ demoRoot: tempDir("lotse-demo-") });
    const c = await paired();
    handle.inject({ sid: "d1", t: 1, kind: "permission", cwd: "<abs>/proj", tool: "Write", path: "<repo>/a.ts" });
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.request.risk.risk).not.toBe("safe");
  });
});

describe("explanations (Task 2)", () => {
  const aiJson = JSON.stringify({ erklaerung: "Löscht das ganze Wurzelverzeichnis.", antworten: ["Nein, erst Backup", "Nur den Unterordner"] });
  const quiet = { log: () => {} };
  /** Deterministic absence check: a bad frame's error reply arrives after anything the server queued earlier. */
  const noExplainFor = async (c: Client, id: string) => {
    const before = c.msgs.length;
    c.send("not json");
    await c.next((m) => m.type === "error" && m.error === "bad-message");
    expect(c.msgs.slice(0, before).some((m) => m.type === "perm_explain" && m.id === id)).toBe(false);
    expect(c.msgs.some((m) => m.type === "perm_explain" && m.id === id)).toBe(false);
  };
  /** Resolves once the fake runner has produced its answer (the server may only act on it afterwards). */
  const finished = () => {
    let done!: () => void;
    const p = new Promise<void>((r) => (done = r));
    return { p, done };
  };

  it("perm_open carries the rule explanation next to (not inside) the request", async () => {
    await boot();
    const c = await paired();
    void post("/perm", permPayload("rm -rf ../legacy-site"));
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.explain.source).toBe("rules");
    expect(open.explain.text.length).toBeGreaterThan(0);
    expect(open.explain.replies.length).toBeGreaterThanOrEqual(2);
    expect(open.request.explain).toBeUndefined();
  });

  it("sends perm_explain after the fake runner resolves, and perm_open comes first", async () => {
    const run = vi.fn(async () => aiJson);
    await boot({ explainer: new Explainer(run, quiet) });
    const c = await paired();
    void post("/perm", permPayload("rm -rf /"));
    const open = await c.next((m) => m.type === "perm_open");
    const ex = await c.next((m) => m.type === "perm_explain");
    expect(ex.id).toBe(open.request.id);
    expect(ex.explain).toMatchObject({ source: "ai", text: open.explain.text, aiText: "Löscht das ganze Wurzelverzeichnis." });
    expect(c.msgs.indexOf(open)).toBeLessThan(c.msgs.indexOf(ex));
    expect(run).toHaveBeenCalledTimes(1);
  });

  it("no explainer means no AI call and no perm_explain", async () => {
    await boot();
    const c = await paired();
    void post("/perm", permPayload());
    const open = await c.next((m) => m.type === "perm_open");
    await noExplainFor(c, open.request.id);
  });

  it("sends no perm_explain when the request was decided before the runner finished", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const ran = finished();
    await boot({ explainer: new Explainer(async () => (await gate, ran.done(), aiJson), quiet) });
    const c = await paired();
    const pending = post("/perm", permPayload());
    const open = await c.next((m) => m.type === "perm_open");
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
    await c.next((m) => m.type === "perm_closed");
    release();
    await ran.p;
    await noExplainFor(c, open.request.id);
    expect(await (await pending).json()).toMatchObject({ hookSpecificOutput: { decision: { behavior: "deny" } } });
  });

  it("re-checks pending right before sending, even if the explainer ignores isOpen (cached result)", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const ran = finished();
    const stub = {
      explain: async () => (await gate, ran.done(), { text: "spät", replies: ["Nein"], source: "ai" as const }),
    } as unknown as Explainer;
    await boot({ explainer: stub });
    const c = await paired();
    void post("/perm", permPayload());
    const open = await c.next((m) => m.type === "perm_open");
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
    await c.next((m) => m.type === "perm_closed");
    release();
    await ran.p;
    await noExplainFor(c, open.request.id);
  });

  it("a throwing runner changes neither the decision path nor the hold timer", async () => {
    const run = vi.fn(async () => {
      throw new Error("claude not installed");
    });
    await boot({ holdMs: 600, explainer: new Explainer(run, quiet) });
    const c = await paired();
    const first = post("/perm", permPayload());
    const open = await c.next((m) => m.type === "perm_open");
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "allow", confirm: "pinch-hold" } });
    expect(await (await first).json()).toEqual(allow);

    const t = Date.now();
    const second = post("/perm", permPayload("pwd"));
    const open2 = await c.next((m) => m.type === "perm_open" && m.request.id !== open.request.id);
    expect(open2.request.expiresAt - open2.request.createdAt).toBe(600);
    expect(await (await second).json()).toEqual({});
    const waited = Date.now() - t;
    expect(waited).toBeGreaterThanOrEqual(500);
    expect(waited).toBeLessThan(1500);
    expect((await c.next((m) => m.type === "perm_closed" && m.id === open2.request.id)).reason).toBe("timeout");
    expect(run).toHaveBeenCalled();
    expect(c.msgs.some((m) => m.type === "perm_explain")).toBe(false);
  });

  it("a deny with a reply as message reaches the hook answer unchanged", async () => {
    await boot({ explainer: new Explainer(async () => aiJson, quiet) });
    const c = await paired();
    const pending = post("/perm", permPayload("rm -rf /"));
    const open = await c.next((m) => m.type === "perm_open");
    const reply = (await c.next((m) => m.type === "perm_explain")).explain.replies[0];
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny", message: reply } });
    expect(await (await pending).json()).toEqual({
      hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message: "Nein, erst Backup" } },
    });
  });

  it("regression: an allow is gated exactly as before while a non-reassuring AI text is shown", async () => {
    await boot({ explainer: new Explainer(async () => aiJson, quiet) });
    const c = await paired();
    const pending = post("/perm", permPayload("rm -rf /"));
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.request.risk.risk).toBe("destructive");
    const ex = await c.next((m) => m.type === "perm_explain");
    expect(ex.explain.source).toBe("ai");
    const errors = () => c.msgs.filter((x) => x.type === "error");
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "allow", confirm: "pinch-hold" } });
    await c.next(() => errors().length === 1);
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "allow" } });
    await c.next(() => errors().length === 2);
    expect(errors()).toEqual([
      { type: "error", error: "needs-two-hand" },
      { type: "error", error: "needs-two-hand" },
    ]);
    c.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "allow", confirm: "two-hand" } });
    expect(await (await pending).json()).toEqual(allow);
  });
});

describe("snapshot explains", () => {
  const quiet = { log: () => {} };
  const ai = JSON.stringify({ erklaerung: "KI-Text", antworten: ["Nein"] });
  const resume = async (session: string) => {
    const c = await connect();
    c.send({ type: "hello", session });
    await c.next((m) => m.type === "snapshot");
    return c;
  };

  it("a client connecting while a request is pending gets the rule, then the AI, explanation in its snapshot; closed ones are dropped", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    await boot({ explainer: new Explainer(async () => (await gate, ai), quiet) });
    const c1 = await paired();
    const pending = post("/perm", permPayload("rm -rf /"));
    const open = await c1.next((m) => m.type === "perm_open");

    const c2 = await resume(c1.session!); // replaces c1 while the AI is still running
    const snap = c2.msgs.find((m) => m.type === "snapshot")!;
    expect(snap.pending.map((r: Msg) => r.id)).toEqual([open.request.id]);
    expect(snap.explains[open.request.id]).toEqual(open.explain);

    release();
    await c2.next((m) => m.type === "perm_explain");
    const c3 = await resume(c1.session!);
    const snap3 = c3.msgs.find((m) => m.type === "snapshot")!;
    expect(snap3.explains[open.request.id]).toMatchObject({ source: "ai", text: open.explain.text, aiText: "KI-Text" });

    c3.send({ type: "perm_decision", decision: { id: open.request.id, behavior: "deny" } });
    await pending;
    const c4 = await resume(c1.session!);
    expect(c4.msgs.find((m) => m.type === "snapshot")).toMatchObject({ pending: [], explains: {} });
  });
});

describe("the AI never replaces the danger explanation", () => {
  const quiet = { log: () => {} };
  const benign = JSON.stringify({ erklaerung: "Claude räumt einen alten Ordner auf.", antworten: ["Zeig mir erst die Liste."] });
  const noExplainFor = async (c: Client, id: string) => {
    c.send("not json");
    await c.next((m) => m.type === "error" && m.error === "bad-message");
    expect(c.msgs.some((m) => m.type === "perm_explain" && m.id === id)).toBe(false);
  };

  it("destructive: perm_explain keeps the rule text and carries the AI sentence as aiText", async () => {
    await boot({ explainer: new Explainer(async () => benign, quiet) });
    const c = await paired();
    void post("/perm", permPayload("rm -rf ../legacy-site"));
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.request.risk.risk).toBe("destructive");
    const ex = await c.next((m) => m.type === "perm_explain");
    expect(ex.explain).toEqual({ text: open.explain.text, aiText: "Claude räumt einen alten Ordner auf.", replies: ["Zeig mir erst die Liste."], source: "ai" });
  });

  it("safe: perm_explain still carries the AI text", async () => {
    await boot({ explainer: new Explainer(async () => benign, quiet) });
    const c = await paired();
    void post("/perm", permPayload("ls"));
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.request.risk.risk).toBe("safe");
    const ex = await c.next((m) => m.type === "perm_explain");
    expect(ex.explain).toEqual({ text: "Claude räumt einen alten Ordner auf.", replies: ["Zeig mir erst die Liste."], source: "ai" });
  });

  it("a truncated destructive request gets no perm_explain and no AI call", async () => {
    const run = vi.fn(async () => benign);
    await boot({ explainer: new Explainer(run, quiet) });
    const c = await paired();
    void post("/perm", permPayload(`echo "${"x".repeat(300)}"; rm -rf ./src`));
    const open = await c.next((m) => m.type === "perm_open");
    expect(open.request).toMatchObject({ truncated: true, risk: { risk: "destructive" } });
    expect(open.explain.source).toBe("rules");
    await noExplainFor(c, open.request.id);
    expect(run).not.toHaveBeenCalled();
  });
});

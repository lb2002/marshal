import { mkdtempSync, readFileSync, realpathSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import type { AgentEvent } from "../src/events.js";
import { Explainer, forSource } from "../src/explain.js";
import { buildPack, parseOverrides, parsePackAgents, parsePackReplies } from "../src/pack.js";
import type { DemoPack } from "../src/pack.js";
import type { ServerMessage } from "../src/protocol.js";
import { COLLISION_WINDOW_MS, createPipeline } from "../src/pipeline.js";
import { readRecording } from "../src/recorder.js";
import { startServer } from "../src/server.js";
import type { ServerHandle } from "../src/server.js";
import { WebSocket } from "ws";

const DEMO = resolve(import.meta.dirname, "../../demo");
const DESTRUCTIVE = ["rm -rf ../legacy-site", "git push --force origin main"];
const FORBIDDEN = ["C:\\", "/Users/", "/home/", "sk-", "ghp_"];

let root: string;
let events: AgentEvent[];

beforeAll(async () => {
  root = realpathSync(mkdtempSync(join(tmpdir(), "lotse-pack-")));
  events = await readRecording(join(DEMO, "jury.ndjson"));
});
afterAll(() => rmSync(root, { recursive: true, force: true }));

type Msg<T extends ServerMessage["type"]> = Extract<ServerMessage, { type: T }>;
const ofType = <T extends ServerMessage["type"]>(pack: DemoPack, type: T) =>
  pack.messages.filter((m) => m.msg.type === type).map((m) => ({ t: m.t, msg: m.msg as Msg<T> }));

describe("buildPack on demo/jury.ndjson (explain off)", () => {
  let pack: DemoPack;
  beforeAll(async () => {
    pack = await buildPack(events, { budgetUsd: 15, demoRoot: root });
  });

  it("has 19 perm_open, exactly the two destructive ones, one collision and one budget_near, sorted by t", () => {
    const opens = ofType(pack, "perm_open");
    expect(opens).toHaveLength(19);
    const destructive = opens.filter((o) => o.msg.request.risk.risk === "destructive").map((o) => o.msg.request.command);
    expect(destructive).toEqual(DESTRUCTIVE);
    const incidents = ofType(pack, "incident").map((i) => i.msg.incident.kind);
    expect(incidents.filter((k) => k === "collision")).toHaveLength(1);
    expect(incidents.filter((k) => k === "budget_near")).toHaveLength(1);
    expect(incidents.filter((k) => k === "budget")).toHaveLength(0);
    const ts = pack.messages.map((m) => m.t);
    expect(ts).toEqual([...ts].sort((a, b) => a - b));
  });

  it("starts with the empty snapshot and carries version, budget and duration", () => {
    expect(pack.version).toBe(1);
    expect(pack.budgetUsd).toBe(15);
    expect(pack.messages[0]).toEqual({ t: 0, msg: { type: "snapshot", agents: [], pending: [], explains: {} } });
    expect(pack.durationMs).toBe(events[events.length - 1].t);
  });

  it("builds perm_open like the live broker: ids p1.., createdAt = t, expiresAt = t + 90000, rules explain, placeholder paths", () => {
    const opens = ofType(pack, "perm_open");
    opens.forEach((o, i) => {
      const r = o.msg.request;
      expect(r.id).toBe(`p${i + 1}`);
      expect(r.createdAt).toBe(o.t);
      expect(r.expiresAt).toBe(o.t + 90_000);
      expect(r.truncated).toBe(false);
      expect(o.msg.explain.source).toBe("rules");
      expect(o.msg.explain.text.length).toBeGreaterThan(0);
    });
    const edit = opens[0].msg.request;
    expect(edit).toMatchObject({ sid: "demo-a1", tool: "Edit", path: "<repo>/src/components/Cart.tsx", paths: ["<repo>/src/components/Cart.tsx"] });
    expect(edit.risk.risk).toBe("safe");
    const rm = opens.find((o) => o.msg.request.command === "rm -rf ../legacy-site")!.msg.request;
    expect(rm).toMatchObject({ tool: "Bash", commandLength: "rm -rf ../legacy-site".length, paths: [] });
  });

  it("the rule explanation shows the project-relative path, without <repo>/", () => {
    const opens = ofType(pack, "perm_open");
    expect(opens[0].msg.explain.text).toBe("Claude changes the file: src/components/Cart.tsx");
    for (const o of opens) expect(o.msg.explain.text.includes("<repo>"), o.msg.explain.text).toBe(false);
  });

  it("the agent message before each perm_open shows the agent blocked at that moment (not its final state)", () => {
    const i = pack.messages.findIndex((m) => m.msg.type === "perm_open");
    const before = pack.messages[i - 1].msg as Msg<"agent">;
    expect(before.type).toBe("agent");
    expect(before.agent).toMatchObject({ sid: "demo-a1", status: "blocked" });
  });

  it("gives every request default consequences: a {sid, status, lastAction} patch, Allowed / Denied", () => {
    expect(Object.keys(pack.perms)).toHaveLength(19);
    for (const o of ofType(pack, "perm_open")) {
      const { onAllow, onDeny } = pack.perms[o.msg.request.id];
      for (const c of [onAllow, onDeny]) {
        expect(Object.keys(c).sort()).toEqual(["lastAction", "sid", "status"]);
        expect(c.sid).toBe(o.msg.request.sid);
        expect(c.status).toBe("working");
      }
    }
    const rmId = ofType(pack, "perm_open").find((o) => o.msg.request.command === "rm -rf ../legacy-site")!.msg.request.id;
    expect(pack.perms[rmId]).toEqual({
      onAllow: { sid: "demo-a3", status: "working", lastAction: "Allowed: Bash rm -rf ../legacy-site" },
      onDeny: { sid: "demo-a3", status: "working", lastAction: "Denied: Bash rm -rf ../legacy-site" },
    });
    expect(pack.perms.p1.onAllow.lastAction).toBe("Allowed: Edit components/Cart.tsx");
  });

  it("contains no personal paths, secrets or the demo root", () => {
    const json = JSON.stringify(pack);
    for (const bad of [...FORBIDDEN, root, root.replace(/\\/g, "/"), "__lotse_abs__"]) expect(json.includes(bad), bad).toBe(false);
  });
});

describe("buildPack mapping and explanations", () => {
  it("classifies with the same <repo>/<abs> mapping as inject, but keeps the placeholders in the request", async () => {
    const pack = await buildPack(
      [
        { sid: "d1", t: 1, kind: "permission", cwd: "<repo>", tool: "Write", path: "<repo>/src/a.ts" },
        { sid: "d1", t: 2, kind: "permission", cwd: "<repo>", tool: "Write", path: "<abs>/hosts" },
      ],
      { budgetUsd: 15, demoRoot: root },
    );
    const [inRepo, abs] = ofType(pack, "perm_open").map((o) => o.msg.request);
    expect(inRepo.risk.risk).toBe("safe");
    expect(abs.risk.risk).toBe("destructive");
    expect(abs.path).toBe("<abs>/hosts");
  });

  it("an override replaces the onAllow/onDeny text and the explanation", async () => {
    const overrides = parseOverrides({
      p11: {
        onAllow: "Hat ../legacy-site mit 214 Dateien gelöscht.",
        onDeny: "Zeigt stattdessen die Liste der 214 Dateien.",
        explain: { text: "Löscht den Ordner legacy-site neben deinem Projekt, samt allen Unterordnern.", replies: ["Zeig mir erst die Liste.", "Nicht löschen."] },
      },
    });
    const pack = await buildPack(events, { budgetUsd: 15, demoRoot: root, overrides });
    const open = ofType(pack, "perm_open").find((o) => o.msg.request.id === "p11")!.msg;
    expect(open.request.command).toBe("rm -rf ../legacy-site");
    expect(open.explain).toEqual({ text: "Löscht den Ordner legacy-site neben deinem Projekt, samt allen Unterordnern.", replies: ["Zeig mir erst die Liste.", "Nicht löschen."], source: "manual" });
    expect(pack.perms.p11.onAllow).toEqual({ sid: "demo-a3", status: "working", lastAction: "Hat ../legacy-site mit 214 Dateien gelöscht." });
    expect(pack.perms.p11.onDeny.lastAction).toBe("Zeigt stattdessen die Liste der 214 Dateien.");
    expect(pack.perms.p10.onAllow.lastAction).toBe("Allowed: Bash npm test");
  });

  it("an override for a request that is not in the pack is an error", async () => {
    await expect(buildPack(events, { budgetUsd: 15, demoRoot: root, overrides: { p99: { onAllow: "x" } } })).rejects.toThrow(/p99/);
  });

  it("with an explainer, the AI text replaces the rules text only when safe, else it is aiText; calls run one at a time with isOpen true", async () => {
    let active = 0;
    let maxActive = 0;
    let calls = 0;
    const runner = async () => {
      calls++;
      active++;
      maxActive = Math.max(maxActive, active);
      await new Promise((r) => setTimeout(r, 2));
      active--;
      return JSON.stringify({ erklaerung: "Claude will etwas tun.", antworten: ["Bitte nicht."] });
    };
    const explainer = new Explainer(runner, { concurrency: 2, log: () => {} });
    const progress: string[] = [];
    const pack = await buildPack(events, { budgetUsd: 15, demoRoot: root, explainer, onProgress: (d, n) => progress.push(`${d}/${n}`) });
    const opens = ofType(pack, "perm_open");
    const rules = ofType(await buildPack(events, { budgetUsd: 15, demoRoot: root }), "perm_open").map((o) => o.msg.explain.text);
    // The rules texts above already name the agent; a Gemini card's safe AI text gets the Deny-only prefix.
    const sourceOf = (sid: string) => events.find((e) => e.sid === sid)?.source;
    opens.forEach((o, i) => {
      const { explain, request } = o.msg;
      const safe = forSource({ text: "Claude will etwas tun.", replies: ["Bitte nicht."], source: "ai" }, sourceOf(request.sid));
      if (request.risk.risk === "safe") expect(explain).toEqual(safe);
      else expect(explain).toEqual({ text: rules[i], aiText: "Claude will etwas tun.", replies: ["Bitte nicht."], source: "ai" });
    });
    expect(opens.some((o) => sourceOf(o.msg.request.sid) === "gemini")).toBe(true);
    expect(opens.some((o) => o.msg.explain.text.includes("Deny only"))).toBe(false);
    expect(opens.filter((o) => o.msg.request.risk.risk !== "safe").length).toBeGreaterThan(0);
    expect(maxActive).toBe(1);
    expect(calls).toBeGreaterThan(0);
    expect(progress).toHaveLength(19);
    expect(progress[18]).toBe("19/19");
  });

  it("keeps the rules text when the explainer gives nothing, and skips it for an overridden explanation", async () => {
    const prompts: string[] = [];
    const explainer = new Explainer(async (p) => (prompts.push(p), "kein JSON"), { log: () => {} });
    const overrides = parseOverrides({ p1: { explain: { text: "Ändert den Warenkorb.", replies: ["Nicht jetzt."] } } });
    const pack = await buildPack(events.slice(0, 50), { budgetUsd: 15, demoRoot: root, explainer, overrides });
    const opens = ofType(pack, "perm_open");
    expect(opens[0].msg.explain.text).toBe("Ändert den Warenkorb.");
    expect(opens.slice(1).every((o) => o.msg.explain.source === "rules")).toBe(true);
    expect(prompts.some((p) => p.includes("Cart.tsx"))).toBe(false);
  });
});

describe("parseOverrides", () => {
  it("accepts the documented shape", () => {
    expect(parseOverrides({ p2: { onDeny: "Nein." } })).toEqual({ p2: { onDeny: "Nein." } });
    const impact = { kind: "delete", target: "../x", files: 3, filesCapped: false, bytes: 10, outsideProject: true, recoverable: "no", reason: "Not in a Git project: gone for good." };
    expect(parseOverrides({ p2: { impact } })).toEqual({ p2: { impact } });
  });

  it.each([
    ["not an object", []],
    ["a bad key", { x1: { onAllow: "a" } }],
    ["an unknown field", { p1: { onAlow: "a" } }],
    ["an empty text", { p1: { onAllow: " " } }],
    ["an overlong consequence", { p1: { onAllow: "x".repeat(81) } }],
    ["an overlong explanation", { p1: { explain: { text: "x".repeat(141), replies: ["a"] } } }],
    ["an overlong reply", { p1: { explain: { text: "a", replies: ["x".repeat(71)] } } }],
    ["too many replies", { p1: { explain: { text: "a", replies: ["a", "b", "c", "d"] } } }],
    ["no replies", { p1: { explain: { text: "a", replies: [] } } }],
    ["a bidi character", { p1: { explain: { text: "a\u202eb", replies: ["a"] } } }],
    ["a control character", { p1: { onDeny: "a\u0007b" } }],
    ["an impact with an unknown kind", { p1: { impact: { kind: "nuke", recoverable: "no", reason: "a" } } }],
    ["an impact with an unknown verdict", { p1: { impact: { kind: "delete", recoverable: "maybe", reason: "a" } } }],
    // Never a reassurance (ruling after the security review): "yes" and "partly" are gone, and so is commitsLost.
    ["an impact that says recoverable", { p1: { impact: { kind: "delete", recoverable: "yes", reason: "a" } } }],
    ["an impact that says partly recoverable", { p1: { impact: { kind: "delete", recoverable: "partly", reason: "a" } } }],
    ["an impact with commitsLost", { p1: { impact: { kind: "force_push", commitsLost: 2, recoverable: "no", reason: "a" } } }],
    ["an impact without a reason", { p1: { impact: { kind: "delete", recoverable: "no" } } }],
    ["an impact with negative files", { p1: { impact: { kind: "delete", files: -1, recoverable: "no", reason: "a" } } }],
    ["an impact with a fractional count", { p1: { impact: { kind: "delete", bytes: 1.5, recoverable: "no", reason: "a" } } }],
    ["an impact with an unknown field", { p1: { impact: { kind: "delete", recoverable: "no", reason: "a", extra: 1 } } }],
    ["an impact with an overlong target", { p1: { impact: { kind: "delete", target: "x".repeat(121), recoverable: "no", reason: "a" } } }],
    ["an impact with a non-boolean flag", { p1: { impact: { kind: "delete", outsideProject: "yes", recoverable: "no", reason: "a" } } }],
  ])("rejects %s", (_name, value) => {
    expect(() => parseOverrides(value)).toThrow();
  });
});

describe("pack agent details (contract: agents[sid] = { title, summary, recent[], waitingFor })", () => {
  const detail = { title: "Warenkorb umbauen", summary: "Baut den Warenkorb um.", recent: ["Edits src/cart.ts", "Runs tests"], waitingFor: null };
  const rec: AgentEvent[] = [
    { sid: "a1", t: 0, kind: "session_start", cwd: "<repo>" },
    { sid: "a1", t: 10, kind: "tool_start", tool: "Read", path: "<repo>/src/cart.ts", cwd: "<repo>" },
    { sid: "a2", t: 20, kind: "session_start", cwd: "<repo>" },
  ];

  it("parsePackAgents accepts the documented shape; parseOverrides leaves the agents key to it", () => {
    expect(parsePackAgents({ agents: { a1: detail }, p1: { onDeny: "x" } })).toEqual({ a1: detail });
    expect(parsePackAgents({ agents: { a1: { ...detail, waitingFor: "Wartet auf die Freigabe." } } }).a1.waitingFor).toBe("Wartet auf die Freigabe.");
    expect(parsePackAgents({ p1: {} })).toEqual({});
    expect(parseOverrides({ agents: { a1: detail }, p2: { onDeny: "Nein." } })).toEqual({ p2: { onDeny: "Nein." } });
  });

  it.each([
    ["not an object", []],
    ["a bad sid", { "a b": detail }],
    ["a missing field", { a1: { title: "t", summary: "s", recent: [] } }],
    ["an unknown field", { a1: { ...detail, extra: 1 } }],
    ["an overlong title", { a1: { ...detail, title: "x".repeat(41) } }],
    ["an overlong summary", { a1: { ...detail, summary: "x".repeat(281) } }],
    ["six recent entries", { a1: { ...detail, recent: ["a", "b", "c", "d", "e", "f"] } }],
    ["an overlong recent entry", { a1: { ...detail, recent: ["x".repeat(81)] } }],
    ["an overlong waitingFor", { a1: { ...detail, waitingFor: "x".repeat(141) } }],
    ["a waitingFor that is not text or null", { a1: { ...detail, waitingFor: 3 } }],
    ["a control character", { a1: { ...detail, summary: "a\u0007b" } }],
    ["a bidi character", { a1: { ...detail, title: "a\u202eb" } }],
  ])("rejects %s", (_name, agents) => {
    expect(() => parsePackAgents({ agents })).toThrow();
  });

  it("accepts a summary longer than an explanation (up to 280 characters)", () => {
    const summary = "Wort ".repeat(50).trim();
    expect(summary.length).toBeGreaterThan(140);
    expect(parsePackAgents({ agents: { a1: { ...detail, summary } } }).a1.summary).toBe(summary);
  });

  it("buildPack puts the details at the top level and the title on that agent's messages", async () => {
    const pack = await buildPack(rec, { budgetUsd: 5, demoRoot: root, agents: { a1: detail } });
    expect(pack.agents).toEqual({ a1: detail });
    const agents = ofType(pack, "agent").map((m) => m.msg.agent);
    expect(agents.filter((a) => a.sid === "a1").every((a) => a.title === "Warenkorb umbauen")).toBe(true);
    expect(agents.filter((a) => a.sid === "a2").every((a) => a.title === undefined)).toBe(true);
  });

  it("leaves agents out without details, refuses details for an unknown agent, and leak-checks them", async () => {
    expect(await buildPack(rec, { budgetUsd: 5, demoRoot: root })).not.toHaveProperty("agents");
    await expect(buildPack(rec, { budgetUsd: 5, demoRoot: root, agents: { nobody: detail } })).rejects.toThrow(/agents.*nobody/);
    await expect(buildPack(rec, { budgetUsd: 5, demoRoot: root, agents: { a1: { ...detail, summary: "Reads C:\\x\\y" } } })).rejects.toThrow(/agents/);
  });
});

describe("pack replies (canned agent reaction to a reply from the headset)", () => {
  const rec: AgentEvent[] = [{ sid: "a1", t: 0, kind: "session_start", cwd: "<repo>" }];
  it("parsePackReplies accepts one clean line per sid, at most 80 characters; parseOverrides ignores the key", () => {
    expect(parsePackReplies({ replies: { a1: "Verstanden – ich mache das so." } })).toEqual({ a1: "Verstanden – ich mache das so." });
    expect(parsePackReplies({})).toEqual({});
    expect(parseOverrides({ replies: { a1: "x" } })).toEqual({});
    for (const bad of [[], { "a b": "x" }, { a1: "" }, { a1: 3 }, { a1: "x".repeat(81) }, { a1: "a\u0007b" }]) {
      expect(() => parsePackReplies({ replies: bad }), JSON.stringify(bad)).toThrow();
    }
  });
  it("buildPack carries them at the top level, refuses unknown sids and leak-checks them", async () => {
    const pack = await buildPack(rec, { budgetUsd: 5, demoRoot: root, replies: { a1: "Verstanden." } });
    expect(pack.replies).toEqual({ a1: "Verstanden." });
    expect(await buildPack(rec, { budgetUsd: 5, demoRoot: root })).not.toHaveProperty("replies");
    await expect(buildPack(rec, { budgetUsd: 5, demoRoot: root, replies: { nobody: "x" } })).rejects.toThrow(/replies.*nobody/);
    await expect(buildPack(rec, { budgetUsd: 5, demoRoot: root, replies: { a1: "Lege C:/x an" } })).rejects.toThrow(/replies/);
  });
});

describe("committed demo pack", () => {
  it("demo/jury.overrides.json has a canned reply for demo-a1..a4, and the pack carries them", () => {
    const raw = JSON.parse(readFileSync(join(DEMO, "jury.overrides.json"), "utf8"));
    const replies = parsePackReplies(raw);
    expect(Object.keys(replies).sort()).toEqual(["demo-a1", "demo-a2", "demo-a3", "demo-a4"]);
    const pack = JSON.parse(readFileSync(join(DEMO, "jury.pack.json"), "utf8")) as DemoPack;
    expect(pack.replies).toEqual(replies);
  });

  it("demo/jury.overrides.json has hand-written details for demo-a1..a4, and the pack carries them", () => {
    const raw = JSON.parse(readFileSync(join(DEMO, "jury.overrides.json"), "utf8"));
    const agents = parsePackAgents(raw);
    expect(Object.keys(agents).sort()).toEqual(["demo-a1", "demo-a2", "demo-a3", "demo-a4"]);
    const pack = JSON.parse(readFileSync(join(DEMO, "jury.pack.json"), "utf8")) as DemoPack;
    expect(pack.agents).toEqual(agents);
    for (const m of ofType(pack, "agent")) expect(m.msg.agent.title).toBe(agents[m.msg.agent.sid].title);
  });

  it("demo/jury.overrides.json is valid and covers the two destructive requests", async () => {
    const overrides = parseOverrides(JSON.parse(readFileSync(join(DEMO, "jury.overrides.json"), "utf8")));
    const pack = await buildPack(events, { budgetUsd: 15, demoRoot: root });
    const ids = ofType(pack, "perm_open").filter((o) => DESTRUCTIVE.includes(o.msg.request.command ?? "")).map((o) => o.msg.request.id);
    expect(Object.keys(overrides).sort()).toEqual([...ids].sort());
    for (const id of ids) expect(overrides[id]).toMatchObject({ onAllow: expect.any(String), onDeny: expect.any(String), explain: expect.anything() });
  });

  it("the two dangerous demo requests carry impacts as perm_impact right after their perm_open", () => {
    const pack = JSON.parse(readFileSync(join(DEMO, "jury.pack.json"), "utf8")) as DemoPack;
    const opens = ofType(pack, "perm_open");
    const rm = opens.find((o) => o.msg.request.command === "rm -rf ../legacy-site")!.msg.request.id;
    const push = opens.find((o) => (o.msg.request.command ?? "").startsWith("git push --force"))!.msg.request.id;
    const impacts = pack.messages.filter((m) => m.msg.type === "perm_impact");
    expect(impacts.map((m) => (m.msg as { id: string }).id).sort()).toEqual([rm, push].sort());
    for (const m of impacts) {
      const id = (m.msg as { id: string }).id;
      const i = pack.messages.indexOf(m);
      const prev = pack.messages[i - 1].msg;
      expect(prev.type === "perm_open" && prev.request.id === id).toBe(true);
    }
    const byId = Object.fromEntries(impacts.map((m) => [(m.msg as { id: string }).id, (m.msg as { impact: unknown }).impact]));
    expect(byId[rm]).toEqual({ kind: "delete", target: "../legacy-site", files: 214, outsideProject: true, recoverable: "no", reason: "Not in a Git project: gone for good." });
    expect(byId[push]).toEqual({ kind: "force_push", target: "origin main", recoverable: "no", reason: "Overwrites what is on the server. Anything new there is gone afterwards." });
  });

  it("demo/jury.pack.json equals a fresh build (explain off, committed overrides) and passes the forbidden-strings check", async () => {
    const text = readFileSync(join(DEMO, "jury.pack.json"), "utf8");
    for (const bad of FORBIDDEN) expect(text.includes(bad), bad).toBe(false);
    const raw = JSON.parse(readFileSync(join(DEMO, "jury.overrides.json"), "utf8"));
    const fresh = await buildPack(events, { budgetUsd: 15, demoRoot: root, overrides: parseOverrides(raw), agents: parsePackAgents(raw), replies: parsePackReplies(raw) });
    expect(JSON.parse(text)).toEqual(JSON.parse(JSON.stringify(fresh)));
  });
});

describe("parity with the live server", () => {
  let handle: ServerHandle | undefined;
  let ws: WebSocket | undefined;
  afterAll(async () => {
    ws?.terminate();
    await handle?.close();
  });

  it("injecting demo/jury.ndjson into startServer gives the same agents, incidents and perm_open risks, in order", async () => {
    const pack = await buildPack(events, { budgetUsd: 15, demoRoot: root });
    handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: "123456", holdMs: 90_000, budgetUsd: 15, demoRoot: root });
    const live: ServerMessage[] = [];
    ws = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`);
    ws.on("message", (d) => live.push(JSON.parse(d.toString())));
    await new Promise<void>((res, rej) => {
      ws!.once("open", () => res());
      ws!.once("error", rej);
    });
    ws.send(JSON.stringify({ type: "hello", code: "123456" }));
    const until = async (ok: () => boolean) => {
      for (const end = Date.now() + 5000; !ok(); await new Promise((r) => setTimeout(r, 10))) {
        if (Date.now() > end) throw new Error("timeout");
      }
    };
    await until(() => live.some((m) => m.type === "paired"));
    for (const e of events) handle.inject(e);
    const relevant = (m: ServerMessage) => m.type === "agent" || m.type === "incident" || m.type === "perm_open";
    const expected = pack.messages.map((m) => m.msg).filter(relevant);
    // What the live server sends for the same events: the pipeline's messages plus one perm_open per permission.
    const offline = createPipeline({ budgetUsd: 15, collisionWindowMs: COLLISION_WINDOW_MS });
    const liveCount = events.reduce((n, e) => n + offline.ingest(e).length + (e.kind === "permission" ? 1 : 0), 0);
    await until(() => live.filter(relevant).length >= liveCount);
    const got = live.filter(relevant);
    expect(got).toHaveLength(liveCount);
    // The pack is the live stream minus the recorded "still waiting" messages after a perm_open, with the
    // recorded permission waits left out of waitingMsTotal; everything else is identical and in order.
    const same = (m: ServerMessage, p: ServerMessage) => {
      if (m.type === "perm_open" && p.type === "perm_open") {
        return m.request.tool === p.request.tool && m.request.command === p.request.command && JSON.stringify(m.request.risk) === JSON.stringify(p.request.risk);
      }
      if (m.type === "agent" && p.type === "agent") {
        return p.agent.waitingMsTotal <= m.agent.waitingMsTotal && JSON.stringify({ ...m.agent, waitingMsTotal: 0 }) === JSON.stringify({ ...p.agent, waitingMsTotal: 0 });
      }
      return JSON.stringify(m) === JSON.stringify(p);
    };
    const dropped: ServerMessage[] = [];
    let j = 0;
    for (const m of got) {
      if (j < expected.length && same(m, expected[j])) j++;
      else dropped.push(m);
    }
    expect(j).toBe(expected.length);
    expect(dropped.length).toBeGreaterThan(0);
    for (const m of dropped) expect(m).toMatchObject({ type: "agent", agent: { status: "blocked" } });
    expect(got.filter((m) => m.type === "perm_open")).toHaveLength(19);
  });
});

describe("timeline after the juror decides", () => {
  const ACTIVITY = new Set(["tool_start", "tool_end", "stop", "prompt", "permission"]);
  const agents = (pack: DemoPack, sid: string) =>
    ofType(pack, "agent").filter((a) => a.msg.agent.sid === sid).map((a) => [a.t, a.msg.agent.status, a.msg.agent.waitingMsTotal]);

  it("drops the recorded still-waiting messages and leaves recorded permission waits out of waitingMsTotal", async () => {
    const pack = await buildPack(
      [
        { sid: "d1", t: 0, kind: "session_start", cwd: "<repo>" },
        { sid: "d1", t: 1000, kind: "permission", cwd: "<repo>", tool: "Bash", command: "rm -rf ../x" },
        { sid: "d1", t: 5000, kind: "status", ctxPct: 10 }, // still blocked in the recording: dropped
        { sid: "d1", t: 61000, kind: "tool_start", cwd: "<repo>", tool: "Read", path: "<repo>/a.ts" }, // recorded wait 60 s
        { sid: "d1", t: 62000, kind: "notify", cwd: "<repo>", notifyType: "agent_needs_input" },
        { sid: "d1", t: 72000, kind: "prompt", cwd: "<repo>" }, // a real (non-permission) wait of 10 s stays
        { sid: "d1", t: 80000, kind: "permission", cwd: "<repo>", tool: "Bash", command: "ls" },
        { sid: "d1", t: 90000, kind: "permission", cwd: "<repo>", tool: "Bash", command: "pwd" }, // its own message stays
        { sid: "d1", t: 95000, kind: "tool_end", cwd: "<repo>", tool: "Bash" }, // recorded wait 15 s
      ],
      { budgetUsd: 15, demoRoot: root },
    );
    expect(agents(pack, "d1")).toEqual([
      [0, "working", 0],
      [1000, "blocked", 0],
      [61000, "reading", 0],
      [62000, "waiting", 0],
      [72000, "working", 10000],
      [80000, "blocked", 10000],
      [90000, "blocked", 10000],
      [95000, "working", 10000],
    ]);
  });

  describe("demo/jury.pack.json", () => {
    const committed = (): DemoPack => JSON.parse(readFileSync(join(DEMO, "jury.pack.json"), "utf8"));

    it("has four agents from four vendors, one each, and every card names the agent that asks", () => {
      const pack = committed();
      const sourceOf = new Map<string, string>();
      for (const m of pack.messages) {
        if (m.msg.type !== "agent") continue;
        const { sid, source } = m.msg.agent;
        expect(sourceOf.get(sid) ?? source, sid).toBe(source);
        sourceOf.set(sid, source);
      }
      expect(Object.fromEntries(sourceOf)).toEqual({ "demo-a1": "claude", "demo-a2": "codex", "demo-a3": "muse", "demo-a4": "gemini" });
      const subject: Record<string, string> = { claude: "Claude ", codex: "Codex ", muse: "Muse ", gemini: "Gemini " };
      for (const m of pack.messages) {
        if (m.msg.type !== "perm_open") continue;
        const source = sourceOf.get(m.msg.request.sid)!;
        expect(m.msg.explain.text.startsWith(subject[source]), m.msg.explain.text).toBe(true);
      }
    });

    it("has no German words in the recording, the overrides or the pack", () => {
      const german = /[äöüÄÖÜß„“]|\b(?:Liest|Bearbeitet|Sucht|Testet|Führt|Nutzt|Wartet|Arbeitet|Fertig|Fehlgeschlagen|Sitzung|Freigegeben|Abgelehnt|Befehl|Datei|nicht|und|der|die|das|mit|wird)\b/;
      for (const f of ["jury.ndjson", "jury.overrides.json", "jury.pack.json"]) {
        const text = readFileSync(join(DEMO, f), "utf8");
        expect(german.exec(text)?.[0], f).toBeUndefined();
      }
    });

    it("shows no agent still blocked between its perm_open and its next activity", () => {
      const pack = committed();
      let windows = 0;
      pack.messages.forEach((m, i) => {
        if (m.msg.type !== "perm_open") return;
        windows++;
        const { sid, createdAt } = m.msg.request;
        const end = events.find((e) => e.sid === sid && e.t > createdAt && ACTIVITY.has(e.kind))?.t ?? Infinity;
        for (const x of pack.messages.slice(i + 1)) {
          if (x.t >= end || x.msg.type !== "agent" || x.msg.agent.sid !== sid) continue;
          expect(x.msg.agent.status, `${sid} at ${x.t}`).not.toBe("blocked");
          expect(x.msg.agent.lastAction.startsWith("Waiting for approval"), `${sid} at ${x.t}`).toBe(false);
        }
      });
      expect(windows).toBe(19);
    });

    it("waitingMsTotal never includes the recorded permission waits", () => {
      const pack = committed();
      // live (pipeline) totals per sid, and the recorded wait of each request until its next activity
      const live = createPipeline({ budgetUsd: 15, collisionWindowMs: COLLISION_WINDOW_MS });
      const liveTotal = new Map<string, number>();
      for (const e of events) for (const msg of live.ingest(e)) if (msg.type === "agent") liveTotal.set(msg.agent.sid, msg.agent.waitingMsTotal);
      const recorded = new Map<string, number>();
      for (const o of ofType(pack, "perm_open")) {
        const { sid, createdAt } = o.msg.request;
        const next = events.find((e) => e.sid === sid && e.t > createdAt && ACTIVITY.has(e.kind))!;
        recorded.set(sid, (recorded.get(sid) ?? 0) + (next.t - createdAt));
      }
      const last = new Map<string, { status: string; since: number; waitingMsTotal: number }>();
      for (const a of ofType(pack, "agent")) {
        const prev = last.get(a.msg.agent.sid);
        // it only ever grows when the agent leaves a real (notify) wait, never when it leaves "blocked"
        if (prev && a.msg.agent.waitingMsTotal !== prev.waitingMsTotal) {
          expect(prev.status).toBe("waiting");
          expect(a.msg.agent.waitingMsTotal - prev.waitingMsTotal).toBe(a.t - prev.since);
        }
        last.set(a.msg.agent.sid, a.msg.agent);
      }
      expect(recorded.size).toBeGreaterThan(0);
      for (const [sid, total] of liveTotal) expect(total - last.get(sid)!.waitingMsTotal, sid).toBe(recorded.get(sid) ?? 0);
    });
  });
});

describe("pack leak check", () => {
  const perm = (over: Partial<AgentEvent>): AgentEvent[] => [{ sid: "d1", t: 1, kind: "permission", cwd: "<repo>", tool: "Bash", ...over }];

  it("redacts secrets in request.command for display", async () => {
    const pack = await buildPack(perm({ command: "curl -H 'x-api-key: sk-ant-api03-abcDEF123_456xyz' https://a.example" }), { budgetUsd: 15, demoRoot: root });
    const cmd = ofType(pack, "perm_open")[0].msg.request.command!;
    expect(cmd).toContain("curl");
    expect(cmd).not.toContain("abcDEF123");
    expect(JSON.stringify(pack)).not.toContain("sk-");
  });

  it.each([
    ["C:\\", perm({ tool: "Write", path: "C:\\Users\\bob\\a.txt" })],
    ["C:/", perm({ tool: "Write", path: "C:/work/a.txt" })],
    ["/Users/", perm({ tool: "Write", path: "/Users/bob/a.txt" })],
    ["/home/", perm({ tool: "Write", path: "/home/bob/a.txt" })],
    ["sk-", [{ sid: "d1", t: 1, kind: "stop", summary: "Used key sk-live-abc" }] as AgentEvent[]],
    ["ghp_", [{ sid: "d1", t: 1, kind: "stop", summary: "Pushed with ghp_abc" }] as AgentEvent[]],
  ])("refuses a pack containing %s", async (marker, evs) => {
    await expect(buildPack(evs, { budgetUsd: 15, demoRoot: root })).rejects.toThrow(`pack would contain ${JSON.stringify(marker)}`);
  });
});

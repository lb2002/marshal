import { describe, expect, it } from "vitest";
import { join } from "node:path";
import { WebSocket } from "ws";
import { classifyRisk } from "../src/risk.js";
import { PermissionBroker } from "../src/broker.js";
import { startServer } from "../src/server.js";
import { buildPack } from "../src/pack.js";
import { tempDir } from "./tmp.js";

// A local absolute path on the host that runs the tests (a cwd without a drive is not local on Windows).
const REPO = process.platform === "win32" ? "C:\\repo" : "/repo";
const patch = (command: unknown, cwd = REPO) => classifyRisk("apply_patch", { command }, cwd, "codex");
const lines = (...l: string[]) => l.join("\n") + "\n";

describe("apply_patch classification (fails closed)", () => {
  it("a patch that only updates and adds files inside the project is safe", () => {
    const r = patch(lines("*** Begin Patch", "*** Update File: src/a.ts", "@@", "-old", "+new", "*** Add File: src/b.ts", "+x", "*** End Patch"));
    expect(r.risk).toBe("safe");
  });
  it("accepts the End of File marker", () => {
    expect(patch(lines("*** Begin Patch", "*** Update File: src/a.ts", "@@", "+x", "*** End of File", "*** End Patch")).risk).toBe("safe");
  });
  it("reads CRLF headers: the paths carry no \\r and a delete is still seen", () => {
    // A CR is a control character: since P3 round 2 that alone makes the request destructive (ruling).
    expect(patch("*** Begin Patch\r\n*** Update File: src/a.ts\r\n@@\r\n+x\r\n*** End Patch\r\n")).toEqual({ risk: "destructive", reason: "Command contains invisible or control characters" });
    expect(patch("*** Begin Patch\r\n*** Delete File: src/a.ts\r\n*** End Patch\r\n")).toMatchObject({ risk: "destructive" });
  });
  it("reads headers with leading spaces and extra whitespace", () => {
    expect(patch(lines("*** Begin Patch", "   *** Delete File: src/a.ts", "*** End Patch")).risk).toBe("destructive");
    expect(patch(lines("*** Begin Patch", "\t***   Update   File :   ../outside.ts", "*** End Patch")).risk).toBe("destructive");
    expect(patch(lines("  ***Begin Patch", "  ***  Update File:src/a.ts", "@@", "+x", " *** End Patch")).risk).toBe("safe");
  });
  it("an unknown *** line makes it destructive (Unklarer Patch)", () => {
    expect(patch(lines("*** Begin Patch", "*** Rename File: src/a.ts", "*** End Patch"))).toEqual({ risk: "destructive", reason: "Unclear patch" });
    expect(patch(lines("*** Begin Patch", "*** Update File: src/a.ts", "@@", "-x", "*** update file: /etc/passwd", "*** End Patch"))).toEqual({ risk: "destructive", reason: "Unclear patch" });
  });
  it("more files than path slots (6) is destructive", () => {
    const files = [1, 2, 3, 4, 5, 6].map((i) => `*** Add File: src/f${i}.ts\n+x`);
    expect(patch(lines("*** Begin Patch", ...files, "*** End Patch")).risk).toBe("destructive");
    expect(patch(lines("*** Begin Patch", ...files.slice(0, 5), "*** End Patch")).risk).toBe("safe");
  });
  it("a delete outside the project is destructive", () => {
    expect(patch(lines("*** Begin Patch", "*** Delete File: /home/u/.bashrc", "*** End Patch")).risk).toBe("destructive");
  });
  it("a write or move outside the project is destructive", () => {
    expect(patch(lines("*** Begin Patch", "*** Add File: ~/.bashrc", "+x", "*** End Patch")).risk).toBe("destructive");
    expect(patch(lines("*** Begin Patch", "*** Update File: src/a.ts", "*** Move to: ../../x.ts", "@@", "+x", "*** End Patch")).risk).toBe("destructive");
  });
  it("a patch with no file headers is destructive", () => {
    expect(patch("rm -rf build")).toEqual({ risk: "destructive", reason: "Unclear patch" });
    expect(patch(lines("*** Begin Patch", "*** End Patch"))).toEqual({ risk: "destructive", reason: "Unclear patch" });
    expect(patch("")).toMatchObject({ risk: "destructive" });
  });
  it("a missing or non-string patch is destructive", () => {
    expect(patch(undefined).risk).toBe("destructive");
    expect(patch(["*** Begin Patch"]).risk).toBe("destructive");
    expect(classifyRisk("apply_patch", {}, REPO, "codex").risk).toBe("destructive");
  });
});

describe("apply_patch: only Codex, and never as an early return", () => {
  const SAFE_PATCH = lines("*** Begin Patch", "*** Update File: src/a.ts", "@@", "-old", "+new", "*** End Patch");

  it("a tool called apply_patch from any other source gets the generic checks, not the patch parser", () => {
    // The patch parser would call this safe; the generic shell rules do not.
    for (const source of [undefined, "claude", "muse", "gemini"] as const) {
      expect(classifyRisk("apply_patch", { command: SAFE_PATCH }, REPO, source).risk, String(source)).toBe("destructive");
      expect(classifyRisk("apply_patch", { command: "rm -rf /" }, REPO, source).risk, String(source)).toBe("destructive");
      expect(classifyRisk("apply_patch", { file_path: "/etc/hosts" }, REPO, source), String(source)).toEqual({ risk: "destructive", reason: "Writes outside the project" });
    }
  });

  it("any input key other than command makes a Codex patch unclear (destructive)", () => {
    for (const extra of [{ file_path: "src/a.ts" }, { path: "src/a.ts" }, { workdir: "/repo" }, { input: "x" }, { justification: "x" }]) {
      expect(classifyRisk("apply_patch", { command: SAFE_PATCH, ...extra }, REPO, "codex"), JSON.stringify(extra)).toEqual({ risk: "destructive", reason: "Unclear patch" });
    }
  });

  it("a safe Codex patch stays safe when the generic checks agree (worst of both)", () => {
    expect(classifyRisk("apply_patch", { command: SAFE_PATCH }, REPO, "codex")).toEqual({ risk: "safe", reason: "Safe" });
  });

  it("the generic tool-name check still applies to a Codex tool that is not apply_patch", () => {
    expect(classifyRisk("delete_patch", { command: SAFE_PATCH }, REPO, "codex").risk).toBe("destructive");
  });
});

describe("the source reaches the classifier", () => {
  const SAFE_PATCH = lines("*** Begin Patch", "*** Update File: src/a.ts", "@@", "-old", "+new", "*** End Patch");

  it("the broker classifies with the request's source", () => {
    const b = new PermissionBroker({ holdMs: 1000, hasClient: () => false });
    expect(b.open("s", "apply_patch", { command: SAFE_PATCH }, REPO, "codex").request.risk.risk).toBe("safe");
    expect(b.open("s", "apply_patch", { command: SAFE_PATCH }, REPO).request.risk.risk).toBe("destructive");
  });

  it("a Codex /perm through the server is classified as a Codex patch; a Claude one is not", async () => {
    const handle = await startServer({ httpPort: 0, wsPort: 0, pairingCode: "123456", holdMs: 5000, budgetUsd: 5 });
    const ws = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`);
    const msgs: Record<string, any>[] = [];
    ws.on("message", (d) => msgs.push(JSON.parse(d.toString())));
    try {
      await new Promise<void>((res, rej) => (ws.once("open", () => res()), ws.once("error", rej)));
      ws.send(JSON.stringify({ type: "hello", code: "123456" }));
      const until = async (ok: () => boolean) => {
        for (const end = Date.now() + 3000; !ok(); await new Promise((r) => setTimeout(r, 10))) if (Date.now() > end) throw new Error("timeout");
      };
      await until(() => msgs.some((m) => m.type === "paired"));
      const perm = (extra: object) =>
        fetch(`http://127.0.0.1:${handle.httpPort}/perm`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ hook_event_name: "PermissionRequest", session_id: "s", cwd: REPO, tool_name: "apply_patch", tool_input: { command: SAFE_PATCH }, ...extra }),
        });
      void perm({ lotse_source: "codex" });
      void perm({});
      await until(() => msgs.filter((m) => m.type === "perm_open").length === 2);
      const risks = msgs.filter((m) => m.type === "perm_open").map((m) => m.request.risk.risk).sort();
      expect(risks).toEqual(["destructive", "safe"]);
    } finally {
      ws.terminate();
      await handle.close();
    }
  });

  it("the pack classifies a recorded Codex patch as Codex", async () => {
    const root = tempDir("lotse-patch-");
    const pack = await buildPack(
      [
        { sid: "x", t: 0, kind: "permission", tool: "apply_patch", command: SAFE_PATCH, cwd: "<repo>", source: "codex" },
        { sid: "y", t: 1, kind: "permission", tool: "apply_patch", command: SAFE_PATCH, cwd: "<repo>" },
      ],
      { budgetUsd: 5, demoRoot: root },
    );
    const risks = pack.messages.flatMap((m) => (m.msg.type === "perm_open" ? [m.msg.request.risk.risk] : []));
    expect(risks).toEqual(["safe", "destructive"]);
  });
});

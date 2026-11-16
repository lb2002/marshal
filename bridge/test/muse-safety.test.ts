// Round 3 of the B4 review: bounded Muse waits, absolute node in hook commands (old `node` entries still ours),
// best-effort cleanup of a half-installed Muse plugin, and non-zero exits of the Muse binary.
import { afterEach, describe, expect, it, vi } from "vitest";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { MUSE_HOLD_MS, PermissionBroker } from "../src/broker.js";
import { MUSE_PERM_WAIT_MS, musePermWaitMs } from "../src/hook-cli.js";
import { bridgeCommand, buildHookSettings, legacyBridgeCmds, removeLotseHooks } from "../src/install.js";
import { buildToolHooks, removeToolHooks } from "../src/install-tools.js";
import { planHooks } from "../src/install-run.js";
import { connectAll, mergeGlobal, removeGlobal, type GlobalOpts } from "../src/install-global.js";
import { manifestText, musePluginDir, realMuseRunner, type MusePluginRecord, type MuseRunner } from "../src/muse-plugin.js";
import { tempDir } from "./tmp.js";

const NODE = "C:\\Program Files\\nodejs\\node.exe";
const CLI = "C:\\work\\lotse\\bridge\\dist\\cli.js";
const NEW = bridgeCommand(CLI, NODE);
const OLD = 'node "C:/work/lotse/bridge/dist/cli.js"';

describe("hook commands use an absolute node", () => {
  it("quotes the absolute node and the CLI path, with forward slashes", () => {
    expect(NEW).toBe('"C:/Program Files/nodejs/node.exe" "C:/work/lotse/bridge/dist/cli.js"');
    expect(legacyBridgeCmds(NEW)).toEqual([OLD]);
    expect(legacyBridgeCmds(OLD)).toEqual([]);
  });
  it("old `node` entries still count as Lotse's: project Claude, the other tools, and the global files", () => {
    expect(removeLotseHooks(buildHookSettings(OLD, "command"), NEW)).toEqual({});
    expect(removeToolHooks(buildToolHooks("codex", OLD), NEW, "codex")).toEqual({});
    const foreign = { theme: "x", hooks: { Stop: [{ hooks: [{ type: "command", command: "mine.sh" }] }] } };
    for (const tool of ["claude", "codex", "gemini", "muse"] as const) {
      const old = (mergeGlobal(tool === "muse" ? { schema_version: 1, ...foreign } : foreign, tool, OLD) as { settings: object }).settings;
      expect(removeGlobal(old, tool, NEW), tool).toEqual(tool === "muse" ? { schema_version: 1, ...foreign } : foreign);
    }
  });
  it("a project install over old `node` hooks upgrades them instead of refusing them as foreign", () => {
    const plan = planHooks(buildToolHooks("muse", OLD), "muse", NEW, "command", false);
    expect(plan).toEqual({ settings: buildToolHooks("muse", NEW) });
  });
  it("the Muse plugin runs the absolute node", () => {
    const m = JSON.parse(manifestText(NEW)) as { capabilities: { hooks: { command: string[] }[] } };
    for (const h of m.capabilities.hooks) expect(h.command.slice(0, 2)).toEqual(["C:/Program Files/nodejs/node.exe", "C:/work/lotse/bridge/dist/cli.js"]);
  });
});

describe("bounded Muse waits", () => {
  afterEach(() => vi.useRealTimers());
  it("the shim waits 20 s for a Muse approval by default; LOTSE_MUSE_PERM_WAIT_MS overrides it", () => {
    expect(MUSE_PERM_WAIT_MS).toBe(20_000);
    expect(musePermWaitMs({})).toBe(20_000);
    expect(musePermWaitMs({ LOTSE_MUSE_PERM_WAIT_MS: "5000" })).toBe(5000);
    for (const bad of ["", "0", "-3", "abc", "1e12"]) expect(musePermWaitMs({ LOTSE_MUSE_PERM_WAIT_MS: bad }), bad).toBe(20_000);
  });
  it("the bridge holds a Muse request at most 18 s (before the shim gives up), the others the full hold", async () => {
    expect(MUSE_HOLD_MS).toBe(18_000);
    expect(MUSE_HOLD_MS).toBeLessThan(MUSE_PERM_WAIT_MS);
    vi.useFakeTimers();
    const b = new PermissionBroker({ holdMs: 90_000, now: () => 1000, hasClient: () => true });
    const muse = b.open("m", "shell", { command: "ls" }, "/w", "muse");
    const codex = b.open("c", "Bash", { command: "ls" }, "/w", "codex");
    expect(muse.request.expiresAt).toBe(1000 + 18_000);
    expect(codex.request.expiresAt).toBe(1000 + 90_000);
    const answers: string[] = [];
    void muse.answer.then(() => answers.push("muse"));
    void codex.answer.then(() => answers.push("codex"));
    await vi.advanceTimersByTimeAsync(18_000);
    expect(answers).toEqual(["muse"]);
    await vi.advanceTimersByTimeAsync(72_000);
    expect(answers).toEqual(["muse", "codex"]);
    // A shorter general hold stays the limit for Muse too.
    expect(new PermissionBroker({ holdMs: 5000, now: () => 0, hasClient: () => true }).open("m", "shell", {}, "/w", "muse").request.expiresAt).toBe(5000);
  });
});

/** A stubbed Muse whose install leaves the plugin behind and then fails (as a timeout would). */
function halfInstallingMuse(plugins: MusePluginRecord[]): MuseRunner & { calls: string[] } {
  const m = {
    calls: [] as string[],
    validate: () => ({ valid: true as const }),
    install(dir: string) {
      m.calls.push("install");
      plugins.push({ id: "lotse", source: dir });
      throw new Error("timed out");
    },
    update() {
      m.calls.push("update");
      throw new Error("timed out");
    },
    list: () => [...plugins],
    remove(id: string) {
      m.calls.push("remove");
      plugins.splice(0, plugins.length, ...plugins.filter((p) => p.id !== id));
    },
  };
  return m;
}

function opts(home: string, muse: MuseRunner): GlobalOpts {
  return { tools: ["muse"], explicit: true, home, env: {}, bridgeCmd: NEW, yes: true, ask: async () => "j", log: () => {}, err: () => {}, muse };
}

describe("a failed Muse plugin install", () => {
  it("removes our plugin again when it shows up in the list although it was not there before", async () => {
    const home = tempDir("lotse-r3-");
    const plugins: MusePluginRecord[] = [];
    const muse = halfInstallingMuse(plugins);
    expect(await connectAll(opts(home, muse))).toMatchObject({ ok: false });
    expect(muse.calls).toEqual(["install", "remove"]);
    expect(plugins).toEqual([]);
    expect(existsSync(musePluginDir(home))).toBe(false);
  });
  it("leaves an earlier plugin of ours in place when only the update failed", async () => {
    const home = tempDir("lotse-r3-");
    const plugins: MusePluginRecord[] = [{ id: "lotse", source: musePluginDir(home) }];
    const muse = halfInstallingMuse(plugins);
    expect(await connectAll(opts(home, muse))).toMatchObject({ ok: false });
    expect(muse.calls).toEqual(["update"]);
    expect(plugins).toHaveLength(1);
  });
});

describe("the real Muse runner (against a fake binary)", () => {
  const fake = join(import.meta.dirname, "fixtures", "fake-muse.mjs");
  const env = (extra: Record<string, string> = {}) => {
    const dir = tempDir("lotse-fakemuse-");
    return { env: { ...process.env, LOTSE_MUSE_BIN: fake, FAKE_MUSE_STATE: join(dir, "state.json"), ...extra }, state: join(dir, "state.json") };
  };
  it("drives plugins validate, install, list and remove", () => {
    const { env: e } = env();
    const r = realMuseRunner(e) as MuseRunner;
    expect(r.validate("C:/b")).toEqual({ valid: true });
    r.install("C:/b");
    expect(r.list()).toEqual([{ id: "lotse", source: "C:/b" }]);
    r.remove("lotse");
    expect(r.list()).toEqual([]);
  });
  it("treats a non-zero exit as failure even when the JSON has no error field", () => {
    const { env: e, state } = env({ FAKE_MUSE_EXIT1: "install" });
    const r = realMuseRunner(e) as MuseRunner;
    expect(() => r.install("C:/b")).toThrow(/exit|code 1/i);
    expect(JSON.parse(readFileSync(state, "utf8")).log).toEqual(["install"]);
    const v = env({ FAKE_MUSE_EXIT1: "validate" });
    expect((realMuseRunner(v.env) as MuseRunner).validate("C:/b")).toMatchObject({ valid: false });
  });
});

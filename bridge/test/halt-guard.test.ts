// Fix round 1 of task P4, finding 1c: while halted, Marshal's hooks are not removed (disconnect, uninstall-hooks).
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { HALT_REFUSAL, bridgeSaysHalted, haltBlocksRemoval, haltMarkerPresent } from "../src/halt-guard.js";
import type { ServerHandle } from "../src/server.js";
import { bootLive, pairHeadset, type Headset } from "./live-helpers.js";
import { tempDir } from "./tmp.js";

const TOKEN = "g".repeat(64);
let handle: ServerHandle | undefined;
let headset: Headset | undefined;
afterEach(async () => {
  headset?.close();
  headset = undefined;
  await handle?.close();
  handle = undefined;
});

describe("halt guard", () => {
  it("the refusal tells the user to release the halt and names no command", () => {
    expect(HALT_REFUSAL).toBe("Marshal is halted, so its hooks stay. Release the halt first, from the headset or the bridge console. Nothing was changed.");
  });
  it("the marker counts whatever it holds (a file, a folder, a stale one)", async () => {
    const home = tempDir("lotse-guard-");
    expect(haltMarkerPresent(home)).toBe(false);
    expect(await haltBlocksRemoval(home, 1, null)).toBeNull();
    mkdirSync(join(home, ".lotse"));
    writeFileSync(join(home, ".lotse", "halt"), JSON.stringify({ pid: 999999999, start: 0 })); // a process that is not running
    expect(haltMarkerPresent(home)).toBe(true);
    expect(await haltBlocksRemoval(home, 1, null)).toBe(HALT_REFUSAL);
    const other = tempDir("lotse-guard-");
    mkdirSync(join(other, ".lotse", "halt"), { recursive: true });
    expect(await haltBlocksRemoval(other, 1, null)).toBe(HALT_REFUSAL);
  });
  it("asks a running bridge: halted yes, not halted no, wrong token or no bridge no", async () => {
    handle = await bootLive({ ingestToken: TOKEN, log: () => {} });
    const port = handle.httpPort;
    const home = tempDir("lotse-guard-"); // no marker: only the bridge's answer counts
    expect(await bridgeSaysHalted(port, TOKEN)).toBe(false);
    expect(await haltBlocksRemoval(home, port, TOKEN)).toBeNull();
    headset = await pairHeadset(handle);
    headset.send({ type: "halt", on: true });
    await headset.next((m) => m.type === "halt_state");
    expect(await bridgeSaysHalted(port, TOKEN)).toBe(true);
    expect(await haltBlocksRemoval(home, port, TOKEN)).toBe(HALT_REFUSAL);
    expect(await bridgeSaysHalted(port, "x".repeat(64))).toBe(false);
    expect(await bridgeSaysHalted(port, null)).toBe(false);
    // the probe changed nothing: still halted, a release works as before
    headset.send({ type: "halt", on: false });
    await headset.next((m) => m.type === "halt_state" && m.on === false);
    expect(await bridgeSaysHalted(port, TOKEN)).toBe(false);
    await handle.close();
    handle = undefined;
    expect(await bridgeSaysHalted(port, TOKEN)).toBe(false);
  });
});

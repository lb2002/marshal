import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { afterAll, describe, expect, it } from "vitest";
import { UsageError, parseArgs, parsePackArgs } from "../src/cli-args.js";
import { readOverridesFile } from "../src/pack.js";

const dir = mkdtempSync(join(tmpdir(), "lotse-cli-args-"));
afterAll(() => rmSync(dir, { recursive: true, force: true }));

describe("parseArgs short aliases", () => {
  it("maps -o only in flag position", () => {
    expect(parseArgs(["a", "-o", "x"], ["out", "overrides"], [], { o: "out" })).toEqual({ pos: ["a"], flags: { out: "x" } });
    // as the value of another flag, -o is just a value
    expect(parseArgs(["a", "--overrides", "-o", "-o", "y"], ["out", "overrides"], [], { o: "out" })).toEqual({
      pos: ["a"],
      flags: { overrides: "-o", out: "y" },
    });
  });

  it("keeps other single-dash arguments positional, as before", () => {
    expect(parseArgs(["-x"], [], [], { o: "out" })).toEqual({ pos: ["-x"], flags: {} });
    expect(() => parseArgs(["--nope"], [], [])).toThrow(UsageError);
  });
});

describe("parsePackArgs", () => {
  it("accepts -o and --out, defaults to explain off and budget 15", () => {
    expect(parsePackArgs(["rec.ndjson", "-o", "p.json"])).toEqual({ recording: "rec.ndjson", out: "p.json", budget: 15, explain: "off" });
    expect(parsePackArgs(["rec.ndjson", "--out", "p.json", "--overrides", "o.json", "--budget", "20", "--explain", "cli"])).toEqual({
      recording: "rec.ndjson",
      out: "p.json",
      overrides: "o.json",
      budget: 20,
      explain: "cli",
    });
  });

  it.each([
    [["rec.ndjson"], /-o/],
    [["-o", "p.json"], /recording/],
    [["rec.ndjson", "-o", "p.json", "--explain", "maybe"], /--explain/],
    [["rec.ndjson", "-o", "p.json", "--budget", "-1"], /--budget/],
  ])("rejects %j", (args, re) => {
    expect(() => parsePackArgs(args)).toThrow(re);
  });
});

describe("readOverridesFile", () => {
  it("names the file and the field in a validation error", () => {
    const f = join(dir, "bad.json");
    writeFileSync(f, JSON.stringify({ p1: { onAlow: "x" } }));
    expect(() => readOverridesFile(f)).toThrow(`${resolve(f)}: overrides: p1: unknown field "onAlow"`);
  });

  it("names the file in a JSON syntax error", () => {
    const f = join(dir, "broken.json");
    writeFileSync(f, "{ nope");
    expect(() => readOverridesFile(f)).toThrow(new RegExp(`^${resolve(f).replace(/[\\^$.*+?()[\]{}|]/g, "\\$&")}: `));
  });

  it("reads a valid file: the request overrides and the agent details", () => {
    const f = join(dir, "ok.json");
    writeFileSync(f, JSON.stringify({ p2: { onDeny: "Nein." } }));
    expect(readOverridesFile(f)).toEqual({ overrides: { p2: { onDeny: "Nein." } }, agents: {}, replies: {} });
    const agents = { a1: { title: "Warenkorb", summary: "Baut den Warenkorb um.", recent: ["Runs tests"], waitingFor: null } };
    writeFileSync(f, JSON.stringify({ p2: { onDeny: "Nein." }, agents }));
    expect(readOverridesFile(f)).toEqual({ overrides: { p2: { onDeny: "Nein." } }, agents, replies: {} });
    writeFileSync(f, JSON.stringify({ replies: { a1: "Verstanden." } }));
    expect(readOverridesFile(f)).toEqual({ overrides: {}, agents: {}, replies: { a1: "Verstanden." } });
  });
  it("names the file and the field for a bad agent detail", () => {
    const f = join(dir, "bad-agent.json");
    writeFileSync(f, JSON.stringify({ agents: { a1: { title: "x".repeat(41), summary: "s", recent: [], waitingFor: null } } }));
    expect(() => readOverridesFile(f)).toThrow(`${resolve(f)}: overrides: agents.a1.title: must be one non-empty line of at most 40 characters`);
  });
});

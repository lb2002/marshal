// Differential test (task p3, item 1): an argument array is classified from its elements, and the result is never
// less severe than today's reading of the same array joined by spaces. The corpus is every string literal of the
// classifier's own tests, each expressed as arrays: its words (when it is one simple literal command), and as the
// command string of `bash -c`, `pwsh -Command` and `cmd /c`.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { classifyRisk } from "../src/risk.js";
import type { RiskResult } from "../src/risk.js";
import { lex } from "../src/risk/lexer.js";
import { classifyShellArgv } from "../src/risk/shell.js";
import { pathEnv } from "../src/risk/paths.js";

const RANK = { safe: 0, network: 1, destructive: 2 } as const;

/** Every double- or single-quoted string literal in a test file (escapes decoded; ones JSON cannot read skipped). */
function literals(file: string): string[] {
  const src = readFileSync(join(import.meta.dirname, file), "utf8");
  const out: string[] = [];
  for (const m of src.matchAll(/"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'/g)) {
    const raw = m[0];
    const body = raw.slice(1, -1);
    const json = raw[0] === '"' ? raw : '"' + body.replace(/\\'/g, "'").replace(/"/g, '\\"') + '"';
    try {
      const s = JSON.parse(json) as string;
      if (s.length > 0 && s.length <= 4000) out.push(s);
    } catch {
      /* JS-only escape (\x, \u{...}): skipped */
    }
  }
  return out;
}

/** The words of `s` when it is one simple command of literal words, else null. */
function words(s: string): string[] | null {
  try {
    const segs = lex(s, "bash").filter((x) => x.words.length > 0 || x.redirects.length > 0 || x.heredocs.length > 0);
    if (segs.length !== 1) return null;
    const g = segs[0];
    if (g.redirects.length || g.heredocs.length || g.substitutions.length || g.funcDef || g.compound || g.pipedIn) return null;
    if (g.words.some((w) => w.dyn || w.glob)) return null;
    return g.words.map((w) => w.text);
  } catch {
    return null;
  }
}

const corpus = [...new Set([...literals("risk.test.ts"), ...literals("risk-parity.test.ts")])];
const arrays: string[][] = [];
for (const s of corpus) {
  const w = words(s);
  if (w !== null && w.length > 0) arrays.push(w);
  // `trap s` reads s in the dialect of the shell (review: the second-dialect element pass).
  arrays.push(["bash", "-c", s], ["pwsh", "-Command", s], ["cmd", "/c", s], ["trap", s]);
}

type Source = "claude" | "codex" | "gemini" | "muse" | undefined;
interface Sender {
  name: string;
  array: (a: string[]) => RiskResult;
  joined: (a: string[]) => RiskResult;
  /** The documented floor besides the joined line: the element reading in each dialect the agent may run. */
  elements: (a: string[]) => RiskResult[];
}

/** Every source on win32 and on Linux, through the Bash tool; Codex also through exec_command `cmd`. */
const senders: Sender[] = [];
for (const platform of ["win32", "linux"] as const) {
  const cwd = platform === "win32" ? "C:\\repo" : "/repo";
  for (const source of [undefined, "claude", "codex", "gemini", "muse"] as Source[]) {
    const both = platform === "win32" && source !== undefined && source !== "claude";
    const elements = (a: string[]) => (both ? ["bash", "pwsh"] : ["bash"]).map((m) => classifyShellArgv(a, cwd, m as "bash" | "pwsh", process.env, pathEnv(platform)));
    senders.push({
      name: `${source ?? "unknown"} ${platform} Bash`,
      array: (a) => classifyRisk("Bash", { command: a }, cwd, source, platform),
      joined: (a) => classifyRisk("Bash", { command: a.join(" ") }, cwd, source, platform),
      elements,
    });
    if (source === "codex") {
      senders.push({
        name: `codex ${platform} exec_command`,
        array: (a) => classifyRisk("exec_command", { cmd: a }, cwd, source, platform),
        joined: (a) => classifyRisk("exec_command", { cmd: a.join(" ") }, cwd, source, platform),
        elements,
      });
    }
  }
}

describe("argument arrays: differential against the joined reading", () => {
  it(`the corpus is the classifier's own test strings (${corpus.length} strings, ${arrays.length} arrays)`, () => {
    expect(corpus.length).toBeGreaterThan(1000);
    expect(arrays.length).toBeGreaterThan(4500);
  });

  for (const s of senders) {
    it(`${s.name}: never less severe than the joined line or the element reading in any dialect the agent may run`, () => {
      const lower: string[] = [];
      for (const a of arrays) {
        const now = s.array(a);
        for (const old of [s.joined(a), ...s.elements(a)]) {
          if (RANK[now.risk] < RANK[old.risk]) lower.push(`${JSON.stringify(a)}: ${now.risk} < ${old.risk} (${old.reason})`);
        }
      }
      expect(lower).toEqual([]);
    }, 120_000);
  }
});

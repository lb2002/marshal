// Task p3, item 3: the classifier uses nothing of the file system but lstat (no realpath, stat, exists, readdir).
// node:fs is wrapped so that every other function records its name while requests are classified on a real tree.
import { describe, expect, it, vi } from "vitest";

const used = vi.hoisted(() => ({ on: false, calls: [] as string[] }));
vi.mock("node:fs", async (orig) => {
  const mod = await orig<Record<string, unknown>>();
  const out: Record<string, unknown> = { ...mod };
  for (const [k, v] of Object.entries(mod)) {
    if (typeof v !== "function" || k === "lstatSync") continue;
    const wrapped = (...args: unknown[]) => {
      if (used.on) used.calls.push(k);
      return (v as (...a: unknown[]) => unknown)(...args);
    };
    // realpathSync.native is a property of realpathSync
    for (const [p, pv] of Object.entries(v as object)) {
      (wrapped as unknown as Record<string, unknown>)[p] =
        typeof pv === "function" ? (...a: unknown[]) => (used.on && used.calls.push(`${k}.${p}`), (pv as (...x: unknown[]) => unknown)(...a)) : pv;
    }
    out[k] = wrapped;
  }
  return { ...out, default: out };
});

import { mkdirSync, symlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { classifyRisk } from "../src/risk.js";
import { tempDir } from "./tmp.js";

describe("the classifier's file-system use", () => {
  it("is lstat only, also through a link", () => {
    const base = tempDir("lotse-risk-fsonly-");
    const root = join(base, "proj");
    mkdirSync(join(root, "src"), { recursive: true });
    mkdirSync(join(base, "secret"));
    writeFileSync(join(root, "src", "a.ts"), "x");
    let link = true;
    try {
      symlinkSync(join(base, "secret"), join(root, "out"), "junction");
    } catch {
      link = false;
    }
    used.calls.length = 0;
    used.on = true;
    const results: string[] = [];
    try {
      for (const command of ["echo x > src/a.ts", "cp a src/new/b", "echo x > out/evil.txt", "cd src && tee ../out/x", "sed -i s/a/b/ src/a.ts"]) {
        results.push(classifyRisk("Bash", { command }, root).risk);
        results.push(classifyRisk("exec_command", { cmd: ["bash", "-lc", command] }, root, "codex").risk);
      }
      results.push(classifyRisk("Write", { file_path: join(root, "out", "x") }, root).risk);
      results.push(classifyRisk("Write", { file_path: join(root, "src", "a.ts") }, root).risk);
    } finally {
      used.on = false;
    }
    expect(used.calls).toEqual([]);
    expect(results.slice(0, 4)).toEqual(["safe", "safe", "safe", "safe"]);
    if (link) expect(results.slice(4, 6)).toEqual(["destructive", "destructive"]);
    expect(results.at(-1)).toBe("safe");
  });
});

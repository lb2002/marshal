// Review 2 M-6: the impact check uses nothing of the file system but `lstat` and `opendir`. Both fs modules are
// wrapped so that every other function records its name while an impact is computed on a real temporary tree.
import { describe, expect, it, vi } from "vitest";

const used = vi.hoisted(() => ({ on: false, calls: [] as string[] }));
function wrap<T extends Record<string, unknown>>(mod: T, name: string, allowed: Set<string>): T {
  const out: Record<string, unknown> = { ...mod };
  for (const [k, v] of Object.entries(mod)) {
    if (typeof v !== "function" || allowed.has(k)) continue;
    out[k] = (...args: unknown[]) => {
      if (used.on) used.calls.push(`${name}.${k}`);
      return (v as (...a: unknown[]) => unknown)(...args);
    };
  }
  return out as T;
}
vi.mock("node:fs/promises", async (orig) => wrap(await orig<Record<string, unknown>>(), "fs/promises", new Set(["lstat", "opendir"])));
vi.mock("node:fs", async (orig) => {
  const m = wrap(await orig<Record<string, unknown>>(), "fs", new Set());
  return { ...m, default: m };
});

import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { computeImpact, realFs } from "../src/impact.js";
import { tempDir } from "./tmp.js";

describe("the impact check's file-system use", () => {
  it("is lstat and opendir only (counting, the .git search, the path walk)", async () => {
    const root = tempDir("lotse-fsonly-");
    mkdirSync(join(root, "old", "sub"), { recursive: true });
    writeFileSync(join(root, "old", "a"), "12345");
    writeFileSync(join(root, "old", "sub", "b"), "1");
    mkdirSync(join(root, "repo", ".git"), { recursive: true });
    used.calls.length = 0;
    used.on = true;
    try {
      for (const command of ["rm -rf old", "rm -rf repo", "rm -rf missing", "git reset --hard"]) {
        await computeImpact({ tool: "Bash", input: { command }, cwd: root, risk: { risk: "destructive", reason: "t" } }, { fs: realFs, now: Date.now, platform: process.platform, homeDir: "/nohome" });
      }
      await computeImpact({ tool: "Write", input: { file_path: join(root, "old", "a") }, cwd: root, risk: { risk: "destructive", reason: "t" } }, { fs: realFs, now: Date.now, platform: process.platform, homeDir: "/nohome" });
    } finally {
      used.on = false;
    }
    expect(used.calls).toEqual([]);
  });
});

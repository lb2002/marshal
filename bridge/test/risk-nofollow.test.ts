// Task p3, item 3: the classifier follows no links and touches no network path. Whether a path leaves the project
// is decided lexically from the validated cwd and the raw path; the only file-system call is lstat on the
// components below the cwd, each validated first (local-path.ts), and a link on the way, a network location or
// anything unvalidated counts as outside the project.
import { describe, expect, it } from "vitest";
import { classifyRisk } from "../src/risk.js";
import type { PathFs, PathKind } from "../src/risk/paths.js";

const OUTSIDE = { risk: "destructive", reason: "Writes outside the project" };
const SAFE = { risk: "safe", reason: "Safe" };
const BS = "\\";

/** A fake file system: every lstat is recorded; unknown paths do not exist. */
function fakeFs(tree: Record<string, PathKind>): PathFs & { calls: string[] } {
  const calls: string[] = [];
  return {
    calls,
    lstat(p: string) {
      calls.push(p);
      if (p === "/proj/denied" || p === "C:" + BS + "proj" + BS + "denied") throw Object.assign(new Error("EACCES"), { code: "EACCES" });
      return tree[p] ?? null;
    },
  };
}

const nix = (): PathFs & { calls: string[] } =>
  fakeFs({ "/proj/src": "dir", "/proj/src/a.ts": "file", "/proj/link": "link", "/proj/src/out": "link", "/proj/f": "link", "/net": "dir", "/net/host": "dir" });
const win = (): PathFs & { calls: string[] } =>
  fakeFs({ ["C:" + BS + "proj" + BS + "src"]: "dir", ["C:" + BS + "proj" + BS + "link"]: "link" });

describe("no file-system call for a path that is not local", () => {
  it("UNC and device paths (win32): outside, without any call", () => {
    const unc = BS + BS + "srv" + BS + "share";
    for (const [file, cwd] of [
      ["a.txt", unc + BS + "proj"],
      [unc + BS + "proj" + BS + "a.txt", unc + BS + "proj"],
      [unc + BS + "x.txt", "C:" + BS + "proj"],
      ["//srv/share/x.txt", "C:/proj"],
      ["a.txt", BS + BS + "?" + BS + "C:" + BS + "proj"],
      ["a.txt", BS + BS + "." + BS + "C:" + BS + "proj"],
      ["a.txt", "C:proj"],
      ["a.txt", "/proj"], // a rooted path without a drive is not a local path on Windows
    ]) {
      const fs = win();
      expect(classifyRisk("Write", { file_path: file }, cwd, undefined, "win32", fs), `${file} in ${cwd}`).toEqual(OUTSIDE);
      expect(classifyRisk("Bash", { command: `echo x > ${file.replaceAll(BS, "/")}` }, cwd, undefined, "win32", fs).risk).toBe("destructive");
      expect(fs.calls, `${file} in ${cwd}`).toEqual([]);
    }
  });

  it("network locations (darwin /net, /Volumes, /home; linux /mnt, /media, gvfs): outside, without any call", () => {
    const cases: [string, string, NodeJS.Platform][] = [
      ["a.txt", "/net/host/proj", "darwin"],
      ["/net/host/proj/a.txt", "/", "darwin"],
      ["/NET/host/x", "/", "darwin"],
      ["/Volumes/share/x", "/", "darwin"],
      ["/System/Volumes/Data/net/h/x", "/", "darwin"],
      ["home/u/x", "/", "darwin"],
      ["/mnt/share/x", "/", "linux"],
      ["a.txt", "/media/usb", "linux"],
      ["/run/user/1000/gvfs/smb-share/x", "/", "linux"],
      // Only a component on the way is a network location (POSIX takes .. after it): checked before each lstat.
      ["/net/../tmp/x", "/", "darwin"],
      ["/mnt/../tmp/x", "/", "linux"],
    ];
    for (const [file, cwd, platform] of cases) {
      const fs = nix();
      expect(classifyRisk("Write", { file_path: file }, cwd, undefined, platform, fs), `${platform} ${file} in ${cwd}`).toEqual(OUTSIDE);
      expect(fs.calls, `${platform} ${file} in ${cwd}`).toEqual([]);
    }
  });

  it("a cwd with . or .. components, control characters or no root is not validated: outside, no call", () => {
    for (const cwd of ["/proj/../proj", "/proj/./x/..", "/pr\noj", "", "proj"]) {
      const fs = nix();
      expect(classifyRisk("Write", { file_path: "a.txt" }, cwd, undefined, "linux", fs), JSON.stringify(cwd)).toEqual(OUTSIDE);
      expect(fs.calls).toEqual([]);
    }
  });
});

describe("links are never followed", () => {
  it("a link on the way: outside, and nothing below it is touched", () => {
    for (const input of [
      { tool: "Write", input: { file_path: "/proj/link/evil.txt" } },
      { tool: "Write", input: { file_path: "link/deep/er/evil.txt" } },
      { tool: "Bash", input: { command: "echo x > link/evil.txt" } },
      { tool: "Bash", input: { command: "cp a link/evil.txt" } },
      { tool: "Bash", input: { command: "cd link && echo x > evil.txt" } },
      { tool: "Write", input: { file_path: "/proj/src/out/x" } },
    ]) {
      const fs = nix();
      expect(classifyRisk(input.tool, input.input, "/proj", undefined, "linux", fs), JSON.stringify(input)).toEqual(OUTSIDE);
      const link = fs.calls.at(-1);
      expect(link === "/proj/link" || link === "/proj/src/out", JSON.stringify(fs.calls)).toBe(true);
      expect(fs.calls.every((c) => !c.startsWith(link + "/")), JSON.stringify(fs.calls)).toBe(true);
    }
  });

  it("a link as the last component, and a link followed by ..: outside", () => {
    const fs = nix();
    expect(classifyRisk("Write", { file_path: "/proj/f" }, "/proj", undefined, "linux", fs)).toEqual(OUTSIDE);
    expect(classifyRisk("Write", { file_path: "/proj/link/../src/a.ts" }, "/proj", undefined, "linux", fs)).toEqual(OUTSIDE);
    const w = win();
    expect(classifyRisk("Write", { file_path: "C:" + BS + "proj" + BS + "link" + BS + "x" }, "C:" + BS + "proj", undefined, "win32", w)).toEqual(OUTSIDE);
    expect(w.calls).toEqual(["C:" + BS + "proj" + BS + "link"]);
  });

  it("real directories and missing parts stay inside; only lstat on components below the cwd", () => {
    const fs = nix();
    expect(classifyRisk("Write", { file_path: "/proj/src/new/deep/a.ts" }, "/proj", undefined, "linux", fs)).toEqual(SAFE);
    expect(fs.calls).toEqual(["/proj/src", "/proj/src/new"]);
    const w = win();
    expect(classifyRisk("Write", { file_path: "src" + BS + "a.ts" }, "C:" + BS + "proj", undefined, "win32", w)).toEqual(SAFE);
    expect(w.calls).toEqual(["C:" + BS + "proj" + BS + "src", "C:" + BS + "proj" + BS + "src" + BS + "a.ts"]);
  });

  it("an error other than a missing part cannot be decided: outside", () => {
    expect(classifyRisk("Write", { file_path: "denied/x" }, "/proj", undefined, "linux", nix())).toEqual(OUTSIDE);
  });

  it("at most 128 lookups per request; beyond that the path counts as outside", () => {
    const tree: Record<string, PathKind> = {};
    for (let i = 0; i < 200; i++) tree[`/proj/d${i}`] = "dir";
    const fs = fakeFs(tree);
    const many = "echo x" + Array.from({ length: 200 }, (_, i) => ` > d${i}/f`).join("");
    expect(classifyRisk("Bash", { command: many }, "/proj", undefined, "linux", fs)).toEqual(OUTSIDE);
    expect(fs.calls.length).toBeLessThanOrEqual(128);
    const few = "echo x" + Array.from({ length: 20 }, (_, i) => ` > d${i}/f`).join("");
    expect(classifyRisk("Bash", { command: few }, "/proj", undefined, "linux", fakeFs(tree))).toEqual(SAFE);
  });
});

// Impact of a destructive request (contract 2026-11-12-contract-impact-halt.md part A, rulings task-p2-fix-1.md).
// Principle: only sure losses ("no") or nothing known ("unknown"); never a reassurance. The attacks of the
// security review (task-p2-review-1.md) are the test cases. Invariant for every test here: no process is ever
// started (node:child_process is mocked to record and throw).
import { mkdirSync, readFileSync, readdirSync, symlinkSync, writeFileSync } from "node:fs";
import { join, posix, win32 } from "node:path";
import { afterAll, describe, expect, it, vi } from "vitest";

const spawned = vi.hoisted(() => [] as string[]);
vi.mock("node:child_process", () => {
  const trap = (name: string) => (...args: unknown[]) => {
    spawned.push(`${name} ${JSON.stringify(args).slice(0, 200)}`);
    throw new Error("the impact check must not start a process");
  };
  return { spawn: trap("spawn"), spawnSync: trap("spawnSync"), exec: trap("exec"), execSync: trap("execSync"), execFile: trap("execFile"), execFileSync: trap("execFileSync"), fork: trap("fork"), default: {} };
});

import { ImpactScheduler, MAX_WALK_ENTRIES, computeImpact, realFs } from "../src/impact.js";
import type { EntryKind, Impact, ImpactDeps, ImpactFs, ImpactRequest } from "../src/impact.js";
import { tempDir } from "./tmp.js";

afterAll(() => {
  expect(spawned).toEqual([]);
});

// The fixed sentences (ruling A3, A5, A6), spelled out here so a changed text is a failing test.
const NOT_IN_GIT = "Not in a Git project: gone for good.";
const IN_GIT = "In a Git project. Marshal does not check whether everything is saved.";
const AMBIGUOUS = "Target unclear. Effects unknown.";
// Round 2: a line that is not exactly one simple literal command.
const NOT_ONE = { kind: "other", recoverable: "unknown", reason: "Several steps or unclear. Effects unknown." };
const RESET = "Discards all unsaved changes in the project. They are gone afterwards.";
const CLEAN = "Deletes every file that is not in Git. They are gone afterwards.";
const PUSH = "Overwrites what is on the server. Anything new there is gone afterwards.";

// ---- a fake file system, keyed by exact path strings of the platform under test ----

type Spec = Record<string, number | "dir" | "link" | "other">;
type Platform = "win32" | "linux" | "darwin";

/**
 * Nodes at the given paths (a number is a file of that size); every parent up to the root is a directory. Records
 * every path it is asked about. `tick` runs on every call (a fake clock).
 */
function fakeFs(platform: Platform, spec: Spec, opts: { tick?: () => void } = {}): ImpactFs & { seen: string[]; listed: string[] } {
  const P = platform === "win32" ? win32 : posix;
  const nodes = new Map<string, { kind: EntryKind; size: number }>();
  const children = new Map<string, Set<string>>();
  const addParents = (p: string) => {
    for (let d = p; ; ) {
      const up = P.dirname(d);
      if (up === d) break;
      if (!children.has(up)) children.set(up, new Set());
      (children.get(up) as Set<string>).add(P.basename(d));
      if (!nodes.has(up)) nodes.set(up, { kind: "dir", size: 0 });
      d = up;
    }
  };
  for (const [p, v] of Object.entries(spec)) {
    nodes.set(p, typeof v === "number" ? { kind: "file", size: v } : { kind: v, size: v === "link" ? 7 : 0 });
    addParents(p);
  }
  const seen: string[] = [];
  const listed: string[] = [];
  return {
    seen,
    listed,
    async lstat(p) {
      opts.tick?.();
      seen.push(p);
      const n = nodes.get(p);
      return n ? { kind: n.kind, size: n.size, id: p } : null;
    },
    async readdir(p, max) {
      opts.tick?.();
      listed.push(p);
      const n = nodes.get(p);
      if (!n || n.kind !== "dir") throw Object.assign(new Error("ENOTDIR"), { code: "ENOTDIR" });
      const all = [...(children.get(p) ?? [])];
      const entries = all.slice(0, max).map((name) => ({ name, kind: (nodes.get(P.join(p, name)) as { kind: EntryKind }).kind }));
      return { entries, more: all.length > max };
    },
  };
}

const WIN_CWD = "C:\\work\\shop";
const POSIX_CWD = "/work/shop";
const cwdOf = (platform: Platform) => (platform === "win32" ? WIN_CWD : POSIX_CWD);
const DESTRUCTIVE = { risk: "destructive" as const, reason: "test" };

function deps(fs: ImpactFs, platform: Platform, over: Partial<ImpactDeps> = {}): ImpactDeps {
  return { fs, now: Date.now, platform, homeDir: platform === "win32" ? "C:\\Users\\u" : "/Users/u", ...over };
}

function shell(command: string, cwd: string, tool = "Bash", source?: ImpactRequest["source"]): ImpactRequest {
  return { tool, input: { command }, cwd, source, risk: DESTRUCTIVE };
}

/** True when no path below `p` (beyond it) was asked about. */
const nothingBeyond = (seen: string[], p: string, sep: string) => seen.every((s) => !s.startsWith(p + sep));

// ---- C1: Git Bash drive paths on Windows ----

describe("impact: Windows paths as Git Bash reads them (review C1)", () => {
  const victim: Spec = { [WIN_CWD]: "dir", "C:\\Users\\x\\important\\a.txt": 5, "C:\\Users\\x\\important\\b.txt": 6 };

  it("rm -rf /c/Users/x/important with C:\\c absent: the drive path, counted, not in Git, no", async () => {
    const fs = fakeFs("win32", victim);
    const i = await computeImpact(shell("rm -rf /c/Users/x/important", WIN_CWD), deps(fs, "win32"));
    // The target is the text as named, shortened for display (anonymize.ts reads `/c/Users/x` as another user's home).
    expect(i).toEqual({ kind: "delete", target: "~other/important", files: 2, bytes: 11, outsideProject: true, recoverable: "no", reason: NOT_IN_GIT });
  });

  it("with C:\\c present (a directory, a file or a link) it is ambiguous: unknown, no counts", async () => {
    for (const shadow of ["dir", 3, "link"] as const) {
      const fs = fakeFs("win32", { ...victim, "C:\\c": shadow });
      const i = (await computeImpact(shell("rm -rf /c/Users/x/important", WIN_CWD), deps(fs, "win32"))) as Impact;
      expect(i, String(shadow)).toEqual({ kind: "delete", recoverable: "unknown", reason: AMBIGUOUS });
      expect(fs.listed).toEqual([]);
    }
  });

  it("a Git Bash path that does not exist is never 'does not exist' with a reassuring verdict", async () => {
    const i = await computeImpact(shell("rm -rf /c/Users/x/missing", WIN_CWD), deps(fakeFs("win32", victim), "win32"));
    expect(i).toEqual({ kind: "delete", recoverable: "unknown", reason: AMBIGUOUS });
  });

  it("every other POSIX-style rooted path is unknown and is not touched", async () => {
    for (const t of ["/tmp/x", "/cygdrive/c/Users/x", "/mnt/c/Users/x", "//host/share/x", "\\Users\\x", "/c:/x", "/usr/bin"]) {
      const fs = fakeFs("win32", { ...victim, "C:\\tmp\\x": 1, "C:\\Users\\x\\f": 1 });
      const i = (await computeImpact(shell(`rm -rf '${t}'`, WIN_CWD), deps(fs, "win32"))) as Impact;
      expect(i, t).toMatchObject({ recoverable: "unknown" });
      expect([AMBIGUOUS, NOT_ONE.reason], t).toContain(i.reason); // a backslash makes the line not eligible at all
      expect("files" in i, t).toBe(false);
      expect(fs.seen.filter((p) => !WIN_CWD.startsWith(p)), t).toEqual([]);
    }
  });
});

// ---- C2: links on the path, as the shell reads them ----

describe("impact: links on the target path (review C2, S1, S3)", () => {
  for (const platform of ["linux", "win32"] as const) {
    const P = platform === "win32" ? win32 : posix;
    const cwd = cwdOf(platform);
    const at = (...p: string[]) => P.join(cwd, ...p);

    it(`${platform}: rm -rf link/, link, dir/link/sub, l/../victim: all unknown, nothing beyond the link touched`, async () => {
      const spec: Spec = { [at("link")]: "link", [at("l")]: "link", [at("dir", "link")]: "link", [at("victim", "f")]: 4, [at("dir", "x")]: 1 };
      const cases: [string, string][] = [
        ["link/", at("link")],
        ["link", at("link")],
        ["dir/link/sub", at("dir", "link")],
        ["l/../victim", at("l")],
      ];
      for (const [t, linkPath] of cases) {
        const fs = fakeFs(platform, spec);
        const i = (await computeImpact(shell(`rm -rf ${t}`, cwd), deps(fs, platform))) as Impact;
        expect(i, t).toEqual({ kind: "delete", recoverable: "unknown", reason: AMBIGUOUS });
        expect(nothingBeyond(fs.seen, linkPath, P.sep), t).toBe(true);
        expect(fs.seen.includes(at("victim")), t).toBe(false);
        expect(fs.listed, t).toEqual([]);
      }
    });

    it(`${platform}: PowerShell Remove-Item on a link or junction is unknown`, async () => {
      const fs = fakeFs(platform, { [at("cache")]: "link" });
      expect(await computeImpact(shell("Remove-Item -Recurse -Force cache", cwd, "PowerShell"), deps(fs, platform))).toMatchObject({ recoverable: "unknown", reason: AMBIGUOUS });
    });

    it(`${platform}: a trailing separator on a file is unknown; on a real directory it is counted`, async () => {
      const fs = fakeFs(platform, { [at("notes.txt")]: 3, [at("old", "a")]: 2 });
      for (const t of ["notes.txt/", "notes.txt/../old"]) {
        expect(await computeImpact(shell(`rm -rf ${t}`, cwd), deps(fs, platform)), t).toMatchObject({ recoverable: "unknown", reason: AMBIGUOUS });
      }
      // rm refuses an operand ending in `.` or `..`: not eligible (round 2)
      expect(await computeImpact(shell("rm -rf notes.txt/.", cwd), deps(fs, platform))).toEqual(NOT_ONE);
      expect(await computeImpact(shell("rm -rf old/", cwd), deps(fs, platform))).toMatchObject({ files: 1, bytes: 2, recoverable: "no" });
    });

    it(`${platform}: a..b after a real directory is followed physically`, async () => {
      const fs = fakeFs(platform, { [at("a", "keep")]: 1, [at("b", "f")]: 9 });
      expect(await computeImpact(shell("rm -rf a/../b", cwd), deps(fs, platform))).toMatchObject({ files: 1, bytes: 9, outsideProject: false, recoverable: "no" });
    });
  }

  it("a nonexistent target is unknown, never 'nothing lost'", async () => {
    for (const platform of ["linux", "win32"] as const) {
      const cwd = cwdOf(platform);
      for (const c of ["rm -rf build", "rm -f a/b/c", "rmdir gone"]) {
        const i = (await computeImpact(shell(c, cwd), deps(fakeFs(platform, { [cwd]: "dir" }), platform))) as Impact;
        expect(i, c).toMatchObject({ kind: "delete", recoverable: "unknown", reason: AMBIGUOUS });
        expect("files" in i, c).toBe(false);
      }
      const w = await computeImpact({ tool: "Write", input: { file_path: "new.ts" }, cwd, risk: DESTRUCTIVE }, deps(fakeFs(platform, { [cwd]: "dir" }), platform));
      expect(w).toEqual({ kind: "overwrite", recoverable: "unknown", reason: AMBIGUOUS });
    }
  });

  it("more than one target where one is unknown: the whole impact is unknown, no counts", async () => {
    const fs = fakeFs("linux", { "/work/shop/a/f": 1, "/work/shop/l": "link" });
    expect(await computeImpact(shell("rm -rf a l", POSIX_CWD), deps(fs, "linux"))).toEqual({ kind: "delete", recoverable: "unknown", reason: AMBIGUOUS });
    expect(await computeImpact(shell("rm -rf a gone", POSIX_CWD), deps(fakeFs("linux", { "/work/shop/a/f": 1 }), "linux"))).toMatchObject({ recoverable: "unknown", reason: AMBIGUOUS });
  });

  it("a real junction on this machine stops the walk (real file system)", async () => {
    const root = tempDir("lotse-impact-");
    const outside = tempDir("lotse-impact-out-");
    writeFileSync(join(outside, "precious.txt"), "abc");
    let linked = true;
    try {
      symlinkSync(outside, join(root, "cache"), process.platform === "win32" ? "junction" : "dir");
    } catch {
      linked = false;
    }
    if (!linked) return;
    const seen: string[] = [];
    const spy: ImpactFs = { lstat: (p) => (seen.push(p), realFs.lstat(p)), readdir: (p, m) => realFs.readdir(p, m) };
    for (const t of ["cache/", "cache", "cache/precious.txt"]) {
      const i = await computeImpact(shell(`rm -rf ${t}`, root), { fs: spy, now: Date.now, platform: process.platform, homeDir: "/nohome" });
      expect(i, t).toMatchObject({ recoverable: "unknown", reason: AMBIGUOUS });
    }
    expect(seen.some((p) => p.includes("precious"))).toBe(false);
  });
});

// ---- A7 / I4: the cwd and network locations ----

describe("impact: the cwd and network locations (review I4)", () => {
  it("a link in the cwd itself is unknown, with no lstat beyond it", async () => {
    for (const platform of ["linux", "win32"] as const) {
      const P = platform === "win32" ? win32 : posix;
      const link = platform === "win32" ? "C:\\proj\\l" : "/proj/l";
      const cwd = P.join(link, "sub");
      const fs = fakeFs(platform, { [link]: "link" });
      for (const c of ["rm -rf x", "rm -rf ../../y"]) {
        expect(await computeImpact(shell(c, cwd), deps(fs, platform)), c).toMatchObject({ kind: "delete", recoverable: "unknown", reason: AMBIGUOUS });
      }
      expect(nothingBeyond(fs.seen, link, P.sep)).toBe(true);
      expect(fs.listed).toEqual([]);
    }
  });

  it("darwin: /net/host/x is unknown with no lstat at all; so are /Volumes, /Network, /home and their Data-volume spellings", async () => {
    for (const t of ["/net/attacker.example/x", "/NET/attacker.example/x", "/Volumes/share/x", "/Network/Servers/h/x", "/home/u/x", "/System/Volumes/Data/net/h/x"]) {
      const fs = fakeFs("darwin", { "/Users/u/shop": "dir" });
      const i = (await computeImpact(shell(`rm -rf ${t}`, "/Users/u/shop"), deps(fs, "darwin"))) as Impact;
      expect(i, t).toMatchObject({ recoverable: "unknown", reason: AMBIGUOUS });
      expect(fs.seen, t).toEqual([]);
    }
  });

  it("darwin: a cwd under /net, or a path that reaches /net through .., touches nothing there", async () => {
    const fs = fakeFs("darwin", { "/Users/u/shop": "dir" });
    expect(await computeImpact(shell("rm -rf x", "/net/h/shop"), deps(fs, "darwin"))).toMatchObject({ recoverable: "unknown" });
    expect(fs.seen).toEqual([]);
    const fs2 = fakeFs("darwin", { "/Users/u/shop": "dir" });
    expect(await computeImpact(shell("rm -rf ../../../net/h/x", "/Users/u/shop"), deps(fs2, "darwin"))).toMatchObject({ recoverable: "unknown" });
    expect(fs2.seen.some((p) => p.toLowerCase().startsWith("/net"))).toBe(false);
  });

  it("linux: /net, /mnt, /media and gvfs are refused before any lstat", async () => {
    for (const t of ["/net/h/x", "/mnt/share/x", "/media/u/usb/x", "/run/user/1000/gvfs/smb-share:server=h/x"]) {
      const fs = fakeFs("linux", { [POSIX_CWD]: "dir" });
      expect(await computeImpact(shell(`rm -rf '${t}'`, POSIX_CWD), deps(fs, "linux")), t).toMatchObject({ recoverable: "unknown" });
      expect(fs.seen, t).toEqual([]);
    }
  });

  const hostile = [
    "-rf", "--no-preserve-root", "a\nb", "x".repeat(5000), "../".repeat(40) + "etc", "con", "NUL.txt", "a:stream", "a.", "a ",
    "\\\\server\\share\\x", "//server/share/x", "\\\\?\\C:\\Windows", "\\\\.\\PhysicalDrive0", "C:relative", "a\u0000b", "~/x",
  ];

  it("never throws, never touches UNC or device paths, never reassures", async () => {
    for (const platform of ["win32", "linux"] as const) {
      const cwd = cwdOf(platform);
      for (const t of hostile) {
        const fs = fakeFs(platform, { [cwd]: "dir", [`${cwd}${platform === "win32" ? "\\" : "/"}.git`]: "dir" });
        const d = deps(fs, platform);
        const write = await computeImpact({ tool: "Write", input: { file_path: t }, cwd, risk: DESTRUCTIVE }, d);
        const quoted = await computeImpact(shell(`rm -rf -- '${t.replace(/'/g, "")}'`, cwd), d);
        for (const r of [write, quoted]) if (r !== null) expect(["no", "unknown"]).toContain(r.recoverable);
        expect(fs.seen.filter((p) => /^[\\/]{2}/.test(p) || p.includes("server") || p.includes("PhysicalDrive")), t).toEqual([]);
        for (const r of [write, quoted]) if (r?.target) expect([...r.target].length).toBeLessThanOrEqual(120);
      }
    }
  });

  it("a UNC, device, drive-relative, relative or control-character cwd touches nothing", async () => {
    for (const cwd of ["\\\\evil-host\\share\\proj", "//evil-host/share/proj", "\\\\?\\C:\\proj", "\\\\.\\C:\\proj", "C:proj", "relative/proj", "C:\\a\nb", "C:\\proj\\..\\x"]) {
      const fs = fakeFs("win32", { [WIN_CWD]: "dir" });
      const i = await computeImpact(shell("rm -rf src", cwd), deps(fs, "win32"));
      expect(i, cwd).toMatchObject({ recoverable: "unknown", reason: AMBIGUOUS });
      expect(fs.seen.filter((p) => p.includes("evil") || p.includes("proj")), cwd).toEqual([]);
    }
  });
});

// ---- A3: Git, by lstat only ----

describe("impact: Git project or not (ruling A3)", () => {
  it("not in a repository gives no; inside one gives unknown with the fixed sentence", async () => {
    const tree: Spec = { "/work/shop/dist/x.js": 1000, "/work/shop/dist/deep/y.js": 24 };
    expect(await computeImpact(shell("rm -rf dist", POSIX_CWD), deps(fakeFs("linux", tree), "linux"))).toEqual({
      kind: "delete", target: "…/dist", files: 2, bytes: 1024, outsideProject: false, recoverable: "no", reason: NOT_IN_GIT,
    });
    expect(await computeImpact(shell("rm -rf dist", POSIX_CWD), deps(fakeFs("linux", { ...tree, "/work/shop/.git": "dir" }), "linux"))).toEqual({
      kind: "delete", target: "…/dist", files: 2, bytes: 1024, outsideProject: false, recoverable: "unknown", reason: IN_GIT,
    });
    expect(await computeImpact(shell("rm -rf dist", POSIX_CWD), deps(fakeFs("linux", { ...tree, "/work/.git": "dir" }), "linux"))).toMatchObject({ recoverable: "unknown", reason: IN_GIT });
  });

  it("a .git file (worktree) counts as inside; so does a .git that is a link; nothing in it is read", async () => {
    for (const kind of [12, "link"] as const) {
      const fs = fakeFs("linux", { "/work/shop/src/a.ts": 1, "/work/shop/.git": kind });
      expect(await computeImpact(shell("rm -rf src", POSIX_CWD), deps(fs, "linux")), String(kind)).toMatchObject({ recoverable: "unknown", reason: IN_GIT });
      expect(fs.listed.some((p) => p.includes(".git"))).toBe(false);
    }
  });

  it("deleting a repository from outside it: the whole project with its history (round 2 A7)", async () => {
    const fs = fakeFs("linux", { "/work/old-repo/.git/HEAD": 20, "/work/old-repo/a": 5, [POSIX_CWD]: "dir" });
    expect(await computeImpact(shell("rm -rf ../old-repo", POSIX_CWD), deps(fs, "linux"))).toMatchObject({ files: 2, bytes: 25, outsideProject: true, recoverable: "no", reason: "Deletes a whole Git project with its history: gone for good." });
  });

  it("an overwrite of a file outside / inside a project", async () => {
    const write = (p: string): ImpactRequest => ({ tool: "Write", input: { file_path: p, content: "x" }, cwd: POSIX_CWD, risk: DESTRUCTIVE });
    expect(await computeImpact(write("/work/shop/a.ts"), deps(fakeFs("linux", { "/work/shop/a.ts": 300 }), "linux"))).toEqual({
      kind: "overwrite", target: "…/a.ts", files: 1, bytes: 300, outsideProject: false, recoverable: "no", reason: NOT_IN_GIT,
    });
    expect(await computeImpact(write("a.ts"), deps(fakeFs("linux", { "/work/shop/a.ts": 300, "/work/shop/.git": "dir" }), "linux"))).toMatchObject({ recoverable: "unknown", reason: IN_GIT });
    expect(await computeImpact(write("dir"), deps(fakeFs("linux", { "/work/shop/dir/x": 1 }), "linux"))).toMatchObject({ recoverable: "unknown", reason: AMBIGUOUS });
  });

  it("a real .git directory on this machine (real file system)", async () => {
    const root = tempDir("lotse-impact-");
    mkdirSync(join(root, "old", "sub"), { recursive: true });
    writeFileSync(join(root, "old", "a.txt"), "12345");
    writeFileSync(join(root, "old", "sub", "b.txt"), "1234567890");
    const live: ImpactDeps = { fs: realFs, now: Date.now, platform: process.platform, homeDir: "/nohome" };
    const before = (await computeImpact(shell("rm -rf old", root), live)) as Impact;
    expect(before).toMatchObject({ kind: "delete", files: 2, bytes: 15, outsideProject: false });
    mkdirSync(join(root, ".git"));
    expect(await computeImpact(shell("rm -rf old", root), live)).toMatchObject({ files: 2, recoverable: "unknown", reason: IN_GIT });
  });
});

// ---- A5: commands whose loss follows from what they do ----

describe("impact: commands that destroy by definition (ruling A5)", () => {
  it("git push --force, --force-with-lease, -f, +refspec: no, the fixed sentence, no file-system access, no commitsLost", async () => {
    for (const c of ["git push --force origin main", "git push --force-with-lease origin main", "git push -f origin main", "git push origin +main", "git push --force"]) {
      const fs = fakeFs("linux", { "/work/shop/.git": "dir" });
      const i = (await computeImpact(shell(c, POSIX_CWD), deps(fs, "linux"))) as Impact;
      expect(i, c).toMatchObject({ kind: "force_push", recoverable: "no", reason: PUSH });
      expect("commitsLost" in i).toBe(false);
      expect(fs.seen, c).toEqual([]);
    }
    expect(await computeImpact(shell("git push --force origin main", POSIX_CWD), deps(fakeFs("linux", {}), "linux"))).toEqual({ kind: "force_push", target: "origin main", recoverable: "no", reason: PUSH });
  });

  it("git reset --hard and git clean -f: no, the fixed sentences, no file-system access", async () => {
    const fs = fakeFs("linux", { "/work/shop/.git": "dir" });
    expect(await computeImpact(shell("git reset --hard HEAD~1", POSIX_CWD), deps(fs, "linux"))).toEqual({ kind: "reset", recoverable: "no", reason: RESET });
    expect(await computeImpact(shell("git clean -fd", POSIX_CWD), deps(fs, "linux"))).toEqual({ kind: "clean", recoverable: "no", reason: CLEAN });
    expect(await computeImpact(shell("git clean -fdx -- build", POSIX_CWD), deps(fs, "linux"))).toEqual({ kind: "clean", recoverable: "no", reason: CLEAN });
    expect(await computeImpact(shell("git clean -n", POSIX_CWD), deps(fs, "linux"))).toEqual(NOT_ONE);
    expect(await computeImpact(shell("git reset --soft HEAD~1", POSIX_CWD), deps(fs, "linux"))).toEqual(NOT_ONE);
    expect(fs.seen).toEqual([]);
  });
});

// ---- command forms (unchanged parsing) ----

describe("impact: command forms", () => {
  const tree: Spec = { "/work/shop/src/a.ts": 100, "/work/shop/src/b.ts": 50, "/work/shop/dist/x.js": 1000, "/work/shop/dist/deep/y.js": 24, "/work/shop/notes.txt": 5, "/work/shop/-rf": 9 };
  const run = (c: string, tool = "Bash", source?: ImpactRequest["source"]) => computeImpact(shell(c, POSIX_CWD, tool, source), deps(fakeFs("linux", tree), "linux"));

  it("rm on files and directories, several targets, arguments after --", async () => {
    expect(await run("rm -f notes.txt src/a.ts")).toMatchObject({ kind: "delete", files: 2, bytes: 105, target: "…/notes.txt, …/a.ts", recoverable: "no" });
    expect(await run("rm -- -rf")).toMatchObject({ files: 1, bytes: 9, target: "…/-rf" });
    expect(await run("rm -rf src src/a.ts")).toMatchObject({ files: 2, bytes: 150 });
  });

  it("PowerShell forms: Remove-Item -Recurse, rm -r -fo; cmd forms in bash and filters are not eligible", async () => {
    for (const c of ["Remove-Item -Recurse -Force dist", "Remove-Item -Path dist -Recurse", "rm -r -fo dist", "rd dist -Recurse"]) {
      expect(await run(c, "PowerShell"), c).toMatchObject({ kind: "delete", target: "…/dist", files: 2, bytes: 1024 });
    }
    for (const c of ["del /s /q dist", "rd /s /q dist"]) expect(await run(c), c).toEqual(NOT_ONE);
    expect(await run("Remove-Item dist -Recurse -Include *.js", "PowerShell")).toEqual(NOT_ONE);
  });

  it("a glob, a variable, a substitution, ~ or a brace give unknown with no file-system access", async () => {
    for (const c of ["rm -rf *.log", "rm -rf $BUILD", "rm -rf $(cat list.txt)", "rm -rf ~/old", "rm -rf {a,b}", "rm -rf `pwd`/x"]) {
      const fs = fakeFs("linux", tree);
      const i = (await computeImpact(shell(c, POSIX_CWD), deps(fs, "linux"))) as Impact;
      expect(i, c).toEqual(NOT_ONE);
      expect(fs.seen, c).toEqual([]);
    }
  });

  it("several destructive commands, a cd, or differing dialects are unknown; other commands give nothing", async () => {
    expect(await run("kill -9 1234")).toEqual(NOT_ONE); // destructive and not understood: no facts
    expect(await run("rm -rf a; rm -rf b")).toEqual(NOT_ONE);
    expect(await run("cd .. && rm -rf shop")).toEqual(NOT_ONE);
    expect(await computeImpact({ ...shell("ls -la", POSIX_CWD), risk: { risk: "network", reason: "x" } }, deps(fakeFs("linux", tree), "linux"))).toBeNull();
  });

  it("only the named target is reported, shortened, never names from inside it", async () => {
    const fs = fakeFs("linux", { "/work/shop/old/secret-name.txt": 1, "/work/shop/old/other.key": 2 });
    const i = await computeImpact(shell("rm -rf /work/shop/old", POSIX_CWD), deps(fs, "linux"));
    const wire = JSON.stringify(i);
    expect(wire).not.toContain("secret-name");
    expect(wire).not.toContain("other.key");
    expect(i?.target).toBe("…/old");
  });
});

// ---- A4 / A8: bounds ----

describe("impact: bounds (review I5, M4)", () => {
  it("stops at 20,000 entries: filesCapped, files a lower bound; the Git verdict still holds", async () => {
    const big: Spec = {};
    for (let k = 0; k < 25_000; k++) big[`/work/shop/big/f${k}`] = 1;
    const fs = fakeFs("linux", big);
    const i = (await computeImpact(shell("rm -rf big", POSIX_CWD), deps(fs, "linux"))) as Impact;
    expect(i).toMatchObject({ filesCapped: true, recoverable: "no", reason: NOT_IN_GIT });
    expect(i.files).toBeLessThanOrEqual(MAX_WALK_ENTRIES);
    expect(i.files).toBeGreaterThan(19_000);
  });

  it("one directory listing never asks for more names than the entry budget", async () => {
    const big: Spec = {};
    for (let k = 0; k < 30_000; k++) big[`/work/shop/big/f${k}`] = 1;
    const fs = fakeFs("linux", big);
    const asked: number[] = [];
    const spy: ImpactFs = { lstat: fs.lstat, readdir: (p, max) => (asked.push(max), fs.readdir(p, max)) };
    await computeImpact(shell("rm -rf big", POSIX_CWD), deps(spy, "linux"));
    expect(Math.max(...asked)).toBeLessThanOrEqual(MAX_WALK_ENTRIES);
  });

  it("a directory swapped for a link between its lstat and its listing: unknown, nothing behind the link is touched", async () => {
    const base = fakeFs("linux", { "/work/shop/s/a": 1, "/work/shop/s/d/inner": 2 });
    let swapped = false;
    const seen: string[] = [];
    const fs: ImpactFs = {
      async lstat(p) {
        seen.push(p);
        if (p === "/work/shop/s/d" && swapped) return { kind: "link", size: 7, id: "link" };
        return base.lstat(p);
      },
      async readdir(p, max) {
        if (p === "/work/shop/s/d") {
          swapped = true; // the listing follows the new link to somewhere else
          return { entries: [{ name: "victim", kind: "file" }], more: false };
        }
        return base.readdir(p, max);
      },
    };
    const i = (await computeImpact(shell("rm -rf s", POSIX_CWD), deps(fs, "linux"))) as Impact;
    expect(i).toMatchObject({ kind: "delete", filesCapped: true, recoverable: "unknown", reason: AMBIGUOUS });
    expect(seen.some((p) => p.startsWith("/work/shop/s/d/"))).toBe(false);
  });

  it("a child that is a link the listing did not report as one ends the walk; no sibling is looked at", async () => {
    const base = fakeFs("linux", { "/work/shop/s/a": 1, "/work/shop/s/b": 1, "/work/shop/s/c": 1 });
    const seen: string[] = [];
    const fs: ImpactFs = {
      async lstat(p) {
        seen.push(p);
        if (p === "/work/shop/s/a") return { kind: "link", size: 7, id: "x" };
        return base.lstat(p);
      },
      readdir: (p, max) => base.readdir(p, max),
    };
    const i = (await computeImpact(shell("rm -rf s", POSIX_CWD), deps(fs, "linux"))) as Impact;
    expect(i).toMatchObject({ filesCapped: true, recoverable: "unknown", reason: AMBIGUOUS });
    expect(seen.filter((p) => p === "/work/shop/s/b" || p === "/work/shop/s/c")).toEqual([]);
    // a link the listing does report as one is just counted
    const ok = fakeFs("linux", { "/work/shop/s/a": "link", "/work/shop/s/b": 1 });
    expect(await computeImpact(shell("rm -rf s", POSIX_CWD), deps(ok, "linux"))).toMatchObject({ files: 2, recoverable: "no" });
  });

  it("more than 32 targets is unknown before any lstat (review M4)", async () => {
    const fs = fakeFs("linux", { [POSIX_CWD]: "dir" });
    const many = Array.from({ length: 33 }, (_, k) => `f${k}`).join(" ");
    expect(await computeImpact(shell(`rm -f ${many}`, POSIX_CWD), deps(fs, "linux"))).toMatchObject({ kind: "delete", recoverable: "unknown", reason: AMBIGUOUS });
    expect(fs.seen).toEqual([]);
  });

  it("400 ms for the whole check: a slow walk is capped, a slow path walk is unknown", async () => {
    let clock = 0;
    const tree: Spec = {};
    for (let k = 0; k < 100; k++) tree[`/work/shop/big/d${k}/a`] = 1;
    const fs = fakeFs("linux", tree, { tick: () => (clock += 10) });
    const i = (await computeImpact(shell("rm -rf big", POSIX_CWD), deps(fs, "linux", { now: () => clock }))) as Impact;
    expect(i.filesCapped).toBe(true);
    expect(i.files).toBeLessThan(100);
    let slow = 0;
    const deep = fakeFs("linux", { [`/work/shop/${"d/".repeat(60)}f`]: 1 }, { tick: () => (slow += 10) });
    expect(await computeImpact(shell(`rm -rf ${"d/".repeat(60)}f`, POSIX_CWD), deps(deep, "linux", { now: () => slow }))).toMatchObject({ recoverable: "unknown", reason: AMBIGUOUS });
  });

  it("the Git search stops after 40 levels: unknown", async () => {
    const deepCwd = "/" + Array.from({ length: 45 }, (_, k) => `d${k}`).join("/");
    const fs = fakeFs("linux", { [`${deepCwd}/x`]: 1 });
    expect(await computeImpact(shell("rm -rf x", deepCwd), deps(fs, "linux"))).toMatchObject({ recoverable: "unknown", reason: AMBIGUOUS });
  });
});

describe("ImpactScheduler: the global bound (ruling A8)", () => {
  it("one computation at a time in the whole bridge, at most 8 waiting (oldest dropped), closed ones skipped", async () => {
    const q = new ImpactScheduler();
    const ran: string[] = [];
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    let running = 0;
    let maxRunning = 0;
    const job = (id: string, wait?: Promise<void>) => async () => {
      running++;
      maxRunning = Math.max(maxRunning, running);
      ran.push(id);
      await wait;
      running--;
    };
    q.enqueue(job("j0", gate), () => true);
    for (let k = 1; k <= 12; k++) q.enqueue(job(`j${k}`), () => k !== 9);
    expect(q.waiting).toBe(8);
    await new Promise((r) => setTimeout(r, 10));
    expect(ran).toEqual(["j0"]);
    release();
    await new Promise((r) => setTimeout(r, 20));
    expect(ran).toEqual(["j0", "j5", "j6", "j7", "j8", "j10", "j11", "j12"]);
    expect(maxRunning).toBe(1);
  });

  it("2 s of work per rolling 10 s: past it a job runs with allowed=false; the budget comes back", async () => {
    let clock = 0;
    const q = new ImpactScheduler(() => clock);
    const allowed: boolean[] = [];
    const job = async (ok: boolean) => {
      allowed.push(ok);
      if (ok) clock += 600;
    };
    for (let k = 0; k < 6; k++) q.enqueue(job, () => true);
    await new Promise((r) => setTimeout(r, 20));
    expect(allowed).toEqual([true, true, true, true, false, false]);
    clock += 10_000;
    q.enqueue(job, () => true);
    await new Promise((r) => setTimeout(r, 10));
    expect(allowed.at(-1)).toBe(true);
  });

  it("50 forged requests with different session ids: bounded work, the event loop stays responsive, nothing grows", async () => {
    const big: Spec = {};
    for (let k = 0; k < 20_000; k++) big[`/work/shop/s/d${k % 60}/f${k}`] = 1;
    const fs = fakeFs("linux", big);
    const q = new ImpactScheduler();
    let computations = 0;
    let maxWaiting = 0;
    let maxGap = 0;
    let last = performance.now();
    const timer = setInterval(() => {
      const t = performance.now();
      maxGap = Math.max(maxGap, t - last);
      last = t;
    }, 5);
    const done: Promise<void>[] = [];
    for (let k = 0; k < 50; k++) {
      done.push(
        new Promise<void>((resolve) => {
          q.enqueue(
            async (ok) => {
              if (ok) {
                computations++;
                await computeImpact(shell("rm -rf s", POSIX_CWD), deps(fs, "linux"));
              }
              resolve();
            },
            () => true,
          );
          maxWaiting = Math.max(maxWaiting, q.waiting);
        }),
      );
    }
    await Promise.race([Promise.all(done.slice(-8)), new Promise((r) => setTimeout(r, 15_000))]);
    maxGap = Math.max(maxGap, performance.now() - last); // a stretch blocked up to the very end counts too
    clearInterval(timer);
    expect(computations).toBeLessThanOrEqual(9);
    expect(maxWaiting).toBeLessThanOrEqual(8);
    expect(maxGap).toBeLessThan(50);
  }, 30_000);
});

// ---- ruling A1: no reassuring value anywhere ----

describe("no 'yes' and no 'partly' (ruling A1)", () => {
  const src = join(import.meta.dirname, "..", "src");
  const demo = join(import.meta.dirname, "..", "..", "demo");

  it("not in the impact module, not as a recoverable value in any source, not in the demo pack or overrides", () => {
    const impact = readFileSync(join(src, "impact.ts"), "utf8");
    expect(impact).not.toMatch(/["'](yes|partly)["']/);
    const files = readdirSync(src, { recursive: true, encoding: "utf8" }).filter((f) => f.endsWith(".ts"));
    for (const f of files) {
      const text = readFileSync(join(src, f), "utf8");
      expect(text, f).not.toMatch(/recoverable[^\n]{0,60}["'](yes|partly)["']/i);
      expect(text, f).not.toMatch(/RECOVERABLE[^\n]*["'](yes|partly)["']/);
    }
    for (const f of ["jury.pack.json", "jury.overrides.json"]) {
      expect(readFileSync(join(demo, f), "utf8"), f).not.toMatch(/"recoverable"\s*:\s*"(yes|partly)"/);
    }
  });
});

// Runs last in this file (tests in a file run in order): the invariant across all tests above.
describe("invariant", () => {
  it("no process was started by the impact check", () => {
    expect(spawned).toEqual([]);
  });
});

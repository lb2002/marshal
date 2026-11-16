// Fix round 2 (rulings task-p2-fix-2.md, review task-p2-review-2.md): an impact carries facts only when the whole
// command line is exactly one simple command the bridge understands, with literal arguments. Everything else gets
// one fixed unknown impact and touches nothing on disk.
import { posix, win32 } from "node:path";
import { describe, expect, it } from "vitest";
import { ImpactScheduler, computeImpact } from "../src/impact.js";
import type { EntryKind, Impact, ImpactDeps, ImpactFs, ImpactRequest } from "../src/impact.js";

const NOT_ONE: Impact = { kind: "other", recoverable: "unknown", reason: "Several steps or unclear. Effects unknown." };
const NOT_IN_GIT = "Not in a Git project: gone for good.";
const IN_GIT = "In a Git project. Marshal does not check whether everything is saved.";
const WHOLE_REPO = "Deletes a whole Git project with its history: gone for good.";
const DESTRUCTIVE = { risk: "destructive" as const, reason: "test" };

type Spec = Record<string, number | "dir" | "link">;
type Platform = "win32" | "linux" | "darwin";

/** A fake file system that records every call (both kinds) in one log. */
function fakeFs(platform: Platform, spec: Spec): ImpactFs & { calls: string[] } {
  const P = platform === "win32" ? win32 : posix;
  const nodes = new Map<string, { kind: EntryKind; size: number }>();
  const children = new Map<string, Set<string>>();
  for (const [p, v] of Object.entries(spec)) {
    nodes.set(p, typeof v === "number" ? { kind: "file", size: v } : { kind: v, size: 0 });
    for (let d = p; ; ) {
      const up = P.dirname(d);
      if (up === d) break;
      if (!children.has(up)) children.set(up, new Set());
      (children.get(up) as Set<string>).add(P.basename(d));
      if (!nodes.has(up)) nodes.set(up, { kind: "dir", size: 0 });
      d = up;
    }
  }
  const calls: string[] = [];
  return {
    calls,
    async lstat(p) {
      calls.push(`lstat ${p}`);
      const n = nodes.get(p);
      return n ? { kind: n.kind, size: n.size, id: p } : null;
    },
    async readdir(p, max) {
      calls.push(`readdir ${p}`);
      const all = [...(children.get(p) ?? [])].slice(0, max);
      return { entries: all.map((name) => ({ name, kind: (nodes.get(P.join(p, name)) as { kind: EntryKind }).kind })), more: false };
    },
  };
}

const WIN = "C:\\Users\\u\\proj";
const NIX = "/home/u/proj";
const winTree: Spec = {
  [`${WIN}\\small`]: 5, [`${WIN}\\dist\\x.js`]: 10, [`${WIN}\\Documents\\a`]: 1, [`${WIN}\\foo[0-9]`]: 1, [`${WIN}\\foo1\\x`]: 99,
  [`${WIN}\\go.sh`]: 10, "C:\\Users\\u\\Documents\\b1": 1000,
};
const nixTree: Spec = Object.fromEntries(Object.entries(winTree).map(([k, v]) => [k.slice(2).replaceAll("\\", "/").replace("/Users/", "/home/"), v]));

function deps(fs: ImpactFs, platform: Platform): ImpactDeps {
  return { fs, now: Date.now, platform, homeDir: platform === "win32" ? "C:\\Users\\u" : "/home/u" };
}

async function run(command: string, o: { tool?: string; platform?: Platform; source?: ImpactRequest["source"] } = {}) {
  const platform = o.platform ?? "linux";
  const fs = fakeFs(platform, platform === "win32" ? winTree : nixTree);
  const req: ImpactRequest = { tool: o.tool ?? "Bash", input: { command }, cwd: platform === "win32" ? WIN : NIX, source: o.source, risk: DESTRUCTIVE };
  const impact = await computeImpact(req, deps(fs, platform));
  return { impact, calls: fs.calls };
}

// ---- review 2: every listed attack is the fixed unknown impact and touches nothing ----

describe("review 2 attacks (C-1, I-1, I-2): not eligible, no file-system access at all", () => {
  const bash = [
    // C-1: a directory change or a shadowed deleter the impact cannot see
    ". ./go.sh && rm -rf Documents",
    "source go.sh; rm -rf Documents",
    "f(){ cd ..; }; f; rm -rf Documents",
    "{ cd ..; }; rm -rf Documents",
    "if true; then cd ..; fi; rm -rf Documents",
    "builtin cd .. && rm -rf Documents",
    "command cd .. && rm -rf Documents",
    "eval 'cd ..'; rm -rf Documents",
    "rm(){ command rm -rf ../Documents; }; rm small",
    "(cd ..); rm -rf Documents",
    "cd .. && rm -rf Documents",
    "PATH=. rm small",
    // I-1: other parts of the same line
    "rm -rf small && find ~ -delete",
    "rm -rf small; python -c 'import shutil; shutil.rmtree(\"/x\")'",
    "mv ../Documents /dev/null; rm small",
    "cp small ../Documents/b1 && rm small",
    "rm -rf small > ../Documents/b1",
    "rm -rf small 2> ../Documents/b1",
    "git rm -rf . && rm -rf small",
    "rm -rf small & rm -rf ../Documents",
    "rm -rf small | tee ../Documents/b1",
    "rm -rf small\nrm -rf ../Documents",
    "rm -rf small #; rm -rf ../Documents",
    // I-2 in a bash reading
    "rm -rf 'foo[0-9]'",
    "rm -rf foo[0-9]",
  ];
  for (const c of bash) {
    it(`bash: ${JSON.stringify(c)}`, async () => {
      for (const platform of ["linux", "win32"] as const) {
        const { impact, calls } = await run(c, { platform });
        expect(impact, platform).toEqual(NOT_ONE);
        expect(calls, platform).toEqual([]);
      }
    });
  }
  const pwsh = [
    "iex 'cd ..'; Remove-Item -Recurse Documents",
    "$ExecutionContext.SessionState.Path.SetLocation('..'); Remove-Item -Recurse Documents",
    "function f { Set-Location .. }; f; Remove-Item -Recurse Documents",
    ". { cd .. }; Remove-Item -Recurse Documents",
    "& { cd .. }; Remove-Item -Recurse Documents",
    "[IO.Directory]::SetCurrentDirectory('..'); Remove-Item -Recurse Documents",
    "Remove-Item -Recurse small; [IO.Directory]::Delete('C:\\Users\\u\\Documents',$true)",
    "Remove-Item small -Recurse; Clear-Content ..\\Documents\\b1",
    "Remove-Item -Recurse -Force small; Get-ChildItem .. -Recurse | Remove-Item -Force",
    "New-PSDrive -Name Q -PSProvider FileSystem -Root ..; Remove-Item -Recurse Q:\\Documents",
    // I-2: PowerShell wildcards, quoted or not, outside -LiteralPath
    "Remove-Item 'foo[0-9]' -Recurse",
    "Remove-Item -Path 'foo[0-9]' -Recurse",
    "Remove-Item foo[0-9] -Recurse",
    "del 'foo[0-9]'",
    "Remove-Item 'sr[c]' -Recurse",
    "Remove-Item 'sm*' -Recurse",
    'Remove-Item -Path "sm?ll" -Recurse',
    "Remove-Item `small -Recurse",
  ];
  for (const c of pwsh) {
    it(`pwsh: ${JSON.stringify(c)}`, async () => {
      const { impact, calls } = await run(c, { tool: "PowerShell", platform: "win32" });
      expect(impact).toEqual(NOT_ONE);
      expect(calls).toEqual([]);
    });
  }

  it("-LiteralPath 'foo[0-9]' is the literal file (counted: 1 file), not foo1", async () => {
    const { impact, calls } = await run("Remove-Item -LiteralPath 'foo[0-9]' -Recurse", { tool: "PowerShell", platform: "win32" });
    expect(impact).toMatchObject({ kind: "delete", files: 1, bytes: 1, recoverable: "no" });
    expect(calls.some((c) => c.includes("foo1"))).toBe(false);
  });
});

// ---- the eligibility table (ruling A2, A3, A5) ----

type Row = [eligible: boolean, command: string, mode?: "pwsh"];
const ROWS: Row[] = [
  // bash: eligible
  [true, "rm small"],
  [true, "rm -rf dist"],
  [true, "rm -fr dist"],
  [true, "rm -Rf dist"],
  [true, "rm -r -f dist"],
  [true, "rm --recursive --force dist"],
  [true, "rm -rfv dist"],
  [true, "rm -d dist"],
  [true, "rm -i small"],
  [true, "rm -I small"],
  [true, "rm --dir --verbose small"],
  [true, "rm -- small"],
  [true, "rm -rf -- dist"],
  [true, "rm 'small'"],
  [true, 'rm "small"'],
  [true, "rm -rf small dist"],
  [true, "rm -rf dist/"],
  [true, "rm\t-rf\tdist"],
  [true, "rmdir dist"],
  [true, "unlink small"],
  [true, "git push --force origin main"],
  [true, "git push -f"],
  [true, "git push --force-with-lease origin main"],
  [true, "git push --force-with-lease=main origin main"],
  [true, "git push origin +main"],
  [true, "git reset --hard"],
  [true, "git reset --hard HEAD~1"],
  [true, "git clean -fd"],
  [true, "git clean -fdx -- build"],
  [true, "git clean --force"],
  // bash: not eligible
  [false, "rm -rf small; ls"],
  [false, "rm -rf small || true"],
  [false, "rm -rf small && ls"],
  [false, "rm -rf small | cat"],
  [false, "rm -rf small &"],
  [false, "rm -rf small\nls"],
  [false, "rm -rf small > out"],
  [false, "rm -rf small 2>/dev/null"],
  [false, "rm -rf small < in"],
  [false, "rm -rf small <<< x"],
  [false, "rm -rf small <<EOF\nx\nEOF"],
  [false, "(rm -rf small)"],
  [false, "{ rm -rf small; }"],
  [false, "rm -rf $(cat list)"],
  [false, "rm -rf `cat list`"],
  [false, "rm -rf <(ls)"],
  [false, "f(){ rm -rf small; }; f"],
  [false, "if true; then rm -rf small; fi"],
  [false, "for x in a; do rm -rf $x; done"],
  [false, "while false; do rm small; done"],
  [false, "case a in a) rm small;; esac"],
  [false, "! rm -rf small"],
  [false, "time rm -rf small"],
  [false, "X=1 rm -rf small"],
  [false, "rm -rf $HOME/x"],
  [false, "rm -rf ${D}"],
  [false, "rm -rf ~/x"],
  [false, "rm -rf ~u/x"],
  [false, "rm -rf *.log"],
  [false, "rm -rf sm?ll"],
  [false, "rm -rf {a,b}"],
  [false, "rm -rf $'small'"],
  [false, '"rm" -rf small'],
  [false, "r\\m -rf small"],
  [false, "rm -rf sm\\all"],
  [false, "/bin/rm -rf small"],
  [false, "./rm small"],
  [false, "command rm -rf small"],
  [false, "builtin rm small"],
  [false, "exec rm -rf small"],
  [false, "eval 'rm -rf small'"],
  [false, "source go.sh"],
  [false, ". go.sh"],
  [false, "sudo rm -rf small"],
  [false, "env rm -rf small"],
  [false, "nohup rm -rf small"],
  [false, "xargs rm -rf"],
  [false, "find . -delete"],
  [false, "sh -c 'rm -rf small'"],
  [false, "bash -c 'rm -rf small'"],
  [false, "rm --no-preserve-root -rf small"],
  [false, "rm -rf --one-file-system small"],
  [false, "rm -rfx small"],
  [false, "rm -rf ."],
  [false, "rm -rf .."],
  [false, "rm -rf dist/.."],
  [false, "rm -rf ''"],
  [false, "rm -rf"],
  [false, "rmdir -p dist"],
  [false, "git -C .. reset --hard"],
  [false, "git -c x=y push --force"],
  [false, "git push --force --mirror"],
  [false, "git push --force --all origin"],
  [false, "git push origin main"],
  [false, "git push --force origin main extra"],
  [false, "git reset --hard -- x"],
  [false, "git reset --hard HEAD x"],
  [false, "git clean -fdn"],
  [false, "git clean -fe x"],
  [false, "git clean -d"],
  [false, "git clean -fi"],
  // PowerShell: eligible
  [true, "Remove-Item small", "pwsh"],
  [true, "remove-item small", "pwsh"],
  [true, "Remove-Item -Recurse -Force dist", "pwsh"],
  [true, "Remove-Item -Path dist -Recurse", "pwsh"],
  [true, "Remove-Item -LiteralPath 'foo[0-9]'", "pwsh"],
  [true, "Remove-Item -LiteralPath foo[0-9]", "pwsh"],
  [true, "rm -r -fo dist", "pwsh"],
  [true, "del small", "pwsh"],
  [true, "ri small", "pwsh"],
  [true, "erase small", "pwsh"],
  [true, "rd dist -Recurse", "pwsh"],
  [true, "rmdir dist", "pwsh"],
  [true, "Remove-Item dist -Recurse -Confirm:$false", "pwsh"],
  [true, "Remove-Item dist -Recurse -ErrorAction SilentlyContinue", "pwsh"],
  [true, "Remove-Item dist -Recurse -ea 0", "pwsh"],
  [true, "Remove-Item 'small'", "pwsh"],
  [true, "git push --force origin main", "pwsh"],
  // PowerShell: not eligible
  [false, "Remove-Item small; ls", "pwsh"],
  [false, "Remove-Item small | Out-Null", "pwsh"],
  [false, "Remove-Item small && ls", "pwsh"],
  [false, "Remove-Item small || ls", "pwsh"],
  [false, "Remove-Item small\nls", "pwsh"],
  [false, "Remove-Item $env:X", "pwsh"],
  [false, 'Remove-Item "$HOME\\x"', "pwsh"],
  [false, "Remove-Item @args", "pwsh"],
  [false, "& Remove-Item small", "pwsh"],
  [false, "iex 'Remove-Item small'", "pwsh"],
  [false, "Invoke-Expression 'Remove-Item small'", "pwsh"],
  [false, "[IO.Directory]::Delete('x', $true)", "pwsh"],
  [false, "Remove-Item $(Get-Location)", "pwsh"],
  [false, "Remove-Item small > out.txt", "pwsh"],
  [false, "Remove-Item small 2>$null", "pwsh"],
  [false, "Remove-Item a,b", "pwsh"],
  [false, "Remove-Item small -Include *.js", "pwsh"],
  [false, "Remove-Item small -Filter x", "pwsh"],
  [false, "Remove-Item small -Exclude x", "pwsh"],
  [false, "Remove-Item small -WhatIf", "pwsh"],
  [false, "Remove-Item -Path:small", "pwsh"],
  [false, "Remove-Item -f small", "pwsh"],
  [false, "Remove-Item small -Recurse # c", "pwsh"],
  [false, "Get-ChildItem | Remove-Item", "pwsh"],
  [false, "cd ..; Remove-Item Documents", "pwsh"],
  [false, "Microsoft.PowerShell.Management\\Remove-Item small", "pwsh"],
  [false, "'Remove-Item' small", "pwsh"],
  [false, "Remove-Item -Path", "pwsh"],
  [false, "Remove-Item", "pwsh"],
  [false, "Remove-Item {small}", "pwsh"],
  [false, "Remove-Item (Get-Item small)", "pwsh"],
];

describe("eligibility table", () => {
  it("has at least 60 rows across bash and PowerShell", () => {
    expect(ROWS.length).toBeGreaterThanOrEqual(60);
    expect(ROWS.filter((r) => r[2] === "pwsh").length).toBeGreaterThanOrEqual(20);
  });
  for (const [eligible, command, mode] of ROWS) {
    it(`${eligible ? "eligible" : "not eligible"} (${mode ?? "bash"}): ${JSON.stringify(command)}`, async () => {
      const platform = mode === "pwsh" ? "win32" : "linux";
      const { impact, calls } = await run(command, { tool: mode === "pwsh" ? "PowerShell" : "Bash", platform });
      if (eligible) {
        expect(impact).not.toEqual(NOT_ONE);
        expect(impact).not.toBeNull();
      } else {
        expect(impact).toEqual(NOT_ONE);
        expect(calls).toEqual([]);
      }
    });
  }

  it("other agents on Windows: both readings must be eligible and agree", async () => {
    expect((await run("rm -rf dist", { platform: "win32", source: "codex" })).impact).toEqual(NOT_ONE); // -rf is no PowerShell parameter
    expect((await run("rm dist", { platform: "win32", source: "codex" })).impact).toMatchObject({ kind: "delete", files: 1 });
    expect((await run("git reset --hard", { platform: "win32", source: "gemini" })).impact).toMatchObject({ kind: "reset", recoverable: "no" });
  });
});

// ---- ruling A6, A7, M-4 ----

describe("what an eligible line reports", () => {
  it("the target is the examined path, resolved and shortened", async () => {
    const fs = fakeFs("linux", { "/home/u/code/legacy-site/a": 1, "/home/u/code/shop/x": 1 });
    const i = await computeImpact({ tool: "Bash", input: { command: "rm -rf ../legacy-site" }, cwd: "/home/u/code/shop", risk: DESTRUCTIVE }, deps(fs, "linux"));
    expect(i).toEqual({ kind: "delete", target: "~/code/legacy-site", files: 1, bytes: 1, outsideProject: true, recoverable: "no", reason: NOT_IN_GIT });
    const w = await computeImpact({ tool: "Bash", input: { command: "rm -rf /c/Users/u/proj/dist" }, cwd: WIN, risk: DESTRUCTIVE }, deps(fakeFs("win32", winTree), "win32"));
    expect(w).toMatchObject({ target: "~/proj/dist", files: 1 });
  });

  it("a target directory that holds a .git entry: the whole project with its history", async () => {
    const fs = fakeFs("linux", { "/home/u/code/old/.git/HEAD": 20, "/home/u/code/old/a": 5, "/home/u/code/shop/x": 1 });
    expect(await computeImpact({ tool: "Bash", input: { command: "rm -rf ../old" }, cwd: "/home/u/code/shop", risk: DESTRUCTIVE }, deps(fs, "linux"))).toMatchObject({ files: 2, recoverable: "no", reason: WHOLE_REPO });
    const repo = fakeFs("linux", { "/home/u/code/shop/.git": "dir", "/home/u/code/shop/x": 1 });
    expect(await computeImpact({ tool: "Bash", input: { command: "rm -rf /home/u/code/shop" }, cwd: "/home/u/code/shop", risk: DESTRUCTIVE }, deps(repo, "linux"))).toMatchObject({ reason: WHOLE_REPO, recoverable: "no" });
    expect(await computeImpact({ tool: "Bash", input: { command: "rm -rf x" }, cwd: "/home/u/code/shop", risk: DESTRUCTIVE }, deps(repo, "linux"))).toMatchObject({ reason: IN_GIT, recoverable: "unknown" });
  });

  it("outsideProject is left out when the cwd is the home directory or a root (review M-4)", async () => {
    for (const cwd of ["/home/u", "/"]) {
      const fs = fakeFs("linux", { "/home/u/Documents/a": 1 });
      const i = (await computeImpact({ tool: "Bash", input: { command: "rm -rf /home/u/Documents" }, cwd, risk: DESTRUCTIVE }, deps(fs, "linux"))) as Impact;
      expect(i, cwd).toMatchObject({ files: 1, recoverable: "no" });
      expect("outsideProject" in i, cwd).toBe(false);
    }
  });
});

// ---- ruling B: no long synchronous work ----

describe("long commands (ruling B, review I-3)", () => {
  it("a command over 4,096 characters is not eligible, decided in constant time without lexing", async () => {
    const mb = "rm -rf " + "a ".repeat(512 * 1024);
    for (const command of [mb, "rm " + "x".repeat(4094), [..."rm -rf a".split(" "), "b".repeat(5000)]]) {
      const fs = fakeFs("linux", nixTree);
      const t0 = performance.now();
      const i = await computeImpact({ tool: "Bash", input: { command }, cwd: NIX, source: "codex", risk: DESTRUCTIVE }, deps(fs, "win32"));
      expect(performance.now() - t0).toBeLessThan(20);
      expect(i).toEqual(NOT_ONE);
      expect(fs.calls).toEqual([]);
    }
    const exact = "rm " + "x".repeat(4093);
    expect(exact.length).toBe(4096);
    expect(await computeImpact({ tool: "Bash", input: { command: exact }, cwd: NIX, risk: DESTRUCTIVE }, deps(fakeFs("linux", nixTree), "linux"))).not.toEqual(NOT_ONE);
  });

  it("50 forged requests of 4,096 characters and of 1 MB: the event loop stays responsive and memory does not grow with the count", async () => {
    const commands = [
      "rm -rf " + Array.from({ length: 1000 }, (_, k) => `a${k}`).join(" ").slice(0, 4089), // eligible length, many targets
      "rm -rf " + "d/".repeat(2044) + "x", // one long path
      "rm -rf a; " + "x".repeat(4086), // a chain
      "rm -rf " + "a ".repeat(512 * 1024), // 1 MB
      "x\n".repeat(512 * 1024), // 1 MB of lines
    ];
    const fs = fakeFs("linux", nixTree);
    const q = new ImpactScheduler();
    let maxGap = 0;
    let last = performance.now();
    const timer = setInterval(() => {
      const t = performance.now();
      maxGap = Math.max(maxGap, t - last);
      last = t;
    }, 5);
    const heap0 = process.memoryUsage().heapUsed;
    const done: Promise<void>[] = [];
    for (let k = 0; k < 50; k++) {
      done.push(
        new Promise<void>((resolve) => {
          q.enqueue(async (ok) => {
            for (const platform of ["win32", "linux"] as const) {
              if (ok) await computeImpact({ tool: "Bash", input: { command: commands[k % commands.length] }, cwd: NIX, source: "codex", risk: DESTRUCTIVE }, deps(fs, platform));
            }
            resolve();
          }, () => true);
        }),
      );
      expect(q.waiting).toBeLessThanOrEqual(8);
    }
    await Promise.race([Promise.all(done.slice(-8)), new Promise((r) => setTimeout(r, 15_000))]);
    maxGap = Math.max(maxGap, performance.now() - last);
    clearInterval(timer);
    expect(maxGap).toBeLessThan(50);
    expect(process.memoryUsage().heapUsed - heap0).toBeLessThan(64 * 1024 * 1024);
  }, 30_000);
});

describe("scheduler watchdog (review M-8)", () => {
  it("short jobs never add up to one long block: a full event-loop turn between jobs", async () => {
    const q = new ImpactScheduler();
    const busy = (ms: number) => {
      const end = performance.now() + ms;
      while (performance.now() < end) {
        /* synchronous work */
      }
    };
    let maxGap = 0;
    let last = performance.now();
    const timer = setInterval(() => {
      const t = performance.now();
      maxGap = Math.max(maxGap, t - last);
      last = t;
    }, 1);
    let done = 0;
    for (let k = 0; k < 9; k++) q.enqueue(async () => (busy(8), done++), () => true);
    while (done < 9) await new Promise((r) => setTimeout(r, 5));
    clearInterval(timer);
    expect(maxGap).toBeLessThan(30); // 9 x 8 ms in one turn would be over 70 ms
  });

  it("a job that never finishes releases the slot after the watchdog time", async () => {
    const q = new ImpactScheduler(Date.now, 8, 2000, 10_000, 30);
    const ran: string[] = [];
    q.enqueue(async () => {
      ran.push("hung");
      await new Promise(() => {});
    }, () => true);
    q.enqueue(async () => {
      ran.push("next");
    }, () => true);
    await new Promise((r) => setTimeout(r, 10));
    expect(ran).toEqual(["hung"]);
    await new Promise((r) => setTimeout(r, 60));
    expect(ran).toEqual(["hung", "next"]);
  });
});

// ---- argument arrays (coordinator ruling): never joined and re-lexed ----

describe("a command given as an argument array", () => {
  const tree: Spec = { "/home/u/proj/a b/f": 3, "/home/u/proj/a/x": 100, "/home/u/proj/b/y": 100, "/home/u/proj/build/z": 7, "/home/u/Documents/d": 50 };
  async function arr(command: unknown[], platform: Platform = "linux") {
    const fs = fakeFs(platform, tree);
    const impact = await computeImpact({ tool: "Bash", input: { command }, cwd: NIX, risk: DESTRUCTIVE }, deps(fs, platform));
    return { impact, calls: fs.calls };
  }

  it('["rm","-rf","a b"] examines exactly the one path "a b"', async () => {
    const { impact, calls } = await arr(["rm", "-rf", "a b"]);
    expect(impact).toMatchObject({ kind: "delete", files: 1, bytes: 3, recoverable: "no" });
    expect(calls).toContain("lstat /home/u/proj/a b");
    expect(calls.some((c) => c === "lstat /home/u/proj/a" || c === "lstat /home/u/proj/b")).toBe(false);
  });

  it('["rm","-rf","a; rm -rf ~"] is the literal odd name (unknown here: it does not exist), never ~', async () => {
    const { impact, calls } = await arr(["rm", "-rf", "a; rm -rf ~"]);
    expect(impact).toMatchObject({ recoverable: "unknown" });
    // the cwd walk (/, /home, /home/u, /home/u/proj), then only the literal name
    expect(calls).toEqual(["lstat /", "lstat /home", "lstat /home/u", "lstat /home/u/proj", "lstat /home/u/proj/a; rm -rf ~"]);
  });

  it("a shell wrapper with exactly one -c/-lc string is examined by that shell's rules", async () => {
    expect((await arr(["bash", "-lc", "rm -rf build"])).impact).toMatchObject({ kind: "delete", files: 1, bytes: 7 });
    expect((await arr(["/bin/sh", "-c", "rm -rf build"])).impact).toMatchObject({ kind: "delete", files: 1 });
    expect((await arr(["pwsh", "-NoProfile", "-Command", "Remove-Item -Recurse build"], "win32")).impact).not.toEqual(NOT_ONE);
    for (const c of [
      ["bash", "-lc", "cd ~ && rm -rf Documents"],
      ["bash", "-c", "rm -rf x", "extra"],
      ["bash", "rm", "-rf", "x"],
      ["bash", "-x", "-c", "rm -rf build"],
      ["cmd.exe", "/c", "del build"],
      ["zsh", "-c"],
      ["env", "rm", "-rf", "build"],
      ["/bin/rm", "-rf", "build"],
      ["rm", "--no-preserve-root", "-rf", "build"],
      [],
      ["rm", 1],
      Array.from({ length: 65 }, () => "x"),
    ]) {
      const { impact, calls } = await arr(c);
      expect(impact, JSON.stringify(c).slice(0, 60)).toEqual(NOT_ONE);
      expect(calls, JSON.stringify(c).slice(0, 60)).toEqual([]);
    }
  });

  it("git as an array", async () => {
    expect((await arr(["git", "push", "--force", "origin", "main"])).impact).toMatchObject({ kind: "force_push", target: "origin main", recoverable: "no" });
    expect((await arr(["git", "-C", "..", "reset", "--hard"])).impact).toEqual(NOT_ONE);
  });
});

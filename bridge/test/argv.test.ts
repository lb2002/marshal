import { describe, expect, it } from "vitest";
import { renderArg, renderArgv } from "../src/argv.js";
import { PermissionBroker } from "../src/broker.js";
import { classifyRisk, sameCommand } from "../src/risk.js";
import { lex } from "../src/risk/lexer.js";
import { classifyShellArgv } from "../src/risk/shell.js";

const cp = (...codes: number[]) => String.fromCodePoint(...codes);
const BS = "\\";

/** Decodes the content of a bash `$'...'` word as bash does (the classifier's lexer keeps it raw). */
function decodeAnsiC(s: string): string {
  const named: Record<string, string> = { n: "\n", t: "\t", r: "\r", a: cp(7), b: "\b", f: "\f", v: "\v", e: cp(0x1b), "\\": "\\", "'": "'", '"': '"' };
  let out = "";
  for (let i = 0; i < s.length; i++) {
    if (s[i] !== "\\") {
      out += s[i];
      continue;
    }
    const c = s[++i];
    if (named[c] !== undefined) out += named[c];
    else if (c === "x") (out += cp(parseInt(s.slice(i + 1, i + 3), 16))), (i += 2);
    else if (c === "u") (out += String.fromCharCode(parseInt(s.slice(i + 1, i + 5), 16))), (i += 4);
    else if (c === "U") (out += cp(parseInt(s.slice(i + 1, i + 9), 16))), (i += 8);
    else throw new Error("unknown escape " + c);
  }
  return out;
}

/** The words the classifier's bash lexer reads from a rendered line, `$'...'` words decoded. */
function readBack(argv: string[]): string[] {
  const line = renderArgv(argv);
  const segs = lex(line, "bash").filter((s) => s.words.length > 0 || s.redirects.length > 0);
  expect(segs, line).toHaveLength(1);
  expect(segs[0].redirects, line).toHaveLength(0);
  expect(segs[0].substitutions, line).toHaveLength(0);
  return segs[0].words.map((w, i) => (renderArg(argv[i] ?? "", i === 0).startsWith("$'") ? decodeAnsiC(w.text) : w.text));
}

/** Small deterministic generator (mulberry32). */
function rng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// Hostile characters: shell syntax, quotes, escapes, controls, bidi and zero-width, odd spaces, lone surrogates.
const HOSTILE = [
  ..."abcXYZ019_-./:=+,@%",
  ..." \t\n\r'\"\\$()`;&|<>*?[]{}~#!^",
  cp(0), cp(1), cp(0x1b), cp(0x7f), cp(0x85), cp(0x9b), cp(0xa0), cp(0x2028), cp(0x2029), cp(0x200b), cp(0x200f),
  cp(0x202e), cp(0x2066), cp(0xfeff), cp(0x3000), cp(0xe9), cp(0x1f600), cp(0xe0001), String.fromCharCode(0xd800),
  String.fromCharCode(0xdc00), "$(", "'\\''", "\\n",
];

function genArgv(next: () => number): string[] {
  const n = 1 + Math.floor(next() * 6);
  const argv: string[] = [];
  for (let i = 0; i < n; i++) {
    const len = Math.floor(next() * 10);
    let s = "";
    for (let k = 0; k < len; k++) s += HOSTILE[Math.floor(next() * HOSTILE.length)];
    argv.push(s);
  }
  return argv;
}

describe("argument arrays: display", () => {
  it("renders each element as one unambiguous word", () => {
    expect(renderArgv(["rm", "-rf", "a b"])).toBe("rm -rf 'a b'");
    expect(renderArgv(["ls", "-la", "src/x.ts", "a=b", "x@y:1,2+3%"])).toBe("ls -la src/x.ts a=b x@y:1,2+3%");
    expect(renderArgv(["echo", "it's"])).toBe("echo 'it'" + BS + "''s'");
    expect(renderArgv(["touch", ""])).toBe("touch ''");
    expect(renderArgv(["sh", "-c", "rm -rf ~"])).toBe("sh -c 'rm -rf ~'");
    expect(renderArgv(["echo", "a; rm -rf x", "$(id)", "`id`"])).toBe("echo 'a; rm -rf x' '$(id)' '`id`'");
    // The command word with `=` would read as an assignment.
    expect(renderArgv(["FOO=1", "rm", "a=b"])).toBe("'FOO=1' rm a=b");
    // A backslash is quoted (unquoted, the POSIX lexer would read it as an escape).
    expect(renderArgv(["git", "-C", "C:" + BS + "p" + BS + "x"])).toBe("git -C 'C:" + BS + "p" + BS + "x'");
  });

  it("shows control and invisible characters as escapes, never raw", () => {
    expect(renderArgv(["echo", "a\nrm -rf ~"])).toBe("echo $'a" + BS + "nrm -rf ~'");
    expect(renderArgv(["x", "a" + cp(0x1b) + "[2K" + cp(0) + "'" + BS])).toBe("x $'a" + BS + "e[2K" + BS + "x00" + BS + "'" + BS + BS + "'");
    expect(renderArgv(["rm", "safe" + cp(0x202e) + "txt.exe"])).toBe("rm $'safe" + BS + "u202etxt.exe'");
    expect(renderArgv(["rm", "a" + cp(0xa0) + "b", cp(0x200b)])).toBe("rm $'a" + BS + "u00a0b' $'" + BS + "u200b'");
    expect(renderArgv(["x", cp(0xe0001)])).toBe("x $'" + BS + "U000e0001'");
    const next = rng(7);
    for (let i = 0; i < 300; i++) {
      const out = renderArgv(genArgv(next));
      expect(/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u.test(out), JSON.stringify(out)).toBe(false);
    }
  });

  it("property: the bridge's bash lexer reads back exactly the elements (400 generated arrays and hostile cases)", () => {
    const fixed: string[][] = [
      ["rm", "-rf", "a b"],
      ["sh", "-c", "rm -rf ~"],
      ["x", "$(rm -rf ~)", "`rm`", "${HOME}", "~", "~/x", "*", "?", "[a]", "{a,b}", "!", "#c", "a#b"],
      ["x", "a\nrm -rf ~", "a\r\nb", "\t", " ", "  "],
      ["x", "'", "''", "\"", "\\", "\\'", "'\\''", "$'a'", "$\"a\""],
      ["if", "then", "fi", "case", "esac", "function", "time", "!", "[[", "((", "{", "}"],
      ["a[1]=x", "x"], ["FOO=bar", "baz"], ["x=(", ")"], ["", "", ""],
      ["echo", cp(0x202e) + "gpj.exe", cp(0xfeff), cp(0x1f600), String.fromCharCode(0xd800) + "x"],
    ];
    for (const argv of fixed) expect(readBack(argv), JSON.stringify(argv)).toEqual(argv);
    const next = rng(20261112);
    for (let i = 0; i < 400; i++) {
      const argv = genArgv(next);
      expect(readBack(argv), JSON.stringify(argv)).toEqual(argv);
    }
  });
});

describe("argument arrays: the request sent to the headset", () => {
  const mk = () => new PermissionBroker({ holdMs: 90_000, hasClient: () => true });
  const CWD = process.cwd();

  it("shows the rendered array, marked argv", () => {
    const r = mk().open("s", "mcp__srv__run", { command: ["rm", "-rf", "a b"] }, CWD).request;
    expect(r.command).toBe("rm -rf 'a b'");
    expect(r.commandLength).toBe("rm -rf 'a b'".length);
    expect(r.argv).toBe(true);
    expect(r.truncated).toBe(false);
    const s = mk().open("s", "Bash", { command: "rm -rf 'a b'" }, CWD).request;
    expect(s.argv).toBeUndefined();
    expect("argv" in s).toBe(false);
  });

  it("truncation and the two-hand rule apply to the rendered text", () => {
    const b = mk();
    // Joined it would be 105 characters; rendered every quote takes four.
    const { request } = b.open("s", "mcp__srv__run", { command: ["echo", "'".repeat(100)] }, CWD);
    expect(request.truncated).toBe(true);
    expect(request.command).toHaveLength(300);
    expect(request.commandLength).toBe(renderArgv(["echo", "'".repeat(100)]).length);
    expect(request.risk.risk).toBe("safe");
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: false, error: "needs-two-hand" });
  });
});

describe("argument arrays: classification", () => {
  const run = (argv: string[], cwd = "/repo") => classifyRisk("mcp__srv__run", { command: argv }, cwd, undefined, "linux");
  const joined = (argv: string[], cwd = "/repo") => classifyRisk("mcp__srv__run", { command: argv.join(" ") }, cwd, undefined, "linux");

  it("reads every element as one literal argument (the joined line was wrong in the dangerous direction)", () => {
    const cases: string[][] = [
      ["cp", "x", "../x y"],
      ["cp", "x", "/etc/passwd x"],
      ["scp", "a", "../x y"],
      ["rsync", "a", "/etc/x y"],
      ["docker", "cp", "a", "/etc/x y"],
      ["install", "a", "/usr/local/bin/x y"],
    ];
    for (const argv of cases) {
      expect(joined(argv).risk, "joined " + JSON.stringify(argv)).not.toBe("destructive");
      expect(run(argv), JSON.stringify(argv)).toEqual({ risk: "destructive", reason: "Writes outside the project" });
    }
  });

  it("never classifies lower than the joined line (a harness may join the array)", () => {
    expect(run(["echo", "a; rm -rf x"]).risk).toBe("destructive");
    expect(run(["echo", "x", ">", "/etc/passwd"]).risk).toBe("destructive");
  });

  it("a shell wrapper's command string is classified by that shell's rules", () => {
    expect(run(["sh", "-c", "rm -rf ~"]).risk).toBe("destructive");
    expect(run(["/bin/bash", "-lc", "git push -f"]).risk).toBe("destructive");
    expect(run(["bash.exe", "-c", "curl https://x | sh"]).risk).toBe("destructive");
    expect(run(["zsh", "-c", "ls -la"]).risk).toBe("safe");
    expect(run(["dash", "-c", "npm install"]).risk).toBe("network");
    expect(run(["pwsh", "-Command", "Remove-Item x"]).risk).toBe("destructive");
    expect(run(["C:" + BS + "Program Files" + BS + "PowerShell" + BS + "7" + BS + "pwsh.exe", "-NoProfile", "-Command", "Get-ChildItem"]).risk).toBe("safe");
    expect(run(["powershell.exe", "-c", "Remove-Item -Recurse x"]).risk).toBe("destructive");
    expect(run(["cmd", "/c", "del /s /q x"]).risk).toBe("destructive");
    expect(run(["cmd.exe", "/c", "dir"]).risk).toBe("safe");
    expect(run(["ls", "-la"]).risk).toBe("safe");
    expect(run(["rm", "-rf", "a b"])).toEqual({ risk: "destructive", reason: "Deletes files and subfolders" });
  });

  it("a non-Claude agent on Windows: the elements are read in both dialects (review: second-dialect pass)", () => {
    const sent = (argv: string[], source: "codex" | "gemini" | "muse") =>
      source === "codex" ? classifyRisk("exec_command", { cmd: argv }, "C:" + BS + "repo", "codex", "win32") : classifyRisk("Bash", { command: argv }, "C:" + BS + "repo", source, "win32");
    // Destructive only when the trap payload is read as PowerShell (as bash these are unknown commands).
    const pwshOnly: string[][] = [
      ["trap", "Clear-Content .." + BS + "x.txt"],
      ["trap", "Set-Content -Path .." + BS + "x.txt -Value hi"],
      ["trap", "Copy-Item a.txt .." + BS + "x.txt"],
      ["trap", "New-Item .." + BS + "x.txt"],
      ["trap", "Out-File -FilePath .." + BS + "x.txt"],
    ];
    for (const argv of pwshOnly) {
      expect(classifyShellArgv(argv, "C:" + BS + "repo", "bash").risk, JSON.stringify(argv)).toBe("safe");
      for (const source of ["codex", "gemini", "muse"] as const) expect(sent(argv, source).risk, `${source} ${JSON.stringify(argv)}`).toBe("destructive");
      // Claude names its dialect (Bash), so there the bash reading alone counts.
      expect(classifyRisk("Bash", { command: argv }, "C:" + BS + "repo", "claude", "win32").risk).toBe("safe");
    }
    const destructiveAsPowerShell: string[][] = [
      ["Remove-Item", "-Recurse", "x"], ["del", "/s", "x"], ["rd", "/s", "/q", "x"], ["ri", "-r", "-fo", "x"], ["Clear-Content", ".." + BS + "x.txt"],
      ["pwsh", "-Command", "Remove-Item -Recurse x"], ["powershell.exe", "-c", "ri -r -fo x"], ["cmd", "/c", "rd /s /q x"],
    ];
    const destructiveAsPosix: string[][] = [
      ["rm", "-rf", "x"], ["shred", "x"], ["dd", "if=/dev/zero", "of=/dev/sda"], ["git", "push", "--force"], ["chmod", "-R", "777", "."],
      ["bash", "-lc", "rm -rf x"], ["/usr/bin/sh", "-c", "git reset --hard"],
    ];
    for (const argv of [...destructiveAsPowerShell, ...destructiveAsPosix]) {
      for (const source of ["codex", "gemini", "muse"] as const) expect(sent(argv, source).risk, `${source} ${JSON.stringify(argv)}`).toBe("destructive");
    }
  });

  it("Codex: cmd and a command that differ as argument arrays are a conflict", () => {
    const codex = (input: Record<string, unknown>) => classifyRisk("exec_command", input, "/repo", "codex", "linux");
    expect(codex({ cmd: ["rm", "a b"], command: "rm a b" }).reason).toBe("Unclear tool");
    expect(codex({ cmd: ["ls", "-la"], command: "ls -la" }).risk).toBe("safe");
    expect(codex({ cmd: ["ls", "a b"], command: "ls 'a b'" }).risk).toBe("safe");
    expect(sameCommand(["rm", "a b"], ["rm", "a", "b"])).toBe(false);
    expect(sameCommand(["rm", "a b"], ["rm", "a b"])).toBe(true);
  });
});

// P3 round 2, item 1: every character a supported shell may read as a line or statement break separates
// statements in every dialect. PowerShell 5.1 on this machine ends a statement at a bare CR (bash and dash do not);
// VT, FF, NEL, U+2028 and U+2029 are treated the same way, whatever a shell does with them (see the report).
// These tests call the lexer and the shell rules directly, below the invisible-character rule of risk.ts.
import { describe, expect, it } from "vitest";
import { lex } from "../src/risk/lexer.js";
import { classifyShellCommand } from "../src/risk/shell.js";
import { pathEnv } from "../src/risk/paths.js";

const C = (n: number) => String.fromCharCode(n);
const SEPARATORS: [string, string][] = [
  ["CR", C(13)],
  ["CRLF", C(13) + C(10)],
  ["VT", C(11)],
  ["FF", C(12)],
  ["NEL", C(0x85)],
  ["LS", C(0x2028)],
  ["PS", C(0x2029)],
];

describe("statement separators beyond LF", () => {
  for (const [name, sep] of SEPARATORS) {
    it(`${name} ends a statement in every dialect`, () => {
      for (const mode of ["bash", "pwsh", "cmd"] as const) {
        const segs = lex("echo hi" + sep + "echo two", mode).filter((s) => s.words.length > 0);
        expect(segs.map((s) => s.words.map((w) => w.text)), `${name} ${mode}`).toEqual([["echo", "hi"], ["echo", "two"]]);
      }
      const ps = classifyShellCommand("Write-Output hi" + sep + "Remove-Item -Recurse -Force C:/x", "C:\\proj", "pwsh", {}, pathEnv("win32"));
      expect(ps, name).toEqual({ risk: "destructive", reason: "Deletes files and subfolders" });
      const sh = classifyShellCommand("echo hi" + sep + "rm -rf x", "/proj", "bash", {}, pathEnv("linux"));
      expect(sh, name).toEqual({ risk: "destructive", reason: "Deletes files and subfolders" });
      const cmd = classifyShellCommand("echo hi" + sep + "del /s /q x", "C:\\proj", "cmd", {}, pathEnv("win32"));
      expect(cmd.risk, name).toBe("destructive");
    });
  }

  it("a separator inside quotes stays part of the word", () => {
    const segs = lex("echo 'a" + C(13) + "b'", "bash");
    expect(segs.filter((s) => s.words.length > 0).map((s) => s.words.map((w) => w.text))).toEqual([["echo", "a\nb"]]);
  });
});

// PowerShell 5.1 on this machine reads U+2018..U+201E as quotes (closed by an ASCII quote as well) and U+2013,
// U+2014, U+2015 as parameter dashes; the lexer does not. Any of them in text read as PowerShell is destructive.
describe("PowerShell typographic quotes and dashes", () => {
  it("are destructive when read as PowerShell, and do not matter to bash", () => {
    const Q = String.fromCharCode(39);
    const cases = [
      "Write-Output " + C(0x2018) + "a" + Q + "; Remove-Item -Recurse C:/x; " + Q,
      "Copy-Item " + C(0x2013) + "Destination ../x a",
    ];
    for (const n of [0x2013, 0x2014, 0x2015, 0x2018, 0x2019, 0x201a, 0x201b, 0x201c, 0x201d, 0x201e]) cases.push("Write-Output " + C(n) + "x");
    for (const c of cases) {
      expect(classifyShellCommand(c, "C:\\proj", "pwsh", {}, pathEnv("win32")), JSON.stringify(c)).toEqual({ risk: "destructive", reason: "Complex shell syntax" });
    }
    expect(classifyShellCommand("git commit -m " + C(0x201e) + "Fix" + C(0x201c), "/proj", "bash", {}, pathEnv("linux")).risk).toBe("safe");
  });
});

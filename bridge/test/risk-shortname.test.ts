// Fix round 1 of task P4 (second review): Windows name forms that reach a protected folder without spelling it.
// No file-system call decides them: an 8.3 short name always counts as protected; case, trailing dots and spaces
// and alternate data streams are folded before the comparison.
import { describe, expect, it } from "vitest";
import { classifyRisk } from "../src/risk.js";

const HOME = "C:/Users/a";
const PROTECTED = { risk: "destructive", reason: "Changes agent or Git settings" };

const forms = [
  "CLAUDE~1/settings.json",
  "LOTSE~1/halt",
  ".lotse/HALT~1",
  ".claude/SETTIN~1.JSO",
  "src/FOO~12.TS",
  ".CLAUDE/settings.json",
  ".Lotse/token",
  ".claude./settings.json",
  ".lotse ./halt",
  ".lotse. . /halt",
  ".lotse::$INDEX_ALLOCATION/halt",
  ".lotse:x/halt",
  ".codex/hooks.json:evil",
  ".gemini./settings.json:stream:$DATA",
];

describe("short names, case, trailing dots and spaces, alternate data streams", () => {
  for (const p of forms) {
    it(p, () => {
      const win = p.replace(/\//g, "\\");
      for (const tool of ["Write", "Edit"]) {
        expect(classifyRisk(tool, { file_path: `${HOME}/${p}` }, HOME, "claude", "win32"), `${tool} absolute`).toEqual(PROTECTED);
        expect(classifyRisk(tool, { file_path: p }, HOME, "claude", "win32"), `${tool} relative`).toEqual(PROTECTED);
      }
      // bash dialect (Claude's Bash on Windows; Codex reads both)
      expect(classifyRisk("Bash", { command: `echo x > '${p}'` }, HOME, "claude", "win32"), "bash redirect").toEqual(PROTECTED);
      expect(classifyRisk("Bash", { command: `cp a.txt '${p}'` }, HOME, "codex", "win32"), "codex cp").toEqual(PROTECTED);
      // PowerShell dialect
      expect(classifyRisk("PowerShell", { command: `'x' > '${win}'` }, HOME, "claude", "win32"), "pwsh redirect").toEqual(PROTECTED);
      expect(classifyRisk("PowerShell", { command: `Copy-Item a.txt '${win}'` }, HOME, "claude", "win32").risk, "pwsh copy").toBe("destructive");
      expect(classifyRisk("PowerShell", { command: `Remove-Item '${win}'` }, HOME, "claude", "win32").risk, "pwsh delete").toBe("destructive");
    });
  }
  it("ordinary names with a tilde elsewhere stay as they were", () => {
    for (const p of ["src/a~b.ts", "notes~/x.md", "src/~1x.ts"]) {
      expect(classifyRisk("Write", { file_path: `${HOME}/proj/${p}` }, `${HOME}/proj`, "claude", "win32"), p).toEqual({ risk: "safe", reason: "Safe" });
    }
  });
});

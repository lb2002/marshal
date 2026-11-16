import { describe, expect, it } from "vitest";
import { classifyRisk } from "../src/risk.js";
import { adaptHook } from "../src/adapters.js";
import { ruleExplanation } from "../src/explain.js";
import { buildPermRequest } from "../src/broker.js";

const CWD = "C:\\work\\proj";
const PROBES = [
  "Clear-Content ..\\x.txt",
  "Set-Content -Path ..\\x.txt -Value hi",
  "Add-Content C:\\other\\x.txt hi",
  "Out-File -FilePath ..\\x.txt",
  "Copy-Item a.txt ..\\x.txt",
  "Move-Item a.txt C:\\other\\x.txt",
  "New-Item -Path ..\\x.txt",
  "Rename-Item C:\\other\\x.txt y.txt",
  "echo hi > ..\\x.txt",
];

describe("I1: on win32 a shell command from Muse, Codex or Gemini gets the worst of Bash and PowerShell rules", () => {
  it.each(PROBES)("%s is destructive from Codex Bash and Muse Bash", (command) => {
    for (const source of ["codex", "muse", "gemini"] as const) {
      expect(classifyRisk("Bash", { command }, CWD, source, "win32").risk, source).toBe("destructive");
    }
    expect(classifyRisk("exec_command", { command }, CWD, "codex", "win32").risk).toBe("destructive");
  });
  it("Muse's PowerShell (its shell tool on Windows) also gets the Bash rules", () => {
    expect(classifyRisk("PowerShell", { command: "rm -rf ../x" }, CWD, "muse", "win32").risk).toBe("destructive");
  });
  it("ordinary commands stay safe", () => {
    for (const command of ["git status", "npm test", "ls", "Get-ChildItem src"]) {
      expect(classifyRisk("Bash", { command }, CWD, "codex", "win32"), command).toEqual({ risk: "safe", reason: "Safe" });
    }
  });
  it("Claude keeps its own dialect, and other platforms keep the Bash rules", () => {
    expect(classifyRisk("Bash", { command: "Set-Content -Path ..\\x.txt -Value hi" }, CWD, "claude", "win32").risk).toBe("safe");
    // In Bash `..\x.txt` is a plain file name in the project; only the PowerShell rules see a parent folder.
    expect(classifyRisk("Bash", { command: "echo hi > ..\\x.txt" }, "/work/proj", "codex", "linux").risk).toBe("safe");
    expect(classifyRisk("Bash", { command: "echo hi > ..\\x.txt" }, "/work/proj", "codex", "win32").risk).toBe("destructive");
  });
});

describe("M8: unknown tools from Muse and Codex", () => {
  it("an unknown tool with no command or path field is unclear (destructive)", () => {
    for (const source of ["codex", "muse"] as const) {
      expect(classifyRisk("mcp__shop__deploy", { query: "x" }, CWD, source), source).toEqual({ risk: "destructive", reason: "Unclear tool" });
      expect(classifyRisk("view_image", {}, CWD, source).risk, source).toBe("destructive");
    }
  });
  it("with a command or a path field the normal rules apply; known tools and Claude are unchanged", () => {
    expect(classifyRisk("mcp__shop__run", { command: "git status" }, CWD, "codex", "win32").risk).toBe("safe");
    expect(classifyRisk("mcp__shop__open", { path: "src/a.ts" }, CWD, "codex").risk).toBe("safe");
    expect(classifyRisk("Read", { file_path: "src/a.ts" }, CWD, "codex").risk).toBe("safe");
    expect(classifyRisk("WebFetch", { url: "https://x" }, CWD, "muse").risk).toBe("network");
    expect(classifyRisk("mcp__shop__deploy", { query: "x" }, CWD, "claude").risk).toBe("safe");
    expect(classifyRisk("mcp__shop__deploy", { query: "x" }, CWD).risk).toBe("safe");
  });
  it("a safe Codex apply_patch is not hit by the unknown-tool rule", () => {
    const patch = "*** Begin Patch\n*** Update File: src/a.ts\n@@\n-a\n+b\n*** End Patch\n";
    expect(classifyRisk("apply_patch", { command: patch }, CWD, "codex").risk).toBe("safe");
  });
  it("the new reason has its own rule text", () => {
    const req = buildPermRequest({ id: "x", sid: "s", tool: "view_image", input: {}, risk: classifyRisk("view_image", {}, CWD, "codex"), createdAt: 0, holdMs: 1 });
    expect(ruleExplanation(req).text).toMatch(/tool/);
    expect(ruleExplanation(req).text).not.toBe("Claude wants to run something that can destroy data");
  });
});

describe("M8 + security review: Codex exec_command runs `cmd`, so `cmd` is what is classified and shown", () => {
  const perm = (tool_input: Record<string, unknown>, tool_name = "exec_command") =>
    adaptHook("codex", { session_id: "c", hook_event_name: "PermissionRequest", cwd: CWD, tool_name, tool_input }, "win32")!;
  const risk = (r: ReturnType<typeof perm>) => classifyRisk(String(r.body.tool_name), r.body.tool_input as Record<string, unknown>, CWD, "codex", "win32");
  const shown = (r: ReturnType<typeof perm>) =>
    buildPermRequest({ id: "x", sid: "c", tool: String(r.body.tool_name), input: r.body.tool_input as Record<string, unknown>, risk: risk(r), createdAt: 0, holdMs: 1 }).command;

  it("cmd only: classified and shown as the command (string or argv)", () => {
    expect(risk(perm({ cmd: "git status" }))).toEqual({ risk: "safe", reason: "Safe" });
    expect(shown(perm({ cmd: "git status" }))).toBe("git status");
    const argv = perm({ cmd: ["bash", "-lc", "rm -rf ../x"] });
    expect(risk(argv).risk).toBe("destructive");
    expect(shown(argv)).toBe("bash -lc 'rm -rf ../x'"); // each element one word (argv.ts), never joined
  });
  it("cmd and command equal: classified as that command", () => {
    expect(risk(perm({ cmd: "git status", command: "git status" })).risk).toBe("safe");
    expect(risk(perm({ cmd: ["ls"], command: "ls" })).risk).toBe("safe");
  });
  it("cmd and command differ: unclear (destructive), and the card shows cmd, the command that runs", () => {
    const r = perm({ cmd: "rm -rf ../x", command: "git status" });
    expect(risk(r)).toEqual({ risk: "destructive", reason: "Unclear tool" });
    expect(shown(r)).toBe("rm -rf ../x");
    // a harmless cmd does not help: the conflict alone is unclear
    expect(risk(perm({ cmd: "git status", command: "ls" }))).toEqual({ risk: "destructive", reason: "Unclear tool" });
  });
  it("command without cmd on exec_command is unclear (destructive), also without the adapter", () => {
    expect(risk(perm({ command: "git status" }))).toEqual({ risk: "destructive", reason: "Unclear tool" });
    expect(classifyRisk("exec_command", { command: "git status" }, CWD, "codex", "win32")).toEqual({ risk: "destructive", reason: "Unclear tool" });
    expect(classifyRisk("exec_command", { cmd: "git status", command: "ls" }, CWD, "codex", "win32")).toEqual({ risk: "destructive", reason: "Unclear tool" });
  });
  it("the same rule for Gemini's path alias: file_path is what runs, a different absolute_path is unclear", () => {
    const g = (tool_input: Record<string, unknown>) =>
      adaptHook("gemini", { session_id: "g", hook_event_name: "BeforeTool", cwd: CWD, tool_name: "write_file", tool_input }, "win32")!;
    const gr = (r: ReturnType<typeof g>) => classifyRisk(String(r.body.tool_name), r.body.tool_input as Record<string, unknown>, CWD, "gemini", "win32");
    expect((g({ absolute_path: "C:\\work\\proj\\a.ts" }).body.tool_input as Record<string, unknown>).file_path).toBe("C:\\work\\proj\\a.ts");
    expect(gr(g({ file_path: "C:\\work\\proj\\a.ts", absolute_path: "C:\\work\\proj\\a.ts" })).risk).toBe("safe");
    const conflict = g({ file_path: "C:\\work\\proj\\a.ts", absolute_path: "C:\\Windows\\x.dll" });
    expect(conflict.route).toBe("/perm");
    expect(gr(conflict)).toEqual({ risk: "destructive", reason: "Unclear tool" });
  });
});

describe("PowerShell write aliases (sc, mi, Tee-Object, tee and friends)", () => {
  const OUTSIDE = [
    "sc ..\\x.txt hi",
    "sc -Path C:\\other\\x.txt -Value hi",
    "mi a.txt ..\\x.txt",
    "Tee-Object -FilePath ..\\x.txt",
    "Get-Date | Tee-Object ..\\x.txt",
    "Get-Date | tee ..\\x.txt",
    "cpi a.txt ..\\x.txt",
    "ac ..\\x.txt hi",
    "clc ..\\x.txt",
    "rni C:\\other\\x.txt y.txt",
  ];
  it.each(OUTSIDE)("%s writes outside the project from Codex, Muse and Gemini on win32", (command) => {
    for (const source of ["codex", "muse", "gemini"] as const) {
      expect(classifyRisk("Bash", { command }, CWD, source, "win32"), source).toEqual({ risk: "destructive", reason: "Writes outside the project" });
    }
    expect(classifyRisk("PowerShell", { command }, CWD, "claude", "win32").risk).toBe("destructive");
  });
  it("inside the project they stay safe, and sc delete is still the service rule", () => {
    for (const command of ["sc notes.txt hi", "mi a.txt b.txt", "Get-Date | Tee-Object log.txt", "cpi a.txt b.txt"]) {
      expect(classifyRisk("PowerShell", { command }, CWD, "claude", "win32").risk, command).toBe("safe");
    }
    expect(classifyRisk("Bash", { command: "sc delete MyService" }, CWD, "codex", "win32")).toEqual({ risk: "destructive", reason: "Deletes a service" });
  });
});

describe("more PowerShell path writers (review B3-1 M4)", () => {
  const OUTSIDE = [
    "Get-Process | Export-Csv -Path ..\\x.csv",
    "Get-Process | Export-Csv ..\\x.csv",
    "Get-Date | Export-Clixml -Path C:\\other\\x.xml",
    "Get-Date | Out-File ..\\x.txt",
    "Set-ItemProperty -Path ..\\x.txt -Name IsReadOnly -Value $true",
    "sp ..\\x.txt IsReadOnly $true",
    "New-Item ..\\x.txt",
    "ni ..\\x.txt",
    "Set-Item ..\\x.txt hi",
    "si ..\\x.txt hi",
  ];
  it.each(OUTSIDE)("%s writes outside the project (Codex, Muse, Gemini on win32; Claude PowerShell)", (command) => {
    for (const source of ["codex", "muse", "gemini"] as const) {
      expect(classifyRisk("Bash", { command }, CWD, source, "win32").risk, source).toBe("destructive");
    }
    expect(classifyRisk("PowerShell", { command }, CWD, "claude", "win32").risk).toBe("destructive");
  });
  it("inside the project they stay safe", () => {
    for (const command of ["Get-Process | Export-Csv out.csv", "Get-Date | Export-Clixml d.xml", "Set-ItemProperty -Path a.txt -Name IsReadOnly -Value 1", "si notes.txt hi"]) {
      expect(classifyRisk("PowerShell", { command }, CWD, "claude", "win32").risk, command).toBe("safe");
    }
  });
});

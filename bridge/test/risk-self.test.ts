// Fix round 1 of task P4, finding 1: Marshal's own CLI is two-hand, and the agents' and Marshal's settings
// folders are settings changes, so an agent cannot switch Marshal off with a "safe" approval.
import { describe, expect, it } from "vitest";
import { classifyRisk } from "../src/risk.js";
import { classifyShellArgv } from "../src/risk/shell.js";
import { ruleExplanation } from "../src/explain.js";

const SELF = { risk: "destructive", reason: "Changes or stops Marshal itself" };
const CWD = "C:/proj";
const BRIDGE = "C:/Users/a/Lotse/bridge";

/** Every source that can send it, in both dialects where the source runs them on Windows. */
function each(command: string, cwd = CWD): { label: string; risk: unknown }[] {
  const out: { label: string; risk: unknown }[] = [];
  for (const source of ["claude", "codex", "gemini", "muse"] as const) {
    out.push({ label: `${source} Bash win32`, risk: classifyRisk("Bash", { command }, cwd, source, "win32") });
    out.push({ label: `${source} Bash linux`, risk: classifyRisk("Bash", { command }, cwd.replace(/^C:/, ""), source, "linux") });
  }
  out.push({ label: "claude PowerShell", risk: classifyRisk("PowerShell", { command }, cwd, "claude", "win32") });
  return out;
}

describe("Marshal's own CLI is destructive (two hands)", () => {
  const changing = [
    "lotse disconnect",
    "lotse disconnect --yes",
    "lotse disconnect --all",
    "lotse disconnect --all --tool muse",
    "lotse connect",
    "lotse connect --yes",
    "lotse connect --all --yes",
    "lotse install-hooks --project .",
    "lotse uninstall-hooks --project .",
    "lotse start",
    "lotse stop",
    "lotse pair",
    "lotse frobnicate",
    "lotse.cmd disconnect",
    "lotse.ps1 disconnect",
    "lotse.exe disconnect",
    "'lotse' disconnect",
    '"lotse" "disconnect"',
    "C:/tools/lotse.cmd disconnect --yes",
    '"C:/Program Files/lotse/lotse.cmd" disconnect',
    "/usr/local/bin/lotse disconnect",
    "./node_modules/.bin/lotse disconnect",
    "npx lotse disconnect",
    "npx --yes lotse disconnect --all",
    "npx lotse@latest connect --yes",
    "npm exec -- lotse disconnect",
    "pnpm dlx lotse disconnect",
    "yarn lotse disconnect",
    "bunx lotse disconnect",
    `node ${BRIDGE}/dist/cli.js disconnect`,
    `node "${BRIDGE}/dist/cli.js" disconnect --yes`,
    `node.exe ${BRIDGE}/dist/cli.js connect --all`,
    `"C:/Program Files/nodejs/node.exe" "${BRIDGE}/dist/cli.js" uninstall-hooks --project .`,
    `node --no-warnings ${BRIDGE}/dist/cli.js disconnect`,
    `node ${BRIDGE}/dist/cli disconnect`,
    "node C:/somewhere/else/dist/cli.js disconnect",
    "node C:/somewhere/else/dist/cli.js install-hooks --project .",
    "node C:/Users/a/marshal/cli.js start",
    `node ${BRIDGE}/dist/cli.js hook --source claude`,
    `node ${BRIDGE}/dist/cli.js pack x -o y`,
    "bash -c 'lotse disconnect'",
    "echo ok && lotse disconnect",
    "sudo lotse disconnect",
    "env FOO=1 lotse disconnect",
    "cmd /c lotse disconnect",
    "powershell -Command lotse disconnect",
  ];
  for (const command of changing) {
    it(command, () => {
      for (const { label, risk } of each(command)) expect(risk, label).toEqual(SELF);
    });
  }
  it("a relative cli.js counts from inside the bridge folder, and after cd into it", () => {
    expect(classifyRisk("Bash", { command: "node dist/cli.js disconnect" }, BRIDGE, "claude", "win32")).toEqual(SELF);
    expect(classifyRisk("Bash", { command: "node dist/cli.js start" }, BRIDGE, "claude", "win32")).toEqual(SELF);
    expect(classifyRisk("Bash", { command: `cd ${BRIDGE} && node dist/cli.js start` }, CWD, "claude", "win32").risk).toBe("destructive");
  });
  it("the PowerShell call operator with a quoted, path-qualified script", () => {
    expect(classifyRisk("PowerShell", { command: '& "C:\\tools\\lotse.ps1" disconnect' }, CWD, "claude", "win32")).toEqual(SELF);
    expect(classifyRisk("PowerShell", { command: `& node "${BRIDGE}\\dist\\cli.js" disconnect` }, CWD, "claude", "win32")).toEqual(SELF);
  });
  it("argument arrays (Codex)", () => {
    expect(classifyRisk("Bash", { command: ["lotse", "disconnect"] }, CWD, "codex", "win32")).toEqual(SELF);
    expect(classifyRisk("Bash", { command: ["node", `${BRIDGE}/dist/cli.js`, "connect", "--yes"] }, CWD, "codex", "linux")).toEqual(SELF);
    expect(classifyShellArgv(["bash", "-lc", "lotse disconnect"], CWD, "bash").risk).toBe("destructive");
  });
  it("exactly one literal read-only word, reached plainly, stays as it was", () => {
    for (const command of ["lotse --help", "lotse -h", "lotse help", "lotse --version", "lotse status", "npx lotse --help", `node ${BRIDGE}/dist/cli.js --version`]) {
      for (const { label, risk } of each(command)) expect(risk, `${command} ${label}`).not.toEqual(SELF);
    }
  });
  it("every other call counts: bare, arguments added later, from a file, splatted, piped or redirected stdin", () => {
    const bash = [
      "lotse",
      `node ${BRIDGE}/dist/cli.js`,
      "npx lotse",
      "echo disconnect | xargs lotse",
      "echo --help | xargs lotse --help",
      "xargs -a args.txt lotse",
      "xargs lotse --help < args.txt",
      "parallel lotse ::: disconnect",
      "find . -name x -exec lotse {} \\;",
      "find . -exec lotse --help \\;",
      "lotse $(cat sub.txt)",
      "lotse `cat sub.txt`",
      "lotse $SUB",
      "lotse @args.txt",
      "lotse --help disconnect",
      "lotse status --yes",
      "lotse --help < /dev/null",
      "echo y | lotse --help",
      "lotse --help <<< y",
      "lotse '--help'x",
      "lotse *",
    ];
    for (const command of bash) {
      expect(classifyRisk("Bash", { command }, "/proj", "claude", "linux"), command).toEqual(SELF);
      expect(classifyRisk("Bash", { command }, CWD, "codex", "win32"), `${command} codex`).toEqual(SELF);
    }
    const pwsh = ["lotse @a", "lotse @args", "'disconnect' | ForEach-Object { lotse $_ }", "'disconnect' | % { lotse $_ }", "$s = 'disconnect'; lotse $s", "Get-Content args.txt | lotse"];
    for (const command of pwsh) expect(classifyRisk("PowerShell", { command }, CWD, "claude", "win32").risk, command).toBe("destructive");
  });
  it("other programs named like it stay as they were", () => {
    for (const command of ["node app/cli.js start", "node app/cli.js build", "lotsen disconnect", "echo lotse disconnect"]) {
      expect(classifyRisk("Bash", { command }, CWD, "claude", "linux"), command).not.toEqual(SELF);
    }
  });
  it("the card explains it", () => {
    const e = ruleExplanation({ id: "p1", sid: "s", tool: "Bash", command: "lotse disconnect", risk: SELF } as never);
    expect(e.text.startsWith("Claude wants to change or stop Marshal, which watches it")).toBe(true);
  });
});

describe("the agents' settings folders and ~/.lotse are settings changes, also from the home folder", () => {
  const HOME = "C:/Users/a";
  const paths = [".lotse/halt", ".lotse/token", ".claude/settings.json", ".codex/hooks.json", ".gemini/settings.json", ".muse/hooks.json", ".config/muse/settings.json", ".lotse/muse-plugin/.muse-plugin/plugin.json", ".config/opencode/plugins/marshal/index.js", ".config/opencode/opencode.json", ".opencode/plugins/x/index.js", "proj/opencode.json", "proj/opencode.jsonc"];
  for (const p of paths) {
    it(p, () => {
      for (const tool of ["Write", "Edit"]) {
        expect(classifyRisk(tool, { file_path: `${HOME}/${p}` }, HOME, "claude", "win32"), `${tool} from home`).toEqual({ risk: "destructive", reason: "Changes agent or Git settings" });
        expect(classifyRisk(tool, { file_path: `${HOME}/${p}` }, CWD, "claude", "win32").risk, `${tool} from a project`).toBe("destructive");
      }
      expect(classifyRisk("Bash", { command: `echo x > ${p}` }, HOME, "claude", "win32").risk).toBe("destructive");
      expect(classifyRisk("Bash", { command: `rm ${HOME}/${p}` }, HOME, "claude", "win32").risk).toBe("destructive");
      expect(classifyRisk("PowerShell", { command: `Remove-Item ${HOME}/${p}` }, HOME, "claude", "win32").risk).toBe("destructive");
    });
  }
});

describe("OpenCode's project config is a settings file inside the project too", () => {
  for (const p of ["opencode.json", "opencode.jsonc", "sub/opencode.json", "OPENCODE.JSON", ".config/opencode/plugins/a/index.js"]) {
    it(p, () => {
      for (const tool of ["Write", "Edit"]) expect(classifyRisk(tool, { file_path: `${CWD}/${p}` }, CWD, "opencode", "win32").risk).toBe("destructive");
      expect(classifyRisk("Bash", { command: `echo x > ${p}` }, CWD, "claude", "linux").risk).toBe("destructive");
    });
  }
  it("a file that only contains the name is not", () => {
    expect(classifyRisk("Write", { file_path: `${CWD}/opencode.json.md` }, CWD, "claude", "win32").risk).not.toBe("destructive");
  });
});

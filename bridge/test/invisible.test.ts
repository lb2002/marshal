// P3 round 2, item 2: invisible and control characters count as destructive and are shown as visible escapes,
// escaped before the display is cut.
import { describe, expect, it } from "vitest";
import { renderArgv, visibleText } from "../src/argv.js";
import { PermissionBroker } from "../src/broker.js";
import { classifyRisk } from "../src/risk.js";
import { lex } from "../src/risk/lexer.js";
import { SessionStore } from "../src/state.js";

const C = (n: number) => String.fromCodePoint(n);
const BS = "\\";
const INVISIBLE = { risk: "destructive", reason: "Command contains invisible or control characters" };
const RLO = C(0x202e);
const CWD = process.cwd();

// Every character of the ruling, plus the blanks PowerShell 5.1 reads as word breaks (tested) and the lexer does not.
const BAD = [
  0x00, 0x01, 0x07, 0x0b, 0x0c, 0x0d, 0x1b, 0x7f, 0x85, 0x9b, // controls other than \n and \t
  0x061c, 0x200e, 0x200f, 0x202a, 0x202b, 0x202c, 0x202d, 0x202e, 0x2066, 0x2067, 0x2068, 0x2069, // bidi
  0x200b, 0x200c, 0x200d, 0x2060, 0xfeff, 0x00ad, 0x180e, // zero-width and invisible format
  0xe0000, 0xe0041, 0xe007f, // tag block
  0x00a0, 0x2000, 0x200a, 0x2028, 0x2029, 0x3000, // blanks only PowerShell reads as blanks
];

describe("invisible and control characters are destructive", () => {
  it("in a command string, in every dialect and for every source", () => {
    for (const n of BAD) {
      const command = "ls" + C(n) + "x";
      for (const [tool, source, platform] of [["Bash", "claude", "linux"], ["PowerShell", "claude", "win32"], ["Bash", "codex", "win32"]] as const) {
        expect(classifyRisk(tool, { command }, CWD, source, platform), `U+${n.toString(16)} ${tool} ${source}`).toEqual(INVISIBLE);
      }
    }
  });

  it("CR before a destructive PowerShell statement, and a right-to-left override in a file name", () => {
    expect(classifyRisk("PowerShell", { command: "Write-Output hi" + C(13) + "Remove-Item -Recurse -Force C:/x" }, "C:" + BS + "proj", "claude", "win32").risk).toBe("destructive");
    expect(classifyRisk("Bash", { command: "cat notes" + RLO + "txt.sh" }, "/proj", undefined, "linux")).toEqual(INVISIBLE);
  });

  it("in an argument array element, a path field, a URL and the tool name", () => {
    expect(classifyRisk("exec_command", { cmd: ["cat", "a" + RLO + "b"] }, "/proj", "codex", "linux")).toEqual(INVISIBLE);
    expect(classifyRisk("mcp__x__run", { command: ["ls", "a" + C(0x200b)] }, "/proj", undefined, "linux")).toEqual(INVISIBLE);
    for (const field of ["file_path", "notebook_path", "path", "destination", "dest", "target", "output", "to", "outputPath"]) {
      expect(classifyRisk("Write", { file_path: "a.ts", [field]: "src/a" + RLO + "st.ts" }, "/proj", undefined, "linux"), field).toEqual(INVISIBLE);
    }
    expect(classifyRisk("WebFetch", { url: "https://example.com/" + C(0x200d) }, "/proj", undefined, "linux")).toEqual(INVISIBLE);
    expect(classifyRisk("Read" + C(0x200b), { file_path: "a" }, "/proj", undefined, "linux")).toEqual(INVISIBLE);
  });

  it("a plain newline and tab in a command are not affected", () => {
    expect(classifyRisk("Bash", { command: "ls\n\tpwd" }, "/proj", undefined, "linux").risk).toBe("safe");
    expect(classifyRisk("Bash", { command: "echo 'café'" }, "/proj", undefined, "linux").risk).toBe("safe");
  });
});

describe("display: visible escapes before the cut", () => {
  it("visibleText escapes controls, bidi, zero-width, tags and odd blanks; keeps ordinary text", () => {
    expect(visibleText("a\rb\x1bc\nd\te")).toBe("a" + BS + "rb" + BS + "ec" + BS + "nd" + BS + "te");
    expect(visibleText("x" + RLO + "y" + C(0x200b) + C(0xe0041) + C(0xa0) + C(0x85))).toBe("xy".replace("y", BS + "u{202e}y") + BS + "u{200b}" + BS + "u{e0041}" + BS + "u{a0}" + BS + "u{85}");
    expect(visibleText("C:" + BS + "Users" + BS + "café ä 中文")).toBe("C:" + BS + "Users" + BS + "café ä 中文");
  });

  const mk = () => new PermissionBroker({ holdMs: 90_000, hasClient: () => true });

  it("a command with a right-to-left override is destructive and shown with the escape", () => {
    const r = mk().open("s", "Bash", { command: "cat notes" + RLO + "txt.sh" }, CWD).request;
    expect(r.risk).toEqual(INVISIBLE);
    expect(r.command).toBe("cat notes" + BS + "u{202e}txt.sh");
    expect(r.command).not.toContain(RLO);
  });

  it("the same for a path field, the URL and the tool name", () => {
    const r = mk().open("s", "Write" + C(0x200b), { file_path: "src/a" + RLO + "st.ts", url: "https://x/" + C(13) }, CWD).request;
    expect(r.risk.risk).toBe("destructive");
    expect(r.path).toBe("src/a" + BS + "u{202e}st.ts");
    expect(r.paths).toEqual(["src/a" + BS + "u{202e}st.ts"]);
    expect(r.url).toBe("https://x/" + BS + "r");
    expect(r.tool).toBe("Write" + BS + "u{200b}");
    expect(JSON.stringify(r)).not.toMatch(/[\u{202e}\u{200b}\r]/u);
  });

  it("an argument array element with such a character still renders as one word that reads back", () => {
    const argv = ["cat", "a" + RLO + "b"];
    const r = mk().open("s", "exec_command", { cmd: argv, command: argv }, CWD, "codex").request;
    expect(r.command).toBe(renderArgv(argv));
    expect(r.command).toBe("cat $'a" + BS + "u202eb'");
    const words = lex(r.command ?? "", "bash")[0].words.map((w) => w.text);
    expect(words).toEqual(["cat", "a" + BS + "u202eb"]);
  });

  it("caps and the truncation flag apply after escaping", () => {
    // 100 RLO characters: 100 raw, 800 escaped -> cut and two hands.
    const b = mk();
    const { request } = b.open("s", "Bash", { command: "ls " + RLO.repeat(100) }, CWD);
    expect(request.commandLength).toBe(3 + 100 * 8);
    expect(request.truncated).toBe(true);
    expect(request.command).toHaveLength(300);
    const p = b.open("s", "Read", { file_path: "a" + C(0x200b).repeat(60) }, CWD).request;
    expect(p.path).toHaveLength(300);
    expect(p.truncated).toBe(true);
    const t = b.open("s", "mcp__" + C(0x200b).repeat(5), {}, CWD).request;
    expect(t.truncated).toBe(true);
    expect(t.tool).toHaveLength(40);
    expect(b.decide({ id: request.id, behavior: "allow", confirm: "pinch-hold" })).toEqual({ ok: false, error: "needs-two-hand" });
  });

  it("a 10 MB command with such characters is still answered at once", () => {
    const t = performance.now();
    const r = mk().open("s", "Bash", { command: (RLO + "a").repeat(5 * 1024 * 1024) }, CWD).request;
    expect(performance.now() - t).toBeLessThan(50);
    expect(r.truncated).toBe(true);
    expect(r.command).not.toContain(RLO);
  });
});

describe("other agent text shown on the headset drops these characters", () => {
  it("the agent's last action (tool name, target, summary)", () => {
    const reg = new SessionStore();
    const ev = (o: Record<string, unknown>) => reg.apply({ sid: "s", source: "claude", t: 1, ...o } as never);
    ev({ kind: "tool_start", tool: "Read" + RLO, path: "src/a" + RLO + "b.ts" });
    expect(JSON.stringify(reg.all())).not.toMatch(/[\u{202e}]/u);
    ev({ kind: "stop", summary: "Fertig" + C(13) + RLO + "x" });
    expect(JSON.stringify(reg.all())).not.toMatch(/[\u{202e}\r]/u);
  });
});

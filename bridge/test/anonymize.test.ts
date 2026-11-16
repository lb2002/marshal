import { describe, expect, it } from "vitest";
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { anonymizeEvent, redactSecrets } from "../src/anonymize.js";
import { Recorder, readRecording } from "../src/recorder.js";
import type { AgentEvent } from "../src/events.js";
import { tempDir } from "./tmp.js";

const opts = { repoRoot: "C:\\Users\\linus\\proj", homeDir: "C:\\Users\\linus" };
const ev = (p: Partial<AgentEvent>): AgentEvent => ({ sid: "s", t: 0, kind: "tool_start", ...p });

describe("anonymizeEvent paths", () => {
  it("maps cwd, repo paths and home paths", () => {
    expect(anonymizeEvent(ev({ cwd: "C:\\Users\\linus\\proj" }), opts).cwd).toBe("<repo>");
    expect(anonymizeEvent(ev({ path: "C:\\Users\\linus\\proj\\src\\a.ts" }), opts).path).toBe("<repo>/src/a.ts");
    expect(anonymizeEvent(ev({ path: "c:/users/linus/proj/src/a.ts" }), opts).path).toBe("<repo>/src/a.ts");
    expect(anonymizeEvent(ev({ path: "C:\\Users\\linus\\.ssh\\config" }), opts).path).toBe("~/.ssh/config");
  });
  it("does not match sibling directories and keeps relative paths", () => {
    expect(anonymizeEvent(ev({ path: "C:\\Users\\linus\\proj2\\a.ts" }), opts).path).toBe("~/proj2/a.ts");
    expect(anonymizeEvent(ev({ path: "src/a.ts" }), opts).path).toBe("src/a.ts");
  });
  it("anonymises paths inside command and summary", () => {
    const e = anonymizeEvent(
      ev({
        command: "cat C:\\Users\\linus\\proj\\src\\a.ts c:/users/linus/notes.txt",
        summary: "edited C:/Users/linus/proj/x.ts",
      }),
      opts,
    );
    expect(e.command).toBe("cat <repo>/src/a.ts ~/notes.txt");
    expect(e.summary).toBe("edited <repo>/x.ts");
  });
  it("handles posix roots and returns a copy", () => {
    const o = { repoRoot: "/home/a/proj", homeDir: "/home/a" };
    const src = ev({ path: "/home/a/proj/src/a.ts", command: "ls /home/a/proj && ls /home/a/x" });
    const out = anonymizeEvent(src, o);
    expect(out.path).toBe("<repo>/src/a.ts");
    expect(out.command).toBe("ls <repo> && ls ~/x");
    expect(src.path).toBe("/home/a/proj/src/a.ts");
  });
});

describe("secret redaction", () => {
  const secrets = [
    "sk-ant-api03-abcDEF123_456",
    "ghp_" + "a1B2c3D4e5".repeat(3) + "a1B2c3",
    "AKIA" + "ABCDEFGH12345678",
    "xoxb-123456789-abcdefGHI",
    "someone@example.com",
    "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig",
  ];
  it.each(secrets)("redacts %s in command and summary", (s) => {
    const e = anonymizeEvent(ev({ command: `curl ${s} done`, summary: `saw ${s} here` }), opts);
    expect(e.command).toBe("curl [REDACTED] done");
    expect(e.summary).toBe("saw [REDACTED] here");
  });
  it("redacts headers, key=value, flags, PEM, long strings, and path secrets", () => {
    expect(redactSecrets('curl -H "Authorization: Basic dXNlcjpwYXNz" x')).toBe('curl -H "Authorization: [REDACTED]" x');
    expect(redactSecrets("x?password=hunter2&a=1")).toBe("x?password=[REDACTED]&a=1");
    expect(redactSecrets("API_TOKEN=abc run")).toBe("API_TOKEN=[REDACTED] run");
    expect(redactSecrets("cli --password 'p w' --token t1 -p keep")).toBe(
      "cli --password [REDACTED] --token [REDACTED] -p keep",
    );
    expect(redactSecrets("a -----BEGIN RSA PRIVATE KEY-----\nMIIB\n-----END RSA PRIVATE KEY----- b")).toBe("a [REDACTED] b");
    expect(redactSecrets("-----BEGIN PRIVATE KEY-----\ncut off")).toBe("[REDACTED]");
    expect(redactSecrets("hash=" + "0123456789abcdef".repeat(2))).toBe("hash=[REDACTED]");
    expect(anonymizeEvent(ev({ path: "/tmp/x?token=abc123" }), opts).path).toBe("<abs>/x?token=[REDACTED]");
  });
  it("leaves ordinary paths and text alone", () => {
    const t = "cd C:/Users/linus/Documents/MetaHackathon/Lotse && npm test";
    expect(redactSecrets(t)).toBe(t);
  });
  it("truncates command to 200 chars after redaction", () => {
    const e = anonymizeEvent(ev({ command: "echo " + "x ".repeat(300) }), opts);
    expect(e.command).toHaveLength(200);
    const long = anonymizeEvent(ev({ command: "sk-ant-" + "a".repeat(400) + " tail" }), opts);
    expect(long.command).toBe("[REDACTED] tail");
  });
  it("is linear on 100k-char adversarial input", () => {
    const inputs = [
      "a".repeat(100_000),
      "-----BEGIN PRIVATE KEY-----".repeat(4000),
      'password="'.repeat(10_000),
      "Bearer ".repeat(14_000),
      "a.".repeat(50_000),
      "authorization:".repeat(7000),
      "=".repeat(100_000),
      " ".repeat(100_000) + "Bearer",
      "token_".repeat(20_000),
      "secret_".repeat(15_000),
      "api_key".repeat(15_000),
      "AKIA".repeat(25_000) + " ",
      "eyJ".repeat(33_000) + " ",
      "sk-".repeat(33_000) + " ",
      "sk-ant-".repeat(14_000) + " x",
      "mysql ".repeat(16_000),
      "http://a:".repeat(11_000),
      "http://a:" + "b".repeat(100_000),
      "/home/".repeat(16_000),
      "C:\\Users\\".repeat(10_000),
      "/c/Users/".repeat(11_000),
      "/mnt/c/Users/".repeat(8_000),
      "\\\\?\\".repeat(25_000),
      '"token":"'.repeat(11_000),
      "-u ".repeat(33_000) + "curl",
      "--user ".repeat(14_000) + "curl",
      "Cookie:".repeat(14_000),
      "-----BEGIN X-----".repeat(5_800),
      "linus ".repeat(16_000),
      "LINUS~1 ".repeat(12_000),
      "a:" + "/".repeat(100_000),
      "TOKEN".repeat(20_000) + "=",
      "C:\\Users\\linus\\proj".repeat(5_000),
    ];
    const posix = { repoRoot: "/home/linus/proj", homeDir: "/home/linus" };
    for (const o of [opts, posix]) {
      for (const s of [...inputs, "/".repeat(100_000), "/home/linus/".repeat(8_000)]) {
        const t0 = performance.now();
        anonymizeEvent(ev({ command: s, summary: s, path: s }), o);
        expect(performance.now() - t0).toBeLessThan(200);
      }
    }
  });
});

describe("fix round 1", () => {
  const A = (p: Partial<AgentEvent>) => anonymizeEvent(ev(p), opts);

  it("keyword rule stays linear on underscore runs", () => {
    for (const s of ["token_".repeat(20_000), "secret_".repeat(20_000), "api_key".repeat(20_000)]) {
      const t0 = performance.now();
      redactSecrets(s);
      expect(performance.now() - t0).toBeLessThan(200);
    }
  });
  it("maps Git Bash and WSL drive forms", () => {
    expect(A({ path: "/c/Users/linus/proj/src/a.ts" }).path).toBe("<repo>/src/a.ts");
    expect(A({ path: "/mnt/c/Users/linus/notes.txt" }).path).toBe("~/notes.txt");
    expect(A({ command: "cat /c/Users/linus/proj/a.ts /mnt/c/users/linus/b" }).command).toBe("cat <repo>/a.ts ~/b");
    const gb = { repoRoot: "/c/Users/linus/proj", homeDir: "/c/Users/linus" };
    expect(anonymizeEvent(ev({ path: "C:\\Users\\linus\\proj\\a" }), gb).path).toBe("<repo>/a");
  });
  it("redacts JSON and quoted secrets", () => {
    expect(redactSecrets(`curl -d '{"password":"hunter2"}'`)).not.toContain("hunter2");
    expect(redactSecrets(`{"token": "abc def"}`)).not.toContain("abc");
    expect(redactSecrets(`'token'='abc'`)).not.toContain("abc");
    expect(redactSecrets(`x --data "{\\"secret\\":\\"zzz\\"}"`)).not.toContain("zzz");
    expect(redactSecrets(`password="unterminated`)).not.toContain("unterminated");
  });
  it("redacts curl users, URL credentials, cookies, mysql -p", () => {
    expect(redactSecrets("curl -u bob:s3cret https://x")).toBe("curl -u bob:[REDACTED] https://x");
    expect(redactSecrets("curl --user bob:s3cret x")).toBe("curl --user bob:[REDACTED] x");
    expect(redactSecrets("git clone https://bob:s3cret@host/r.git")).toBe("git clone https://bob:[REDACTED]@host/r.git");
    expect(redactSecrets('curl -H "Cookie: a=b; c=d" x')).toBe('curl -H "Cookie: [REDACTED]" x');
    expect(redactSecrets("Set-Cookie: sid=zzz")).toBe("Set-Cookie: [REDACTED]");
    expect(redactSecrets("mysql -u root -pSECRET db")).toBe("mysql -u root -p[REDACTED] db");
    expect(redactSecrets("git push -u origin:main")).toBe("git push -u origin:main");
  });
  it("redacts any armored block", () => {
    expect(redactSecrets("a -----BEGIN OPENSSH PRIVATE KEY-----\nx\n-----END OPENSSH PRIVATE KEY----- b")).toBe("a [REDACTED] b");
    expect(redactSecrets("a -----BEGIN PGP PRIVATE KEY BLOCK-----\nx\n-----END PGP PRIVATE KEY BLOCK----- b")).toBe("a [REDACTED] b");
    expect(redactSecrets("a -----BEGIN CERTIFICATE-----\nx")).toBe("a [REDACTED]");
  });
  it("scrubs the bare username, 8.3 form and other homes", () => {
    expect(A({ summary: "user linus did it as LINUS~1" }).summary).toBe("user <user> did it as <user>");
    expect(A({ command: "ls C:\\Users\\bob\\x /home/eve/y /Users/linus/z" }).command).toBe("ls ~other/x ~other/y ~/z");
    expect(A({ path: "\\\\wsl$\\Ubuntu\\home\\eve\\f" }).path).toBe("~other/f");
    expect(A({ cwd: "C:\\Users\\bob" }).cwd).toBe("~other");
    expect(A({ command: "echo linuxer linus-x" }).command).toBe("echo linuxer <user>-x");
  });
  it("strips the extended-length prefix", () => {
    expect(A({ path: "\\\\?\\C:\\Users\\linus\\x" }).path).toBe("~/x");
    expect(A({ command: "cat \\\\?\\C:\\Users\\linus\\proj\\a" }).command).toBe("cat <repo>/a");
  });
  it("redacts known-prefix tokens cut short at the end", () => {
    for (const p of ["sk-", "sk-ant-ab", "ghp_a1", "gho_x", "ghs_x", "github_pat_ab", "AKIAAB", "xoxb-12", "xoxp-1", "eyJhb"]) {
      expect(redactSecrets(`run ${p}`)).toBe("run [REDACTED]");
    }
    expect(redactSecrets("task-force")).toBe("task-force");
    expect(redactSecrets("echo ask-")).toBe("echo ask-");
  });
  it("keeps sibling repos out of <repo> in text and leaves repo subdirs alone", () => {
    expect(A({ command: "cd C:\\Users\\linus\\proj2 && ls" }).command).toBe("cd ~/proj2 && ls");
    expect(A({ command: "cat <repo>/home/bob/x" }).command).toBe("cat <repo>/home/bob/x");
  });
  it("redacts tool and notifyType, caps summary at 280, surrogate-safe", () => {
    expect(A({ tool: "mcp__tool x@y.com", notifyType: "token=abc" })).toMatchObject({
      tool: "mcp__tool [REDACTED]",
      notifyType: "token=[REDACTED]",
    });
    expect(A({ summary: "s".repeat(1000) }).summary).toHaveLength(280);
    expect(A({ command: "a".repeat(199) + "\u{1F600}" }).command).toBe("a".repeat(199));
    expect(A({ summary: "a".repeat(279) + "\u{1F600}" }).summary).toBe("a".repeat(279));
  });
  it("new keywords and prefixed names", () => {
    for (const k of ["private_key", "passphrase", "credentials", "auth", "MYTOKEN", "GITHUB_TOKEN", "DB_PASSWORD"]) {
      expect(redactSecrets(`${k}=abc1 z`)).toBe(`${k}=[REDACTED] z`);
    }
    expect(redactSecrets("--passwd x --pass y --auth z")).toBe("--passwd [REDACTED] --pass [REDACTED] --auth [REDACTED]");
    expect(redactSecrets("author=bob")).toBe("author=bob");
  });
});

describe("fix round 2", () => {
  const anon = (command: string) => anonymizeEvent(ev({ command }), opts).command as string;
  it("redacts keys with a long identifier suffix", () => {
    expect(anon("password_" + "a".repeat(200) + "=hunter2")).not.toContain("hunter2");
  });
  it("stays linear on adversarial repeats of the long-suffix key", () => {
    const t0 = performance.now();
    anonymizeEvent(ev({ summary: ("password_" + "a".repeat(300)).repeat(Math.ceil(100_000 / 309)).slice(0, 100_000) }), opts);
    expect(performance.now() - t0).toBeLessThan(500);
  });
  it("redacts nested JSON values after a keyword", () => {
    const a = anon('{"token":{"a":"b c"},"z":1}');
    expect(a).not.toContain("b c");
    expect(a).toContain('"z":1}');
    expect(anon('{"secret":["x y","z"]}')).not.toContain("x y");
    expect(anon('{"secret":{"a":"q r"')).not.toContain("q r");
  });
  it("bare values stop at JSON delimiters", () => {
    expect(anon('{"password":123,"b":2}')).toBe('{"password":[REDACTED],"b":2}');
    expect(anon('{"token":null}')).toBe('{"token":[REDACTED]}');
  });
  it("shell forms redact the whole value including commas", () => {
    for (const c of ["password=a,b", "--token a,b", "--password=a,b"]) {
      const a = anon(`run ${c} done`);
      expect(a).toContain("[REDACTED]");
      expect(a).not.toMatch(/[ab],|,b/);
    }
  });
  it("a bracket value runs on to the end of the bare value", () => {
    for (const c of ["password={a}hunter2", "--token [x]secret", "token=[x]secret&n=1"]) {
      const a = anon(`run ${c}`);
      expect(a).not.toMatch(/hunter2|secret/);
    }
    expect(anon("token=[x]secret&n=1")).toContain("&n=1");
    expect(anon('{"token":{"a":"b c"},"z":1}')).toContain('"z":1}');
  });
  it("single-quoted JSON-style keys also stop at delimiters", () => {
    expect(anon("{'token':null,'b':2}")).toBe("{'token':[REDACTED],'b':2}");
  });
  it("scrubs the username next to underscores only", () => {
    expect(anon("linus_1")).toBe("<user>_1");
    expect(anon("build_linus")).toBe("build_<user>");
    expect(anon("linuxer mylinus")).toBe("linuxer mylinus");
  });
});

describe("fix round 4", () => {
  const anon = (command: string) => anonymizeEvent(ev({ command }), opts).command as string;
  it("flag values go through the same bracket scan as keyword values", () => {
    for (const c of ['--token {"a":"b c"}', "--password [a b]secret"]) {
      expect(anon(`run ${c}`)).not.toMatch(/b c|secret|b\]/);
    }
    expect(redactSecrets('run --token {"a":"b"} next')).toBe("run --token [REDACTED] next");
  });
  it("bracket scan skips single-quoted strings and fails closed on escaped quotes", () => {
    for (const c of ["token={'a':'b} secret'}", "{'token':{'a':'b} x'}}", '\\"token\\":{\\"a\\":\\"b} secret\\"}}']) {
      expect(anon(c)).not.toMatch(/secret| x/);
    }
    expect(anon('{"token":{"a":"b c"},"z":1}')).toContain('"z":1}');
  });
  it("fail-closed redaction (escaped or unclosed quote) runs to the end of the text", () => {
    expect(redactSecrets('token={\\"a\\":1} x\nnext')).toBe("token=[REDACTED]");
    expect(redactSecrets("--auth [\\'a] x\nnext")).toBe("--auth [REDACTED]");
    expect(redactSecrets("token={'a\nb} c")).toBe("token=[REDACTED]");
  });
  it("stays linear on escaped-quote and bracket repeats", () => {
    const inputs = [
      'token={\\"'.repeat(11_200),
      'token={\\"\n'.repeat(10_000),
      "--token {".repeat(11_200),
      "--password [\\'\n".repeat(7_200),
      "token={'".repeat(12_500),
      "token={" + "\\".repeat(100_000),
    ];
    for (const s of inputs) {
      const t0 = performance.now();
      anonymizeEvent(ev({ command: s, summary: s, path: s }), opts);
      expect(performance.now() - t0).toBeLessThan(500);
    }
  });
});

describe("fix round 5", () => {
  const anon = (command: string) => anonymizeEvent(ev({ command }), opts).command as string;
  it("a redacted value extends across glued quotes", () => {
    for (const c of ["token=abc'def'", "token=[a]'secret'", `token="a"b'c'`]) {
      expect(anon(`run ${c} next`)).toBe("run token=[REDACTED] next");
    }
    expect(anon('{"token":"a","z":1}')).toBe('{"token":[REDACTED],"z":1}');
    expect(anon("x?token=a'b'&n=1")).toBe("x?token=[REDACTED]&n=1");
  });
  it("a flag value that is itself a secret flag still gets redacted, and so does the next value", () => {
    expect(anon("--token --password hunter2")).not.toContain("hunter2");
    expect(anon("--token --password=hunter2 y")).toBe("--token [REDACTED] y");
    expect(anon("--password -Xy9! y")).toBe("--password [REDACTED] y");
    expect(anon("--token -x")).toBe("--token [REDACTED]");
    expect(anon("--token=-x y")).toBe("--token=[REDACTED] y");
    const s = "--token ".repeat(12_500);
    const t0 = performance.now();
    anonymizeEvent(ev({ command: s, summary: s, path: s }), opts);
    expect(performance.now() - t0).toBeLessThan(500);
  });
  it("an escaped quote in an open group fails closed to the end of the text", () => {
    expect(redactSecrets("token={\\\"a\\\":1,\n \"b\":\"secret\"}")).toBe("token=[REDACTED]");
  });
});

describe("final wave F2: range-merge redaction", () => {
  const anon = (command: string) => anonymizeEvent(ev({ command }), opts).command as string;
  it("a value containing a keyword is redacted whole", () => {
    expect(anon("mytool --password Ab1token=Q")).toBe("mytool --password [REDACTED]");
    const a = anon("mytool --password pwd=Zq7 next");
    expect(a).not.toMatch(/Zq7|pwd=/);
    expect(a).toBe("mytool --password [REDACTED] next");
    expect(anon("--password token=abc")).toBe("--password [REDACTED]"); // former README gap
  });
  it("chained flags and dash values", () => {
    expect(anon("--token --password hunter2")).toBe("--token [REDACTED] [REDACTED]");
    expect(anon("--password -Xy9!")).not.toContain("Xy9");
  });
  it("a value starting inside another value that runs past it extends the redaction", () => {
    expect(anon('run --password xtoken="b c" next')).toBe("run --password [REDACTED] next");
  });
  it("stays linear on 100k chars of nested and repeated prefixes", () => {
    const inputs = [
      "token=".repeat(16_700),
      "--token --password ".repeat(5_300),
      "--password Ab1token=".repeat(5_000),
      '{"token":'.repeat(11_100),
      '{"token":{"a":"b"},'.repeat(5_300),
      'token="'.repeat(14_300),
      "--token {".repeat(11_200),
      "--token [".repeat(11_200) + "]".repeat(11_200),
      'a token="b c" '.repeat(7_100),
      "token=\\\"".repeat(10_000),
    ];
    for (const s of inputs) {
      const t0 = performance.now();
      anonymizeEvent(ev({ command: s, summary: s, path: s }), opts);
      expect(performance.now() - t0, s.slice(0, 20)).toBeLessThan(500);
    }
  });
});

describe("final wave F4: per-session roots and paths outside home", () => {
  const W = (p: string) => p.replace(/\//g, "\\"); // Windows form, without escaping noise
  const cliCwd = W("C:/Users/linus/Documents/Lotse/bridge");
  const sandbox = W("C:/Users/linus/Documents/Lotse/sandbox");
  const recOpts = { repoRoot: cliCwd, homeDir: W("C:/Users/linus") };
  async function record(events: AgentEvent[]): Promise<AgentEvent[]> {
    const file = join(tempDir("rec-"), "r.ndjson");
    const r = new Recorder(file, recOpts);
    for (const e of events) r.write(e);
    await r.close();
    return readRecording(file);
  }
  it("uses the first cwd of each session as its <repo>, and the CLI cwd when an event has none", async () => {
    const got = await record([
      ev({ sid: "a", cwd: sandbox, path: W(`${sandbox}/src/a.ts`) }),
      ev({ sid: "a", cwd: W(`${sandbox}/src`), path: W(`${sandbox}/b.ts`) }), // a later cwd does not move the root
      ev({ sid: "b", path: W(`${cliCwd}/x.ts`) }), // no cwd yet: the CLI cwd
      ev({ sid: "b", cwd: sandbox, path: W(`${sandbox}/y.ts`) }),
    ]);
    expect(got.map((e) => [e.cwd, e.path])).toEqual([
      ["<repo>", "<repo>/src/a.ts"],
      ["<repo>/src", "<repo>/b.ts"],
      [undefined, "<repo>/x.ts"],
      ["<repo>", "<repo>/y.ts"],
    ]);
  });
  it("a session cwd that is home or above it never becomes the root, so home paths stay ~/...", async () => {
    const got = await record([
      ev({ sid: "c", cwd: W("C:/Users"), path: W("C:/Users/linus/.bashrc") }),
      ev({ sid: "d", cwd: W("C:/Users/linus"), path: W("C:/Users/linus/x.txt") }),
    ]);
    expect(got.map((e) => e.path)).toEqual(["~/.bashrc", "~/x.txt"]);
  });
  it("an absolute path outside home and the session root keeps only its basename", async () => {
    const [e] = await record([ev({ sid: "a", cwd: sandbox, path: W("D:/clients/acme-corp/src/a.ts") })]);
    expect(e.path).toBe("<abs>/a.ts");
    expect(JSON.stringify(e)).not.toContain("acme-corp");
    expect(anonymizeEvent(ev({ path: "/srv/acme-corp/x.log" }), opts).path).toBe("<abs>/x.log");
    expect(anonymizeEvent(ev({ cwd: W("\\\\fileserver/acme-corp/proj") }), opts).cwd).toBe("<abs>/proj");
    // home paths, other users' homes and relative paths keep their forms
    expect(anonymizeEvent(ev({ path: W("C:/Users/linus/notes.md") }), opts).path).toBe("~/notes.md");
    expect(anonymizeEvent(ev({ path: W("C:/Users/bob/x") }), opts).path).toBe("~other/x");
    expect(anonymizeEvent(ev({ path: "src/a.ts" }), opts).path).toBe("src/a.ts");
  });
});

describe("Recorder", () => {
  it("round-trips with t rebased and anonymised content, one line per event", async () => {
    const file = join(tempDir("rec-"), "sub", "r.ndjson");
    const r = new Recorder(file, opts);
    r.write(ev({ t: 5000, path: "C:\\Users\\linus\\proj\\a.ts" }));
    r.write(ev({ t: 6500, command: "echo\nhi ghp_" + "a".repeat(36) }));
    r.write(ev({ t: 9000, kind: "stop" }));
    await r.close();
    const raw = readFileSync(file, "utf8");
    expect(raw.split("\n")).toHaveLength(4);
    expect(raw).not.toContain("linus");
    expect(raw).not.toContain("ghp_");
    const got = await readRecording(file);
    expect(got.map((e) => e.t)).toEqual([0, 1500, 4000]);
    expect(got[0].path).toBe("<repo>/a.ts");
    expect(() => r.write(ev({}))).toThrow(/closed/);
  });
  it("readRecording skips blank lines and reports the line number on bad JSON", async () => {
    const dir = tempDir("rec-");
    const ok = join(dir, "ok.ndjson");
    writeFileSync(ok, `\n${JSON.stringify(ev({}))}\n\n`);
    expect(await readRecording(ok)).toHaveLength(1);
    const bad = join(dir, "bad.ndjson");
    writeFileSync(bad, `${JSON.stringify(ev({}))}\n{oops\n`);
    await expect(readRecording(bad)).rejects.toThrow(/line 2/);
  });
  it("F5: readRecording rejects wrongly typed fields with the 1-based line number", async () => {
    const dir = tempDir("rec-");
    const okLine = JSON.stringify(ev({ tool: "Bash", command: "ls", ctxPct: 50, costUsd: 1.5 }));
    const cases: [string, RegExp][] = [
      ['"costUsd":"16"', /line 2: costUsd/],
      ['"tool":5', /line 2: tool/],
      ['"ctxPct":null', /line 2: ctxPct/],
      ['"command":["rm"]', /line 2: command/],
      ['"path":{}', /line 2: path/],
      ['"cwd":1', /line 2: cwd/],
      ['"summary":true', /line 2: summary/],
      ['"notifyType":0', /line 2: notifyType/],
    ];
    for (const [field, re] of cases) {
      const f = join(dir, "bad.ndjson");
      writeFileSync(f, `${okLine}\n{"sid":"s","t":1,"kind":"status",${field}}\n`);
      await expect(readRecording(f), field).rejects.toThrow(re);
    }
    const kind = join(dir, "kind.ndjson");
    writeFileSync(kind, `\n\n{"sid":"s","t":1,"kind":"explode"}\n`);
    await expect(readRecording(kind)).rejects.toThrow(/line 3: kind/);
    const ok = join(dir, "ok.ndjson");
    writeFileSync(ok, `${okLine}\n`);
    expect(await readRecording(ok)).toEqual([JSON.parse(okLine)]);
  });
});

describe("residual R1: readRecording finiteness", () => {
  it.each([
    ['{"sid":"s","t":1e400,"kind":"prompt"}', "t"],
    ['{"sid":"s","t":0,"kind":"tool_start","ctxPct":1e400}', "ctxPct"],
  ])("rejects a non-finite number with the line number: %s", async (line, field) => {
    const f = join(tempDir("rr-"), "r.ndjson");
    writeFileSync(f, `{"sid":"s","t":0,"kind":"prompt"}
${line}
`);
    await expect(readRecording(f)).rejects.toThrow(new RegExp(`line 2: .*${field}`));
  });
});

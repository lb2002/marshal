import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { isEphemeral, runtimeDir, stableCliFile } from "../src/runtime-home.js";

function fakeNpx(version: string): { cli: string; home: string } {
  const base = mkdtempSync(join(tmpdir(), "lotse-npx-"));
  const nm = join(base, "_npx", "abc123", "node_modules");
  mkdirSync(join(nm, "marshal-agents", "dist"), { recursive: true });
  writeFileSync(join(nm, "marshal-agents", "dist", "cli.js"), "// cli\n");
  writeFileSync(join(nm, "marshal-agents", "package.json"), JSON.stringify({ name: "marshal-agents", version, type: "module" }));
  mkdirSync(join(nm, "ws"), { recursive: true });
  writeFileSync(join(nm, "ws", "package.json"), JSON.stringify({ name: "ws", version: "8.22.2", main: "index.js" }));
  writeFileSync(join(nm, "ws", "index.js"), "module.exports = {};\n");
  const home = join(base, "home");
  mkdirSync(home);
  return { cli: join(nm, "marshal-agents", "dist", "cli.js"), home };
}

describe("runtime home", () => {
  it("recognises npx and bunx caches, not a normal install", () => {
    expect(isEphemeral(String.raw`C:\Users\a\AppData\Local\npm-cache\_npx\abc\node_modules\marshal-agents\dist\cli.js`)).toBe(true);
    expect(isEphemeral("/home/a/.npm/_npx/abc/node_modules/marshal-agents/dist/cli.js")).toBe(true);
    expect(isEphemeral("/home/a/.bun/install/cache/marshal-agents@1/dist/cli.js")).toBe(true);
    expect(isEphemeral("C:/Users/a/marshal/bridge/dist/cli.js")).toBe(false);
    expect(isEphemeral("/usr/lib/node_modules/marshal-agents/dist/cli.js")).toBe(false);
  });
  it("keeps a normal path as it is and copies nothing", () => {
    const { home } = fakeNpx("1.0.0");
    expect(stableCliFile("/opt/marshal/dist/cli.js", home)).toBe("/opt/marshal/dist/cli.js");
    expect(existsSync(join(home, ".lotse"))).toBe(false);
  });
  it("copies an npx run to ~/.lotse/runtime/<version> with ws, and reuses the copy", () => {
    const { cli, home } = fakeNpx("1.2.3");
    const stable = stableCliFile(cli, home);
    expect(stable).toBe(join(runtimeDir(home, "1.2.3"), "dist", "cli.js"));
    expect(readFileSync(stable, "utf8")).toBe("// cli\n");
    expect(existsSync(join(runtimeDir(home, "1.2.3"), "node_modules", "ws", "index.js"))).toBe(true);
    expect(JSON.parse(readFileSync(join(runtimeDir(home, "1.2.3"), "package.json"), "utf8")).type).toBe("module");
    writeFileSync(stable, "// kept\n");
    expect(stableCliFile(cli, home)).toBe(stable);
    expect(readFileSync(stable, "utf8")).toBe("// kept\n");
  });
  it("never uses a version string as a path", () => {
    const { cli, home } = fakeNpx("../../evil");
    expect(stableCliFile(cli, home)).toBe(join(runtimeDir(home, "0.0.0"), "dist", "cli.js"));
  });
});

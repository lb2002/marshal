// A stable home for the CLI when it runs from a throwaway place.
//
// `npx marshal-agents` runs the package from npm's cache (`…/_npx/<hash>/node_modules/marshal-agents`), which npm
// may delete at any time. The hooks that `connect` installs call the CLI by its absolute path, so they must not
// point into that cache. Before installing, the package (dist/, package.json and the `ws` library) is copied to
// `~/.lotse/runtime/<version>/` and the hooks point there. A copy that is already complete is reused.

import { cpSync, existsSync, mkdirSync, readFileSync, renameSync, rmSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";

/** True when `cliFile` lies in npm's npx cache (or another one-off runner cache). */
export function isEphemeral(cliFile: string): boolean {
  const p = cliFile.replace(/\\/g, "/").toLowerCase();
  return p.includes("/_npx/") || p.includes("/.bun/install/cache/") || p.includes("/dlx-");
}

/** The package root of a `<root>/dist/cli.js`. */
const packageRoot = (cliFile: string): string => dirname(dirname(cliFile));

/** Where the stable copy of `version` lives. */
export const runtimeDir = (home: string, version: string): string => join(home, ".lotse", "runtime", version);

/**
 * Returns the cli.js the hooks should call: `cliFile` itself unless it is ephemeral; then a copy under
 * ~/.lotse/runtime/<version> (made now if missing). The copy is built in a temp folder next to it and renamed,
 * so a crash never leaves a half copy that looks complete.
 */
export function stableCliFile(cliFile: string, home: string): string {
  if (!isEphemeral(cliFile)) return cliFile;
  const root = packageRoot(cliFile);
  const pkg = JSON.parse(readFileSync(join(root, "package.json"), "utf8")) as { version?: unknown };
  const version = typeof pkg.version === "string" && /^[0-9A-Za-z.+-]+$/.test(pkg.version) ? pkg.version : "0.0.0";
  const target = runtimeDir(home, version);
  const done = join(target, "dist", "cli.js");
  if (existsSync(done) && existsSync(join(target, "node_modules", "ws", "package.json"))) return done;

  const tmp = `${target}.tmp-${process.pid}`;
  rmSync(tmp, { recursive: true, force: true });
  mkdirSync(tmp, { recursive: true });
  cpSync(join(root, "dist"), join(tmp, "dist"), { recursive: true });
  cpSync(join(root, "package.json"), join(tmp, "package.json"));
  // `ws` sits next to the package in npx's node_modules, or inside it; resolve it from the package itself.
  const wsPkg = createRequire(join(root, "package.json")).resolve("ws/package.json");
  cpSync(dirname(wsPkg), join(tmp, "node_modules", "ws"), { recursive: true });
  rmSync(target, { recursive: true, force: true });
  mkdirSync(dirname(target), { recursive: true });
  renameSync(tmp, target);
  return done;
}

// Ruling A9 (review M8): the impact module must not import, directly or through other modules, anything that can
// start a process, a worker or a network connection. This walks the static import graph of src/impact.ts.
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { describe, expect, it } from "vitest";

const SRC = resolve(import.meta.dirname, "..", "src");
const FORBIDDEN = new Set(["child_process", "worker_threads", "cluster", "net", "tls", "http", "https", "http2", "dgram", "dns", "inspector"]);

/** Runtime module specifiers of a TypeScript source (type-only imports and exports left out). */
export function runtimeImports(text: string): string[] {
  const out: string[] = [];
  const code = text.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:"'`])\/\/.*$/gm, "$1");
  for (const m of code.matchAll(/(?:^|[\n;])\s*(import|export)(\s+type)?\b([^;"'`]*?)\bfrom\s*["']([^"']+)["']/g)) {
    if (m[2] === undefined) out.push(m[4]);
  }
  for (const m of code.matchAll(/(?:^|[\n;])\s*import\s*["']([^"']+)["']/g)) out.push(m[1]);
  for (const m of code.matchAll(/\bimport\s*\(\s*([^)]*)\)/g)) out.push(/^["'][^"']+["']$/.test(m[1].trim()) ? m[1].trim().slice(1, -1) : `<computed ${m[1]}>`);
  for (const m of code.matchAll(/\brequire\s*\(\s*([^)]*)\)/g)) out.push(`<require ${m[1]}>`);
  return out;
}

/** Every module reachable from `entry` through runtime imports: local files and builtins/packages. */
function graph(entry: string): { files: string[]; external: string[] } {
  const files = new Set<string>();
  const external = new Set<string>();
  const todo = [entry];
  while (todo.length > 0) {
    const f = todo.pop() as string;
    if (files.has(f)) continue;
    files.add(f);
    for (const spec of runtimeImports(readFileSync(f, "utf8"))) {
      if (spec.startsWith(".")) todo.push(resolve(dirname(f), spec.replace(/\.js$/, ".ts")));
      else external.add(spec.replace(/^node:/, "").split("/")[0]);
    }
  }
  return { files: [...files].map((f) => f.slice(SRC.length + 1).replace(/\\/g, "/")).sort(), external: [...external].sort() };
}

describe("the impact module's import graph", () => {
  it("contains no process, worker or network module, and no computed import or require", () => {
    const g = graph(join(SRC, "impact.ts"));
    expect(g.external.filter((x) => FORBIDDEN.has(x) || x.startsWith("<"))).toEqual([]);
    expect(g.files).not.toContain("explain.ts");
    expect(g.files).toContain("risk/lexer.ts"); // the walk does follow local imports
  });

  it("the walker sees what it must see", () => {
    const sample = [
      'import { spawn } from "node:child_process";',
      'import type { X } from "./types.js";',
      "import {\n  a,\n  type B,\n} from './multi.js';",
      'export { c } from "./re.js";',
      'export type { D } from "./typesonly.js";',
      'import "./side.js";',
      'const m = await import("node:net");',
      "const n = await import(name);",
      "// import { z } from \"./commented.js\";",
    ].join("\n");
    expect(runtimeImports(sample)).toEqual(["node:child_process", "./multi.js", "./re.js", "./side.js", "node:net", "<computed name>"]);
  });
});

describe("no network globals in the impact module's graph (review 2 M-6)", () => {
  it("no fetch, WebSocket, XMLHttpRequest, process.binding or dlopen in any file it imports", () => {
    const files = graph(join(SRC, "impact.ts")).files;
    for (const f of files) {
      const text = readFileSync(join(SRC, f), "utf8");
      expect(text, f).not.toMatch(/\bfetch\s*\(|\bWebSocket\b|XMLHttpRequest|process\.binding|process\.dlopen|\beval\s*\(|new Function\s*\(/);
    }
  });
});

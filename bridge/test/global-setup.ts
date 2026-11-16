// Builds the CLI once into a temp folder before any test file runs, so the spawn-level hook tests
// (hook-cli.test.ts) need no `npm run build` and the compile does not load the CPU while timing tests run.
import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

declare module "vitest" {
  export interface ProvidedContext {
    cliBuild: string;
  }
}

export default function setup(project: { provide: (key: "cliBuild", value: string) => void }): () => void {
  const root = resolve(import.meta.dirname, "..");
  const out = mkdtempSync(join(tmpdir(), "lotse-cli-build-"));
  execFileSync(process.execPath, [join(root, "node_modules", "typescript", "bin", "tsc"), "-p", root, "--outDir", out], { stdio: "pipe" });
  project.provide("cliBuild", out);
  return () => rmSync(out, { recursive: true, force: true });
}

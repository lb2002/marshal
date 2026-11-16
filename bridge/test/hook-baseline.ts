// The hook outputs of the non-halted path, computed by the shim for every agent fixture and every kind of
// bridge reply. `hook-output-baseline.json` holds these outputs as captured before the halt-all task; the test in
// halt.test.ts recomputes them with the current shim and requires them to be identical.
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { runShim } from "../src/hook-shim.js";
import type { ShimSource } from "../src/adapters.js";
import { UNREACHABLE } from "../src/hook-fallback.js";

const FIXTURES = join(import.meta.dirname, "fixtures", "agents");

const ALLOW = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "allow" } } };
const DENY = { hookSpecificOutput: { hookEventName: "PermissionRequest", decision: { behavior: "deny", message: "Nein." } } };
/** What the bridge may answer, named. */
export const REPLIES: Record<string, unknown> = { empty: {}, allow: ALLOW, deny: DENY, garbage: { x: 1 }, none: undefined, unreachable: UNREACHABLE };

export async function hookOutputs(): Promise<Record<string, { route: string; ms: number; out: string }>> {
  const out: Record<string, { route: string; ms: number; out: string }> = {};
  for (const file of readdirSync(FIXTURES).sort()) {
    const source = file.split("-")[0] as ShimSource;
    const raw = readFileSync(join(FIXTURES, file), "utf8");
    for (const platform of ["win32", "linux"] as const) {
      for (const approval of [false, true]) {
        for (const [name, reply] of Object.entries(REPLIES)) {
          let route = "";
          let ms = -1;
          const post = async (path: string, _body: string, wait: number) => {
            route = path;
            ms = wait;
            return reply;
          };
          const o = await runShim(source, raw, post, { evMs: 500, permMs: 1000, platform, approval });
          out[`${file} ${platform} approval=${approval} reply=${name}`] = { route, ms, out: o };
        }
      }
    }
  }
  return out;
}

// Fix round 1 of task P4, finding 1d: no text that reaches an agent tells it how to disconnect, uninstall or stop
// Marshal. Agents read hook output (deny messages, halt refusals, replies); a hint there is an instruction to them.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { MUSE_BRIDGE_OFF_DENY, MUSE_NO_DECISION_DENY } from "../src/hook-fallback.js";
import { HALT_MESSAGE, haltRefusal, permHaltReply, preToolHaltReply } from "../src/halt.js";
import { museNoDecision, preToolDeny, shimOutput } from "../src/adapters.js";
import { REPLY_PREFIX } from "../src/hook-cli.js";

const HINT = /disconnect|uninstall|install-hooks|connect\b|lotse\s+\w|cli\.js|remove (?:the )?(?:\w+ )?hooks|switch (?:it |them )?off|turn (?:it )?off|stop (?:marshal|the bridge)|kill|--tool|--all/i;

/** The modules whose strings are printed to an agent (hook stdout/stderr). */
const AGENT_MODULES = ["hook-fallback.ts", "halt.ts", "adapters.ts", "hook-cli.ts", "hook-shim.ts", "replies.ts"];

/** String literals of a TypeScript file, comments removed first. */
function literals(file: string): string[] {
  const text = readFileSync(join(import.meta.dirname, "..", "src", file), "utf8")
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .replace(/^\s*\/\/.*$/gm, "");
  return [...text.matchAll(/"((?:[^"\\\n]|\\.)*)"|`((?:[^`\\]|\\.)*)`/g)].map((m) => m[1] ?? m[2]);
}

describe("texts that reach an agent name no way to switch Marshal off", () => {
  it("the fixed deny and halt texts", () => {
    const texts = [
      MUSE_BRIDGE_OFF_DENY,
      MUSE_NO_DECISION_DENY,
      museNoDecision(),
      HALT_MESSAGE,
      JSON.stringify(preToolHaltReply()),
      JSON.stringify(permHaltReply()),
      REPLY_PREFIX,
      ...(["claude", "codex", "gemini", "muse"] as const).flatMap((s) => [haltRefusal(s, "pre"), haltRefusal(s, "perm"), shimOutput(s, { behavior: "deny" }), preToolDeny(s, HALT_MESSAGE)]),
    ];
    for (const t of texts) expect(HINT.exec(t)?.[0], t).toBeUndefined();
  });
  for (const file of AGENT_MODULES) {
    it(`every user-readable string literal in src/${file}`, () => {
      // Only sentences (with a blank and a letter) can carry a hint; identifiers, flags parsed and paths are code.
      const sentences = literals(file).filter((s) => /[A-Za-z]{3,} [A-Za-z]{2,}/.test(s));
      for (const s of sentences) expect(HINT.exec(s)?.[0], `${file}: ${s}`).toBeUndefined();
    });
  }
});

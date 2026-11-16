// Installing and removing the Lotse hooks of one tool in one project folder, shared by `install-hooks`,
// `uninstall-hooks` and `connect`. The guards and the guarded write live in install.ts.
//
// Installing is idempotent: hooks that are exactly Lotse's own (of this CLI, either Claude transport) are
// replaced; any other existing hooks (or a foreign Claude statusLine) are refused unless forced, as before.
import { existsSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { buildHookSettings, checkTargetDir, claudeTarget, mergeIntoProjectSettings, parseSettingsText, removeHooksWhere, removeLotseHooks, writeTargetFile, } from "./install.js";
import { TOOL_TARGETS, buildToolHooks, mergeToolHooks, removeToolHooks } from "./install-tools.js";
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
export function targetFor(tool) {
    return tool === "claude" ? claudeTarget() : TOOL_TARGETS[tool];
}
/** Removes every Lotse hook of this CLI for `tool` (Claude: both transports and the statusLine). */
function withoutOurs(existing, tool, bridgeCmd) {
    return tool === "claude" ? removeLotseHooks(existing, bridgeCmd) : removeToolHooks(existing, bridgeCmd, tool);
}
/**
 * The settings to write: our hooks replace our old ones; anything else under `hooks` (even an empty entry)
 * is refused unless `force`, exactly as before. Pure.
 */
export function planHooks(existing, tool, bridgeCmd, transport, force) {
    let base = existing;
    if (existing !== null) {
        const cleaned = withoutOurs(existing, tool, bridgeCmd);
        // Only a file that held some of our hooks is cleaned; otherwise the old refusal applies unchanged.
        // (Compared with a removal of nothing, which drops empty groups the same way.)
        if (!same(cleaned, removeHooksWhere(existing, () => false)))
            base = cleaned;
    }
    return tool === "claude"
        ? mergeIntoProjectSettings(base, buildHookSettings(bridgeCmd, transport), force)
        : mergeToolHooks(base, buildToolHooks(tool, bridgeCmd), force);
}
const isDir = (p) => existsSync(p) && statSync(p).isDirectory();
/** Guard checks, then the existing settings file (null when absent). */
function prepare(p) {
    const dir = resolve(p.dir);
    const target = targetFor(p.tool);
    const problem = checkTargetDir(dir, p.homeDir, isDir, target);
    if (problem)
        return { error: problem };
    const file = join(dir, target.sub, target.file);
    if (!existsSync(file))
        return { file, target, existing: null };
    const parsed = parseSettingsText(readFileSync(file, "utf8"));
    if ("error" in parsed)
        return { error: `${file} is ${parsed.error}; not touching it` };
    return { file, target, existing: parsed.value };
}
export function installHooks(p, transport, force) {
    const prep = prepare(p);
    if ("error" in prep)
        return { ok: false, error: prep.error };
    const plan = planHooks(prep.existing, p.tool, p.bridgeCmd, transport, force);
    if ("error" in plan) {
        const what = p.tool === "claude" ? "hooks or a statusLine" : "hooks";
        return { ok: false, error: `${prep.file} already has ${what}; use --force to replace them` };
    }
    if (prep.existing !== null && same(plan.settings, prep.existing))
        return { ok: true, file: prep.file, changed: false };
    const problem = writeTargetFile(resolve(p.dir), p.homeDir, JSON.stringify(plan.settings, null, 2) + "\n", prep.target);
    if (problem)
        return { ok: false, error: problem };
    return { ok: true, file: prep.file, changed: true };
}
/** What uninstallHooks would write, without writing: the file, its settings now and after (null: no file). */
export function uninstallPlan(p) {
    const prep = prepare(p);
    if ("error" in prep)
        return { ok: false, error: prep.error };
    if (prep.existing === null)
        return { ok: true, file: prep.file, before: null, after: null };
    return { ok: true, file: prep.file, before: prep.existing, after: withoutOurs(prep.existing, p.tool, p.bridgeCmd) };
}
export function uninstallHooks(p) {
    const prep = prepare(p);
    if ("error" in prep)
        return { ok: false, error: prep.error };
    if (prep.existing === null)
        return { ok: true, file: prep.file, changed: false, existed: false };
    const cleaned = withoutOurs(prep.existing, p.tool, p.bridgeCmd);
    if (same(cleaned, prep.existing))
        return { ok: true, file: prep.file, changed: false, existed: true };
    const problem = writeTargetFile(resolve(p.dir), p.homeDir, JSON.stringify(cleaned, null, 2) + "\n", prep.target);
    if (problem)
        return { ok: false, error: problem };
    return { ok: true, file: prep.file, changed: true, existed: true };
}

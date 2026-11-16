// Commands that change or stop Marshal itself: its own CLI (`lotse`, or `node …/cli.js` of the bridge) with any
// subcommand that installs or removes hooks, starts or stops a bridge, or that this rule does not know. An agent
// must not switch off the control that watches it with a one-hand approval (task-p4 review, finding 1).
// Every call counts (a bare `lotse` too: arguments can come later, from xargs, a file or a wrapper), except
// exactly one read-only word from HARMLESS with nothing after it, written out literally, not reached through
// a wrapper that adds arguments, and with stdin neither piped nor redirected (fix round 1, second review).
import { fileURLToPath } from "node:url";
import { destructive } from "./common.js";
import { resolveFor } from "./paths.js";
import { PRODUCT_NAME } from "../product.js";
export const SELF = destructive(`Changes or stops ${PRODUCT_NAME} itself`);
const HARMLESS = new Set(["--help", "-h", "help", "--version", "-v", "version", "status"]);
const LAUNCHERS = new Set(["npx", "pnpx", "bunx", "npm", "pnpm", "yarn", "bun"]);
const CLI_FILES = new Set(["cli.js", "cli.mjs", "cli.cjs", "cli"]); // node resolves `…/dist/cli` to cli.js
const CLI_DIR_RE = /lotse|bridge|marshal/i;
const baseName = (t) => t.slice(Math.max(t.lastIndexOf("/"), t.lastIndexOf("\\")) + 1).toLowerCase();
/** `lotse`, `lotse.cmd`, `lotse.ps1`, `lotse.exe`, with or without a path, or a package spec `lotse@1.2`. */
const isLotseName = (t) => /^lotse(?:\.(?:cmd|ps1|exe|bat))?$/.test(baseName(t).replace(/\^/g, "")) || /^lotse@/i.test(baseName(t));
/** This bridge's own `dist/cli.js` (forward slashes, lower case), wherever it is installed. */
const OWN_CLI = (() => {
    try {
        return fileURLToPath(new URL("../cli.js", import.meta.url)).replace(/\\/g, "/").toLowerCase();
    }
    catch {
        return null;
    }
})();
/** Subcommands that only Marshal's CLI has: with them any `cli.js` counts, whatever folder it is in. */
const OWN_SUBS = new Set(["connect", "disconnect", "install-hooks", "uninstall-hooks"]);
/**
 * A script path that is the bridge's CLI: basename cli.js (or `cli`) inside a path naming lotse, bridge or
 * marshal, or this bridge's own cli.js; `rest` is what follows it.
 */
function isMarshalCli(w, ctx, rest = []) {
    if (!CLI_FILES.has(baseName(w.text)))
        return false;
    if (w.dyn || w.glob)
        return true; // cannot be known: count it
    const full = (resolveFor(w.text, ctx) ?? w.text).replace(/\\/g, "/");
    if (CLI_DIR_RE.test(full))
        return true;
    if (OWN_CLI !== null && full.toLowerCase() === OWN_CLI)
        return true;
    return OWN_SUBS.has((rest[0]?.text ?? "").toLowerCase());
}
/** After Marshal's CLI: harmless only for exactly one literal read-only word, reached plainly. */
function subRisk(rest, site) {
    if (site.argsAdded || site.stdinConnected || rest.length !== 1)
        return SELF;
    const sub = rest[0];
    if (sub.dyn || sub.glob || sub.inert)
        return SELF;
    return HARMLESS.has(sub.text.toLowerCase()) ? null : SELF;
}
/** SELF when `name args` runs Marshal's own CLI in any way but a plain read-only one; else null. */
export function marshalSelfRisk(name, args, ctx, site = { argsAdded: false, stdinConnected: false }) {
    if (name === "lotse")
        return subRisk(args, site);
    if (name === "node") {
        const i = args.findIndex((w, k) => !w.text.startsWith("-") && isMarshalCli(w, ctx, args.slice(k + 1)));
        return i < 0 ? null : subRisk(args.slice(i + 1), site);
    }
    if (LAUNCHERS.has(name)) {
        const i = args.findIndex((w, k) => isLotseName(w.text) || isMarshalCli(w, ctx, args.slice(k + 1)));
        return i < 0 ? null : subRisk(args.slice(i + 1), site);
    }
    return null;
}

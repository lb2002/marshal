// Argument parsing for the CLI, kept apart from cli.ts (which runs on import) so it can be tested.
export class UsageError extends Error {
}
/**
 * Minimal flag parser: `value` flags take an argument, `bool` flags do not. `alias` maps a short
 * flag (`-o`) to a long one; it applies only in flag position, never to a value already consumed.
 * Any other argument that does not start with `--` is positional, as before.
 */
export function parseArgs(args, value, bool, alias = {}) {
    const pos = [];
    const flags = {};
    for (let i = 0; i < args.length; i++) {
        const a = args[i];
        let name;
        if (a.startsWith("--")) {
            name = a.slice(2);
        }
        else if (a.length === 2 && a[0] === "-" && Object.hasOwn(alias, a[1])) {
            name = alias[a[1]];
        }
        else {
            pos.push(a);
            continue;
        }
        if (bool.includes(name)) {
            flags[name] = true;
        }
        else if (value.includes(name)) {
            const v = args[++i];
            if (v === undefined)
                throw new UsageError(`missing value for ${a}`);
            flags[name] = v;
        }
        else {
            throw new UsageError(`unknown flag ${a}`);
        }
    }
    return { pos, flags };
}
/** `--budget` in USD, default 15. */
export function parseBudget(v) {
    const budget = v === undefined ? 15 : Number(v);
    if (!Number.isFinite(budget) || budget < 0)
        throw new UsageError("--budget must be a number >= 0");
    return budget;
}
/** `pack <recording.ndjson> -o|--out <pack.json> [--overrides <file>] [--budget 15] [--explain cli|off]` */
export function parsePackArgs(args) {
    const { pos, flags } = parseArgs(args, ["out", "overrides", "budget", "explain"], [], { o: "out" });
    if (pos.length !== 1)
        throw new UsageError("pack needs exactly one recording file");
    if (typeof flags.out !== "string")
        throw new UsageError("pack needs -o <pack.json>");
    const explain = flags.explain ?? "off";
    if (explain !== "cli" && explain !== "off")
        throw new UsageError("--explain must be cli or off");
    const out = { recording: pos[0], out: flags.out, budget: parseBudget(flags.budget), explain };
    if (typeof flags.overrides === "string")
        out.overrides = flags.overrides;
    return out;
}

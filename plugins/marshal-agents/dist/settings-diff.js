// What a change to a hook settings file adds and removes, entry by entry, so that the list shown before the one
// question is exactly what gets written (task-p4 review, finding 4): every hook handler per event, and the
// statusLine. Handlers are compared as canonical JSON (sorted keys); a handler twice in one event counts twice.
const isObj = (v) => typeof v === "object" && v !== null && !Array.isArray(v);
/** JSON with sorted keys. */
export function canonJson(v) {
    if (Array.isArray(v))
        return `[${v.map(canonJson).join(",")}]`;
    if (isObj(v))
        return `{${Object.keys(v).sort().map((k) => `${JSON.stringify(k)}:${canonJson(v[k])}`).join(",")}}`;
    return JSON.stringify(v);
}
/** Every handler of every event: `hooks.<Event>` with the handler's canonical JSON (a group's `matcher` kept). */
function handlers(settings) {
    const out = [];
    if (!isObj(settings) || !isObj(settings.hooks))
        return out;
    for (const [event, groups] of Object.entries(settings.hooks)) {
        if (!Array.isArray(groups))
            continue;
        for (const g of groups) {
            if (!isObj(g) || !Array.isArray(g.hooks)) {
                out.push([`hooks.${event}`, canonJson(g)]);
                continue;
            }
            const where = g.matcher === undefined ? "" : `[matcher=${encodeURIComponent(canonJson(g.matcher))}]`;
            for (const h of g.hooks)
                out.push([`hooks.${event}${where}`, canonJson(h)]);
        }
    }
    return out;
}
/** The entries `after` adds to and removes from `before` (null: no file). Removals first, then additions. */
export function entryChanges(before, after) {
    const count = (list) => {
        const m = new Map();
        for (const [k, v] of list)
            m.set(`${k}\u0000${v}`, (m.get(`${k}\u0000${v}`) ?? 0) + 1);
        return m;
    };
    const b = count(handlers(before));
    const a = count(handlers(after));
    const diff = (x, y, op) => {
        const out = [];
        for (const [kv, n] of x) {
            const [key, value] = kv.split("\u0000");
            for (let i = 0; i < n - (y.get(kv) ?? 0); i++)
                out.push({ op, key, value });
        }
        return out;
    };
    const removed = diff(b, a, "-");
    const added = diff(a, b, "+");
    // statusLine and any other top-level key that differs (a file created or deleted whole, Muse's schema_version).
    const keys = new Set([...(isObj(before) ? Object.keys(before) : []), ...(isObj(after) ? Object.keys(after) : [])]);
    for (const k of keys) {
        if (k === "hooks")
            continue;
        const vb = isObj(before) && k in before ? canonJson(before[k]) : null;
        const va = isObj(after) && k in after ? canonJson(after[k]) : null;
        if (vb === va)
            continue;
        if (vb !== null)
            removed.push({ op: "-", key: k, value: vb });
        if (va !== null)
            added.push({ op: "+", key: k, value: va });
    }
    return [...removed, ...added];
}
/** One line per entry, indented: `+ hooks.Stop: {…}` (added) or `- statusLine: {…}` (removed or replaced). */
export function formatChanges(changes, indent = "      ") {
    return changes.map((c) => `${indent}${c.op} ${c.key}: ${c.value}`);
}
/** Parses lines printed by formatChanges back (for tests and for checking the list against the write). */
export function parseChangeLine(line) {
    const m = /^\s*([+-]) (\S+): (.*)$/.exec(line);
    return m ? { op: m[1], key: m[2], value: m[3] } : null;
}

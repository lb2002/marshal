import { normalizePath } from "./risk/paths.js";
import { shortPath } from "./state.js";
/** Share of the budget at which a session gets the `budget_near` heads-up. */
const BUDGET_NEAR_SHARE = 0.7;
const EDIT_TOOLS = new Set(["edit", "write", "multiedit", "notebookedit"]);
const pairKey = (path, a, b) => `${path}|${[a, b].sort().join("|")}`;
export class Detectors {
    opts;
    lastEdit = new Map(); // normalised path -> sid -> last edit time
    collisionFired = new Map(); // path|sidA|sidB -> time of last incident
    budgetFired = new Set();
    budgetNearFired = new Set();
    constructor(opts) {
        this.opts = opts;
    }
    check(e, store) {
        const out = [];
        if (e.kind === "tool_start" && e.path && EDIT_TOOLS.has((e.tool ?? "").toLowerCase())) {
            const key = normalizePath(e.path);
            const w = this.opts.collisionWindowMs;
            const edits = this.lastEdit.get(key) ?? new Map();
            for (const [sid, t] of edits)
                if (e.t - t > w)
                    edits.delete(sid);
            for (const [k, t] of this.collisionFired)
                if (e.t - t > w)
                    this.collisionFired.delete(k);
            const others = [...edits].filter(([sid, t]) => sid !== e.sid && Math.abs(e.t - t) <= w).map(([sid]) => sid);
            const fresh = others.filter((sid) => !this.collisionFired.has(pairKey(key, e.sid, sid)));
            if (fresh.length > 0) {
                const sids = [e.sid, ...fresh];
                for (const sid of fresh)
                    this.collisionFired.set(pairKey(key, e.sid, sid), e.t);
                const names = sids.map((sid) => store.get(sid)?.name ?? sid);
                out.push({ kind: "collision", sids, detail: `${names.join(" and ")} edit ${shortPath(e.path)}`, t: e.t });
            }
            edits.set(e.sid, e.t);
            this.lastEdit.set(key, edits);
        }
        if (e.kind === "status" && e.costUsd !== undefined) {
            const budget = this.opts.budgetUsd;
            const detail = `${store.get(e.sid)?.name ?? e.sid}: ${e.costUsd.toFixed(2)} USD of ${budget.toFixed(2)} USD budget`;
            if (e.costUsd >= budget) {
                // at or over budget; a session that jumps straight here gets no separate heads-up
                this.budgetNearFired.add(e.sid);
                if (!this.budgetFired.has(e.sid)) {
                    this.budgetFired.add(e.sid);
                    out.push({ kind: "budget", sids: [e.sid], detail, t: e.t });
                }
            }
            else if (e.costUsd >= budget * BUDGET_NEAR_SHARE && !this.budgetNearFired.has(e.sid)) {
                this.budgetNearFired.add(e.sid);
                out.push({ kind: "budget_near", sids: [e.sid], detail: `${detail} (${Math.round(BUDGET_NEAR_SHARE * 100)}%)`, t: e.t });
            }
        }
        return out;
    }
}

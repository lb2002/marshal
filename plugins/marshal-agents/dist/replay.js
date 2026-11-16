/** Emits events in order, spaced by their `t` deltas divided by `speed`. Returns a cancel function. */
export function replay(events, emit, opts) {
    const { speed } = opts;
    if (!(speed > 0) || !Number.isFinite(speed))
        throw new Error(`replay speed must be > 0 (got ${speed})`);
    let cancelled = false;
    const handles = new Set();
    const schedule = opts.setTimer ??
        ((fn, ms) => {
            const h = setTimeout(() => {
                handles.delete(h);
                fn();
            }, ms);
            handles.add(h);
        });
    let first = null;
    let prev = 0;
    for (const e of events) {
        first ??= e.t;
        prev = Math.max(prev, (e.t - first) / speed); // never earlier than the previous event
        schedule(() => {
            if (!cancelled)
                emit(e);
        }, prev);
    }
    return () => {
        cancelled = true;
        for (const h of handles)
            clearTimeout(h);
        handles.clear();
    };
}

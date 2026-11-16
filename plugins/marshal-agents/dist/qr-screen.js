// The pairing QR code on the terminal, and what happens to it when its code stops working (task-p4 review,
// finding 3): when the code is used, expires or is replaced, the QR block is erased from the screen if it is still
// the last thing printed (cursor up, erase to the end of the screen; terminal only), and in every case one line
// says that the code shown above is no longer valid. A new code prints a new QR block. A headset that keeps
// scanning would otherwise read a dead code and run into the attempt limits.
const WHY = {
    used: "used: a headset paired with it",
    expired: "expired",
    replaced: "replaced by a new code",
};
/** The line printed when the code (and its QR code) above stops working. */
export const invalidLine = (why) => `The pairing code shown above is no longer valid (${WHY[why]}).`;
/** Cursor up `n` lines to column 1, then erase to the end of the screen. */
export const eraseUp = (n) => `\x1b[${n}F\x1b[0J`;
export class QrScreen {
    out;
    /** Writes by anyone, counted; a QR block can be erased only while this is unchanged since it was printed. */
    seq = 0;
    own = false;
    /** The QR block on screen: its line count and `seq` right after it was printed; null when there is none. */
    block = null;
    /** Whether a code is shown that has not been declared invalid yet. */
    live = false;
    constructor(out) {
        this.out = out;
    }
    /** Counts every write to `stream` that does not come from this screen (stdout and stderr share the terminal). */
    watch(stream) {
        const orig = stream.write.bind(stream);
        stream.write = (...args) => {
            if (!this.own)
                this.seq++;
            return orig(...args);
        };
    }
    /** Something else was printed (the user's typed line echoed by the terminal, for example). */
    noteOutput() {
        this.seq++;
    }
    write(s) {
        this.own = true;
        try {
            this.out.write(s);
        }
        finally {
            this.own = false;
        }
    }
    /** A code is now shown (the caller printed it); `qr` is its QR block, printed here as the last thing ([]: none). */
    shown(qr) {
        this.live = true;
        this.block = null;
        if (qr.length === 0 || this.out.isTTY !== true)
            return;
        this.write(qr.join("\n") + "\n");
        this.block = { lines: qr.length, seq: this.seq };
    }
    /**
     * The shown code stopped working. Erases its QR block when it is still the last output on a terminal (`echoed`:
     * lines the terminal itself echoed since, such as the Enter that asked for a new code), then prints the line.
     * Does nothing when no live code is shown (each code is declared invalid once).
     */
    invalidate(why, echoed = 0) {
        if (!this.live)
            return;
        this.live = false;
        const b = this.block;
        this.block = null;
        if (b !== null && this.out.isTTY === true && this.seq === b.seq)
            this.write(eraseUp(b.lines + echoed));
        this.write(invalidLine(why) + "\n");
    }
    /** Whether a live code is shown (for tests). */
    get showing() {
        return this.live;
    }
}

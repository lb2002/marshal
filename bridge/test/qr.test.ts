// The QR encoder (src/qr.ts) and the pairing QR code (src/pair-qr.ts). The repository has no QR decoder, so the
// matrix is checked structurally by a reader written here on its own (its own function map, zigzag, masks, GF(256)):
// finder, timing and alignment patterns, the format bits (BCH check), the codewords read back in placement order,
// Reed-Solomon syndromes zero, and the byte-mode segment equal to the payload.
import { describe, expect, it } from "vitest";
import { byteCapacity, encodeQr, formatBits, penalty, renderQr, renderedWidth, rsRemainder, versionFor, type QrCode } from "../src/qr.js";
import { chooseHost, hostFirst, isPrivateIPv4, pairingPayload, pairingQrLines } from "../src/pair-qr.js";
import { HEADSET_LOOK, connectBanner } from "../src/connect.js";

// ---------------------------------------------------------------- an independent reader

/** GF(256), 0x11D, by shift-and-add (no tables, unlike the encoder). */
function mul(a: number, b: number): number {
  let r = 0;
  while (b > 0) {
    if (b & 1) r ^= a;
    a <<= 1;
    if (a & 0x100) a ^= 0x11d;
    b >>= 1;
  }
  return r;
}
function alphaPow(i: number): number {
  let x = 1;
  for (let k = 0; k < i; k++) x = mul(x, 2);
  return x;
}
/** Syndromes S_i = c(a^i), i = 0..n-1, of a codeword block (highest coefficient first). */
function syndromes(block: number[], n: number): number[] {
  return Array.from({ length: n }, (_, i) => block.reduce((acc, c) => mul(acc, alphaPow(i)) ^ c, 0));
}

/** Level M block layout per version (ISO/IEC 18004 table 9), written out again here. */
const BLOCKS: Record<number, { data: number; ec: number; n: number }> = {
  1: { data: 16, ec: 10, n: 1 },
  2: { data: 28, ec: 16, n: 1 },
  3: { data: 44, ec: 26, n: 1 },
  4: { data: 32, ec: 18, n: 2 },
  5: { data: 43, ec: 24, n: 2 },
  6: { data: 27, ec: 16, n: 4 },
};

/** Which modules are function patterns, by the standard's areas (not by the encoder's drawing code). */
function functionMap(version: number): boolean[][] {
  const size = 17 + 4 * version;
  const fn = Array.from({ length: size }, () => new Array<boolean>(size).fill(false));
  const box = (r0: number, c0: number, h: number, w: number) => {
    for (let r = r0; r < r0 + h; r++) for (let c = c0; c < c0 + w; c++) if (r >= 0 && c >= 0 && r < size && c < size) fn[r][c] = true;
  };
  box(0, 0, 9, 9); // finder, separator, format (top left)
  box(0, size - 8, 9, 8); // finder, separator, format row (top right)
  box(size - 8, 0, 8, 9); // finder, separator, format column, dark module (bottom left)
  box(6, 0, 1, size); // timing
  box(0, 6, size, 1);
  if (version >= 2) box(size - 9, size - 9, 5, 5); // the one alignment pattern, centre (size-7, size-7)
  return fn;
}

const MASKS: ((r: number, c: number) => boolean)[] = [
  (r, c) => (r + c) % 2 === 0,
  (r) => r % 2 === 0,
  (_r, c) => c % 3 === 0,
  (r, c) => (r + c) % 3 === 0,
  (r, c) => (Math.floor(r / 2) + Math.floor(c / 3)) % 2 === 0,
  (r, c) => ((r * c) % 2) + ((r * c) % 3) === 0,
  (r, c) => (((r * c) % 2) + ((r * c) % 3)) % 2 === 0,
  (r, c) => (((r + c) % 2) + ((r * c) % 3)) % 2 === 0,
];

/** Format bits as stored (bit 14 first in reading order of the standard's figure 25), both copies. */
function readFormat(m: boolean[][]): { a: number; b: number } {
  const size = m.length;
  const bit = (r: number, c: number) => (m[r][c] ? 1 : 0);
  // Copy 1: bits 14..9 along row 8 (cols 0-5), bit 8 at (8,7), bit 7 at (8,8), bit 6 at (7,8), bits 5..0 up col 8 (rows 5-0).
  const a1 = [bit(8, 0), bit(8, 1), bit(8, 2), bit(8, 3), bit(8, 4), bit(8, 5), bit(8, 7), bit(8, 8), bit(7, 8), bit(5, 8), bit(4, 8), bit(3, 8), bit(2, 8), bit(1, 8), bit(0, 8)];
  // Copy 2: bits 14..8 up col 8 from the bottom (rows size-1 .. size-7), bits 7..0 along row 8 (cols size-8 .. size-1).
  const a2: number[] = [];
  for (let r = size - 1; r >= size - 7; r--) a2.push(bit(r, 8));
  for (let c = size - 8; c < size; c++) a2.push(bit(8, c));
  const val = (bits: number[]) => bits.reduce((x, b) => (x << 1) | b, 0);
  return { a: val(a1), b: val(a2) };
}

/** Remainder of a 15-bit word modulo the BCH generator x^10+x^8+x^5+x^4+x^2+x+1 (0x537). */
function bchRemainder(v: number): number {
  for (let i = 14; i >= 10; i--) if ((v >>> i) & 1) v ^= 0x537 << (i - 10);
  return v;
}

/** The codewords in placement order: zigzag from the bottom right, two columns at a time, skipping column 6. */
function readCodewords(m: boolean[][], version: number, mask: number): number[] {
  const size = m.length;
  const fn = functionMap(version);
  const bits: number[] = [];
  let upward = true;
  for (let right = size - 1; right > 0; right -= 2) {
    if (right === 6) right--;
    for (let k = 0; k < size; k++) {
      const r = upward ? size - 1 - k : k;
      for (const c of [right, right - 1]) if (!fn[r][c]) bits.push((m[r][c] !== MASKS[mask](r, c)) ? 1 : 0);
    }
    upward = !upward;
  }
  const total = BLOCKS[version].n * (BLOCKS[version].data + BLOCKS[version].ec);
  expect(bits.length).toBe(total * 8 + (version === 1 ? 0 : 7));
  const out: number[] = [];
  for (let i = 0; i < total; i++) out.push(bits.slice(i * 8, i * 8 + 8).reduce((x, b) => (x << 1) | b, 0));
  return out;
}

/** Everything the reader checks; returns the decoded byte-mode payload. */
function readAndCheck(q: QrCode): Uint8Array {
  const { modules: m, size, version } = q;
  expect(size).toBe(17 + 4 * version);
  expect(m.length).toBe(size);
  for (const row of m) expect(row.length).toBe(size);
  // Finder patterns with their light separators.
  for (const [r0, c0] of [[0, 0], [0, size - 7], [size - 7, 0]]) {
    for (let dr = -1; dr <= 7; dr++) {
      for (let dc = -1; dc <= 7; dc++) {
        const r = r0 + dr;
        const c = c0 + dc;
        if (r < 0 || c < 0 || r >= size || c >= size) continue;
        const ring = Math.max(Math.abs(dr - 3), Math.abs(dc - 3));
        expect(m[r][c], `finder ${r0},${c0} at ${r},${c}`).toBe(ring <= 1 || ring === 3);
      }
    }
  }
  // Timing patterns.
  for (let i = 8; i < size - 8; i++) {
    expect(m[6][i], `timing row at ${i}`).toBe(i % 2 === 0);
    expect(m[i][6], `timing col at ${i}`).toBe(i % 2 === 0);
  }
  // Alignment pattern (versions 2 to 6: one, centred at size-7).
  if (version >= 2) {
    const c0 = size - 7;
    for (let dr = -2; dr <= 2; dr++) for (let dc = -2; dc <= 2; dc++) expect(m[c0 + dr][c0 + dc]).toBe(Math.max(Math.abs(dr), Math.abs(dc)) !== 1);
  }
  // Dark module.
  expect(m[size - 8][8]).toBe(true);
  // Format information: both copies equal, a valid BCH code word, level M (00) and the chosen mask.
  const { a, b } = readFormat(m);
  expect(a).toBe(b);
  const raw = a ^ 0x5412;
  expect(bchRemainder(raw)).toBe(0);
  expect(raw >>> 13).toBe(0b00);
  expect((raw >>> 10) & 7).toBe(q.mask);
  // Codewords back out of the matrix, equal to the encoder's interleaved stream.
  const cws = readCodewords(m, version, q.mask);
  expect(cws).toEqual([...q.codewords]);
  // De-interleave; every block's syndromes are zero; the data blocks join to the data codewords.
  const { data: per, ec, n } = BLOCKS[version];
  const data: number[] = [];
  for (let k = 0; k < n; k++) {
    const d = Array.from({ length: per }, (_, i) => cws[i * n + k]);
    const e = Array.from({ length: ec }, (_, i) => cws[per * n + i * n + k]);
    expect(syndromes([...d, ...e], ec), `block ${k}`).toEqual(new Array(ec).fill(0));
    data.push(...d);
  }
  expect(data).toEqual([...q.data]);
  // Byte mode segment: mode 0100, 8-bit count, the bytes, a terminator, then 0xEC/0x11 pads.
  const bits = data.flatMap((x) => [7, 6, 5, 4, 3, 2, 1, 0].map((i) => (x >>> i) & 1));
  const take = (k: number) => bits.splice(0, k).reduce((x, y) => (x << 1) | y, 0);
  expect(take(4)).toBe(0b0100);
  const len = take(8);
  const out = Uint8Array.from({ length: len }, () => take(8));
  const rest = bits.splice(0, Math.min(4, bits.length));
  expect(rest.every((x) => x === 0)).toBe(true);
  while (bits.length % 8 !== 0) expect(bits.shift()).toBe(0);
  for (let i = 0; bits.length > 0; i++) expect(take(8)).toBe(i % 2 === 0 ? 0xec : 0x11);
  return out;
}

// ---------------------------------------------------------------- tests

const PAYLOAD = "marshal://pair?v=1&h=192.168.100.200&p=47821&c=123456";

describe("Reed-Solomon and format bits: published vectors", () => {
  it("ISO/IEC 18004 Annex I, 01234567 at 1-M: the 10 error correction codewords", () => {
    // Data codewords of the standard's worked example (numeric mode, then pad codewords).
    const data = [0x10, 0x20, 0x0c, 0x56, 0x61, 0x80, 0xec, 0x11, 0xec, 0x11, 0xec, 0x11, 0xec, 0x11, 0xec, 0x11];
    expect([...rsRemainder(data, 10)]).toEqual([165, 36, 212, 193, 237, 54, 199, 135, 44, 85]);
  });
  it("HELLO WORLD at 1-M and 1-Q (the widely published tutorial vectors)", () => {
    const m = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
    expect([...rsRemainder(m, 10)]).toEqual([196, 35, 39, 119, 235, 215, 231, 226, 93, 23]);
    expect([...rsRemainder(m.slice(0, 13), 13)]).toEqual([168, 72, 22, 82, 217, 54, 156, 0, 46, 15, 180, 122, 16]);
  });
  it("the format bits for level M equal the standard's table for masks 0 to 7", () => {
    const table = ["101010000010010", "101000100100101", "101111001111100", "101101101001011", "100010111111001", "100000011001110", "100111110010111", "100101010100000"];
    for (let mask = 0; mask < 8; mask++) expect(formatBits(mask).toString(2).padStart(15, "0")).toBe(table[mask]);
  });
  it("there is no published full-matrix vector in byte mode to compare with", () => {
    // The standard's full example (Annex I, "01234567", 1-M) is numeric mode, which this encoder does not have.
    // Its data and error correction codewords are checked above; the matrices are checked structurally below.
    expect(true).toBe(true);
  });
});

describe("encodeQr: structure of every version, every mask", () => {
  const lengths = [1, 14, 15, 26, 27, 42, 43, 53, 62, 63, 84, 85, 106];
  for (const n of lengths) {
    it(`${n} bytes: smallest version, reads back to the payload`, () => {
      const bytes = Uint8Array.from({ length: n }, (_, i) => (i * 37 + 11) & 0xff);
      const q = encodeQr(bytes);
      expect(q.version).toBe(versionFor(n));
      expect(n).toBeLessThanOrEqual(byteCapacity(q.version));
      if (q.version > 1) expect(n).toBeGreaterThan(byteCapacity(q.version - 1));
      expect([...readAndCheck(q)]).toEqual([...bytes]);
    });
  }
  it("capacities at level M: 14, 26, 42, 62, 84, 106 bytes; 107 bytes are refused", () => {
    expect([1, 2, 3, 4, 5, 6].map(byteCapacity)).toEqual([14, 26, 42, 62, 84, 106]);
    expect(() => encodeQr(new Uint8Array(107))).toThrow(/too long/);
  });
  it("every forced mask reads back, and the chosen mask has the lowest penalty (ties to the lower number)", () => {
    const scores = [0, 1, 2, 3, 4, 5, 6, 7].map((mask) => {
      const q = encodeQr(PAYLOAD, { mask });
      expect(new TextDecoder().decode(readAndCheck(q))).toBe(PAYLOAD);
      return penalty(q.modules);
    });
    const chosen = encodeQr(PAYLOAD);
    expect(chosen.mask).toBe(scores.indexOf(Math.min(...scores)));
  });
  it("the pairing payload is version 4, at most 62 bytes even with the longest address", () => {
    const longest = pairingPayload("255.255.255.255", 65535, "999999");
    expect(longest.length).toBeLessThanOrEqual(62);
    expect(encodeQr(longest).version).toBe(4);
    expect(new TextDecoder().decode(readAndCheck(encodeQr(PAYLOAD)))).toBe(PAYLOAD);
  });
});

describe("penalty", () => {
  it("an all-light 21x21 matrix: rule 1 798, rule 2 1200, rule 3 0, rule 4 100", () => {
    const m = Array.from({ length: 21 }, () => new Array<boolean>(21).fill(false));
    expect(penalty(m)).toBe(21 * 2 * (3 + 16) + 20 * 20 * 3 + 100);
  });
  it("a checkerboard scores 0 (no runs, no 2x2 blocks, half dark, no finder-like pattern)", () => {
    const checker = Array.from({ length: 21 }, (_, r) => Array.from({ length: 21 }, (_, c) => (r + c) % 2 === 0));
    expect(penalty(checker)).toBe(0);
  });
  it("one 1:1:3:1:1 pattern in a light 21x21 matrix, by hand: rule 1 772, rule 2 1152, rule 3 80, rule 4 90", () => {
    const m = Array.from({ length: 21 }, () => new Array<boolean>(21).fill(false));
    for (const c of [4, 6, 7, 8, 10]) m[10][c] = true; // 0000 1011101 0000…: counted with the light run before and after
    expect(penalty(m)).toBe(772 + 1152 + 80 + 90);
  });
});

describe("renderQr", () => {
  it("two modules per line, a 4-module quiet zone, explicit colours on every cell, a reset at each line end", () => {
    const q = encodeQr(PAYLOAD);
    const lines = renderQr(q);
    const width = renderedWidth(q);
    expect(width).toBe(q.size + 8);
    expect(lines).toHaveLength(Math.ceil(width / 2));
    for (const line of lines) {
      expect([...line.replace(/\x1b\[[0-9;]*m/g, "")].length).toBe(width);
      expect(line.replace(/\x1b\[[0-9;]*m/g, "")).toMatch(/^▀+$/);
      expect(line.startsWith("\x1b[")).toBe(true);
      expect(line.endsWith("\x1b[0m")).toBe(true);
    }
    // The first two lines are quiet zone: light over light only.
    expect(lines[0]).toBe(`\x1b[97;107m${"▀".repeat(width)}\x1b[0m`);
    expect(lines[1]).toBe(lines[0]);
  });
  it("draws the modules: decoding the colours gives the matrix back", () => {
    const q = encodeQr(PAYLOAD);
    const grid: boolean[][] = [];
    for (const line of renderQr(q)) {
      const top: boolean[] = [];
      const bottom: boolean[] = [];
      let fg = false;
      let bg = false;
      for (const part of line.split(/(\x1b\[[0-9;]*m)/)) {
        const sgr = /^\x1b\[(\d+);(\d+)m$/.exec(part);
        if (sgr) {
          fg = sgr[1] === "30";
          bg = sgr[2] === "40";
          continue;
        }
        for (const ch of part) if (ch === "▀") (top.push(fg), bottom.push(bg));
      }
      grid.push(top, bottom);
    }
    const inner = grid.slice(4, 4 + q.size).map((row) => row.slice(4, 4 + q.size));
    expect(inner).toEqual(q.modules);
    for (const row of [...grid.slice(0, 4), ...grid.slice(4 + q.size)]) expect(row.every((x) => !x)).toBe(true);
  });
});

describe("pairing QR code", () => {
  it("payload is exactly marshal://pair?v=1&h=<host>&p=<port>&c=<code>, and nothing else gets in", () => {
    expect(pairingPayload("192.168.1.23", 47821, "048213")).toBe("marshal://pair?v=1&h=192.168.1.23&p=47821&c=048213");
    for (const host of ["my-pc", "fe80::1", "192.168.1.256", "1.2.3", "1.2.3.4&t=x", " 1.2.3.4", "01.2.3.4"]) expect(() => pairingPayload(host, 47821, "123456")).toThrow();
    for (const code of ["12345", "1234567", "12a456", "123456&t=x", ""]) expect(() => pairingPayload("10.0.0.5", 47821, code)).toThrow();
    for (const port of [0, 65536, 1.5, NaN]) expect(() => pairingPayload("10.0.0.5", port, "123456")).toThrow();
  });
  it("private addresses only (RFC 1918), and hostFirst keeps the order", () => {
    expect(hostFirst(["a", "b", "c"], "b")).toEqual(["b", "a", "c"]);
    expect(hostFirst(["a", "a"], null)).toEqual(["a"]);
    expect(["10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.0.1"].every(isPrivateIPv4)).toBe(true);
    expect(["172.15.0.1", "172.32.0.1", "192.169.0.1", "11.0.0.1", "169.254.0.1", "100.64.0.1", "127.0.0.1", "8.8.8.8"].some(isPrivateIPv4)).toBe(false);
  });
  it("shown only on a terminal wide enough, never with --no-qr, never without an address", () => {
    const base = { host: "192.168.1.23", port: 47821, code: "482913", disabled: false };
    const lines = pairingQrLines({ ...base, term: { isTTY: true, columns: 120 } });
    expect(lines.length).toBeGreaterThan(0);
    const width = [...lines[0].replace(/\x1b\[[0-9;]*m/g, "")].length;
    expect(pairingQrLines({ ...base, term: { isTTY: true, columns: width } })).toEqual(lines);
    expect(pairingQrLines({ ...base, term: { isTTY: true, columns: width - 1 } })).toEqual([]);
    expect(pairingQrLines({ ...base, term: { isTTY: true, columns: undefined } })).toEqual([]);
    expect(pairingQrLines({ ...base, term: { isTTY: false, columns: 200 } })).toEqual([]);
    expect(pairingQrLines({ ...base, disabled: true, term: { isTTY: true, columns: 200 } })).toEqual([]);
    expect(pairingQrLines({ ...base, host: null, term: { isTTY: true, columns: 200 } })).toEqual([]);
  });
  it("the shown QR code decodes to the payload with the code printed next to it", () => {
    const lines = pairingQrLines({ host: "10.0.0.5", port: 47821, code: "000042", disabled: false, term: { isTTY: true, columns: 80 } });
    expect(lines).toEqual(renderQr(encodeQr("marshal://pair?v=1&h=10.0.0.5&p=47821&c=000042")));
    expect(new TextDecoder().decode(readAndCheck(encodeQr("marshal://pair?v=1&h=10.0.0.5&p=47821&c=000042")))).toBe("marshal://pair?v=1&h=10.0.0.5&p=47821&c=000042");
  });
  it("the banner says where the QR code is, marks its address, and names why there is none", () => {
    const b = connectBanner({ code: "482913", ips: ["192.168.1.23", "10.0.0.5"], wsPort: 47821, project: "", tool: "claude", temporary: false, global: ["claude"], qrShown: true, qrHost: "192.168.1.23" });
    expect(HEADSET_LOOK).toBe("   In the headset: open Marshal and look at this code.");
    const lines = b.split("\n");
    expect(lines).toContain(HEADSET_LOOK);
    expect(lines).toContain("   Address:   192.168.1.23   (Port 47821)   <- in the QR code");
    expect(lines).toContain("              10.0.0.5   (Port 47821)");
    expect(lines).toContain("   The QR code is below this frame.");
    expect(b).not.toContain("Connect to my PC");
    expect(b).not.toContain("\x1b[");
    const none = connectBanner({ code: "482913", ips: ["100.101.1.2"], wsPort: 47821, project: "", tool: "claude", temporary: false, global: ["claude"], qrShown: false, noQrWhy: "no private LAN address" });
    expect(none).toContain("In the headset: Connect to my PC");
    expect(none).toContain("   No QR code: no private LAN address.");
    expect(none).not.toContain("<- in the QR code");
  });
});

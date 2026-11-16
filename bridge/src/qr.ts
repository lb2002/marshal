// A QR code encoder for the pairing code, written here so the bridge needs no dependency for it (ISO/IEC 18004).
// Scope: byte mode, error correction level M, versions 1 to 6 (at most 106 bytes; the pairing payload is under 60).
// Reed-Solomon over GF(256) with the field polynomial x^8+x^4+x^3+x^2+1 (0x11D), generator roots a^0..a^(n-1).
// The mask is chosen by the standard's four penalty rules (lowest score, ties to the lower mask number).
//
// Coordinates: `modules[row][col]`, true = dark. Nothing here does I/O.

/** Level M, per version: data codewords per block, error correction codewords per block, number of blocks. */
const LEVEL_M: readonly { data: number; ec: number; blocks: number }[] = [
  { data: 0, ec: 0, blocks: 0 }, // no version 0
  { data: 16, ec: 10, blocks: 1 },
  { data: 28, ec: 16, blocks: 1 },
  { data: 44, ec: 26, blocks: 1 },
  { data: 32, ec: 18, blocks: 2 },
  { data: 43, ec: 24, blocks: 2 },
  { data: 27, ec: 16, blocks: 4 },
];

export const MAX_VERSION = 6;
/** Format information: the two bits of level M. */
const LEVEL_M_BITS = 0b00;
/** Versions 2 to 6 have 7 remainder bits after the codewords; version 1 none. */
const remainderBits = (version: number) => (version === 1 ? 0 : 7);

export const sizeOf = (version: number) => 17 + 4 * version;

/** Data codewords (all blocks) of a version at level M. */
export const dataCodewords = (version: number) => LEVEL_M[version].data * LEVEL_M[version].blocks;

/** Bytes that fit in byte mode at level M (mode 4 bits + count 8 bits + data). */
export const byteCapacity = (version: number) => Math.floor((dataCodewords(version) * 8 - 12) / 8);

// ---------------------------------------------------------------- GF(256) and Reed-Solomon

const EXP = new Uint8Array(512);
const LOG = new Uint8Array(256);
{
  let x = 1;
  for (let i = 0; i < 255; i++) {
    EXP[i] = x;
    LOG[x] = i;
    x <<= 1;
    if (x & 0x100) x ^= 0x11d;
  }
  for (let i = 255; i < 512; i++) EXP[i] = EXP[i - 255];
}

/** Product in GF(256). */
export function gfMul(a: number, b: number): number {
  return a === 0 || b === 0 ? 0 : EXP[LOG[a] + LOG[b]];
}

/** a^i in GF(256). */
export const gfPow = (i: number) => EXP[((i % 255) + 255) % 255];

/** The generator polynomial (x - a^0)(x - a^1)…(x - a^(degree-1)), coefficients highest first without the leading 1. */
function generator(degree: number): Uint8Array {
  let g = [1];
  for (let i = 0; i < degree; i++) {
    const next = new Array<number>(g.length + 1).fill(0);
    for (let j = 0; j < g.length; j++) {
      next[j] ^= g[j];
      next[j + 1] ^= gfMul(g[j], gfPow(i));
    }
    g = next;
  }
  return Uint8Array.from(g.slice(1));
}

/** The `degree` error correction codewords of `data` (the remainder of data·x^degree divided by the generator). */
export function rsRemainder(data: ArrayLike<number>, degree: number): Uint8Array {
  const g = generator(degree);
  const rem = new Uint8Array(degree);
  for (let i = 0; i < data.length; i++) {
    const factor = data[i] ^ rem[0];
    rem.copyWithin(0, 1);
    rem[degree - 1] = 0;
    for (let j = 0; j < degree; j++) rem[j] ^= gfMul(g[j], factor);
  }
  return rem;
}

// ---------------------------------------------------------------- codewords

/** Byte mode segment, terminator, bit padding and pad codewords: exactly the data codewords of `version`. */
export function encodeData(bytes: Uint8Array, version: number): Uint8Array {
  if (bytes.length > byteCapacity(version)) throw new Error(`${bytes.length} bytes do not fit version ${version}-M`);
  const capacity = dataCodewords(version) * 8;
  const bits: number[] = [];
  const put = (value: number, n: number) => {
    for (let i = n - 1; i >= 0; i--) bits.push((value >>> i) & 1);
  };
  put(0b0100, 4); // byte mode
  put(bytes.length, 8); // character count, 8 bits for versions 1 to 9
  for (const b of bytes) put(b, 8);
  put(0, Math.min(4, capacity - bits.length)); // terminator
  while (bits.length % 8 !== 0) bits.push(0);
  const out = new Uint8Array(capacity / 8);
  for (let i = 0; i < bits.length; i += 8) out[i / 8] = bits.slice(i, i + 8).reduce((a, b) => (a << 1) | b, 0);
  for (let i = bits.length / 8, pad = 0; i < out.length; i++, pad ^= 1) out[i] = pad === 0 ? 0xec : 0x11;
  return out;
}

/** Splits the data codewords into blocks, adds each block's error correction, and interleaves them. */
export function interleave(data: Uint8Array, version: number): Uint8Array {
  const { data: per, ec, blocks } = LEVEL_M[version];
  const dataBlocks: Uint8Array[] = [];
  const ecBlocks: Uint8Array[] = [];
  for (let b = 0; b < blocks; b++) {
    const block = data.subarray(b * per, (b + 1) * per);
    dataBlocks.push(block);
    ecBlocks.push(rsRemainder(block, ec));
  }
  const out: number[] = [];
  for (let i = 0; i < per; i++) for (const block of dataBlocks) out.push(block[i]);
  for (let i = 0; i < ec; i++) for (const block of ecBlocks) out.push(block[i]);
  return Uint8Array.from(out);
}

// ---------------------------------------------------------------- format information

/** The 15 format bits for level M and `mask`: 5 data bits, BCH(15,5) remainder (0x537), XOR 0x5412. */
export function formatBits(mask: number): number {
  const data = (LEVEL_M_BITS << 3) | mask;
  let rem = data;
  for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
  return ((data << 10) | (rem & 0x3ff)) ^ 0x5412;
}

/** Where the two copies of format bit i (0 = least significant) sit, as [row, col]. */
export function formatPositions(size: number): { first: [number, number][]; second: [number, number][] } {
  const first: [number, number][] = [];
  const second: [number, number][] = [];
  for (let i = 0; i <= 5; i++) first.push([i, 8]);
  first.push([7, 8], [8, 8], [8, 7]);
  for (let i = 9; i < 15; i++) first.push([8, 14 - i]);
  for (let i = 0; i < 8; i++) second.push([8, size - 1 - i]);
  for (let i = 8; i < 15; i++) second.push([size - 15 + i, 8]);
  return { first, second };
}

// ---------------------------------------------------------------- masks and penalty

/** Whether mask `mask` flips the module at (row, col). */
export function maskApplies(mask: number, row: number, col: number): boolean {
  switch (mask) {
    case 0: return (row + col) % 2 === 0;
    case 1: return row % 2 === 0;
    case 2: return col % 3 === 0;
    case 3: return (row + col) % 3 === 0;
    case 4: return (Math.floor(row / 2) + Math.floor(col / 3)) % 2 === 0;
    case 5: return ((row * col) % 2) + ((row * col) % 3) === 0;
    case 6: return (((row * col) % 2) + ((row * col) % 3)) % 2 === 0;
    case 7: return (((row + col) % 2) + ((row * col) % 3)) % 2 === 0;
    default: throw new Error(`no mask ${mask}`);
  }
}

const FINDER_LIKE = [true, false, true, true, true, false, true];

/** The standard's penalty score of a finished matrix (rules N1 = 3, N2 = 3, N3 = 40, N4 = 10). */
export function penalty(m: readonly (readonly boolean[])[]): number {
  const size = m.length;
  const at = (r: number, c: number, transpose: boolean) => (transpose ? m[c][r] : m[r][c]);
  let score = 0;
  for (const t of [false, true]) {
    for (let i = 0; i < size; i++) {
      // Rule 1: five or more of one colour in a row or column: 3 + (length - 5).
      let run = 1;
      for (let j = 1; j <= size; j++) {
        if (j < size && at(i, j, t) === at(i, j - 1, t)) run++;
        else {
          if (run >= 5) score += 3 + (run - 5);
          run = 1;
        }
      }
      // Rule 3: 1:1:3:1:1 dark-light pattern with four light modules on one side, inside the symbol.
      for (let j = 0; j + 11 <= size; j++) {
        const before = [0, 1, 2, 3].every((k) => !at(i, j + k, t)) && FINDER_LIKE.every((d, k) => at(i, j + 4 + k, t) === d);
        const after = FINDER_LIKE.every((d, k) => at(i, j + k, t) === d) && [7, 8, 9, 10].every((k) => !at(i, j + k, t));
        if (before) score += 40;
        if (after) score += 40;
      }
    }
  }
  // Rule 2: every 2x2 block of one colour.
  for (let r = 0; r + 1 < size; r++) {
    for (let c = 0; c + 1 < size; c++) {
      const v = m[r][c];
      if (m[r][c + 1] === v && m[r + 1][c] === v && m[r + 1][c + 1] === v) score += 3;
    }
  }
  // Rule 4: 10 for every full 5 % the dark share is away from 50 %.
  let dark = 0;
  for (const row of m) for (const v of row) if (v) dark++;
  const total = size * size;
  score += 10 * Math.floor(Math.abs(dark * 20 - total * 10) / total);
  return score;
}

// ---------------------------------------------------------------- the matrix

export interface QrCode {
  version: number;
  size: number;
  /** The chosen mask, 0 to 7. */
  mask: number;
  /** `modules[row][col]`, true = dark. */
  modules: boolean[][];
  /** The data codewords before error correction (for tests). */
  data: Uint8Array;
  /** Every codeword in placement order (interleaved data, then interleaved error correction). */
  codewords: Uint8Array;
}

/** Alignment pattern centres per axis for versions 2 to 6 (one pattern each, at the far corner). */
const ALIGN: readonly (readonly number[])[] = [[], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34]];

/** Function patterns (finders with separators, timing, alignment, format area, dark module); `fn` marks them. */
function functionPatterns(version: number): { m: boolean[][]; fn: boolean[][] } {
  const size = sizeOf(version);
  const m = Array.from({ length: size }, () => new Array<boolean>(size).fill(false));
  const fn = Array.from({ length: size }, () => new Array<boolean>(size).fill(false));
  const set = (r: number, c: number, dark: boolean) => {
    m[r][c] = dark;
    fn[r][c] = true;
  };
  for (let i = 0; i < size; i++) {
    set(6, i, i % 2 === 0);
    set(i, 6, i % 2 === 0);
  }
  for (const [cr, cc] of [[3, 3], [3, size - 4], [size - 4, 3]]) {
    for (let dr = -4; dr <= 4; dr++) {
      for (let dc = -4; dc <= 4; dc++) {
        const r = cr + dr;
        const c = cc + dc;
        if (r < 0 || r >= size || c < 0 || c >= size) continue;
        const d = Math.max(Math.abs(dr), Math.abs(dc));
        set(r, c, d !== 2 && d !== 4);
      }
    }
  }
  const centres = ALIGN[version];
  for (const ar of centres) {
    for (const ac of centres) {
      // Skip the three that would overlap a finder.
      if ((ar === 6 && ac === 6) || (ar === 6 && ac === centres[centres.length - 1]) || (ar === centres[centres.length - 1] && ac === 6)) continue;
      for (let dr = -2; dr <= 2; dr++) for (let dc = -2; dc <= 2; dc++) set(ar + dr, ac + dc, Math.max(Math.abs(dr), Math.abs(dc)) !== 1);
    }
  }
  const { first, second } = formatPositions(size);
  for (const [r, c] of [...first, ...second]) set(r, c, false); // reserved, written per mask
  set(size - 8, 8, true); // the dark module
  return { m, fn };
}

/** Places the codewords in the standard zigzag (two columns, up and down, skipping the timing column). */
export function placementOrder(fn: readonly (readonly boolean[])[]): [number, number][] {
  const size = fn.length;
  const out: [number, number][] = [];
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    const upward = ((right + 1) & 2) === 0;
    for (let v = 0; v < size; v++) {
      const row = upward ? size - 1 - v : v;
      for (let j = 0; j < 2; j++) {
        const col = right - j;
        if (!fn[row][col]) out.push([row, col]);
      }
    }
  }
  return out;
}

function writeFormat(m: boolean[][], mask: number): void {
  const bits = formatBits(mask);
  const { first, second } = formatPositions(m.length);
  for (let i = 0; i < 15; i++) {
    const dark = ((bits >>> i) & 1) === 1;
    m[first[i][0]][first[i][1]] = dark;
    m[second[i][0]][second[i][1]] = dark;
  }
}

/** The smallest version (1 to 6) at level M that holds `n` bytes. */
export function versionFor(n: number): number {
  for (let v = 1; v <= MAX_VERSION; v++) if (n <= byteCapacity(v)) return v;
  throw new Error(`${n} bytes are too long for a version ${MAX_VERSION}-M QR code (at most ${byteCapacity(MAX_VERSION)})`);
}

/** Encodes `input` (a string is taken as UTF-8) as a byte mode, level M QR code. `mask` forces one (tests). */
export function encodeQr(input: string | Uint8Array, opts: { mask?: number; version?: number } = {}): QrCode {
  const bytes = typeof input === "string" ? new TextEncoder().encode(input) : input;
  const version = opts.version ?? versionFor(bytes.length);
  if (!Number.isInteger(version) || version < 1 || version > MAX_VERSION) throw new Error(`version must be 1 to ${MAX_VERSION}`);
  const data = encodeData(bytes, version);
  const codewords = interleave(data, version);
  const { m: base, fn } = functionPatterns(version);
  const order = placementOrder(fn);
  const total = codewords.length * 8 + remainderBits(version);
  if (order.length !== total) throw new Error(`internal: ${order.length} free modules for ${total} bits`);
  codewords.forEach((cw, i) => {
    for (let b = 0; b < 8; b++) {
      const [r, c] = order[i * 8 + b];
      base[r][c] = ((cw >>> (7 - b)) & 1) === 1;
    }
  }); // remainder bits stay light
  const masked = (mask: number) => {
    const m = base.map((row, r) => row.map((v, c) => (fn[r][c] ? v : v !== maskApplies(mask, r, c))));
    writeFormat(m, mask);
    return m;
  };
  let best = opts.mask;
  if (best === undefined) {
    let bestScore = Infinity;
    for (let mask = 0; mask < 8; mask++) {
      const s = penalty(masked(mask));
      if (s < bestScore) {
        bestScore = s;
        best = mask;
      }
    }
  }
  if (best === undefined || !Number.isInteger(best) || best < 0 || best > 7) throw new Error("mask must be 0 to 7");
  return { version, size: sizeOf(version), mask: best, modules: masked(best), data, codewords };
}

// ---------------------------------------------------------------- terminal rendering

export const QUIET_ZONE = 4;

/** Width in terminal columns of a rendered code (one column per module, quiet zone on both sides). */
export const renderedWidth = (q: QrCode, quiet = QUIET_ZONE) => q.size + 2 * quiet;

/**
 * The code as terminal lines: one upper half block per two modules (top module = foreground, bottom module =
 * background), dark modules black and light ones bright white, with explicit colours on every cell so it reads
 * the same on dark and light terminal themes. Each line ends with a colour reset.
 */
export function renderQr(q: QrCode, quiet = QUIET_ZONE): string[] {
  const n = q.size + 2 * quiet;
  const dark = (r: number, c: number) => {
    const rr = r - quiet;
    const cc = c - quiet;
    return rr >= 0 && rr < q.size && cc >= 0 && cc < q.size && q.modules[rr][cc];
  };
  const lines: string[] = [];
  for (let r = 0; r < n; r += 2) {
    let line = "";
    let last = "";
    for (let c = 0; c < n; c++) {
      const sgr = `\x1b[${dark(r, c) ? 30 : 97};${dark(r + 1, c) ? 40 : 107}m`;
      if (sgr !== last) line += sgr;
      last = sgr;
      line += "▀";
    }
    lines.push(line + "\x1b[0m");
  }
  return lines;
}

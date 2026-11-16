// Display of a command given as an argument array (`["rm", "-rf", "a b"]`): every element must stay one visible,
// unambiguous word, so the human reads the argv that will run and not a space-joined line that means something
// else. Lexing the result with the classifier's own bash lexer gives back the elements (risk/lexer.ts does not
// decode `$'…'`, see renderArg).

/** Characters shown without quotes: ASCII letters and digits, `_ - . / : = + , @ %`. */
const SAFE_ARG = /^[A-Za-z0-9_\-./:=+,@%]+$/;
/**
 * Characters that are invisible or look like something else: C0/C1 controls and DEL, format characters (bidi
 * overrides, zero-width), line and paragraph separators, every space other than U+0020, and lone surrogates.
 */
const INVISIBLE = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}\u{a0}\u{1680}\u{2000}-\u{200a}\u{202f}\u{205f}\u{3000}\u{d800}-\u{dfff}]/u;
const INVISIBLE_ALL = new RegExp(INVISIBLE.source, "gu");

const NAMED: Record<string, string> = { "\n": "\\n", "\t": "\\t", "\r": "\\r", "\x07": "\\a", "\b": "\\b", "\f": "\\f", "\v": "\\v", "\x1b": "\\e" };

const hex = (n: number, w: number) => n.toString(16).padStart(w, "0");

/** One invisible character as a bash `$'…'` escape. */
function escapeChar(c: string): string {
  const named = NAMED[c];
  if (named !== undefined) return named;
  const code = c.codePointAt(0) ?? 0;
  return code < 0x80 ? "\\x" + hex(code, 2) : code <= 0xffff ? "\\u" + hex(code, 4) : "\\U" + hex(code, 8);
}

/**
 * One element as a word: as it is when every character is in SAFE_ARG; `''` when empty; otherwise in single
 * quotes (`'` becomes `'\''`). An element with an invisible character is written in bash ANSI-C quotes instead,
 * `$'…'`, with `\\`, `\'` and every invisible character as an escape (`\n`, `\e`, `\x01`, `\u202e`), never raw.
 * `first`: the command word, quoted when it contains `=` (it would read as a variable assignment). `quote`: always
 * quoted (a cut element, whose rest is not seen).
 */
export function renderArg(s: string, first = false, quote = false): string {
  if (s === "") return "''";
  if (!quote && SAFE_ARG.test(s) && !(first && s.includes("="))) return s;
  if (!INVISIBLE.test(s)) return "'" + s.replaceAll("'", "'\\''") + "'";
  const body = s.replaceAll("\\", "\\\\").replaceAll("'", "\\'").replace(INVISIBLE_ALL, escapeChar);
  return "$'" + body + "'";
}

/** The array as one line, each element rendered by renderArg and separated by one space. */
export function renderArgv(argv: readonly string[]): string {
  return argv.map((a, i) => renderArg(a, i === 0)).join(" ");
}

/** One invisible character as a display escape: `\n`, `\r`, `\e`, `\x01` for C0 controls and DEL, else `\u{202e}`. */
function displayEscape(c: string): string {
  const named = NAMED[c];
  if (named !== undefined) return named;
  const code = c.codePointAt(0) ?? 0;
  return code < 0x80 ? "\\x" + hex(code, 2) : "\\u{" + code.toString(16) + "}";
}

/**
 * Display text of any field the headset shows (command string, path, URL, tool name): every control, format
 * (bidi, zero-width, tag), line or paragraph separator, odd blank and lone surrogate becomes a visible escape, so
 * the text cannot read differently from what it is. A newline is shown as `\n` too: nothing in the bridge says the
 * headset renders line breaks in a command, and a visible `\n` never hides the statement after it.
 */
export function visibleText(s: string): string {
  return s.replace(INVISIBLE_ALL, displayEscape);
}

/**
 * The first `max` characters of visibleText(s) and its length, for display. Escaping comes first, so a field that
 * grows past `max` by escaping counts as cut. Above MAX_RENDER only the head is escaped, and the length is then
 * the raw length (a lower bound; the field is cut anyway).
 */
export function shownText(s: string, max: number): { text: string; length: number } {
  if (s.length > MAX_RENDER) return { text: visibleText(s.slice(0, max)).slice(0, max), length: s.length };
  const v = visibleText(s);
  return { text: v.slice(0, max), length: v.length };
}

export function isStringArray(v: unknown): v is string[] {
  return Array.isArray(v) && v.every((x) => typeof x === "string");
}

/**
 * Lower bound of renderArgv(argv).length without rendering: every element plus one separator. Cheap for any size
 * (no character is read), so an oversized array is recognised before it is rendered.
 */
export function argvMinLength(argv: readonly string[]): number {
  let n = argv.length > 0 ? argv.length - 1 : 0;
  for (const a of argv) n += a.length === 0 ? 2 : a.length;
  return n;
}

/** Above this lower bound (argvMinLength) an array is not rendered in full: only its head is shown. */
export const MAX_RENDER = 64 * 1024;

/**
 * The first `max` characters of renderArgv(argv), without rendering the rest: an element longer than `max` is
 * cut first and then always quoted. For arrays of any size; reads at most about `max` characters per element shown.
 */
export function renderArgvHead(argv: readonly string[], max: number): string {
  let out = "";
  for (let i = 0; i < argv.length && out.length < max; i++) {
    const a = argv[i];
    const cut = a.length > max;
    out += (i > 0 ? " " : "") + renderArg(cut ? a.slice(0, max) : a, i === 0, cut);
  }
  return out.slice(0, max);
}

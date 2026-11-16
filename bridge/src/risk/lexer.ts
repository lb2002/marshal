// Quote-aware shell lexer for the risk classifier.
// Single pass, linear time, no regular expressions. It follows bash rules
// (mode "bash"), with two reduced dialects for PowerShell ("pwsh") and cmd.exe
// ("cmd") payloads. It never executes or expands anything: expansions are kept
// as source text and the word is flagged `dyn`.

export type Mode = "bash" | "pwsh" | "cmd";

export interface Word {
  /** Text after quote removal; expansions stay as their source text. */
  text: string;
  /** Contains an active expansion: `$`, backtick, substitution, brace or bracket pattern, cmd `%`. */
  dyn: boolean;
  /** Contains an unquoted `*` or `?`. */
  glob: boolean;
  /** Contained any quoting or escaping. */
  quoted: boolean;
  /** Contains quoted (not yet active) substitution syntax such as '$(x)', a stored payload. */
  inert: boolean;
}

export interface Redirect {
  op: string; // > >> >| < <> >& <& &> &>>
  fd: string; // "" when absent
  target: Word;
}

export interface Heredoc {
  op: "<<" | "<<-" | "<<<";
  quoted: boolean;
  body: string;
}

export interface Substitution {
  kind: "$(" | "`" | "<(" | ">(";
  text: string;
  /**
   * The segments the lexer parsed for `$(...)`, `<(...)`, `>(...)`, in their original context. Classify these
   * instead of re-lexing `text`: some parses (a heredoc ending at `DELIM ... )`) depend on the enclosing `)`.
   * Absent for backticks, whose content bash itself re-parses.
   */
  segments?: Segment[];
}

/** One simple command. */
export interface Segment {
  words: Word[];
  redirects: Redirect[];
  heredocs: Heredoc[];
  substitutions: Substitution[];
  /** stdin comes from the previous segment through `|`. */
  pipedIn: boolean;
  /** `name()` function definition. */
  funcDef: boolean;
  /** Elements of a compound array assignment `a=( ... )`: data, not a command (substitutions still run). */
  compound?: boolean;
}

/** Lexing failed closed: nesting too deep, or syntax the lexer cannot model safely (`complex`). */
export class LexError extends Error {
  constructor(
    message: string,
    readonly complex = false,
  ) {
    super(message);
  }
}

/** `NAME=`, `NAME+=`, `NAME[...]=`, `NAME[...]+=` (an assignment word keeps the command position open). */
function isAssignmentWord(t: string): boolean {
  if (t === "" || !(isNameChar(t[0]) && !isDigit(t[0]))) return false;
  let k = 1;
  while (k < t.length && isNameChar(t[k])) k++;
  if (t[k] === "[") {
    let depth = 0;
    for (; k < t.length; k++) {
      if (t[k] === "[") depth++;
      else if (t[k] === "]" && --depth === 0) break;
    }
    if (k >= t.length) return false;
    k++;
  }
  if (t[k] === "+") k++;
  return t[k] === "=";
}

/** Reserved words after which bash is still at command position (assignments and subscripts allowed). */
const COMMAND_POSITION_WORDS = new Set(["{", "!", "if", "then", "else", "elif", "do", "while", "until", "time"]);
/** Builtins whose `NAME=(...)` arguments are compound array assignments. */
const DECLARERS = new Set(["declare", "typeset", "local", "export", "readonly"]);

const MAX_NEST = 32;
const MAX_ARITH_FAILURES = 64;

const isBlank = (c: string) => c === " " || c === "\t" || c === "\r" || c === "\f" || c === "\v";
const isDigit = (c: string) => c >= "0" && c <= "9";
const isNameChar = (c: string) => (c >= "a" && c <= "z") || (c >= "A" && c <= "Z") || isDigit(c) || c === "_";
const SPECIAL_PARAMS = "@*#?$!-0123456789";
/** Unquoted shell metacharacters that make bash split a glob-mode subscript word (`<(`/`>(` included). */
const SUBSCRIPT_METACHARS = "<>();&|";

function newSegment(pipedIn: boolean): Segment {
  return { words: [], redirects: [], heredocs: [], substitutions: [], pipedIn, funcDef: false };
}

class WordBuilder {
  text = "";
  started = false;
  dyn = false;
  glob = false;
  quoted = false;
  inert = false;
  /** The word starts at command position, where `NAME[` opens an array subscript (bash matched pair). */
  assignPos = false;
  /** The word is an element of a compound array assignment, where a leading `[` opens a subscript. */
  compoundElem = false;
  private braceAt = -1;
  private bracketOpen = false;
  private identOk = true; // every character so far is a name character (and the first is not a digit)

  add(s: string) {
    if (this.identOk) {
      for (let k = 0; k < s.length; k++) {
        const ch = s[k];
        if (!isNameChar(ch) || (this.text.length === 0 && k === 0 && isDigit(ch))) {
          this.identOk = false;
          break;
        }
      }
    }
    this.text += s;
    this.started = true;
  }
  /** The word so far is a valid shell NAME (O(1); tracked incrementally). */
  get isIdent(): boolean {
    return this.identOk && this.text.length > 0;
  }
  /** Unquoted character that may carry glob or brace meaning. */
  addBare(c: string, mode: Mode) {
    if (mode !== "cmd") {
      if (c === "*" || c === "?") this.glob = true;
      else if (c === "[") this.bracketOpen = true;
      else if (c === "]" && this.bracketOpen) {
        this.dyn = true; // glob bracket expression
        this.bracketOpen = false;
      }
      else if (c === "{" && mode === "bash") this.braceAt = this.text.length;
      else if (c === "}" && this.braceAt >= 0) {
        const inner = this.text.slice(this.braceAt);
        if (inner.includes(",") || inner.includes("..")) this.dyn = true;
        this.braceAt = -1;
      }
    }
    this.add(c);
  }
  build(): Word {
    return { text: this.text, dyn: this.dyn, glob: this.glob, quoted: this.quoted, inert: this.inert };
  }
}

interface PendingHeredoc {
  delim: string;
  strip: boolean;
  doc: Heredoc;
  seg: Segment;
}

class Lexer {
  pos = 0;
  private nest = 0;
  private substDepth = 0; // inside one or more $(...) / <(...) / >(...)
  // Each failed `((`/`$((` arithmetic attempt costs a scan; bound them so ambiguous input stays linear.
  private arithFailures = 0;
  // The last readArith saw an unquoted `;`, `&`, `|` or newline at its own level (not inside quotes/substitutions).
  private arithSeparator = false;
  // The last subscript readArith (close `]`) saw, at its own level, an unquoted blank, `$'` or metacharacter.
  private arithSubscriptRisk = false;
  private pending: PendingHeredoc[] = [];

  constructor(
    private readonly src: string,
    private readonly mode: Mode,
  ) {}

  private enter() {
    if (++this.nest > MAX_NEST) throw new LexError("nesting too deep");
  }
  private leave() {
    this.nest--;
  }

  /** Lex a command list until end of input, or until an unbalanced `)` when `inParen` (left unconsumed). */
  list(inParen: boolean): Segment[] {
    const { src, mode } = this;
    const segs: Segment[] = [];
    let seg = newSegment(false);
    let wb = new WordBuilder();
    let pendingRedirect: { op: string; fd: string } | null = null;
    let depth = 0;
    let inTest = false; // inside [[ ... ]], < and > compare strings
    let cmdSeen = false; // a command word has been read in this segment (no longer command position)
    let compoundDepth = 0; // paren depth of an open compound array assignment `a=( ... )`, 0 when none

    const finishWord = () => {
      if (!wb.started) return;
      const w = wb.build();
      wb = new WordBuilder();
      if (!w.quoted && !pendingRedirect) {
        if (w.text === "[[" && seg.words.length === 0) inTest = true;
        else if (w.text === "]]") inTest = false;
      }
      if (pendingRedirect) {
        this.attachRedirect(seg, pendingRedirect, w);
        pendingRedirect = null;
      } else {
        seg.words.push(w);
        if (w.quoted || !(isAssignmentWord(w.text) || COMMAND_POSITION_WORDS.has(w.text))) cmdSeen = true;
      }
    };
    /** The current word is the unquoted `]]` that closes a `[[ ... ]]` test. */
    const closesTest = () => inTest && wb.started && !wb.quoted && wb.text === "]]";
    const endSegment = (pipe: boolean) => {
      finishWord();
      if (pendingRedirect) {
        this.attachRedirect(seg, pendingRedirect, { text: "", dyn: false, glob: false, quoted: false, inert: false });
        pendingRedirect = null;
      }
      if (seg.words.length || seg.redirects.length || seg.heredocs.length || seg.substitutions.length || seg.funcDef) segs.push(seg);
      seg = newSegment(pipe);
      if (compoundDepth > 0) seg.compound = true; // further elements (after a newline) are still data
      inTest = false;
      cmdSeen = false;
    };
    const startRedirect = (op: string) => {
      let fd = "";
      if (wb.started && !wb.quoted && (wb.text === "*" || (wb.text !== "" && [...wb.text].every(isDigit)))) {
        fd = wb.text;
        wb = new WordBuilder();
      } else finishWord();
      if (pendingRedirect) this.attachRedirect(seg, pendingRedirect, { text: "", dyn: false, glob: false, quoted: false, inert: false });
      pendingRedirect = { op, fd };
    };

    while (this.pos < src.length) {
      const c = src[this.pos];
      const n = src[this.pos + 1];

      if (isBlank(c)) {
        finishWord();
        this.pos++;
        continue;
      }
      if (c === "\n") {
        endSegment(false);
        this.pos++;
        this.readHeredocBodies();
        continue;
      }
      if (c === "#" && !wb.started && mode !== "cmd") {
        while (this.pos < src.length && src[this.pos] !== "\n") this.pos++;
        continue;
      }

      // ---- operators
      // A `]]` glued to the next operator closes the test first: `[[ 1 ]]&&cmd`, `([[ 1 ]])`, `[[ 1 ]]>f`.
      if (closesTest() && (c === "&" || c === "|" || c === "(" || c === ")" || c === "<" || c === ">")) finishWord();
      if (inTest && (c === "&" || c === "|")) {
        // `&&` / `||` inside [[ ... ]] are conditional operators, not command separators
        finishWord();
        let k = this.pos + 1;
        while (k < src.length && src[k] === c) k++;
        seg.words.push({ text: src.slice(this.pos, k), dyn: false, glob: false, quoted: false, inert: false });
        this.pos = k;
        continue;
      }
      if (c === ";") {
        endSegment(false);
        this.pos += n === ";" ? (src[this.pos + 2] === "&" ? 3 : 2) : n === "&" ? 2 : 1;
        continue;
      }
      if (c === "|") {
        if (n === "|") {
          endSegment(false);
          this.pos += 2;
        } else {
          endSegment(true);
          this.pos += n === "&" ? 2 : 1;
        }
        continue;
      }
      if (c === "&") {
        if (n === "&") {
          endSegment(false);
          this.pos += 2;
        } else if (n === ">" && mode === "bash") {
          startRedirect(src[this.pos + 2] === ">" ? "&>>" : "&>");
          this.pos += src[this.pos + 2] === ">" ? 3 : 2;
        } else if (mode === "pwsh" && !wb.started) {
          // call operator: keep it as a word
          seg.words.push({ text: "&", dyn: false, glob: false, quoted: false, inert: false });
          this.pos++;
        } else {
          endSegment(false);
          this.pos++;
        }
        continue;
      }
      if ((c === "<" || c === ">") && inTest) {
        finishWord(); // comparison operator inside [[ ... ]], not a redirect
        seg.words.push({ text: c, dyn: false, glob: false, quoted: false, inert: false });
        this.pos++;
        continue;
      }
      if ((c === "(" || c === ")") && inTest) {
        wb.addBare(c, mode); // grouping inside [[ ... ]], plain text
        this.pos++;
        continue;
      }
      if (c === "(" && n === "(" && mode === "bash" && !wb.started) {
        // `(( ... ))` is arithmetic only when it closes with an adjacent `))`; otherwise bash re-parses it as
        // nested subshells, which the `(` handler below does for the first paren.
        const start = this.pos;
        this.pos += 2;
        if (this.readArith(seg, ")", 2)) continue;
        this.pos = start;
      }
      if (c === "<" || c === ">") {
        if (mode === "bash" && n === "(") {
          this.pos += 2;
          this.readSubstitution(c === "<" ? "<(" : ">(", seg, wb);
          continue;
        }
        if (mode === "pwsh" && c === "<" && n === "#") {
          const end = src.indexOf("#>", this.pos + 2);
          this.pos = end < 0 ? src.length : end + 2;
          continue;
        }
        if (c === "<") {
          if (mode === "bash" && n === "<") {
            if (src[this.pos + 2] === "<") {
              startRedirect("<<<");
              this.pos += 3;
            } else if (src[this.pos + 2] === "-") {
              startRedirect("<<-");
              this.pos += 3;
            } else {
              startRedirect("<<");
              this.pos += 2;
            }
          } else if (n === "&") {
            startRedirect("<&");
            this.pos += 2;
          } else if (n === ">") {
            startRedirect("<>");
            this.pos += 2;
          } else {
            startRedirect("<");
            this.pos++;
          }
        } else if (n === ">") {
          startRedirect(">>");
          this.pos += 2;
        } else if (n === "&") {
          startRedirect(">&");
          this.pos += 2;
        } else if (n === "|") {
          startRedirect(">|");
          this.pos += 2;
        } else {
          startRedirect(">");
          this.pos++;
        }
        continue;
      }
      if (c === "(") {
        if (mode === "pwsh" && wb.started && wb.text.startsWith("$")) wb.add("("); // `$x.Method(`: mark the call
        // `NAME=(`, `NAME+=(`, `NAME[i]=(` in assignment position or as a declare/export/local argument:
        // a compound array assignment. Its elements are data; a leading `[` is a matched-pair subscript.
        const compound =
          mode === "bash" && compoundDepth === 0 && wb.started && !wb.quoted && wb.text.endsWith("=") &&
          isAssignmentWord(wb.text) && (!cmdSeen || DECLARERS.has(seg.words[0]?.text ?? ""));
        finishWord();
        if (compound) {
          endSegment(false);
          depth++;
          compoundDepth = depth;
          seg.compound = true;
          this.pos++;
          continue;
        }
        if (mode === "bash" && seg.words.length === 1 && !pendingRedirect) {
          let k = this.pos + 1;
          while (k < src.length && isBlank(src[k])) k++;
          if (src[k] === ")") {
            seg.funcDef = true;
            endSegment(false);
            this.pos = k + 1;
            continue;
          }
        }
        endSegment(false);
        depth++;
        this.pos++;
        continue;
      }
      if (c === ")") {
        if (depth > 0) {
          depth--;
          if (compoundDepth > depth) compoundDepth = 0; // compound assignment closed
          endSegment(false);
          this.pos++;
          continue;
        }
        if (inParen) {
          endSegment(false);
          return segs;
        }
        endSegment(false);
        this.pos++;
        continue;
      }
      if (mode === "pwsh" && (c === "{" || c === "}")) {
        endSegment(false);
        this.pos++;
        continue;
      }

      if (!wb.started) {
        // Redirect operands and heredoc delimiters never take a subscript (findings-6 item 1).
        wb.assignPos = !cmdSeen && !inTest && compoundDepth === 0 && pendingRedirect === null;
        wb.compoundElem = compoundDepth > 0;
      }
      this.readWordPart(wb, seg);
    }
    endSegment(false);
    if (this.pending.length) this.readHeredocBodies();
    // bash reads an unterminated compound assignment to EOF (syntax error); fail closed instead of
    // treating the rest of the input as data.
    if (compoundDepth > 0) throw new LexError("unterminated compound assignment", true);
    return segs;
  }

  private attachRedirect(seg: Segment, r: { op: string; fd: string }, target: Word) {
    if (r.op === "<<" || r.op === "<<-") {
      const doc: Heredoc = { op: r.op, quoted: target.quoted, body: "" };
      seg.heredocs.push(doc);
      this.pending.push({ delim: target.text, strip: r.op === "<<-", doc, seg });
    } else if (r.op === "<<<") {
      seg.heredocs.push({ op: "<<<", quoted: target.quoted, body: target.text });
    } else {
      seg.redirects.push({ op: r.op, fd: r.fd, target });
    }
  }

  /** Read the bodies of all pending heredocs, starting at the current position (after a newline). */
  private readHeredocBodies() {
    const { src } = this;
    const pending = this.pending;
    this.pending = [];
    for (const h of pending) {
      let body = "";
      while (this.pos < src.length) {
        const lineStart = this.pos;
        const { logical, joined } = this.readHeredocLine(h.doc.quoted);
        let cmp = logical;
        let tabCount = 0;
        if (h.strip) {
          while (tabCount < cmp.length && cmp[tabCount] === "\t") tabCount++;
          cmp = cmp.slice(tabCount);
        }
        if (cmp === h.delim) break;
        if (!joined && this.substDepth > 0 && cmp.startsWith(h.delim)) {
          const rest = cmp.slice(h.delim.length);
          // `DELIM)` ends the heredoc and closes the substitution (all bash versions).
          if (rest.startsWith(")")) {
            this.pos = lineStart + tabCount + h.delim.length;
            break;
          }
          // `DELIM ... )`: bash 5.2 ends the heredoc here and runs the rest of the line, older bash does not.
          if (rest.includes(")")) throw new LexError("ambiguous heredoc end in substitution", true);
        }
        body += logical + "\n";
      }
      h.doc.body = body;
      if (!h.doc.quoted) {
        // Unquoted heredoc bodies undergo parameter and command substitution.
        const sub = new Lexer(body, this.mode);
        sub.nest = this.nest;
        sub.readDoubleQuoted(new WordBuilder(), h.seg, null);
      }
    }
  }

  /** Read one heredoc body line, joining backslash-newline continuations for unquoted delimiters (bash semantics). */
  private readHeredocLine(quoted: boolean): { logical: string; joined: boolean } {
    const { src } = this;
    let logical = "";
    let joined = false;
    for (;;) {
      let nl = src.indexOf("\n", this.pos);
      if (nl < 0) nl = src.length;
      let raw = src.slice(this.pos, nl);
      if (raw.endsWith("\r")) raw = raw.slice(0, -1);
      this.pos = Math.min(nl + 1, src.length);
      if (!quoted && nl < src.length) {
        let b = 0;
        while (b < raw.length && raw[raw.length - 1 - b] === "\\") b++;
        if (b % 2 === 1) {
          logical += raw.slice(0, -1);
          joined = true;
          continue;
        }
      }
      logical += raw;
      return { logical, joined };
    }
  }

  /** Read one word fragment (quoted string, escape, expansion or plain character). */
  private readWordPart(wb: WordBuilder, seg: Segment) {
    const { src, mode } = this;
    const c = src[this.pos];
    const n = src[this.pos + 1];

    const escape = mode === "bash" ? "\\" : mode === "pwsh" ? "`" : "^";
    if (c === escape) {
      if (n === "\n") {
        this.pos += 2; // line continuation
        return;
      }
      if (n === "\r" && src[this.pos + 2] === "\n") {
        this.pos += 3;
        return;
      }
      if (n === undefined) {
        wb.add(c);
        this.pos++;
        return;
      }
      wb.quoted = true;
      wb.add(n);
      this.pos += 2;
      return;
    }
    if (c === "'" && mode !== "cmd") {
      wb.quoted = true;
      wb.started = true;
      const end = src.indexOf("'", this.pos + 1);
      const stop = end < 0 ? src.length : end;
      const lit = src.slice(this.pos + 1, stop);
      if (lit.includes("$(") || lit.includes("`")) wb.inert = true;
      wb.add(lit);
      this.pos = end < 0 ? src.length : end + 1;
      return;
    }
    if (c === '"') {
      wb.quoted = true;
      wb.started = true;
      this.pos++;
      if (mode === "cmd") {
        const end = src.indexOf('"', this.pos);
        const stop = end < 0 ? src.length : end;
        this.addCmdText(wb, src.slice(this.pos, stop));
        this.pos = end < 0 ? src.length : end + 1;
      } else this.readDoubleQuoted(wb, seg, '"');
      return;
    }
    if (c === "$" && mode !== "cmd") {
      this.readDollar(wb, seg, false);
      return;
    }
    if (c === "@" && n === "(" && mode === "pwsh") {
      this.pos += 2;
      this.readSubstitution("$(", seg, wb);
      return;
    }
    if (c === "`" && mode === "bash") {
      this.readBacktick(wb, seg);
      return;
    }
    if (c === "%" && mode === "cmd") {
      wb.dyn = true;
      wb.add(c);
      this.pos++;
      return;
    }
    if (
      c === "[" &&
      mode === "bash" &&
      ((wb.assignPos && !wb.quoted && wb.isIdent) || (wb.compoundElem && !wb.started))
    ) {
      // `NAME[` at command position: bash reads the subscript as a matched pair (blanks, `<`, `>`, `;`
      // included) up to the matching `]`. Unterminated: bash reads to EOF; fail closed.
      const start = this.pos;
      this.pos++;
      if (!this.readArith(seg, "]", 1)) throw new LexError("unterminated array subscript", true);
      // Where bash keeps `[` a glob char, these would split commands; we do not model that, so fail closed.
      if (this.arithSeparator) throw new LexError("separator in array subscript", true);
      // Blanks (also ` #`), `$'...'` and `< > ( ) ; & |` change how bash splits a glob-mode subscript; fail closed.
      if (this.arithSubscriptRisk) throw new LexError("blank, $' or metacharacter in array subscript", true);
      wb.dyn = true;
      wb.add(this.src.slice(start, this.pos));
      return;
    }
    wb.addBare(c, mode);
    this.pos++;
  }

  private addCmdText(wb: WordBuilder, s: string) {
    if (s.includes("%") || s.includes("!")) wb.dyn = true;
    wb.add(s);
  }

  /**
   * Content of a double-quoted string (terminator `"`), or of an unquoted heredoc body (terminator null).
   */
  readDoubleQuoted(wb: WordBuilder, seg: Segment, terminator: '"' | null) {
    const { src, mode } = this;
    const escape = mode === "pwsh" ? "`" : "\\";
    while (this.pos < src.length) {
      const c = src[this.pos];
      if (c === terminator) {
        this.pos++;
        return;
      }
      if (c === escape) {
        const n = src[this.pos + 1];
        if (n === undefined) {
          wb.add(c);
          this.pos++;
          continue;
        }
        if (mode === "pwsh") {
          wb.add(n);
        } else if (n === "\n") {
          // continuation: removed
        } else if (n === "$" || n === "`" || n === "\\" || (n === '"' && terminator === '"')) {
          if (n === "`" || (n === "$" && src[this.pos + 2] === "(")) wb.inert = true;
          wb.add(n);
        } else {
          wb.add(c + n);
        }
        this.pos += 2;
        continue;
      }
      if (c === "$") {
        this.readDollar(wb, seg, true);
        continue;
      }
      if (c === "`" && mode === "bash") {
        this.readBacktick(wb, seg);
        continue;
      }
      wb.add(c);
      this.pos++;
    }
  }

  /** `$...` at the current position. */
  private readDollar(wb: WordBuilder, seg: Segment, inDq: boolean) {
    const { src, mode } = this;
    const start = this.pos;
    const n = src[this.pos + 1];
    if (n === "(") {
      if (mode === "bash" && src[this.pos + 2] === "(") {
        this.readArithmetic(wb, seg);
        return;
      }
      this.pos += 2;
      this.readSubstitution("$(", seg, wb);
      return;
    }
    if (n === "{") {
      this.pos += 2;
      this.readBraceParam(seg);
      wb.dyn = true;
      wb.add(src.slice(start, this.pos));
      return;
    }
    if (n === "[" && mode === "bash") {
      // legacy arithmetic `$[ ... ]`: no heredocs, `<`/`>` are operators
      this.pos += 2;
      if (!this.readArith(seg, "]", 1)) throw new LexError("unterminated $[", true);
      wb.dyn = true;
      wb.add(src.slice(start, this.pos));
      return;
    }
    if (mode === "bash" && n === "'" && !inDq) {
      // ANSI-C quoting: decoded by bash, treated as non-literal here.
      let k = this.pos + 2;
      while (k < src.length && src[k] !== "'") k += src[k] === "\\" ? 2 : 1;
      wb.quoted = true;
      wb.dyn = true;
      wb.inert = true;
      wb.add(src.slice(this.pos + 2, Math.min(k, src.length)));
      this.pos = Math.min(k + 1, src.length);
      return;
    }
    if (mode === "bash" && n === '"' && !inDq) {
      this.pos++; // locale string: same as "..."
      return;
    }
    if (n !== undefined && (isNameChar(n) || (mode === "bash" && SPECIAL_PARAMS.includes(n)))) {
      let k = this.pos + 2; // $0..$9 and special parameters are one character
      if (!(mode === "bash" && SPECIAL_PARAMS.includes(n))) {
        while (k < src.length && (isNameChar(src[k]) || (mode === "pwsh" && src[k] === ":"))) k++;
      }
      wb.dyn = true;
      wb.add(src.slice(this.pos, k));
      this.pos = k;
      return;
    }
    wb.add("$");
    this.pos++;
  }

  /** After `$(`, `<(`, `>(` or `@(`: lex the inner list and record it, with its parsed segments. */
  private readSubstitution(kind: Substitution["kind"], seg: Segment, wb: WordBuilder) {
    const open = this.pos;
    this.enter();
    this.substDepth++;
    const segments = this.list(true);
    this.substDepth--;
    this.leave();
    const text = this.src.slice(open, this.pos);
    if (this.src[this.pos] === ")") this.pos++;
    seg.substitutions.push({ kind, text, segments });
    wb.dyn = true;
    wb.add((kind === "$(" ? "$(" : kind) + text + ")");
  }

  /**
   * `$((...))`: arithmetic when it closes with an adjacent `))` (`<`/`>` are operators, never heredocs;
   * embedded command substitutions still run). Otherwise bash parses `$( (...) ... )` as a command
   * substitution, so take that path.
   */
  private readArithmetic(wb: WordBuilder, seg: Segment) {
    const start = this.pos;
    this.pos += 3; // past `$((`
    if (this.readArith(seg, ")", 2)) {
      wb.dyn = true;
      wb.add(this.src.slice(start, this.pos));
      return;
    }
    this.pos = start + 2; // past `$(`
    this.readSubstitution("$(", seg, wb);
  }

  /**
   * Read an arithmetic expression or array subscript as raw text up to the matching close. `<`/`>` are plain
   * operators and no heredoc is opened; embedded `$(...)`/backticks/`${...}` are still extracted. For `((` /
   * `$((` (close `)`, depth 2) the expression must end with an adjacent `))`: when the inner group closes and
   * the next character is not `)`, bash re-parses as subshells / command substitution, so this returns false.
   * On false (also at end of input) the position and every recorded substitution/heredoc are rolled back.
   */
  private readArith(seg: Segment, close: ")" | "]", depth: number): boolean {
    const { src, mode } = this;
    const start = this.pos;
    const subs = seg.substitutions.length;
    const docs = seg.heredocs.length;
    const pending = this.pending.slice();
    const needAdjacent = close === ")" && depth === 2;
    const open = close === ")" ? "(" : "[";
    const scratch = new WordBuilder();
    let sep = false;
    let risk = false;
    this.enter();
    try {
      while (this.pos < src.length) {
        const c = src[this.pos];
        if (c === ";" || c === "&" || c === "|" || c === "\n") sep = true;
        const n = src[this.pos + 1];
        if (close === "]" && (isBlank(c) || (c === "$" && n === "'") || SUBSCRIPT_METACHARS.includes(c))) risk = true;
        if (c === open) {
          depth++;
          this.pos++;
        } else if (c === close) {
          depth--;
          this.pos++;
          if (depth === 0) return true;
          if (needAdjacent && depth === 1) {
            if (src[this.pos] !== ")") break; // `((cmd) ...`: nested subshells
            this.pos++;
            return true;
          }
        } else if (c === "'") {
          const e = src.indexOf("'", this.pos + 1);
          this.pos = e < 0 ? src.length : e + 1;
        } else if (c === '"') {
          this.pos++;
          this.readDoubleQuoted(scratch, seg, '"');
        } else if (c === "$") {
          this.readDollar(scratch, seg, true);
        } else if (c === "`" && mode === "bash") {
          this.readBacktick(scratch, seg);
        } else if (c === "\\") {
          this.pos += 2;
        } else {
          this.pos++;
        }
      }
    } finally {
      this.leave();
      this.arithSeparator = sep; // set last, after any nested readArith inside this one
      this.arithSubscriptRisk = risk;
    }
    this.pos = start;
    seg.substitutions.length = subs;
    seg.heredocs.length = docs;
    this.pending = pending;
    if (++this.arithFailures > MAX_ARITH_FAILURES) throw new LexError("ambiguous (( nesting", true);
    return false;
  }

  /** After `${`: read to the matching `}`, extracting nested substitutions. */
  private readBraceParam(seg: Segment) {
    const { src } = this;
    let depth = 0;
    const scratch = new WordBuilder();
    while (this.pos < src.length) {
      const c = src[this.pos];
      if (c === "}") {
        this.pos++;
        if (depth === 0) return;
        depth--;
        continue;
      }
      if (c === "{") {
        depth++;
        this.pos++;
        continue;
      }
      if (c === "\\") {
        this.pos += 2;
        continue;
      }
      if (c === "'") {
        const end = src.indexOf("'", this.pos + 1);
        this.pos = end < 0 ? src.length : end + 1;
        continue;
      }
      if (c === '"') {
        this.pos++;
        this.readDoubleQuoted(scratch, seg, '"');
        continue;
      }
      if (c === "$") {
        this.readDollar(scratch, seg, true);
        continue;
      }
      if (c === "`" && this.mode === "bash") {
        this.readBacktick(scratch, seg);
        continue;
      }
      this.pos++;
    }
  }

  /** Backtick command substitution (bash). */
  private readBacktick(wb: WordBuilder, seg: Segment) {
    const { src } = this;
    let k = this.pos + 1;
    let text = "";
    while (k < src.length && src[k] !== "`") {
      if (src[k] === "\\" && (src[k + 1] === "`" || src[k + 1] === "\\" || src[k + 1] === "$")) {
        text += src[k + 1];
        k += 2;
      } else text += src[k++];
    }
    seg.substitutions.push({ kind: "`", text });
    wb.dyn = true;
    wb.add("`" + text + "`");
    this.pos = Math.min(k + 1, src.length);
  }
}

/**
 * Characters read as a line break in every dialect: CR LF, CR (PowerShell ends a statement at a bare CR, tested),
 * VT, FF, NEL, U+2028 and U+2029 (no shell on the test machine breaks there, PowerShell 7 was not tested; read as
 * a break, so a command after them is never hidden). Inside quotes they stay part of the word, as a newline does.
 */
const LINE_BREAKS = /\r\n?|[\v\f\u{85}\u{2028}\u{2029}]/gu;

/** Split a command string into simple commands. Throws LexError when nesting is too deep. */
export function lex(src: string, mode: Mode = "bash"): Segment[] {
  return new Lexer(src.replace(LINE_BREAKS, "\n"), mode).list(false);
}

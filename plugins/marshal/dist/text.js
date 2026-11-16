// Display-text cleaning shared by the explainer, the impact check and the feeds. No imports on purpose: the
// impact check must not pull in anything that can start a process (explain.ts does).
// Control, format (bidi, zero-width), private-use, unassigned and surrogate code points, plus
// blank-looking fillers (braille blank, hangul fillers) and variation selectors.
const INVISIBLE = /[\p{Cc}\p{Cf}\p{Co}\p{Cn}\p{Cs}\u2800\u3164\u115F\u1160\uFFA0\u034F\u17B4\u17B5\u180B-\u180D\uFE00-\uFE0F\u{E0100}-\u{E01EF}]/gu;
/** The text with line breaks as blanks and every invisible character (INVISIBLE) removed; length is not capped. */
export function stripInvisible(s) {
    return s.replace(/[\r\n\t\u{2028}\u{2029}]+/gu, " ").replace(INVISIBLE, "");
}
/** Single line without invisible characters, at most `max` code points; longer text is cut at the last word boundary plus "…". */
export function cleanText(s, max) {
    const text = s.replace(/[\r\n\t\u2028\u2029]+/g, " ").replace(INVISIBLE, "").replace(/\s+/g, " ").trim();
    const chars = [...text];
    if (chars.length <= max)
        return text;
    let cut = chars.slice(0, max - 1).join("");
    const space = cut.lastIndexOf(" ");
    if (chars[max - 1] !== " " && space > 0)
        cut = cut.slice(0, space);
    return cut.trimEnd().replace(/[,;:\-–]+$/, "") + "…";
}

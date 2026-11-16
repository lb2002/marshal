// The product name comes from one constant (src/product.ts). hook-fallback.ts must not import anything, so it spells
// the name out: this keeps it in step, and checks a few texts that take the constant.
import { describe, expect, it } from "vitest";
import { PRODUCT_NAME } from "../src/product.js";
import { MUSE_BRIDGE_OFF_DENY, MUSE_NO_DECISION_DENY } from "../src/hook-fallback.js";
import { R } from "../src/impact.js";

describe("product name", () => {
  it("is Marshal", () => {
    expect(PRODUCT_NAME).toBe("Marshal");
  });

  it("the fallback denies spell the same name", () => {
    for (const text of [MUSE_NO_DECISION_DENY, MUSE_BRIDGE_OFF_DENY]) {
      const message = (JSON.parse(text) as { hookSpecificOutput: { decision: { message: string } } }).hookSpecificOutput.decision.message;
      expect(message.startsWith(`${PRODUCT_NAME}`)).toBe(true);
      expect(message).not.toMatch(/Lotse/);
    }
  });

  it("the impact sentence names it", () => {
    expect(R.inGit).toBe(`In a Git project. ${PRODUCT_NAME} does not check whether everything is saved.`);
  });
});

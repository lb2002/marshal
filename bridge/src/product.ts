// The product name as users read it. Every user-facing text that names the product takes it from here, so a
// rename is one change. No imports on purpose: anything may load this. Exceptions that cannot import it spell the
// name out and say so (hook-fallback.ts); test/product-name.test.ts keeps them in step.
// Not covered: the CLI command `lotse`, the `~/.lotse` folder, LOTSE_* variables and other machine names.

export const PRODUCT_NAME = "Marshal";

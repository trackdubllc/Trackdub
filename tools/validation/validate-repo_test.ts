import { normalizeGeneratedText, parseTarget } from "./validate-repo.ts";

Deno.test("parseTarget defaults to all", () => {
  if (parseTarget([]) !== "all") {
    throw new Error("expected empty target to select all validation");
  }
});

Deno.test("parseTarget rejects unknown target", () => {
  try {
    parseTarget(["unknown"]);
  } catch (error) {
    if (error instanceof Error && error.message.includes("Unknown")) {
      return;
    }
    throw error;
  }
  throw new Error("expected unknown target to fail");
});

Deno.test("generated text comparison ignores BOM and line endings", () => {
  const windows = "\uFEFFexport interface paths {}\r\n";
  const unix = "export interface paths {}\n";
  if (normalizeGeneratedText(windows) !== normalizeGeneratedText(unix)) {
    throw new Error("expected normalized generated text to match");
  }
});

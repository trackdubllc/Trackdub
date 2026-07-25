import { join } from "node:path";

export type ValidationTarget = "all" | "manifest" | "openapi";

interface CommandStep {
  label: string;
  command: string;
  args: string[];
  cwd: string;
}

export function parseTarget(args: string[]): ValidationTarget {
  const target = args[0] ?? "all";
  if (target === "all" || target === "manifest" || target === "openapi") {
    return target;
  }

  throw new Error(
    `Unknown validation target '${target}'. Use all, manifest, or openapi.`,
  );
}

export function normalizeGeneratedText(text: string): string {
  return text.replace(/^\uFEFF/, "").replaceAll("\r\n", "\n").trimEnd();
}

async function runStep(step: CommandStep): Promise<void> {
  console.log(`[validate] ${step.label}`);
  const result = await new Deno.Command(step.command, {
    args: step.args,
    cwd: step.cwd,
    stdout: "piped",
    stderr: "piped",
  }).output();

  if (result.stdout.length > 0) {
    await Deno.stdout.write(result.stdout);
  }
  if (result.stderr.length > 0) {
    await Deno.stderr.write(result.stderr);
  }
  if (!result.success) {
    throw new Error(`${step.label} failed with exit code ${result.code}.`);
  }
}

async function readOptionalFile(path: string): Promise<Uint8Array | undefined> {
  try {
    return await Deno.readFile(path);
  } catch (error) {
    if (error instanceof Deno.errors.NotFound) {
      return undefined;
    }
    throw error;
  }
}

async function restoreFile(
  path: string,
  original: Uint8Array | undefined,
): Promise<void> {
  if (original) {
    await Deno.writeFile(path, original);
    return;
  }

  try {
    await Deno.remove(path);
  } catch (error) {
    if (!(error instanceof Deno.errors.NotFound)) {
      throw error;
    }
  }
}

async function removeIfExists(path: string): Promise<void> {
  try {
    await Deno.remove(path);
  } catch (error) {
    if (!(error instanceof Deno.errors.NotFound)) {
      throw error;
    }
  }
}

async function validateManifest(repoRoot: string): Promise<void> {
  const python = Deno.build.os === "windows" ? "python" : "python3";
  const summaryPath = join(repoRoot, "audit-summary.md");
  const originalSummary = await readOptionalFile(summaryPath);

  try {
    await runStep({
      label: "model license and attribution audit",
      command: python,
      args: ["tools/ci/audit-bundled-model-manifest.py"],
      cwd: repoRoot,
    });
    await runStep({
      label: "model manifest schema validation",
      command: python,
      args: ["tools/ci/validate-manifest-schema.py"],
      cwd: repoRoot,
    });
    await runStep({
      label: "model manifest structural hash validation",
      command: python,
      args: [
        "tools/ci/verify-manifest-hashes.py",
        "--structural",
        "--all-audited",
      ],
      cwd: repoRoot,
    });
  } finally {
    await restoreFile(summaryPath, originalSummary);
  }
}

async function validateOpenApi(repoRoot: string): Promise<void> {
  const frontendDir = join(repoRoot, "frontend");
  const artifactsDir = join(repoRoot, ".artifacts", "validation");
  const generatedPath = join(artifactsDir, "schema.generated.d.ts");
  const committedPath = join(frontendDir, "src", "api", "schema.d.ts");
  const npm = Deno.build.os === "windows" ? "npm.cmd" : "npm";

  await Deno.mkdir(artifactsDir, { recursive: true });
  try {
    await runStep({
      label: "OpenAPI TypeScript schema generation",
      command: npm,
      args: [
        "exec",
        "--",
        "openapi-typescript",
        "../src/Trackdub.Api/openapi.json",
        "-o",
        generatedPath,
      ],
      cwd: frontendDir,
    });

    const [generated, committed] = await Promise.all([
      Deno.readTextFile(generatedPath),
      Deno.readTextFile(committedPath),
    ]);
    if (
      normalizeGeneratedText(generated) !== normalizeGeneratedText(committed)
    ) {
      throw new Error(
        "OpenAPI TypeScript schema drift detected. Run 'npm --prefix frontend run generate-api:file' and commit frontend/src/api/schema.d.ts.",
      );
    }

    console.log("[validate] OpenAPI TypeScript schema is current.");
  } finally {
    await removeIfExists(generatedPath);
  }
}

async function main(): Promise<void> {
  const scriptDir = import.meta.dirname;
  if (!scriptDir) {
    throw new Error("Unable to resolve validation script directory.");
  }

  const repoRoot = join(scriptDir, "..", "..");
  const target = parseTarget(Deno.args);
  if (target === "all" || target === "manifest") {
    await validateManifest(repoRoot);
  }
  if (target === "all" || target === "openapi") {
    await validateOpenApi(repoRoot);
  }
  console.log(`[validate] ${target} validation passed.`);
}

if (import.meta.main) {
  try {
    await main();
  } catch (error) {
    console.error(
      `[validate] ${error instanceof Error ? error.message : String(error)}`,
    );
    Deno.exit(1);
  }
}

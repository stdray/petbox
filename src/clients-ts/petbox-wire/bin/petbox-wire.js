#!/usr/bin/env node
// Thin launcher for the PetBox agent-wiring kit.
//
// Plain JS (no TypeScript) so it still starts on an old Node and can print a clear
// version error. The kit itself is plain TypeScript executed by Node's native
// type-stripping, which needs Node >= 23.6.

const [maj, min] = process.versions.node.split(".").map((n) => parseInt(n, 10));
if (maj < 23 || (maj === 23 && min < 6)) {
  console.error(
    `petbox-wire needs Node >= 23.6 (native TypeScript type-stripping); you have ${process.versions.node}`,
  );
  process.exit(1);
}

// Node deliberately refuses to type-strip .ts files under node_modules
// (ERR_UNSUPPORTED_NODE_MODULES_TYPE_STRIPPING), and the npx cache is exactly that — so the
// kit cannot run in place. Copy it out to a temp dir and import from there; wire.ts then
// installs the stable copy (~/.petbox/wire/) itself after validating the key.
const { cpSync, mkdtempSync, readFileSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join, dirname } = await import("node:path");
const { fileURLToPath, pathToFileURL } = await import("node:url");

const pkgDir = join(dirname(fileURLToPath(import.meta.url)), "..");
const srcDir = join(pkgDir, "src");
const runDir = mkdtempSync(join(tmpdir(), "petbox-wire-"));
cpSync(srcDir, runDir, { recursive: true });

// `runDir` becomes wire.ts's own HERE — a FLAT scratch dir directly under the OS temp dir, whose
// parent is never this package's root. agent-definition.ts's loadKitVersion() therefore cannot
// find `../package.json` from here (that was the actual defect behind kit-version-unknown-inside-hooks
// surviving its first fix: it is not just hooks running from the STABLE mirror that miss
// package.json — every `npx petbox-wire` invocation does, via this exact scratch dir). Stamp the
// copy with THIS install's real version — read from OUR OWN `../package.json`, which npm always
// ships in the tarball regardless of the `files` allowlist — so KIT_VERSION resolves correctly
// from the very first command, before wire.ts is even imported. This file then rides along for
// free the moment wire.ts's copyKitToStable mirrors HERE (this scratch dir) into ~/.petbox/wire/,
// so the hook finds it too, with zero extra plumbing (see agent-definition.ts's KIT_VERSION doc
// comment, step 2). Best-effort: package.json is guaranteed present here, but a version label
// must never be able to block the CLI from starting.
try {
  const pkg = JSON.parse(readFileSync(join(pkgDir, "package.json"), "utf8"));
  if (typeof pkg.version === "string" && pkg.version.trim()) {
    writeFileSync(
      join(runDir, "kit-version.json"),
      JSON.stringify({ version: pkg.version.trim(), installedAt: new Date().toISOString(), source: pkgDir }, null, 2) +
        "\n",
      "utf8",
    );
  }
} catch {
  // best-effort — a missing/corrupt package.json here must not block the CLI from starting
}

// wire.ts runs main() at module top level, so importing it executes the CLI.
await import(pathToFileURL(join(runDir, "wire.ts")).href);

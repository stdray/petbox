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
const { cpSync, mkdtempSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join, dirname } = await import("node:path");
const { fileURLToPath, pathToFileURL } = await import("node:url");

const srcDir = join(dirname(fileURLToPath(import.meta.url)), "..", "src");
const runDir = mkdtempSync(join(tmpdir(), "petbox-wire-"));
cpSync(srcDir, runDir, { recursive: true });

// wire.ts runs main() at module top level, so importing it executes the CLI.
await import(pathToFileURL(join(runDir, "wire.ts")).href);

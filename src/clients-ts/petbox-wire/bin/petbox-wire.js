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

// wire.ts runs main() at module top level, so importing it executes the CLI.
await import("../src/wire.ts");

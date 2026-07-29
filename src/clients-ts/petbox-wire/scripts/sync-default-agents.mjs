// Copy the CANONICAL agent roster (repo `src/common/default-agents.json`) into this package's
// own `src/`, where agent-definition.ts reads it as DEFAULT_AGENT_DEFINITION.
//
// WHY A COPY AT ALL: the kit is published to npm, and `package.json`'s `files` allowlist can only
// ship paths INSIDE the package directory — a sibling `src/common/` two levels up is simply not in
// the tarball. And the baseline is the kit's OFFLINE fallback: it must be physically present on a
// machine with no network and no PetBox reachable, so fetching it at run time is not an option.
//
// WHY THE COPY IS GITIGNORED: a tracked copy is a second editable source, i.e. exactly the drift
// this whole arrangement removes. Ignored, it can only ever be produced by this script, from the
// one canonical file. (npm's `files` allowlist takes precedence over ignore rules, so being
// gitignored does NOT keep it out of the published tarball — verified with `npm pack`.)
//
// Runs from package.json's `pretest` / `pretypecheck` / `prepack`, so every path that consumes or
// ships the kit regenerates it first. Zero deps, node builtins only — same rule as the kit source.

import { copyFileSync, mkdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

const packageRoot = resolve(import.meta.dirname, "..");
const canonical = resolve(packageRoot, "..", "..", "common", "default-agents.json");
const destination = join(packageRoot, "src", "default-agents.json");

// Parse before copying: a malformed canonical file must fail HERE, loudly, with the path in the
// message — not later as a confusing crash inside the kit or a broken published package.
let parsed;
try {
  parsed = JSON.parse(readFileSync(canonical, "utf8"));
} catch (err) {
  throw new Error(
    `sync-default-agents: cannot read the canonical roster at ${canonical} — ` +
      `it is the single source both the server and this kit consume (see src/common/README.md). ` +
      `Cause: ${err instanceof Error ? err.message : String(err)}`,
  );
}

const roleCount = Array.isArray(parsed?.roles) ? parsed.roles.length : 0;
if (roleCount === 0) throw new Error(`sync-default-agents: ${canonical} declares no roles.`);

mkdirSync(dirname(destination), { recursive: true });
copyFileSync(canonical, destination);
console.log(`sync-default-agents: ${canonical} -> ${destination} (${roleCount} roles)`);

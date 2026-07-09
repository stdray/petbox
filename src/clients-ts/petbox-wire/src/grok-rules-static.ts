// Static Grok project-rules bootstrap (no network). Shared by wire.ts (install into project)
// and grok-materialize-rules.ts (SessionStart refresh). Single source: buildProtocol + grok namer.

import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { buildProtocol, grokPetboxTool } from "./protocol.ts";

export const GROK_BOOTSTRAP_RULES_FILE = "petbox-memory.md";
export const GROK_LOCAL_RULES_FILE = "petbox-session.local.md";

// Protocol-only rules file for cold start / before first SessionStart materialize.
// Live canon lands in petbox-session.local.md (gitignored) via grok-materialize-rules.ts.
export function buildGrokBootstrapRules(project: string): string {
  const protocol = buildProtocol(project, grokPetboxTool);
  return `${protocol}

---

## Grok Build wiring note (temporary)

Grok Build **ignores SessionStart stdout** (docs: passive hooks). Claude Code injects
\`pull-memory.ts\` stdout; Grok does not — so this \`.grok/rules/\` file is the **temp**
channel for the PetBox memory protocol (intake \`grok-sessionstart-stdout-ignored\`).

- **This file** (\`${GROK_BOOTSTRAP_RULES_FILE}\`): protocol + self-intro contract (tracked or wire-installed).
- **Live canon** (\`${GROK_LOCAL_RULES_FILE}\`, gitignored): rewritten each Grok SessionStart with protocol + canon when the project is registered.

If \`${GROK_LOCAL_RULES_FILE}\` is present, prefer its canon block; the first-response banner
and orchestrator rules still apply.

**Not** prompt-RAG. Proper fix = Grok-native context inject or first-class wire parity.
`;
}

export function writeGrokBootstrapRules(projectRoot: string, projectKey: string = "$system"): void {
  const rulesDir = join(projectRoot, ".grok", "rules");
  mkdirSync(rulesDir, { recursive: true });
  writeFileSync(join(rulesDir, GROK_BOOTSTRAP_RULES_FILE), buildGrokBootstrapRules(projectKey), "utf8");
}

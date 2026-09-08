// Render the kit's OWN skills salience index for a probe workspace.
//
//   node emit-skills-index.mjs <workspace-root> <abs path to skill-files.ts>
//
// The text MUST come from `buildAutoSkillsIndex` itself, never from a Python re-implementation:
// a re-implementation would measure text this probe wrote instead of the text the kit ships.
// The module path is an ARGUMENT (unlike s4/emit-block.mjs, which hardcodes one machine's
// checkout) so the script resolves against whatever worktree probe.py is running from.
import { pathToFileURL } from "node:url";

const [, , root, modulePath] = process.argv;
const mod = await import(pathToFileURL(modulePath).href);
const block = mod.buildAutoSkillsIndex(root);
process.stdout.write(block === null ? "<<NULL>>" : block);

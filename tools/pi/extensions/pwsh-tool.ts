/**
 * pwsh — the built-in PowerShell tool, registered a second time under another name.
 *
 * Why this exists: pi-web picks the session's active tools from a fixed
 * `CODING_TOOL_NAMES` set that contains `powershell`, and rewrites every name in that
 * set to the single configured shell (lib/powershell-settings.ts via
 * lib/rpc-manager.ts resolveActiveToolNames). With `defaultTools` listing both `bash`
 * and `powershell`, `powershell` is rewritten to `bash` and disappears. A tool name
 * outside that set is carried through untouched, so `pwsh` survives.
 *
 * Route: re-register the SDK's own PowerShell tool definition
 * (`createPowerShellToolDefinition`), overriding only `name` and `label`. Behaviour,
 * parameter schema (command, timeout), output truncation, temp-file spill, UTF-8 output
 * prefix and session-environment behaviour are therefore the built-in's, not a copy.
 *
 * Compatibility: `createPowerShellToolDefinition` is exported from the package root of
 * both @earendil-works/pi-coding-agent 1.3.0 (CLI) and 1.2.0 (nested inside pi-web).
 * Extensions resolve `@earendil-works/pi-coding-agent` through the loader's virtual
 * module table, which maps it to the SDK copy of whichever process loaded us, so the
 * import picks the right version per host. No API used here is version-specific.
 */

import type { ExtensionAPI, ToolDefinition } from "@earendil-works/pi-coding-agent";
import { createPowerShellToolDefinition } from "@earendil-works/pi-coding-agent";

const TOOL_NAME = "pwsh";

export default function (pi: ExtensionAPI): void {
	// cwd is only the fallback: the definition's execute() uses ctx.cwd when the
	// session provides one (dist/core/tools/bash.js: `ctx?.cwd || cwd`).
	const base = createPowerShellToolDefinition(process.cwd());

	const definition = {
		...base,
		name: TOOL_NAME,
		label: TOOL_NAME,
		// Description, promptSnippet, promptGuidelines, parameters, outputSchema and
		// execute all come from the built-in definition unchanged.
	} as ToolDefinition;

	pi.registerTool(definition);
}

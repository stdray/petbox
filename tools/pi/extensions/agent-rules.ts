// Assemble the rule files in ~/.pi/agent/skills into system-prompt sections for the MAIN
// session, and FREEZE them for the life of that session.
//
// Two sets, split by filename prefix:
//
//   amd-*.md  -> section `agent_rules`   always
//   pbx-*.md  -> section `petbox_rules`  only when the project configures a petbox MCP server
//
// Why not AGENTS.md: a context file cannot include other files, and the rules live as skills
// so that pi-subagents children can preload exactly the ones they need (`skills: amd-...`).
// This extension is the main session's half of that split — same files, one source, no
// generated copy.
//
// Why frozen: pi diffs the desired sections against the ones the current system message
// carries, and when any section differs it replays the prompt; the provider then re-reads the
// conversation without its cache ("Cache miss: N tokens re-billed"). Re-reading the rule files
// on every turn made any mid-session edit — or a late flip of the petbox gate — silently cost
// a full re-bill. So both the content and the gate are decided ONCE per session, on
// `session_start`. Consequence, by design: editing a rule takes effect at the next pi start,
// exactly like editing a context file. Same reasoning as the banner freeze in petbox-pi.ts.
//
// Children do NOT need this extension: pi-subagents overrides the child system prompt, so a
// section set here never reaches them; their preload comes from the `skills:` field.

import { existsSync, readFileSync, readdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const SKILLS_DIR = join(homedir(), ".pi", "agent", "skills");

const ALWAYS_SECTION = "agent_rules";
const ALWAYS_PREFIX = "amd-";
const PETBOX_SECTION = "petbox_rules";
const PETBOX_PREFIX = "pbx-";
const PETBOX_SERVER_NAME = "petbox";

/** Every `<prefix>*.md` under the skills dir, joined in byte order. */
function compose(prefix: string): string {
	const files = readdirSync(SKILLS_DIR)
		.filter((f) => f.startsWith(prefix) && f.endsWith(".md"))
		.sort();
	const parts: string[] = [];
	for (const f of files) {
		const raw = readFileSync(join(SKILLS_DIR, f), "utf8");
		// Strip Agent-Skills frontmatter if a file ever carries any; the body is what the
		// model reads.
		let body = raw;
		if (raw.startsWith("---")) {
			const end = raw.indexOf("\n---", 3);
			if (end !== -1) body = raw.slice(end + 4).replace(/^\s*\n/, "");
		}
		parts.push(body.trimEnd());
	}
	return parts.join("\n\n");
}

// Is this project wired to PetBox? Ask the CONFIG, not the runtime.
//
// Measured 2026-10-20: at the only `before_agent_start` of a headless run, `getMcpServers()`
// returned [] and the tool list held no petbox tool — MCP connects in the background — yet the
// same run declared 8 `mcp__petbox__*` tools a moment later. The config is also the honest
// question: "does this project have a petbox MCP server" does not depend on whether it
// finished connecting this second. User scope, then project scope — the same files pi reads.
//
// The project scope is the SESSION's cwd, not `process.cwd()`. Measured 2026-10-21 under a
// pi-web host: one server process serves many projects, so its cwd is not the session's
// project and the project's `.pi/mcp.json` was never read — `petbox_rules` silently stayed
// out of the prompt while `agent_rules` (gate-free) arrived. `process.cwd()` stays as the
// last fallback for a host that passes no context.
//
// Deliberately looser than pi's own MCP loader, which reads a project `mcp.json` only when
// the project is trusted: the section is prose about how to orchestrate, not a connection.
function declaresPetbox(pi: any, sessionCwd?: string): boolean {
	try {
		const servers = pi.getMcpServers?.() ?? [];
		if (servers.some((s: any) => s?.name === PETBOX_SERVER_NAME)) return true;
	} catch {
		// fall through to the config files
	}
	for (const file of [
		join(homedir(), ".pi", "agent", "mcp.json"),
		...(sessionCwd ? [join(sessionCwd, ".pi", "mcp.json")] : []),
		join(process.cwd(), ".pi", "mcp.json"),
	]) {
		try {
			if (!existsSync(file)) continue;
			const parsed = JSON.parse(readFileSync(file, "utf8"));
			const servers = parsed?.mcpServers;
			if (servers && typeof servers === "object" && PETBOX_SERVER_NAME in servers) return true;
		} catch {
			// an unreadable or non-JSON config is not proof of absence, so keep looking
		}
	}
	return false;
}

export default function (pi: any) {
	// Frozen for the life of the session; `undefined` means "not loaded yet".
	let alwaysText: string | undefined;
	let petboxText: string | undefined;

	const load = (sessionCwd?: string): void => {
		alwaysText = "";
		petboxText = "";
		try {
			alwaysText = compose(ALWAYS_PREFIX);
		} catch {
			// A broken skills dir must not break the session; the section is simply absent.
		}
		try {
			petboxText = declaresPetbox(pi, sessionCwd) ? compose(PETBOX_PREFIX) : "";
		} catch {
			// Same: absence beats a failed session.
		}
	};

	pi.on("session_start", (_event: any, ctx: any) => load(ctx?.cwd));

	pi.on("before_agent_start", (event: any, ctx: any) => {
		const options = event?.systemPromptOptions;
		if (!options?.sections) return;
		if (alwaysText === undefined) load(ctx?.cwd);
		if (alwaysText) options.sections[ALWAYS_SECTION] = alwaysText;
		if (petboxText) options.sections[PETBOX_SECTION] = petboxText;
	});
}

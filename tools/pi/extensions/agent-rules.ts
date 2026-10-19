// Assemble the rule files in ~/.pi/agent/skills into system-prompt sections for the MAIN
// session.
//
// Two sets, split by filename prefix:
//
//   amd-*.md  -> section `agent_rules`   always
//   pbx-*.md  -> section `petbox_rules`  ONLY when the session declares a petbox MCP tool
//
// Why not AGENTS.md: a context file cannot include other files, and the rules live as
// skills so that pi-subagents children can preload exactly the ones they need
// (`skills: amd-tool-schema, amd-verification, ...`). This extension is the main
// session's half of that split — same files, one source, no generated copy.
//
// Children do NOT need this extension: pi-subagents overrides the child system prompt, so
// a section set here never reaches them; their preload comes from the `skills:` field.
//
// The pbx- set is PetBox-specific prose (orchestrator behaviour, board protocol). It must
// not appear in a session that cannot act on it — no petbox MCP tool means no PetBox
// board, and then the rules describe tools the model does not have. Same capability test
// petbox-pi.ts uses for its banner: ask the session what it DECLARES, and keep the same
// asymmetry (a positive sticks, a negative is re-checked every run, because MCP tools are
// registered asynchronously and the first run of a restart can see an empty catalog).

import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const SKILLS_DIR = join(homedir(), ".pi", "agent", "skills");

const ALWAYS_SECTION = "agent_rules";
const ALWAYS_PREFIX = "amd-";
const PETBOX_SECTION = "petbox_rules";
const PETBOX_PREFIX = "pbx-";

// pi registers MCP tools as `mcp__<server>__<tool>`; the server is `petbox`.
const PETBOX_TOOL_PREFIX = "mcp__petbox__";
// `mcp__petbox` is how the server appears in the `mcp_servers` section (the tool prefix with
// the trailing separator dropped).
const PETBOX_SERVER_NAME = "petbox";

type Cache = { key: string; text: string };

function compose(prefix: string, cache: Cache | undefined): { text: string; cache: Cache | undefined } {
	// Key the cache on every file's mtime+size so an edit is picked up without a restart
	// (pi re-reads the section each turn).
	const files = readdirSync(SKILLS_DIR)
		.filter((f) => f.startsWith(prefix) && f.endsWith(".md"))
		.sort();
	let key = "";
	const parts: string[] = [];
	for (const f of files) {
		const p = join(SKILLS_DIR, f);
		const st = statSync(p);
		key += `${f}:${st.mtimeMs}:${st.size};`;
		const raw = readFileSync(p, "utf8");
		// Strip Agent-Skills frontmatter if a file ever carries any; the body is what the
		// model reads.
		let body = raw;
		if (raw.startsWith("---")) {
			const end = raw.indexOf("\n---", 3);
			if (end !== -1) body = raw.slice(end + 4).replace(/^\s*\n/, "");
		}
		parts.push(body.trimEnd());
	}
	if (cache?.key === key) return { text: cache.text, cache };
	const text = parts.join("\n\n");
	return { text, cache: { key, text } };
}

// Is this project wired to PetBox? Ask the CONFIG, not the runtime.
//
// Measured on 2026-10-20: at the only `before_agent_start` of a headless run, `getMcpServers()`
// returned [], the tool list held 17 non-MCP tools and `mcp_servers` was not rendered yet —
// even though the same run then declared 8 `mcp__petbox__*` tools a moment later. MCP connects
// in the background, so every runtime signal is too early on a cold start, and the section was
// silently withheld in every headless run.
//
// The config is the honest question anyway: "does this project have a petbox MCP server" does
// not depend on whether it finished connecting this second. User scope, then project scope —
// the same two files pi reads.
function declaresPetbox(pi: any): boolean {
	try {
		const servers = pi.getMcpServers?.() ?? [];
		if (servers.some((s: any) => s?.name === PETBOX_SERVER_NAME)) return true;
	} catch {
		// fall through to the config files
	}
	const candidates = [
		join(homedir(), ".pi", "agent", "mcp.json"),
		join(process.cwd(), ".pi", "mcp.json"),
	];
	for (const file of candidates) {
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
	let alwaysCache: Cache | undefined;
	let petboxCache: Cache | undefined;
	// A positive is sticky: once this session has shown the tools, the prose stays. A
	// negative is re-checked every run, so a late MCP registration still gets the section.
	let petboxSeen = false;

	pi.on("before_agent_start", (event: any) => {
		const options = event?.systemPromptOptions;
		if (!options?.sections) return;

		try {
			const rules = compose(ALWAYS_PREFIX, alwaysCache);
			alwaysCache = rules.cache;
			if (rules.text.length > 0) options.sections[ALWAYS_SECTION] = rules.text;
		} catch {
			// A broken skills dir must not break the session; the section is simply absent.
		}

		try {
			if (!petboxSeen) petboxSeen = declaresPetbox(pi);
			if (!petboxSeen) return;
			const rules = compose(PETBOX_PREFIX, petboxCache);
			petboxCache = rules.cache;
			if (rules.text.length > 0) options.sections[PETBOX_SECTION] = rules.text;
		} catch {
			// Same: absence beats a failed session.
		}
	});
}

// Assemble the amd-* rule skills (~/.pi/agent/skills/amd-*.md) into one system-prompt
// section for the MAIN session.
//
// Why not AGENTS.md: a context file cannot include other files, and the rules live as
// skills so that pi-subagents children can preload exactly the ones they need
// (`skills: amd-tool-schema, amd-verification, ...`). This extension is the main
// session's half of that split — same files, one source, no generated copy.
//
// Children do NOT need this extension: pi-subagents overrides the child system prompt,
// so a section set here never reaches them; their preload comes from the `skills:` field.

import { readFileSync, readdirSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const SKILLS_DIR = join(homedir(), ".pi", "agent", "skills");
const SECTION = "agent_rules";

type Cache = { key: string; text: string };
let cache: Cache | undefined;

function compose(): string {
	// Key the cache on every file's mtime+size so an edit is picked up without a restart
	// (pi re-reads the section each turn).
	const files = readdirSync(SKILLS_DIR)
		.filter((f) => f.startsWith("amd-") && f.endsWith(".md"))
		.sort();
	let key = "";
	const parts: string[] = [];
	for (const f of files) {
		const p = join(SKILLS_DIR, f);
		const st = statSync(p);
		key += `${f}:${st.mtimeMs}:${st.size};`;
		const raw = readFileSync(p, "utf8");
		// Strip the Agent-Skills frontmatter; the body is what the model reads.
		let body = raw;
		if (raw.startsWith("---")) {
			const end = raw.indexOf("\n---", 3);
			if (end !== -1) body = raw.slice(end + 4).replace(/^\s*\n/, "");
		}
		parts.push(body.trimEnd());
	}
	if (cache?.key === key) return cache.text;
	const text = parts.join("\n\n");
	cache = { key, text };
	return text;
}

export default function (pi: any) {
	pi.on("before_agent_start", (event: any) => {
		const options = event?.systemPromptOptions;
		if (!options?.sections) return;
		try {
			const text = compose();
			if (text.length > 0) options.sections[SECTION] = text;
		} catch {
			// A broken skills dir must not break the session; the section is simply absent.
		}
	});
}

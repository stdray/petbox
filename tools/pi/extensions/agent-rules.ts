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
const AGENTS_DIR = join(homedir(), ".pi", "agent", "agents");

const ALWAYS_SECTION = "agent_rules";
const ALWAYS_PREFIX = "amd-";
const PETBOX_SECTION = "petbox_rules";
const PETBOX_PREFIX = "pbx-";
const PETBOX_SERVER_NAME = "petbox";

/** A composed section, plus everything that went wrong while composing it. */
type Composed = { readonly text: string; readonly problems: readonly string[] };

function describe(error: unknown): string {
	return error instanceof Error ? error.message : String(error);
}

// Every `<prefix>*.md` under the skills dir, joined in byte order.
//
// Failures are COLLECTED, not thrown. Throwing one exception for one bad file is how the whole
// section used to vanish without a word — the caller now publishes `problems` instead (see
// `report`), and a section built from the readable fragments beats an empty one that nobody
// mentions.
function compose(prefix: string): Composed {
	let files: string[];
	try {
		files = readdirSync(SKILLS_DIR)
			.filter((f) => f.startsWith(prefix) && f.endsWith(".md"))
			.sort();
	} catch (error) {
		return { text: "", problems: [`cannot read ${SKILLS_DIR}: ${describe(error)}`] };
	}
	if (files.length === 0) {
		// Not an error in the filesystem sense, and exactly as silent as one: a skills dir that
		// exists but holds no fragment leaves the session rule-free and says nothing about it.
		return { text: "", problems: [`no ${prefix}*.md under ${SKILLS_DIR}`] };
	}
	const parts: string[] = [];
	const problems: string[] = [];
	for (const f of files) {
		try {
			const raw = readFileSync(join(SKILLS_DIR, f), "utf8");
			// Strip Agent-Skills frontmatter if a file ever carries any; the body is what the
			// model reads.
			let body = raw;
			if (raw.startsWith("---")) {
				const end = raw.indexOf("\n---", 3);
				if (end !== -1) body = raw.slice(end + 4).replace(/^\s*\n/, "");
			}
			parts.push(body.trimEnd());
		} catch (error) {
			problems.push(`${f}: ${describe(error)}`);
		}
	}
	return { text: parts.join("\n\n"), problems };
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

// Children never see this extension's section: `@tintinweb/pi-subagents` overrides their system
// prompt (`prompt_mode: replace` makes the role file the whole prompt), so a role's rules reach
// it only through `skills:` in its frontmatter. A name that resolves to nothing is not an error
// to that loader — `skill-loader.ts` substitutes `(Skill "x" not found in ...)` and the child
// starts with a placeholder where its rules were. Nobody sees that: not the model, which reads
// one odd line among many, and not the owner, who never opens a child's prompt. So the check
// runs here, once, on the main session's start — the only place left that can still speak.
//
// `prompt_mode: replace` also means a role with NO `skills:` line runs with no rules at all:
// the same silent hole wearing a different hat, reported the same way.
function missingRoleSkills(): string[] {
	let roles: string[];
	let fragments: Set<string>;
	try {
		roles = readdirSync(AGENTS_DIR)
			.filter((f) => f.endsWith(".md"))
			.sort();
	} catch (error) {
		return [`cannot read ${AGENTS_DIR}: ${describe(error)}`];
	}
	try {
		fragments = new Set(
			readdirSync(SKILLS_DIR)
				.filter((f) => f.endsWith(".md"))
				.map((f) => f.slice(0, -3)),
		);
	} catch (error) {
		return [`cannot read ${SKILLS_DIR}: ${describe(error)}`];
	}
	const problems: string[] = [];
	for (const role of roles) {
		let raw: string;
		try {
			raw = readFileSync(join(AGENTS_DIR, role), "utf8");
		} catch (error) {
			problems.push(`${role}: ${describe(error)}`);
			continue;
		}
		const line = /^skills:[ \t]*(.*)$/m.exec(raw);
		if (!line) {
			problems.push(`${role}: no skills: line, so the child preloads no rules`);
			continue;
		}
		const value = line[1].trim();
		if (value === "" || value === "false" || value === "none") {
			problems.push(`${role}: skills: ${value === "" ? "" : value} preloads no rules`);
			continue;
		}
		const names = value
			.replace(/^\[/, "")
			.replace(/\]$/, "")
			.split(",")
			.map((n) => n.trim())
			.filter((n) => n.length > 0);
		for (const name of names) {
			if (!fragments.has(name)) problems.push(`${role}: skill "${name}" is not under ${SKILLS_DIR}`);
		}
	}
	return problems;
}

export default function (pi: any) {
	// Frozen for the life of the session; `undefined` means "not loaded yet".
	let alwaysText: string | undefined;
	let petboxText: string | undefined;
	// One report per session, not one per turn: the same broken fragment must not re-warn on
	// every `before_agent_start`.
	let reported = false;

	// Why shout at all, when the old code deliberately stayed quiet: staying quiet is how a
	// session ends up running with NO operating rules and no trace that it did. Before this, an
	// unreadable fragment or a skills dir that moved left `alwaysText` empty, `before_agent_start`
	// skipped the section, and "rules missing" became indistinguishable from "rules never
	// deployed" — the exact failure this whole section exists to prevent. So the problems go to
	// the log AND to the UI once, and the session still starts: a warning is a report, not a veto.
	const report = (ctx: any, problems: readonly string[]): void => {
		if (reported || problems.length === 0) return;
		reported = true;
		const line = `pi rules: ${problems.join("; ")}`;
		try {
			console.warn(line);
		} catch {
			// a host without a console must not lose the session over a log call
		}
		try {
			ctx?.ui?.notify?.(line, "warning");
		} catch {
			// print and JSON modes carry no UI; the console line is the fallback
		}
	};

	const load = (ctx: any, sessionCwd?: string): void => {
		const problems: string[] = [];
		const always = compose(ALWAYS_PREFIX);
		alwaysText = always.text;
		problems.push(...always.problems);
		// `declaresPetbox` swallows its own read and parse failures by design, so there is no
		// outer catch left here to hide a broken compose.
		petboxText = "";
		if (declaresPetbox(pi, sessionCwd)) {
			const petbox = compose(PETBOX_PREFIX);
			petboxText = petbox.text;
			problems.push(...petbox.problems);
		}
		// The children's half of the same question, asked from the only session that can still
		// reach a human. Collected, not reported piecemeal: `report` fires once per session, so a
		// second call would be swallowed and the role problems would never be heard.
		problems.push(...missingRoleSkills());
		report(ctx, problems);
	};

	pi.on("session_start", (_event: any, ctx: any) => load(ctx, ctx?.cwd));

	pi.on("before_agent_start", (event: any, ctx: any) => {
		const options = event?.systemPromptOptions;
		if (!options?.sections) return;
		if (alwaysText === undefined) load(ctx, ctx?.cwd);
		if (alwaysText) options.sections[ALWAYS_SECTION] = alwaysText;
		if (petboxText) options.sections[PETBOX_SECTION] = petboxText;
	});
}

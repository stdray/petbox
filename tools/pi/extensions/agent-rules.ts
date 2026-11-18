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

import type { Dirent } from "node:fs";
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
//
// The verdict has to agree with `skill-loader.ts`, or this becomes the failure it reports: a
// name that resolves fine but is called missing is a warning nobody can act on, and the owner
// learns to scroll past the ones that matter. So the shapes below mirror that loader — the same
// roots, the same two layouts per root, and the same readings of the field's YAML forms.

/**
 * The skill roots `skill-loader.ts` searches, in its precedence order: project scope first
 * (`<cwd>/.pi/skills`, `<cwd>/.agents/skills`), then user scope. A cwd is optional — a host that
 * passes none still gets the user roots rather than no check at all.
 */
function skillRoots(sessionCwd?: string): string[] {
	const roots: string[] = [];
	if (typeof sessionCwd === "string" && sessionCwd.length > 0) {
		roots.push(join(sessionCwd, ".pi", "skills"), join(sessionCwd, ".agents", "skills"));
	}
	roots.push(SKILLS_DIR, join(homedir(), ".agents", "skills"), join(homedir(), ".pi", "skills"));
	return roots;
}

/**
 * Every skill name those roots resolve: a flat `<root>/<name>.md`, or any `<name>/SKILL.md`
 * directory below a root. Dot-directories and `node_modules` are skipped and a skill directory
 * is not descended into (Pi: skills don't nest), exactly as the loader walks them.
 */
function installedSkills(roots: readonly string[]): Set<string> {
	const names = new Set<string>();
	for (const root of roots) {
		let entries: Dirent[];
		try {
			entries = readdirSync(root, { withFileTypes: true });
		} catch {
			continue; // a project root that does not exist is the normal case, not a problem
		}
		for (const entry of entries) {
			if (entry.isFile() && entry.name.endsWith(".md")) names.add(entry.name.slice(0, -3));
		}
		const queue = [root];
		while (queue.length > 0) {
			const current = queue.shift();
			if (current === undefined) continue;
			let children: Dirent[];
			try {
				children = readdirSync(current, { withFileTypes: true });
			} catch {
				continue;
			}
			for (const child of children) {
				if (!child.isDirectory() || child.name.startsWith(".") || child.name === "node_modules") continue;
				const path = join(current, child.name);
				if (existsSync(join(path, "SKILL.md"))) names.add(child.name);
				else queue.push(path);
			}
		}
	}
	return names;
}

/**
 * The frontmatter block of a role file, or "" when there is none — pi's own fence rule: a leading
 * `---`, closed by the next line starting one, BOM and CRLF tolerated. Only this block is a
 * field; a body line that happens to start with `skills:` is prose.
 */
function frontmatterOf(raw: string): string {
	const normalized = raw.replace(/^\uFEFF/, "").replace(/\r\n/g, "\n").replace(/\r/g, "\n");
	if (!normalized.startsWith("---")) return "";
	const end = normalized.indexOf("\n---", 3);
	return end === -1 ? "" : normalized.slice(4, end);
}

/** One frontmatter field as YAML hands it over: an inline scalar, plus any block-list items. */
type Field = { readonly inline: string; readonly items: readonly string[] };

function unquote(value: string): string {
	return value.replace(/^["']/, "").replace(/["']$/, "");
}

function field(frontmatter: string, key: string): Field | undefined {
	const lines = frontmatter.split("\n");
	for (let i = 0; i < lines.length; i++) {
		const head = /^([A-Za-z_][\w-]*)[ \t]*:(.*)$/.exec(lines[i]);
		if (!head || head[1] !== key) continue;
		const items: string[] = [];
		for (let j = i + 1; j < lines.length; j++) {
			if (lines[j].trim() === "") continue;
			const item = /^[ \t]*-[ \t]+(.*)$/.exec(lines[j]);
			if (!item) break;
			items.push(unquote(item[1].trim()));
		}
		return { inline: unquote(head[2].trim()), items };
	}
	return undefined;
}

/**
 * The names a `skills:` field preloads, `undefined` for pi's "inherit all", `[]` for "none".
 * All three YAML writings are accepted because the loader accepts all three: a CSV scalar, a
 * flow list (`[a, b]`), and a block list on the lines below the key.
 */
function skillNames(value: Field | undefined): string[] | undefined | null {
	if (!value) return null; // no field at all
	const inline = value.inline;
	const lower = inline.toLowerCase();
	if (lower === "true") return undefined; // inherit all: pi's own "not configured" answer
	if (lower === "false" || lower === "none") return [];
	const scalar = inline.replace(/^\[/, "").replace(/\]$/, "");
	return [...scalar.split(","), ...value.items].map((n) => unquote(n.trim())).filter((n) => n.length > 0);
}

function missingRoleSkills(sessionCwd?: string): string[] {
	let roles: string[];
	try {
		roles = readdirSync(AGENTS_DIR)
			.filter((f) => f.endsWith(".md"))
			.sort();
	} catch (error) {
		return [`cannot read ${AGENTS_DIR}: ${describe(error)}`];
	}
	const roots = skillRoots(sessionCwd);
	const installed = installedSkills(roots);
	if (installed.size === 0) {
		// Every role would now be reported name by name; one line naming the roots is the news.
		return [`no skill fragment found under any skill root (${roots.join(", ")})`];
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
		const frontmatter = frontmatterOf(raw);
		// `enabled: false` is the owner disabling the role on purpose; its rules are moot.
		if (field(frontmatter, "enabled")?.inline.toLowerCase() === "false") continue;
		const value = field(frontmatter, "skills") ?? field(frontmatter, "inherit_skills");
		const names = skillNames(value);
		if (names === undefined) continue; // inherit all: every installed skill is preloaded
		if (names === null) {
			problems.push(`${role}: no skills: line, so the child preloads no rules`);
			continue;
		}
		if (names.length === 0) {
			problems.push(`${role}: skills: ${value?.inline ?? ""} preloads no rules`);
			continue;
		}
		for (const name of names) {
			if (!installed.has(name)) problems.push(`${role}: skill "${name}" is not installed in any skill root`);
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
	// And one COMPOSE per session: recomposing on a later call would replace the frozen text —
	// the mid-transcript change this file exists to prevent — and, with `report` already spent,
	// swallow whatever the second pass found.
	let loaded = false;

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
		if (loaded) return;
		loaded = true;
		try {
			const problems: string[] = [];
			const always = compose(ALWAYS_PREFIX);
			alwaysText = always.text;
			problems.push(...always.problems);
			// `declaresPetbox` swallows its own read and parse failures by design; the paths it
			// builds from a non-string cwd are not its own, and they throw.
			petboxText = "";
			if (declaresPetbox(pi, sessionCwd)) {
				const petbox = compose(PETBOX_PREFIX);
				petboxText = petbox.text;
				problems.push(...petbox.problems);
			}
			// The children's half of the same question, asked from the only session that can still
			// reach a human. Collected, not reported piecemeal: `report` fires once per session, so a
			// second call would be swallowed and the role problems would never be heard.
			problems.push(...missingRoleSkills(sessionCwd));
			report(ctx, problems);
		} catch (error) {
			// Nothing may escape into the event handler: a throw from `before_agent_start` takes the
			// turn with it, and a session that cannot be started is worse than one without rules.
			report(ctx, [`cannot assemble the rule sections: ${describe(error)}`]);
		}
	};

	pi.on("session_start", (_event: any, ctx: any) => load(ctx, ctx?.cwd));

	pi.on("before_agent_start", (event: any, ctx: any) => {
		const options = event?.systemPromptOptions;
		if (!options?.sections) return;
		if (!loaded) load(ctx, ctx?.cwd);
		if (alwaysText) options.sections[ALWAYS_SECTION] = alwaysText;
		if (petboxText) options.sections[PETBOX_SECTION] = petboxText;
	});
}

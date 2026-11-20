#!/usr/bin/env bun
// tools/pi/skills.mjs — install the three PetBox skills pi should carry, from the KIT'S OWN
// templates (never a hand copy of them).
//
// WHY THIS EXISTS (card: pi-kit-skills-and-write-rule, part 2). The rules under `rules/` reach pi
// as prompt sections; a long procedure does not belong there — it belongs in a skill, which pi
// lists cheaply and the model opens only when the task matches. The kit already ships exactly
// these procedures as `templates/<dir>/SKILL.md` for the harnesses `petbox-wire apply` wires, and
// a second hand-maintained copy here would drift from that one on the first upstream edit. So the
// templates are rendered through `renderSkillTemplate` — the same function `wire` calls — and the
// pi-specific differences are applied as DELTAS after rendering, each pinned to an anchor.
//
// THE ANCHORS ARE THE CONTRACT. A delta whose anchor is missing means the upstream template
// changed in a way this installer does not understand; the script then exits non-zero naming the
// skill, the delta and the anchor. A silently skipped delta is the failure this whole file exists
// to prevent: the skill would install "successfully" while telling the model that `$system` is the
// project, or that a shell heredoc is the way to write a draft.
//
// A template that renders a project key at all is rendered with `$system` (this machine's
// project) and every occurrence is then rewritten to a `<project>` placeholder — a machine-wide
// install serves whichever project the session connects to, so a literal is wrong for every
// project but one. The final guard re-scans the rendered text for the literal and for any
// unrendered `{{PLACEHOLDER}}`; either one is a hard failure.
//
// Usage: skills.mjs [--dry-run] [--target <agent dir>]
//   <agent dir> is `--target` when given (deploy.sh passes the target it already resolved), else
//   `$PI_AGENT_DIR`, else `~/.pi/agent` — exactly the directory deploy.sh installs into. Skills land
//   in `<agent dir>/skills/<name>/SKILL.md`.
//
// Output discipline mirrors deploy.sh: `=` unchanged, `+` written (or would write, under
// --dry-run), nothing touched in a dry run, a second run is a no-op.
//
// Every plan is built (rendered, deltas applied, guarded) BEFORE the first write, so a broken
// anchor leaves the skills directory exactly as it was rather than half-updated. The installed
// file is a deploy artifact and is replaced wholesale, like the rules deploy.sh copies — the
// `petbox: managed` marker in the frontmatter is the KIT's clobber guard for `petbox-wire apply`
// and is not consulted here; edit the template, not `~/.pi/agent/skills`.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// The kit's registry + renderer. Imported, not copied: PROJECT_SKILLS is the one place a skill is
// registered, and `extraFiles` (petbox-node-authoring's validate-body.mjs) comes from there too.
import { PROJECT_SKILLS, renderSkillTemplate } from "../../src/clients-ts/petbox-wire/src/skill-files.ts";

const here = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = dirname(dirname(here));
const TEMPLATES_ROOT = join(REPO_ROOT, "src", "clients-ts", "petbox-wire", "src", "templates");

/** The three skills pi installs. Exactly these: `petbox`/`petbox-agent-factory`/… are what
 * `petbox-wire apply` delivers INTO a project, and four of them are owner-invocable slash-command
 * surfaces that pi does not have. */
const SKILL_DIRS = ["petbox-methodology", "petbox-write-economy", "petbox-node-authoring"];

// What a machine-wide install renders before the deltas run.
//
//   - PROJECT: `$system` — this machine's project, and the only key that could be hardcoded here.
//     Every occurrence is rewritten below; the guard at the end fails if one survives.
//   - WORKSPACE: unused by all three templates (grep: zero `{{WORKSPACE}}`; `petbox` is the only
//     template that has one and it is not installed here). Kept explicit rather than a silent ""
//     so a future template that starts using it renders something visibly wrong instead of
//     "undefined".
//   - ENV_VAR: `PETBOX_API_KEY` — the name this machine's pi MCP config actually reads
//     (`.pi/mcp.json`: "X-Api-Key": "${PETBOX_API_KEY}"). The kit's own deriveEnvVar("$system")
//     would produce PETBOX_SYSTEM_API_KEY, which no pi config here sets; a curl example naming it
//     would be wrong on this machine.
const PROJECT = "$system";
const WORKSPACE = "$workspace";
const ENV_VAR = "PETBOX_API_KEY";

/** `<project>` is the placeholder every rendered project key becomes. It appears inside quoted
 * strings only (`"…/api/blobs/<project>"`, `projectKey:"<project>"`), so it is never a shell
 * redirect. */
const PROJECT_PLACEHOLDER = "<project>";

// ---- pi deltas ------------------------------------------------------------------------------
//
// One entry per (skill, delta, anchor). `find` MUST occur exactly once in that skill's rendered
// text — absent or duplicated is a hard failure, because in either case the replacement no longer
// means what the delta says it means.
//
// A `find` is written against the RENDERED text (`$system`, `PETBOX_API_KEY` already substituted),
// so an upstream template edit that removes or reflows the anchor breaks this install loudly.

const DELTAS = [
  // ===== petbox-methodology ==================================================================
  {
    skill: "petbox-methodology",
    delta: "project key: the \"which one (if any)\" line",
    find: `Which one (if any) \`${PROJECT}\` runs`,
    replace: "Which one (if any) THIS project runs",
  },
  {
    skill: "petbox-methodology",
    delta: "project key: the tasks_methodology_guide call",
    find: ["```", `tasks_methodology_guide(projectKey:"${PROJECT}")`, "```"].join("\n"),
    replace: [
      "```",
      `tasks_methodology_guide(projectKey:"${PROJECT_PLACEHOLDER}")`,
      "```",
      "",
      "`<project>` is the key on the banner's \"this project is wired to PetBox (project …)\" line —",
      "read it in this session's `<petbox>` banner and substitute it into every call this skill shows.",
      "This file is installed once per machine, outside any project, so it never carries a project key",
      "itself: a literal here would be wrong for every project except the one it happened to be",
      "rendered for.",
    ].join("\n"),
  },
  {
    skill: "petbox-methodology",
    delta: "defect routing: observations, then promote (not a hand-made card, not intake)",
    find: [
      "- A defect-like finding (broken, unexpected, contradicts docs, a process defect) goes straight",
      "  onto this board yourself — never into memory, never into a generic intake bucket. It dedups by",
      `  recurrence, so "already observed" is never a reason to stay silent.`,
    ].join("\n"),
    replace: [
      "- A defect-like finding (broken, unexpected, contradicts docs, a defect in the process itself)",
      "  goes onto this board yourself — never into memory, and never as a hand-built `work` or `ideas`",
      `  card. It dedups by recurrence, so "already observed" is never a reason to stay silent.`,
      "- **Promoting is what makes it a card — `tasks_observation_promote`.** The observation stays",
      "  here (status `promoted`), the promote creates the `work`/`ideas` node and the",
      "  `observation_obligation` relation, and the observation flips to `fixed` by itself when that",
      "  node reaches a terminal-ok status; a recurrence after that reopens it to `seen`. Creating the",
      "  card by hand with `tasks_upsert` instead breaks the chain: two nodes for one finding, no",
      "  relation between them, and nothing to close the loop when the work is done.",
      "- **Intake is not where a defect goes.** The routing rules above still cover what is not a",
      "  defect — a question, a wish, a report whose destination you cannot settle from it.",
    ].join("\n"),
  },

  // ===== petbox-write-economy ================================================================
  {
    skill: "petbox-write-economy",
    delta: "project key: the curl blob URL",
    find: `curl -sS -X POST "https://petbox.3po.su/api/blobs/${PROJECT}" \\`,
    replace: `curl -sS -X POST "https://petbox.3po.su/api/blobs/${PROJECT_PLACEHOLDER}" \\`,
  },
  {
    skill: "petbox-write-economy",
    delta: "project key: the PowerShell blob URL",
    find: `Invoke-RestMethod -Method Post -Uri "https://petbox.3po.su/api/blobs/${PROJECT}" \``,
    replace: `Invoke-RestMethod -Method Post -Uri "https://petbox.3po.su/api/blobs/${PROJECT_PLACEHOLDER}" \``,
  },
  {
    skill: "petbox-write-economy",
    delta: "project key: the bodyRef tasks_upsert call",
    find: `tasks_upsert(projectKey:"${PROJECT}", nodes:[{ key:"...", bodyRef:"blob-<ref>" }])`,
    replace: `tasks_upsert(projectKey:"${PROJECT_PLACEHOLDER}", nodes:[{ key:"...", bodyRef:"blob-<ref>" }])`,
  },
  {
    skill: "petbox-write-economy",
    delta: "project key: the fragment tasks_upsert call",
    find: [`tasks_upsert(projectKey:"${PROJECT}", nodes:[{`, `  key:"...", version:<n>,`].join("\n"),
    replace: [`tasks_upsert(projectKey:"${PROJECT_PLACEHOLDER}", nodes:[{`, `  key:"...", version:<n>,`].join(
      "\n",
    ),
  },
  {
    skill: "petbox-write-economy",
    delta: "project key: where <project> comes from",
    find: "Upload text once, then pass a short reference instead of retyping the text into the call:",
    replace: [
      "Upload text once, then pass a short reference instead of retyping the text into the call.",
      "`<project>` below is the key on the banner's \"this project is wired to PetBox (project …)\"",
      "line — it is in this session's `<petbox>` banner. This file is installed once per machine,",
      "outside any project, so it never carries a project key itself.",
    ].join("\n"),
  },
  {
    skill: "petbox-write-economy",
    delta: "heredoc advice: a draft file is written with the `write` tool",
    find: [
      "Writing the text to a file with a",
      "shell heredoc and uploading it costs one extra call and removes that failure mode completely —",
      "**only if the delimiter is quoted** (`<<'EOF'`, never bare `<<EOF`). An unquoted delimiter puts",
      "the heredoc BODY through shell substitution, and Cyrillic/CJK markdown is exactly the content",
      "most likely to carry `$`, backticks or backslashes that trigger it — reintroducing the same",
      "class of silent corruption this technique exists to remove.",
    ].join("\n"),
    replace: [
      "Writing the draft to a file and",
      "uploading it costs one extra call and removes that failure mode completely.",
      "**Write the draft file with the `write` tool, not with a shell heredoc.** The tool call carries",
      "the text, the file on disk is what you can read back and diff, and a fragment that did not match",
      "is reported instead of quietly producing something else. A heredoc is reserved for the one",
      "case `write` cannot serve — content that must not enter the transcript at all (a secret, a",
      "token, anything read out of a secrets file), written by a script that prints nothing. Even then",
      "the delimiter must be quoted (`<<'EOF'`, never bare `<<EOF`): an unquoted one puts the body",
      "through shell substitution, and Cyrillic/CJK markdown is exactly the content most likely to",
      "carry `$`, backticks or backslashes that trigger it.",
    ].join("\n"),
  },

  // ===== petbox-node-authoring ===============================================================
  {
    skill: "petbox-node-authoring",
    delta: "project key: the frontmatter description",
    find: `How to structure the BODY of a ${PROJECT} node or comment so a person reads it, not an agent.`,
    replace: "How to structure the BODY of a PetBox node or comment so a person reads it, not an agent.",
  },
  {
    skill: "petbox-node-authoring",
    delta: "validator path: it sits next to this SKILL.md in pi's skills tree",
    find: [
      "a REAL FILE, `validate-body.mjs`, materialized right next to this SKILL.md on every surface",
      "(`.claude/skills/petbox-node-authoring/`, `.factory/skills/petbox-node-authoring/`) — run it",
      "BEFORE writing the body, from that same directory: `node validate-body.mjs path/to/draft.md`.",
    ].join("\n"),
    replace: [
      "a REAL FILE, `validate-body.mjs`, materialized right next to this SKILL.md in pi's own skills",
      "tree (`~/.pi/agent/skills/petbox-node-authoring/`, or `$PI_AGENT_DIR/skills/petbox-node-authoring/`",
      "when that variable is set) — run it BEFORE writing the body, from that same directory:",
      "`node validate-body.mjs path/to/draft.md`.",
    ].join("\n"),
  },
  {
    skill: "petbox-node-authoring",
    delta: "validator provenance: installed by this script, not by petbox-wire apply",
    find: "`petbox-wire apply`/`wire` writes it to disk byte-for-byte; some write pipelines",
    replace:
      "`tools/pi/skills.mjs` (run by `tools/pi/deploy.sh`) writes it to disk\n" +
      "> byte-for-byte from the kit's own template; some write pipelines",
  },
];

// ---- plumbing -------------------------------------------------------------------------------

function fail(message) {
  console.error(`skills: ${message}`);
  process.exit(1);
}

function readTemplate(relPath) {
  const abs = join(TEMPLATES_ROOT, relPath);
  try {
    return readFileSync(abs, "utf8");
  } catch (e) {
    fail(`cannot read kit template ${abs} (${e instanceof Error ? e.message : String(e)})`);
  }
}

/** Apply every delta for one skill, then prove none was skipped and no project-specific literal
 * survived. Exits non-zero naming the skill, the delta and the anchor. */
function renderForPi(dir, rendered) {
  for (const patch of DELTAS.filter((d) => d.skill === dir)) {
    const count = rendered.split(patch.find).length - 1;
    if (count !== 1) {
      const what = count === 0 ? "NOT FOUND" : `matched ${count} times (expected exactly 1)`;
      fail(
        `${dir}/SKILL.md: pi delta "${patch.delta}" — anchor ${what}. ` +
          `The upstream template changed; fix the template or this delta, do not ship without it.\n` +
          `  anchor: ${JSON.stringify(patch.find)}`,
      );
    }
    rendered = rendered.split(patch.find).join(patch.replace);
  }

  // The belt to the anchors' braces: whatever the deltas claim, the rendered file must not still
  // carry the project literal or an unrendered placeholder.
  const problems = [];
  rendered.split(/\r?\n/).forEach((line, i) => {
    if (line.includes("{{")) problems.push(`${i + 1}: unrendered placeholder — ${line.trim()}`);
    if (line.includes(PROJECT)) problems.push(`${i + 1}: project literal "${PROJECT}" — ${line.trim()}`);
  });
  if (problems.length > 0) {
    fail(
      `${dir}/SKILL.md: the pi delta set is incomplete — ${problems.length} problem line(s):\n  ` +
        problems.join("\n  "),
    );
  }
  return rendered;
}

/** `skills/<dir>/<file>` — the path relative to the agent dir, posix-separated, the way
 * deploy.sh reports `= skills/…` lines. */
function relPath(...parts) {
  return ["skills", ...parts].join("/");
}

/** deploy.sh's own resolution, reproduced: `$PI_AGENT_DIR` when set, else `~/.pi/agent` — the same
 * value deploy.sh computes with `target="${PI_AGENT_DIR:-$HOME/.pi/agent}"` and then passes here as
 * `--target`, so the two can never disagree about where a skill lands. */
function defaultTarget() {
  const env = (process.env.PI_AGENT_DIR ?? "").trim();
  return env.length > 0 ? env : join(homedir(), ".pi", "agent");
}

function parseArgs(argv) {
  let dryRun = false;
  let target;
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--dry-run") {
      dryRun = true;
      continue;
    }
    if (arg === "--target") {
      target = argv[++i];
      if (!target || target.startsWith("--")) fail("--target needs a directory");
      continue;
    }
    fail(`unknown argument: ${arg}`);
  }
  return { dryRun, target: target ?? defaultTarget() };
}

function main() {
  const { dryRun, target } = parseArgs(process.argv.slice(2));

  console.log(`skills -> ${target}${dryRun ? "  (dry run)" : ""}`);

  /** [relPath, content] in install order: SKILL.md first, its extraFiles after. */
  const planned = [];
  for (const dir of SKILL_DIRS) {
    const spec = PROJECT_SKILLS.find((s) => s.dir === dir);
    if (!spec) {
      fail(
        `the kit's PROJECT_SKILLS has no "${dir}" entry — it was renamed or removed upstream. ` +
          `Update SKILL_DIRS here (and the deltas) rather than installing a skill the kit no longer ships.`,
      );
    }
    const rendered = renderForPi(dir, renderSkillTemplate(readTemplate(join(dir, "SKILL.md")), PROJECT, WORKSPACE, ENV_VAR));
    planned.push([relPath(dir, "SKILL.md"), rendered]);

    for (const assetName of spec.extraFiles ?? []) {
      const assetRendered = renderSkillTemplate(readTemplate(join(dir, assetName)), PROJECT, WORKSPACE, ENV_VAR);
      if (assetRendered.includes("{{")) {
        fail(`${dir}/${assetName}: unrendered placeholder in the kit's asset template`);
      }
      planned.push([relPath(dir, assetName), assetRendered]);
    }
  }

  for (const [rel, content] of planned) {
    const abs = join(target, ...rel.split("/"));
    const existing = existsSync(abs) ? readFileSync(abs, "utf8") : undefined;
    if (existing === content) {
      console.log(`  = ${rel}`);
      continue;
    }
    if (dryRun) {
      console.log(`  + ${rel} (would write${existing === undefined ? "" : ", differs"})`);
      continue;
    }
    mkdirSync(dirname(abs), { recursive: true });
    writeFileSync(abs, content, "utf8");
    console.log(`  + ${rel}`);
  }
}

main();

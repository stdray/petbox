// Definition layer cascade: resolve ONE agent definition by laying ordered layers over each
// other, instead of picking one document (spec: definition-layer-cascade; decisions D2/D13).
//
// Order is DECLARED by the caller, never inferred from versions or timestamps: base (shipped in
// the kit) < user < project. The directory list is a PARAMETER of this resolver — where those
// directories physically live is the caller's decision and deliberately not encoded here.
//
// Four operations, and a layer needs all four or "custom" degenerates into "keep a full copy":
//   ADD a role      — a `.json` for a slug nothing below defines (must be COMPLETE: nothing
//                     underneath can supply its tier/capabilities).
//   CHANGE a field  — a `.json` carrying only the fields it overrides. RFC 7396-style, except
//                     `null` is not overloaded as deletion; see REMOVE.
//   REMOVE a role   — `{"slug": "...", "removed": true}`, a TOMBSTONE. Never "the file is
//                     absent": absence is how every layer says "no opinion", so it cannot also
//                     mean "delete", and a tombstone can carry a `reason` a reader can read.
//   REPLACE roster  — `layer.json` with `"mode": "replace"`: everything below is dropped.
//
// PROSE IS NOT MERGED, and this is the rule most likely to be got wrong:
//   `petbox-<slug>.md`        REPLACES the role's notes ENTIRELY — and RESETS the addenda
//                             collected below it. Keeping them would leave an orphan paragraph
//                             commenting on text this layer just threw away.
//   `petbox-<slug>.append.md` ADDS a section, attributed to its layer by name in the heading,
//                             so a reader can see who said it.
//   Both in ONE layer is E4 — a layer that replaces and appends to the same role is
//   contradicting itself; the replacement wins and the append is dropped, loudly.
//
// TWO SCHEMAS, NOT ONE — the trap that makes or breaks this module. agent-definition.ts's
// validateAgentDefinition demands `tier` and `requiredCapabilities` on EVERY role: that is the
// RESOLVE-level schema and it stays exactly as strict as it is. A LAYER is checked by this
// module instead, where those fields are optional — a one-field patch is a legitimate layer and
// must never be asked to look like a whole definition. Completeness is a property of the
// RESULT, checked once, at the end.
//
// NO CACHE, BY DECISION (D15). A layer file either exists and parses or it does not. A broken
// layer FAILS LOUD, naming the file and the parser's own position — it must never fall back to
// a last-known-good copy, because that is precisely the failure that smeared a real outage over
// days: the user broke the source and everything kept working from a stale replica.
//
// Diagnostic codes are the research prototype's (research/wire-source-of-truth/prototype/
// resolve.mjs + RESOLVED.md), so its hand-written walkthrough and this implementation name the
// same defects the same way. E1 lives in definition-integrity.ts — it is a property of the
// RESOLVED definition, shared with apply's own gate, not something the cascade owns privately.
//
// Plain TS for native node type-stripping: zero deps.

import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import type { AgentDefinition, AgentRole, RoleEscalation, RoleSpawn } from "./agent-definition.ts";
import { findDanglingTargets, formatDanglingTargets } from "./definition-integrity.ts";

/** Declared roster policy of one layer. */
export type LayerMode = "replace" | "overlay";

/** `layer.json` — the layer's own identity and policy. */
export type LayerManifest = {
  /** Human name used in provenance, traces and addendum headings. Must be non-empty. */
  readonly name: string;
  /** "overlay" (patch what is below) or "replace" (declare the roster outright). */
  readonly mode: string;
  readonly comment?: string;
};

/**
 * A role document inside a layer. Every field except `slug` is OPTIONAL — that is the entire
 * point of the layer schema (see the header's TWO SCHEMAS note).
 */
export type RolePatch = {
  readonly slug: string;
  /** Tombstone: this role is removed from the resolve. Mutually exclusive with the fields below. */
  readonly removed?: boolean;
  /** Free text explaining a tombstone; carried for readers, never used by the resolver. */
  readonly reason?: string;
  readonly tier?: string;
  readonly requiredCapabilities?: ReadonlyArray<string>;
  readonly spawn?: RoleSpawn;
  readonly escalation?: RoleEscalation;
};

/** Fields a `.json` patch may override. Prose is separate (its own files) — see `notes`. */
export const PATCHABLE_FIELDS = ["tier", "requiredCapabilities", "spawn", "escalation"] as const;
export type PatchableField = (typeof PATCHABLE_FIELDS)[number];
/** Everything provenance is tracked for: the patchable fields plus the role's prose. */
export type ProvenanceField = PatchableField | "notes";

/** Which layer each field of a resolved role came from. Absent key = the role has no such field. */
export type RoleProvenance = Readonly<Partial<Record<ProvenanceField, string>>>;

export type Addendum = { readonly layer: string; readonly text: string };

export type CascadeCode = "E0" | "E1" | "E2" | "E3" | "E4" | "E5" | "W3";

export type CascadeDiagnostic = {
  readonly code: CascadeCode;
  readonly severity: "error" | "warning";
  /** Layer name when known, else the directory (a manifest may be the thing that is broken). */
  readonly layer: string;
  readonly message: string;
};

/** What the cascade DID, step by step — the evidence for "which path did the resolve take". */
export type CascadeTraceEntry =
  | { readonly kind: "reset"; readonly layer: string; readonly dropped: ReadonlyArray<string> }
  | { readonly kind: "add"; readonly layer: string; readonly slug: string; readonly fields: ReadonlyArray<string> }
  | { readonly kind: "remove"; readonly layer: string; readonly slug: string }
  | { readonly kind: "update"; readonly layer: string; readonly slug: string; readonly fields: ReadonlyArray<string> };

/** One role's files inside one layer, after the layer directory has been read. */
export type LayerRoleEntry = {
  readonly slug: string;
  /** Filenames that contributed, sorted — used by W3 to name what changed nothing. */
  readonly files: ReadonlyArray<string>;
  readonly patch?: RolePatch;
  /** `petbox-<slug>.md` — REPLACES notes. */
  readonly prose?: string;
  /** `petbox-<slug>.append.md` — ADDS a section. */
  readonly append?: string;
};

export type DefinitionLayer = {
  readonly dir: string;
  readonly manifest: LayerManifest;
  /** Role slug → its documents in this layer, in sorted slug order. */
  readonly entries: ReadonlyArray<LayerRoleEntry>;
  /** Problems found while READING this layer (E4/E5) — reported, never thrown. */
  readonly diagnostics: ReadonlyArray<CascadeDiagnostic>;
};

export type CascadeResolution = {
  /** The resolved document, ready for planApply — same shape any other source produces. */
  readonly definition: AgentDefinition;
  /** Layers actually read, in the order applied (lowest priority first). */
  readonly layers: ReadonlyArray<{ readonly name: string; readonly dir: string; readonly mode: string }>;
  /** Role slug → per-field origin layer. */
  readonly provenance: ReadonlyMap<string, RoleProvenance>;
  /** Role slug → addendum sections, in the order they were appended. */
  readonly addenda: ReadonlyMap<string, ReadonlyArray<Addendum>>;
  readonly trace: ReadonlyArray<CascadeTraceEntry>;
  readonly diagnostics: ReadonlyArray<CascadeDiagnostic>;
};

/**
 * A layer source is missing or unreadable. Thrown, never collected: D15 — a broken source must
 * fail loud with the file that broke and the parser's own position, never degrade quietly into
 * a stale-but-working resolve.
 */
export class LayerSourceError extends Error {
  readonly path: string;
  constructor(path: string, message: string) {
    super(message);
    this.name = "LayerSourceError";
    this.path = path;
  }
}

const ROLE_FILE_RE = /^petbox-([a-z0-9-]+)\.(json|md|append\.md)$/;
const MANIFEST_FILE = "layer.json";

function readJson(path: string): unknown {
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch (err) {
    throw new LayerSourceError(
      path,
      `definition layer: cannot read ${path}: ${err instanceof Error ? err.message : String(err)}`,
    );
  }
  try {
    return JSON.parse(raw) as unknown;
  } catch (err) {
    // The V8 message already carries "position N (line L column C)" — keep it verbatim; it is
    // the whole reason this throws instead of falling back to anything (D15).
    throw new LayerSourceError(
      path,
      `definition layer: ${path} is not valid JSON — ${err instanceof Error ? err.message : String(err)}`,
    );
  }
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function readManifest(dir: string): LayerManifest {
  const path = join(dir, MANIFEST_FILE);
  if (!existsSync(path)) {
    throw new LayerSourceError(
      path,
      `definition layer: ${dir} has no ${MANIFEST_FILE} — every layer must name itself and ` +
        `declare its mode ("overlay" or "replace"); a directory without one is not a layer`,
    );
  }
  const parsed = readJson(path);
  if (!isRecord(parsed)) {
    throw new LayerSourceError(path, `definition layer: ${path} must be a JSON object`);
  }
  const name = parsed["name"];
  const mode = parsed["mode"];
  if (typeof name !== "string" || !name.trim()) {
    throw new LayerSourceError(path, `definition layer: ${path} has no non-empty "name"`);
  }
  if (typeof mode !== "string" || !mode.trim()) {
    throw new LayerSourceError(path, `definition layer: ${path} has no non-empty "mode"`);
  }
  const comment = parsed["comment"];
  return {
    name: name.trim(),
    mode: mode.trim(),
    ...(typeof comment === "string" ? { comment } : {}),
  };
}

/**
 * Read one layer directory. Throws (LayerSourceError) when the SOURCE is broken — missing
 * directory, missing/invalid `layer.json`, unparseable role JSON. Collects (never throws) the
 * problems that are about the layer's CONTENT rather than its readability: E5 (a filename or
 * slug that does not follow the schema) and E4 (replace + append for the same role).
 */
export function readDefinitionLayer(dir: string): DefinitionLayer {
  if (!existsSync(dir) || !statSync(dir).isDirectory()) {
    throw new LayerSourceError(dir, `definition layer: ${dir} does not exist or is not a directory`);
  }
  const manifest = readManifest(dir);
  const diagnostics: CascadeDiagnostic[] = [];
  const byName = new Map<string, { files: string[]; patch?: RolePatch; prose?: string; append?: string }>();

  for (const file of readdirSync(dir).sort()) {
    if (file === MANIFEST_FILE) continue;
    const abs = join(dir, file);
    try {
      if (!statSync(abs).isFile()) continue;
    } catch {
      continue;
    }
    const m = ROLE_FILE_RE.exec(file);
    if (!m) {
      diagnostics.push({
        code: "E5",
        severity: "error",
        layer: manifest.name,
        message: `${abs}: filename does not follow petbox-<slug>.{json,md,append.md}`,
      });
      continue;
    }
    const slug = m[1] as string;
    const kind = m[2] as string;
    const entry = byName.get(slug) ?? { files: [] };
    entry.files.push(file);
    if (kind === "json") {
      const parsed = readJson(abs);
      if (!isRecord(parsed)) {
        throw new LayerSourceError(abs, `definition layer: ${abs} must be a JSON object`);
      }
      const patchSlug = parsed["slug"];
      if (patchSlug !== slug) {
        diagnostics.push({
          code: "E5",
          severity: "error",
          layer: manifest.name,
          message: `${abs}: "slug" is ${JSON.stringify(patchSlug)} but the filename says "${slug}"`,
        });
      }
      entry.patch = { ...(parsed as Record<string, unknown>), slug } as RolePatch;
    } else {
      let text: string;
      try {
        text = readFileSync(abs, "utf8");
      } catch (err) {
        throw new LayerSourceError(
          abs,
          `definition layer: cannot read ${abs}: ${err instanceof Error ? err.message : String(err)}`,
        );
      }
      if (kind === "md") entry.prose = text.trimEnd();
      else entry.append = text.trimEnd();
    }
    byName.set(slug, entry);
  }

  const entries: LayerRoleEntry[] = [];
  for (const slug of [...byName.keys()].sort()) {
    const e = byName.get(slug) as { files: string[]; patch?: RolePatch; prose?: string; append?: string };
    let append = e.append;
    if (e.prose !== undefined && append !== undefined) {
      diagnostics.push({
        code: "E4",
        severity: "error",
        layer: manifest.name,
        message:
          `role "${slug}" both REPLACES prose (petbox-${slug}.md) and APPENDS to it ` +
          `(petbox-${slug}.append.md) in the same layer — pick one. The replacement wins; the ` +
          `append is ignored (it would comment on text this same layer just discarded).`,
      });
      append = undefined;
    }
    entries.push({
      slug,
      files: [...e.files].sort(),
      ...(e.patch !== undefined ? { patch: e.patch } : {}),
      ...(e.prose !== undefined ? { prose: e.prose } : {}),
      ...(append !== undefined ? { append } : {}),
    });
  }

  return { dir, manifest, entries, diagnostics };
}

type WorkingRole = {
  slug: string;
  tier?: string;
  requiredCapabilities?: ReadonlyArray<string>;
  spawn?: RoleSpawn;
  escalation?: RoleEscalation;
  notes: string;
  provenance: Partial<Record<ProvenanceField, string>>;
  addenda: Addendum[];
};

function sameValue(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

export type ResolveLayersOptions = {
  /**
   * Name of the resolved document. Default: the layer names joined lowest-first with " < " —
   * self-documenting in apply's own log line ("which layers did this come from").
   */
  readonly name?: string;
};

/**
 * Resolve `dirs` (lowest priority FIRST) into one definition, with per-field provenance, a
 * trace and a diagnostic report. Throws only on a broken SOURCE (LayerSourceError); every
 * content problem comes back as a diagnostic so the caller can print the whole report at once
 * instead of one error per run.
 *
 * The returned `definition` is always structurally complete — an incomplete new role is an E3
 * and is NOT added — so a caller that checks `cascadeErrors(...)` is empty may hand it straight
 * to planApply.
 */
export function resolveDefinitionLayers(
  dirs: ReadonlyArray<string>,
  options: ResolveLayersOptions = {},
): CascadeResolution {
  if (dirs.length === 0) {
    throw new LayerSourceError("", "definition layer: at least one layer directory is required");
  }

  const diagnostics: CascadeDiagnostic[] = [];
  const trace: CascadeTraceEntry[] = [];
  const layers: { name: string; dir: string; mode: string }[] = [];
  let roles = new Map<string, WorkingRole>();

  for (const dir of dirs) {
    const layer = readDefinitionLayer(dir);
    const name = layer.manifest.name;
    layers.push({ name, dir: layer.dir, mode: layer.manifest.mode });
    diagnostics.push(...layer.diagnostics);

    const mode = layer.manifest.mode;
    if (mode !== "replace" && mode !== "overlay") {
      diagnostics.push({
        code: "E0",
        severity: "error",
        layer: name,
        message: `${join(dir, MANIFEST_FILE)}: unknown mode "${mode}" — expected "overlay" or "replace"; treated as overlay`,
      });
    }
    if (mode === "replace") {
      if (roles.size > 0) {
        trace.push({ kind: "reset", layer: name, dropped: [...roles.keys()].sort() });
      }
      roles = new Map();
    }

    let touched = 0;
    for (const entry of layer.entries) {
      const slug = entry.slug;
      const prev = roles.get(slug);

      // REMOVE — a tombstone. Nothing to remove is E2: the layer is stale, or the base renamed
      // the role underneath it. Silently ignoring it would hide exactly that.
      if (entry.patch?.removed === true) {
        if (!prev) {
          diagnostics.push({
            code: "E2",
            severity: "error",
            layer: name,
            message: `tombstone for role "${slug}", which no layer below defines — stale layer, or the role was renamed underneath it`,
          });
          continue;
        }
        roles.delete(slug);
        touched++;
        trace.push({ kind: "remove", layer: name, slug });
        continue;
      }

      // ADD — nothing below defines this slug, so this layer must supply a COMPLETE role.
      if (!prev) {
        const patch = entry.patch;
        if (!patch) {
          diagnostics.push({
            code: "E3",
            severity: "error",
            layer: name,
            message: `new role "${slug}" has prose (${entry.files.join(", ")}) but no petbox-${slug}.json — nothing below defines it, so there is no role to attach prose to`,
          });
          continue;
        }
        const missing: string[] = [];
        if (patch.tier === undefined) missing.push("tier");
        if (patch.requiredCapabilities === undefined) missing.push("requiredCapabilities");
        if (missing.length > 0) {
          diagnostics.push({
            code: "E3",
            severity: "error",
            layer: name,
            message: `new role "${slug}" is incomplete, missing: ${missing.join(", ")} — nothing below can supply them`,
          });
          continue;
        }
        const provenance: Partial<Record<ProvenanceField, string>> = {
          tier: name,
          requiredCapabilities: name,
        };
        const fields: string[] = ["tier", "requiredCapabilities"];
        if (patch.spawn !== undefined) {
          provenance.spawn = name;
          fields.push("spawn");
        }
        if (patch.escalation !== undefined) {
          provenance.escalation = name;
          fields.push("escalation");
        }
        if (entry.prose !== undefined) {
          provenance.notes = name;
          fields.push("notes");
        }
        roles.set(slug, {
          slug,
          ...(patch.tier !== undefined ? { tier: patch.tier } : {}),
          ...(patch.requiredCapabilities !== undefined
            ? { requiredCapabilities: patch.requiredCapabilities }
            : {}),
          ...(patch.spawn !== undefined ? { spawn: patch.spawn } : {}),
          ...(patch.escalation !== undefined ? { escalation: patch.escalation } : {}),
          notes: entry.prose ?? "",
          provenance,
          addenda: entry.append !== undefined ? [{ layer: name, text: entry.append }] : [],
        });
        if (entry.append !== undefined) fields.push("addendum");
        touched++;
        trace.push({ kind: "add", layer: name, slug, fields });
        continue;
      }

      // CHANGE — field patch and/or prose over what is already resolved.
      const next: WorkingRole = {
        ...prev,
        provenance: { ...prev.provenance },
        addenda: [...prev.addenda],
      };
      const changed: string[] = [];
      const patch = entry.patch;
      if (patch) {
        for (const field of PATCHABLE_FIELDS) {
          const incoming = patch[field];
          if (incoming === undefined) continue;
          if (sameValue(prev[field], incoming)) {
            diagnostics.push({
              code: "W3",
              severity: "warning",
              layer: name,
              message: `${slug}.${field} is set to the value it already had — this layer changes nothing here`,
            });
          }
          // Assigned through a narrow cast: each PATCHABLE_FIELDS key has the same type on the
          // patch and on the working role, but TS cannot see that through the loop variable.
          (next as Record<string, unknown>)[field] = incoming;
          next.provenance[field] = name;
          changed.push(field);
        }
      }
      if (entry.prose !== undefined) {
        if (entry.prose === prev.notes) {
          diagnostics.push({
            code: "W3",
            severity: "warning",
            layer: name,
            message: `prose of "${slug}" is byte-identical to the layer below — that is a REPLICA, not a layer`,
          });
        }
        next.notes = entry.prose;
        // Replace, not merge: addenda written against the discarded text go with it.
        next.addenda = [];
        next.provenance.notes = name;
        changed.push(prev.addenda.length > 0 ? "notes (replaced; addenda reset)" : "notes (replaced)");
      }
      if (entry.append !== undefined) {
        next.addenda.push({ layer: name, text: entry.append });
        changed.push("addendum");
      }
      if (changed.length === 0) {
        diagnostics.push({
          code: "W3",
          severity: "warning",
          layer: name,
          message: `${entry.files.join(", ")} change nothing for role "${slug}"`,
        });
        continue;
      }
      roles.set(slug, next);
      touched++;
      trace.push({ kind: "update", layer: name, slug, fields: changed });
    }

    if (touched === 0 && mode !== "replace") {
      diagnostics.push({
        code: "W3",
        severity: "warning",
        layer: name,
        message: `layer changes nothing at all — a replica, not a layer`,
      });
    }
  }

  const provenance = new Map<string, RoleProvenance>();
  const addenda = new Map<string, ReadonlyArray<Addendum>>();
  const resolvedRoles: AgentRole[] = [];
  for (const [slug, r] of roles) {
    provenance.set(slug, r.provenance);
    addenda.set(slug, r.addenda);
    const notes = renderNotes(r);
    resolvedRoles.push({
      slug,
      tier: r.tier as string,
      requiredCapabilities: r.requiredCapabilities as ReadonlyArray<string>,
      ...(r.spawn !== undefined ? { spawn: r.spawn } : {}),
      ...(r.escalation !== undefined ? { escalation: r.escalation } : {}),
      ...(notes ? { notes } : {}),
    });
  }

  const definition: AgentDefinition = {
    name: options.name ?? layers.map((l) => l.name).join(" < "),
    roles: resolvedRoles,
  };

  // E1 is a property of the RESULT, so it runs last and over the same shared check apply's own
  // gate uses — a cascade that subtracts a role is precisely how a dangling target is born.
  for (const d of findDanglingTargets(definition)) {
    diagnostics.push({
      code: "E1",
      severity: "error",
      layer: provenance.get(d.role)?.[d.field === "spawn.allowedRoles" ? "spawn" : "escalation"] ?? "",
      message: `${d.role}.${d.field} → "${d.target}": no such role after the cascade`,
    });
  }

  return { definition, layers, provenance, addenda, trace, diagnostics };
}

/**
 * Final notes text for a role: replaced prose first, then each addendum as its own section
 * headed by the layer that contributed it — a reader must be able to see WHO said a paragraph,
 * which is the whole difference between an addendum and an anonymous merge.
 */
function renderNotes(r: WorkingRole): string {
  const parts: string[] = [];
  if (r.notes.trim()) parts.push(r.notes.trimEnd());
  for (const a of r.addenda) {
    if (!a.text.trim()) continue;
    parts.push(`## Layer addendum (${a.layer})\n\n${a.text.trimEnd()}`);
  }
  return parts.join("\n\n");
}

export function cascadeErrors(resolution: CascadeResolution): ReadonlyArray<CascadeDiagnostic> {
  return resolution.diagnostics.filter((d) => d.severity === "error");
}

export function cascadeWarnings(resolution: CascadeResolution): ReadonlyArray<CascadeDiagnostic> {
  return resolution.diagnostics.filter((d) => d.severity === "warning");
}

/** One human line per trace entry — "which path did the resolve take", printable as-is. */
export function formatCascadeTrace(resolution: CascadeResolution): string {
  return resolution.trace
    .map((t) => {
      switch (t.kind) {
        case "reset":
          return `  ${t.layer}: mode=replace — layers below DROPPED (${t.dropped.join(", ")})`;
        case "add":
          return `  ${t.layer}: + ${t.slug} (new role: ${t.fields.join(", ")})`;
        case "remove":
          return `  ${t.layer}: − ${t.slug} (tombstone)`;
        case "update":
          return `  ${t.layer}: ~ ${t.slug} → ${t.fields.join(", ")}`;
      }
    })
    .join("\n");
}

/** `<code> [<layer>] <message>` per diagnostic, warnings first, then errors (prototype order). */
export function formatCascadeReport(resolution: CascadeResolution): string {
  const line = (d: CascadeDiagnostic) =>
    `  ${d.code}${d.layer ? ` [${d.layer}]` : ""} ${d.message}`;
  const out = [...cascadeWarnings(resolution).map(line), ...cascadeErrors(resolution).map(line)];
  return out.length > 0 ? out.join("\n") : "  clean";
}

/** `<slug>  tier=<t>  provenance: field=layer ...` per resolved role, in resolve order. */
export function formatCascadeProvenance(resolution: CascadeResolution): string {
  return resolution.definition.roles
    .map((role) => {
      const from = resolution.provenance.get(role.slug) ?? {};
      const fields = Object.entries(from)
        .map(([k, v]) => `${k}=${v}`)
        .join(" ");
      const add = (resolution.addenda.get(role.slug) ?? [])
        .map((a) => `addendum=${a.layer}`)
        .join(" ");
      return `  ${role.slug}  tier=${role.tier}  provenance: ${[fields, add].filter(Boolean).join(" ")}`;
    })
    .join("\n");
}

/** Re-exported so a caller needs one import to gate a cascade AND a fetched definition alike. */
export { findDanglingTargets, formatDanglingTargets };

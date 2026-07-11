// Pure identity resolution for the full wire: the per-project API-key env var name and the
// workspace used in the skill template.
//
// Kept in a tiny side-effect-free module (not wire.ts) so unit tests can import it — wire.ts
// runs main() at module top level and must never be imported by a test (see posix-env.ts /
// bin/petbox-wire.js for the same reasoning).

import { WIRE_EXIT } from "./wire-exit.ts";

// Canonical env-var name for a project key. MUST stay byte-identical to the server's EnvSlug
// (PetBox.Web/Pages/Admin/ProjectConnect.cshtml.cs), which is what the Connect page shows an
// operator to paste: uppercase, every run of non-alphanumerics collapsed to a single "_",
// leading/trailing "_" trimmed, wrapped in PETBOX_…_API_KEY.
//
//   "$system" → "PETBOX_SYSTEM_API_KEY"   (not "_SYSTEM_API_KEY": the leading "$" is trimmed)
//   "kpvotes" → "PETBOX_KPVOTES_API_KEY"
//
// This is only the DEFAULT for a fresh wire. An already-wired directory keeps the env-var name
// recorded in ~/.petbox/projects.json (registryEnvVar in wire.ts wins over this), and --env
// overrides both — so changing this name never orphans an existing machine's keys.json entry.
export function deriveEnvVar(projectKey: string): string {
  // Same operation ORDER as EnvSlug: sanitise+trim first, uppercase last. Uppercasing first would
  // diverge on non-ASCII letters that case-fold into ASCII (C#: "ı" → "_", uppercase-first: "I").
  const slug = projectKey
    .replace(/[^A-Za-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .toUpperCase();
  return `PETBOX_${slug}_API_KEY`;
}

export type WorkspaceResolution =
  | { ok: true; workspace: string; source: "flag" | "server" }
  | { ok: false; message: string; exitCode: number };

const nonEmpty = (v: string | undefined | null): string | undefined => {
  const s = typeof v === "string" ? v.trim() : "";
  return s.length > 0 ? s : undefined;
};

// Workspace for the skill template's {{WORKSPACE}}. Resolution order:
//   1. --workspace WS                       (explicit operator override)
//   2. the `workspace` field of GET /api/auth/validate (the server owns this)
//   3. hard fail, exit 2 (usage) — NEVER a hardcoded personal workspace.
// An old server that predates the `workspace` field simply reports none → case 3, a clear
// actionable usage error rather than a crash or a silent wrong default.
export function resolveWorkspace(
  flag: string | undefined,
  serverWorkspace: string | undefined,
): WorkspaceResolution {
  const fromFlag = nonEmpty(flag);
  if (fromFlag) return { ok: true, workspace: fromFlag, source: "flag" };

  const fromServer = nonEmpty(serverWorkspace);
  if (fromServer) return { ok: true, workspace: fromServer, source: "server" };

  return {
    ok: false,
    exitCode: WIRE_EXIT.usage,
    message:
      "--workspace is required: this server did not report a workspace.\n" +
      "  GET /api/auth/validate returned no `workspace` field (an older PetBox server).\n" +
      "  Re-run with --workspace <WS> (the workspace the project belongs to), or upgrade the server.",
  };
}

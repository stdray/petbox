# `src/common` — artifacts consumed by more than one language

Files here are the SINGLE canonical copy of something both the .NET server and a client kit need.
They are plain data (JSON), never code, because the only property that makes them worth this
directory is that no side can be edited without the others seeing it.

## `default-agents.json`

The portable agent roster (`agent-definition-as-data`): six roles with `tier`,
`requiredCapabilities`, `spawn`, `escalation` and `notes` prose. Two consumers, one file:

- **The server** embeds it (`PetBox.Core.csproj` → `EmbeddedResource`) and
  `PetBox.Core.Contract.DefaultAgentDefinition` deserializes + validates it at first use. It is
  seeded into every project the server creates (`ProjectAgentDefSeeder`), so a fresh project's
  AUTHORITATIVE definition exists instead of being empty.
- **The wiring kit** (`src/clients-ts/petbox-wire`) exports it as `DEFAULT_AGENT_DEFINITION` — its
  OFFLINE fallback when PetBox is unreachable and no LKG cache exists. The kit must work with no
  network, so `scripts/sync-default-agents.mjs` COPIES this file into the package's own `src/`
  before test/typecheck/pack; that copy is gitignored precisely so it cannot be hand-edited into a
  divergent third version, and `package.json`'s `files` allowlist puts it in the published tarball.

There is no ratchet test between the two, because there is nothing to ratchet: they read the same
bytes. Editing this file changes both sides at once — which is the point.

**Editing it does NOT re-seed existing projects.** A project owns its definition from the moment it
is created (that is why it is seeded at all — so it can be edited); the seeder only ever fills an
ABSENT one. A "re-seed from the updated baseline, showing a diff" path is deliberately a separate
piece of work.

Constraints the document must keep (enforced on load, on both sides):

- No property named `model`, anywhere. Model binding is LOCAL (`~/.petbox/roles.json`), never part
  of a portable definition.
- Every role carries a non-empty `slug`, `tier` and `notes`, and a `requiredCapabilities` array
  (possibly empty).
- Slugs are unique, and every slug named in `spawn.allowedRoles` / `escalation.targets` resolves to
  a role in this same document.

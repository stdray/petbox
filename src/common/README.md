# `src/common` — artifacts consumed by more than one language

Files here are the SINGLE canonical copy of something both the .NET server and a client kit need.
They are plain data (JSON), never code, because the only property that makes them worth this
directory is that no side can be edited without the others seeing it.

## `default-agents.json`

The portable agent roster (`agent-definition-as-data`): five roles with `tier`,
`requiredCapabilities`, `spawn`, `escalation` and `notes` prose. Two consumers, one file:

- **The server** embeds it (`PetBox.Core.csproj` → `EmbeddedResource`) and
  `PetBox.Core.Contract.DefaultAgentDefinition` deserializes + validates it at first use. That is
  ALL it does with it: a CI ratchet, so a malformed or self-inconsistent roster fails the build here
  rather than on a user's machine at wire time (where a bad document means an agent renders with no
  briefing, or does not render at all, and the failure reads like a harness bug). The server does not
  serve this document to anyone and does not store a per-project copy of it — the store that used to
  do both, with its REST/MCP/admin surfaces and its table, is gone (work `agent-defs-server-teardown`).
  Nothing the server holds is on the path between this file and a rendered agent.
- **The wiring kit** (`src/clients-ts/petbox-wire`) exports it as `DEFAULT_AGENT_DEFINITION` — the
  `base` LAYER at the bottom of its definition cascade (base < user < project), which is how the kit
  resolves a definition for every command and every SessionStart hook. It is not a fallback for a
  failed fetch: the kit does not fetch definitions at all. It is the floor, always present, and its
  absence throws at import rather than yielding an empty roster. So the kit must carry it
  physically: `scripts/sync-default-agents.mjs` COPIES this file into the package's own `src/`
  before test/typecheck/pack; that copy is gitignored precisely so it cannot be hand-edited into a
  divergent third version, and `package.json`'s `files` allowlist puts it in the published tarball.

There is no ratchet test between the two, because there is nothing to ratchet: they read the same
bytes. Editing this file changes both sides at once — which is the point.

**Editing it reaches a machine when that machine next installs the kit and runs `petbox-wire apply`**
— that is the whole delivery path, and there is no other. Nothing is pushed, nothing is re-seeded,
and no server-side copy has to be reconciled: a checkout on an older kit version keeps rendering from
the roster its own package carries until it upgrades.

**Editing it does NOT override anyone's local changes**, because it is the BOTTOM of the cascade. A
role field set in `~/.petbox/agents` (user) or `<root>/.petbox/agents` (project) keeps winning after
this file moves; what changes is only the value those layers are laid over. `petbox-wire layers`
prints which layer supplied every field, so the effect of an edit here is inspectable per field
rather than guessed at.

Constraints the document must keep (enforced on load, on both sides):

- No property named `model`, anywhere. Model binding is LOCAL (`~/.petbox/roles.json`), never part
  of a portable definition.
- Every role carries a non-empty `slug`, `tier` and `notes`, and a `requiredCapabilities` array
  (possibly empty).
- Slugs are unique, and every slug named in `spawn.allowedRoles` / `escalation.targets` resolves to
  a role in this same document.
- Every `requiredCapabilities` id is one a harness actually declares
  (`src/clients-ts/petbox-wire/src/harness-capabilities.ts`, mirrored server-side by
  `AgentDefinitionCapabilities` and pinned equal to it by `AgentDefinitionCapabilitiesSyncTests`).
  This one is enforced on THIS document only: a typo'd id fails nowhere at run time — the kit's
  compiler simply emits no artifact for a role whose requirements nothing meets, so the role
  silently is not there. A user's or project's own layer stays free to name a capability a future
  harness declares.

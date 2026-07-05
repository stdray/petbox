# UI terminology lexicon

Canonical display-text vocabulary for the PetBox UI (Razor Pages chrome). This is the
outcome of the `ui-terminology-pass` audit card: for each term that diverged across the
admin and user zones, the word that wins, where it applies, and why. **Future UI work
follows this table.** It governs *display text only* — labels, headings, sidebar entries,
page titles, buttons, empty states. It does **not** govern routes/URLs, page-handler names,
`asp-page` targets, `data-testid` values, entity keys, C# identifiers, MCP/REST field
names, or stored status slugs. Those are contracts; see "Known deferred inconsistencies".

## Canonical terms

| Contested | Canonical | Where it applies | Rationale |
|-----------|-----------|------------------|-----------|
| tenant / workspace | **Workspace** | Everywhere (admin prose + headings + user nav) | The entity is `Workspace`; "tenant" was internal jargon leaking into copy. Match the model. |
| data / databases | **Databases** (collection); **Data** kept only as the *module/feature* name | Nav labels, page headings, collection empty states → "Databases". "The Data module is disabled" stays (feature name). | Users interact with concrete databases; the admin zone said "Data", the user zone said "Databases". The entity is `Database`/`DataDb` → the concrete plural wins for the collection. |
| task boards / tasks | **Tasks** (section/module); **board** (one board); **boards** (collection inside the Tasks section) | Nav + page section = "Tasks". Board list heading = "… · tasks". Counts/empty states inside the section = "boards" ("No boards yet"). Qualified "task boards" is kept only in **mixed-resource prose** (project-delete confirm, agent-connect capability list, dashboard stat grid) where "board" alone is ambiguous. | The module is Tasks; a board is a board. Redundant "task boards" inside the already-Tasks section is noise; the qualifier earns its keep only where many resource types are listed together. Aligns the admin board-list heading with its own page title ("— Tasks"). |
| binding / config | **Config** (module/section); **binding** (one entry) | Nav = "Config". A single stored entry = "binding" (New binding / Edit binding / Delete binding). | Already the entity split (`Binding` under the Config module). Kept and codified — not a rename, a rule: never call an entry a "config", never call the module "bindings". |
| Account / Profile | **Account** | The `/ui/me/account` page title AND its sidebar entry. Section chrome ("Signed in as", nav "Account settings") already says Account. | The page shows account identity (username, id, role), not an editable profile. The sidebar entry linked to it now matches the page it opens. |
| node / task / plan node | **Node** (the item on a board); "plan node" acceptable in prose (e.g. delete confirm) | Node detail page, relation panel. | "node" is the addressable unit (detail page, relations). "plan node" is the fuller descriptive form, fine in explanatory prose; avoid bare "task" for the unit. |

## Status display casing (centralized)

Work-board statuses are stored PascalCase (`InProgress`), methodology-board statuses
lowercase (`defined`). In **display** they were rendered as raw slugs, so casing clashed.

- **Rule:** the UI renders a status's **declared human `Name`**, never the raw slug.
  `InProgress → "In progress"`, `defined → "Defined"`, `wontfix → "Won't fix"`.
- **The stored slug is unchanged** (data-status attributes, POST values, the wire contract,
  status-slug persistence all keep the slug).
- **Single source of truth:** `MethodologyRuntime.StatusName(kindSlug, statusSlug)` →
  the declared `WorkflowStatus.Name` (preset or defined-kind), falling back to the slug
  verbatim for an out-of-vocab legacy status. Backed by `MethodologyPresets.NameOfSlug`.
- **One rendering site:** the shared `_StatusBadge` partial (`StatusBadgeModel.Display`).
  The node-page status-change `<select>` reuses the same helper for its option labels
  (option *values* stay slugs). No per-view casing logic.

## Admin-by-Key vs user-by-Name titles — decision

The admin zone titles by **Key** (`$system · databases`); the user zone titles by **Name**
(`System · databases`). This is kept as a **deliberate, documented convention**, not flattened:

- **User zone → Name-primary.** Human-facing; people recognize a workspace/project by name.
- **Admin zone → Key-primary.** The admin zone is the operational surface where the key is
  the addressable identity (URLs, MCP scope, config tags all key on it); showing the key is
  the useful thing there. Where an admin page already pairs them (e.g. Workspace detail shows
  Name with the Key as a muted mono badge), that pattern is the preferred richer form and new
  admin pages should follow it when cheap.

No mass rewrite of admin headings was done (large, and many are asserted by tests); the
convention above is the rule going forward.

## Known deferred inconsistencies (do NOT rename yet)

These are route/testid/slug-level names that *diverge from the canonical display term* but are
**contracts** — renaming them breaks links, E2E `data-testid` selectors, or the wire. Left
intentionally; track as follow-up if ever addressed (each needs a redirect/testid-migration plan):

- **Databases vs `data` in routes/testids:** display is "Databases", but `Routes.ProjectData`,
  the URL segment `/data` (admin) vs `/databases` (user), and testids `admin-side-proj-data` /
  `nav-proj-data` / `project-data-*` / `datadbs-*` keep "data". Route segments themselves also
  diverge between zones (`/data` vs `/databases`).
- **Account vs `profile`/`account`:** display is "Account", but the route helper is
  `Routes.MeProfile()`, the testid is `account-side-profile`, and the URL is `/ui/me/account`
  (Profile handler ↔ account URL — already divergent at the contract level).
- **`node-status` testid:** the badge now shows the human status Name; the testid stays
  `node-status` (tests select by it).
- **Status slugs are not display:** `InProgress` / `defined` etc. remain the stored/wire values;
  only their rendering was normalized.
- **`/log` vs `/logs`:** pre-existing route naming inconsistency called out in the audit brief;
  not a display-text issue, left as-is.
- **Empty-state testids** (`boards-empty`, `tasks-empty`, `datadbs-empty`) keep their names even
  though the visible copy changed.

## RU chrome translated

Per the English-only-chrome invariant (AGENTS.md), the one Russian inline error string surfaced
in the UI was translated: the stale-node conflict message on the task node page
(`TaskBoardNode.cshtml.cs`) → "This node changed since the page was opened … refresh and redo
your edit." (Russian text in backend search stemming, LLM prompt bodies, and preset methodology
checklist data is language-support/agent-facing content or asserted contract, not Razor chrome —
left untouched.)

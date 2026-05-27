# UI conventions

Canonical choices for building YobaBox UI. Keep this short. If a recipe isn't here, follow what's already in `Pages/` and add a line here.

Hard invariants live in `AGENTS.md` ("Hard invariants" section) — localization, `data-testid`, no inline JS, English-only chrome. This doc covers component-level "how" only.

## Stack

- Tailwind 3.4 + daisyUI 4
- Alpine.js 3.14 for client-only state (chips, modal show/hide, dropdowns)
- htmx 2.0 for server-driven updates (log auto-refresh, partial swaps)
- TS bundled by bun → `ts/site.ts` is the entry, per-page init from there

## Theme

- `data-theme="dark"` on `<html>`. Only `dark` is used. `night` / `business` from `tailwind.config` are unused; keep them in config but don't switch via UI.
- Body: `min-h-full`. HTML: `h-full`.

## Layout shell

- Drawer-based responsive: `drawer lg:drawer-open` on outer container. Sidebar in `drawer-side`, content in `drawer-content`.
- Top bar (navbar): `navbar bg-base-200 border-b border-base-300 px-2 lg:px-4 min-h-12`.
- Sidebar: `bg-base-200 w-64 min-h-full border-r border-base-300 flex flex-col`.

## Typography

- Page title (`<h1>`): `text-2xl font-semibold` (preferred) or `text-xl font-semibold` for dense pages.
- Section header: `text-xs opacity-50 uppercase tracking-wide`.
- Body text: default.
- Muted/secondary: `opacity-60`. Even more subtle: `opacity-50`.
- Code, keys, identifiers, project keys: `font-mono`. Always.

## Containers / sections

- Grouped content block: `bg-base-200 border border-base-300 rounded-box p-4`.
- Collapsible section: `<details>` with `summary class="cursor-pointer font-semibold px-4 py-2 select-none text-sm"` and `class="bg-base-200 border border-base-300 mb-4 rounded-box"` on the `<details>`.
- Spacing between major blocks: `mb-4`. Tight: `mb-3`. Inside a block: `gap-3` or `gap-4`.

## Buttons

- Primary action: `btn btn-sm btn-primary` (or `btn btn-xs btn-primary` in toolbar rows).
- Secondary: `btn btn-sm btn-ghost`.
- Destructive: `btn btn-sm btn-ghost` with `onclick="return confirm('…?');"`. No fancy framework — explicit and works without JS.
- Icon-only: `btn btn-ghost btn-sm` with SVG child (h-4 w-4 or h-5 w-5).
- Grouped buttons (filter chips, pagination): `<span class="join">` wrapping `class="btn ... join-item"` elements.
- Active state on filter chips: swap `btn-ghost` → `btn-primary`.

## Forms

- Wrap each field: `<label class="form-control">` containing `<span class="label-text">…</span>` then the input.
- Inputs: `input input-bordered input-sm` (preferred). `input-xs` only in tight toolbar rows.
- Selects: `select select-bordered select-sm` (or `select-xs` for tight rows).
- Textareas: `textarea textarea-bordered`.
- Filter forms: `flex flex-wrap items-end gap-2`.
- Search/filter forms use `method="get"`. CRUD uses `method="post"` + `@Html.AntiForgeryToken()`.
- For `font-mono` payloads (keys, KQL): add `font-mono text-xs` to the input.

## Tables

- Default: `table table-sm`.
- Dense (telemetry, ingestion endpoints): `table table-xs`.
- Always wrap in `<div class="overflow-x-auto">` if any column may be wide.

## Alerts / flash

- `alert alert-error mb-4 text-sm` for errors.
- `alert alert-success mb-3` for confirmations.
- `alert alert-warning` for non-blocking warnings.
- Always with `data-testid` like `{page}-flash-error` / `{page}-flash-success`.

## Empty states

- One-liner inline: `<div class="text-xs opacity-60 italic" data-testid="{section}-empty">No X yet.</div>`.
- Full-page empty (e.g. no projects in workspace): card with centered text + primary CTA button.

## Modals

- daisyUI `modal modal-open` toggled by Alpine `x-data`/`x-show` (since Alpine handles client state).
- Confirmation for destructive: prefer `onclick="return confirm(...)"` for simple deletes; reserve modals for multi-field flows or anything with consequences worth re-stating.

## Badges

- Workspace/key context: `badge badge-outline font-mono`.
- Status tags: `badge badge-success/error/warning/info`.
- Tiny inline badge ($system marker etc.): `badge badge-ghost badge-xs`.

## Tabs

- Project page tabs and Config sub-tabs: `tabs tabs-bordered`.
- Each tab: `<a class="tab" :class="{ 'tab-active': … }">`. Active state via Razor on server-side render (no JS needed if URL changes).

## Client interactivity boundary

- **htmx** for: log auto-refresh (`hx-get` + `hx-trigger="every Ns"` + `hx-swap`), trace waterfall expand, any "load fragment from server".
- **Alpine** for: chip add/remove on filter rows, modal open/close, dropdown toggle, conditional reveals, any client-only state.
- **Plain forms** for: GET filters, POST CRUD. Default.
- **No inline JS in `.cshtml`**. All client logic in `ts/`. `site.ts` is the entry; add per-page init function and call it from there.

## KQL editor

Already implemented (see `_KqlCompletions.cshtml`, `ts/logs.ts`). Don't redesign. Reuse as-is when adding new query surfaces.

## data-testid

- Every interactive element (buttons, links that change state, form inputs).
- Every container whose presence/absence matters to a test (`config-empty`, `logs-flash-error`).
- Convention: `{page-or-section}-{role}` — kebab-case. Examples: `nav-project`, `config-filter-key`, `logs-flash-error`, `proj-tab-logs`.
- E2E tests must not use text, classes, or roles for selection.

## Confirmation for destructive actions

- Single-field delete: `onclick="return confirm('Delete X?');"`. Explicit.
- Project/workspace delete: typed-name confirmation in a modal (not built yet — when needed, follow this rule).

## Open / undecided

These will be filled when a real use-case hits. Don't over-specify upfront.

- Loading spinner placement during htmx swaps (currently no spinner).
- Toast notifications outside the existing hotkey-toast (see `ts/site.ts` for the pattern if you need a second kind).
- Pagination/virtual-scroll for project lists >100 (not needed at current scale).

# Research: own code-graph tool on Roslyn (2026-08-20)

This is a **dated research snapshot**, moved into the repo from the working folder it was
produced in (`C:\Users\stdray\tools\codegraph-research\` on the machine that ran it, outside
version control). It backs the wiki article and card `work/roslyn-code-graph-research` on
whether PetBox should build its own Roslyn-based code-graph tool instead of relying on NDepend.

**Read `00-INDEX.md` first** — it is the table of contents for everything else here and states
the reading order and authority rules for the analysis files (`01-legend.md` … `50-verdict.md`).

## Why this lives under `research/`, not `doc/`

`doc/` is maintained documentation, fixed in the same change that breaks it. This folder is the
opposite: a snapshot of measurements and conclusions frozen at a point in time. **Nothing in here
is meant to be kept up to date.** Every number, timing, and tool-version claim in these files is
accurate as of **2026-08-20**, against `main` as it stood on that day — not against the current
HEAD. If the codebase or NDepend/Roslyn/.NET versions have moved on since, that is expected and is
not a bug to fix here; re-running the research is a new dated snapshot, not an edit to this one.

## What's here

- `00-INDEX.md` … `50-verdict.md` — the analysis, in reading order.
- `proto/` — prototypes: `CodeGraphProbe` (Roslyn `MSBuildWorkspace`), `ILProbe`
  (`System.Reflection.Metadata`), `di/no-build-harness` and `di/full-host-harness` (DI
  introspection), plus three small session-scratchpad probes (`kqlprobe`, `workerE-kusto`,
  `workerE-objredirect`) that were referenced from the docs but lived outside this folder — moved
  in here since they were small and the scratchpad they came from is ephemeral.
- `queries/` — `.cqlinq` queries used against the NDepend baseline.
- `measurements/` — raw timings, logs, and small JSON provenance dumps.

## What was left out of the move

Build artifacts (`bin/`, `obj/`) and everything over ~2MB were not carried over: DuckDB/SQLite
files and a couple of Parquet/JSON dumps under the original `measurements/workerE/` (edges.json,
edges.parquet, calls_synthetic.parquet, codegraph.db, mat.duckdb, mat2.duckdb, reach.duckdb,
reach3.duckdb) plus `measurements/workerE/redirect-target/` (a build-output-only harness dir).
They are not preserved anywhere; see `15-blocking-measurements.md` for how each was produced.

## Build gate

`proto/**` has its own `Directory.Build.props` / `Directory.Packages.props` that stop inheriting
the repo root's (`TreatWarningsAsErrors`, `AnalysisMode=All`, central package management, …) — this
is throwaway research code, not held to product gates. Nothing here is referenced by
`PetBox.slnx`, so it is invisible to `dotnet build PetBox.slnx`, the pre-commit/pre-push hooks, and
CI, the same way `tools/**` (see `.gitignore`) is.

## Choosing sources — [own]

- **Choose before digging.** Start with the least costly source that can settle
  the question. Check available documentation, declarations and version-matched
  source before reverse-engineering a build. Local is not automatically cheaper:
  fetching the source can beat inspecting an installed artifact.
- **Examples:**
  - Architecture: read the author's README or ADR before reconstructing the design
    from implementation.
  - Dependencies: inspect `package.json` and the lockfile before tracing imports.
  - Implementation: read source at the installed version's tag before decoding
    minified JavaScript.
- **Match evidence to the claim.** Documentation guides the search; it does not
  prove current behavior. Check relevant code, effective configuration or runtime
  evidence when needed, including evidence that could disprove the claim.
  Source alone does not prove what is installed or running.
- **Name the starting source in the brief.** If it is unknown, ask the scout to
  locate it first; do not prescribe an expensive artifact by default.
- **Reassess before grinding.** Before bulk reading or deminifying, check for a
  cheaper source. If the question cannot be settled within budget, report the gap
  and the next check; do not silently widen the investigation.

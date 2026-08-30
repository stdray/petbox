# layer-cascade fixture

Byte-copy of `research/wire-source-of-truth/prototype/{base,user,project}` (2026-08-30).

Its expected resolve was written **by hand, before any code ran**, in
`research/wire-source-of-truth/prototype/RESOLVED.md`. `layer-cascade.test.ts` asserts exactly
what that document says: the three-role roster, per-field provenance for each role, prose
replacement resetting addenda, and the single deliberate `E1` (`user/` removed `reserve` but
left `orchestrator.escalation.targets: ["reserve"]` behind).

The research copy stays where it is on purpose — `resolve.mjs` next to it reads `./base` etc.
relative to its own directory, and RESOLVED.md documents running it that way. This copy is the
one the package's tests read; the two must stay byte-identical, and `layer-cascade.test.ts`
asserts that too.

Lives OUTSIDE `src/` so it is not swept into `tsconfig.json`'s `include` or into the published
tarball (`package.json`'s `files` allowlist is `bin`, `src`, `README.md`).

# Self-test walkthrough

This fixture is `fixture-card.md` — a tiny 4-bullet card — plus `work/`, an intentionally
incomplete "result" for it: `calc.js` only implements `add`, not `sub`; `calc.test.js`
only tests `add`; `work/README.md` was never created at all.

## Step 2 (mechanical, no model) — catches the missing file

```
$ bash scripts/mechanical_check.sh --artifacts self-test/expected-artifacts.txt --base -
== Artifacts ==
OK      .claude/skills/comprehension-check/self-test/work/calc.js
OK      .claude/skills/comprehension-check/self-test/work/calc.test.js
MISSING .claude/skills/comprehension-check/self-test/work/README.md

== Diff overlap == (skipped: --base -)

RESULT: mechanical check found a gap — see MISSING/NOT TOUCHED lines above
EXIT=1
```

No model call happened here — a shell script named the exact missing artifact. This is
what Step 2 is for, and it is the cheapest, most reliable part of the whole procedure.

## Step 3 (model pass) — catches the gap inside a file that does exist

The mechanical check cannot see that `sub` is missing — `calc.js` exists, so check 1
passes for it. That gap only shows up when something reads the card's bullets against the
file content. Walking `fixture-card.md`'s 4 bullets against `work/` by hand:

```
1. Add add(a, b) to work/calc.js.                                -> COVERED
2. Add sub(a, b) to work/calc.js.                                 -> NOT COVERED (calc.js
   only defines and exports add; no sub anywhere in the file)
3. Add a test for both functions in work/calc.test.js.            -> NOT COVERED (only
   add is asserted; there is no sub to test either)
4. Add usage examples for both functions to work/README.md.       -> NOT COVERED
   (work/README.md does not exist)
```

Headline: **NOT COVERED — bullet 2, "Add `sub(a, b)` to `work/calc.js`"** (plus 3 and 4,
which are direct consequences of 2 and of the missing file already caught in Step 2).
Bullet 2 is the one Step 2 alone cannot see, which is the point of running both steps.

## Note on the diff-overlap check

`--base <ref>` (e.g. `--base origin/main`) reruns the same artifact check plus a real
`git diff --stat`/`--numstat` overlap. Verified against this fixture's own commit: the
touch check matches on `--numstat` (full, untruncated paths), not `--stat` (which
abbreviates long paths with a `.../` prefix for display and would otherwise silently mark
a genuinely touched file as NOT TOUCHED).

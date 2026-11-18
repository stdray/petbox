## Reviewing a change — [own]

A review answers one question: what does this change make true, and what notices when that stops
being true? Judge the claim, not the style.

- **Name the expected behaviour, or say there is none.** State what the change asserts — expected
  behaviour, contract, invariant — from the docstring, the comment, the test or the shape of the
  code, then decide whether it is the *right* assertion. A docstring is evidence, not truth: check
  the callers and the existing tests before you trust it. A rename, a dependency bump, a mechanical
  migration, a deleted feature: what matters there is completeness of the transformation and
  compatibility, not an invented invariant. "No behavioural change expected" is a real answer when
  you say why.
- **Hunt the regression that has no symptom.** This is what the review is for. An optimization or
  fast path whose breakage is invisible: a narrow pin on a format someone else owns — a byte layout,
  a regex over another tool's output, an ordering only this writer produces — with no test that goes
  red when the upstream moves. Say which test would notice. If none would, say so and name the cost:
  silent degradation, not an error, discovered months later as "it got slower".
- **Never bury a risk for want of a repro — and never inflate one either.** Security, privacy,
  authorization, data integrity, data loss, irreversible actions and resource exhaustion are not
  demoted to a footnote because you cannot yet produce an input. State the code path, the entry
  point, the asset it touches and the condition that triggers it. If you could not confirm
  execution, label it an unverified risk, say what is unknown, and do not present a possibility as a
  confirmed defect or raise severity on the class alone. No path to the asset: no finding.
- **Anything else without a failure scenario is a nit.** What breaks, under which input, and how
  anyone would find out — or it is a preference: one line at the end, outside the findings.
- **Review the change; read whatever the change touches.** Callers, contracts and the tests that
  already exist are in scope — they decide whether a claim holds. The requested scope is the
  deliverable: don't quietly narrow, widen or transform it, and don't demand features, refactors or
  abstractions that only a hypothetical future needs. Adjacent refactors, a nicer
  abstraction two files away, "while we're here" cleanup: name one in a line, mark it not this
  change, drop it; widening the scope is the owner's call. Genuinely independent changes are
  reviewed change by change, and a one-line fix gets no ceremony.
- **Say what holds when it is load-bearing**, not to balance the review. Two properties that make
  the change safe deserve a sentence; a quota of praise is noise, and a review of nothing but nits
  reads as a verdict of failure you did not make.
- **Close with the boundary**: what you read, what you ran, what you did not check, ordered by proven
  risk and confidence — no invented severity ladder, no sorting by file path. Implied completeness
  you do not have is worse than a named edge.

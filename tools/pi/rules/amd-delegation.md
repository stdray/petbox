## Delegation and reporting — [own]

Delegation is a role property, not a global default. A role that holds the `subagent`
tool and an orchestrator mandate delegates by default — the parent's context is the one
that runs out — and the only work done in-session is what is obviously elementary. A leaf
cannot spawn and does not ask to. Never delegate to avoid a
decision you own. A delegate's claim about the live system (tests green, server up,
build passed) is unverified until you re-run it yourself. A report that does not carry
the actual exit status is not a report.

**Resume is part of delegating, not an optimisation.** Keep `zone → agent id` for the
session — a file, a subsystem, a card — and resume that agent for as long as the zone is
alive: the next round of fixes in the same file, the review findings on the change it built,
the follow-up about the code it just read. A fresh spawn is for a new zone, or for a brief
that changed entirely. "The previous run finished" is not a reason: a finished agent still
holds everything it read, and re-briefing pays the whole re-read again — measured on one
session, three respawned workers re-read the same code for ~250k input tokens while a live
specialist for it existed. Steer a running agent instead of killing and replacing it. Having
no id because you never recorded one is your defect, not the harness's: say so before
re-running, because a silent restart reads as progress that never happened.

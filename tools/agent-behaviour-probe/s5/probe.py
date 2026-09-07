#!/usr/bin/env python3
"""S5 transfer-test harness -- a THIN SHIM over ../probe.py, deliberately not a fork.

Why a shim and not a copy. s4/ took the copy route and its 939 lines are already behind the
parent's 1009: the parent has since grown the opencode salience-index delivery (M3), which this
test MUST have, because production opencode now receives that index and the transfer test is
supposed to measure production conditions. A second fork would have to be re-synced by hand
every time the parent moves, and a probe whose scoring code silently diverges from the probe it
is being compared against is not evidence. So: one code path, two scoring contracts.

What is rebound, and what deliberately is not:

  * `probe.HERE` -> this directory. HERE is read at CALL time in exactly three places --
    scenarios.json, emit-skills-index.mjs and the `--out` default -- so S5 gets its own scoring
    contract (its own spec_hash) and its own artifacts while running the parent's code.
    emit-skills-index.mjs is therefore copied here byte-for-byte; it takes the skill-files.ts
    path as an ARGUMENT, so the copy resolves the same module the parent does.

  * `probe.REPO` / `TEMPLATES` / `SKILL_FILES_TS` -> UNTOUCHED. They were bound at import from
    the parent's location, so S5 serves THE SAME git templates as M2/M3 did. That is the whole
    point: if S5 served different text, a difference in the numbers would not be a transfer
    failure, it would be an edit.

  * `probe.ensure_fixture` -> wrapped, to add the one fixture kind S5 needs (`board_card`: a
    simple board plus a per-run target node to hang the comment on). The parent's three kinds
    fall through unchanged. The fixture lives HERE rather than in the parent for the same reason
    the scenario does -- the parent file stays byte-identical to what M2/M3 ran.

Usage is the parent's, with this file as the entry point:

    python tools/agent-behaviour-probe/s5/probe.py check --key-env PETBOX_PROBE_API_KEY
    python tools/agent-behaviour-probe/s5/probe.py run --key-env PETBOX_PROBE_API_KEY \
        --project probe-m1-sandbox --workspace stdray --harnesses claude --repeats 10 \
        --out tools/agent-behaviour-probe/s5/results/<label>
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

S5 = Path(__file__).resolve().parent

# Load the parent probe BY ABSOLUTE PATH. A plain `import probe` would be ambiguous here (this
# file is also named probe.py and its own directory is on sys.path[0] as the script dir), and an
# ambiguity about which scoring code ran is exactly the thing this probe exists not to have.
_spec = importlib.util.spec_from_file_location("probe_parent", S5.parent / "probe.py")
probe = importlib.util.module_from_spec(_spec)
sys.modules["probe_parent"] = probe
_spec.loader.exec_module(probe)

probe.HERE = S5

_parent_ensure_fixture = probe.ensure_fixture


def ensure_fixture(mcp, sc: dict, project: str, run_id: str) -> None:
    """S5's fixture: a simple board and a per-run node to comment on.

    Per-RUN target node, not a shared one: comments accumulate under their owner node, and a
    shared target would let run N read run N-1's comment -- including, on the runs that used it,
    a visible bodyRef precedent. That would contaminate the very variable under test.
    """
    if sc["fixture"] != "board_card":
        return _parent_ensure_fixture(mcp, sc, project, run_id)
    try:
        mcp.call("tasks_board_create", {"projectKey": project, "board": sc["board"],
                                        "kind": sc.get("board_kind", "simple"),
                                        "methodologyInstance": "$utility"})
    except RuntimeError as e:
        if "exist" not in str(e).lower():
            raise
    card = sc["card"].replace("{RUN}", run_id)
    mcp.call("tasks_upsert", {
        "projectKey": project, "board": sc["board"],
        "nodes": [{"key": card, "title": sc["card_title"], "body": sc["card_body"],
                   "type": "task", "version": 0}]})


probe.ensure_fixture = ensure_fixture


if __name__ == "__main__":
    sys.exit(probe.main())

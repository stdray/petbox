#!/usr/bin/env python3
"""S6 third-path harness -- a THIN SHIM over ../probe.py, exactly as s5/probe.py is.

Why a shim and not a copy: s4/ took the copy route and drifted behind the parent within a day.
A probe whose scoring code silently diverges from the probe it is being compared against is not
evidence. So: one code path, three scoring contracts (S1-S3, S5, S6).

What is rebound, and what deliberately is not:

  * `probe.HERE` -> this directory. HERE is read at CALL time in exactly three places --
    scenarios.json, emit-skills-index.mjs and the `--out` default -- so S6 gets its own scoring
    contract (its own spec_hash) and its own artifacts while running the parent's code.
    emit-skills-index.mjs is therefore copied here byte-for-byte (sha256 verified against the
    parent's at creation); it takes the skill-files.ts path as an ARGUMENT, so the copy resolves
    the same module the parent does.

  * `probe.REPO` / `TEMPLATES` / `SKILL_FILES_TS` -> UNTOUCHED. They were bound at import from
    the parent's location, so S6 serves THE SAME git templates as M2/M3/S5 did (tree
    99ac8a4b808bea100fbd181a67f3a8ece40be8ec, verified clean before the sweep). That is the whole
    point: if S6 served different text, a difference in the numbers would not be a transfer
    failure, it would be an edit.

  * `probe.ensure_fixture` -> wrapped, to add the one fixture kind S6 needs (`memory_store`: a
    per-run memory store for the fact to land in). The parent's three kinds fall through
    unchanged. The fixture lives HERE rather than in the parent for the same reason the scenario
    does -- the parent file stays byte-identical to what M2/M3/S5 ran.

Usage is the parent's, with this file as the entry point:

    python tools/agent-behaviour-probe/s6/probe.py check --key-env PETBOX_PROBE_API_KEY
    python tools/agent-behaviour-probe/s6/probe.py run --key-env PETBOX_PROBE_API_KEY \
        --project probe-m1-sandbox --workspace stdray --harnesses claude --repeats 30 \
        --out tools/agent-behaviour-probe/s6/results/<label>
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

S6 = Path(__file__).resolve().parent

# Load the parent probe BY ABSOLUTE PATH. A plain `import probe` would be ambiguous here (this
# file is also named probe.py and its own directory is on sys.path[0] as the script dir), and an
# ambiguity about which scoring code ran is exactly the thing this probe exists not to have.
_spec = importlib.util.spec_from_file_location("probe_parent", S6.parent / "probe.py")
probe = importlib.util.module_from_spec(_spec)
sys.modules["probe_parent"] = probe
_spec.loader.exec_module(probe)

probe.HERE = S6

_parent_ensure_fixture = probe.ensure_fixture


def ensure_fixture(mcp, sc: dict, project: str, run_id: str) -> None:
    """S6's fixture: a PER-RUN memory store for the fact to be written into.

    Per-RUN, not shared, for S5's reason moved one path over: memory entries accumulate in their
    store and memory_search is among the first things an agent reaches for, so a shared store
    would let run N read run N-1's fact -- including, on the runs that used it, a visible textRef
    precedent. That would contaminate the very variable under test.

    The store name is spelled independently in two places (the `store` field, which this function
    creates, and the prompt, which the parent renders). Asserted rather than trusted: a silent
    mismatch would point every agent at a store that does not exist, and the run would measure
    "could not find the store" while looking like a behaviour measurement.
    """
    if sc["fixture"] != "memory_store":
        return _parent_ensure_fixture(mcp, sc, project, run_id)
    if sc["store"] not in sc["prompt"]:
        raise RuntimeError(f"fixture store {sc['store']!r} is not the store named in the prompt")
    store = sc["store"].replace("{RUN}", run_id)
    try:
        mcp.call("memory_store_create", {"projectKey": project, "store": store,
                                         "scope": "project",
                                         "description": sc.get("store_description", "")})
    except RuntimeError as e:
        if "exist" not in str(e).lower():
            raise


probe.ensure_fixture = ensure_fixture


if __name__ == "__main__":
    sys.exit(probe.main())

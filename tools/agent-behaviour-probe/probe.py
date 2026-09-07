#!/usr/bin/env python3
"""Behavioural probe for agent-facing text (card probe-baseline-agent-behaviour-scenarios).

WHAT THIS MEASURES, AND WHY IT IS NOT A TEST
--------------------------------------------
`protocol.test.ts` pins the TEXT of the agent-facing rules. This pins the BEHAVIOUR: it
runs a real headless agent against a real PetBox instance and records, per run,

  * which text the agent actually READ before it acted (skill invocations, guide/workflow
    reads) -- ordered, and cut at the first decisive tool call, and
  * which AXIS it named out loud in its own prose (post hoc keyword match), and
  * what it DID (the decisive tool call and its argument shape).

"Read what, named which axis" is the whole point: it separates "the text is wrong" from
"the text is right but was not read". A pass/fail verdict would throw that away.

NEUTRAL PROMPTS. The prompts in scenarios.json state a task and name no axis. Asking the
agent "did you consider the token budget?" would put the axis in front of it, which is
the exact variable under test.

CONTAINMENT (read this before changing anything)
------------------------------------------------
1. The probe workspace is created OUTSIDE every path registered in ~/.petbox/projects.json.
   The kit's global Stop/SessionStart hooks resolve their project by longest-prefix match on
   cwd (src/clients-ts/petbox-wire/src/registry.ts) and no-op when nothing matches, so probe
   sessions are never mirrored into a real project.
2. Every CREDENTIAL is stripped from the child environment -- by name pattern and by value shape,
   NOT by a `PETBOX_*` prefix (see child_env for why that was wrong and what it leaked) -- and
   only the sandbox key is injected. `check` asserts this empirically instead of trusting it.
2b. Transcripts are redacted before they are written, for a credential an agent read from a file
   rather than from the environment. These artifacts are committed to a public repository.
3. Claude Code is invoked with --strict-mcp-config so it uses ONLY the probe's .mcp.json and
   does not inherit the user's real petbox MCP server. opencode takes MCP only from the
   project directory (the global ~/.config/opencode/opencode.json declares no mcp section).

Raw stdout of every run is always written to disk before any parsing, so a parser change
never costs a re-run.

USAGE
  python tools/agent-behaviour-probe/probe.py --check-containment --key-env PETBOX_PROBE_API_KEY
  python tools/agent-behaviour-probe/probe.py run --project <sandbox> --workspace <ws> \
      --key-env PETBOX_PROBE_API_KEY --repeats 5 --out <dir>
  python tools/agent-behaviour-probe/probe.py score --out <dir>      # re-score, no re-run
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
TEMPLATES = REPO / "src" / "clients-ts" / "petbox-wire" / "src" / "templates"
SERVER = "https://petbox.3po.su"

HARNESSES = {
    # model is pinned so a rerun is comparable; both legs run a plain general-purpose agent
    # (no petbox role notes) so the measured variable stays "skills + server guide text".
    "claude": {"model": "sonnet"},
    "opencode": {"model": "deepseek/deepseek-v4-pro", "agent": "build"},
}


# --------------------------------------------------------------------------- spec freeze


def load_spec() -> dict:
    return json.loads((HERE / "scenarios.json").read_text(encoding="utf-8"))


def spec_hash(spec: dict) -> str:
    """Fingerprint of the SCORING CONTRACT: the axis word lists, the signal definitions and the
    decisive-tool sets.

    Why this exists. "Which axis did the agent name" is decided by a hand-written word list, so
    the entire product of M0 rests on that list staying put. If the dictionary may drift between
    M0 and M1, then "the behaviour improved" is indistinguishable from "the word list was
    adjusted", and the probe stops being evidence. The dictionary is therefore FROZEN by
    fingerprint: every run record and every summary carries this hash, and `score` refuses to
    report over records whose hash differs from the current scenarios.json.

    Prompts are deliberately EXCLUDED: a typo fix in a prompt changes what was asked (and is
    visible in the stored prompt of every run record) but does not silently re-interpret an
    already-recorded transcript. Only the scoring contract can do that, so only it is pinned."""
    contract = [{"id": s["id"], "axes": s["axes"], "signals": s["signals"],
                 "decisive_tools": s["decisive_tools"]} for s in spec["scenarios"]]
    blob_ = json.dumps(contract, sort_keys=True, ensure_ascii=False).encode("utf-8")
    return hashlib.sha256(blob_).hexdigest()[:16]


# --------------------------------------------------------------------------- workspace


def render_skills(dest: Path, project: str, workspace: str) -> list[str]:
    """Materialize the kit's skills exactly as `petbox-wire` would, but WITHOUT running the
    full wire -- the full wire also installs global hooks, writes ~/.petbox/keys.json and
    registers the directory, i.e. mutates the developer's machine. Rendering from the repo
    templates keeps the probe reproducible from a git sha and touches nothing global."""
    out = []
    for d in sorted(p for p in TEMPLATES.iterdir() if (p / "SKILL.md").exists()):
        text = (d / "SKILL.md").read_text(encoding="utf-8")
        text = text.replace("{{PROJECT}}", project).replace("{{WORKSPACE}}", workspace)
        tgt = dest / ".claude" / "skills" / d.name / "SKILL.md"
        tgt.parent.mkdir(parents=True, exist_ok=True)
        tgt.write_text(text, encoding="utf-8")
        out.append(d.name)
    return out


def build_workspace(dest: Path, project: str, workspace: str, key_env: str) -> list[str]:
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True)
    skills = render_skills(dest, project, workspace)
    (dest / ".mcp.json").write_text(json.dumps({
        "mcpServers": {"petbox": {"type": "http", "url": f"{SERVER}/mcp",
                                  "headers": {"X-Api-Key": "${" + key_env + "}"}}}
    }, indent=2), encoding="utf-8")
    oc = dest / ".opencode"
    oc.mkdir()
    (oc / "opencode.json").write_text(json.dumps({
        "$schema": "https://opencode.ai/config.json",
        "mcp": {"petbox": {"type": "remote", "url": f"{SERVER}/mcp", "enabled": True,
                           "headers": {"X-Api-Key": "{env:" + key_env + "}"}}}
    }, indent=2), encoding="utf-8")
    return skills


def sha(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


def collect_provenance(ws: Path, project: str) -> dict:
    """Record WHICH TEXT the agent was actually shown, not just what it answered.

    The kit does not update itself: the global hooks run a stable local mirror under
    ~/.petbox/wire, and `.claude/skills/` is gitignored and materialized by a separate
    `petbox-wire` step that nobody runs automatically. So the skill text a developer's agent
    reads can lag the template in git by days -- on 2026-09-07 exactly one of the eight
    (petbox-write-economy) was 5 days stale and still carried the section that commit 31f345b
    removed. A probe that records only the answer cannot tell "behaviour changed" from "the
    text underneath changed", which is precisely the comparison M0->M1 exists to make.

    THIS PROBE SERVES THE GIT TEMPLATES, not the machine's materialized copy -- that is what
    makes a run reproducible from a sha instead of from one laptop's unversioned state. Both
    digests are recorded side by side so the difference is never a matter of anyone's memory."""
    def _run(c):
        try:
            return subprocess.run(c, capture_output=True, timeout=30, cwd=str(REPO),
                                  shell=False).stdout.decode("utf-8", "replace").strip()
        except Exception:
            return ""

    served, deployed = {}, {}
    for d in sorted(p for p in (ws / ".claude" / "skills").iterdir() if p.is_dir()):
        f = d / "SKILL.md"
        if f.exists():
            served[d.name] = sha(f.read_text(encoding="utf-8"))
    # The materialized copy lives in the PRIMARY checkout, not in a worktree: `.claude/skills/`
    # is gitignored, so a worktree never has one. Resolve the primary checkout from the shared
    # git dir rather than assuming this process runs there.
    cands = [REPO / ".claude" / "skills", Path.home() / ".claude" / "skills"]
    common = _run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"])
    if common:
        cands.insert(0, Path(common).parent / ".claude" / "skills")
    machine_root = next((c for c in cands if c.exists()), None)
    for cand in cands:
        if cand.exists():
            for d in sorted(p for p in cand.iterdir() if p.is_dir()):
                f = d / "SKILL.md"
                if f.exists() and d.name not in deployed:
                    deployed[d.name] = sha(f.read_text(encoding="utf-8"))
    kitv = ""
    kf = Path.home() / ".petbox" / "kit-version.json"
    if kf.exists():
        try:
            kitv = json.dumps(json.loads(kf.read_text(encoding="utf-8")), ensure_ascii=False)
        except Exception:
            kitv = kf.read_text(encoding="utf-8")[:200]
    srv = ""
    try:
        with urllib.request.urlopen(f"{SERVER}/version", timeout=20) as r:
            srv = r.read().decode("utf-8")[:400]
    except Exception as e:
        srv = f"<unreachable: {e}>"
    return {
        "captured": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "project": project,
        "templates_git_sha": _run(["git", "rev-parse", "HEAD"]),
        "templates_git_status_dirty": bool(_run(["git", "status", "--porcelain",
                                                 "src/clients-ts/petbox-wire/src/templates"])),
        "kit_version_file": kitv,
        "server_version": srv,
        "harness_versions": {"claude": _run([exe("claude"), "--version"]),
                             "opencode": _run([exe("opencode"), "--version"])},
        "skills_served_to_agent_sha256": served,
        "skills_materialized_on_machine_sha256": deployed,
        # REAL staleness only. A naive served-vs-machine digest compare is worthless here: the
        # probe renders {{PROJECT}}/{{WORKSPACE}} for the sandbox while the machine's copy is
        # stamped for its own project, so half the skills differ for that reason alone. Drift is
        # therefore measured template-vs-machine, re-rendering the template with the project
        # actually stamped into the machine's copy.
        "skills_stale_on_machine_vs_templates": machine_drift(machine_root),
        # EVERY file placed in front of the agent, so "a clean workspace that knows nothing about
        # PetBox" is a recorded property of the run rather than an assertion in a report. What is
        # NOT here matters as much as what is: no AGENTS.md, no project canon, no $system boards.
        # node_modules is excluded: opencode installs its MCP deps into the workspace at run time,
        # which is thousands of files of harness plumbing that no agent reads as instructions.
        "workspace_inventory": sorted(
            f for f in (str(p.relative_to(ws)).replace("\\", "/")
                        for p in ws.rglob("*") if p.is_file())
            if "node_modules/" not in f),
        "workspace_inventory_excluded_node_modules": sum(
            1 for p in ws.rglob("*") if p.is_file()
            and "node_modules" in str(p.relative_to(ws)).replace("\\", "/")),
        "workspace_is_git_repo": (ws / ".git").exists(),
        # The hooks resolve their project by cwd prefix and no-op when nothing matches, so an
        # unregistered workspace gets no canon injection and no session mirroring.
        "workspace_registered_in_projects_json": any(
            str(ws).lower().replace("\\", "/").startswith(e["prefix"].lower().replace("\\", "/"))
            for e in json.loads((Path.home() / ".petbox" / "projects.json")
                                .read_text(encoding="utf-8"))["entries"])
        if (Path.home() / ".petbox" / "projects.json").exists() else None,
    }


def machine_drift(root: Path | None) -> list[str]:
    """Which materialized skills on this machine no longer match the git template."""
    if not root or not root.exists():
        return []
    live = root / "petbox" / "SKILL.md"
    proj = ws_ = "$system"
    if live.exists():
        m = re.search(r"ui/([^/\s]+)/([^/\s]+)/", live.read_text(encoding="utf-8"))
        if m:
            ws_, proj = m.group(1), m.group(2)
    out = []
    for d in sorted(p for p in TEMPLATES.iterdir() if (p / "SKILL.md").exists()):
        cur = root / d.name / "SKILL.md"
        if not cur.exists():
            out.append(f"{d.name}:MISSING")
            continue
        tpl = ((d / "SKILL.md").read_text(encoding="utf-8")
               .replace("{{PROJECT}}", proj).replace("{{WORKSPACE}}", ws_))
        if tpl.strip() != cur.read_text(encoding="utf-8").strip():
            out.append(d.name)
    return out


# Names whose VALUE is a credential. A probe agent runs with permissions bypassed, so anything
# left here is one `env` away from a third-party model's context window.
SECRET_NAME = re.compile(
    r"(API_?KEY|APIKEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|_PAT$|^PAT_|SESSION_KEY|"
    r"ACCESS_KEY|PRIVATE_KEY|AUTH)", re.IGNORECASE)
# Values that look like a credential regardless of what the variable is called.
SECRET_VALUE = re.compile(r"^(yb_key_[0-9a-f]{16,}|sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,})")


def child_env(key_env: str, key: str) -> dict:
    """Strip EVERY credential from the child environment, then put back only the probe key.

    This was originally a prefix denylist on `PETBOX*`, and that was wrong in a way worth
    recording. This developer's shell also holds live PetBox project keys under names that do
    NOT carry the prefix -- ANIMEMOV_API_KEY, AGENT_RELAY_API_KEY, KPVOTES_API_KEY,
    YOBAPUB_API_KEY -- next to provider keys and a messaging token. A probe agent duly ran
    `env | grep -iE 'petbox|api_key'` while orienting itself, so those values were captured into
    a stored transcript and sent to a third-party model API. GitHub push protection then refused
    the commit, but only for the provider key: it has no rule for `yb_key_*`, so the PetBox keys
    would have been published.

    Hence a denylist by NAME PATTERN plus a check on the VALUE shape: a credential must fail both
    tests to survive. A name-only rule cannot catch a key parked in a variable called `FOO`, and
    a prefix rule catches nothing it was not told about in advance."""
    env = {}
    for k, v in os.environ.items():
        if SECRET_NAME.search(k) or SECRET_VALUE.match(v or ""):
            continue
        env[k] = v
    env[key_env] = key
    return env


# --------------------------------------------------------------------------- fixtures


class Mcp:
    """Minimal streamable-HTTP MCP client. The probe builds its own fixtures with the SANDBOX
    key so `run` is one command end to end -- a probe whose fixtures are set up by hand is not
    reproducible, and reproducibility is the whole requirement of this card."""

    def __init__(self, key, url=f"{SERVER}/mcp"):
        self.key, self.url, self.sid, self.n = key, url, None, 0
        self._post({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                    "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                               "clientInfo": {"name": "agent-behaviour-probe", "version": "1"}}})
        try:
            self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})
        except Exception:
            pass

    def _post(self, payload):
        self.n += 1
        req = urllib.request.Request(
            self.url, data=json.dumps(payload, ensure_ascii=False).encode("utf-8"), method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json, text/event-stream")
        req.add_header("X-Api-Key", self.key)
        if self.sid:
            req.add_header("Mcp-Session-Id", self.sid)
        with urllib.request.urlopen(req, timeout=120) as r:
            if r.headers.get("Mcp-Session-Id"):
                self.sid = r.headers["Mcp-Session-Id"]
            raw = r.read().decode("utf-8")
        if raw.startswith(("event:", "data:")):
            for line in raw.splitlines():
                if line.startswith("data:"):
                    return json.loads(line[5:].strip())
            return None
        return json.loads(raw) if raw.strip() else None

    def call(self, tool, args):
        r = self._post({"jsonrpc": "2.0", "id": self.n + 100, "method": "tools/call",
                        "params": {"name": tool, "arguments": args}})
        if r is None:
            return None
        if "error" in r:
            raise RuntimeError(f"{tool}: {r['error']}")
        res = r.get("result", {})
        txt = "\n".join(c.get("text", "") for c in res.get("content", [])
                        if c.get("type") == "text")
        if res.get("isError"):
            raise RuntimeError(f"{tool} isError: {txt}")
        try:
            return json.loads(txt)
        except Exception:
            return txt


def ensure_fixture(mcp: Mcp, sc: dict, project: str, run_id: str) -> None:
    """Idempotent per-scenario setup. Failures raise -- a run against a missing fixture would
    silently measure 'agent could not find the board' instead of the behaviour under test."""
    kind = sc["fixture"]
    if kind == "board":
        try:
            mcp.call("tasks_board_create", {"projectKey": project, "board": sc["board"],
                                            "kind": sc.get("board_kind", "simple"),
                                            "methodologyInstance": "$utility"})
        except RuntimeError as e:
            if "exist" not in str(e).lower():
                raise
    elif kind == "classic_card":
        try:
            mcp.call("tasks_methodology_create", {"projectKey": project, "key": "s2-classic",
                                                  "source": "builtin", "sourceKey": "classic"})
        except RuntimeError as e:
            if "exist" not in str(e).lower():
                raise
        card = sc["card"].replace("{RUN}", run_id)
        mcp.call("tasks_upsert", {
            "projectKey": project, "board": sc["board"],
            "nodes": [{"key": card, "title": sc["card_title"], "body": sc["card_body"],
                       "type": "task", "version": 0}]})
    elif kind == "instance_per_run":
        inst = sc["instance"].replace("{RUN}", run_id)
        mcp.call("tasks_methodology_create", {"projectKey": project, "key": inst,
                                              "source": "builtin", "sourceKey": "classic"})


# --------------------------------------------------------------------------- run


def exe(name: str) -> str:
    """Resolve a real executable. On Windows the PATH entry for these CLIs is a .cmd shim that
    CreateProcess cannot launch with shell=False; both ship a real .exe next to it. Using the
    .exe keeps shell=False, so a 3000-character Cyrillic prompt is passed as one argv element
    and never goes through cmd.exe quoting."""
    p = shutil.which(name)
    if p and p.lower().endswith(".exe"):
        return p
    for cand in (shutil.which(name + ".exe"),
                 Path(p).parent / "node_modules" / f"{name}-ai" / "bin" / f"{name}.exe" if p else None):
        if cand and Path(cand).exists():
            return str(cand)
    return p or name


def run_claude(ws: Path, prompt: str, env: dict, timeout: int) -> tuple[str, str, int, float]:
    cmd = [exe("claude"), "-p", prompt, "--output-format", "stream-json", "--verbose",
           "--dangerously-skip-permissions", "--model", HARNESSES["claude"]["model"],
           "--mcp-config", str(ws / ".mcp.json"), "--strict-mcp-config", "--max-turns", "40"]
    return _spawn(cmd, ws, env, timeout)


def run_opencode(ws: Path, prompt: str, env: dict, timeout: int) -> tuple[str, str, int, float]:
    cmd = [exe("opencode"), "run", "--format", "json", "--auto",
           "--agent", HARNESSES["opencode"]["agent"],
           "--model", HARNESSES["opencode"]["model"], "--dir", str(ws), prompt]
    return _spawn(cmd, ws, env, timeout)


def _spawn(cmd, ws, env, timeout):
    t0 = time.time()
    try:
        p = subprocess.run(cmd, cwd=str(ws), env=env, capture_output=True,
                           timeout=timeout, shell=False)
        return (p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace"),
                p.returncode, time.time() - t0)
    except subprocess.TimeoutExpired as e:
        return ((e.stdout or b"").decode("utf-8", "replace"),
                f"TIMEOUT after {timeout}s", 124, time.time() - t0)
    except FileNotFoundError as e:
        return ("", f"NOT FOUND: {e}", 127, time.time() - t0)


# --------------------------------------------------------------------------- parsing


REDACTIONS = [
    re.compile(r"yb_key_[0-9a-f]{16,}"),
    re.compile(r"sk-[A-Za-z0-9_-]{16,}"),
    re.compile(r"gh[pousr]_[A-Za-z0-9]{20,}"),
    # KEY=VALUE as printed by `env`, for names that read as credentials
    re.compile(r"((?:API_?KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL|AUTH)[A-Z_]*\s*[=:]\s*)"
               r"([^\s\"'\\,}]{8,})", re.IGNORECASE),
]


def redact(text: str) -> str:
    """Second line of defence, applied before a transcript ever reaches disk.

    The environment scrub is the first line, but it cannot be the only one: an agent may print a
    credential it read from a FILE (`~/.petbox/keys.json`, an opencode auth store) rather than
    from the environment, and transcripts are committed to a public repository. Redaction is
    deliberately blunt -- over-redacting a transcript costs a little evidence, publishing a live
    key costs a rotation."""
    for rx in REDACTIONS:
        text = rx.sub(lambda m: (m.group(1) + "<REDACTED>") if m.re.groups >= 2
                      else "<REDACTED>", text)
    return text


def parse_events(raw: str) -> list:
    """Lenient: accepts NDJSON, a JSON array, or a single object. Unparseable lines are
    skipped rather than failing -- the raw stdout is on disk either way."""
    raw = raw.strip()
    if not raw:
        return []
    try:
        v = json.loads(raw)
        return v if isinstance(v, list) else [v]
    except Exception:
        pass
    out = []
    for line in raw.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            out.append(json.loads(line))
        except Exception:
            continue
    return out


TOOL_ARG_KEYS = ("input", "arguments", "args", "parameters")


def walk(events):
    """Yield ('tool', name, args) and ('text', str) in document order, from either harness's
    event shape. Structural rather than schema-bound: both harnesses nest tool calls as an
    object carrying a name plus an argument bag."""
    seen = []

    def rec(o):
        if isinstance(o, dict):
            name = o.get("name") or o.get("tool") or o.get("toolName")
            argbag = next((o[k] for k in TOOL_ARG_KEYS if isinstance(o.get(k), dict)), None)
            if argbag is None and isinstance(o.get("state"), dict):
                # opencode: {"type":"tool","tool":"petbox_x","state":{"input":{...}}}
                st = o["state"]
                argbag = next((st[k] for k in TOOL_ARG_KEYS if isinstance(st.get(k), dict)), None)
            if isinstance(name, str) and argbag is not None:
                seen.append(("tool", name, argbag))
            if o.get("type") == "text" and isinstance(o.get("text"), str):
                seen.append(("text", o["text"], None))
            elif isinstance(o.get("text"), str) and "name" not in o:
                seen.append(("text", o["text"], None))
            for v in o.values():
                rec(v)
        elif isinstance(o, list):
            for v in o:
                rec(v)

    rec(events)
    return seen


def assistant_texts(events: list, harness: str) -> list[str]:
    """The agent's OWN prose only -- never tool output.

    This distinction is load-bearing and was got wrong once: scanning all text found the axis
    word `utf` inside a skill's bundled validator source (`readFileSync(file, "utf8")`) and the
    `\\u` marker inside the Windows path `C:\\Users\\...`, both of which arrived as TOOL RESULTS.
    That scored 5/5 "named the reliability axis" for a cell whose agents had in fact said
    nothing of the sort. "Which axis did the agent name out loud" must read only what the agent
    said out loud, so tool_result content is excluded structurally rather than filtered by
    keyword."""
    out = []
    for ev in events:
        if not isinstance(ev, dict):
            continue
        t = ev.get("type")
        if harness == "claude":
            if t == "assistant":
                for c in (ev.get("message", {}) or {}).get("content", []) or []:
                    if isinstance(c, dict) and c.get("type") == "text" and c.get("text"):
                        out.append(c["text"])
            elif t == "result" and isinstance(ev.get("result"), str):
                out.append(ev["result"])
        else:  # opencode: {"type":"text","part":{"type":"text","text":...}}
            if t == "text":
                p = ev.get("part") or {}
                if isinstance(p, dict) and isinstance(p.get("text"), str):
                    out.append(p["text"])
    return out


def marker_hit(m: str, text: str):
    """Markers are substrings by default; a `re:` prefix makes one a regex. The regex form
    exists because the most specific reliability marker is the escape NOTATION itself, and a
    bare substring `\\u` also matches every `C:\\Users` path an agent happens to print."""
    if m.startswith("re:"):
        mo = re.search(m[3:], text, re.IGNORECASE)
        return mo.span() if mo else None
    i = text.find(m.lower())
    return (i, i + len(m)) if i >= 0 else None


def short(name: str) -> str:
    """Normalize both harnesses' MCP tool naming to the bare verb, so a signal defined once in
    scenarios.json matches on either leg: Claude Code emits `mcp__petbox__tasks_upsert`,
    opencode emits `petbox_tasks_upsert`."""
    n = name.split("__")[-1]
    return n[len("petbox_"):] if n.startswith("petbox_") else n


def blob(x) -> str:
    try:
        return json.dumps(x, ensure_ascii=False).lower()
    except Exception:
        return str(x).lower()


def analyse(raw: str, scenario: dict, harness: str) -> dict:
    events = parse_events(raw)
    steps = walk(events)

    tools = [(short(a), a, b) for kind, a, b in steps if kind == "tool"]
    texts = assistant_texts(events, harness)

    decisive = set(scenario["decisive_tools"])
    cut = len(tools)
    for i, (sn, _, _) in enumerate(tools):
        if sn in decisive:
            cut = i
            break

    def skill_reads(upto):
        """A skill counts as READ when the harness's skill tool was invoked for it, or when
        its SKILL.md was opened directly."""
        got = []
        for sn, full, args in tools[:upto]:
            ab = blob(args)
            if sn.lower() in ("skill", "skills") or "skill" in sn.lower():
                for tok in re.findall(r"petbox[a-z-]*", ab):
                    got.append(tok)
            if "skill.md" in ab:
                for tok in re.findall(r"petbox[a-z-]*", ab):
                    got.append(tok)
        return got

    reads_before = [sn for sn, _, _ in tools[:cut]]
    skills_before = sorted(set(skill_reads(cut)))
    all_text = "\n".join(texts).lower()
    all_args = " ".join(blob(a) for _, _, a in tools)

    sig = {}
    for key, spec in scenario["signals"].items():
        k = spec["kind"]
        if k == "tool_arg_key":
            # structural only: a literal "body" inside prose must not count as passing a body
            sig[key] = any(sn in spec["tools"] and _has_key(args, spec["key"])
                           for sn, _, args in tools)
        elif k == "any_arg_contains":
            sig[key] = spec["needle"].lower() in all_args
        elif k == "tool_called":
            sig[key] = any(sn in spec["tools"] for sn, _, _ in tools)
        elif k == "read_skill":
            sig[key] = spec["skill"] in skill_reads(len(tools))
        elif k == "status_set":
            sig[key] = spec["status"].lower() in _statuses(tools)
        elif k == "status_set_other_than":
            sig[key] = any(s != spec["initial"].lower() for s in _statuses(tools))
        else:
            sig[key] = None

    # Axis detection is a keyword match, so it MUST ship its evidence: a bare hit on a short
    # marker like "utf" or "gate" can be a substring accident, and this number is the headline
    # of the whole probe. Every match carries the surrounding text so a reader can reject it.
    axes, axes_ev = {}, {}
    for ax, markers in scenario["axes"].items():
        hits, ev = [], []
        for m in markers:
            span = marker_hit(m, all_text)
            if span:
                hits.append(m)
                a, b = span
                ev.append(f"...{all_text[max(0, a - 70):b + 70]}...".replace("\n", " "))
        if hits:
            axes[ax] = sorted(hits)
            axes_ev[ax] = ev

    return {
        "reads_before_decision": reads_before,
        "skills_read_before_decision": skills_before,
        "all_tools": [sn for sn, _, _ in tools],
        "decisive_call_args": next(({k: _arg_shape(v) for k, v in args.items()}
                                    for sn, _, args in tools if sn in decisive), None),
        "signals": sig,
        "axes_named": axes,
        "axis_evidence": axes_ev,
        "final_text": (texts[-1][:1200] if texts else ""),
        "n_tool_calls": len(tools),
    }


def _has_key(o, key) -> bool:
    if isinstance(o, dict):
        return key in o or any(_has_key(v, key) for v in o.values())
    if isinstance(o, list):
        return any(_has_key(v, key) for v in o)
    return False


def _arg_shape(v):
    if isinstance(v, str):
        return f"<str {len(v)} chars>"
    if isinstance(v, list):
        return [_arg_shape(x) for x in v[:3]]
    if isinstance(v, dict):
        return {k: _arg_shape(x) for k, x in v.items()}
    return v


def _statuses(tools) -> list[str]:
    out = []
    for sn, _, args in tools:
        for m in re.finditer(r'"status"\s*:\s*"([^"]+)"', blob(args)):
            out.append(m.group(1).lower())
    return out


# --------------------------------------------------------------------------- commands


def cmd_check_containment(args):
    key = os.environ.get(args.key_env, "")
    if not key:
        print(f"FAIL: {args.key_env} is not set", file=sys.stderr)
        return 2
    env = child_env(args.key_env, key)
    # Assert on the credential shape, not on a name prefix: the prefix rule is exactly what let
    # ANIMEMOV_API_KEY / AGENT_RELAY_API_KEY through to a third-party model once already.
    leaked = sorted(k for k, v in env.items()
                    if k != args.key_env and (SECRET_NAME.search(k) or SECRET_VALUE.match(v or "")))
    parent = sorted(k for k, v in os.environ.items()
                    if SECRET_NAME.search(k) or SECRET_VALUE.match(v or ""))
    print(f"parent credential-shaped vars : {len(parent)}")
    print(f"child  credential-shaped vars : {[k for k in env if SECRET_NAME.search(k)]}")
    if leaked:
        print(f"FAIL: credentials would reach the probe child: {leaked}", file=sys.stderr)
        return 1
    probe = redact("token=" + key)
    if key in probe:
        print("FAIL: redact() does not mask the probe key shape", file=sys.stderr)
        return 1
    reg = Path.home() / ".petbox" / "projects.json"
    prefixes = []
    if reg.exists():
        prefixes = [e["prefix"] for e in json.loads(reg.read_text(encoding="utf-8"))["entries"]]
    ws = Path(args.ws).resolve()
    hits = [p for p in prefixes if str(ws).lower().replace("\\", "/").startswith(
        p.lower().replace("\\", "/"))]
    print(f"probe workspace      : {ws}")
    print(f"registered prefixes  : {len(prefixes)}")
    if hits:
        print(f"FAIL: workspace is inside registered project path(s) {hits} -- the global "
              f"Stop hook would mirror probe sessions into a REAL project", file=sys.stderr)
        return 1
    print("OK: no real key reachable by the child; workspace outside every registered project")
    return 0


def cmd_run(args):
    key = os.environ.get(args.key_env, "")
    if not key:
        print(f"FAIL: {args.key_env} is not set", file=sys.stderr)
        return 2
    spec = json.loads((HERE / "scenarios.json").read_text(encoding="utf-8"))
    out = Path(args.out).resolve()
    (out / "runs").mkdir(parents=True, exist_ok=True)
    ws = Path(args.ws).resolve()
    skills = build_workspace(ws, args.project, args.workspace, args.key_env)
    env = child_env(args.key_env, key)
    prov = collect_provenance(ws, args.project)
    prov["spec_hash"] = spec_hash(spec)
    (out / "provenance.json").write_text(json.dumps(prov, ensure_ascii=False, indent=1),
                                         encoding="utf-8")
    print(f"workspace {ws} ({len(skills)} skills) "
          f"templates@{prov['templates_git_sha'][:8]} spec@{prov['spec_hash']}")
    if prov["skills_stale_on_machine_vs_templates"]:
        print(f"  NOTE: this machine's materialized skills are STALE vs the git templates for "
              f"{prov['skills_stale_on_machine_vs_templates']}. The probe serves the TEMPLATES; "
              f"agents working on this machine read the stale copy until `petbox-wire update`.")

    only = set(args.only.split(",")) if args.only else None
    harnesses = args.harnesses.split(",")

    for sc in spec["scenarios"]:
        if only and sc["id"] not in only:
            continue
        for h in harnesses:
            for r in range(1, args.repeats + 1):
                tag = f"{sc['id']}__{h}__r{r}"
                dst = out / "runs" / f"{tag}.json"
                if dst.exists() and not args.force:
                    print(f"  skip {tag} (exists)")
                    continue
                run_id = f"{h[:2]}{r}{int(time.time()) % 10000}"
                try:
                    ensure_fixture(Mcp(key), sc, args.project, run_id)
                except Exception as e:
                    print(f"  FIXTURE FAIL {tag}: {e}")
                    continue
                prompt = (sc["prompt"].replace("{PROJECT}", args.project)
                          .replace("{RUN}", run_id)
                          .replace("{BOARD}", sc.get("board", "").replace("{RUN}", run_id))
                          .replace("{CARD}", sc.get("card", "").replace("{RUN}", run_id))
                          .replace("{INSTANCE}", sc.get("instance", "").replace("{RUN}", run_id)))
                print(f"  run  {tag} ...", end="", flush=True)
                fn = run_claude if h == "claude" else run_opencode
                raw, err, rc, dur = fn(ws, prompt, env, args.timeout)
                raw, err = redact(raw), redact(err)
                (out / "runs" / f"{tag}.raw.txt").write_text(raw, encoding="utf-8")
                errf = out / "runs" / f"{tag}.err.txt"
                if err.strip():
                    errf.write_text(err, encoding="utf-8")
                elif errf.exists():
                    errf.unlink()  # never leave a previous attempt's stderr next to a fresh run
                rec = {"scenario": sc["id"], "harness": h, "run": r, "run_id": run_id,
                       "model": HARNESSES[h]["model"], "exit_code": rc,
                       "duration_s": round(dur, 1), "spec_hash": spec_hash(spec),
                       "prompt": prompt}
                try:
                    rec.update(analyse(raw, sc, h))
                except Exception as e:
                    rec["analyse_error"] = repr(e)
                dst.write_text(json.dumps(rec, ensure_ascii=False, indent=1), encoding="utf-8")
                print(f" rc={rc} {dur:.0f}s tools={rec.get('n_tool_calls','?')}")
    return cmd_score(args)


def cmd_score(args):
    spec = load_spec()
    want = spec_hash(spec)
    out = Path(args.out).resolve()
    recs = []
    for f in sorted((out / "runs").glob("*.json")):
        if f.name.endswith(".raw.txt"):
            continue
        rec = json.loads(f.read_text(encoding="utf-8"))
        if args.rescore:
            sc = next(s for s in spec["scenarios"] if s["id"] == rec["scenario"])
            raw = (out / "runs" / f.name.replace(".json", ".raw.txt"))
            if raw.exists():
                rec.update(analyse(raw.read_text(encoding="utf-8"), sc, rec["harness"]))
                rec["spec_hash"] = want
                f.write_text(json.dumps(rec, ensure_ascii=False, indent=1), encoding="utf-8")
        recs.append(rec)

    # THE FREEZE. Comparing M1 against M0 is only meaningful if both were scored by the same
    # dictionary; otherwise a changed word list reads as changed behaviour. Refuse rather than
    # quietly report. --rescore is the sanctioned way through: it recomputes EVERY run from the
    # stored raw transcript under the new contract, so the whole set moves together or not at all.
    stale = sorted({r.get("spec_hash", "<unstamped>") for r in recs} - {want})
    if stale:
        print(f"SPEC HASH MISMATCH: runs on disk were scored under {stale}, "
              f"current scenarios.json is {want}.\n"
              f"The axis dictionary / signal contract moved since these runs were scored, so "
              f"these numbers are NOT comparable with a baseline taken under another hash.\n"
              f"Re-score every run from its stored raw transcript with:  "
              f"probe.py score --rescore --out {args.out}", file=sys.stderr)
        return 3

    summary = {"generated": time.strftime("%Y-%m-%dT%H:%M:%S"), "spec_hash": want, "cells": []}
    for sc in spec["scenarios"]:
        for h in HARNESSES:
            cell = [r for r in recs if r["scenario"] == sc["id"] and r["harness"] == h]
            if not cell:
                continue
            n = len(cell)
            ok = [r for r in cell if r["exit_code"] == 0]
            sig_names = list(sc["signals"])
            summary["cells"].append({
                "scenario": sc["id"], "harness": h,
                "model": cell[0]["model"], "runs": n, "runs_exit0": len(ok),
                "signal_rates": {s: f"{sum(1 for r in cell if r.get('signals', {}).get(s))}/{n}"
                                 for s in sig_names},
                "axis_rates": {ax: f"{sum(1 for r in cell if ax in r.get('axes_named', {}))}/{n}"
                               for ax in sc["axes"]},
                "reads_before_decision_union": sorted({t for r in cell
                                                       for t in r.get("reads_before_decision", [])}),
                "skills_read_rate": f"{sum(1 for r in cell if r.get('skills_read_before_decision'))}/{n}",
            })
    (out / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1),
                                      encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=1))
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("command", choices=["run", "score", "check"], nargs="?", default="run")
    ap.add_argument("--check-containment", action="store_true")
    ap.add_argument("--project", default="probe-m0-sandbox")
    ap.add_argument("--workspace", default="stdray")
    ap.add_argument("--key-env", default="PETBOX_PROBE_API_KEY")
    ap.add_argument("--repeats", type=int, default=5)
    ap.add_argument("--timeout", type=int, default=420)
    ap.add_argument("--harnesses", default="claude,opencode")
    ap.add_argument("--only", default="")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--rescore", action="store_true")
    ap.add_argument("--ws", default=str(Path(os.environ.get("TEMP", "/tmp")) / "petbox-probe-ws"))
    ap.add_argument("--out", default=str(HERE / "baseline" / "latest"))
    a = ap.parse_args()
    if a.check_containment or a.command == "check":
        return cmd_check_containment(a)
    return {"run": cmd_run, "score": cmd_score}[a.command](a)


if __name__ == "__main__":
    sys.exit(main())

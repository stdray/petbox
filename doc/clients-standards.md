# Clients & standards posture

How PetBox decides whether a surface gets a **bespoke client library** or **targets an existing
standard**. The governing principle: PetBox does NOT proliferate its own clients — it prefers
improving the backend to emit/accept a widely-adopted standard over shipping clients we'll later
want to delete. Applied with judgment, not dogma (see Config below).

Backed by a deep-research pass (2026-06-07, 25 adversarially-verified claims, 0 refuted); sources
cited inline.

## Posture matrix

| Surface | Standard? | Decision | Artifact |
|---|---|---|---|
| **Config — distribution** | native `/v1/conf` tag-vector resolution is the source of truth | keep native; small MEC client for .NET | keep thin `PetBox.Client.Config` |
| **Config — env injection** | dotenv (`KEY=value`) is a de-facto format the whole ecosystem reads | emit `.env`, consume with stock tooling — **no bespoke runner** | none (`template=dotenv` on the server) |
| **Data — raw SQL over HTTP** | none (Turso/libSQL "Hrana over HTTP" is vendor-locked; PostgREST/OData/GraphQL are query-DSLs, not pass-through) | minimal bespoke | keep-minimal `PetBox.Client` core (auth+transport) |
| **Log ingestion** | CLEF/Seq (+ optionally OTLP logs) | already compliant | none |
| **Traces / metrics** | OTel / OTLP | already compliant | none |
| **LLM** | OpenAI-compatible chat/embeddings API | expose on backend; consumers use stock `openai` clients | none |
| **Agent / tasks / memory** | Model Context Protocol (MCP) | already compliant | none |

Net: the client SDK collapses to **one thin core** (`PetBox.Client` — auth + transport, needed only
for the no-standard Data surface) plus the existing **MEC provider** (`PetBox.Client.Config`). There
is no "uniform multi-language SDK" to build — that framing is retired.

## Why config is differentiated (we did NOT adopt a config standard wholesale)

Two config standards were evaluated as backend compat-endpoints and **both deferred**:

- **Spring Cloud Config Server** (`GET /{application}/{profile}[/{label}]`) has mature off-the-shelf
  clients across .NET (Steeltoe), TS (`cloud-config-client`) and Python (`config-client`). But it is
  a fixed 3-slot positional model (only `{profile}` is multi-valued), which does **not** fit PetBox's
  open N-dimensional tag vector `{env,project,host,role,…}` without a lossy mapping. Adopting it would
  replace an already-tiny MEC client with the obligation to emit a foreign `propertySource` shape, and
  only pays off when interoperating with an **external** Spring application. PetBox has no external
  config consumers today → benefit ≈ 0, server cruft > 0. **Deferred.**
  (Spring docs: <https://docs.spring.io/spring-cloud-config/reference/server.html>; Steeltoe:
  <https://docs.steeltoe.io/api/v3/configuration/config-server-provider.html>.)
- **Consul KV** (`/v1/kv`) is the key that unlocks off-the-shelf sidecar runners (envconsul /
  consul-template), but it is a flat single-dimension store — same tag-vector mismatch — and emulating
  it bends our own model to a foreign frame purely for our own dogfooding. **Deferred** until a real
  external envconsul/consul-template consumer exists.
  (Consul KV: <https://developer.hashicorp.com/consul/api-docs/kv>.)

## Env injection — the recipe (no bespoke runner)

A bespoke "config runner" (the yobaconf `yobaconf-run` model) was **rejected**: shipped as a
self-contained .NET single-file binary it bloats non-.NET (Python/Bun) images by tens of MB, and its
SIGTERM-forwarding + exit-code-propagation machinery is self-inflicted — a consequence of .NET not
being able to `execve`. With a proper `exec`-replacement the child becomes PID 1 directly and the OS
handles signals and exit codes; none of that wrapper code is needed.

Instead, the server emits a **dotenv** body and the standard ecosystem consumes it:

```bash
# GET /v1/conf?<tags>&template=dotenv  →  text/plain, one KEY=value per line (UPPER_SNAKE keys)
curl -fsS -H "X-Api-Key: $PETBOX_API_KEY" \
  "$PETBOX_ENDPOINT/v1/conf?env=prod&project=myapp&template=dotenv"
# DB_HOST=h1
# DB_PORT=5432
# FEATURE_X=true
```

Consumption options (pick per need):

- **Container entrypoint (dynamic, per start), fail-closed:**
  ```sh
  set -a
  curl -fsS -H "X-Api-Key: $PETBOX_API_KEY" "$PETBOX_ENDPOINT/v1/conf?env=prod&project=myapp&template=dotenv" -o /tmp/app.env || exit 1
  . /tmp/app.env
  set +a
  exec "$@"            # the app becomes PID 1 — its own signals & exit code, no wrapper
  ```
- **`docker run --env-file`** (fetch to a file first; `--env-file` reads `KEY=value` literally):
  ```sh
  curl -fsS … "…&template=dotenv" -o app.env && docker run --env-file app.env myimage
  ```
- **compose `env_file:`** — point at a file fetched at deploy time.
- **dotenv libraries** — `python-dotenv`, node `dotenv`, etc. load the same file.

### dotenv format caveat
There is no single dotenv spec; quoting/escaping/multiline differ across parsers, and
`docker --env-file` does NOT strip quotes or interpolate (it reads values literally and has no
multiline support). PetBox therefore emits the **lowest common denominator**: bare, unquoted
`KEY=value`, one per line, keys UPPER_SNAKE (same shaping as `template=envvar`). This round-trips for
ordinary scalar values (the overwhelming majority of config). Values containing newlines, quotes, `#`,
or leading/trailing spaces are **not** safe in this format across all consumers — for those use a JSON
template (`flat` / `dotnet`) and parse in-app. Implemented in `src/PetBox.Config/ConfigTemplates.cs`
(`Dotenv`) + `ConfigApi.cs` (the `/v1/conf` `template=dotenv` branch returns `text/plain`).

## Playbook — "in case of" (when a deferred standard becomes worth it)

- **An external app wants env injection with an off-the-shelf supervisor** → add a **Consul-KV-compat**
  endpoint (`/v1/kv` facade over resolved config + a `key → tag-vector` mapping) so `envconsul`
  (env-var injection into a subprocess) or `consul-template` (file render + exec/reload) work unchanged.
  envconsul: <https://github.com/hashicorp/envconsul>; consul-template:
  <https://github.com/hashicorp/consul-template>. (`confd` is abandonware since 2018 — not a target.)
- **An external Spring application must read PetBox config without writing code** → add a
  **Spring-Cloud-Config-compat** endpoint emitting Spring's JSON `propertySource` shape, mapping the
  tag vector onto `application`+`profile`(+`label`). Accept the expressiveness loss only for that
  interop.
- **Kubernetes-native delivery** → emit config as a `ConfigMap`/`Secret` and let the kubelet inject via
  `env`/`envFrom` (a PetBox controller must populate/refresh the objects from tag resolution).
  <https://kubernetes.io/docs/tasks/inject-data-application/define-environment-variable-container/>.

Until one of those real needs lands, none of this is built — that is the point of having researched it.

## Release conventions (client packages)

Spec: `sdk-release-uniform`. The three client packages publish uniformly:

| | .NET | TypeScript | Python |
|---|---|---|---|
| Package | `PetBox.Client.Config` | `@stdray-npm/petbox-client` | `petbox-client` |
| Registry | **nuget.org** (public) | **npmjs.org** (public) | **PyPI** (public) |
| Version | GitVersion (stamped at publish) | GitVersion (stamped at publish) | GitVersion → PEP 440 (stamped at publish) |
| Publish trigger | push tag `nuget` | push tag `npm` | push tag `pypi` |

- **Registry posture: canonical PUBLIC registry per ecosystem.** No private/preview tier — .NET moved off GitHub Packages to nuget.org to match ts/py (decided 2026-06-08).
- **Version from a single source (GitVersion), stamped at publish** — never hand-edited per language. Repo manifests carry a `0.0.0` placeholder (`package.json`, `pyproject.toml`); `build.cs` overwrites it from GitVersion in `Pack` / `TsSdkPack` (`npm version`) / `PyPiPack` (`uv version` → `MajorMinorPatch[.devN]`). Python `__version__` reads back from installed dist metadata.
- **Secrets** (CI, repo settings): `NUGET_API_KEY` (nuget.org), `NPM_TOKEN` (npmjs), `PYPI_TOKEN` (PyPI). A publish job fails-closed if its secret is absent.

## Related
- Pre-methodology client roadmap in `doc/plan.md` Phase 26 is partly superseded: the "PetBox.Client +
  .Config + ts/py" multi-package vision and the bespoke config-runner are revised per this document.
- Methodology idea `config-runner` ($system/ideas) is pivoted to this dotenv posture.

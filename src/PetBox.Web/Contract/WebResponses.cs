using PetBox.Deploy.Contract;

namespace PetBox.Web.Contract;

// Liveness probe payload: {"status":"healthy"}.
public sealed record HealthStatusResponse(string Status);

// Build-identity payload for /version: the semantic version, short commit SHA and
// commit date (all sourced from env vars, empty/dev fallbacks at runtime).
public sealed record VersionResponse(string SemVer, string ShortSha, string CommitDate);

// ---- POST /api/health (Health/HealthApi.cs) -------------------------------------------

// Wire contract of POST /api/health, constructed by ASP.NET Core JSON model binding on
// HealthApi.PushAsync — moved here from Health/HealthApi.cs (resharper-clt-move-wire-records).
public sealed record HealthPushRequest(
	string Svc,
	string? Name,
	Dictionary<string, string>? Tags,
	string? Version,
	string? Sha,
	string? BuildDate,
	string Status);

// ---- /api/deploy/nodes (Deploy/DeployApi.cs) ------------------------------------------

// Constructed by ASP.NET Core JSON model binding on DeployApi.EnrollNodeAsync — moved here from
// Deploy/DeployApi.cs (resharper-clt-move-wire-records).
public sealed record NodeEnrollRequest(string Id, string? DisplayName, string? Tags, bool Ephemeral, bool MintKey);

// Serialized back to the caller by the minimal-API JSON writer — moved here alongside
// NodeEnrollRequest above (same doctrine).
public sealed record NodeEnrollResponse(NodeView Node, string? Key);

// ---- GET /api/memory/{projectKey}/canon (Memory/MemoryApi.cs) -------------------------

// One scope's canon: the raw index body plus its temporal cursor (updatedAt/version) — moved
// here from Memory/MemoryApi.cs (resharper-clt-move-wire-records). See CanonResponse (still in
// MemoryApi.cs) for the full shape and the Version-0-as-discriminator convention.
public sealed record CanonPart(string Body, DateTime UpdatedAt, long Version);

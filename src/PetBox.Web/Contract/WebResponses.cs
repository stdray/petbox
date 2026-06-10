namespace PetBox.Web.Contract;

// Liveness probe payload: {"status":"healthy"}.
public sealed record HealthStatusResponse(string Status);

// Build-identity payload for /version: the semantic version, short commit SHA and
// commit date (all sourced from env vars, empty/dev fallbacks at runtime).
public sealed record VersionResponse(string SemVer, string ShortSha, string CommitDate);

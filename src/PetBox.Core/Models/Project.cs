using LinqToDB.Mapping;

namespace PetBox.Core.Models;

[Table("Projects")]
public sealed record Project
{
	[PrimaryKey]
	public string Key { get; init; } = string.Empty;
	public string WorkspaceKey { get; init; } = string.Empty;
	public string Name { get; init; } = string.Empty;
	public string Description { get; init; } = string.Empty;
	// A sandbox project is where smoke/background-job traffic is allowed to land for real (spec
	// work/smoke-writes-into-real-projects): enrichment stays ON there — no separate "smoke mode"
	// to keep in parity with prod — but a SandboxOnly key (ApiKey.SandboxOnly) can physically write
	// only into projects with this flag set. Normal projects default to false.
	public bool Sandbox { get; init; }
}

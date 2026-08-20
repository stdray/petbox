namespace PetBox.Core.Models;

public enum WorkspaceRole { Admin = 0, Member = 1, Viewer = 2 }

public sealed record WorkspaceMember
{
	public long Id { get; init; }
	public long UserId { get; init; }
	public string WorkspaceKey { get; init; } = string.Empty;
	public WorkspaceRole Role { get; init; }
}

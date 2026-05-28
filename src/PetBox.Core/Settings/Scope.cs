namespace PetBox.Core.Settings;

// Scope axes for the L2 Settings store. Ordered from coarsest to finest —
// resolver walks from `deepestScope` up toward `Scope.System` until it hits
// either a match or the setting's TopLevel cap.
public enum Scope
{
	System = 0,
	Workspace = 1,
	Project = 2,
	Service = 3,
	User = 4,
	Membership = 5,
}

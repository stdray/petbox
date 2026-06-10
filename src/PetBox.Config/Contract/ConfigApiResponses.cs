namespace PetBox.Config.Contract;

// REST echo of a freshly created binding: its identity, canonical path and tags.
public sealed record ConfigBindingCreatedResponse(long Id, string Path, string Tags);

// 409 when one path resolves to multiple bindings under the request tags.
// Candidates carries the colliding binding ids so the caller can disambiguate.
public sealed record ConfigAmbiguousResponse(string Error, string Path, IReadOnlyList<long> Candidates);

// 404 when the calling key's project has no row in PetBoxDb.Projects.
public sealed record ConfigProjectNotFoundResponse(string Error, string Project);

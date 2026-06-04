using PetBox.Core.Models;

namespace PetBox.Web.Pages.Config;

public sealed record RowViewModel(
	ConfigBinding Binding,
	string WorkspaceKey,
	string? ProjectKey,
	string? KeyQuery,
	IReadOnlyDictionary<string, string> TagFilter);

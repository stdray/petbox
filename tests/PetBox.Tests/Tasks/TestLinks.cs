namespace PetBox.Tests;

// Test helper for the generic links door (spec methodology-link-kinds-declared): builds the
// NodePatch.Links map that replaced the specRef/ideaRef sugar fields.
public static class TestLinks
{
	public static IReadOnlyDictionary<string, IReadOnlyList<string>> Of(string kind, params string[] refs) =>
		new Dictionary<string, IReadOnlyList<string>> { [kind] = refs };

	public static IReadOnlyDictionary<string, IReadOnlyList<string>> TaskSpec(params string[] refs) => Of("task_spec", refs);
	public static IReadOnlyDictionary<string, IReadOnlyList<string>> IdeaSpec(params string[] refs) => Of("idea_spec", refs);
}

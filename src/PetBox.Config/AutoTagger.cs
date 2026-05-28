namespace PetBox.Config;

public static class AutoTagger
{
	public static string ForProject(string projectKey) => $"project:{projectKey}";

	public static string ForService(string projectKey, string serviceKey) =>
		$"project:{projectKey},service:{serviceKey}";

	public static string Merge(string? existingTags, string newTag)
	{
		if (string.IsNullOrWhiteSpace(existingTags))
			return newTag;

		var tags = existingTags
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		tags.Add(newTag);
		return string.Join(',', tags.Order(StringComparer.OrdinalIgnoreCase));
	}
}

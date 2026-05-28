namespace PetBox.Config;

public sealed class AmbiguousConfigException : Exception
{
	public string Path { get; }
	public IReadOnlyList<long> CandidateBindingIds { get; }

	public AmbiguousConfigException(string path, IReadOnlyList<long> candidateIds)
		: base($"Multiple bindings match path '{path}' with equal specificity: ids {string.Join(", ", candidateIds)}")
	{
		Path = path;
		CandidateBindingIds = candidateIds;
	}
}

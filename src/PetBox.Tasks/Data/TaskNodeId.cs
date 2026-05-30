using System.Text.RegularExpressions;

namespace PetBox.Tasks.Data;

// Domain identity for a plan node: a 1-to-3 level path (Phase / Wave / Task).
// The temporal engine is string-keyed and payload-agnostic; this type owns the
// canonicalisation between the structured id agents speak and the flat string
// Key the engine stores. Canonical Key = segments joined by '/':
//   "phase"            depth 1
//   "phase/wave"       depth 2
//   "phase/wave/task"  depth 3
// Each segment matches the shared name spec, so '/' is an unambiguous separator
// and Parse is the exact inverse of ToKey.
public sealed partial record TaskNodeId
{
	// Same spec as boards/logs: starts a-z, then a-z/0-9/_/- up to 100 chars.
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex SegmentRegex();

	public string PhaseKey { get; }
	public string? WaveKey { get; }
	public string? TaskKey { get; }

	public TaskNodeId(string phaseKey, string? waveKey = null, string? taskKey = null)
	{
		// Normalise empty/whitespace to null so callers can pass "" for absent levels.
		phaseKey = Norm(phaseKey) ?? throw new ArgumentException("phaseKey is required", nameof(phaseKey));
		waveKey = Norm(waveKey);
		taskKey = Norm(taskKey);

		// No gaps: a task without a wave is not a valid 3-level path.
		if (taskKey is not null && waveKey is null)
			throw new ArgumentException("taskKey requires waveKey (no level may be skipped)", nameof(taskKey));

		ValidateSegment(phaseKey, nameof(phaseKey));
		if (waveKey is not null) ValidateSegment(waveKey, nameof(waveKey));
		if (taskKey is not null) ValidateSegment(taskKey, nameof(taskKey));

		PhaseKey = phaseKey;
		WaveKey = waveKey;
		TaskKey = taskKey;
	}

	// 1 (phase), 2 (phase/wave) or 3 (phase/wave/task).
	public int Depth => TaskKey is not null ? 3 : WaveKey is not null ? 2 : 1;

	// The canonical flat Key stored by the engine.
	public string ToKey() => TaskKey is not null
		? $"{PhaseKey}/{WaveKey}/{TaskKey}"
		: WaveKey is not null
			? $"{PhaseKey}/{WaveKey}"
			: PhaseKey;

	// The parent node's Key, or null for a phase-level (depth 1) node.
	public string? ParentKey => Depth switch
	{
		3 => $"{PhaseKey}/{WaveKey}",
		2 => PhaseKey,
		_ => null,
	};

	// Parse a canonical Key back into its levels. Throws on 0 or >3 segments or a
	// segment that violates the spec.
	public static TaskNodeId Parse(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
			throw new ArgumentException("key is required", nameof(key));

		var parts = key.Split('/');
		if (parts.Length is < 1 or > 3)
			throw new ArgumentException($"key '{key}' must have 1-3 '/'-separated segments", nameof(key));

		return new TaskNodeId(
			parts[0],
			parts.Length > 1 ? parts[1] : null,
			parts.Length > 2 ? parts[2] : null);
	}

	public static bool TryParse(string key, out TaskNodeId? id)
	{
		try { id = Parse(key); return true; }
		catch (ArgumentException) { id = null; return false; }
	}

	static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

	static void ValidateSegment(string segment, string paramName)
	{
		if (!SegmentRegex().IsMatch(segment))
			throw new ArgumentException(
				$"'{segment}' is not a valid path segment; must match ^[a-z][a-z0-9_-]{{0,99}}$", paramName);
	}
}

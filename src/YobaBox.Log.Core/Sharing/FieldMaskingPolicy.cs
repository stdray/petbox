using System.Collections.Immutable;

namespace YobaBox.Log.Core.Sharing;

public sealed record FieldMaskingPolicy(ImmutableDictionary<string, MaskMode> Modes)
{
	public static readonly FieldMaskingPolicy Empty =
		new(ImmutableDictionary<string, MaskMode>.Empty.WithComparers(StringComparer.Ordinal));

	public MaskMode ModeFor(string path) =>
		Modes.TryGetValue(path, out var m) ? m : MaskMode.Keep;
}

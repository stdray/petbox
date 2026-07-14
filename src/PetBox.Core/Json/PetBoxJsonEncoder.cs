using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PetBox.Core.Json;

/// <summary>
/// Shared text encoder for human-facing JSON. The default <see cref="JavaScriptEncoder"/>
/// escapes every non-ASCII char (Cyrillic -> \uXXXX), which makes serialized log events and
/// MCP tool results unreadable. This allows common ranges while keeping HTML-sensitive chars
/// escaped (so it's safe to emit into HTML attributes), unlike UnsafeRelaxedJsonEscaping.
/// </summary>
public static class PetBoxJsonEncoder
{
	/// <summary>Relaxed encoder: BasicLatin + Latin1 + LatinExtendedA + GeneralPunctuation + Cyrillic.</summary>
	public static readonly JavaScriptEncoder Relaxed = JavaScriptEncoder.Create(
		UnicodeRanges.BasicLatin,
		UnicodeRanges.Latin1Supplement,
		UnicodeRanges.LatinExtendedA,
		UnicodeRanges.GeneralPunctuation, // — … “” ‘’ etc.
		UnicodeRanges.Cyrillic);

	/// <summary>
	/// One shared options instance (Web naming defaults + <see cref="Relaxed"/>) for manual
	/// <c>JsonSerializer.Serialize</c>/<c>Deserialize</c> call sites that have no deliberate reason
	/// to diverge (a different naming policy, a wire contract owned by another party, …). This is
	/// the answer to "can't we just share the JSON serialization settings" — reach for this instead
	/// of a bare <c>JsonSerializer.Serialize(x)</c>, which silently falls back to the default
	/// HTML-safe encoder and re-escapes Cyrillic into \uXXXX. A call site that legitimately needs
	/// its own <see cref="JsonSerializerOptions"/> (a different naming policy, an external wire
	/// shape) should still set <c>Encoder = PetBoxJsonEncoder.Relaxed</c> on ITS OWN options rather
	/// than adopt this instance wholesale.
	/// </summary>
	public static readonly JsonSerializerOptions SharedOptions = new(JsonSerializerDefaults.Web)
	{
		Encoder = Relaxed,
	};
}

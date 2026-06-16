using System.Text.Encodings.Web;
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
}

using System.Security.Cryptography;
using System.Text;
using PetBox.Core.Models;

namespace PetBox.Config;

// Stable content fingerprint over (Path, Tags, Kind, Value-or-cipher). Used to detect
// "no-op" updates (incrementing Version when nothing changed adds noise to History).
public static class BindingContentHash
{
	public static string Compute(string path, string tags, BindingKind kind, string value, string? ciphertext)
	{
		var canonical = new StringBuilder();
		canonical.Append(path).Append('\0');
		canonical.Append(tags).Append('\0');
		canonical.Append((int)kind).Append('\0');
		canonical.Append(kind == BindingKind.Secret ? (ciphertext ?? string.Empty) : value);

		Span<byte> hash = stackalloc byte[32];
		SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()), hash);
		return Convert.ToHexStringLower(hash);
	}
}

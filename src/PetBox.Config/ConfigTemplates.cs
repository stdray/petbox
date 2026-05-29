using System.Text;

namespace PetBox.Config;

// Shapes a resolved path→value map into the four response templates the config clients
// understand (see clients-ts/petbox-client types.ts and clients-net/PetBox.Client.Config):
//
//   flat        — nested JSON object, dotted path split into objects: {"db":{"host":"x"}}
//   dotnet      — flat map, dots → ':' (ASP.NET configuration key convention): {"db:host":"x"}
//   envvar      — flat map, UPPER_SNAKE, non-alphanumeric → '_': {"DB_HOST":"x"}
//   envvar-deep — flat map, dots → '__' (double-underscore .NET env nesting), UPPER: {"DB__HOST":"x"}
//
// The TS client traverses dotted paths for "flat" and does direct key lookups for the others;
// the .NET provider re-flattens whatever JSON it receives via JsonFlattener.
public static class ConfigTemplates
{
	public static object Shape(IReadOnlyDictionary<string, string> values, string? template)
	{
		return (template ?? "flat").ToLowerInvariant() switch
		{
			"dotnet" => Flat(values, DotnetKey),
			"envvar" => Flat(values, EnvVarKey),
			"envvar-deep" => Flat(values, EnvVarDeepKey),
			_ => Nested(values),
		};
	}

	static Dictionary<string, string> Flat(IReadOnlyDictionary<string, string> values, Func<string, string> keyShaper)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (path, value) in values)
			result[keyShaper(path)] = value;
		return result;
	}

	// Build a nested object tree by splitting each path on '.'. Leaf wins over branch on
	// conflict (last write); the resolver already guarantees one value per path.
	static Dictionary<string, object> Nested(IReadOnlyDictionary<string, string> values)
	{
		var root = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach (var (path, value) in values)
		{
			var segments = path.Split('.');
			var node = root;
			for (var i = 0; i < segments.Length - 1; i++)
			{
				var seg = segments[i];
				if (node.TryGetValue(seg, out var existing) && existing is Dictionary<string, object> child)
				{
					node = child;
				}
				else
				{
					var created = new Dictionary<string, object>(StringComparer.Ordinal);
					node[seg] = created;
					node = created;
				}
			}
			node[segments[^1]] = value;
		}
		return root;
	}

	static string DotnetKey(string path) => path.Replace('.', ':');

	static string EnvVarKey(string path)
	{
		var sb = new StringBuilder(path.Length);
		foreach (var ch in path)
			sb.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_');
		return sb.ToString();
	}

	static string EnvVarDeepKey(string path)
	{
		var sb = new StringBuilder(path.Length + 4);
		foreach (var ch in path)
		{
			if (ch == '.')
				sb.Append("__");
			else
				sb.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_');
		}
		return sb.ToString();
	}
}

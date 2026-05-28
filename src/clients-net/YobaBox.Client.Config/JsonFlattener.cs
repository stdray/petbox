using System.Globalization;
using System.Text.Json;

namespace YobaBox.Client.Config;

// Flattens a JSON tree into Microsoft.Extensions.Configuration's flat-key form:
//
//   {"db":{"host":"localhost","port":5432},"features":["a","b"]}
//     =>
//   { "db:host" = "localhost", "db:port" = "5432", "features:0" = "a", "features:1" = "b" }
//
// Matches the algorithm used by JsonConfigurationProvider in the BCL — same `:`
// separator, same array-index pattern, ordinal-ignore-case keys.
static class JsonFlattener
{
	public static Dictionary<string, string?> Flatten(JsonDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
		Walk(document.RootElement, string.Empty, result);
		return result;
	}

	static void Walk(JsonElement element, string prefix, Dictionary<string, string?> result)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var prop in element.EnumerateObject())
				{
					var key = prefix.Length == 0 ? prop.Name : prefix + ":" + prop.Name;
					Walk(prop.Value, key, result);
				}
				break;
			case JsonValueKind.Array:
				var i = 0;
				foreach (var item in element.EnumerateArray())
				{
					var key = prefix + ":" + i.ToString(CultureInfo.InvariantCulture);
					Walk(item, key, result);
					i++;
				}
				break;
			case JsonValueKind.String:
				result[prefix] = element.GetString();
				break;
			case JsonValueKind.Number:
				// Preserve original numeric representation. GetRawText returns the JSON
				// literal verbatim so downstream IConfiguration.GetValue<int>/double does
				// its own parsing without double-rounding through a .NET numeric here.
				result[prefix] = element.GetRawText();
				break;
			case JsonValueKind.True:
				result[prefix] = "true";
				break;
			case JsonValueKind.False:
				result[prefix] = "false";
				break;
			case JsonValueKind.Null:
				result[prefix] = null;
				break;
			case JsonValueKind.Undefined:
			default:
				// Undefined shouldn't appear in a parsed JsonDocument root.
				break;
		}
	}
}

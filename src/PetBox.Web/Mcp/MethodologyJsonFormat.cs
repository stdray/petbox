using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PetBox.Web.Mcp;

// DISPLAY-side JSON layout policy for methodology definition documents (the editor textarea
// prefill and the preset templates): the familiar indented layout, EXCEPT that every element
// of a `statuses` / `transitions` array renders on ONE line — e.g.
//   { "slug": "reported", "name": "Reported", "kind": "open" }
// — so a workflow block reads like the table it is instead of a wall of one-field lines.
// System.Text.Json has no per-node indentation policy, so this is a small hand-rolled
// pretty-printer over JsonNode. Layout only: parsing accepts any valid JSON and the MCP
// def_get/def_upsert wire is untouched. Leaves render with RELAXED escaping (see LeafOptions)
// so human text stays readable ("Won't fix", not "Won't fix").
static class MethodologyJsonFormat
{
	// Property names whose ARRAY ELEMENTS are laid out one-per-line, compact.
	static readonly HashSet<string> InlineElementArrays = new(StringComparer.Ordinal) { "statuses", "transitions" };

	// Leaf-value escaping for DISPLAY: JsonNode.ToJsonString() with no options uses the default
	// HTML-safe encoder, which escapes ' < > & to \uXXXX — that surfaced as `Won't fix` in
	// the editor textarea. This output lands in a Razor <textarea> (HTML-encoded at the output
	// boundary), so relaxed escaping is safe and reads cleanly. Display-only; the wire is unchanged.
	static readonly JsonSerializerOptions LeafOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	const string IndentUnit = "  ";

	public static string ToDisplayJson<T>(T doc, JsonSerializerOptions wireOptions)
	{
		var node = JsonSerializer.SerializeToNode(doc, wireOptions);
		var sb = new StringBuilder();
		Write(node, sb, 0);
		return sb.ToString();
	}

	static void Write(JsonNode? node, StringBuilder sb, int depth)
	{
		switch (node)
		{
			case JsonObject obj:
				WriteObject(obj, sb, depth);
				break;
			case JsonArray arr:
				WriteArray(arr, sb, depth, inlineElements: false);
				break;
			default:
				sb.Append(node?.ToJsonString(LeafOptions) ?? "null");
				break;
		}
	}

	static void WriteObject(JsonObject obj, StringBuilder sb, int depth)
	{
		if (obj.Count == 0) { sb.Append("{}"); return; }
		sb.Append('{');
		var first = true;
		foreach (var (name, value) in obj)
		{
			if (!first) sb.Append(',');
			first = false;
			sb.Append('\n');
			Indent(sb, depth + 1);
			WritePropertyName(name, sb);
			if (value is JsonArray arr && InlineElementArrays.Contains(name))
				WriteArray(arr, sb, depth + 1, inlineElements: true);
			else
				Write(value, sb, depth + 1);
		}
		sb.AppendLine();
		Indent(sb, depth);
		sb.Append('}');
	}

	static void WriteArray(JsonArray arr, StringBuilder sb, int depth, bool inlineElements)
	{
		if (arr.Count == 0) { sb.Append("[]"); return; }
		sb.Append('[');
		var first = true;
		foreach (var element in arr)
		{
			if (!first) sb.Append(',');
			first = false;
			sb.Append('\n');
			Indent(sb, depth + 1);
			if (inlineElements)
				WriteInline(element, sb);
			else
				Write(element, sb, depth + 1);
		}
		sb.AppendLine();
		Indent(sb, depth);
		sb.Append(']');
	}

	// One-line form: `{ "slug": "reported", "name": "Reported", "kind": "open" }` /
	// `["a", "b"]`. Nested values (a transition's checklist array) stay on the same line.
	static void WriteInline(JsonNode? node, StringBuilder sb)
	{
		switch (node)
		{
			case JsonObject obj when obj.Count == 0:
				sb.Append("{}");
				break;
			case JsonObject obj:
				sb.Append("{ ");
				var firstProp = true;
				foreach (var (name, value) in obj)
				{
					if (!firstProp) sb.Append(", ");
					firstProp = false;
					WritePropertyName(name, sb);
					WriteInline(value, sb);
				}
				sb.Append(" }");
				break;
			case JsonArray arr:
				sb.Append('[');
				var firstEl = true;
				foreach (var element in arr)
				{
					if (!firstEl) sb.Append(", ");
					firstEl = false;
					WriteInline(element, sb);
				}
				sb.Append(']');
				break;
			default:
				sb.Append(node?.ToJsonString(LeafOptions) ?? "null");
				break;
		}
	}

	static void WritePropertyName(string name, StringBuilder sb)
	{
		sb.Append(JsonValue.Create(name).ToJsonString(LeafOptions));
		sb.Append(": ");
	}

	static void Indent(StringBuilder sb, int depth)
	{
		for (var i = 0; i < depth; i++) sb.Append(IndentUnit);
	}
}

using ModelContextProtocol.Protocol;

namespace PetBox.Web.Mcp;

// spec tool-description-economy — a tool's [Description] essay stays the SOURCE OF TRUTH in code,
// but tools/list serves only a COMPACT HEAD for heavy tools so the tool surface is cheap to load;
// the full prose is fetched addressably via the `tool_describe` tool.
//
// Compaction is OPT-IN per tool via a sentinel: a line consisting solely of the marker below
// (surrounding whitespace ignored). Everything ABOVE that line is the compact head; the full text
// is head + everything below, with the marker line removed. A tool WITHOUT the marker is served
// unchanged — so compaction rolls out one description at a time, nothing breaks by default.
//
// The marker is `[[full]]` on its own line rather than a bare `---`, so it can never collide with a
// GFM thematic break or a YAML front-matter fence that legitimately appears in a description essay.
static class McpToolDescriptions
{
	public const string Sentinel = "[[full]]";

	public static bool HasSentinel(string? description) =>
		description is not null && Lines(description).Any(IsSentinel);

	// The compact head: the text ABOVE the sentinel line, trimmed. No sentinel → the text unchanged.
	public static string? Head(string? description)
	{
		if (description is null) return null;
		if (!HasSentinel(description)) return description;
		var head = new List<string>();
		foreach (var line in Lines(description))
		{
			if (IsSentinel(line)) break;
			head.Add(line);
		}
		return string.Join("\n", head).Trim();
	}

	// The full text with the lone sentinel line removed (head + body rejoined). No sentinel → unchanged.
	public static string? Full(string? description)
	{
		if (description is null) return null;
		if (!HasSentinel(description)) return description;
		return string.Join("\n", Lines(description).Where(l => !IsSentinel(l))).Trim();
	}

	// Serve-time projection for tools/list: a tool WITH a sentinel is replaced by a CLONE carrying only
	// its head (cloning, not mutating, because the Tool instances are shared with the server's canonical
	// ToolCollection that tool_describe reads the full text from); a tool without a sentinel is returned
	// as-is (reference-equal), so the un-piloted ~majority stay byte-identical on the wire.
	public static Tool Compact(Tool tool)
	{
		if (!HasSentinel(tool.Description)) return tool;
		return new Tool
		{
			Name = tool.Name,
			Title = tool.Title,
			Description = Head(tool.Description),
			InputSchema = tool.InputSchema,
			OutputSchema = tool.OutputSchema,
			Annotations = tool.Annotations,
			Icons = tool.Icons,
			Meta = tool.Meta,
		};
	}

	static string[] Lines(string s) =>
		s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

	static bool IsSentinel(string line) => line.Trim() == Sentinel;
}

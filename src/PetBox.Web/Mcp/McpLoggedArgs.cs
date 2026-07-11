using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using ModelContextProtocol.Server;

namespace PetBox.Web.Mcp;

// The [LogArg] registry: tool name -> the parameters that opted into telemetry.
//
// Built ONCE (static ctor, over the tools assembly) with the SAME predicate as the tool scan in
// McpOutputSchema.WithSchemaHonestToolsFromAssembly — [McpServerToolType] on the type,
// [McpServerTool] on the method — so what is registered as a tool is exactly what can be shaped.
// The hot path (McpTracingFilter, every CallTool) then does ONE FrozenDictionary lookup by tool
// name and walks a tiny array: no per-call reflection, no per-call string building (the span tag
// and the log property name are precomputed here).
static class McpLoggedArgs
{
	// A marked parameter, with its telemetry names resolved up front.
	// SpanTag: the existing convention — petbox.arg.<snake_case> (petbox.arg.body_len).
	// LogProperty: Arg_<paramName> VERBATIM from the signature (Arg_bodyLen) — a KQL column, and
	// the signature spelling is the only name a reader can grep back to the tool.
	internal readonly record struct LoggedArg(string Name, LogArgMode Mode, string SpanTag, string LogProperty);

	// A marked parameter that was actually PRESENT on a call, with its privacy-safe value
	// (the number/bool itself in Value mode; a bare `true` in Presence mode). Carries both
	// telemetry names so neither sink has to hit the registry again.
	internal readonly record struct MarkedArg(string SpanTag, string LogProperty, object? Value);

	static readonly FrozenDictionary<string, ImmutableArray<LoggedArg>> Map =
		Build(typeof(McpLoggedArgs).Assembly);

	// The marked params of a tool; empty for an unknown/unmarked tool — the SAFE DEFAULT: a tool
	// nobody marked up contributes NO arg telemetry at all.
	public static ImmutableArray<LoggedArg> For(string? tool) =>
		tool is not null && Map.TryGetValue(tool, out var args) ? args : [];

	internal static FrozenDictionary<string, ImmutableArray<LoggedArg>> Build(Assembly assembly)
	{
		var map = new Dictionary<string, ImmutableArray<LoggedArg>>(StringComparer.Ordinal);

		foreach (var toolType in assembly.GetTypes())
		{
			if (toolType.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

			foreach (var method in toolType.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
			{
				// The tool NAME is what arrives on the wire (CallToolRequestParams.Name) and is
				// always set explicitly on the attribute in this codebase; without it there is
				// nothing to key by, so such a method simply carries no markup.
				if (method.GetCustomAttribute<McpServerToolAttribute>()?.Name is not { } name) continue;

				var marked = method.GetParameters()
					.Select(p => (Param: p, Attr: p.GetCustomAttribute<LogArgAttribute>()))
					.Where(x => x.Attr is not null && x.Param.Name is not null)
					.Select(x => new LoggedArg(
						x.Param.Name!, x.Attr!.Mode, $"petbox.arg.{Snake(x.Param.Name!)}", $"Arg_{x.Param.Name}"))
					.ToImmutableArray();

				if (!marked.IsEmpty)
					map[name] = marked;
			}
		}

		return map.ToFrozenDictionary(StringComparer.Ordinal);
	}

	// camelCase -> snake_case (bodyLen -> body_len): the span-tag spelling OTel conventions want,
	// and the spelling the existing petbox.arg.* tags already use.
	static string Snake(string name)
	{
		var sb = new System.Text.StringBuilder(name.Length + 4);
		foreach (var c in name)
		{
			if (char.IsUpper(c))
			{
				if (sb.Length > 0) sb.Append('_');
				sb.Append(char.ToLowerInvariant(c));
			}
			else
			{
				sb.Append(c);
			}
		}
		return sb.ToString();
	}
}

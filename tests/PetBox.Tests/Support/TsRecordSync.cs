using System.Reflection;
using System.Text.RegularExpressions;
using PetBox.Core.Settings;

namespace PetBox.Tests.Support;

// The C#<->TS parity guard ui-state-framework asks for INSTEAD of a codegen pipeline (there is
// none in this project, and none is being added): reflect over a C# record's [BrowserState]
// properties and compare them, by key, against a hand-written TS interface. Used by
// UiStateTypeSyncTests both against the REAL BrowserState.cs / ui-state.ts pair (a guard — see
// that test for why it's vacuous today) and against synthetic fixtures, to prove the comparator
// itself fails loudly on a real divergence.
public static class TsRecordSync
{
	// One entry per divergence found — human-readable and names the property, so a failing
	// assertion tells the reader exactly what to fix without re-deriving the diff.
	public static IReadOnlyList<string> Diff(Type csharpType, string tsInterfaceSource, string interfaceName)
	{
		var csProps = csharpType
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => (Prop: p, Attr: p.GetCustomAttribute<BrowserStateAttribute>()))
			.Where(x => x.Attr is not null)
			.ToDictionary(x => x.Attr!.Key, x => x.Prop, StringComparer.Ordinal);

		var tsProps = ParseInterface(tsInterfaceSource, interfaceName);

		var diffs = new List<string>();

		foreach (var (key, prop) in csProps)
		{
			if (!tsProps.TryGetValue(key, out var tsType))
			{
				diffs.Add($"'{key}' ({csharpType.Name}.{prop.Name}) is [BrowserState] in C# but missing from the TS interface {interfaceName}.");
				continue;
			}

			var expected = CsTypeToTs(prop.PropertyType);
			if (!string.Equals(expected, tsType, StringComparison.Ordinal))
				diffs.Add($"'{key}' ({csharpType.Name}.{prop.Name}): C# type maps to TS '{expected}' but the interface declares '{tsType}'.");
		}

		foreach (var key in tsProps.Keys)
		{
			if (!csProps.ContainsKey(key))
				diffs.Add($"'{key}' is declared in the TS interface {interfaceName} but no [BrowserState] property on {csharpType.Name} produces it.");
		}

		return diffs;
	}

	// Minimal parser for a flat `interface Name { key?: type; ... }` block — no nested objects, no
	// generics beyond what a hand-written, deliberately small wire-shape interface needs. If it
	// ever needs more, grow it here rather than starting a second parser.
	static Dictionary<string, string> ParseInterface(string source, string interfaceName)
	{
		var start = Regex.Match(source, $@"interface\s+{Regex.Escape(interfaceName)}\s*\{{");
		if (!start.Success)
			throw new InvalidOperationException($"No `interface {interfaceName} {{` found in the given TS source.");

		var bodyStart = start.Index + start.Length;
		var close = source.IndexOf('}', bodyStart);
		if (close < 0)
			throw new InvalidOperationException($"Unterminated interface {interfaceName} in the given TS source.");

		var body = source[bodyStart..close];
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (Match m in Regex.Matches(body, @"^\s*(\w+)\??\s*:\s*([^;]+);", RegexOptions.Multiline))
			result[m.Groups[1].Value] = m.Groups[2].Value.Trim();
		return result;
	}

	static string CsTypeToTs(Type t)
	{
		var underlying = Nullable.GetUnderlyingType(t) ?? t;
		if (underlying == typeof(string)) return "string";
		if (underlying == typeof(bool)) return "boolean";
		if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(double) || underlying == typeof(float))
			return "number";
		// UiStateResolver serializes enums with JsonStringEnumConverter, so they cross the wire —
		// and should be declared in TS — as strings (or a string-literal union; callers that need
		// that precision compare it themselves, this guard only checks the "string" floor).
		if (underlying.IsEnum) return "string";
		throw new NotSupportedException($"No TS type mapping registered for CLR type {underlying.Name}. Add one to TsRecordSync.CsTypeToTs.");
	}
}

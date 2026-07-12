using System.Text.RegularExpressions;

namespace PetBox.Tests.Docs;

// F1 — a stale agent guide MISROUTES deploys, silently.
//
// deploy_upsert's project parameter was renamed `project` → `projectKey` (every project-routing MCP
// arg is spelled that way, and McpProjectDefaultFilter keys its default-injection on exactly that
// name). doc/guides/deploy-fleet.md was updated; doc/guides/deploy-fleet-agent.md was not — and an
// agent following it passes `project=…`, which the binder IGNORES while the filter INJECTS the key's
// default into `projectKey`. The deployment then lands in the wrong project instead of failing.
//
// So the guides are checked mechanically: no doc may spell a projectKey argument `project`.
public sealed class McpToolArgNamesInDocsTests
{
	// A tool-call sample line: `<tool_name>(` … with a `project=` / `project:` ARGUMENT in it.
	// "projectKey=" does not match (after `project` comes `K`, not `=`/`:`). String LITERALS are blanked
	// first, so a config tag vector — `tags: "ws:myws,project:kpvotes"`, a legitimate `project:` that
	// does live on tool-call lines — is not mistaken for an argument name.
	static readonly Regex ToolCall = new(@"\b[a-z]+_[a-z_]+\s*\(", RegexOptions.Compiled);
	static readonly Regex StaleArg = new(@"\bproject\s*[=:]", RegexOptions.Compiled);
	static readonly Regex Literals = new("\"[^\"]*\"|'[^']*'", RegexOptions.Compiled);

	static string WithoutLiterals(string line) => Literals.Replace(line, "\"\"");

	static string DocRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "doc", "guides");
			if (Directory.Exists(candidate)) return Path.Combine(dir, "doc");
			dir = Path.GetDirectoryName(dir);
		}
		throw new DirectoryNotFoundException("doc/guides not found walking up from the test bin");
	}

	[Fact]
	public void NoDoc_PassesAProjectArg_ToAnMcpTool()
	{
		var offenders = Directory
			.EnumerateFiles(DocRoot(), "*.md", SearchOption.AllDirectories)
			.SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, No: i + 1, Line: line)))
			.Where(x => ToolCall.IsMatch(x.Line) && StaleArg.IsMatch(WithoutLiterals(x.Line)))
			.Select(x => $"{Path.GetFileName(x.File)}:{x.No}: {x.Line.Trim()}")
			.ToList();

		offenders.Should().BeEmpty(
			"every project-routing MCP argument is named `projectKey` — a doc that says `project` teaches "
			+ "agents a call whose project arg is silently dropped (and then filled with the key's default)");
	}

	[Fact]
	public void TheAgentDeployGuide_TeachesProjectKey()
	{
		var guide = File.ReadAllText(Path.Combine(DocRoot(), "guides", "deploy-fleet-agent.md"));
		guide.Should().Contain("deploy_upsert(service=\"<svc>\", projectKey=");
	}
}

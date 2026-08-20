using Microsoft.Extensions.Logging;
// PetBox.Tests has a global using for PetBox.Log.Core.Models, whose own LogLevel collides with the
// logging abstraction's. Alias, so the capturing logger below implements the interface it says it does.
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;

namespace PetBox.Tests.Auth;

// apikey-principal-authz-cluster, finding 2. ConfigApiKeyLookup copies `Auth:ApiKeys[]`.Scopes into
// ApiKey.Scopes VERBATIM — it is the one door into that column that never ran ApiKeyScopes.Validate
// (mint, the admin form, the project-page re-scope and the MCP patch all do). So an operator typo in
// appsettings/env reaches the authorization gates as a token that matches nothing, and since
// `scope-claims-canonicalization` made every transport compare Ordinal, a wrong CASE ("Data:Read")
// is exactly as dead as a wrong WORD ("log:query") — with no message anywhere saying so.
//
// THE CHOSEN REMEDY IS A WARNING, NOT A REFUSAL, and these tests pin BOTH halves of that choice: the
// warning fires, AND the key is still served. A throw here would run inside a singleton constructor
// during host build, i.e. one stale token in a config file would become a process that does not
// start — on the one plane that cannot be fixed through the UI (config keys have no `apikey_list`
// row and no revoke; only edit-and-restart).
public sealed class ConfigApiKeyScopeWarningTests
{
	sealed record Entry(string Level, string Message);

	sealed class CapturingLogger : ILogger<ConfigApiKeyLookup>
	{
		public List<Entry> Entries { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;
		public void Log<TState>(
			MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add(new Entry(logLevel.ToString(), formatter(state, exception)));
	}

	static (ConfigApiKeyLookup Lookup, CapturingLogger Log) Build(params ConfigApiKeyEntry[] entries)
	{
		var log = new CapturingLogger();
		var lookup = new ConfigApiKeyLookup(
			Options.Create(new ConfigApiKeyOptions { ApiKeys = entries }), log);
		return (lookup, log);
	}

	// A wrong WORD. "log:query" is not in the catalog (the scope is "logs:query"), so it authorizes
	// nothing and used to do so in total silence.
	[Fact]
	public void UnknownScopeToken_IsWarnedAboutAtStartup()
	{
		var (_, log) = Build(new ConfigApiKeyEntry
		{
			Key = "yb_cfg_typo",
			ProjectKey = "cfgproj",
			Scopes = "logs:query, log:query",
		});

		var warning = log.Entries.Should().ContainSingle(e => e.Level == "Warning").Subject;
		warning.Message.Should().Contain("log:query", "the operator needs the offending token named");
		warning.Message.Should().Contain("cfgproj", "and the entry it belongs to identified");
		warning.Message.Should().NotContain("yb_cfg_typo",
			"the key VALUE is a credential and must never reach the log");
	}

	// A wrong CASE. This is the half that only became lethal with `scope-claims-canonicalization`:
	// REST used to compare scopes case-insensitively, so "Data:Read" worked there and nowhere else.
	// It now works nowhere, which is consistent — and silent, which is not.
	[Fact]
	public void WrongCaseScopeToken_IsWarnedAboutToo()
	{
		var (_, log) = Build(new ConfigApiKeyEntry
		{
			Key = "yb_cfg_case",
			ProjectKey = "cfgproj",
			Scopes = "Data:Read",
		});

		log.Entries.Should().ContainSingle(e => e.Level == "Warning")
			.Which.Message.Should().Contain("Data:Read",
				"Ordinal comparison makes a case mismatch a dead token, not a lenient one");
	}

	// The refusal that must NOT happen: a key with one bad token still authenticates, and still
	// carries its good ones. This is the assertion that fails if someone later "hardens" the warning
	// into a throw — which would trade a silent misconfiguration for a dead installation.
	[Fact]
	public void KeyWithABadToken_IsStillServed_WithItsRemainingScopes()
	{
		var (lookup, _) = Build(new ConfigApiKeyEntry
		{
			Key = "yb_cfg_typo",
			ProjectKey = "cfgproj",
			Scopes = "logs:query, log:query",
		});

		var key = lookup.FindByKey("yb_cfg_typo");
		key.Should().NotBeNull("a warning must not remove the key from the lookup");
		key!.Scopes.Should().Be("logs:query, log:query",
			"the stored string is projected unchanged — the warning explains it, it does not rewrite it");
		ApiKeyScopes.Granted(key.Scopes, ApiKeyScopes.LogsQuery).Should().BeTrue(
			"the VALID half of the grant must keep working");
	}

	// The quiet case, so the warning cannot degrade into noise nobody reads: a correctly spelled
	// entry produces no log line at all.
	[Fact]
	public void ValidScopes_ProduceNoWarning()
	{
		var (_, log) = Build(new ConfigApiKeyEntry
		{
			Key = "yb_cfg_ok",
			ProjectKey = "cfgproj",
			Scopes = "logs:query data:read,tasks:write",
		});

		log.Entries.Should().BeEmpty("every token is in the catalog, so there is nothing to report");
	}

	// One line PER KEY, not per token: three typos on one entry must not read like three broken keys.
	[Fact]
	public void MultipleBadTokensOnOneEntry_ProduceOneLine_NamingAllOfThem()
	{
		var (_, log) = Build(new ConfigApiKeyEntry
		{
			Key = "yb_cfg_many",
			ProjectKey = "cfgproj",
			Scopes = "log:query, Data:Read, tasks:writ",
		});

		var warning = log.Entries.Should().ContainSingle(e => e.Level == "Warning").Subject;
		warning.Message.Should().Contain("log:query").And.Contain("Data:Read").And.Contain("tasks:writ");
	}
}

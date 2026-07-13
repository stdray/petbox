using PetBox.Core.Models;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Web;

// logs-traces-default-log: the Logs/Traces pages used to preselect NOTHING when the request
// carried no ?log=/?logName=, rendering the same blank as a project with zero telemetry — that
// indistinguishability (not a crash) is what fooled the maintainer into thinking a live self-log
// full of spans had no data. DefaultLogSelector.Resolve is the pure rule both page models now
// share; these tests pin the rule directly, independent of the ASP.NET/DB plumbing (see
// TracesListFilterTests / LogsIndexDefaultLogTests for the page-model-level regression tests).
public sealed class DefaultLogSelectorTests
{
	static LogMeta Log(string name, DateTime createdAt) => new()
	{
		ProjectKey = "proj",
		Name = name,
		CreatedAt = createdAt,
		UpdatedAt = createdAt,
	};

	[Fact]
	public void No_logs_resolves_to_null()
	{
		DefaultLogSelector.Resolve([], requested: null).Should().BeNull();
	}

	[Fact]
	public void Single_log_wins_regardless_of_name()
	{
		var logs = new[] { Log("cc-telemetry", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc)) };
		DefaultLogSelector.Resolve(logs, requested: null).Should().Be("cc-telemetry");
	}

	[Fact]
	public void Explicit_request_wins_over_everything_else()
	{
		var logs = new[]
		{
			Log("app", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
			Log("default", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
		};
		DefaultLogSelector.Resolve(logs, requested: "app").Should().Be("app");
	}

	[Fact]
	public void Unknown_requested_name_falls_through_to_the_rest_of_the_rule()
	{
		var logs = new[]
		{
			Log("app", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
			Log("default", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
		};
		DefaultLogSelector.Resolve(logs, requested: "does-not-exist").Should().Be("default");
	}

	[Fact]
	public void A_log_literally_named_default_wins_among_several()
	{
		var logs = new[]
		{
			Log("app", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
			Log("default", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
			Log("worker", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
		};
		DefaultLogSelector.Resolve(logs, requested: null).Should().Be("default");
	}

	// The reproduced production bug: three logs on $system, none named "default". Alphabetical
	// order (the OLD rule) picks "cc-telemetry" — a short-lived telemetry spike created weeks
	// after "petbox", the long-running self-log that actually holds the data. The new rule picks
	// the OLDEST log instead, so a newer, differently-named experiment can never silently outrank
	// the project's established log.
	[Fact]
	public void Several_logs_without_a_default_name_pick_the_OLDEST_not_the_alphabetically_first()
	{
		var logs = new[]
		{
			Log("cc-telemetry", new DateTime(2026, 7, 6, 11, 6, 2, DateTimeKind.Utc)),
			Log("petbox", new DateTime(2026, 5, 29, 18, 10, 16, DateTimeKind.Utc)),
			Log("prompt-rag-audit", new DateTime(2026, 7, 6, 13, 29, 53, DateTimeKind.Utc)),
		};
		DefaultLogSelector.Resolve(logs, requested: null).Should().Be("petbox");
	}

	[Fact]
	public void Ties_in_CreatedAt_break_by_name_ordinal()
	{
		var same = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		var logs = new[] { Log("zeta", same), Log("alpha", same) };
		DefaultLogSelector.Resolve(logs, requested: null).Should().Be("alpha");
	}
}

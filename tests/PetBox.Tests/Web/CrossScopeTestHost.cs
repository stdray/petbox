using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Web.Navigation;
using PetBox.Web.Search;

namespace PetBox.Tests.Web;

// Shared plumbing for the cross-scope fan-out tests. The fan-out is ALWAYS built out of a DI
// container here — never by `new` — because that is how production builds it (Program.cs:500,
// AddScoped) and because the fix under test is a DI-shaped one: each fan-out branch takes its own
// IServiceScope, hence its own PetBoxDb. A test that hand-constructed the service with one
// ITasksService could not tell the fixed graph from the broken one.
static class CrossScopeTestHost
{
	// A container that hands EVERY branch the SAME ITasksService instance — deliberately: it pins
	// the per-store connection fixes (TaskBoardStore/LlmRegistryLevelResolver open their own
	// connection) INDEPENDENTLY of the per-branch DI scope. Used by the leg-level repros.
	public static ServiceProvider SharedTasksService(ITasksService tasks)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
		services.AddSingleton<INavigationContext, UnusedNavigationContext>();
		services.AddScoped(_ => tasks);
		services.AddScoped<CrossScopeTaskSearchService>();
		return services.BuildServiceProvider();
	}

	public static CrossScopeTaskSearchService Search(this ServiceProvider sp) =>
		sp.GetRequiredService<CrossScopeTaskSearchService>();

	// The fan-out's testable core takes the project enumeration explicitly, so the navigation
	// context is never touched — it only has to be resolvable.
	sealed class UnusedNavigationContext : INavigationContext
	{
		public bool IsAuthenticated => throw new NotSupportedException();
		public string? Username => throw new NotSupportedException();
		public string? CurrentWorkspaceKey => throw new NotSupportedException();
		public bool HasWorkspace => throw new NotSupportedException();
		public string? CurrentProjectKey => throw new NotSupportedException();
		public IReadOnlyList<WorkspaceOption> AvailableWorkspaces => throw new NotSupportedException();
		public IReadOnlyList<Project> ProjectsInCurrentWorkspace => throw new NotSupportedException();
		public IReadOnlyDictionary<string, IReadOnlyList<Project>> ProjectsByWorkspace => throw new NotSupportedException();
		public bool DataEnabled => throw new NotSupportedException();
		public bool TasksEnabled => throw new NotSupportedException();
		public bool MemoryEnabled => throw new NotSupportedException();
		public bool LlmRouterEnabled => throw new NotSupportedException();
	}
}

// Captures every log line the graph emits, so a test can assert on what production would SEE.
// This matters here: the embed-leg race does not escape the fan-out — SearchService catches a
// failing index and degrades honestly (event 400) — so "it didn't throw" proves nothing and the
// log is the only channel that tells the truth.
sealed class CapturingLoggerProvider : ILoggerProvider
{
	public ConcurrentBag<string> Lines { get; } = [];

	public ILogger CreateLogger(string categoryName) => new Sink(Lines);
	public void Dispose() { }

	sealed class Sink(ConcurrentBag<string> lines) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;

		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			lines.Add($"{logLevel} {formatter(state, exception)}{(exception is null ? "" : " || " + exception.GetType().Name + ": " + exception.Message)}");
	}
}

using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;

namespace PetBox.Tests;

// ModuleMcp.AssertProject/ResolveProject (spec work/smoke-writes-into-real-projects) now resolve
// IProjectCatalog off the caller's HttpContext.RequestServices — the sandbox write gate's
// containment check (ProjectScope.AuthorizesAsync) is a DB read, and the DI container is where
// every other per-request service already comes from. A unit test that hand-builds a bare
// `DefaultHttpContext` therefore needs SOME resolvable IProjectCatalog or the lookup itself throws
// before AssertProject's own logic ever runs.
//
// None of the tests that use this stub set the `sandbox_only` claim, so the containment check
// always short-circuits on `!sandboxOnly` and IsSandboxAsync is never actually invoked — hence the
// NotSupportedException bodies: a test that DOES reach one is exercising the real gate and should
// use a real (or a purpose-built fake) IProjectCatalog instead, not this generic stub.
public sealed class TestProjectCatalog : IProjectCatalog
{
	public static readonly TestProjectCatalog Instance = new();

	// A ready-to-attach RequestServices for `new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, ... }`.
	public static readonly IServiceProvider Services =
		new ServiceCollection().AddSingleton<IProjectCatalog>(Instance).BuildServiceProvider();

	public Task<IReadOnlyList<string>> ListProjectKeysAsync(CancellationToken ct = default) =>
		throw new NotSupportedException($"{nameof(TestProjectCatalog)} is a sandbox-gate DI stub only");

	public Task<IReadOnlyList<string>> ListWorkspaceKeysAsync(CancellationToken ct = default) =>
		throw new NotSupportedException($"{nameof(TestProjectCatalog)} is a sandbox-gate DI stub only");

	public Task<IReadOnlyList<string>> ListMemoryProjectKeysAsync(CancellationToken ct = default) =>
		throw new NotSupportedException($"{nameof(TestProjectCatalog)} is a sandbox-gate DI stub only");

	public Task<IReadOnlyList<string>> ListTaskProjectKeysAsync(CancellationToken ct = default) =>
		throw new NotSupportedException($"{nameof(TestProjectCatalog)} is a sandbox-gate DI stub only");

	// Never sandbox — every attached test either never sets sandboxOnly (the common case, where this
	// is never even called) or brings its own real/fake catalog when it actually means to exercise
	// the gate.
	public Task<bool> IsSandboxAsync(string projectKey, CancellationToken ct = default) => Task.FromResult(false);

	// Live-tail's cookie path (LogApi.AuthorizeLiveTailAsync) asks this; a test that exercises THAT
	// gate needs a real catalog, so the stub keeps the same "not supported" posture as the lists.
	public Task<string?> WorkspaceKeyOfAsync(string projectKey, CancellationToken ct = default) =>
		throw new NotSupportedException($"{nameof(TestProjectCatalog)} is a sandbox-gate DI stub only");
}

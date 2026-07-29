namespace PetBox.Tests.Architecture;

// THE RAZOR BLIND SPOT, ASSERTED — the half of the page plane nobody had ever aimed at another tenant.
//
// AuthzCrossTenantTests probes one call per SURFACE, and a Razor page is one surface however many
// handlers it has. That is what makes its 217 accounting add up, and it is also why its page sweep only
// ever sent GET: every `?handler=` mutation in the tree was unmeasured. 82 POST handlers across 31
// pages, and a mutation through another tenant's project is strictly worse than a read of one, so the
// unmeasured half was the dangerous half.
//
// This file asks the same question of those handlers, from its own list (AuthzCrossTenantHost.
// PagePostProbes) so that the numbers next door keep meaning what they meant. There is deliberately NO
// deviation list here: it was written after enforcement, not before it, so it has nothing to grandfather
// — an entry would be a hole somebody decided to keep, and that decision belongs to the owner, not to
// this file.
[Collection("WebAppFactory")]
public sealed class AuthzCrossTenantPostHandlerTests : IClassFixture<AuthzCrossTenantHost>
{
	readonly AuthzCrossTenantHost _host;

	public AuthzCrossTenantPostHandlerTests(AuthzCrossTenantHost host) => _host = host;

	[Fact]
	public void EveryPagePostHandler_RefusesAForeignTenant()
	{
		var served = _host.PagePostProbes
			.Where(p => p.Verdict != CrossTenantVerdict.Denied)
			.OrderBy(p => p.Verdict)
			.ThenBy(p => p.Key, StringComparer.Ordinal)
			.Select(p => $"  [{p.Verdict}] {p.Key}\n      call:     {p.Call}\n      answered: {p.Observed}")
			.ToList();

		served.Should().BeEmpty(
			"a POST that names ANOTHER tenant must be refused on the authorization axis before the handler "
			+ "runs — every one of these MUTATES, so 'served' here is one tenant writing into another's "
			+ "data, not a form-of-denial question. If a handler needs to cross tenants it belongs to one of "
			+ "the six exemption classes and its PAGE declares that; there is no per-handler exemption and "
			+ "no list in this file to add it to.\n" + string.Join("\n", served));
	}

	// ── GUARD THE GUARD ──────────────────────────────────────────────────────────────────────────

	// The sweep must actually have found the handlers. If HandlerMethods ever stopped being readable — a
	// framework change, a page descriptor that is no longer Compiled — the assertion above would pass
	// over an empty list and this whole file would protect nothing.
	[Fact]
	public void TheSweep_ActuallyFoundThePostHandlers()
	{
		_host.PagePostProbes.Should().HaveCountGreaterThan(40,
			"the tree has 82 OnPost*Async handlers across 31 pages; the ones swept here are those on a page "
			+ "whose ROUTE has a tenant slot to aim. If this collapses toward zero the sweep found nothing");

		_host.PagePostProbes.Select(p => p.Key).Should().OnlyHaveUniqueItems(
			"one probe per (page, handler, route template) triple");

		// Anchors on three different families, so a sweep that lost a whole page tree fails here rather
		// than reporting an inventory of untouched handlers.
		var keys = _host.PagePostProbes.Select(p => p.Key).ToList();
		keys.Should().Contain(k => k.StartsWith("page:/ProjectHome/TaskBoard?handler=Create", StringComparison.Ordinal),
			"the board quick-add is the mutation workspace-access-isolation was filed about");
		keys.Should().Contain(k => k.StartsWith("page:/Admin/WorkspaceUsers?handler=Add", StringComparison.Ordinal),
			"membership writes are the highest-privilege POST on the workspace plane");

		// THE TRAP FAMILY, BOTH WAYS ROUND. /Config/Index carries one class-level declaration and two route
		// templates, and the workspace-only one is where [TenantFrom(Route, "projectKey")] would have
		// resolved to nothing. Its POST handlers must be probed on BOTH — a sweep keyed by page alone would
		// have dropped whichever template it saw second and never asked the question that matters.
		keys.Where(k => k.StartsWith("page:/Config/Index?handler=Delete [", StringComparison.Ordinal))
			.Should().HaveCount(2, "the workspace-scoped AND the project-scoped template of the same handler");
	}

	// AND THE SWEEP MUST NOT BE MEASURING ITSELF. The tenant PEP is middleware and decides long before
	// MVC's antiforgery filter, so a probe with a bad token would still get a real tenant refusal on a
	// well-behaved surface — but a 400 cannot tell "the check is late" from "my token was rejected", and a
	// universal 400 would read as a field of denials to any assertion that accepted 4xx.
	//
	// This is the same failure mode as the 415 the REST wave chased: the old probe was measuring its own
	// content-type guess, not the surface. So 400 is called out as INCONCLUSIVE and fails on its own.
	[Fact]
	public void NoPagePostHandler_AnsweredAnInconclusiveArgumentError()
	{
		var inconclusive = _host.PagePostProbes
			.Where(p => p.Verdict == CrossTenantVerdict.ArgumentError)
			.Select(p => $"  {p.Key}\n      call:     {p.Call}\n      answered: {p.Observed}")
			.ToList();

		inconclusive.Should().BeEmpty(
			"a 400 here is ambiguous and must not be counted as a refusal: it is either the sweep's own "
			+ "antiforgery pair being rejected (in which case every result in this file is meaningless) or a "
			+ "surface that BOUND the request before it judged it (in which case the refusal is unreachable "
			+ "without valid arguments). Both are defects; neither is a denial.\n"
			+ string.Join("\n", inconclusive));
	}

	// ── THE MACHINE REPORT ───────────────────────────────────────────────────────────────────────

	// Same discipline as the other two sweeps: any number this closes-the-blind-spot work reports has to
	// be reproducible by running the test and reading the file, never quoted from a commit message.
	[Fact]
	public void ThePostSweep_IsMachineReadable()
	{
		var rendered = AuthzCrossTenantHost.Render(_host.PagePostProbes);
		var dir = Path.Combine(AuthzCrossTenantHost.RepoRoot(), ".tmp");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "authz-cross-tenant-post-report.txt"), rendered);

		_host.PagePostProbes.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Observed),
			"a probe with nothing observed is a probe that never ran");
	}
}

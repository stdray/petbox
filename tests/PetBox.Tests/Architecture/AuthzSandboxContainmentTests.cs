namespace PetBox.Tests.Architecture;

// SANDBOX CONTAINMENT ON THE CONTAINER SPELLING OF A WORKSPACE — work
// `memory-container-sandbox-containment-bypass`.
//
// WHY THE 217-SURFACE CROSS-TENANT PROBE DID NOT CATCH THIS, which is worth stating precisely because
// it is the reason coverage had to be added rather than merely re-run. That probe's attacker holds a
// key scoped to a neighbouring PROJECT and NOT marked sandboxOnly, and it writes tenant keys only in
// their plain spelling. The leak needed BOTH of the things it lacks — the `$workspace` / `$ws-<key>`
// container spelling, and the `sandboxOnly` flag whose enforcement was skipped. A probe missing either
// one reports green. That is not a lapse in diligence; it is what an inventory-shaped test cannot
// reach, and it is why a count of surfaces was never the same thing as a count of questions asked.
//
// The sweep behind these assertions lives on its own host (SandboxContainmentHost) rather than on
// AuthzCrossTenantHost. That was tried and reverted: the extra tenants this question needs perturbed
// the 217-surface probe's own guard, which reads a truncated `project_list` response. Its numbers are
// a calibrated instrument, and a different question does not get to share its fixture.
[Collection("WebAppFactory")]
public sealed class AuthzSandboxContainmentTests : IClassFixture<SandboxContainmentHost>
{
	readonly SandboxContainmentHost _host;

	public AuthzSandboxContainmentTests(SandboxContainmentHost host) => _host = host;

	// EVERY memory verb, at every container spelling, refused for a sandboxOnly key.
	//
	// This is the argument-addressed half — the half TenantAuthorizer.KeyOnWorkspaceAsync answers,
	// where the PEP decides before the tool body runs. The `scope:"workspace"` half is a different code
	// path entirely and is asserted by NoSandboxOnlyKeyShape_ReachesWorkspaceMemoryItIsNotContainedBy
	// in SandboxContainmentMeasurement; a fix to only one of the two leaves the other wide open, which
	// is exactly what the first attempt at this work did.
	[Fact]
	public void SandboxOnlyKey_IsRefusedAtEveryWorkspaceContainer()
	{
		var served = _host.ContainerProbes
			.Where(p => !p.Denied)
			.OrderBy(p => p.Container, StringComparer.Ordinal)
			.ThenBy(p => p.Verb, StringComparer.Ordinal)
			.Select(p => $"  {p.Verb} [{p.Container}]\n      answered: {p.Observed}")
			.ToList();

		served.Should().BeEmpty(
			"a sandboxOnly key cannot satisfy containment on a WORKSPACE target — a workspace carries no "
			+ "Sandbox flag and strictly contains projects that are not sandboxes, so there is no workspace "
			+ "for which containment could be demonstrated. Anything but an UnauthorizedAccessException "
			+ "here is the production leak of 2026-07-25 available again: a smoke-shaped key reaching "
			+ "another tenant's shared memory through the container door while the same key is correctly "
			+ "refused through the project door.\n" + string.Join("\n", served));
	}

	// ── GUARD THE GUARD ──────────────────────────────────────────────────────────────────────────

	// Every assertion above is satisfied by a DENIAL, so a sweep that stopped calling anything, or a key
	// that stopped authenticating, would make this file green while testing nothing.
	[Fact]
	public void TheSweep_ActuallyCalledTheMemoryFamily()
	{
		_host.ContainerProbes.Should().NotBeEmpty("a sweep with no calls proves nothing");

		var verbs = _host.ContainerProbes.Select(p => p.Verb).Distinct(StringComparer.Ordinal).ToList();

		// Reads AND writes, named rather than counted, so a sweep that quietly lost the mutating half
		// fails here instead of reporting a shorter list of denials. memory_remember / memory_upsert are
		// the write path the work card could only hypothesise about ("запись эмпирически не
		// проверялась") because proving it live would have meant writing garbage into real workspace
		// memory; here it costs nothing, so the hypothesis is settled rather than inherited.
		verbs.Should().Contain(
			["memory_store_list", "memory_search", "memory_get", "memory_remember", "memory_upsert", "memory_store_create"],
			"the sweep must cover the verbs the production measurement used AND the write path the card left unverified");

		_host.ContainerProbes.Select(p => p.Container).Distinct(StringComparer.Ordinal)
			.Should().HaveCount(2, "the key's own workspace container AND a container of a workspace it is not in");

		// A binder error would mean the call never reached the tool body — 'the check is late' and
		// 'there is no check' would then be indistinguishable, the same trap the cross-tenant probe
		// guards against with its garbage-argument filling.
		_host.ContainerProbes
			.Where(p => p.Observed.Contains("missing a value for the required parameter", StringComparison.Ordinal))
			.Should().BeEmpty("every call must reach the tool body, or its denial says nothing about tenancy");
	}

	// THE DIFFERENTIAL. Without this, "denied everywhere" is equally consistent with having broken the
	// container path for every caller — a bigger outage than the leak it fixes.
	[Fact]
	public void TheTightening_IsConfinedToSandboxOnlyKeys()
	{
		_host.ContainmentControls.Should().HaveCount(2, "one control per axis of the differential");
		_host.ContainmentControls.Where(c => !c.Served).Should().BeEmpty(
			"BOTH of these must be SERVED, and each rules out a different way of being wrong:\n"
			+ "  * the sandboxOnly key on a real SANDBOX PROJECT proves the key still works at all — if it "
			+ "did not, every denial in this file would be the denial of a broken caller;\n"
			+ "  * the plain wildcard key on the SAME container the sweep is refused at proves the refusal "
			+ "is caused by `sandboxOnly` and not by the container path being closed wholesale. The two "
			+ "keys differ in that flag and in nothing else — same wildcard claim, same scopes.\n  "
			+ string.Join("\n  ", _host.ContainmentControls.Select(c => $"{c.Name} -> {c.Observed}")));
	}
}

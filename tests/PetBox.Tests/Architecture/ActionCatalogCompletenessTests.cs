using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using PetBox.Core.Auth;
using PetBox.Dashboard;
using PetBox.Deploy.Services;

namespace PetBox.Tests.Architecture;

// THE RATCHET for spec `action-catalog-completeness`, over the artifact of spec
// `action-catalog-artifact` (PetBox.Core.Auth.ActionCatalog + its embedded action-catalog.json).
//
// WHAT IT ASSERTS: the catalog of domain actions and the LIVE surface of the running application
// describe the same system. That is TWO statements, and the acceptance criterion is that it goes red
// on both of them:
//
//   ARROW 1 — a live surface nobody catalogued. A new endpoint, page or MCP verb arrives with no
//             action record and no `unmapped` line, and the build fails naming the key and its owner.
//   ARROW 2 — a catalog record naming something that is no longer there. A surface key that has been
//             renamed or deleted, or a background/internal type that no longer resolves, and the
//             build fails naming the record. Without this half the catalog rots quietly: every
//             assertion of arrow 1 stays green while the document describes last quarter's system.
//
// The whole reason the catalog is machine-readable at all is that this pair cannot be done by
// review. A reviewer sees the diff of a PR; neither arrow is visible in a diff — arrow 1 is an
// absence somewhere else in the tree, and arrow 2 is a line nobody touched.
//
// IT REUSES THE AUTHZ SWEEP, deliberately. AuthzSurfaceHost boots the real application with every
// module on and enumerates REST + Razor + MCP; AuthzSurface.Key is the key format the catalog's
// `surfaces` are written in. A second sweep of my own would be a second definition of "a surface",
// and the first thing it would do is disagree — which is how a catalog ends up complete against an
// inventory nobody enforces. See the header of AuthzSurfaces for why the sweep must run on a BOOTED
// host rather than over the source.
//
// GUARD THE GUARD: like the authz ratchet next door, almost every assertion here has the shape "this
// list is empty", and an empty catalog or an empty sweep would make all of them vacuously true.
// TheCatalogSweep_ActuallySeesSomething is what stops that.
[Collection("WebAppFactory")]
public sealed partial class ActionCatalogCompletenessTests : IClassFixture<AuthzSurfaceHost>
{
	readonly AuthzSurfaceHost _host;

	public ActionCatalogCompletenessTests(AuthzSurfaceHost host) => _host = host;

	// ── ARROW 1: EVERY LIVE SURFACE IS ACCOUNTED FOR ─────────────────────────────────────────────

	[Fact]
	public void EveryLiveSurface_IsInTheCatalog_OrIsExplicitlyUnmapped()
	{
		var unmapped = ActionCatalog.Unmapped.Select(u => u.Surface).ToHashSet(StringComparer.Ordinal);

		var orphans = _host.All
			.Where(s => !ActionCatalog.SurfaceToActions.ContainsKey(s.Key) && !unmapped.Contains(s.Key))
			.OrderBy(s => s.Key, StringComparer.Ordinal)
			.Select(s => $"  {s.Key}   [{s.Owner}]")
			.ToList();

		orphans.Should().BeEmpty(
			"every externally reachable surface either CARRIES a catalogued domain action or is written "
			+ "down as carrying none. A surface on neither list is a thing the product does that the "
			+ "catalog does not know about — and the catalog is what the target-kind declaration layer "
			+ "will be built from, so an unknown surface is an unprotected one. Add the surface key to the "
			+ "`surfaces` of the action it performs in src/PetBox.Core/Auth/action-catalog.json, or add an "
			+ "`unmapped` entry with one of the three reasons (`no-domain-action`, `ui-preference`, or — "
			+ "only if it really does perform a domain action nobody has catalogued yet — "
			+ "`not-covered-by-source-pass`, which also means raising the debt pin below, so prefer doing "
			+ "the work). The owner is named so there is somebody to ask. Uncatalogued:\n"
			+ string.Join("\n", orphans));
	}

	// ── ARROW 2: EVERYTHING THE CATALOG NAMES STILL EXISTS ───────────────────────────────────────

	[Fact]
	public void EveryCatalogSurface_IsLive()
	{
		var live = _host.All.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

		var dead = ActionCatalog.Actions
			.SelectMany(a => a.Surfaces.Select(surface => (surface, owner: $"action '{a.Id}'")))
			.Concat(ActionCatalog.Unmapped.Select(u => (surface: u.Surface, owner: $"unmapped ({Wire(u.Reason)})")))
			.Where(x => !live.Contains(x.surface))
			.OrderBy(x => x.surface, StringComparer.Ordinal)
			.Select(x => $"  {x.surface}   named by {x.owner}")
			.ToList();

		dead.Should().BeEmpty(
			"a catalog record names a surface that is not in the live sweep — it was renamed, moved or "
			+ "deleted and the catalog was not told. This half is what keeps the artifact from rotting: "
			+ "arrow 1 alone stays green forever over a document describing a system that no longer "
			+ "exists. Fix the key in src/PetBox.Core/Auth/action-catalog.json (the live keys are printed "
			+ "to .tmp/authz-surface-inventory.txt), or delete the record if the action is gone. Dead:\n"
			+ string.Join("\n", dead));
	}

	// Arrow 2 for the actions with NO external surface at all. `bg` (a background job invokes it) and
	// `internal` (an inside step of a synchronous flow) are semantically different kinds of invoker
	// and the catalog keeps them apart — but the check is the same for both: the type that executes
	// the action has to exist.
	//
	// WHY AN EXPLICIT ASSEMBLY LIST. The five type names live in FOUR different assemblies, and
	// Type.GetType("PetBox.Web.Auth.AgentKeyAdminService") returns null for every one of them that is
	// not in this test assembly or in corelib — a plain name with no assembly qualifier is only ever
	// resolved against those two. Silently null is the worst possible answer here: this test would
	// report every live service as missing (a permanent red nobody could fix), or — with a
	// `?? skip` — report a DELETED service as fine. So the assemblies are named by typeof() anchors,
	// which are checked by the COMPILER: a project that is renamed or dropped from the reference
	// graph breaks the build here instead of quietly shrinking the search.
	//
	// The anchors are deliberately types that are NOT themselves catalog targets, so that deleting a
	// catalogued service produces this test's failure message rather than a compile error — except
	// for PetBox.Dashboard, whose entire public surface is the one class HealthPoller. There the
	// anchor and the target necessarily coincide: deleting HealthPoller turns this file red at COMPILE
	// time instead of at assert time. Same direction, one step earlier, and the compiler error lands
	// on the line that explains what to do.
	static readonly Assembly[] CatalogAssemblies =
	[
		typeof(ActionCatalog).Assembly,   // PetBox.Core
		typeof(Program).Assembly,         // PetBox.Web
		typeof(HealthPoller).Assembly,    // PetBox.Dashboard
		typeof(DeployService).Assembly,   // PetBox.Deploy
	];

	[Fact]
	public void EveryTypeBackedAction_NamesALiveType()
	{
		var missing = ActionCatalog.Actions
			.SelectMany(a => a.Bg.Select(t => (action: a.Id, field: "bg", type: t))
				.Concat(a.Internal.Select(t => (action: a.Id, field: "internal", type: t))))
			.Where(x => Resolve(x.type) is null)
			.OrderBy(x => x.type, StringComparer.Ordinal)
			.Select(x => $"  {x.type}   named in {x.field} of action '{x.action}'")
			.ToList();

		missing.Should().BeEmpty(
			"an action with no external surface is carried by a TYPE, and that type must exist: it is the "
			+ "only evidence the action is still performed at all. Searched: "
			+ string.Join(", ", CatalogAssemblies.Select(a => a.GetName().Name))
			+ ". Rename the type in src/PetBox.Core/Auth/action-catalog.json, or delete the record if the "
			+ "action is gone (and if the type moved to an assembly not in that list, add a typeof() "
			+ "anchor for it above). Unresolved:\n" + string.Join("\n", missing));
	}

	static Type? Resolve(string fullName) => CatalogAssemblies
		.Select(a => a.GetType(fullName, throwOnError: false, ignoreCase: false))
		.FirstOrDefault(t => t is not null);

	// ── THE RECORDS THEMSELVES ───────────────────────────────────────────────────────────────────

	// A record with neither a surface nor a type is a sentence about the product with nothing
	// underneath it — nothing to check, nothing to break when the code changes, and therefore
	// nothing this ratchet can hold. Both arrows above simply skip it.
	[Fact]
	public void EveryAction_HasASurfaceOrAType()
	{
		var floating = ActionCatalog.Actions
			.Where(a => a.Surfaces.Count == 0 && a.Bg.Count == 0 && a.Internal.Count == 0)
			.Select(a => $"  {a.Id} — {a.Action}")
			.ToList();

		floating.Should().BeEmpty(
			"every action is reachable through at least one surface, or executed by a named background "
			+ "or internal type. A record with none of the three cannot be checked against anything and "
			+ "would sit in the catalog forever, true or false:\n" + string.Join("\n", floating));
	}

	// `^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)+$` — the id grammar of .tmp/SCHEMA.md: ASCII
	// lower-case segments separated by dots, hyphens inside a segment, at least two segments.
	[GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)+$")]
	private static partial Regex ActionIdRegex();

	[Fact]
	public void ActionIds_AreUniqueAndWellFormed()
	{
		var ids = ActionCatalog.Actions.Select(a => a.Id).ToList();

		ids.Should().OnlyHaveUniqueItems(
			"an action id is an IDENTITY — the declaration layer will resolve a target kind by it, and "
			+ "two records under one id means the answer depends on which one is read first");

		var malformed = ActionCatalog.Actions
			.Where(a => !ActionIdRegex().IsMatch(a.Id))
			.Select(a => $"  {a.Id}")
			.ToList();

		malformed.Should().BeEmpty(
			"an id is dotted lower-case ASCII segments with hyphens inside a segment (.tmp/SCHEMA.md). "
			+ "It is a machine key, not a title:\n" + string.Join("\n", malformed));

		// The first segment IS the subdomain — that is what makes the id readable as an address rather
		// than an opaque string, and what lets a whole family be selected by prefix.
		var mismatched = ActionCatalog.Actions
			.Where(a => !string.Equals(a.Id.Split('.')[0], a.Subdomain, StringComparison.Ordinal))
			.Select(a => $"  {a.Id} — subdomain '{a.Subdomain}'")
			.ToList();

		mismatched.Should().BeEmpty(
			"the first segment of an id must be its subdomain:\n" + string.Join("\n", mismatched));
	}

	// ── THE DEBT PIN ─────────────────────────────────────────────────────────────────────────────

	// The number of live surfaces that DO perform a domain action which the transfer pass never
	// reached. It is not a budget: it is the same mechanism as the authz allowlist —
	// StaleAllowlistEntries_AreDeleted over there, this constant over here — a debt made countable so
	// it cannot grow back while nobody is looking.
	//
	// IT IS ZERO, AND THAT IS THE FINISHED STATE — the same shape TenantEnforcementAllowlist.Keys is
	// in next door, and for the same reason: a ceiling of zero is what makes "only shrinks" mean
	// "cannot come back". It STARTED at 31 (2 MCP tools plus 29 Razor pages, the transfer pass having
	// been MCP/REST-centric), and work `action-catalog-debt-and-drift` paid all 31 off in one commit:
	// 30 were catalogued against a read of their PageModel/tool, and page:/Admin/ProjectLogSettings —
	// a bare 302 to the settings page — was reclassified `no-domain-action`, which is a statement
	// about the surface and not a line in the backlog.
	//
	// The name is a CEILING, not a historical marker, precisely because it may no longer stand at the
	// transfer's number: the assertions below quote it, so a constant claiming to be "the count at the
	// transfer" while reading 0 would put a false sentence in a failure message.
	//
	// LOWER IT WHEN YOU CATALOGUE ONE. Raising it is not a fix; it is a decision to ship an
	// uncatalogued domain action, and it needs the owner, not a PR.
	const int UncataloguedDebtCeiling = 0;

	[Fact]
	public void UncataloguedDebt_OnlyShrinks()
	{
		var debt = ActionCatalog.Unmapped
			.Where(u => u.Reason == UnmappedReason.NotCoveredBySourcePass)
			.OrderBy(u => u.Surface, StringComparer.Ordinal)
			.ToList();

		debt.Count.Should().BeLessThanOrEqualTo(UncataloguedDebtCeiling,
			$"`not-covered-by-source-pass` is DEBT and only ever shrinks (the ceiling stands at "
			+ $"{UncataloguedDebtCeiling}; it was 31 at the transfer). A new surface does not go on this "
			+ "list — it gets catalogued, or it is `no-domain-action` / `ui-preference`, which are "
			+ "statements about the surface rather than about the backlog. Current:\n  "
			+ string.Join("\n  ", debt.Select(u => u.Surface)));

		// And when it does shrink, the pin comes down with it — otherwise the guard slowly loosens
		// into a budget with room in it, which is how a paid-off debt gets re-borrowed.
		debt.Count.Should().Be(UncataloguedDebtCeiling,
			"the debt shrank — lower UncataloguedDebtCeiling to " + debt.Count
			+ " in the same commit, so the ratchet holds at the new level instead of leaving slack");
	}

	// ── THE MACHINE INVENTORY ────────────────────────────────────────────────────────────────────

	// Same rule as TheInventory_IsMachineReadable next door: a number nobody can reproduce by running
	// something is a human count, and human counts of this surface have been wrong by 30. So the
	// catalog's breakdown is EMITTED by a run and never written into a comment.
	[Fact]
	public void TheCatalog_IsMachineReadable()
	{
		var live = _host.All.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
		var claimed = ActionCatalog.SurfaceToActions.Count;

		var lines = new List<string>
		{
			$"# PetBox domain action catalog — {ActionCatalog.Actions.Count} actions",
			"#",
			$"#   live surfaces:        {live.Count}",
			$"#   claimed by an action: {claimed}",
			$"#   unmapped:             {ActionCatalog.Unmapped.Count}",
			"#",
			"# ── by subdomain ─────────────────────────────────────────────",
		};

		lines.AddRange(ActionCatalog.Actions
			.GroupBy(a => a.Subdomain, StringComparer.Ordinal)
			.OrderBy(g => g.Key, StringComparer.Ordinal)
			.Select(g => $"#   {g.Key,-16} {g.Count()}"));

		lines.Add("#");
		lines.Add("# ── by target kind ───────────────────────────────────────────");
		lines.AddRange(ActionCatalog.Actions
			.GroupBy(a => a.Target)
			.OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
			.Select(g => $"#   {Wire(g.Key),-22} {g.Count()}"));

		lines.Add("#");
		lines.Add("# ── unmapped, by reason ──────────────────────────────────────");
		// Every reason is printed, including the ones at zero: a reason that vanishes from the report
		// because nothing uses it looks the same as a reason nobody thought about.
		lines.AddRange(Enum.GetValues<UnmappedReason>()
			.Select(r => $"#   {Wire(r),-26} {ActionCatalog.Unmapped.Count(u => u.Reason == r)}"));

		lines.Add("#");
		lines.AddRange(ActionCatalog.Actions
			.OrderBy(a => a.Id, StringComparer.Ordinal)
			.Select(a => $"{a.Id}\t{Wire(a.Target)}\t{Carriers(a)}\t{a.Action}"));
		lines.Add("#");
		lines.AddRange(ActionCatalog.Unmapped
			.OrderBy(u => u.Surface, StringComparer.Ordinal)
			.Select(u => $"{u.Surface}\tUNMAPPED: {Wire(u.Reason)}"));

		var rendered = string.Join(Environment.NewLine, lines) + Environment.NewLine;

		var dir = Path.Combine(RepoRoot(), ".tmp");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "action-catalog-inventory.txt"), rendered);

		rendered.Split('\n').Length.Should()
			.BeGreaterThan(ActionCatalog.Actions.Count + ActionCatalog.Unmapped.Count,
				"the rendered catalog carries a line per action and per unmapped surface, plus its header");
	}

	static string Carriers(DomainAction a) =>
		string.Join(" ", a.Surfaces.Concat(a.Bg.Select(t => "bg:" + t)).Concat(a.Internal.Select(t => "internal:" + t)));

	// The kebab-case-lower spelling the JSON uses, so the report and the artifact read the same. It
	// calls the very policy the reader is configured with rather than re-implementing the casing —
	// a second spelling rule is how a report starts naming values that do not exist in the file.
	static string Wire<TEnum>(TEnum value) where TEnum : struct, Enum =>
		JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString()!);

	// ── GUARD THE GUARD ──────────────────────────────────────────────────────────────────────────

	// Everything above is "this list is empty". An empty catalog, or a sweep that saw nothing, makes
	// every one of them pass while protecting nothing — the same failure mode
	// AuthzDeclarationRatchetTests.TheSweep_ActuallySeesTheSurface exists for, and it is not
	// hypothetical: the sweep depends on a host booting all of its modules.
	[Fact]
	public void TheCatalogSweep_ActuallySeesSomething()
	{
		ActionCatalog.Actions.Should().HaveCountGreaterThan(50,
			"the catalog is the whole product's action list, not a sample");
		_host.All.Should().HaveCountGreaterThan(150,
			"the live sweep is REST + Razor + MCP with every module on — if it collapsed to a handful, "
			+ "arrow 1 above would be green over a system nobody looked at");

		// The catalog must actually MEET the sweep. Both lists being long proves nothing on its own:
		// a catalog written in a different key format would be long, live and wholly disjoint, and
		// arrow 1 would then be red — but a catalog of surfaces that ALL fell into `unmapped` would be
		// long, live and green while claiming the product does nothing.
		var live = _host.All.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
		var claimed = live.Count(k => ActionCatalog.SurfaceToActions.ContainsKey(k));

		claimed.Should().BeGreaterThan(live.Count / 2,
			$"most live surfaces carry a catalogued action ({claimed} of {live.Count} do). If this ever "
			+ "dipped, the catalog would be passing arrow 1 by declaring the product's surface unmapped "
			+ "rather than by describing it");

		// And the type-backed half is real too: EveryTypeBackedAction_NamesALiveType is also an "is
		// empty", and it would be vacuous if no record named a type at all.
		ActionCatalog.Actions.SelectMany(a => a.Bg.Concat(a.Internal)).Should().NotBeEmpty(
			"some actions have no external surface and are carried by a type — that check must have "
			+ "something to check");
	}

	// The repo root — where the machine inventory is written (.tmp/, gitignored). Same walk-up as
	// AuthzDeclarationRatchetTests uses; its copy is private to that class.
	static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, "src", "PetBox.Web"))) return dir;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("repo root (with src/PetBox.Web) not found walking up from the test bin.");
	}
}

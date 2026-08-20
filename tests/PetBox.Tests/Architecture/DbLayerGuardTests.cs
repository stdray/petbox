using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Deploy.Data;

namespace PetBox.Tests.Architecture;

// THE LAYER GUARD for AGENTS.md § "Database connections — a hard invariant":
//
//     "The database is visible only in the service layer. [...] a Razor PageModel, a page filter,
//      middleware, an IClaimsTransformation or an endpoint lambda asks a service, it does not call
//      .Open() itself."
//
// Work item: `db-out-of-pages-into-services` (work board). Until this file existed, that sentence
// was PROSE — DbInjectionGuardTests enforces something adjacent but different (that a DataConnection
// is never INJECTABLE at all), and it is perfectly happy with a PageModel that takes an
// ICoreDbFactory and opens core.db in OnGetAsync. That is exactly the pile the work item is paying
// off, and nothing stopped it growing back: the type-level guard is green either way.
//
// WHAT THIS ADDS: the FACTORY may not reach the presentation layer. Not the connection — the
// factory. A page that cannot obtain a factory cannot open a connection, so "ask a service" stops
// being a convention a new page can forget and becomes the only thing that compiles.
//
// WHICH TYPES ARE PRESENTATION IS NOT DECIDED HERE. It is declared once, in `LayerClassification`,
// and this gate READS that declaration (see `Presentation` below, now a one-line delegation). It used
// to decide for itself, and the cost was work `configtools-gate-classification`: this file swept the
// whole `PetBox.Web.Mcp` namespace into presentation while `ConfigBoundaryTests` recorded in prose
// that `ConfigTools` is service code — one type, two opposite answers, both gates green. The
// classification now lives in one place because the two gates could not be trusted to agree on it
// separately; `LayerClassificationTests` is what fails when a second definition reappears.
//
// WHY REFLECTION AND NOT A TEXT SCAN. A text scan over Pages/** would be simpler and STRICTLY
// WRONG here, in both directions:
//   - False positives it cannot avoid: Program.cs says `AddSingleton<ICoreDbFactory>(...)` and
//     `GetRequiredService<ICoreDbFactory>()` — the composition root's whole job. Those are generic
//     type ARGUMENTS, invisible to reflection over ctor/field/parameter types, which is precisely
//     the discrimination we want: wiring a factory into a service is legal, holding one in a page
//     is not.
//   - False negatives it cannot avoid: a minimal-API handler takes its factory as a LAMBDA
//     PARAMETER (`app.MapGet("/x", (ICoreDbFactory f) => ...)`). That is not a ctor and not a field
//     — but the C# compiler lowers the lambda to a method (and its captures to fields of a
//     `<>c__DisplayClass`) ON THE ENDPOINT CLASS, so sweeping methods + fields of every type nested
//     under a presentation type sees it. That is why `MembersOf` walks methods and fields, not just
//     constructors, and why `Outermost` walks the DeclaringType chain.
//
// WHY NOT NetArchTest, which most guards in this folder use: it reasons about type DEPENDENCIES.
// A class that merely *mentions* ICoreDbFactory in a `GetRequiredService<>` call depends on it
// exactly as much as one that stores it in a field, so NetArchTest cannot tell the composition root
// from a leaking page. Same reason DbInjectionGuardTests composes DI by hand instead.
public sealed class DbLayerGuardTests
{
	// The doors onto every database in the system — INFERRED from shape, not enumerated by name.
	//
	// Work `arch-gates-scope-declared-not-inferred`: this used to be a hand-listed
	// `Type[] { ICoreDbFactory, IDeployDbFactory, IScopedDbFactory<> }`, and `IConfigDbFactory` —
	// a thin typed facade over the guarded `IScopedDbFactory<ConfigDb>` — was invisible to it simply
	// because nobody added a fourth line. Any future `IFooDbFactory` facade would have repeated the
	// same miss: the list only knows what its author remembered to write down.
	//
	// THE SHAPE, INSTEAD: every guarded factory's job is handing back a live database connection —
	// `ICoreDbFactory.Open(): PetBoxDb`, `IDeployDbFactory.Open(): DeployDb`,
	// `IScopedDbFactory<TContext>.GetDb()/NewEnsuredConnection(): TContext`,
	// `IConfigDbFactory.GetConfigDb()/NewConfigDb(): ConfigDb` — and `PetBoxDb`/`DeployDb`/`TContext`/
	// `ConfigDb` are all `LinqToDB.Data.DataConnection`. That is the trait a hand-list can't grow on
	// its own: a new `IFooDbFactory` is a door the instant handing back a connection is what it does,
	// with no line to remember here.
	//
	// WHY "WHAT IT DOES" MEANS A STRICT MAJORITY OF ITS OWN METHODS, NOT "ANY OF THEM". A type is a
	// door when handing back a connection is its PRIMARY job — more than half of the (non-accessor)
	// methods it declares do that. This threshold is not decoration; it is what separates a factory
	// from a STORE that also happens to expose one raw-connection escape hatch alongside a domain
	// CRUD surface, and the difference is load-bearing:
	//
	//   - `IConfigDbFactory`: GetConfigDb/NewConfigDb, 2 of 2 methods return ConfigDb (100%) — a door.
	//   - `IScopedDbFactory<>`: GetDb/NewEnsuredConnection return TContext, EvictAsync does not — 2 of
	//     3 (67%) — still a door (its EvictAsync is bookkeeping on the SAME set of connections).
	//   - `ILogStore` (PetBox.Log.Core.Data): GetContext/NewEnsuredContext return LogDb, but
	//     ExistsAsync/ListAsync/CreateAsync/DeleteAsync/UpdateRetentionDaysAsync do not — 2 of 7
	//     (29%) — NOT a door: its majority is domain CRUD over log metadata.
	//   - `ISessionStore` (PetBox.Sessions.Data): GetContext returns SessionsDb; ListAsync/
	//     ListPageAsync/GetAsync/GetCreatedAsync/ResolveIdAsync/UpsertAsync/DeleteAsync do not — 1 of
	//     8 (12.5%) — NOT a door, same reason.
	//   - `IProjectDirectory`: 0 of 9 methods return a `DataConnection` at all — NOT a door (it is the
	//     sanctioned service the dozen `Pages/Admin/*.cshtml.cs` models are SUPPOSED to hold).
	//
	// The first version of this inference used "ANY method returns a DataConnection", not "a
	// majority" — it was run against this tree (not just reasoned about) and it lit up
	// `NoPresentationType_TakesADbFactory` on `ILogStore`/`ISessionStore` too: real methods on real
	// interfaces really do hand back a raw connection, and `LogApi`/`SessionModel`/`SessionsModel`/
	// `ShareApi`/`OtlpEndpoints` really do hold those stores. Whether that raw-connection escape hatch
	// on an otherwise-domain store is itself worth closing is a SEPARATE, real question (see the
	// accumulator note filed alongside this work) — it is not what this task's target
	// (`IConfigDbFactory`) needed, and landing it here would have turned five unrelated, currently-
	// green files red. The majority threshold is the line that catches the one facade this task named
	// without also re-opening that separate question by accident.
	//
	// WHY THE SHAPE IS RETURN-TYPE, NOT "WRAPS A GUARDED FACTORY IN ITS CONSTRUCTOR". The other
	// obvious alternative — close the set transitively over "an implementation that HOLDS a guarded
	// factory" — was tried against this tree too and rejected on its own, independent grounds:
	// `ProjectDirectory` takes `ICoreDbFactory` in its own constructor, so that closure would have
	// made `IProjectDirectory` itself guarded regardless of any threshold — the same false violation
	// as above, for a type with ZERO connection-returning methods of its own.
	//
	// `internal` so a proof test can compare this inference against the old enumeration form on a
	// fixture type — see DbLayerGuardInferenceTests.
	internal static bool IsGuarded(Type t)
	{
		var methods = t.GetMethods(AllMembers).Where(m => !m.IsSpecialName).ToArray();
		if (methods.Length == 0) return false;

		var doors = methods.Count(m => typeof(DataConnection).IsAssignableFrom(m.ReturnType));
		return doors * 2 > methods.Length;
	}

	// ── THE ALLOWLIST — AND IT IS EMPTY ───────────────────────────────────────────────────────────
	//
	// It used to hold 30 entries: every page, endpoint and middleware that predated the rule and still
	// opened a database itself. `db-out-of-pages-into-services` drained fourteen of them (the doors
	// already existed), and `db-out-of-pages-remaining-24` wrote the doors the other sixteen were
	// waiting for — the rollup/counts service, the credential and self-service account doors, a service
	// layer for Config and for Logging's SavedQueries, an owner for ShareLinks, and the name rules that
	// DataDbsApi kept to itself lifted into IDataDbCatalog.
	//
	// EMPTY IS THE POINT, and it is why the two tests below now read as they do: there is no
	// presentation type left that holds a db factory, so `NoPresentationType_TakesADbFactory` asserts
	// the rule with no exceptions, and `AllowlistEntries_AreStillNeeded` has nothing left to keep
	// honest. Do not read the emptiness as "this guard checks nothing" — read
	// TheGuard_ActuallyInspectsSomething, which exists to prove the sweep still sees the code.
	//
	// NEVER add an entry to make a new violation pass. That was true when the list was long and it is
	// truer now: an entry here would be the first one in the file's history to mark debt that was
	// CREATED rather than inherited. New presentation code asks a service; if the service does not
	// exist, the work is to open the door.
	static readonly IReadOnlyDictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.Ordinal)
	{
	};

	// Every PetBox product assembly, anchored on Web (the composition root references them all) —
	// the same sweep DbInjectionGuardTests uses.
	internal static readonly Assembly[] ProductAssemblies = LoadProductAssemblies();

	static Assembly[] LoadProductAssemblies()
	{
		var web = typeof(Program).Assembly;
		return web.GetReferencedAssemblies()
			.Where(n => n.Name?.StartsWith("PetBox.", StringComparison.Ordinal) == true)
			.Select(Assembly.Load)
			.Append(web)
			.DistinctBy(a => a.FullName)
			.ToArray();
	}

	// AGENTS.md's categories, as reflectable signatures — DECLARED IN ONE PLACE and read here.
	// Returns the category name (used in the failure message) or null when the type is not presentation.
	//
	// The composition root, the four HTTP-side categories and the MCP split (transport pipeline =
	// presentation, tool class = its module's service layer) all live in `LayerClassification.Rules`.
	// Do not re-add a local rule here: the whole point of the delegation is that a second definition
	// of "presentation" cannot exist without `LayerClassificationTests` going red.
	// `internal` so LayerClassificationTests can compare THIS gate's own entry point against the
	// other gate's, rather than comparing two copies of the same idea.
	internal static string? Presentation(Type t) => LayerClassification.PresentationCategory(t);

	const BindingFlags AllMembers =
		BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

	static Type Outermost(Type t) => LayerClassification.Outermost(t);

	// Every way a factory can come to REST inside a type: taken in a constructor, stored in a field
	// (including a lambda's captured local, which the compiler turns into exactly that), or accepted
	// as a method parameter (including a minimal-API handler's, which the compiler turns into exactly
	// that). Properties are covered too — an auto-property IS a field.
	// `internal` so a proof test can point it at a fixture type and show the inference actually fires
	// (see DbLayerGuardInferenceTests) — same reason `Presentation` above is internal.
	internal static IEnumerable<string> GuardedMembersOf(Type t) =>
		t.GetConstructors(AllMembers)
			.SelectMany(c => c.GetParameters())
			.Where(p => IsGuarded(p.ParameterType))
			.Select(p => $".ctor({Pretty(p.ParameterType)} {p.Name})")
		.Concat(t.GetFields(AllMembers)
			.Where(f => IsGuarded(f.FieldType))
			.Select(f => $"field {Pretty(f.FieldType)} {f.Name}"))
		.Concat(t.GetMethods(AllMembers)
			.SelectMany(m => m.GetParameters().Select(p => (m, p)))
			.Where(x => IsGuarded(x.p.ParameterType))
			.Select(x => $"{x.m.Name}({Pretty(x.p.ParameterType)} {x.p.Name})"));

	static string Pretty(Type t) =>
		t.IsGenericType ? $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(", ", t.GetGenericArguments().Select(a => a.Name))}>" : t.Name;

	// Every leaking presentation type, keyed by the OUTERMOST type (so a violation hiding in a
	// compiler-generated closure is reported against the class that wrote the lambda).
	// An ASYNC STATE MACHINE's fields are not a stable observation surface, and trusting them cost this
	// guard a false green on its first Verify run. In DEBUG the compiler hoists EVERY local of an async
	// method into a state-machine field (so the debugger can show them); in RELEASE it hoists only the
	// locals that live across an `await`. So `var f = ctx.RequestServices.GetRequiredService<ICoreDbFactory>()`
	// inside an async method appears as a field in Debug and VANISHES in Release — a guard that reads it
	// is red in one configuration and green in the other, for reasons that have nothing to do with the
	// violation. State machines are therefore skipped entirely, and the service-locator escape they were
	// accidentally catching is closed properly, in the source plane, by
	// NoCodeOutsideTheCompositionRoot_ResolvesAFactoryFromTheContainer below.
	//
	// Closure display classes (`<>c__DisplayClass`) are NOT skipped: a captured variable lives as long as
	// the lambda does, so it is a real field in both configurations. That is what keeps a minimal-API
	// handler's factory visible.
	static bool IsAsyncStateMachine(Type t) => typeof(IAsyncStateMachine).IsAssignableFrom(t);

	static Dictionary<string, (string Category, List<string> Members)> Offenders()
	{
		var found = new Dictionary<string, (string Category, List<string> Members)>(StringComparer.Ordinal);

		foreach (var type in ProductAssemblies.SelectMany(SafeGetTypes))
		{
			if (IsAsyncStateMachine(type)) continue;

			var owner = Outermost(type);
			if (Presentation(owner) is not { } category) continue;

			var members = GuardedMembersOf(type).ToList();
			if (members.Count == 0) continue;

			var name = owner.FullName!;
			if (!found.TryGetValue(name, out var entry)) found[name] = entry = (category, []);
			entry.Members.AddRange(members);
		}

		return found;
	}

	[Fact]
	public void NoPresentationType_TakesADbFactory()
	{
		var offenders = Offenders()
			.Where(o => !Allowlist.ContainsKey(o.Key))
			.OrderBy(o => o.Key, StringComparer.Ordinal)
			.Select(o => $"  {o.Key} [{o.Value.Category}] -> {string.Join("; ", o.Value.Members.Distinct())}")
			.ToList();

		offenders.Should().BeEmpty(
			"the database is visible only in the SERVICE layer (AGENTS.md, 'Database connections — a hard "
			+ "invariant'; work `db-out-of-pages-into-services'). A Razor PageModel, a page filter, "
			+ "middleware, an IClaimsTransformation, a minimal-API endpoint or an MCP tool ASKS A SERVICE — "
			+ "it does not hold a db factory and does not call .Open() itself. Two reasons, and the second "
			+ "is the one that bites: a rule that lives in ten pages is a rule the eleventh forgets (that is "
			+ "how ten copies of the workspace-ownership check drifted into an IDOR), and nothing over "
			+ "core.db can ever be cached while its readers are scattered across pages. Take the service in "
			+ "the ctor instead; if the service does not exist yet, OPEN THE DOOR — do not add a line to "
			+ "this test's Allowlist. Offenders:\n" + string.Join("\n", offenders));
	}

	// The allowlist may only SHRINK. An entry whose type no longer holds a factory is a page somebody
	// ALREADY converted — leaving the line behind silently re-grants the exemption to whoever edits
	// that page next, and hides the fact that the work was done. Fail, so the line gets deleted.
	[Fact]
	public void AllowlistEntries_AreStillNeeded()
	{
		var offenders = Offenders();

		var stale = Allowlist.Keys
			.Where(name => !offenders.ContainsKey(name))
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToList();

		stale.Should().BeEmpty(
			"these types are on this test's Allowlist but no longer take a db factory — either they were "
			+ "converted to a service (delete the line: the allowlist only ever shrinks, and a stale entry "
			+ "hides work that is already done) or the type was renamed/removed (delete the line, it now "
			+ "protects nothing). Stale entries:\n  " + string.Join("\n  ", stale));
	}

	// ── THE SERVICE-LOCATOR PLANE ────────────────────────────────────────────────────────────────
	//
	// Everything above reasons about TYPES: what a class takes, holds, or accepts. There is one way to
	// get a factory that leaves no trace in any of those — a local, pulled from the container mid-method:
	//
	//     var factory = context.RequestServices.GetRequiredService<ICoreDbFactory>();
	//
	// No ctor parameter, no declared field, no method parameter — a local, pulled out of the container
	// mid-method. Reflection cannot see it (see IsAsyncStateMachine for the Debug/Release trap that
	// made this look catchable when it is not), so it is caught HERE, in the source.
	//
	// A WARNING PAID FOR IN A FALSE GREEN: this plane is a TEXT scan, so it matches the pattern
	// wherever it appears — including inside a COMMENT. A converted file whose comment quoted the call
	// it no longer makes ("this used to say GetRequiredService<ICoreDbFactory>()") kept matching, and
	// ServiceLocatorAllowlistEntries_AreStillNeeded stayed green over work that was already finished.
	// Describe the old call, do not spell it.
	//
	// The rule this scan enforces is broader than the presentation layer, and deliberately so: NOTHING
	// outside the composition root resolves a db factory from the container. A SERVICE takes its factory
	// in the constructor — that is what makes its dependencies visible and its lifetime checkable
	// (CaptiveDependencyTests). Reaching into the container mid-method hides the dependency from every
	// tool we have, including the two tests above. Program.cs is the sole exception: BUILDING the graph
	// is its entire job.
	//
	// `(?:[\w.]*\.)?` is not decoration: the generic argument may be written FULLY QUALIFIED
	// (`GetRequiredService<PetBox.Core.Data.ICoreDbFactory>()`), and the first draft of this pattern —
	// which demanded the bare name — let exactly that through. It was caught by seeding the violation
	// and watching the guard stay green, which is the only way this kind of hole is ever found.
	static readonly Regex ServiceLocatorPattern = new(
		@"Get(Required)?Service<\s*(?:[\w.]*\.)?(I(Core|Deploy)DbFactory\s*>|IScopedDbFactory\s*<)",
		RegexOptions.Compiled);

	// The composition root — the one file allowed to pull factories out of the container.
	const string CompositionRootFile = "Program.cs";

	// ALLOWLIST — EMPTY, same contract as the type allowlist above.
	//
	// Its one entry was ApiKeyAuthMiddleware, and the entry told a story that turned out to be fiction:
	// "the hottest core.db reader in the app — convert it WITH A MEASUREMENT, not on principle". The file
	// was never registered, in the entire history of the repository. It read the X-Api-Key header itself
	// and knew nothing of expiry, sandboxOnly, or claims; the auth SCHEME that superseded it 96 minutes
	// after it was written is what every real request has always gone through. So its per-request cost
	// was not "hot", it was ZERO, and the caution in this allowlist was guarding code that never ran.
	// It is deleted (intake `apikey-auth-middleware-is-dead-code`).
	//
	// The lesson worth keeping: this allowlist's comments are the only place some of these claims are
	// written down, and nothing checks them. Measure before you repeat one.
	static readonly IReadOnlyDictionary<string, string> ServiceLocatorAllowlist =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
		};

	static string SrcDir()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src");
			if (Directory.Exists(Path.Combine(candidate, "PetBox.Web"))) return candidate;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("src/ (with PetBox.Web) not found walking up from the test bin.");
	}

	// Every product .cs file, minus build artifacts (bin/obj hold generated copies that would be scanned
	// twice and would resurrect deleted code).
	static IReadOnlyList<string> ProductSourceFiles() =>
		[.. Directory.EnumerateFiles(SrcDir(), "*.cs", SearchOption.AllDirectories)
			.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				&& !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

	static IReadOnlyList<string> ServiceLocatorOffenders() =>
		[.. ProductSourceFiles()
			.Where(p => !string.Equals(Path.GetFileName(p), CompositionRootFile, StringComparison.OrdinalIgnoreCase))
			.Where(p => ServiceLocatorPattern.IsMatch(File.ReadAllText(p)))
			.Select(p => Path.GetFileName(p)!)
			.Order(StringComparer.OrdinalIgnoreCase)];

	[Fact]
	public void NoCodeOutsideTheCompositionRoot_ResolvesAFactoryFromTheContainer()
	{
		var offenders = ServiceLocatorOffenders()
			.Where(f => !ServiceLocatorAllowlist.ContainsKey(f))
			.ToList();

		offenders.Should().BeEmpty(
			"a db factory is TAKEN IN A CONSTRUCTOR, not fished out of the container mid-method "
			+ "(AGENTS.md, 'the database is visible only in the service layer'; work "
			+ "`db-out-of-pages-into-services'). GetRequiredService<ICoreDbFactory>() hides the dependency "
			+ "from every tool we have — it is invisible to the ctor/field/parameter sweep in this same "
			+ "file, and to CaptiveDependencyTests' lifetime check. Inject the SERVICE you actually need "
			+ "instead. Program.cs is the only file exempt: building the graph is its job. Offenders: "
			+ string.Join(", ", offenders));
	}

	[Fact]
	public void ServiceLocatorAllowlistEntries_AreStillNeeded()
	{
		var offenders = ServiceLocatorOffenders().ToHashSet(StringComparer.OrdinalIgnoreCase);

		var stale = ServiceLocatorAllowlist.Keys.Where(f => !offenders.Contains(f)).Order().ToList();

		stale.Should().BeEmpty(
			"these files no longer resolve a db factory from the container — delete the entry (this "
			+ "allowlist only ever shrinks; a stale line hides work that is already done). Stale: "
			+ string.Join(", ", stale));
	}

	// Guard-the-guard. Every assertion above is an "is empty" — if the sweep or the classifier ever
	// silently matched NOTHING (a moved namespace, an assembly that failed to load, a renamed base
	// type), both would pass by vacuity and this file would protect exactly nothing while looking
	// green. That has happened here before, so it is tested rather than assumed.
	[Fact]
	public void TheGuard_ActuallyInspectsSomething()
	{
		ProductAssemblies.Should().HaveCountGreaterThan(5, "the sweep must cover the product assemblies");

		var byCategory = ProductAssemblies
			.SelectMany(SafeGetTypes)
			.Where(t => !t.IsNested)
			.Select(t => (Type: t, Category: Presentation(t)))
			.Where(x => x.Category is not null)
			.GroupBy(x => x.Category!)
			.ToDictionary(g => g.Key, g => g.Select(x => x.Type).ToList());

		// All four of AGENTS.md's categories must actually be FOUND — not merely defined.
		byCategory.Should().ContainKey("Razor PageModel").WhoseValue.Should()
			.HaveCountGreaterThan(25, "Pages/** is the pile this work item exists to drain");
		byCategory.Should().ContainKey("middleware");
		byCategory.Should().ContainKey("minimal-API endpoint class");
		byCategory.Should().ContainKey("MCP pipeline stage").WhoseValue.Should()
			.HaveCountGreaterThan(5, "the MCP transport pipeline (Mcp/Mcp*Filter.cs) wraps every request "
				+ "the way middleware wraps an HTTP one and is swept as presentation");

		// AND THE HALF THAT IS DELIBERATELY NOT SWEPT. There used to be an "MCP tools" category here
		// covering the whole namespace; it is gone by DECISION (work `configtools-gate-classification`),
		// not by the classifier quietly failing to match. Assert the decision, so that "no MCP tool is
		// presentation" cannot become true by accident — a broken attribute check would show up here as
		// a missing rule name rather than as a silently emptied category.
		byCategory.Should().NotContainKey("MCP tools",
			"the namespace is no longer swept wholesale — the pipeline and the tool classes are "
			+ "classified separately, by shape");
		LayerClassification.Decide(typeof(PetBox.Web.Mcp.ConfigTools)).Should().NotBeNull()
			.And.Subject.As<LayerRule>().Should().Match<LayerRule>(
				r => r.Name == "MCP module adapter" && r.Layer == Layer.Service,
				"an MCP tool class is its module's service layer BY A DECLARED RULE, not by falling "
				+ "through every presentation rule unmatched");

		// And the specific shapes that are easy to break silently.
		byCategory["Razor PageModel"].Should().Contain(typeof(PetBox.Web.Pages.Admin.ProjectDetailModel),
			"the page this wave converted must still be CLASSIFIED (it is now clean, not unseen)");
		// Conventional middleware implements no interface, so the RequestDelegate-ctor rule is the only
		// thing that catches it — and this anchor is what proves that rule still fires. It used to point
		// at ApiKeyAuthMiddleware, which was DELETED as dead code (never registered, in the whole history
		// of the repo; see intake `apikey-auth-middleware-is-dead-code`). That is the inversion this line
		// now avoids: an anchor pinned to unused code is a test keeping a corpse warm, and it is why the
		// corpse survived two refactors. KeyUsageStampMiddleware is pinned instead because it is LIVE —
		// Program.cs registers it, a real feature (key last-used stamping) needs it, so it cannot quietly
		// become dead the way its predecessor did.
		byCategory["middleware"].Should().Contain(typeof(PetBox.Core.Auth.KeyUsageStampMiddleware),
			"conventional middleware implements no interface — the RequestDelegate-ctor rule must catch it");
		byCategory["minimal-API endpoint class"].Should().Contain(typeof(PetBox.Data.DataDbsApi),
			"a static *Api class maps endpoints via IEndpointRouteBuilder and must be swept");

		// The guarded set really is the set of db doors — i.e. this guard points at what it claims to.
		// No enumerated list to assert a count against any more (work
		// `arch-gates-scope-declared-not-inferred`): the inference is a shape predicate, so what is
		// worth pinning is which real types it does and does not call doors.
		IsGuarded(typeof(ICoreDbFactory)).Should().BeTrue("Open(): PetBoxDb is the door's own shape");
		IsGuarded(typeof(IDeployDbFactory)).Should().BeTrue("Open(): DeployDb is the door's own shape");
		IsGuarded(typeof(IScopedDbFactory<PetBox.Tasks.Data.TasksDb>)).Should()
			.BeTrue("GetDb()/NewEnsuredConnection(): TasksDb — closed generics are covered directly, "
				+ "with no open-generic special case needed any more");
		IsGuarded(typeof(PetBox.Config.Data.IConfigDbFactory)).Should().BeTrue(
			"THE FIX: a typed facade over IScopedDbFactory<ConfigDb> is a door by ITS OWN shape "
			+ "(GetConfigDb()/NewConfigDb(): ConfigDb) — the enumerated form this guard used to have "
			+ "never named it and never saw it");
		IsGuarded(typeof(PetBox.Web.Auth.IProjectDirectory)).Should().BeFalse(
			"NOT a door: IProjectDirectory's own implementation holds ICoreDbFactory in its ctor, but "
			+ "its members return Project/bool/domain results, never a DataConnection — a closure over "
			+ "'wraps a guarded factory' would have made every one of the Pages/Admin/* models that "
			+ "legitimately ask this service a false violation, which is exactly the pattern this guard "
			+ "exists to REQUIRE, not forbid");
		// Pinned so the majority threshold cannot silently widen back to "any method returns a
		// DataConnection": that form was tried, run against this tree, and it lit up real violations
		// on ILogStore/ISessionStore's real callers (LogApi, SessionModel, SessionsModel, ShareApi,
		// OtlpEndpoints) — a different, real question this task did not ask, see DbLayerGuardTests'
		// GuardedFactories comment and the accumulator note filed alongside this work.
		IsGuarded(typeof(PetBox.Log.Core.Data.ILogStore)).Should().BeFalse(
			"GetContext/NewEnsuredContext return LogDb, but 5 of its other 7 methods are domain CRUD "
			+ "(ExistsAsync/ListAsync/CreateAsync/DeleteAsync/UpdateRetentionDaysAsync) — a minority, "
			+ "not the type's primary job");
		IsGuarded(typeof(PetBox.Sessions.Data.ISessionStore)).Should().BeFalse(
			"GetContext returns SessionsDb, but 7 of its other 8 methods are domain CRUD — a minority, "
			+ "not the type's primary job");

		// THE MEMBER SWEEP MUST STILL SEE A FACTORY WHEN THERE IS ONE. This assertion used to read
		// `Offenders().Should().NotBeEmpty()` — the offenders WERE the proof the sweep worked. Both
		// allowlists are empty now, so that proof is gone with them, and its absence is exactly how this
		// guard would rot into a green that means nothing: break GuardedMembersOf, and every assertion
		// above passes by vacuity.
		//
		// So the sweep is now pointed at a type that legally holds a factory and always will: a SERVICE.
		// ProjectDirectory takes ICoreDbFactory in its constructor — that is the shape the guard forbids
		// in a page and requires in a service, and if the sweep cannot see it there, it would not see it
		// in a page either.
		GuardedMembersOf(typeof(PetBox.Web.Auth.ProjectDirectory)).Should().NotBeEmpty(
			"a service TAKES a db factory in its ctor — if the member sweep cannot see it here, where it "
			+ "is legal, it cannot see it in a page, where it is not, and every 'is empty' above is vacuous");
		Presentation(typeof(PetBox.Web.Auth.ProjectDirectory)).Should().BeNull(
			"and that same type must NOT be classified as presentation — the sweep sees it, the rule spares it");

		// The source plane must actually be reading the tree (a moved src/, a test host that does not
		// ship the sources next to the binaries) — otherwise the service-locator guard is vacuous.
		ProductSourceFiles().Should().HaveCountGreaterThan(200, "the source scan must see the real tree");
		ProductSourceFiles().Select(Path.GetFileName).Should().Contain(CompositionRootFile);

		// Same rot, same fix, on the source plane: its anchor used to be ApiKeyAuthMiddleware.cs, the one
		// known holdout, and that file is converted. What is left to anchor on is the composition root —
		// the file that resolves factories BY RIGHT. The scan must find the pattern in it (proving the
		// scan reads real text) while ServiceLocatorOffenders excludes it BY NAME (proving the exemption
		// is deliberate, not luck). If Program.cs ever stops resolving a factory, this assertion is the
		// one that should be re-pointed — not deleted.
		var compositionRoot = ProductSourceFiles().Single(p => Path.GetFileName(p) == CompositionRootFile);
		ServiceLocatorPattern.IsMatch(File.ReadAllText(compositionRoot)).Should().BeTrue(
			"the composition root resolves db factories — if the scan cannot see them THERE, it is not "
			+ "reading source at all, and NoCodeOutsideTheCompositionRoot is green over nothing");
		ServiceLocatorOffenders().Should().NotContain(CompositionRootFile,
			"and the composition root is excluded by name, not by the pattern failing to match it");

		// And the pattern must not merely be matching everything: the composition root is the file that
		// legitimately resolves factories, and it is excluded by name rather than by luck.
		ServiceLocatorPattern.IsMatch("sp.GetRequiredService<ICoreDbFactory>()").Should().BeTrue();
		ServiceLocatorPattern.IsMatch("sp.GetService<IDeployDbFactory>()").Should().BeTrue();
		ServiceLocatorPattern.IsMatch("sp.GetRequiredService<IScopedDbFactory<TasksDb>>()").Should().BeTrue();
		// The FULLY QUALIFIED form — the hole the first draft of this pattern shipped with, found by
		// seeding the violation and watching the guard stay green. It stays pinned so it cannot reopen.
		ServiceLocatorPattern.IsMatch("ctx.RequestServices.GetRequiredService<PetBox.Core.Data.ICoreDbFactory>()")
			.Should().BeTrue("a namespace-qualified generic argument must not evade the scan");
		ServiceLocatorPattern.IsMatch("services.AddScoped<IProjectDirectory, ProjectDirectory>()").Should().BeFalse();
	}

	static IEnumerable<Type> SafeGetTypes(Assembly asm) => LayerClassification.SafeGetTypes(asm);
}

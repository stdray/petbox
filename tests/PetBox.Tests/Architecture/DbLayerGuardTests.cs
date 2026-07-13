using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Data;
using PetBox.Deploy.Data;
using PetBox.Web;

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
// THE FOUR CATEGORIES (AGENTS.md's own list, in `Presentation` below): Razor PageModels (Pages/**),
// page filters, middleware and IClaimsTransformation, minimal-API endpoint classes, and the MCP
// tools (Mcp/**).
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
	// The doors onto every database in the system. `IScopedDbFactory<>` is matched as an OPEN
	// generic, so `IScopedDbFactory<TasksDb>`, `<MemoryDb>`, `<LogDb>`, `<SessionsDb>`, `<ConfigDb>`
	// are all covered by the one entry — a new context gets a guarded factory for free.
	static readonly Type[] GuardedFactories =
	[
		typeof(ICoreDbFactory),
		typeof(IDeployDbFactory),
		typeof(IScopedDbFactory<>),
	];

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
	static readonly Assembly[] ProductAssemblies = LoadProductAssemblies();

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

	// THE COMPOSITION ROOT IS NOT THE PRESENTATION LAYER. Program builds the service graph: it hands
	// factories TO services, and at startup it resolves them directly to run migrations and seed the
	// admin. That is the one place in the app where holding a factory outside a service is the whole
	// job, so it is excluded by construction rather than allowlisted — an allowlist entry would claim
	// it is debt somebody should pay off, and it is not.
	//
	// The cost of this exclusion, stated plainly: an endpoint lambda written INLINE in Program.cs
	// would not be seen. Today Program maps by delegating to the *Api classes (which ARE swept), so
	// this is not a live hole — but if inline endpoints ever appear in Program, this exclusion is the
	// thing to revisit.
	static bool IsCompositionRoot(Type t) => t == typeof(Program);

	// AGENTS.md's four categories, as reflectable signatures. Returns the category name (used in the
	// failure message) or null when the type is not presentation.
	static string? Presentation(Type t)
	{
		if (IsCompositionRoot(t)) return null;

		// 1. Razor PageModels — the Pages/** pile.
		if (typeof(PageModel).IsAssignableFrom(t)) return "Razor PageModel";

		// 2. Page filters, middleware, claims transformation. Middleware is matched BOTH ways:
		//    the interface (IMiddleware) and the CONVENTION (a ctor taking RequestDelegate), because
		//    ASP.NET's conventional middleware implements no interface at all and every middleware in
		//    this repo is the conventional kind — matching only IMiddleware would sweep nothing.
		if (typeof(IPageFilter).IsAssignableFrom(t) || typeof(IAsyncPageFilter).IsAssignableFrom(t))
			return "page filter";
		if (typeof(IClaimsTransformation).IsAssignableFrom(t)) return "IClaimsTransformation";
		if (typeof(IMiddleware).IsAssignableFrom(t)) return "middleware";
		if (t.GetConstructors(AllMembers).Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(RequestDelegate))))
			return "middleware";

		// 3. Minimal-API endpoint classes: the reflectable signature of "this class maps endpoints" is
		//    a method that takes the thing you map them onto.
		if (t.GetMethods(AllMembers).Any(m => m.GetParameters().Any(p =>
				p.ParameterType == typeof(IEndpointRouteBuilder) || p.ParameterType == typeof(WebApplication))))
			return "minimal-API endpoint class";

		// 4. The MCP tool surface — Mcp/** by namespace.
		if (t.Namespace?.StartsWith("PetBox.Web.Mcp", StringComparison.Ordinal) == true)
			return "MCP tools";

		return null;
	}

	const BindingFlags AllMembers =
		BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

	// The outermost declaring type. A lambda inside `AuthApi.Map(...)` is lowered onto a nested
	// `<>c__DisplayClass`/`<>c` whose DeclaringType is AuthApi — so a captured factory is a FIELD of a
	// nested type, and a `[FromServices] ICoreDbFactory` handler parameter is a PARAMETER of a nested
	// type's method. Classifying by the outermost type is what makes those two visible as violations
	// of the class that hosts them.
	static Type Outermost(Type t)
	{
		while (t.DeclaringType is { } outer) t = outer;
		return t;
	}

	static bool IsGuarded(Type t) =>
		GuardedFactories.Contains(t)
		|| (t.IsGenericType && GuardedFactories.Contains(t.GetGenericTypeDefinition()));

	// Every way a factory can come to REST inside a type: taken in a constructor, stored in a field
	// (including a lambda's captured local, which the compiler turns into exactly that), or accepted
	// as a method parameter (including a minimal-API handler's, which the compiler turns into exactly
	// that). Properties are covered too — an auto-property IS a field.
	static IEnumerable<string> GuardedMembersOf(Type t) =>
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
	// get a factory that leaves no trace in any of those, and ApiKeyAuthMiddleware used to use it:
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

	// ALLOWLIST — EMPTY, same contract as the type allowlist above. Its one entry was
	// ApiKeyAuthMiddleware, which now takes IApiKeyLookup as an invoke parameter (it cannot take one in
	// the ctor: conventional middleware is a singleton and the lookup is scoped — that is a captive
	// dependency, and CaptiveDependencyTests says so). The conversion was MEASURED, as that entry
	// demanded: 20.5 vs 21.4 µs per verification, median over 8x5000 alternating rounds — the extra hop
	// is a virtual call against a db round trip, and it disappears in the noise.
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
		byCategory.Should().ContainKey("MCP tools").WhoseValue.Should()
			.HaveCountGreaterThan(5, "Mcp/**Tools.cs must be in the swept set");

		// And the specific shapes that are easy to break silently.
		byCategory["Razor PageModel"].Should().Contain(typeof(PetBox.Web.Pages.Admin.ProjectDetailModel),
			"the page this wave converted must still be CLASSIFIED (it is now clean, not unseen)");
		byCategory["middleware"].Should().Contain(typeof(PetBox.Core.Auth.ApiKeyAuthMiddleware),
			"conventional middleware implements no interface — the RequestDelegate-ctor rule must catch it");
		byCategory["minimal-API endpoint class"].Should().Contain(typeof(PetBox.Data.DataDbsApi),
			"a static *Api class maps endpoints via IEndpointRouteBuilder and must be swept");

		// The guarded set really is the set of db doors — i.e. this guard points at what it claims to.
		GuardedFactories.Should().HaveCount(3);
		IsGuarded(typeof(IScopedDbFactory<PetBox.Tasks.Data.TasksDb>)).Should()
			.BeTrue("IScopedDbFactory<> is matched as an OPEN generic, so every context is covered");

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

	static IEnumerable<Type> SafeGetTypes(Assembly asm)
	{
		try { return asm.GetTypes(); }
		catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
	}
}

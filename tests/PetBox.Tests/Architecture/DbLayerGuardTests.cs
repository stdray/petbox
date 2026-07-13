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

	// ── THE ALLOWLIST — IT SHRINKS, IT DOES NOT GROW ──────────────────────────────────────────────
	//
	// `db-out-of-pages-into-services` is IN PROGRESS: most of Pages/** predates the rule (AGENTS.md
	// says so in as many words) and still holds a factory. This guard has to be GREEN on today's
	// main or it could not be merged at all, so every not-yet-converted holdout is listed here WITH
	// THE SERVICE IT IS WAITING FOR. The entry is a debt marker, not a dispensation.
	//
	// NEVER add an entry to make a NEW violation pass. New presentation code asks a service; if the
	// service does not exist, the work is to open the door, not to widen this list.
	//
	// The list cannot silently rot in either direction, and BOTH directions are tested:
	//   - a file that leaks but is NOT listed          -> NoPresentationType_TakesADbFactory fails
	//   - a file that is listed but is ALREADY CLEAN   -> AllowlistEntries_AreStillNeeded fails
	// The second is the one that matters most: a stale entry HIDES WORK SOMEBODY ALREADY DID and
	// re-opens the door for free. When you convert a page, DELETE ITS LINE — the build makes you.
	// Each entry names the TABLES the type actually opens core.db for, and what must exist before the
	// line can be deleted. Two kinds of debt, and they are NOT the same size — the comment says which:
	//   "DOOR EXISTS"  -> the service is already there; this is a mechanical conversion nobody did yet.
	//   "NEEDS A DOOR" -> no service owns that table; converting means writing one first.
	static readonly IReadOnlyDictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		// ── Razor PageModels ─────────────────────────────────────────────────────────────────────
		["PetBox.Web.Pages.Admin.IndexModel"] =
			"Reads ApiKeys + Projects + Settings + Users + Workspaces — the sysadmin landing's COUNTS. "
			+ "NEEDS A DOOR: every one of those tables has a service, but none of them COUNTS. A rollup/"
			+ "stats door is the missing piece (and is the thing that finally makes core.db cacheable).",

		["PetBox.Web.Pages.Admin.ProjectAgentDefsModel"] =
			"Reads Projects only (AgentDefs itself already goes through AgentDefinitionService). "
			+ "DOOR EXISTS: IProjectDirectory. Mechanical.",

		["PetBox.Web.Pages.Admin.ProjectConnectModel"] =
			"Reads Projects and INSERTS an ApiKey (the onboarding snippet mints a key). "
			+ "DOOR EXISTS: IProjectDirectory + AgentKeyAdminService.MintAsync — both are exactly the "
			+ "calls ProjectDetail now makes. Should fall next, and cheaply.",

		["PetBox.Web.Pages.Admin.ProjectDataModel"] =
			"Reads Projects + DataDbs and inserts a DataDb. DOOR EXISTS: IProjectDirectory + "
			+ "IDataDbCatalog (List/Get/Create/Delete/Describe — it covers this page's whole shape).",

		["PetBox.Web.Pages.Admin.ProjectDataDbModel"] =
			"Reads DataDbs. DOOR EXISTS: IDataDbCatalog.",

		["PetBox.Web.Pages.Admin.ProjectLogsModel"] =
			"Reads Projects. DOOR EXISTS: IProjectDirectory. Mechanical.",

		["PetBox.Web.Pages.Admin.ProjectMemoryModel"] =
			"Reads Projects. DOOR EXISTS: IProjectDirectory. Mechanical.",

		["PetBox.Web.Pages.Admin.ProjectMethodologyModel"] =
			"Reads Projects. DOOR EXISTS: IProjectDirectory. Mechanical.",

		["PetBox.Web.Pages.Admin.ProjectTasksModel"] =
			"Reads Projects. DOOR EXISTS: IProjectDirectory. Mechanical.",

		["PetBox.Web.Pages.Admin.WorkspaceAdminModel"] =
			"Reads Projects + Workspaces + WorkspaceMembers. DOOR EXISTS: IProjectDirectory + "
			+ "IWorkspaceAdminService + IWorkspaceMembershipService — all three landed in wave 2.",

		["PetBox.Web.Pages.ProjectHome.DatabaseModel"] =
			"Reads DataDbs. DOOR EXISTS: IDataDbCatalog.",

		["PetBox.Web.Pages.ProjectHome.DatabasesModel"] =
			"Reads DataDbs. DOOR EXISTS: IDataDbCatalog.",

		["PetBox.Web.Pages.ProjectHome.IndexModel"] =
			"Reads ApiKeys + DataDbs + HealthReports + Logs — the project-home rollup, and THE page "
			+ "AGENTS.md means by 'a GET of a project page opens core.db 7-9 times'. Each table now has a "
			+ "door (AgentKeyAdminService / IDataDbCatalog / IHealthReportService), but the page wants ONE "
			+ "rollup, not four round trips. NEEDS A DOOR: the project-rollup read this work item exists "
			+ "to make possible.",

		["PetBox.Web.Pages.ProjectHome.MemoryModel"] =
			"Opens core.db for exactly one thing: WorkspaceMemory.EnsureContainerAsync(db, ...) — lazily "
			+ "provisioning a workspace-memory container. NEEDS A DOOR: the page says so itself in a "
			+ "comment ('provisioning a container has no service door yet'). IWorkspaceMemoryDirectory "
			+ "resolves containers but does not PROVISION one.",

		["PetBox.Web.Pages.ProjectHome.TableModel"] =
			"Reads DataDbs. DOOR EXISTS: IDataDbCatalog.",

		["PetBox.Web.Pages.Config.IndexModel"] =
			"Reads/writes SavedConfigFilters via IScopedDbFactory<ConfigDb>. NEEDS A DOOR: PetBox.Config "
			+ "has no service layer at all — the module reads its db inline everywhere.",

		["PetBox.Web.Pages.Dashboard.IndexModel"] =
			"Reads ApiKeys + DataDbs + HealthReports + Logs + Projects — the fleet rollup. Same shape and "
			+ "same missing piece as ProjectHome.IndexModel: NEEDS A DOOR (a rollup, not five reads).",

		["PetBox.Web.Pages.Llm.IndexModel"] =
			"Reads Projects. DOOR EXISTS: IProjectDirectory. Mechanical.",

		["PetBox.Web.Pages.LoginModel"] =
			"Reads Users + WorkspaceMembers to AUTHENTICATE (name -> password hash). NEEDS A DOOR: "
			+ "IUserAdminService is admin-scoped and must not be handed to the anonymous login page; the "
			+ "credential lookup wants its own door in PetBox.Core.Auth.",

		["PetBox.Web.Pages.Logs.IndexModel"] =
			"Reads Projects + reads/writes SavedQueries. IProjectDirectory covers the first half; NEEDS A "
			+ "DOOR for SavedQueries (nothing owns that table).",

		["PetBox.Web.Pages.Logs.TracesModel"] =
			"Reads Projects. DOOR EXISTS: IProjectDirectory. Mechanical.",

		["PetBox.Web.Pages.Me.SecurityModel"] =
			"Reads/writes the CURRENT user's own row (password change). NEEDS A DOOR: a self-service "
			+ "account door. IUserAdminService is admin-scoped — handing it to a page any logged-in user "
			+ "reaches would be a privilege widening, not a conversion.",

		["PetBox.Web.Pages.Nav.TreeModel"] =
			"Reads DataDbs + Logs to decide which nav nodes to show. DOOR EXISTS for DataDbs "
			+ "(IDataDbCatalog); the log-catalog half NEEDS A DOOR (PetBox.Logging has no service layer).",

		["PetBox.Web.Pages.ShareModel"] =
			"Resolves a share token against ShareLinks. NEEDS A DOOR: nothing owns ShareLinks today.",

		// NOTE: ApiKeyAuthMiddleware is NOT here. It does not TAKE a factory — it RESOLVES one from the
		// container mid-method (a service locator), which no ctor/field/parameter sweep can see. It is
		// caught in the source plane instead: see ServiceLocatorAllowlist below.

		// ── Minimal-API endpoint classes ─────────────────────────────────────────────────────────
		// These are the `.MapGet(..., (ICoreDbFactory dbf) => ...)` handlers — the factory arrives as a
		// LAMBDA PARAMETER, which is why this guard sweeps methods and closure fields, not just ctors.
		["PetBox.Data.DataDbsApi"] =
			"The data-module REST surface. It carries its OWN db/table NAME rules and applies them in the "
			+ "same statement as the write; lifting it to IDataDbCatalog without carrying those rules "
			+ "across is how they get quietly dropped. The largest single conversion left, and explicitly "
			+ "NOT in this wave.",

		["PetBox.Data.SchemaApi"] =
			"Schema apply/introspect REST. Same door (IDataDbCatalog, extended) and same naming-rule "
			+ "hazard as PetBox.Data.DataDbsApi.",

		["PetBox.Core.Auth.AuthApi"] =
			"Login/logout/whoami REST. NEEDS A DOOR: the same credential lookup as Pages.LoginModel.",

		["PetBox.Config.ConfigApi"] =
			"Config REST (Conf/Create/Delete). NEEDS A DOOR: PetBox.Config has no service layer — same "
			+ "gap as Pages.Config.IndexModel.",

		["PetBox.Log.Core.LogApi"] =
			"Log ingest REST (the Seq-compatible ingest paths). NEEDS A DOOR: PetBox.Logging has no "
			+ "service layer — same gap as Pages.Logs.IndexModel.",

		["PetBox.Log.Core.ShareApi"] =
			"Log-share REST (create a share link, serve its TSV). NEEDS A DOOR: ShareLinks — the same "
			+ "table, and the same missing owner, as Pages.ShareModel.",
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
	// get a factory that leaves no trace in any of those, and ApiKeyAuthMiddleware uses it:
	//
	//     var factory = context.RequestServices.GetRequiredService<ICoreDbFactory>();
	//
	// No ctor parameter, no declared field, no method parameter — a local, pulled out of the container
	// mid-method. Reflection cannot see it (see IsAsyncStateMachine for the Debug/Release trap that
	// made this look catchable when it is not), so it is caught HERE, in the source.
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

	// ALLOWLIST — SHRINKS ONLY, same contract as the type allowlist above.
	static readonly IReadOnlyDictionary<string, string> ServiceLocatorAllowlist =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["ApiKeyAuthMiddleware.cs"] =
				"API-key authentication, on EVERY authenticated request: it pulls ICoreDbFactory out of "
				+ "RequestServices and opens core.db to verify the key. NEEDS A DOOR — the credential lookup "
				+ "Pages.LoginModel and Core.Auth.AuthApi are also waiting on. Convert it WITH A MEASUREMENT "
				+ "rather than on principle: this is the hottest core.db reader in the app, and it is the one "
				+ "place where an extra service hop is not obviously free. Until then it must at least be "
				+ "VISIBLE, which is what this entry buys.",
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

		// The offender sweep itself must be finding things — the allowlist is non-empty today, and if
		// Offenders() returned nothing then AllowlistEntries_AreStillNeeded would be the only thing
		// failing and NoPresentationType_TakesADbFactory would be vacuously green.
		Offenders().Should().NotBeEmpty("the conversion is still in progress — the allowlist is non-empty");

		// The source plane must actually be reading the tree (a moved src/, a test host that does not
		// ship the sources next to the binaries) — otherwise the service-locator guard is vacuous.
		ProductSourceFiles().Should().HaveCountGreaterThan(200, "the source scan must see the real tree");
		ProductSourceFiles().Select(Path.GetFileName).Should().Contain(CompositionRootFile);
		ServiceLocatorOffenders().Should().Contain("ApiKeyAuthMiddleware.cs",
			"the one known service-locator holdout must be SEEN by the scan (it is allowlisted, not invisible)");

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

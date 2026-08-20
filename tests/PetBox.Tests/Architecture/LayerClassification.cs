using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Mono.Cecil;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// ── THE LAYER CLASSIFICATION — DECLARED ONCE, READ BY EVERY ARCHITECTURAL GATE ────────────────────
//
// Spec `arch-gate-classification-single`: "the same type MUST NOT get opposite classifications from
// two architectural gates; the classification MUST be declared once as a decision, not left as one
// gate's default."
//
// WHY THIS FILE EXISTS — the defect it closes (work `configtools-gate-classification`).
// `DbLayerGuardTests.Presentation()` used to sweep the WHOLE `PetBox.Web.Mcp` namespace into the
// presentation layer ("MCP tools", its category 4). Ten lines away, `ConfigBoundaryTests` said in
// prose that `ConfigTools` is "deliberately NOT in scope — module/service code, not presentation".
// One type, two opposite answers, and BOTH gates green: not because there was no defect, but because
// `IConfigDbFactory` — a thin typed facade over the guarded `IScopedDbFactory<ConfigDb>` — is not in
// the first gate's door list, so the first gate could not SEE the violation the second one named.
// Neither answer was a decision. Each was a side effect of what its own gate happened to look at.
//
// THE DECISION (owner, 2026-08-20): an MCP tool class is the SERVICE layer of its module, not
// presentation. It owns its module's data access the same way `LogCatalogTools` owns the log catalog
// and `RelationTools` owns the relation store (`TasksBoundaryTests` already carves that precedent).
// The rejected alternative was to migrate `ConfigTools` onto `IConfigDirectory` the way Razor pages
// were migrated: that moves four live call sites to satisfy a test, when the argument was never about
// the code — it was about the definition.
//
// WHAT MAKES THIS A DECLARATION AND NOT AN ALLOWLIST. Every rule below matches a SHAPE, never a name.
// A brand-new `FooTools` in a brand-new module is classified the moment it carries
// `[McpServerToolType]` — no line is added anywhere, and `LayerClassificationTests` proves that on a
// type emitted at runtime that no list could possibly contain. The one entry that names a specific
// type is the composition root, and it is a declared OVERRIDE tier, not a quiet exemption: Program's
// whole job is holding factories, so calling it debt would be a lie.
//
// HOW A NEW DISAGREEMENT GOES RED. The rules are a TABLE, and `Decide` refuses to pick a winner when
// two rules of the same tier claim a type for different layers — it throws, so every gate reading the
// classification fails at once, and `LayerClassificationTests.EveryProductType_HasOneAgreedLayer`
// reports the conflicting pair by name. Re-adding the old "all of PetBox.Web.Mcp is presentation"
// rule next to the MCP-adapter rule is exactly that shape, and it reds on all twenty tool types.
public enum Layer
{
	// Holds no database door and asks a service. AGENTS.md, "Database connections — a hard invariant".
	Presentation,

	// May take a db factory in its constructor. The default for anything no rule claims — which is why
	// a layer we actually decided on (MCP module adapters) is DECLARED below rather than left to fall
	// through to it. A default is not a decision.
	Service,
}

// Tiers exist so a documented, deliberate exception (the composition root) can outrank a shape rule
// WITHOUT hiding a genuine contradiction between two shape rules. Conflict detection runs inside the
// winning tier, so adding an override cannot silence a disagreement — only an override can beat a
// shape rule, and there is exactly one override in the table.
public enum RuleTier
{
	Shape,
	Override,
}

public sealed record LayerRule(string Name, Layer Layer, RuleTier Tier, Func<Type, bool> Matches);

public sealed class LayerClassificationConflictException(Type type, IReadOnlyList<LayerRule> rules)
	: Exception(
		$"'{type.FullName}' is claimed by {rules.Count} {rules[0].Tier} rules that DISAGREE about its layer: "
		+ string.Join(", ", rules.Select(r => $"'{r.Name}' says {r.Layer}"))
		+ ". A type gets ONE declared classification (spec `arch-gate-classification-single`) — decide "
		+ "which rule is right and delete or narrow the other. Do not resolve this by ordering the "
		+ "table: precedence between shape rules is exactly the silent default this file exists to "
		+ "abolish.")
{
	public Type Type { get; } = type;

	public IReadOnlyList<LayerRule> Rules { get; } = rules;
}

public static class LayerClassification
{
	public const BindingFlags AllMembers =
		BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

	// THE TABLE. Order is documentation, NOT precedence — see LayerClassificationConflictException.
	public static readonly IReadOnlyList<LayerRule> Rules =
	[
		// ── OVERRIDE ────────────────────────────────────────────────────────────────────────────────
		// THE COMPOSITION ROOT IS NOT THE PRESENTATION LAYER. Program builds the service graph: it hands
		// factories TO services, and at startup resolves them directly to run migrations and seed the
		// admin. It also matches the minimal-API shape below (it takes a WebApplication), which is why it
		// needs a tier that outranks a shape rule rather than an allowlist entry claiming it is debt.
		//
		// The cost, stated plainly: an endpoint lambda written INLINE in Program.cs is not seen. Today
		// Program maps by delegating to the *Api classes (which ARE swept), so this is not a live hole —
		// but if inline endpoints ever appear in Program, this exclusion is the thing to revisit.
		new("composition root", Layer.Service, RuleTier.Override, t => t == typeof(Program)),

		// ── SHAPE: PRESENTATION ─────────────────────────────────────────────────────────────────────
		// AGENTS.md's own list, as reflectable signatures: "a Razor PageModel, a page filter, middleware,
		// an IClaimsTransformation or an endpoint lambda asks a service, it does not call .Open() itself."
		new("Razor PageModel", Layer.Presentation, RuleTier.Shape,
			t => typeof(PageModel).IsAssignableFrom(t)),

		new("page filter", Layer.Presentation, RuleTier.Shape,
			t => typeof(IPageFilter).IsAssignableFrom(t) || typeof(IAsyncPageFilter).IsAssignableFrom(t)),

		new("IClaimsTransformation", Layer.Presentation, RuleTier.Shape,
			t => typeof(IClaimsTransformation).IsAssignableFrom(t)),

		// Middleware is matched BOTH ways: the interface (IMiddleware) and the CONVENTION (a ctor taking
		// RequestDelegate), because ASP.NET's conventional middleware implements no interface at all and
		// every middleware in this repo is the conventional kind — matching only IMiddleware sweeps nothing.
		new("middleware", Layer.Presentation, RuleTier.Shape,
			t => typeof(IMiddleware).IsAssignableFrom(t)
				|| t.GetConstructors(AllMembers)
					.Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(RequestDelegate)))),

		// The reflectable signature of "this class maps endpoints" is a method that takes the thing you
		// map them onto.
		new("minimal-API endpoint class", Layer.Presentation, RuleTier.Shape,
			t => t.GetMethods(AllMembers).Any(m => m.GetParameters().Any(p =>
				p.ParameterType == typeof(IEndpointRouteBuilder) || p.ParameterType == typeof(WebApplication)))),

		// THE MCP TRANSPORT PIPELINE — and the reason "the namespace" was never the right unit.
		// `PetBox.Web.Mcp` holds two different things. These seven (`McpTenantEnforcementFilter`,
		// `McpToolScopeFilter`, `McpTracingFilter`, `McpUnknownParameterFilter`, `McpErrorEnvelopeFilter`,
		// `McpProjectDefaultFilter`, `McpProjectExistsFilter`) are the protocol pipeline: they wrap every
		// request the way page filters and middleware wrap an HTTP one, and they are presentation for the
		// same reason those are. Their reflectable signature is the exact analogue of the rule above —
		// `Register(IMcpRequestFilterBuilder)` is to the MCP pipeline what `Map(IEndpointRouteBuilder)` is
		// to the HTTP one — so a new pipeline stage is classified without touching this file either.
		new("MCP pipeline stage", Layer.Presentation, RuleTier.Shape,
			t => t.GetMethods(AllMembers)
				.Any(m => m.GetParameters().Any(p => p.ParameterType == typeof(IMcpRequestFilterBuilder)))),

		// ── SHAPE: SERVICE (THE DECISION) ───────────────────────────────────────────────────────────
		// The other thing in `PetBox.Web.Mcp`: the twenty tool classes. An MCP tool class is its module's
		// SERVICE-layer face — the agent-facing twin of the module's REST surface, owning its module's
		// data the way `LogCatalogTools` owns the log catalog. Five sibling gates already read it this
		// way (`TasksBoundaryTests`, `MemoryBoundaryTests`, `SessionBoundaryTests`, `DeployBoundaryTests`,
		// `LlmRouterBoundaryTests` each require MCP tools to reach OTHER modules through those modules'
		// services) — this rule is what made that reading a decision instead of a coincidence.
		//
		// STATED AS A COST, NOT HIDDEN: a tool class may now hold a db factory, and
		// `NoPresentationType_TakesADbFactory` no longer covers the twenty. What still binds them is the
		// per-module boundary gates above — an adapter owns ITS OWN module's data and nothing else. If a
		// tool class is ever caught opening a database belonging to another module, the answer is that
		// module's boundary gate, not a return to sweeping this namespace.
		new("MCP module adapter", Layer.Service, RuleTier.Shape,
			t => t.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false)),
	];

	// Every rule that claims this type. Public so a gate can EXPLAIN itself rather than assert a bare bool.
	public static IReadOnlyList<LayerRule> MatchingRules(Type t) => [.. Rules.Where(r => r.Matches(t))];

	// The rule that decides, or null when no rule claims the type (→ Service by default).
	// Throws when the winning tier disagrees with itself — see the class comment.
	public static LayerRule? Decide(Type t)
	{
		var matches = MatchingRules(t);
		if (matches.Count == 0) return null;

		var tier = matches.Any(r => r.Tier == RuleTier.Override) ? RuleTier.Override : RuleTier.Shape;
		var winners = matches.Where(r => r.Tier == tier).ToList();
		if (winners.DistinctBy(r => r.Layer).Count() > 1) throw new LayerClassificationConflictException(t, winners);

		return winners[0];
	}

	public static Layer Of(Type t) => Decide(t)?.Layer ?? Layer.Service;

	// The presentation CATEGORY NAME, or null when the type is not presentation. This is the shape
	// `DbLayerGuardTests` consumes: it names the category in its failure message.
	public static string? PresentationCategory(Type t) =>
		Decide(t) is { Layer: Layer.Presentation } rule ? rule.Name : null;

	// The outermost declaring type. A lambda inside `AuthApi.Map(...)` is lowered onto a nested
	// `<>c__DisplayClass`/`<>c` whose DeclaringType is AuthApi — so a captured factory is a FIELD of a
	// nested type. Classifying by the outermost type is what makes that visible as a violation of the
	// class that hosts it.
	public static Type Outermost(Type t)
	{
		while (t.DeclaringType is { } outer) t = outer;
		return t;
	}

	public static IEnumerable<Type> SafeGetTypes(Assembly asm)
	{
		try { return asm.GetTypes(); }
		catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
	}

	// Conflicts across a whole sweep, collected instead of thrown — the reporting form
	// `LayerClassificationTests` needs to name every offender in one failure instead of the first.
	public static IReadOnlyList<(Type Type, IReadOnlyList<LayerRule> Rules)> Conflicts(IEnumerable<Type> types)
	{
		var found = new List<(Type, IReadOnlyList<LayerRule>)>();
		foreach (var t in types)
		{
			try { Decide(t); }
			catch (LayerClassificationConflictException ex) { found.Add((ex.Type, ex.Rules)); }
		}

		return found;
	}

	public static IEnumerable<Type> PresentationTypesIn(Assembly asm) =>
		SafeGetTypes(asm).Where(t => Of(Outermost(t)) == Layer.Presentation);

	// ── THE BRIDGE TO NetArchTest ────────────────────────────────────────────────────────────────
	//
	// The module boundary gates reason over Mono.Cecil, not reflection, so they cannot call
	// `PresentationCategory` directly — and before this file existed, that is precisely why they each
	// re-declared the layer with a namespace literal of their own and drifted apart from the reflective
	// gate. This adapter is the whole fix: the DECISION stays in the table above, and Cecil-side gates
	// select their scope through it instead of restating it.
	//
	// Nested types are keyed on the reflection spelling (`Outer+Nested`); Cecil writes `Outer/Nested`.
	public sealed class PresentationTypesOf : ICustomRule
	{
		readonly HashSet<string> _names;

		public PresentationTypesOf(Assembly assembly) =>
			_names = [.. PresentationTypesIn(assembly).Select(t => t.FullName!)];

		public int Count => _names.Count;

		public bool MeetsRule(TypeDefinition type) => _names.Contains(type.FullName.Replace('/', '+'));
	}
}

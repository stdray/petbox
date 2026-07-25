using Microsoft.AspNetCore.Builder;

namespace PetBox.Core.Auth;

// THE DECLARATION TYPE for spec `authz-scope-declaration`:
//
//     "Каждая поверхность в одном машиночитаемом месте объявляет ровно одно: откуда берётся
//      целевой арендатор — либо к какому классу исключения она относится. Класс вне закрытого
//      перечня объявить нельзя."
//
// This file is ONLY the declaration — the vocabulary and its two carriers. It enforces nothing:
// no PEP reads it yet (work `authz-default-deny-delivery`, steps 2-3), and the ratchet
// (AuthzDeclarationRatchetTests) is what makes a NEW undeclared surface fail the build.
//
// WHY A TYPE AND NOT A CONVENTION. "Объявление — ДАННЫЕ, а не комментарий в коде": a comment
// saying "this endpoint is fleet-wide" is invisible to every tool we have. An attribute is
// readable by the ratchet, by the future PEP, and by the surface's own description.
//
// HOW THE UNION IS CLOSED — by the COMPILER, not by review. TenantDeclarationAttribute's
// constructor is `private protected`, so the only types that can derive from it are the two
// below, in this assembly. A third kind of declaration cannot be written in PetBox.Web, in a
// module, or in a test — it is a change to THIS file, which is the point: "Добавление класса —
// правка спеки, то есть решение владельца, а не PR". The exemption list itself is closed the
// same way (the TenantExemption enum), and both closures are pinned by
// AuthzDeclarationRatchetTests.TheDeclarationType_IsClosed.
//
// THE TWO CARRIERS, one type:
//   * REST + Razor — ENDPOINT METADATA. An attribute on a minimal-API handler method lands in
//     Endpoint.Metadata by itself (RequestDelegateFactory copies the method's attributes), and so
//     does an attribute on a Razor PageModel class (the page action descriptor carries the model
//     type's attributes). Endpoints mapped from a shared helper, or built where no method exists
//     to attribute, use the DeclaresTenant*/ convention extensions at the bottom of this file.
//   * MCP — the SAME attribute on the [McpServerTool] method (or on its [McpServerToolType]
//     class, which declares for every tool in it). MCP tools are not endpoints — the whole tool
//     surface hangs off one /mcp endpoint — so they are read by reflection instead.
//
// A declaration is ADDITIVE by construction: an attribute or a convention call, never a change to
// a method signature. That is deliberate — a signature-wide diff would collide with every
// parallel branch touching the same surfaces.
public abstract class TenantDeclarationAttribute : Attribute
{
	// Closed hierarchy: `private protected` means "derivable only inside PetBox.Core", and the two
	// derivations that exist are the two below. Do not relax it to `protected` — that reopens the
	// exemption list to anyone with a new verb, which is exactly the opt-in hole this replaces.
	private protected TenantDeclarationAttribute() { }

	// One line, for a failure message or a surface's own self-description.
	public abstract string Describe();
}

// `TenantKind` (which of the two things a reference names) lives with the reference itself, in
// TenantRef.cs — a declaration only points at a kind, it does not define one.

// WHERE the target tenant comes from.
//
// CLOSED, BUT NOT BY THE SPEC — and the difference matters. Spec `authz-scope-declaration` closes
// exactly one list, the EXEMPTION CLASSES ("Класс вне закрытого перечня объявить нельзя"); it never
// enumerates sources at all. This enum is closed by the same REASONING (a source is something the
// decision point must learn to read, not something a handler can invent), which makes adding one an
// engineering decision inside the work item rather than a change to a promise. It is still a
// deliberate edit HERE, pinned by AuthzDeclarationRatchetTests.TheDeclarationType_IsClosed, so it
// cannot happen by accident — which is the whole property that matters.
public enum TenantSource
{
	// A route value: /api/data/{project}/... — the tenant is in the path.
	Route,

	// A call argument: a query-string parameter, or an MCP tool parameter (`projectKey`).
	Argument,

	// A call argument that carries EITHER encoding of a tenant: an ordinary project key, or the
	// `$workspace` / `$ws-<key>` PSEUDO-PROJECT that names a workspace's shared-memory container. The
	// KIND is therefore decided per CALL, by the value, not per surface — which is why it is a source
	// of its own instead of a flag on Argument.
	//
	// WHY IT EXISTS. Shared memory is addressed through the same `projectKey` parameter as a real
	// project (spec authz-tenant-default-deny: "один и тот же смысл выражен двумя способами …
	// псевдо-ключом проекта у разделяемой памяти"), and a container is reachable by a key of ANY
	// project in its workspace (IWorkspaceMemoryDirectory.ReachableByAsync). Neither existing reading
	// serves it: as a plain Argument the PEP refuses `$ws-x` for every non-wildcard key and breaks
	// callers that work today; decoding containers in the RESOLVER instead would apply to every
	// Argument surface and hand a project-scoped key `tasks/$ws-x.db` — a loosening, on the security
	// axis, to fix one family.
	//
	// The decode is therefore attached to the DECLARATION, not to the transport: only a surface that
	// says this word gets it. tasks_*, session_*, db_* and the rest stay plain Argument, so `$ws-x`
	// remains a claim mismatch for them exactly as before. Unlike a tool-name list inside a filter
	// (McpProjectExistsFilter.WildcardTools — the antipattern this work item exists to absorb), this
	// is data ON the surface, read by the same mechanism as every other declaration and visible to the
	// ratchet, so which surfaces have it can be ENUMERATED rather than remembered.
	ArgumentOrContainer,

	// A field of the request body. "Цель, пришедшая в теле запроса, проверяется так же строго,
	// как пришедшая из маршрута" — the position of the parameter carries no leniency.
	BodyField,

	// Derived from the caller itself: the key's project claim or its configured default project.
	// Still a tenant, still checked — "Цель, ВЫВЕДЕННАЯ из клейма вызывающего, проверяется так же
	// строго, как названная им явно" — and an absent/unresolvable one is a refusal, including for
	// a cross-project claim.
	CallerDefault,
}

// THE CLOSED LIST, verbatim from spec `authz-scope-declaration`. Six classes, no seventh.
//
// "Открытый перечень — это opt-in под другим именем: любой новый глагол объявит себе исключение и
// выйдет из-под default-deny без чьего-либо внимания."
//
// What each class SUSPENDS is part of the class (spec, Следствия): CapabilityToken and Public
// suspend tenant-principal authentication as well; the other four suspend only the tenant-claim
// check, leaving authentication and the scope axis (ScopeRequirement / RequireAuthorization)
// fully in force. The scope axis is orthogonal and is not touched by any of this.
public enum TenantExemption
{
	// An operation over the fleet of nodes, not over a tenant.
	FleetWide,

	// Cross-tenant handing out of resources. NOTE (work card, "Вне скоупа"): this class currently
	// covers apikey_create, which can mint a key on ANY project — i.e. `admin:provision` is de
	// facto root over every tenant. The spec DECLARES that; narrowing it is a separate idea and an
	// explicit stop-and-ask, not a line in a PR.
	Provisioning,

	// A report to the maintainer, landing in a FIXED tenant regardless of the caller's (report_issue).
	Feedback,

	// Facts about the caller and about the surface itself: whoami, /version, the OpenAPI document.
	Identity,

	// Access is granted by a presented token that ITSELF carries the extent of access and was
	// issued by an explicit act (a share link). Per the spec this class does NOT cover a surface
	// where the token merely identifies the caller and the extent of access comes from elsewhere —
	// that is an ordinary tenant reference, not a capability.
	CapabilityToken,

	// Reachable with no tenant and no authentication at all (the login page, static error pages).
	Public,
}

// "Откуда берётся целевой арендатор" — the declaring half of the union.
//
// Usage (all three carriers):
//   [TenantFrom(TenantSource.Route, "project")]              on a minimal-API handler method
//   [TenantFrom(TenantSource.Argument, "projectKey")]        on an [McpServerTool] method or its type
//   [TenantFrom(TenantSource.Route, "key", Tenant = TenantKind.Workspace)]   on a Razor PageModel
//   app.MapGet(...).DeclaresTenant(TenantSource.Route, "project")            via the convention
[AttributeUsage(
	AttributeTargets.Method | AttributeTargets.Class,
	AllowMultiple = false,
	Inherited = false)]
public sealed class TenantFromAttribute : TenantDeclarationAttribute
{
	// `Inherited = false` on purpose: a declaration is a property of the SURFACE, and a base class
	// silently declaring for every page that derives from it is the opt-in hole in miniature. A
	// class-level declaration on an MCP tool TYPE is a different thing — that is an explicit
	// lookup order (method first, then its declaring type), not attribute inheritance.
	public TenantFromAttribute(TenantSource source, string? name = null, TenantKind tenant = TenantKind.Project)
	{
		if (!Enum.IsDefined(source))
			throw new ArgumentOutOfRangeException(nameof(source), source, "not a declared TenantSource");
		if (!Enum.IsDefined(tenant))
			throw new ArgumentOutOfRangeException(nameof(tenant), tenant, "not a declared TenantKind");

		// The name is what the decision point will READ the tenant out of, so it is required for
		// every source that has a place to read it from — and forbidden for the one that does not.
		// Enforced here rather than in a test: an attribute ctor that throws fails the reflection
		// that reads it, which is loud at startup instead of silent in production.
		var named = source != TenantSource.CallerDefault;
		if (named && string.IsNullOrWhiteSpace(name))
			throw new ArgumentException(
				$"TenantFrom({source}) must name the route value / argument / body field it reads the tenant from.",
				nameof(name));
		if (!named && !string.IsNullOrWhiteSpace(name))
			throw new ArgumentException(
				"TenantFrom(CallerDefault) reads the caller's own claim — it names no parameter.", nameof(name));

		// ArgumentOrContainer decides the KIND per call, from the value, so declaring one here is a
		// contradiction rather than a refinement: whichever kind was written would be wrong half the
		// time. `Tenant` stays at its Project default and is not read for this source.
		if (source == TenantSource.ArgumentOrContainer && tenant != TenantKind.Project)
			throw new ArgumentException(
				$"TenantFrom({source}) resolves the tenant KIND from the value (a project key, or a "
				+ "`$workspace`/`$ws-<key>` container), so it cannot also declare one.", nameof(tenant));

		Source = source;
		Name = named ? name!.Trim() : "";
		Tenant = tenant;
	}

	public TenantSource Source { get; }

	// The route value / argument / body field the tenant is read from; "" for CallerDefault.
	public string Name { get; }

	public TenantKind Tenant { get; }

	public override string Describe() => Source switch
	{
		TenantSource.CallerDefault => $"tenant: {Tenant} from the caller's own claim",
		// No kind is named on purpose — the value decides it, and printing "Project" here would be a
		// lie on exactly the calls this source exists for.
		TenantSource.ArgumentOrContainer => $"tenant: Project or Workspace container from Argument '{Name}'",
		_ => $"tenant: {Tenant} from {Source} '{Name}'",
	};
}

// "…либо к какому классу исключения она относится" — the exempting half of the union.
[AttributeUsage(
	AttributeTargets.Method | AttributeTargets.Class,
	AllowMultiple = false,
	Inherited = false)]
public sealed class TenantExemptAttribute : TenantDeclarationAttribute
{
	public TenantExemptAttribute(TenantExemption exemption, string reason)
	{
		// A cast int (`(TenantExemption)99`) is the one way to escape a closed enum, so the ctor
		// closes it again.
		if (!Enum.IsDefined(exemption))
			throw new ArgumentOutOfRangeException(nameof(exemption), exemption,
				"not one of the six exemption classes (spec authz-scope-declaration) — the list is CLOSED; "
				+ "a new class is a spec change, not a PR");
		if (string.IsNullOrWhiteSpace(reason))
			throw new ArgumentException(
				"an exemption states WHY this surface has no tenant — an unexplained exemption is the "
				+ "opt-in hole with extra steps", nameof(reason));

		Exemption = exemption;
		Reason = reason.Trim();
	}

	public TenantExemption Exemption { get; }

	// Why this surface is in that class. Read by humans, and by whatever renders the surface's own
	// description; never load-bearing for a decision.
	public string Reason { get; }

	public override string Describe() => $"exempt: {Exemption} — {Reason}";
}

// The convention form of the same two attributes, for endpoints that have no method to attribute
// (a lambda mapped from a helper, a group, an endpoint built by the framework). Adds the very same
// attribute instance to Endpoint.Metadata, so every reader — the ratchet, the future PEP — sees one
// shape regardless of which carrier was used.
public static class TenantDeclarationEndpointExtensions
{
	public static TBuilder DeclaresTenant<TBuilder>(
		this TBuilder builder, TenantSource source, string? name = null, TenantKind tenant = TenantKind.Project)
		where TBuilder : IEndpointConventionBuilder
	{
		var declaration = new TenantFromAttribute(source, name, tenant);
		builder.Add(b => b.Metadata.Add(declaration));
		return builder;
	}

	public static TBuilder DeclaresTenantExempt<TBuilder>(
		this TBuilder builder, TenantExemption exemption, string reason)
		where TBuilder : IEndpointConventionBuilder
	{
		var declaration = new TenantExemptAttribute(exemption, reason);
		builder.Add(b => b.Metadata.Add(declaration));
		return builder;
	}
}

using System.Security.Claims;

namespace PetBox.Core.Auth;

// The verdict a policy-enforcement point acts on. `Message` is empty on a pass and is the ONE thing
// a refused caller is told on a refusal — the transports render it differently (403 body vs the MCP
// {error} envelope) but neither invents a second reason.
public readonly record struct TenantGateResult(bool Allowed, string Message)
{
	public static TenantGateResult Pass { get; } = new(true, "");

	public static TenantGateResult Refuse(string message) => new(false, message);
}

// THE HALF OF A PEP THAT IS THE SAME ON BOTH PLANES (work `authz-default-deny-delivery`, step 3).
//
// There are two enforcement points, not one — endpoint middleware for REST+Razor, a request filter
// for MCP — because those are two genuinely different pipelines. What must NOT be two is the rule
// they enforce: "an exemption passes, a declared source is authorized, and anything else is
// refused". That rule is here, once, and each PEP supplies only the part it alone can do — reading
// the tenant out of ITS transport (a route value, a query argument, a JSON body field, an MCP tool
// argument). Hence the `resolve` callback: the decision does not know what a route value is, and
// must not learn.
//
// It reads nothing ambient, exactly like ITenantAuthorizer below it: principal and declaration in,
// verdict out.
//
// DEFAULT-DENY IS THE ZERO CASE. A surface with NO declaration is refused — not passed. That is the
// whole point of the work item ("default-deny — свойство того, что происходит с поверхностью, где
// никто ничего не написал"), and it is why the allowlist check happens in the PEP BEFORE this is
// called: today all 217 surfaces are allowlisted, so this branch is unreachable in production and
// stays that way until the ratchet's list is empty. It is written as a refusal anyway, because the
// day the list empties is the day it must already be a refusal.
public static class TenantGate
{
	public static async ValueTask<TenantGateResult> DecideAsync(
		ITenantAuthorizer authorizer,
		ClaimsPrincipal? principal,
		string surfaceKey,
		IReadOnlyList<TenantDeclarationAttribute> declarations,
		Func<TenantFromAttribute, ValueTask<TenantRef>> resolve,
		CancellationToken ct = default)
	{
		if (declarations.Count == 0)
			return TenantGateResult.Refuse(
				$"'{surfaceKey}' declares no target tenant. A surface reachable from outside states in one "
				+ "machine-readable place where its tenant comes from ([TenantFrom]) or which closed exemption "
				+ "class it belongs to ([TenantExempt]); until it does, it is refused.");

		// Two declarations are not "extra safe" — the answer would depend on which reader wins, which is
		// the ambiguity spec `authz-scope-declaration` ("ровно одно") exists to forbid. Refusing is the
		// only reading that cannot be gamed by adding a second, laxer attribute.
		if (declarations.Count > 1)
			return TenantGateResult.Refuse(
				$"'{surfaceKey}' declares its tenant more than once ("
				+ string.Join(" + ", declarations.Select(d => d.Describe()))
				+ "). Exactly one declaration per surface.");

		switch (declarations[0])
		{
			// An exemption suspends the TENANT axis and nothing else: authentication and the scope axis
			// (ScopeRequirement / RequireAuthorization) have already run above this point and stay in force.
			case TenantExemptAttribute:
				return TenantGateResult.Pass;

			case TenantFromAttribute from:
				{
					var tenant = await resolve(from);
					var access = await authorizer.AuthorizeAsync(principal, tenant, ct);
					return access == TenantAccess.Allowed
						? TenantGateResult.Pass
						: TenantGateResult.Refuse(Message(access, from, tenant, surfaceKey));
				}

			// Unreachable while TenantDeclarationAttribute's ctor stays `private protected` (the union is
			// closed by the compiler, pinned by AuthzDeclarationRatchetTests.TheDeclarationType_IsClosed).
			// If a third kind ever appears, it must appear as a REFUSAL, not as a pass-through.
			default:
				return TenantGateResult.Refuse(
					$"'{surfaceKey}' carries an unknown kind of tenant declaration ({declarations[0].GetType().Name}).");
		}
	}

	// The two things a refused caller may learn, and the line between them is the same one
	// TenantAuthorizer draws: a SYNTACTICALLY absent target is reported as absent (it leaks nothing —
	// the caller already knows it named no tenant), while every other outcome collapses into one
	// "not authorized". A named-but-unknown tenant is deliberately indistinguishable from a
	// wrong-tenant denial, so no surface becomes an existence oracle for another tenant's keys.
	static string Message(TenantAccess access, TenantFromAttribute from, TenantRef tenant, string surfaceKey) =>
		access == TenantAccess.NoTenant
			? $"'{surfaceKey}' takes its {from.Tenant.ToString().ToLowerInvariant()} from {from.Describe()}, and this "
				+ "call named none. A request without an authorized tenant does not reach the handler."
			: $"Not authorized for {tenant}.";
}

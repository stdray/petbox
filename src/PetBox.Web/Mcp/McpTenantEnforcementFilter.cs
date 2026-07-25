using System.Collections.Frozen;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;

namespace PetBox.Web.Mcp;

// PEP #2 of 2 (work `authz-default-deny-delivery`, step 3): the MCP plane.
//
// The MCP tool surface is not endpoints — ~100 verbs hang off ONE /mcp endpoint and are addressed by
// name inside the JSON-RPC body — so the endpoint middleware cannot see them and this filter is where
// they are enforced instead.
//
// ORDER IN THE CHAIN IS LOAD-BEARING, and it is the reason this filter is registered exactly where
// Program.cs registers it (the SDK nests each registered filter inside the previous one, so LATER =
// INNERMOST):
//
//   … McpProjectDefaultFilter  →  [this]  →  McpProjectExistsFilter
//
//   * AFTER McpProjectDefaultFilter, because that filter is what RESOLVES the argument: it strips a
//     blank projectKey and injects the key's default on the ~66 tools whose projectKey is required.
//     Authorizing before it would authorize a tenant the call does not actually act on.
//   * BEFORE McpProjectExistsFilter, because AUTHORIZATION MUST PRECEDE THE EXISTENCE CHECK. That
//     filter's own header (McpProjectExistsFilter.cs:114-120) spells out why: an unauthorized caller
//     must get an authorization refusal, never "does not exist" / "did you mean 'kpvotes'?" — the
//     moment the order inverts, the project registry becomes an existence oracle for anyone holding
//     any key. Moving this registration one line down is a security regression, not a reordering.
//
// TODAY IT REFUSES NOTHING: all 97 MCP tools are in TenantEnforcementAllowlist, and an allowlisted
// surface is passed through before its declaration is read. Rollout = deleting lines from that list.
static class McpTenantEnforcementFilter
{
	// tool name → the declarations found on it. Built ONCE by reflection, mirroring the lookup order
	// the inventory uses: the METHOD's own declaration wins, otherwise the tool TYPE's (which declares
	// for every tool in it). Kept honest against the inventory by
	// AuthzDeclarationRatchetTests.TheEnforcementLookup_AgreesWithTheInventory — two readers of the
	// same attributes that disagree would mean the ratchet guards one set and the PEP enforces another.
	static readonly FrozenDictionary<string, IReadOnlyList<TenantDeclarationAttribute>> Declarations =
		Scan(typeof(McpTenantEnforcementFilter).Assembly);

	public static void Register(IMcpRequestFilterBuilder filters) =>
		filters.AddCallToolFilter(next => async (request, ct) =>
		{
			await AssertAuthorizedAsync(request, ct);
			return await next(request, ct);
		});

	static async ValueTask AssertAuthorizedAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
	{
		if (request.Params is not { Name: { Length: > 0 } tool }) return;

		// No authorizer in this host at all (a tool-only harness) — there is nothing to decide with, so
		// there is nothing to enforce. It cannot happen in the real host: Program.cs registers
		// ITenantAuthorizer unconditionally, and CaptiveDependencyTests would fail if it stopped being
		// scoped.
		if (request.Services?.GetService<ITenantAuthorizer>() is not { } authorizer) return;

		var verdict = await DecideAsync(authorizer, request.User, tool, request.Params.Arguments, Declarations, ct);

		// ONE refusal shape, like the endpoint plane's 403: a throw, which McpErrorEnvelopeFilter (the
		// OUTERMOST filter) turns into the structured { error: { type, message } } body every other MCP
		// reject already uses. UnauthorizedAccessException is the type the tool bodies' own Assert*
		// helpers throw, so an agent that already branches on it needs no new case.
		if (!verdict.Allowed) throw new UnauthorizedAccessException(verdict.Message);
	}

	// The whole decision, over the four things it actually depends on — deliberately separated from
	// RequestContext, which cannot be constructed outside a live MCP server. Tests drive this.
	// A tool that is not in the declaration map is UNDECLARED and TenantGate refuses it.
	internal static ValueTask<TenantGateResult> DecideAsync(
		ITenantAuthorizer authorizer,
		ClaimsPrincipal? user,
		string tool,
		IDictionary<string, JsonElement>? arguments,
		IReadOnlyDictionary<string, IReadOnlyList<TenantDeclarationAttribute>> declarations,
		CancellationToken ct = default)
	{
		var surfaceKey = AuthzSurfaceKey.McpPrefix + tool;
		if (TenantEnforcementAllowlist.Contains(surfaceKey)) return ValueTask.FromResult(TenantGateResult.Pass);

		return TenantGate.DecideAsync(
			authorizer,
			user,
			surfaceKey,
			declarations.TryGetValue(tool, out var found) ? found : [],
			from => Resolve(user, arguments, from),
			ct);
	}

	// The transport-specific half: where an MCP call carries its tenant.
	static ValueTask<TenantRef> Resolve(
		ClaimsPrincipal? user, IDictionary<string, JsonElement>? arguments, TenantFromAttribute from)
	{
		var key = from.Source switch
		{
			// A tool ARGUMENT. When the caller supplied none, the tenant is the caller's own default —
			// the same fallback the tool itself will take (ModuleMcp.ResolveProject) and the same one
			// McpProjectExistsFilter validates. Authorizing the argument while the tool acts on the
			// default would check a tenant nobody touches.
			TenantSource.Argument => Argument(arguments, from.Name)
				?? CallerTenant.DefaultProjectOf(user),

			TenantSource.CallerDefault => CallerTenant.DefaultProjectOf(user),

			// Route values and request-body fields do not exist in a JSON-RPC tool call: every input a
			// tool has is an argument. A declaration naming one of these on an MCP tool is a
			// MIS-DECLARATION, and it resolves to nothing — i.e. it is refused, not waved through.
			_ => null,
		};

		return ValueTask.FromResult(
			from.Tenant == TenantKind.Workspace ? TenantRef.Workspace(key) : TenantRef.Project(key));
	}

	static string? Argument(IDictionary<string, JsonElement>? arguments, string name)
	{
		if (arguments is null || !arguments.TryGetValue(name, out var element)) return null;
		if (element.ValueKind != JsonValueKind.String) return null;
		var value = element.GetString();
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}

	// Deliberately the same reflection AuthzSurfaces.FromMcpTools does (which in turn mirrors
	// McpOutputSchema.WithSchemaHonestToolsFromAssembly): [McpServerToolType] on the type,
	// [McpServerTool] on the method, public AND non-public, static AND instance. `inherit: false` on
	// both reads is not an optimisation — TenantFrom/TenantExempt are Inherited = false precisely so a
	// BASE class can never declare on a subclass's behalf; the type-level fallback below is an explicit
	// lookup order, not attribute inheritance.
	internal static FrozenDictionary<string, IReadOnlyList<TenantDeclarationAttribute>> Scan(Assembly assembly)
	{
		var found = new Dictionary<string, IReadOnlyList<TenantDeclarationAttribute>>(StringComparer.Ordinal);

		foreach (var toolType in assembly.GetTypes())
		{
			if (toolType.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

			var onType = toolType.GetCustomAttributes<TenantDeclarationAttribute>(inherit: false).ToList();

			foreach (var method in toolType.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
			{
				if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } tool) continue;

				var onMethod = method.GetCustomAttributes<TenantDeclarationAttribute>(inherit: false).ToList();
				found[tool.Name ?? method.Name] = onMethod.Count > 0 ? onMethod : onType;
			}
		}

		return found.ToFrozenDictionary(StringComparer.Ordinal);
	}
}

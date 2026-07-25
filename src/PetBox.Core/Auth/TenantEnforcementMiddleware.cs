using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace PetBox.Core.Auth;

// PEP #1 of 2 (work `authz-default-deny-delivery`, step 3): the ENDPOINT plane — REST and Razor at
// once.
//
// WHY ONE MIDDLEWARE COVERS BOTH. Razor Pages and minimal APIs are the same thing by the time
// routing has run: both are `Endpoint`s in one `EndpointDataSource`, both carry their declaration in
// `Endpoint.Metadata` (a minimal-API handler's method attributes are copied there by
// RequestDelegateFactory; a PageModel class's attributes by the page action descriptor). So reading
// metadata — rather than hooking MVC filters on one side and endpoint filters on the other — is what
// makes this ONE enforcement point instead of two. It also does not care HOW a route was registered,
// which matters because this tree has no `MapGroup` to hang a group filter on.
//
// WHERE IT SITS: after UseRouting (there is no endpoint before it), after UseAuthentication (there is
// no principal before it) and after UseAuthorization — deliberately last of the three. The scope axis
// (`ScopeRequirement` / `RequireAuthorization`) is orthogonal and already centralized; letting it run
// first means an unauthenticated browser still gets its login redirect and a scope-less key still
// gets its scope refusal, instead of both being re-shaped into a tenant refusal. The tenant axis is
// the LAST gate before the handler, which is exactly what spec `authz-tenant-default-deny` asks for
// ("запрос без авторизованного арендатора не доходит до обработчика").
//
// THE RAZOR UNIT. The inventory counts a PAGE (carrier = the PageModel class), not an endpoint, and
// this PEP is per-REQUEST, i.e. per endpoint. Those converge because AuthzSurfaceKey.OfEndpoint keys
// every endpoint of a page by its ViewEnginePath: a page reachable by several routes yields several
// endpoints, ONE surface key, ONE allowlist entry, and one declaration carried by the class into all
// of them. Mechanized in AuthzDeclarationRatchetTests.EveryEndpointOfAPage_CarriesTheSameDeclaration —
// if a page's endpoints ever disagreed, the per-endpoint decision would stop matching the per-page
// inventory and that test goes red.
//
// TODAY IT REFUSES NOTHING. Every one of the 217 surfaces is in TenantEnforcementAllowlist, and an
// allowlisted surface is passed through before its declaration is even looked at. Rollout is deleting
// lines from that list (step 5), not flipping a flag here.
public sealed class TenantEnforcementMiddleware(RequestDelegate next)
{
	// A body big enough to be a payload rather than a reference. `TenantSource.BodyField` names a
	// tenant KEY, so anything past this is not the thing we are looking for — and buffering an
	// unbounded upload to find out would be a denial-of-service door opened by an authz check.
	const int MaxBufferedBody = 64 * 1024;

	public async Task InvokeAsync(HttpContext context, ITenantAuthorizer authorizer)
	{
		// No endpoint = nothing routed here (static files, a 404, the health probes' short-circuits).
		// There is no surface to have a tenant, and the request reaches no handler of ours.
		var surfaceKey = AuthzSurfaceKey.OfEndpoint(context.GetEndpoint());

		// null also covers /mcp — a transport, not a surface. Its ~100 tools are enforced one by one by
		// McpTenantEnforcementFilter, INSIDE the JSON-RPC dispatch where the tool name exists.
		if (surfaceKey is null || TenantEnforcementAllowlist.Contains(surfaceKey))
		{
			await next(context);
			return;
		}

		var declarations = context.GetEndpoint()!.Metadata.OfType<TenantDeclarationAttribute>().ToList();

		var verdict = await TenantGate.DecideAsync(
			authorizer,
			context.User,
			surfaceKey,
			declarations,
			from => ResolveAsync(context, from),
			context.RequestAborted);

		if (verdict.Allowed)
		{
			await next(context);
			return;
		}

		// ONE refusal shape for every surface on this plane — acceptance criterion 1 of the work card
		// keeps the allow/deny OUTCOME and deliberately drops the four different shapes the hand-written
		// checks used. 403 rather than 401: authentication already ran and had its say above.
		context.Response.StatusCode = StatusCodes.Status403Forbidden;
		context.Response.ContentType = "text/plain; charset=utf-8";
		await context.Response.WriteAsync(verdict.Message, context.RequestAborted);
	}

	// The transport-specific half: where THIS plane reads a tenant key from. The decision (TenantGate)
	// never learns what a route value is.
	static async ValueTask<TenantRef> ResolveAsync(HttpContext context, TenantFromAttribute from)
	{
		var key = from.Source switch
		{
			TenantSource.Route => context.Request.RouteValues.TryGetValue(from.Name, out var value)
				? value?.ToString()
				: null,
			TenantSource.Argument => context.Request.Query.TryGetValue(from.Name, out var query)
				? query.ToString()
				: null,
			TenantSource.BodyField => await BodyFieldAsync(context, from.Name),
			TenantSource.CallerDefault => CallerTenant.DefaultProjectOf(context.User),
			_ => null,
		};

		// An unreadable/absent key becomes an UNRESOLVED TenantRef, and an unresolved TenantRef is a
		// refusal for every principal including a wildcard one — the deny is the zero value of the type,
		// not a branch someone has to remember to write.
		return from.Tenant == TenantKind.Workspace ? TenantRef.Workspace(key) : TenantRef.Project(key);
	}

	// "Цель, пришедшая в теле запроса, проверяется так же строго, как пришедшая из маршрута" — so the
	// body is actually read, not waved through. The request is buffered first and rewound after, so the
	// handler still sees an unconsumed stream.
	//
	// FAIL-CLOSED at every step: not JSON, too large, unparseable, not an object, field missing or not a
	// string → null → an unresolved tenant → refused. This costs nothing today (no non-allowlisted
	// surface exists) and costs one buffered read per declared body-tenant call afterwards.
	static async ValueTask<string?> BodyFieldAsync(HttpContext context, string field)
	{
		if (!context.Request.HasJsonContentType()) return null;
		if (context.Request.ContentLength is > MaxBufferedBody) return null;

		context.Request.EnableBuffering(MaxBufferedBody, MaxBufferedBody);
		try
		{
			using var document = await JsonDocument.ParseAsync(
				context.Request.Body, cancellationToken: context.RequestAborted);
			return document.RootElement.ValueKind == JsonValueKind.Object
				&& document.RootElement.TryGetProperty(field, out var element)
				&& element.ValueKind == JsonValueKind.String
					? element.GetString()
					: null;
		}
		catch (JsonException)
		{
			return null;
		}
		catch (IOException)
		{
			// A chunked body that blows past the buffer limit — no ContentLength to reject it up front.
			return null;
		}
		finally
		{
			if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
		}
	}
}

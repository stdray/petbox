using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
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
// IT REFUSES ON EVERY SURFACE ON THIS PLANE. That sentence used to read "it refuses nothing", and then
// "on every surface but two": all 217 surfaces were allowlisted when this was written, and rollout was
// deleting lines from TenantEnforcementAllowlist (step 5) rather than flipping a flag here. The REST
// wave took 53 of the 55 REST surfaces out, the Razor wave all 65 pages, and work
// `seq-compat-ingest-has-no-principal` the last two — the Seq ingest routes, once the ApiKey scheme
// learned to read their header so they had a ClaimsPrincipal to be judged on. The allowlist is EMPTY,
// so the early-out below is now dead by construction and every request on this plane reaches the
// declaration path. Keep the check anyway: it is the shape that makes re-adding an entry a visible,
// testable act rather than a rewrite of the PEP.
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

		// ONE refusal per SURFACE, and the surface's own plane decides how it is WORDED. Acceptance
		// criterion 1 keeps the allow/deny OUTCOME and drops the four different shapes the hand-written
		// checks used — it does not say every caller must be answered in the same dialect, and on the
		// Razor plane that difference is the whole delivery.
		//
		// A BROWSER'S REFUSAL IS A REDIRECT, NOT A 403 IN THE FACE. Every Razor page in this tree is
		// reached with a session cookie, and the cookie handler's own denial form is a 302 to
		// AccessDeniedPath (/AccessDenied, which renders a real 403 page and explains itself) —
		// deliberately NOT /Login, because re-rendering the sign-in form to somebody who is already
		// signed in is the loop `auth-denied-and-empty-state` was filed for. Answering a plain-text 403
		// here instead would have replaced that page with a bare body on every cross-tenant page hit,
		// and WorkspaceAccessIsolationTests / LogEventDetailsApiTests pin the redirect precisely because
		// it is the user-visible half of the boundary.
		//
		// ForbidAsync() rather than a hand-written 302: it goes through the SAME
		// IAuthenticationService the authorization pipeline above uses, so the shape is whatever that
		// request's credential says it is and this middleware never has to know. Under the "Smart"
		// policy scheme that means a cookie session gets the /AccessDenied redirect (with its
		// ReturnUrl) and an api-key caller on a page-plane surface — /Logs/EventDetails is one, its
		// route lives under /api and a fetch() reaches it — still gets a 403, which is what a
		// programmatic caller can act on. One call, both dialects, no branch on the credential here.
		//
		// THE REST PLANE KEEPS THE PLAIN 403, including for a cookie: the REST wave moved
		// GET /api/logs/{projectKey}/{logName}/live-tail from a 302 to exactly that on purpose
		// (LogLiveTailTests.Cookie_session_cannot_tail_a_project_of_another_workspace pins it), because
		// an EventSource cannot follow a redirect into an HTML page usefully. That decision is not
		// re-opened here.
		if (surfaceKey.StartsWith(AuthzSurfaceKey.PagePrefix, StringComparison.Ordinal))
		{
			await context.ForbidAsync();
			return;
		}

		// 403 rather than 401: authentication already ran and had its say above.
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
	// body is actually read, not waved through.
	//
	// IT MUST READ THE ENCODING THE HANDLER READS, or the declaration is a 403 for everybody. Two
	// encodings carry a named field into a body in this tree, and a surface does not get to be exempt
	// from the tenant axis because of which one it chose:
	//
	//   * FORM (application/x-www-form-urlencoded / multipart) — the [FromForm] endpoints. ReadFormAsync
	//     caches the parsed form on the request feature, so the handler's own [FromForm] binding reuses
	//     this read rather than paying for a second one, and there is nothing to rewind. The size limits
	//     are the framework's own FormOptions, i.e. the same ones the handler was already subject to.
	//   * JSON — buffered and rewound, so the handler still sees an unconsumed stream.
	//
	// This is the transport half of the PEP (see ResolveAsync), not a new TenantSource: the KIND of
	// tenant and the field NAME are the same question in both encodings, and only the wire differs.
	// TenantSource is closed by a deliberate edit for exactly the cases where the DECISION changes.
	//
	// TWO READING RULES, both there so the PEP and the model binder cannot disagree about which field
	// carries the tenant:
	//   * a dotted name walks nested objects ("tags.project" — HealthApi's tenant lives one level down);
	//   * matching is case-INSENSITIVE, because minimal-API JSON binding is
	//     (JsonSerializerDefaults.Web) and form binding is. A PEP that matched case-sensitively would
	//     refuse the caller that sends `ProjectKey` while the handler happily bound it.
	//
	// FAIL-CLOSED at every step: unsupported content type, too large, unparseable, not an object, field
	// missing, not a string, or SEVERAL case-variants of the name carrying DIFFERENT values → null → an
	// unresolved tenant → refused.
	static async ValueTask<string?> BodyFieldAsync(HttpContext context, string field)
	{
		if (context.Request.HasFormContentType)
		{
			try
			{
				var form = await context.Request.ReadFormAsync(context.RequestAborted);
				return Blank(form.TryGetValue(field, out var values) ? values.ToString() : null);
			}
			catch (InvalidDataException) { return null; }
			catch (IOException) { return null; }
		}

		if (!context.Request.HasJsonContentType()) return null;
		if (context.Request.ContentLength is > MaxBufferedBody) return null;

		context.Request.EnableBuffering(MaxBufferedBody, MaxBufferedBody);
		try
		{
			using var document = await JsonDocument.ParseAsync(
				context.Request.Body, cancellationToken: context.RequestAborted);
			return Blank(JsonPath(document.RootElement, field));
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

	// Walks a dot-separated path of case-insensitive object property names down to a STRING leaf.
	static string? JsonPath(JsonElement root, string path)
	{
		var element = root;
		foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (element.ValueKind != JsonValueKind.Object) return null;
			if (Property(element, segment) is not { } next) return null;
			element = next;
		}

		return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
	}

	// One property by case-insensitive name. Two variants that AGREE are the same answer; two that
	// DISAGREE are an ambiguity a caller could steer, so they resolve to nothing and the call is refused.
	static JsonElement? Property(JsonElement owner, string name)
	{
		JsonElement? found = null;
		foreach (var property in owner.EnumerateObject())
		{
			if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
			if (found is null) { found = property.Value; continue; }

			var first = found.Value.ValueKind == JsonValueKind.String ? found.Value.GetString() : null;
			var second = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
			if (!string.Equals(first, second, StringComparison.Ordinal)) return null;
		}

		return found;
	}

	static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

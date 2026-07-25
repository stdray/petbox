using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;

namespace PetBox.Tests.Architecture;

// THE ENUMERATION of every surface that can be reached from outside the process, and what each one
// DECLARES about its tenant (spec `authz-scope-declaration`, work `authz-default-deny-delivery`
// step 1).
//
// Why an enumeration at all: default-deny is not a property of where the check is written, it is a
// property of what happens on a surface where NOBODY wrote anything. That question can only be
// answered by something that can list every surface. Three independent human counts of the MCP tool
// surface alone came back 92 / 100 / 122 — so the inventory is a MECHANISM, and any number quoted
// about this system's attack surface has to be produced by it.
//
// TWO DISCOVERY PLANES, because there are two kinds of surface and they are not reachable the same way:
//
//   * REST + Razor — EndpointDataSource, off a BOOTED host. There is no static way to get this list:
//     endpoints are mapped by executing Program.Configure (feature flags, environment, `if` blocks),
//     so a reflection sweep over *Api classes would enumerate the code that maps endpoints, not the
//     endpoints. It also silently misses anything mapped inline in Program.cs (/health, /version,
//     the .well-known probes, logout) — which is where four surfaces actually live.
//
//   * MCP — reflection over [McpServerToolType] + [McpServerTool], mirroring EXACTLY what
//     McpOutputSchema.WithSchemaHonestToolsFromAssembly does at startup. The whole tool surface hangs
//     off ONE endpoint (/mcp), so the endpoint plane sees one door where there are ~100 verbs; the
//     tools are the real surfaces and they are reached by name inside the JSON-RPC body.
//     `TheMcpSweep_MatchesTheToolsTheServerActuallyRegisters` cross-checks this reflection against the
//     McpServerTool instances the running host has in DI — i.e. against the live registration, not
//     against a second copy of the same assumption.
public enum AuthzTransport
{
	Rest,
	Razor,
	Mcp,
}

// One externally reachable surface + every tenant declaration found on it (0 = undeclared, which is
// what the ratchet is about; 2 = contradictory, which is also a defect).
public sealed record AuthzSurface(
	AuthzTransport Transport,
	string Id,
	string Owner,
	IReadOnlyList<TenantDeclarationAttribute> Declarations)
{
	// The stable identity used by the ratchet's allowlist. Prefixed by transport so a route and a
	// tool can never collide, and so the allowlist reads as an inventory.
	public string Key => Transport switch
	{
		AuthzTransport.Rest => $"rest:{Id}",
		AuthzTransport.Razor => $"page:{Id}",
		AuthzTransport.Mcp => $"mcp:{Id}",
		_ => throw new ArgumentOutOfRangeException(nameof(Transport)),
	};

	public string Describe() => Declarations.Count switch
	{
		0 => "(undeclared)",
		1 => Declarations[0].Describe(),
		_ => "CONTRADICTORY: " + string.Join(" + ", Declarations.Select(d => d.Describe())),
	};
}

public static class AuthzSurfaces
{
	// ── THE ENDPOINT PLANE (REST + Razor) ────────────────────────────────────────────────────────

	// Takes the ENDPOINTS rather than the data source: the same reader then works against a booted
	// host's EndpointDataSource and against a throwaway app built in a test (the carrier proof), and
	// nothing here depends on how the host chose to compose its data sources.
	public static IReadOnlyList<AuthzSurface> FromEndpoints(IEnumerable<Endpoint> endpoints)
	{
		var surfaces = new List<AuthzSurface>();
		var pages = new Dictionary<string, (string Owner, List<TenantDeclarationAttribute> Declarations)>(
			StringComparer.Ordinal);

		foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
		{
			if (IsMcpTransport(endpoint)) continue;

			var declarations = endpoint.Metadata.OfType<TenantDeclarationAttribute>().ToList();

			// A Razor page is ONE surface even when it is mapped by several endpoints (a page with a
			// route template, an /Index page reachable with and without its name). The declaration
			// carrier is the PageModel CLASS, so the page — not the endpoint — is the unit that can
			// declare, and keying the inventory by endpoint would ask one class to be declared twice.
			if (endpoint.Metadata.GetMetadata<PageActionDescriptor>() is { } page)
			{
				var path = page.ViewEnginePath;
				if (!pages.TryGetValue(path, out var entry))
					pages[path] = entry = (OwnerOfPage(page), []);
				entry.Declarations.AddRange(declarations);
				continue;
			}

			surfaces.Add(new AuthzSurface(
				AuthzTransport.Rest, RestId(endpoint), OwnerOfEndpoint(endpoint), declarations));
		}

		surfaces.AddRange(pages
			.Select(p => new AuthzSurface(
				AuthzTransport.Razor, p.Key, p.Value.Owner, p.Value.Declarations.Distinct().ToList())));

		return [.. surfaces.OrderBy(s => s.Key, StringComparer.Ordinal)];
	}

	// "METHOD[|METHOD] /route/{pattern}" — the identity a caller actually addresses. Methods are
	// sorted so the id does not depend on the order MapMethods was handed them.
	static string RestId(RouteEndpoint endpoint)
	{
		var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
		var verb = methods.Count == 0 ? "ANY" : string.Join("|", methods.Order(StringComparer.Ordinal));
		var pattern = endpoint.RoutePattern.RawText ?? "";
		return $"{verb} {(pattern.StartsWith('/') ? pattern : "/" + pattern)}";
	}

	// THE ONE EXCLUSION, and it is by construction rather than by allowlist — the same reasoning as
	// DbLayerGuardTests' composition-root exclusion. /mcp is not a surface with a tenant: it is a
	// TRANSPORT whose body names one of ~100 tools, each of which is enumerated on the MCP plane
	// below. Allowlisting it would claim it is debt somebody should pay off; it is not. If the MCP
	// sweep ever stops running, the guard-the-guard test is what fails — not this exclusion.
	static bool IsMcpTransport(RouteEndpoint endpoint)
	{
		var pattern = endpoint.RoutePattern.RawText ?? "";
		return pattern is "/mcp" or "mcp"
			|| pattern.StartsWith("/mcp/", StringComparison.Ordinal)
			|| pattern.StartsWith("mcp/", StringComparison.Ordinal);
	}

	// Who to go to when a surface is undeclared. For a minimal-API endpoint the handler's method is
	// in the metadata; the outermost declaring type is the *Api class that mapped it (a lambda is
	// lowered onto a nested display class, exactly as in DbLayerGuardTests).
	static string OwnerOfEndpoint(RouteEndpoint endpoint)
	{
		if (endpoint.Metadata.GetMetadata<MethodInfo>() is { } method)
		{
			var owner = method.DeclaringType;
			while (owner?.DeclaringType is { } outer) owner = outer;
			return owner?.FullName ?? method.Name;
		}

		return endpoint.DisplayName ?? "(unknown)";
	}

	static string OwnerOfPage(PageActionDescriptor page) =>
		(page as Microsoft.AspNetCore.Mvc.RazorPages.CompiledPageActionDescriptor)?.ModelTypeInfo?.FullName
		?? page.RelativePath;

	// ── THE MCP PLANE ────────────────────────────────────────────────────────────────────────────

	// Mirrors McpOutputSchema.WithSchemaHonestToolsFromAssembly's reflection, deliberately
	// line-for-line: [McpServerToolType] on the type, [McpServerTool] on the method, public AND
	// non-public, static AND instance. If registration ever changes shape, the DI cross-check in the
	// ratchet's guard-the-guard is what catches the divergence.
	//
	// LOOKUP ORDER for the declaration: the METHOD's own declaration wins; otherwise the tool TYPE's,
	// which declares for every tool in it (TasksTools' 30 verbs all read the same `projectKey`
	// argument — making each of them repeat it is how one of them ends up different by accident).
	// This is an explicit fallback, not attribute inheritance: TenantFrom/TenantExempt are
	// Inherited = false precisely so a BASE class can never declare on a subclass's behalf.
	public static IReadOnlyList<AuthzSurface> FromMcpTools(Assembly assembly)
	{
		var surfaces = new List<AuthzSurface>();

		foreach (var toolType in assembly.GetTypes())
		{
			if (toolType.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

			var onType = toolType.GetCustomAttributes<TenantDeclarationAttribute>(inherit: false).ToList();

			foreach (var method in toolType.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
			{
				if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } tool) continue;

				var onMethod = method.GetCustomAttributes<TenantDeclarationAttribute>(inherit: false).ToList();

				surfaces.Add(new AuthzSurface(
					AuthzTransport.Mcp,
					tool.Name ?? method.Name,
					$"{toolType.FullName}.{method.Name}",
					onMethod.Count > 0 ? onMethod : onType));
			}
		}

		return [.. surfaces.OrderBy(s => s.Key, StringComparer.Ordinal)];
	}

	// ── THE MACHINE INVENTORY ────────────────────────────────────────────────────────────────────

	// The full list, as text. Point 4 of the work card: the enumeration must be able to EMIT the
	// whole surface, not just answer yes/no about it — a number in a report that nobody can
	// reproduce is the human count all over again.
	public static string Render(IEnumerable<AuthzSurface> surfaces)
	{
		var all = surfaces.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
		var lines = new List<string>
		{
			$"# PetBox authz surface inventory — {all.Count} surfaces",
			"#",
		};
		lines.AddRange(all
			.GroupBy(s => s.Transport)
			.OrderBy(g => g.Key)
			.Select(g => $"#   {g.Key}: {g.Count()}"));
		lines.Add("#");
		lines.AddRange(all.Select(s => $"{s.Key}\t{s.Describe()}\t{s.Owner}"));
		return string.Join(Environment.NewLine, lines) + Environment.NewLine;
	}
}

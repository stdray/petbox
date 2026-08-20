using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Models;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Auth;

// ONE KEY, ONE SCOPE, ONE ANSWER — spec `access-permission-uniform`, work
// `scope-claims-canonicalization`.
//
// "Одно и то же полномочие одного и того же ключа ДОЛЖНО давать одинаковое решение о доступе
// независимо от поверхности, через которую пришёл вызов; каноническая запись полномочия объявляется
// каталогом полномочий, а не местом сравнения."
//
// WHAT WAS BROKEN. The `scopes` claim was read and compared by SIXTEEN hand-rolled copies that
// disagreed on TWO axes at once, so "the same key, the same scope, a different verdict" was
// reachable two independent ways:
//
//   * CASE — ScopeAuthorizationHandler (the REST policy) compared OrdinalIgnoreCase; ModuleMcp
//     (the MCP guard), KeyIssuer and the other thirteen compared Ordinal. A key holding
//     `Data:Read` was ALLOWED over REST and DENIED over MCP.
//   * SEPARATORS — the REST policy split on ',' alone; the MCP guard split on ',' ' ' ';'. A key
//     holding `data:read logs:query` (which ApiKeyScopes.Validate accepts verbatim, because IT
//     splits on all three) was one opaque token to REST and two scopes to MCP, so the SAME key was
//     DENIED over REST and ALLOWED over MCP — the asymmetry pointing the other way.
//
// WHY A COINCIDENCE TEST IS NOT ENOUGH, and why this file drives the REAL components rather than
// asserting on ApiKeyScopes.Granted directly: the defect was never in any one comparison, it was in
// there being several. A unit test of the catalog helper would stay green while a surface quietly
// kept its own copy. So every case below is asked of BOTH transports and the verdicts are compared
// to EACH OTHER first — the equivalence is the assertion, the expected value is only the second
// half. The principal is minted by the REAL ApiKeyAuthenticationHandler, so the claim NAMES are
// under test too (see the `yb:` trap test at the bottom).
public sealed class ScopeClaimTransportEquivalenceTests
{
	// stored ............ the ApiKeys.Scopes column value, verbatim
	// required .......... the scope the surface demands
	// expected .......... the ONE verdict both transports must reach
	[Theory]
	// ── the ordinary cases: both transports always agreed on these ──────────────────────────────
	[InlineData("data:read", ApiKeyScopes.DataRead, true)]
	[InlineData("data:write", ApiKeyScopes.DataRead, false)]
	[InlineData("", ApiKeyScopes.DataRead, false)]
	[InlineData("data:read,logs:query", ApiKeyScopes.LogsQuery, true)]

	// ── THE CASE AXIS. REST said Allow, MCP said Deny. Ordinal is the canon (see the class header
	//    of ApiKeyScopes), so the shared answer is DENY: the catalog holds `data:read` and nothing
	//    else, and a gate must not recognize a permission the catalog does not.
	[InlineData("Data:Read", ApiKeyScopes.DataRead, false)]
	[InlineData("DATA:READ", ApiKeyScopes.DataRead, false)]
	[InlineData("data:READ", ApiKeyScopes.DataRead, false)]
	// …and the mixed set: the correctly-spelled sibling must still be honoured, so a casing typo
	// costs exactly the scope it was typed on and nothing else.
	[InlineData("Data:Read,logs:query", ApiKeyScopes.LogsQuery, true)]
	[InlineData("Data:Read,logs:query", ApiKeyScopes.DataRead, false)]

	// ── THE SEPARATOR AXIS. REST said Deny, MCP said Allow — the asymmetry pointing the other way,
	//    and the reason "just make REST Ordinal" would have fixed half a bug. The catalog's own
	//    Validate() accepts all three separators when MINTING, so enforcement must read all three.
	[InlineData("data:read logs:query", ApiKeyScopes.LogsQuery, true)]
	[InlineData("data:read logs:query", ApiKeyScopes.DataRead, true)]
	[InlineData("data:read;logs:query", ApiKeyScopes.LogsQuery, true)]
	[InlineData(" data:read , logs:query ", ApiKeyScopes.DataRead, true)]
	[InlineData("data:read\tlogs:query", ApiKeyScopes.LogsQuery, false)] // tab is NOT a separator
	public async Task RestAndMcpReachTheSameVerdict(string stored, string required, bool expected)
	{
		var user = await PrincipalForAsync(stored);

		var rest = await RestAllowsAsync(user, required);
		var mcp = McpAllows(user, required);

		// THE assertion of this file: the two transports agree. Stated before the expected value on
		// purpose — a future edit that breaks the equivalence fails HERE, naming both verdicts.
		rest.Should().Be(mcp,
			$"a key whose ApiKeys.Scopes column holds '{stored}' must get ONE answer for '{required}' "
			+ $"— REST(ScopeRequirement) said {rest}, MCP(ModuleMcp.AssertScope) said {mcp}. "
			+ "Spec access-permission-uniform: the catalog declares the permission, not the comparison site.");

		rest.Should().Be(expected,
			$"'{stored}' vs '{required}' — the canonical verdict (Ordinal compare, ',' ' ' ';' separators)");
	}

	// KeyIssuer is the THIRD reader, and the sharpest one: its answer decides whether a caller may
	// hand out root-equivalent scopes (work `workspaceadmin-self-issue-admin-provision-root`). It
	// used to carry a private HasScope copy, so it could in principle have drifted from both gates.
	//
	// THE CASE ROW IS THE SECURITY ROW. If enforcement were case-INsensitive while the catalog's
	// IsPrivileged/PrivilegedIn stayed Ordinal — which they are — then `Admin:Provision` would be a
	// scope that ENFORCES as admin:provision yet classifies as unprivileged at the grant gate. This
	// pins both halves to the same reading, so that split cannot reappear.
	[Theory]
	[InlineData("admin:provision", true)]
	[InlineData("admin:provision,tasks:read", true)]
	[InlineData("admin:provision tasks:read", true)]
	[InlineData("Admin:Provision", false)]
	[InlineData("ADMIN:PROVISION", false)]
	[InlineData("tasks:read", false)]
	[InlineData("", false)]
	public async Task KeyIssuerAgreesWithBothGates(string stored, bool expectedPrivileged)
	{
		var user = await PrincipalForAsync(stored);

		var issuer = KeyIssuer.From(user);
		var rest = await RestAllowsAsync(user, ApiKeyScopes.AdminProvision);
		var mcp = McpAllows(user, ApiKeyScopes.AdminProvision);

		issuer.MayGrantPrivileged.Should().Be(expectedPrivileged);
		issuer.MayGrantPrivileged.Should().Be(rest, "KeyIssuer and the REST policy read one claim");
		issuer.MayGrantPrivileged.Should().Be(mcp, "KeyIssuer and the MCP guard read one claim");

		// And the catalog classifies the SAME string the same way the gates enforced it. A spelling
		// the gates honour must be one the grant gate knows is privileged.
		ApiKeyScopes.Split(stored).Any(ApiKeyScopes.IsPrivileged)
			.Should().Be(expectedPrivileged,
				"the enforcement gates and ApiKeyScopes.IsPrivileged must classify one spelling identically — "
				+ "a scope that enforces as admin:provision but reads as unprivileged at the grant gate is "
				+ "the escalation workspaceadmin-self-issue-admin-provision-root closed");
	}

	// THE `yb:` TRAP, pinned. PetBoxClaims.ProjectKey/.Scopes declare `yb:project`/`yb:scopes` and
	// have NEVER been emitted — they are [Obsolete] dead letters kept only so the names stay
	// greppable. The tempting one-line "cleanup" (point the readers at the declared constants)
	// compiles, reviews clean, and denies every live key, because the token simply has no such
	// claim. This test is what makes that edit loud.
	[Fact]
	public async Task TheMintedTokenCarriesBareClaimNames_NotThePrefixedOnes()
	{
		var ticket = await AuthenticateAsync(new ApiKey
		{
			Key = "k",
			ProjectKey = "kpvotes",
			Scopes = "data:read",
		});
		var claims = ticket!.Principal.Claims.ToList();

		claims.Should().Contain(c => c.Type == "project" && c.Value == "kpvotes");
		claims.Should().Contain(c => c.Type == "scopes" && c.Value == "data:read");
		ApiKeyAuthenticationHandler.ProjectClaim.Should().Be("project");
		ApiKeyAuthenticationHandler.ScopesClaim.Should().Be("scopes");

		claims.Should().NotContain(c => c.Type.StartsWith("yb:", StringComparison.Ordinal),
			"the api-key identity carries BARE claim names; `yb:` names belong to the cookie identity "
			+ "and PetBoxClaims.ProjectKey/.Scopes were never emitted by anything");
	}

	// THE RATCHET (shape borrowed from AuthzDeclarationRatchetTests / SandboxContainmentCallSiteGuard):
	// the acceptance criterion of this work is a COUNT, so a machine keeps it rather than a comment.
	// Exactly one site in src/ may spell each claim name — its declaration. Every other reader must
	// import the constant, which is what makes a future rename a compiler problem instead of a grep
	// problem, and what stops a seventeenth private copy of the comparison from appearing.
	//
	// Only the claim-READ/WRITE shapes are matched, so the many unrelated `"project"` strings in this
	// codebase (health tags, memory scope labels, the KQL `project` operator, [TenantFrom] route and
	// argument names) are correctly ignored: those are different concepts that happen to share a word.
	[Theory]
	[InlineData("scopes", "ScopesClaim")]
	[InlineData("project", "ProjectClaim")]
	public void OnlyTheDeclarationSpellsTheClaimName(string claim, string constant)
	{
		// `c.Type == "x"`, `FindFirst("x")`, `FindFirstValue("x")`, `Claim(ctx, "x")`, `new("x", …)`
		var read = new Regex(
			$$"""(c\.Type\s*==\s*|FindFirst\(|FindFirstValue\(|Claim\(ctx,\s*|new\()"{{claim}}"|"{{claim}}"\s*\)\?\.Value""",
			RegexOptions.Compiled);

		var offenders = Directory
			.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
			.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				&& !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.Select(p => (Path: p, Code: StripComments(File.ReadAllText(p))))
			.Where(f => read.IsMatch(f.Code))
			.Select(f => Path.GetRelativePath(RepoRoot(), f.Path))
			.OrderBy(p => p, StringComparer.Ordinal)
			.ToList();

		offenders.Should().BeEmpty(
			$"the `{claim}` claim name is declared ONCE, as ApiKeyAuthenticationHandler.{constant}, and every "
			+ $"reader imports it. These files still spell the literal: {string.Join(", ", offenders)}");
	}

	// ── plumbing ─────────────────────────────────────────────────────────────────────────────────

	// The REST plane: the real ScopeAuthorizationHandler against a real ScopeRequirement — the same
	// pair AddPolicy(...RequireScope) builds for every [Authorize(Policy=…)] REST surface.
	static async Task<bool> RestAllowsAsync(ClaimsPrincipal user, string required)
	{
		var requirement = new ScopeRequirement(required);
		var context = new AuthorizationHandlerContext([requirement], user, resource: null);
		await new ScopeAuthorizationHandler().HandleAsync(context);
		return context.HasSucceeded;
	}

	// The MCP plane: the real ModuleMcp.AssertScope, reached exactly as a tool body reaches it —
	// through an IHttpContextAccessor carrying the authenticated principal.
	static bool McpAllows(ClaimsPrincipal user, string required)
	{
		var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
		try
		{
			ModuleMcp.AssertScope(accessor, required);
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	// The principal both planes are asked about — minted by the REAL handler from a REAL key row, so
	// the claim names in the token are the handler's own and not this test's guess at them.
	static async Task<ClaimsPrincipal> PrincipalForAsync(string storedScopes)
	{
		var ticket = await AuthenticateAsync(new ApiKey
		{
			Key = "k",
			ProjectKey = "kpvotes",
			Scopes = storedScopes,
		});
		return ticket!.Principal;
	}

	static async Task<AuthenticationTicket?> AuthenticateAsync(ApiKey key)
	{
		using var services = new ServiceCollection()
			.AddOptions()
			.AddLogging()
			.AddSingleton<IApiKeyLookup>(new StubLookup(key))
			.BuildServiceProvider();

		var ctx = new DefaultHttpContext { RequestServices = services };
		ctx.Request.Headers[ApiKeyAuthenticationHandler.ApiKeyHeader] = key.Key;

		var handler = new ApiKeyAuthenticationHandler(
			services.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
			services.GetRequiredService<ILoggerFactory>(),
			UrlEncoder.Default);
		await handler.InitializeAsync(
			new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler)),
			ctx);

		var result = await handler.AuthenticateAsync();
		result.Succeeded.Should().BeTrue();
		return result.Ticket;
	}

	// Comments stripped before the sweep: this file's own prose spells both claim names repeatedly,
	// and so do the headers of the files under test. DbLayerGuardTests paid for that lesson already.
	static string StripComments(string source)
	{
		var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
		return Regex.Replace(noBlock, @"//[^\n]*", "");
	}

	static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, "src", "PetBox.Web"))) return dir;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("repo root (with src/PetBox.Web) not found walking up from the test bin.");
	}

	sealed class StubLookup(ApiKey key) : IApiKeyLookup
	{
		public ApiKey? FindByKey(string k) => k == key.Key ? key : null;
	}
}

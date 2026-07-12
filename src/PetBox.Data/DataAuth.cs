using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Data;

namespace PetBox.Data;

// Cross-checks the ApiKey's "project" claim against the URL's projectKey
// segment. petbox keys are project-level — a key issued for project X must
// not reach project Y's DataDbs through a crafted URL.
//
// ApiKeyAuthenticationHandler emits the claim under the literal "project"
// (not the PetBoxClaims constant); matches the same convention as AuthApi.
//
// AuthorizesAsync also enforces the sandbox write gate (spec
// work/smoke-writes-into-real-projects): a SandboxOnly key additionally needs projectKey to name
// a Project.Sandbox = true row.
internal static class DataAuth
{
	public static async Task<(bool Ok, IResult? Forbid)> AuthorizeProjectAsync(
		HttpContext ctx, string projectKey, IProjectCatalog catalog, CancellationToken ct)
	{
		if (!await ProjectScope.AuthorizesAsync(ctx.User, projectKey, catalog, ct))
			return (false, Results.Forbid());
		return (true, null);
	}
}

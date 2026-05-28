using Microsoft.AspNetCore.Http;

namespace PetBox.Data;

// Cross-checks the ApiKey's "project" claim against the URL's projectKey
// segment. petbox keys are project-level — a key issued for project X must
// not reach project Y's DataDbs through a crafted URL.
//
// ApiKeyAuthenticationHandler emits the claim under the literal "project"
// (not the PetBoxClaims constant); matches the same convention as AuthApi.
internal static class DataAuth
{
	public static bool AuthorizeProject(HttpContext ctx, string projectKey, out IResult forbid)
	{
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (string.IsNullOrEmpty(claim) || !string.Equals(claim, projectKey, StringComparison.Ordinal))
		{
			forbid = Results.Forbid();
			return false;
		}
		forbid = null!;
		return true;
	}
}

using PetBox.Core.Auth;

namespace PetBox.Tests.Auth;

// A normal `project` claim authorizes only its own project; the cross-project wildcard
// "*" authorizes any; null/empty authorizes nothing.
public sealed class ProjectScopeTests
{
	[Theory]
	[InlineData("kpvotes", "kpvotes", true)]   // exact match
	[InlineData("kpvotes", "other", false)]    // mismatch
	[InlineData("*", "kpvotes", true)]          // wildcard -> any project
	[InlineData("*", "$system", true)]
	[InlineData("", "kpvotes", false)]          // empty claim -> denied
	[InlineData(null, "kpvotes", false)]        // missing claim -> denied
	public void Authorizes(string? claim, string projectKey, bool expected) =>
		ProjectScope.Authorizes(claim, projectKey).Should().Be(expected);
}

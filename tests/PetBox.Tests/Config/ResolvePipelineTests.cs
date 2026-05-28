using PetBox.Config;
using PetBox.Core.Models;

namespace PetBox.Tests.Config;

public sealed class ResolvePipelineTests
{
	static ConfigBinding B(long id, string path, string value, string tags) => new()
	{
		Id = id,
		Path = path,
		Value = value,
		Tags = tags,
		CreatedAt = DateTime.UtcNow,
		UpdatedAt = DateTime.UtcNow,
	};

	[Fact]
	public void Resolve_NoBindings_ReturnsNull()
	{
		var result = ResolvePipeline.Resolve("/a", ["env:dev"], []);
		result.Should().BeNull();
	}

	[Fact]
	public void Resolve_NoPathMatch_ReturnsNull()
	{
		var bindings = new[] { B(1, "/a", "v", "env:dev") };
		var result = ResolvePipeline.Resolve("/b", ["env:dev"], bindings);
		result.Should().BeNull();
	}

	[Fact]
	public void Resolve_ExactMatch_Wins()
	{
		var bindings = new[] { B(1, "/a", "value", "env:dev") };
		var result = ResolvePipeline.Resolve("/a", ["env:dev"], bindings);
		result.Should().Be("value");
	}

	[Fact]
	public void Resolve_RequestSupersetOfBinding_Matches()
	{
		var bindings = new[] { B(1, "/a", "value", "env:dev") };
		var result = ResolvePipeline.Resolve("/a", ["env:dev", "service:bot"], bindings);
		result.Should().Be("value");
	}

	[Fact]
	public void Resolve_BindingHasExtraTag_DoesNotMatch()
	{
		// The original bug: binding with tags NOT in request used to match anyway.
		var bindings = new[]
		{
			B(1, "/a", "generic", "env:dev"),
			B(2, "/a", "prod-specific", "env:dev,env:prod"),
		};
		// Request asks for env:dev only. Binding 2 has env:prod which is NOT in request → must not match.
		var result = ResolvePipeline.Resolve("/a", ["env:dev"], bindings);
		result.Should().Be("generic");
	}

	[Fact]
	public void Resolve_MostSpecificSubsetWins()
	{
		var bindings = new[]
		{
			B(1, "/a", "generic", "env:dev"),
			B(2, "/a", "specific", "env:dev,service:bot"),
		};
		var result = ResolvePipeline.Resolve("/a", ["env:dev", "service:bot"], bindings);
		result.Should().Be("specific");
	}

	[Fact]
	public void Resolve_AmbiguousMatch_Throws()
	{
		// Two bindings, both subsets of request, with same specificity → ambiguous.
		var bindings = new[]
		{
			B(1, "/a", "first", "env:dev"),
			B(2, "/a", "second", "service:bot"),
		};
		var act = () => ResolvePipeline.Resolve("/a", ["env:dev", "service:bot"], bindings);
		act.Should().Throw<AmbiguousConfigException>()
			.Which.CandidateBindingIds.Should().BeEquivalentTo([1L, 2L]);
	}

	[Fact]
	public void Resolve_EmptyTagBinding_MatchesAnyRequest()
	{
		var bindings = new[] { B(1, "/a", "fallback", "") };
		var result = ResolvePipeline.Resolve("/a", ["env:dev"], bindings);
		result.Should().Be("fallback");
	}

	[Fact]
	public void Resolve_EmptyTagBinding_VsSpecific_SpecificWins()
	{
		var bindings = new[]
		{
			B(1, "/a", "fallback", ""),
			B(2, "/a", "matched", "env:dev"),
		};
		var result = ResolvePipeline.Resolve("/a", ["env:dev"], bindings);
		result.Should().Be("matched");
	}

	[Fact]
	public void Resolve_PathCaseInsensitive()
	{
		var bindings = new[] { B(1, "/A", "value", "env:dev") };
		var result = ResolvePipeline.Resolve("/a", ["env:dev"], bindings);
		result.Should().Be("value");
	}

	[Fact]
	public void Resolve_TagCaseInsensitive()
	{
		var bindings = new[] { B(1, "/a", "value", "ENV:DEV") };
		var result = ResolvePipeline.Resolve("/a", ["env:dev"], bindings);
		result.Should().Be("value");
	}

	[Fact]
	public void ResolveDetailed_ReturnsBindingAndSpecificity()
	{
		var bindings = new[]
		{
			B(1, "/a", "generic", "env:dev"),
			B(2, "/a", "specific", "env:dev,service:bot"),
		};
		var match = ResolvePipeline.ResolveDetailed("/a", ["env:dev", "service:bot"], bindings);
		match.Should().NotBeNull();
		match!.Binding.Id.Should().Be(2);
		match.Specificity.Should().Be(2);
	}

	[Fact]
	public void ResolveDetailed_NoMatch_ReturnsNull()
	{
		var match = ResolvePipeline.ResolveDetailed("/missing", ["env:dev"], []);
		match.Should().BeNull();
	}
}

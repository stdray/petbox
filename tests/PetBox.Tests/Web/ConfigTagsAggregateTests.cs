using PetBox.Web.Pages.Config;

namespace PetBox.Tests.Web;

// Repro for the /config/tags "Used values" / "In use, not declared" columns rendering empty:
// the aggregator split binding tags on '=' but canonical binding tags are "namespace:value"
// tokens (as Config/Index.ParseTags splits). Splitting on ':' now surfaces the values.
public sealed class ConfigTagsAggregateTests
{
	[Fact]
	public void AggregateUsedValues_NamespaceValueTag_PopulatesUsedValues()
	{
		var result = TagsModel.AggregateUsedValues(["uiaudit:probe"]);

		result.Should().ContainKey("uiaudit");
		result["uiaudit"].Should().ContainSingle().Which.Should().Be("probe");
	}

	[Fact]
	public void AggregateUsedValues_MultipleBindings_UnionsDistinctSortedValues()
	{
		var result = TagsModel.AggregateUsedValues(
		[
			"env:dev,ws:$system",
			"env:prod",
			"env:dev",
		]);

		result["env"].Should().Equal("dev", "prod");
		result["ws"].Should().Equal("$system");
	}

	[Fact]
	public void AggregateUsedValues_BareNamespaceWithoutColon_IsSkipped()
	{
		var result = TagsModel.AggregateUsedValues(["uiaudit", "env:dev"]);

		result.Should().NotContainKey("uiaudit");
		result.Should().ContainKey("env");
	}
}

using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

public sealed class TaskNodeIdTests
{
	[Fact]
	public void ToKey_BuildsPathByDepth()
	{
		new TaskNodeId("log").ToKey().Should().Be("log");
		new TaskNodeId("log", "ingest").ToKey().Should().Be("log/ingest");
		new TaskNodeId("log", "ingest", "endpoint").ToKey().Should().Be("log/ingest/endpoint");
	}

	[Fact]
	public void Depth_And_ParentKey_TrackLevels()
	{
		new TaskNodeId("log").Depth.Should().Be(1);
		new TaskNodeId("log").ParentKey.Should().BeNull();

		var wave = new TaskNodeId("log", "ingest");
		wave.Depth.Should().Be(2);
		wave.ParentKey.Should().Be("log");

		var task = new TaskNodeId("log", "ingest", "endpoint");
		task.Depth.Should().Be(3);
		task.ParentKey.Should().Be("log/ingest");
	}

	[Fact]
	public void Parse_IsInverseOfToKey()
	{
		foreach (var key in new[] { "log", "log/ingest", "log/ingest/endpoint" })
			TaskNodeId.Parse(key).ToKey().Should().Be(key);
	}

	[Fact]
	public void Parse_DecomposesLevels()
	{
		var id = TaskNodeId.Parse("phase1/wave2/task3");
		id.PhaseKey.Should().Be("phase1");
		id.WaveKey.Should().Be("wave2");
		id.TaskKey.Should().Be("task3");
	}

	[Fact]
	public void Constructor_NormalisesBlankLevelsToNull()
	{
		var id = new TaskNodeId("log", "", "  ");
		id.Depth.Should().Be(1);
		id.WaveKey.Should().BeNull();
		id.TaskKey.Should().BeNull();
	}

	[Fact]
	public void TaskWithoutWave_Throws() =>
		Assert.Throws<ArgumentException>(() => new TaskNodeId("log", null, "endpoint"));

	[Fact]
	public void Parse_TooManySegments_Throws() =>
		Assert.Throws<ArgumentException>(() => TaskNodeId.Parse("a/b/c/d"));

	[Theory]
	[InlineData("Log")]        // uppercase
	[InlineData("1log")]       // starts with digit
	[InlineData("log ingest")] // space
	public void InvalidSegment_Throws(string bad) =>
		Assert.Throws<ArgumentException>(() => new TaskNodeId(bad));

	[Fact]
	public void TryParse_ReturnsFalseOnGarbage()
	{
		TaskNodeId.TryParse("a/b/c/d", out var id).Should().BeFalse();
		id.Should().BeNull();
	}
}

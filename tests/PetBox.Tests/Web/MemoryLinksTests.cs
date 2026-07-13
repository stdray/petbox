using PetBox.Memory.Contract;
using PetBox.Web.Memory;

namespace PetBox.Tests.Web;

// The stable memory-entry URL (spec memory-entry-url): …/memory/{store}#{key}, project- and
// workspace-scoped, and the sensitivity refusal that both this card and the key-autolink rest on.
public sealed class MemoryLinksTests
{
	const string Key = "m-0123456789abcdef0123456789abcdef";

	[Fact]
	public void ProjectEntry_IsStoreUrlPlusKeyFragment()
		=> MemoryLinks.ProjectEntry("$system", "$system", "notes", Key)
			.Should().Be($"/ui/$system/$system/memory/notes#{Key}");

	// Workspace scope has no separate route — it is the reserved memory CONTAINER project
	// ("$workspace" under $system, "$ws-{ws}" elsewhere), addressed through the same UI entry.
	[Fact]
	public void WorkspaceEntry_TargetsTheWorkspaceMemoryContainer()
	{
		MemoryLinks.WorkspaceEntry("$system", "canon", "index")
			.Should().Be("/ui/$system/$workspace/memory/canon#index");
		MemoryLinks.WorkspaceEntry("acme", "canon", "index")
			.Should().Be("/ui/acme/$ws-acme/memory/canon#index");
	}

	// A sensitive store gets NO automatic link — in either scope, whatever the key.
	[Fact]
	public void SensitiveStore_GetsNoLink()
	{
		MemoryStores.IsSensitive("ops").Should().BeTrue();
		MemoryLinks.ProjectEntry("$system", "$system", "ops", Key).Should().BeNull();
		MemoryLinks.WorkspaceEntry("$system", "OPS", Key).Should().BeNull(); // case-insensitive
	}

	// Plumbing stores that are still knowledge (IsSystem, but not secret-bearing) stay linkable —
	// the sensitivity marker is deliberately narrower than the system badge.
	[Fact]
	public void SystemButNotSensitiveStores_StayLinkable()
	{
		MemoryStores.IsSensitive("canon").Should().BeFalse();
		MemoryStores.IsSensitive("autocaptured").Should().BeFalse();
		MemoryLinks.ProjectEntry("$system", "$system", "canon", Key).Should().NotBeNull();
	}

	[Theory]
	[InlineData("", "$system", "notes", Key)]
	[InlineData("$system", "", "notes", Key)]
	[InlineData("$system", "$system", "", Key)]
	[InlineData("$system", "$system", "notes", "")]
	public void EmptyInput_YieldsNoLink(string ws, string project, string store, string key)
		=> MemoryLinks.ProjectEntry(ws, project, store, key).Should().BeNull();
}

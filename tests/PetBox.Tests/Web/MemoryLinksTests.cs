using PetBox.Memory.Contract;
using PetBox.Web.Memory;

namespace PetBox.Tests.Web;

// The stable memory-entry URL (spec memory-entry-url): …/memory/{store}?key={key}#{key}, project-
// and workspace-scoped, and the sensitivity refusal that both this card and the key-autolink rest on.
//
// The `?key=` half is not decoration: the store page pages at 40 entries, a fragment is never sent to
// the server, so a bare `#{key}` silently rendered page 0 without the card. The query is what makes
// the server resolve the entry's page (memory-anchor-ignores-pagination).
public sealed class MemoryLinksTests
{
	const string Key = "m-0123456789abcdef0123456789abcdef";

	[Fact]
	public void ProjectEntry_CarriesTheKeyAsQueryAndFragment()
		=> MemoryLinks.ProjectEntry("$system", "$system", "notes", Key)
			.Should().Be($"/ui/$system/$system/memory/notes?key={Key}#{Key}");

	// Workspace scope has no separate route — it is the reserved memory CONTAINER project
	// ("$workspace" under $system, "$ws-{ws}" elsewhere), addressed through the same UI entry.
	[Fact]
	public void WorkspaceEntry_TargetsTheWorkspaceMemoryContainer()
	{
		MemoryLinks.WorkspaceEntry("$system", "canon", "index")
			.Should().Be("/ui/$system/$workspace/memory/canon?key=index#index");
		MemoryLinks.WorkspaceEntry("acme", "canon", "index")
			.Should().Be("/ui/acme/$ws-acme/memory/canon?key=index#index");
	}

	// The query value is percent-encoded (the fragment keeps the raw key — it must match the card's
	// `id`), so a key with URL-significant characters cannot break the link.
	[Fact]
	public void KeyIsEncodedInTheQuery()
		=> MemoryLinks.ProjectEntry("$system", "$system", "notes", "a b&c")
			.Should().Be("/ui/$system/$system/memory/notes?key=a%20b%26c#a b&c");

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

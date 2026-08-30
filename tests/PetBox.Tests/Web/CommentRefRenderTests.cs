using PetBox.Tasks.Contract;
using PetBox.Web.Pages.Shared;
using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// `[[#comment]]` reference autolinking (work `comment-slug-and-refs`, spec `comment-ref-links`) —
// the fourth member of the family, and deliberately the SAME mechanism as `[[slug]]` node mentions:
// the caller hands the renderer a prebuilt token→target map and the renderer never looks anything
// up. A token that is not in the map stays literal.
//
// That last sentence is the whole security model of this feature. There is no "anonymous mode" to
// test here because there is none to write: the public page's behaviour is produced entirely by
// what CommentRefMap is given (CommentRefShareTests exercises that end of it over real HTTP).
public sealed class MarkdownRendererCommentRefTests
{
	static readonly IMarkdownRenderer R = new MarkdownRenderer();

	const string Id = "9f1c2d3e4a5b6c7d8e9f0a1b2c3d4e5f"; // 32-hex, the shape a comment id has

	static IReadOnlyDictionary<string, NodeRefTarget> Map(params (string Token, string? Text)[] entries)
		=> entries.ToDictionary(e => e.Token, e => new NodeRefTarget("#comment-" + Id, "comment by alice · 2026-08-30", e.Text),
			StringComparer.Ordinal);

	static string Html(string md, IReadOnlyDictionary<string, NodeRefTarget>? map, string? commitTemplate = null)
		=> R.RenderToHtml(md, commitTemplate, null, null, map);

	[Fact]
	public void ResolvableSlugReference_LinksToTheInPageAnchor_KeepingTheWrittenText()
	{
		var html = Html("as analysed in [[#part-04]], the numbers hold", Map(("part-04", null)));

		html.Should().Contain($"<a href=\"#comment-{Id}\"");
		html.Should().Contain("title=\"comment by alice · 2026-08-30\"");
		html.Should().Contain(">#part-04</a>", "a slug is what the author wrote, so it stays the link text — "
			+ "the same rule a `[[slug]]` node mention follows");
		html.Should().NotContain("[[#part-04]]");
	}

	[Fact]
	public void ResolvableIdReference_LinksWithTheAuthorDateLabel_NotTheRawGuid()
	{
		var html = Html($"see [[#{Id}]]", Map((Id, "alice · 2026-08-30")));

		html.Should().Contain($"<a href=\"#comment-{Id}\"");
		html.Should().Contain(">alice · 2026-08-30</a>", "nobody reads a 32-hex id — the map supplies the "
			+ "visible text, which is why the substitution lives in the DATA and not in a renderer branch");
		html.Should().NotContain(Id + "</a>");
	}

	[Fact]
	public void UnmappedReference_StaysLiteralText_WithNoLinkAtAll()
	{
		var html = Html("see [[#part-13]] for the appendix", Map(("part-04", null)));

		html.Should().NotContain("<a");
		html.Should().Contain("[[#part-13]]", "an unresolvable reference renders as its original text, "
			+ "brackets included — it never becomes a broken link and never silently disappears");
	}

	[Fact]
	public void NoMapAtAll_LeavesEveryReferenceLiteral()
	{
		var md = "see [[#part-04]] and [[#" + Id + "]]";

		var html = Html(md, null);

		html.Should().NotContain("<a");
		html.Should().Contain("[[#part-04]]");
	}

	[Fact]
	public void ReferenceInsideACodeSpan_IsNotLinked()
	{
		var html = Html("write `[[#part-04]]` to reference it", Map(("part-04", null)));

		html.Should().NotContain("<a");
		MarkdownCodeText.VisibleCodeText(html).Should().Contain("[[#part-04]]");
	}

	[Fact]
	public void ReferenceInsideAnExistingLink_IsNotNested()
	{
		var html = Html("[[[#part-04]]](https://example.test/x)", Map(("part-04", null)));

		html.Should().Contain("https://example.test/x");
		html.Should().NotContain("#comment-", "an <a> is never nested inside an <a>");
	}

	// The reason comment-ref spans are claimed BEFORE the other passes, and claimed even when the
	// token does not resolve: without it a hash-shaped identifier inside a reference the page chose
	// not to publish would be picked up by the commit-hash rule and linked out to a repo browser —
	// "plain text" quietly meaning "some other link".
	[Fact]
	public void CommitHashInsideAnUnresolvedReference_StaysLiteral()
	{
		var html = Html("see [[#cc20e34abc]] please", null, "https://git.test/commit/{sha}");

		html.Should().NotContain("<a");
		html.Should().Contain("[[#cc20e34abc]]");
	}

	[Fact]
	public void NodeMentionAndCommentReference_DoNotShadowEachOther()
	{
		var nodeRefs = new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal)
		{ ["some-node"] = new("/ui/ws/proj/tasks/work/some-node", "The Node") };

		var html = R.RenderToHtml("[[some-node]] and [[#part-04]]", null, nodeRefs, null, Map(("part-04", null)));

		html.Should().Contain("/ui/ws/proj/tasks/work/some-node");
		html.Should().Contain($"#comment-{Id}");
		html.Should().Contain(">some-node</a>").And.Contain(">#part-04</a>",
			"the two shapes are disjoint — `[[` + `#` can never be read as a node slug, and vice versa");
	}

	[Fact]
	public void NoContextAtAll_IsStillTheByteIdenticalLegacyPath()
	{
		var md = "## Head\nsee [[#part-04]] here";

		R.RenderToHtml(md, null, null, null, null).Should().Be(R.RenderToHtml(md));
	}
}

// CommentRefMap — the DATA half of the feature. It takes the comments a surface is ACTUALLY
// RENDERING and builds the token→target map from those and nothing else; that argument is the whole
// confinement mechanism (see the class's own comment).
public sealed class CommentRefMapTests
{
	static CommentView Comment(string id, string author, string? slug = null) =>
		new(id, "node", null, author, "body", [], 1, new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
			new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc), slug);

	[Fact]
	public void ACommentIsAddressableByBothOfItsAddresses()
	{
		var map = CommentRefMap.Build([Comment("aa11", "alice", "part-04")]);

		map.Should().ContainKey("aa11").And.ContainKey("part-04");
		map["aa11"].Url.Should().Be("#comment-aa11");
		map["part-04"].Url.Should().Be("#comment-aa11", "both are addresses of the SAME comment");
	}

	[Fact]
	public void ACommentWithoutASlug_IsStillAddressableByItsId()
	{
		var map = CommentRefMap.Build([Comment("bb22", "bob")]);

		map.Should().ContainKey("bb22");
		map.Should().HaveCount(1, "nothing has a slug until someone chooses one — the id form is what the "
			+ "`ref` affordance hands an author, so it has to work on its own");
	}

	[Fact]
	public void TheIdFormCarriesAnAuthorDateLabel_TheSlugFormKeepsTheWrittenText()
	{
		var map = CommentRefMap.Build([Comment("cc33", "carol", "intro")]);

		map["cc33"].Text.Should().Be("carol · 2026-08-30");
		map["intro"].Text.Should().BeNull("the slug the author wrote is already good anchor text");
	}

	[Fact]
	public void AnEmptySet_YieldsAnEmptyMap_WhichIsTheShareBodyScope()
	{
		CommentRefMap.Build([]).Should().BeEmpty();
	}
}

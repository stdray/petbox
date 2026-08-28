using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Memory.Contract;
using PetBox.Sessions.Contract;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.ByReference;

// THE OTHER FOUR SURFACES. The card names five verbs, and BodyRefTransportTests proves the
// mechanism on ONE of them (tasks_upsert) in depth. This file proves the parameter is actually
// WIRED on the remaining four rather than merely declared — a DTO property that nothing reads
// compiles perfectly and silently drops the caller's body, which is the failure this file exists
// to make impossible.
//
// Each verb gets the same pair, and the pair is the point:
//   * the POSITIVE — the referenced text really becomes the stored body, so the wiring is live;
//   * the NEGATIVE — sending the inline field AND the reference together is refused, through
//     whatever channel that verb has (conflicts[] where one exists, a throw where it does not).
//
// THE NAMES DIFFER ON PURPOSE and the tests spell that out: `bodyRef` where the field is `body`,
// `textRef` on memory_remember (whose field is `text`), `contentRef` on a session message (whose
// field is `content`). A parameter must name the field it replaces, or the pairing is unreadable
// at the call site.
[Collection("WebAppFactory")]
public sealed class BodyRefSurfacesTests : IClassFixture<BodyRefTransportFixture>
{
	readonly BodyRefTransportFixture _fx;

	public BodyRefSurfacesTests(BodyRefTransportFixture fx) => _fx = fx;

	const string FileText = "## Отчёт сабагента\n\nТекст, который уже лежит файлом.";

	// ── memory_upsert: `bodyRef` ────────────────────────────────────────────────────────────────

	[Fact]
	public async Task MemoryUpsert_BodyRef_BecomesTheEntryBody_AndCollidesWithBody()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		var http = Http(scope);
		var wsmem = scope.ServiceProvider.GetRequiredService<IWorkspaceMemoryDirectory>();

		var ok = await MemoryTools.UpsertAsync(http, Flags(), wsmem, memory,
			projectKey: BodyRefTransportFixture.ProjA, store: "notes",
			entries: [new MemoryEntryInputDto { Key = "e1", Type = "Project", Description = "d", BodyRef = await UploadAsync(FileText) }],
			bodyLen: -1);

		ok.Applied.Should().BeTrue();
		ok.Added.Single().Body.Should().Be(FileText);

		var both = await MemoryTools.UpsertAsync(http, Flags(), wsmem, memory,
			projectKey: BodyRefTransportFixture.ProjA, store: "notes",
			entries:
			[
				new MemoryEntryInputDto
				{
					Key = "e2", Type = "Project", Description = "d",
					Body = "inline", BodyRef = await UploadAsync("from the file"),
				},
			],
			bodyLen: -1);

		both.Applied.Should().BeFalse();
		both.Conflicts.Should().ContainSingle().Which.Reason.Should().Be(BodyRefs.BodyAndBodyRef);
		// On the STORE too — a silent precedence would have left an entry behind.
		(await memory.ListActiveEntriesAsync(BodyRefTransportFixture.ProjA, "notes"))
			.Should().NotContain(e => e.Key == "e2");
	}

	// ── memory_remember: `textRef` (the field here is `text`, not `body`) ────────────────────────

	[Fact]
	public async Task MemoryRemember_TextRef_BecomesTheFact_AndCollidesWithText()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		var wsmem = scope.ServiceProvider.GetRequiredService<IWorkspaceMemoryDirectory>();
		var http = Http(scope);

		var ack = await MemoryTools.RememberAsync(http, Flags(), wsmem, memory,
			text: null, scope: null, projectKey: BodyRefTransportFixture.ProjA, store: "notes",
			type: "Project", tags: null, description: "d", textRef: await UploadAsync(FileText));

		var stored = (await memory.ListActiveEntriesAsync(BodyRefTransportFixture.ProjA, "notes"))
			.Single(e => e.Key == ack.Key);
		stored.Body.Should().Be(FileText);

		// memory_remember has no conflicts[] — its refusals are throws, so this one is too. The RULE
		// is identical (both present = refused, never a precedence); only the channel differs.
		var reference = await UploadAsync("from the file");
		var act = async () => await MemoryTools.RememberAsync(http, Flags(), wsmem, memory,
			text: "inline", scope: null, projectKey: BodyRefTransportFixture.ProjA, store: "notes",
			type: "Project", tags: null, description: "d", textRef: reference);

		(await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("'text'",
			"the refusal must name THIS verb's field, not a `body` the caller never sent");
	}

	[Fact]
	public async Task MemoryRemember_WithNeitherTextNorTextRef_IsRefused()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		var wsmem = scope.ServiceProvider.GetRequiredService<IWorkspaceMemoryDirectory>();

		var act = async () => await MemoryTools.RememberAsync(Http(scope), Flags(), wsmem, memory,
			text: null, scope: null, projectKey: BodyRefTransportFixture.ProjA, store: "notes",
			type: "Project", tags: null, description: "d", textRef: null);

		// Making `text` optional (so `textRef` can stand in for it) must not make it OPTIONAL —
		// a call carrying neither is still a refusal, not an empty fact.
		await act.Should().ThrowAsync<ArgumentException>();
	}

	// ── comments_upsert: `bodyRef`, and legal on a CREATE ────────────────────────────────────────

	[Fact]
	public async Task CommentsUpsert_BodyRef_BecomesTheCommentBody_OnACreate_AndCollidesWithBody()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		var comments = scope.ServiceProvider.GetRequiredService<ICommentService>();
		var http = Http(scope);
		await tasks.CreateBoardAsync(BodyRefTransportFixture.ProjA, "cb", null, null, null);
		await tasks.UpsertAsync(BodyRefTransportFixture.ProjA, "cb",
			[new NodePatch { Key = "host", Title = "t", Body = "b", Version = 0 }]);

		// A CREATE — the case the mechanism is for (a subagent's report posted as a comment, from a
		// file, in one call). Unlike `fragment`, a bodyRef needs no existing text to match.
		var ok = await CommentTools.UpsertAsync(http, Flags(), comments, tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "cb",
			items: [new CommentItemInput { Node = "host", Author = "agent", BodyRef = await UploadAsync(FileText) }],
			bodyLen: -1);

		ok.Applied.Should().BeTrue();
		ok.Added.Single().Body.Should().Be(FileText);

		var both = await CommentTools.UpsertAsync(http, Flags(), comments, tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "cb",
			items:
			[
				new CommentItemInput
				{
					Node = "host", Author = "agent", Body = "inline", BodyRef = await UploadAsync("from the file"),
				},
			],
			bodyLen: -1);

		both.Applied.Should().BeFalse();
		both.Conflicts.Should().ContainSingle().Which.Reason.Should().Be(BodyRefs.BodyAndBodyRef);
	}

	// ── session_append: `contentRef`, PER MESSAGE ───────────────────────────────────────────────

	[Fact]
	public async Task SessionAppend_ContentRef_BecomesTheMessageContent_PerMessage()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
		var http = Http(scope);
		var sessionId = "s-" + Guid.NewGuid().ToString("N");

		// One referenced message BESIDE an ordinary inline one — the per-ITEM granularity the owner
		// chose, not per-call: a batch may carry one huge referenced body and several small typed ones.
		var r = await SessionTools.AppendAsync(http, Flags(), sessions,
			projectKey: BodyRefTransportFixture.ProjA, sessionId: sessionId, agent: "a", fromOrdinal: 1,
			messages:
			[
				new SessionMessageDto { Role = "user", Content = "inline question" },
				new SessionMessageDto { Role = "assistant", ContentRef = await UploadAsync(FileText) },
			]);

		r.Applied.Should().BeTrue();
		r.Appended.Should().Be(2);

		var got = await sessions.GetAsync(BodyRefTransportFixture.ProjA, sessionId);
		got!.Content.Should().Contain(FileText).And.Contain("inline question");
	}

	[Fact]
	public async Task SessionAppend_ContentAndContentRef_OnOneMessage_AreRefused_NamingTheMessage()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
		var sessionId = "s-" + Guid.NewGuid().ToString("N");
		var reference = await UploadAsync("from the file");

		var act = async () => await SessionTools.AppendAsync(Http(scope), Flags(), sessions,
			projectKey: BodyRefTransportFixture.ProjA, sessionId: sessionId, agent: "a", fromOrdinal: 1,
			messages:
			[
				new SessionMessageDto { Role = "user", Content = "ok" },
				new SessionMessageDto { Role = "assistant", Content = "inline", ContentRef = reference },
			]);

		var ex = await act.Should().ThrowAsync<ArgumentException>();
		// The INDEX is part of the message: in a batch, "a message was refused" without saying which
		// one leaves the caller to bisect their own call.
		ex.Which.Message.Should().Contain("messages[1]").And.Contain("'content'");

		// Nothing was appended, and the blob was not spent.
		(await sessions.GetAsync(BodyRefTransportFixture.ProjA, sessionId)).Should().BeNull();
		using var inner = _fx.Factory.Services.CreateScope();
		var blobs = inner.ServiceProvider.GetRequiredService<IBodyRefBlobStore>();
		(await blobs.PeekAsync(reference, DateTime.UtcNow))!.Body.Should().Be("from the file");
	}

	// ── HELPERS ─────────────────────────────────────────────────────────────────────────────────

	// Uploads through the REAL endpoint, so these tests exercise the same transport a caller uses
	// rather than seeding the table directly — a blob the endpoint could not have produced would
	// prove nothing about the round trip.
	async Task<string> UploadAsync(string text)
	{
		using var client = _fx.ClientFor(BodyRefTransportFixture.KeyA1);
		using var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
		using var res = await client.PostAsync($"/api/blobs/{BodyRefTransportFixture.ProjA}", content);
		res.EnsureSuccessStatusCode();
		return (await res.Content.ReadFromJsonAsync<PetBox.Web.Blobs.BodyRefUploadResponse>())!.Ref;
	}

	static IHttpContextAccessor Http(IServiceScope scope)
	{
		var claims = new List<Claim>
		{
			new("project", BodyRefTransportFixture.ProjA),
			new("scopes", "tasks:read,tasks:write,memory:read,memory:write"),
			new("key_name", "a1"),
		};
		return new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext
			{
				RequestServices = scope.ServiceProvider,
				User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
			},
		};
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
		}).Build());
}

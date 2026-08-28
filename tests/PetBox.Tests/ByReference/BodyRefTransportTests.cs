using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Web.Blobs;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.ByReference;

// END-TO-END for work/write-body-by-reference: a file goes up over REST, its reference goes into an
// MCP write verb, and the text lands in the node — having passed through no JSON argument anywhere.
//
// WHY THIS RUNS AGAINST A REAL HOST rather than calling the pieces directly. Every claim this file
// makes is about a JOINT between two layers — the PEP and the endpoint (sandbox containment), the
// blob's tenant and the caller's claims (isolation), the tool and the store (one-shot consumption).
// A unit test of either side alone would have passed for every one of those while the joint was
// wrong; MemoryApi's own header records a leak of exactly that shape, measured on production, where
// the gate refused the key everywhere it was aimed and the handler handed the container over anyway.
//
// EVERY REFUSAL IS ASSERTED TWICE, the discipline TaskFragmentPatchTests states: once on the answer
// (403/413/conflicts[]) and once on the STORE — the node body read back, or the blob still being
// there. An ack that says "refused" while the write landed anyway is the failure mode this feature
// can actually have, and only a read-back can see it.
//
// AND EVERY NEGATIVE HAS ITS POSITIVE. "Project B cannot see project A's blob" is worth nothing on
// its own — a mechanism that resolves NOTHING passes it. Each isolation test is therefore paired
// with the same call from the authorized side, so a green pair means the gate discriminates rather
// than merely refuses.
public sealed class BodyRefTransportFixture : IAsyncLifetime
{
	// Two projects in one workspace, plus a sandbox project. The keys are the interesting part:
	// ProjA has TWO of them, because "a blob is visible to ANOTHER key of the same project" is the
	// fan-out case the owner chose this tenant model for, and one key per project cannot test it.
	public const string ProjA = "proja";
	public const string ProjB = "projb";
	public const string SandboxProj = "sbox";

	public const string KeyA1 = "yb_key_test_bodyref_a1";
	public const string KeyA2 = "yb_key_test_bodyref_a2";
	public const string KeyB = "yb_key_test_bodyref_b";
	public const string KeyReadOnlyA = "yb_key_test_bodyref_ro";
	public const string KeySandbox = "yb_key_test_bodyref_sbox";

	public WebApplicationFactory<Program> Factory { get; }

	public BodyRefTransportFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					// Every per-project db file derives from this connection string's DIRECTORY
					// (Program.ResolveDataDir), so one temp connection string isolates the whole host.
					["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
					["Host:BackgroundServices"] = "false",
					["Features:Tasks"] = "true",
					["Features:Memory"] = "true",
				}));
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		using var _ = Factory.CreateClient();

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new Workspace { Key = "brws", Name = "BodyRef", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = ProjA, WorkspaceKey = "brws", Name = "A" });
		await db.InsertAsync(new Project { Key = ProjB, WorkspaceKey = "brws", Name = "B" });
		await db.InsertAsync(new Project { Key = SandboxProj, WorkspaceKey = "brws", Name = "S", Sandbox = true });

		const string Write = "tasks:read,tasks:write,memory:read,memory:write";
		await db.InsertAsync(new ApiKey { Key = KeyA1, ProjectKey = ProjA, Scopes = Write, Name = "a1", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new ApiKey { Key = KeyA2, ProjectKey = ProjA, Scopes = Write, Name = "a2", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new ApiKey { Key = KeyB, ProjectKey = ProjB, Scopes = Write, Name = "b", CreatedAt = DateTime.UtcNow });
		// Deliberately read-only: the upload gate is "you could already write SOMETHING here", and a
		// key that cannot must not be able to park bytes on the server.
		await db.InsertAsync(new ApiKey { Key = KeyReadOnlyA, ProjectKey = ProjA, Scopes = "tasks:read,memory:read", Name = "ro", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new ApiKey
		{
			Key = KeySandbox,
			ProjectKey = SandboxProj,
			SandboxOnly = true,
			Scopes = Write,
			Name = "sbox",
			CreatedAt = DateTime.UtcNow,
		});
	}

	public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

	public HttpClient ClientFor(string? apiKey)
	{
		var c = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		if (apiKey is not null) c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
		return c;
	}
}

[Collection("WebAppFactory")]
public sealed class BodyRefTransportTests : IClassFixture<BodyRefTransportFixture>
{
	readonly BodyRefTransportFixture _fx;

	public BodyRefTransportTests(BodyRefTransportFixture fx) => _fx = fx;

	// A body a model would genuinely not want to retype: Cyrillic prose with structure. Its
	// character count and byte count differ by ~2x, which is the whole reason the transport exists.
	const string CyrillicBody =
		"# Отчёт\n\n## Первое\n\nТело, которое уже существует файлом.\n\n- пункт один\n- пункт два\n\n## Второе\n\nВторой абзац.";

	// ── UPLOAD ──────────────────────────────────────────────────────────────────────────────────

	async Task<(HttpStatusCode Status, BodyRefUploadResponse? Body)> UploadAsync(
		string? apiKey, string project, string text)
	{
		using var client = _fx.ClientFor(apiKey);
		using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
		using var res = await client.PostAsync($"/api/blobs/{project}", content);
		return (res.StatusCode,
			res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<BodyRefUploadResponse>() : null);
	}

	async Task<string> UploadOkAsync(string apiKey, string project, string text)
	{
		var (status, body) = await UploadAsync(apiKey, project, text);
		status.Should().Be(HttpStatusCode.OK);
		return body!.Ref;
	}

	[Fact]
	public async Task Upload_ReturnsAWellFormedReference_AndBothSizes()
	{
		var (status, body) = await UploadAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, CyrillicBody);

		status.Should().Be(HttpStatusCode.OK);
		BodyRefs.IsWellFormed(body!.Ref).Should().BeTrue("a `ref` the caller pastes back must satisfy the shape check that guards it");
		body.Chars.Should().Be(CyrillicBody.Length);
		body.Bytes.Should().Be(Encoding.UTF8.GetByteCount(CyrillicBody));
		body.Bytes.Should().BeGreaterThan(body.Chars, "Cyrillic costs ~2 bytes per character — the two numbers are not the same measurement");
		body.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow + BodyRefs.Ttl, TimeSpan.FromMinutes(5));
	}

	[Fact]
	public async Task AReadOnlyKey_CannotUpload_ButAWritingKeyOfTheSameProjectCan()
	{
		var (refused, _) = await UploadAsync(BodyRefTransportFixture.KeyReadOnlyA, BodyRefTransportFixture.ProjA, "x");
		refused.Should().Be(HttpStatusCode.Forbidden);

		// THE OTHER DIRECTION. Without it, an endpoint that forbade everyone would pass the line above.
		var (allowed, _) = await UploadAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, "x");
		allowed.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task AKeyOfProjectB_CannotUploadIntoProjectA_ButAKeyOfProjectACan()
	{
		var (refused, _) = await UploadAsync(BodyRefTransportFixture.KeyB, BodyRefTransportFixture.ProjA, "x");
		refused.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

		var (allowed, _) = await UploadAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, "x");
		allowed.Should().Be(HttpStatusCode.OK);
	}

	// THE SANDBOX CLAIM, both halves. A sandboxOnly key is authorized for its OWN project by
	// identity — the containment question is separate, and is the one that decides here.
	[Fact]
	public async Task ASandboxOnlyKey_CannotUploadIntoANonSandboxProject_ButCanIntoItsOwnSandbox()
	{
		var (refused, _) = await UploadAsync(BodyRefTransportFixture.KeySandbox, BodyRefTransportFixture.ProjA, "x");
		refused.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

		var (allowed, _) = await UploadAsync(BodyRefTransportFixture.KeySandbox, BodyRefTransportFixture.SandboxProj, "x");
		allowed.Should().Be(HttpStatusCode.OK, "containment confines a sandboxOnly key to sandbox projects — it does not disable it");
	}

	[Fact]
	public async Task AnUnauthenticatedUpload_IsRefused()
	{
		var (status, _) = await UploadAsync(null, BodyRefTransportFixture.ProjA, "x");
		status.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
	}

	[Fact]
	public async Task AnEmptyBody_IsRefused()
	{
		var (status, _) = await UploadAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, "");
		status.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task InvalidUtf8_IsRefused_RatherThanStoredAsReplacementCharacters()
	{
		using var client = _fx.ClientFor(BodyRefTransportFixture.KeyA1);
		// A lone 0x80 continuation byte: not valid UTF-8 under any decoding.
		using var content = new ByteArrayContent([0x41, 0x80, 0x42]);
		using var res = await client.PostAsync($"/api/blobs/{BodyRefTransportFixture.ProjA}", content);

		res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task ALeadingByteOrderMark_IsStripped_SoAMarkdownHeadingStaysAHeading()
	{
		using var client = _fx.ClientFor(BodyRefTransportFixture.KeyA1);
		var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("# Заголовок")).ToArray();
		using var content = new ByteArrayContent(bytes);
		using var res = await client.PostAsync($"/api/blobs/{BodyRefTransportFixture.ProjA}", content);
		var uploaded = await res.Content.ReadFromJsonAsync<BodyRefUploadResponse>();

		var text = await PeekAsync(uploaded!.Ref);
		text.Should().StartWith("# ", "a BOM left in place becomes an invisible first character and the heading stops being one");
	}

	// ── THE SIZE CEILING ────────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task ABodyOverTheCeiling_IsRefused_WhenItsLengthIsDeclared()
	{
		using var client = _fx.ClientFor(BodyRefTransportFixture.KeyA1);
		// ByteArrayContent sets Content-Length, so this exercises the branch that refuses without
		// reading the body at all.
		using var content = new ByteArrayContent(new byte[BodyRefs.MaxBytes + 1]);
		using var res = await client.PostAsync($"/api/blobs/{BodyRefTransportFixture.ProjA}", content);

		res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
	}

	// THE CLAIM THAT MATTERS: the ceiling refuses BEFORE the overage is buffered. A chunked upload
	// declares no length, so the only thing standing between the server and a 40 MB allocation is
	// the bounded read — and this test measures how much the server actually pulled. Delete the
	// bound and the byte counter runs to 40 MB, which is what makes this a real assertion rather
	// than a restatement of the status code.
	[Fact]
	public async Task ABodyOverTheCeiling_IsRefused_WithoutReadingPastTheCeiling_WhenLengthIsNotDeclared()
	{
		using var client = _fx.ClientFor(BodyRefTransportFixture.KeyA1);
		var source = new CountingStream(40L * 1024 * 1024);
		using var content = new StreamContent(source);
		using var res = await client.PostAsync($"/api/blobs/{BodyRefTransportFixture.ProjA}", content);

		res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
		// `BytesRead` counts what the CLIENT pulled out of this stream, not what the server actually
		// consumed — the server's 64 KB-chunked reader stops at most one chunk past the ceiling, but
		// the client-side send pump races that abort and can push a further chunk or two into the
		// pipe before the server tears the request down. Under load that race can land closer to the
		// edge than in isolation, so the slack here is sized to comfortably clear the race (1 MB, vs.
		// the 40 MB this test measures if the bound in ReadBoundedAsync is removed) rather than to sit
		// tight against the reader's one-chunk overshoot — a tighter bound flakes under load without
		// this test discriminating any better.
		source.BytesRead.Should().BeLessThan(BodyRefs.MaxBytes + (1024 * 1024),
			"the ceiling must be enforced on the way in, not after the whole body has been buffered");
	}

	[Fact]
	public async Task ABodyExactlyAtTheCeiling_IsAccepted()
	{
		using var client = _fx.ClientFor(BodyRefTransportFixture.KeyA1);
		using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('a', (int)BodyRefs.MaxBytes)));
		using var res = await client.PostAsync($"/api/blobs/{BodyRefTransportFixture.ProjA}", content);

		res.StatusCode.Should().Be(HttpStatusCode.OK, "the ceiling is a ceiling, not a fence one byte below it");
	}

	// ── THE SUBSTITUTION, THROUGH THE REAL VERB ─────────────────────────────────────────────────

	[Fact]
	public async Task AnUploadedFile_BecomesTheNodeBody_WithNoBodyArgumentInTheCall()
	{
		var reference = await UploadOkAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, CyrillicBody);

		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.CreateBoardAsync(BodyRefTransportFixture.ProjA, "b", null, null, null);

		var r = await TasksTools.UpsertAsync(
			Http(scope, BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA), Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "b",
			nodes: [new TaskNodeInput { Key = "n1", Title = "t", BodyRef = reference, Version = 0 }],
			bodyLen: -1);

		r.Applied.Should().BeTrue();
		r.Conflicts.Should().BeEmpty();
		r.Added.Single().Body.Should().Be(CyrillicBody, "the file's text, verbatim — that is the whole feature");
	}

	// ── THE REFUSAL, WORDED AND CHANNELLED LIKE body-vs-fragment ────────────────────────────────

	[Fact]
	public async Task Body_AndBodyRef_Together_AreRefused_ThroughConflicts_AndNothingIsWritten()
	{
		var reference = await UploadOkAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, "from the file");

		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.CreateBoardAsync(BodyRefTransportFixture.ProjA, "b2", null, null, null);

		var r = await TasksTools.UpsertAsync(
			Http(scope, BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA), Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "b2",
			nodes: [new TaskNodeInput { Key = "n2", Title = "t", Body = "inline", BodyRef = reference, Version = 0 }],
			bodyLen: -1);

		// A REFUSAL, not a precedence — and through conflicts[], the same channel body-vs-fragment uses.
		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle().Which.Reason.Should().Be(BodyRefs.BodyAndBodyRef);
		r.Added.Should().BeEmpty();

		// ...and on the STORE. This is the assertion that catches a silent precedence: if either
		// value had quietly won, a node would exist.
		var nodes = (await tasks.GetAsync(BodyRefTransportFixture.ProjA, "b2")).Nodes;
		nodes.Should().BeEmpty("a refused write writes nothing — neither the inline body nor the referenced one");

		// The blob is untouched too: a refused write does not spend it.
		(await PeekAsync(reference)).Should().Be("from the file");
	}

	[Fact]
	public async Task Fragment_AndBodyRef_Together_AreRefused_InTheirOwnVocabulary()
	{
		var reference = await UploadOkAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, "replacement");

		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.CreateBoardAsync(BodyRefTransportFixture.ProjA, "b3", null, null, null);
		await tasks.UpsertAsync(BodyRefTransportFixture.ProjA, "b3",
			[new NodePatch { Key = "n3", Title = "t", Body = "original text", Version = 0 }]);

		var r = await TasksTools.UpsertAsync(
			Http(scope, BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA), Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "b3",
			nodes:
			[
				new TaskNodeInput
				{
					Key = "n3", BodyRef = reference, Version = 1,
					Fragment = [new FragmentEditDto { Old = "original", New = "edited" }],
				},
			],
			bodyLen: -1);

		r.Applied.Should().BeFalse();
		// NOT FragmentPatch.BodyAndFragment: that message names a `body` this caller never sent, and
		// a refusal that quotes a field the caller did not use sends them looking in the wrong place.
		r.Conflicts.Should().ContainSingle().Which.Reason.Should().Be(BodyRefs.FragmentAndBodyRef);
		(await tasks.GetAsync(BodyRefTransportFixture.ProjA, "b3")).Nodes.Single().Body.Should().Be("original text");
	}

	[Fact]
	public async Task AMalformedReference_IsRefused_WithAMessageNamingWhatAReferenceLooksLike()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.CreateBoardAsync(BodyRefTransportFixture.ProjA, "b4", null, null, null);

		var r = await TasksTools.UpsertAsync(
			Http(scope, BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA), Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "b4",
			nodes: [new TaskNodeInput { Key = "n4", Title = "t", BodyRef = "/tmp/report.md", Version = 0 }],
			bodyLen: -1);

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("/api/blobs/",
			"a caller who pasted a PATH must be told what a reference actually is, not merely that this one failed");
	}

	// ── ONE-SHOT ────────────────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task ABlobIsOneShot_TheSecondReferenceToItFails()
	{
		var reference = await UploadOkAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, "spend me once");

		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.CreateBoardAsync(BodyRefTransportFixture.ProjA, "b5", null, null, null);
		var http = Http(scope, BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA);

		var first = await TasksTools.UpsertAsync(http, Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "b5",
			nodes: [new TaskNodeInput { Key = "first", Title = "t", BodyRef = reference, Version = 0 }], bodyLen: -1);
		first.Applied.Should().BeTrue();
		first.Added.Single().Body.Should().Be("spend me once");

		// The blob is GONE from the store, not merely flagged.
		(await PeekAsync(reference)).Should().BeNull();

		var second = await TasksTools.UpsertAsync(http, Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "b5",
			nodes: [new TaskNodeInput { Key = "second", Title = "t", BodyRef = reference, Version = 0 }], bodyLen: -1);

		second.Applied.Should().BeFalse();
		second.Conflicts.Should().ContainSingle().Which.Reason.Should().Be(BodyRefs.Unresolvable(reference));
		(await tasks.GetAsync(BodyRefTransportFixture.ProjA, "b5")).Nodes
			.Should().ContainSingle().Which.Key.Should().Be("first", "the second node must not exist with an empty body");
	}

	// The counterpart, and the reason consumption is deferred past the write: a blob spent by a
	// write that was REFUSED would make every stale-baseline retry cost a re-upload — which is the
	// double payment this mechanism exists to abolish, merely moved to the retry.
	[Fact]
	public async Task ARefusedWrite_DoesNotSpendTheBlob_SoTheRetryReusesTheSameReference()
	{
		var reference = await UploadOkAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, "survives a conflict");

		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.CreateBoardAsync(BodyRefTransportFixture.ProjA, "b6", null, null, null);
		await tasks.UpsertAsync(BodyRefTransportFixture.ProjA, "b6",
			[new NodePatch { Key = "n6", Title = "t", Body = "v1", Version = 0 }]);
		var http = Http(scope, BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA);

		// A deliberately STALE baseline: version 99 was never issued.
		var stale = await TasksTools.UpsertAsync(http, Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "b6",
			nodes: [new TaskNodeInput { Key = "n6", BodyRef = reference, Version = 99 }], bodyLen: -1);
		stale.Applied.Should().BeFalse();

		(await PeekAsync(reference)).Should().Be("survives a conflict", "a refused write must not spend the blob");

		var current = (await tasks.GetAsync(BodyRefTransportFixture.ProjA, "b6")).Nodes.Single().Version;
		var retry = await TasksTools.UpsertAsync(http, Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "b6",
			nodes: [new TaskNodeInput { Key = "n6", BodyRef = reference, Version = current }], bodyLen: -1);

		retry.Applied.Should().BeTrue("the SAME reference must still work after a rebase — no re-upload");
		retry.Updated.Single().Body.Should().Be("survives a conflict");
	}

	// ── TENANT ISOLATION, BOTH DIRECTIONS ───────────────────────────────────────────────────────

	[Fact]
	public async Task ABlobOfProjectA_IsInvisibleToAKeyOfProjectB()
	{
		var reference = await UploadOkAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, "A's secret report");

		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.CreateBoardAsync(BodyRefTransportFixture.ProjB, "b7", null, null, null);

		var r = await TasksTools.UpsertAsync(
			Http(scope, BodyRefTransportFixture.KeyB, BodyRefTransportFixture.ProjB), Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjB, board: "b7",
			nodes: [new TaskNodeInput { Key = "stolen", Title = "t", BodyRef = reference, Version = 0 }], bodyLen: -1);

		r.Applied.Should().BeFalse();
		// Indistinguishable from "no such blob" — a distinct message would make this an existence
		// oracle for another tenant's uploads.
		r.Conflicts.Should().ContainSingle().Which.Reason.Should().Be(BodyRefs.Unresolvable(reference));
		(await tasks.GetAsync(BodyRefTransportFixture.ProjB, "b7")).Nodes.Should().BeEmpty();

		// AND THE BLOB SURVIVES: a failed cross-tenant reference must not consume another project's
		// upload, or B could destroy A's pending bodies just by guessing at references.
		(await PeekAsync(reference)).Should().Be("A's secret report");
	}

	// THE OPPOSITE DIRECTION, and it is not a formality: without it, a mechanism that resolved
	// nothing at all would pass the isolation test above. This is also the fan-out case the owner
	// chose "tenant = project" for — one agent uploads, ANOTHER references.
	[Fact]
	public async Task ABlobOfProjectA_IsVisibleToADifferentKeyOfProjectA()
	{
		var reference = await UploadOkAsync(BodyRefTransportFixture.KeyA1, BodyRefTransportFixture.ProjA, "handed off between agents");

		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.CreateBoardAsync(BodyRefTransportFixture.ProjA, "b8", null, null, null);

		// Uploaded under KeyA1, referenced under KeyA2.
		var r = await TasksTools.UpsertAsync(
			Http(scope, BodyRefTransportFixture.KeyA2, BodyRefTransportFixture.ProjA), Flags(), tasks,
			projectKey: BodyRefTransportFixture.ProjA, board: "b8",
			nodes: [new TaskNodeInput { Key = "n8", Title = "t", BodyRef = reference, Version = 0 }], bodyLen: -1);

		r.Applied.Should().BeTrue();
		r.Added.Single().Body.Should().Be("handed off between agents");
	}

	// ── TTL AND PRUNING ─────────────────────────────────────────────────────────────────────────

	// The TTL is enforced on READ, not by the job — a background sweep can be arbitrarily late, and
	// a deadline that only held when a scheduler happened to run would not be a deadline.
	[Fact]
	public async Task AnExpiredBlob_StopsResolving_BeforeAnyPruneJobHasRun()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var blobs = scope.ServiceProvider.GetRequiredService<IBodyRefBlobStore>();
		var expired = BodyRefBlobStore.NewBlob(BodyRefTransportFixture.ProjA, "stale", 5, "t",
			DateTime.UtcNow - BodyRefs.Ttl - TimeSpan.FromMinutes(1));
		await blobs.PutAsync(expired);

		(await blobs.PeekAsync(expired.Ref, DateTime.UtcNow)).Should().BeNull();
	}

	[Fact]
	public async Task ThePruneJob_DeletesExpiredBlobs_AndLeavesLiveOnesAlone()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var blobs = scope.ServiceProvider.GetRequiredService<IBodyRefBlobStore>();

		var dead = BodyRefBlobStore.NewBlob(BodyRefTransportFixture.ProjA, "dead", 4, "t",
			DateTime.UtcNow - BodyRefs.Ttl - TimeSpan.FromHours(1));
		var live = BodyRefBlobStore.NewBlob(BodyRefTransportFixture.ProjA, "live", 4, "t", DateTime.UtcNow);
		await blobs.PutAsync(dead);
		await blobs.PutAsync(live);

		var job = new PetBox.Web.Search.BodyRefPruneJob(blobs);
		var removed = await job.DrainAllAsync(CancellationToken.None);

		removed.Should().BeGreaterThanOrEqualTo(1);
		// The ROW is gone, which is what "reclaims space" means — a Peek returning null would also
		// be true of a blob the read predicate merely hides.
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		db.BodyRefBlobs.Count(b => b.Ref == dead.Ref).Should().Be(0);
		db.BodyRefBlobs.Count(b => b.Ref == live.Ref).Should().Be(1, "pruning must not eat blobs that are still within their TTL");
	}

	// ── HELPERS ─────────────────────────────────────────────────────────────────────────────────

	async Task<string?> PeekAsync(string reference)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var blobs = scope.ServiceProvider.GetRequiredService<IBodyRefBlobStore>();
		return (await blobs.PeekAsync(reference, DateTime.UtcNow))?.Body;
	}

	// An accessor carrying BOTH halves the resolver needs: the caller's claims (whose blob may this
	// be?) and the request scope it pulls IBodyRefBlobStore/IProjectCatalog from.
	static IHttpContextAccessor Http(IServiceScope scope, string apiKey, string project)
	{
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		var row = db.ApiKeys.First(k => k.Key == apiKey);
		var claims = new List<Claim> { new("project", project), new("scopes", row.Scopes), new("key_name", row.Name) };
		if (row.SandboxOnly) claims.Add(new Claim("sandbox_only", "true"));
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

// A stream that yields `total` zero bytes and REMEMBERS how many were taken. The memory of what was
// read is the whole point: it turns "the server said 413" into "the server stopped reading", which
// is the actual claim about the ceiling.
sealed class CountingStream(long total) : Stream
{
	public long BytesRead { get; private set; }

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => total;
	public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

	public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

	public override int Read(Span<byte> buffer)
	{
		var remaining = total - BytesRead;
		if (remaining <= 0) return 0;
		var n = (int)Math.Min(buffer.Length, remaining);
		buffer[..n].Clear();
		BytesRead += n;
		return n;
	}

	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(Read(buffer.Span));

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override void Flush() { }
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

using System.Diagnostics;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Pages;
using PetBox.Web.Pages.Admin;

namespace PetBox.Tests.Auth;

// add-member-composite-fix — the acceptance scenario of the card, one [Fact] per consequence.
//
// One method carried three defects and this file is the ratchet for all three:
//
//  (A) An ACCOUNT-ENUMERATION ORACLE. AddMemberAsync used to change what it demanded based on
//      whether the username was already in the Users table: known name → membership added with no
//      password; unknown name → PasswordRequired. A workspace admin could therefore probe the
//      whole instance for account names — including accounts a sysadmin made on a page this admin
//      cannot see — by reading which answer came back. Closed by making the CALLER declare its
//      intent (AddMemberMode) and by collapsing the three table-dependent outcomes into one
//      public response. The tests below compare, for one declared mode, everything a caller can
//      observe: the result TYPE, the redirect target, the notice TEXT, the error TEXT, and (the
//      one that survives a careless fix) the wall-clock TIME.
//
//  (B) A SILENT WorkspaceQuota = 0. The create branch never set the column, so a brand-new
//      account got the CLR default and could create no workspace of its own — a limit nobody
//      chose and nobody was told about. Closed by demanding the allowance exactly as
//      UserAdminService.CreateAsync demands it; the test pins the two refusals to the same words
//      so they cannot drift apart.
//
//  (C) NO TRANSACTION. The account INSERT, the AlreadyMember read and the membership INSERT ran
//      unwrapped, so a failure in the gap left an account with no membership: invisible on the
//      members page and holding that silent 0. Closed with one transaction over all three. The
//      test does not take that on trust — it INJECTS the failure with a BEFORE INSERT trigger on
//      WorkspaceMembers and then looks for the orphan.
public sealed class AddMemberCompositeFixTests : IDisposable
{
	const string Ws = "alpha";

	// A real PBKDF2 hash, so "the password was not touched" is asserted against a value that
	// could actually authenticate rather than a placeholder.
	const string TakenHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ICoreDbFactory _dbf;
	readonly WorkspaceMembershipService _svc;

	public AddMemberCompositeFixTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-addmember-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_dbf = _db.Factory();
		_svc = new WorkspaceMembershipService(_dbf);

		// add-member-hardening-cluster #2: AddMemberAsync now refuses a workspaceKey with no
		// Workspaces row. Every test in this file targets Ws ("alpha"), so it must exist for any of
		// them to reach the behaviour they actually test.
		_db.Insert(new Workspace { Key = Ws, Name = Ws, Description = "", CreatedAt = DateTime.UtcNow });
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	long SeedUser(string name, int quota) =>
		_db.InsertWithInt64Identity(new User
		{
			Username = name,
			PasswordHash = TakenHash,
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = quota,
		});

	// A bare PageModel with a TempData bag, so the success notice — which is half of what a caller
	// can observe — can be read back without a request pipeline.
	WorkspaceUsersModel Page()
	{
		var http = new DefaultHttpContext();
		var page = new WorkspaceUsersModel(_svc)
		{
			PageContext = new PageContext { HttpContext = http },
		};
		page.TempData = new TempDataDictionary(http, new NoopTempDataProvider());
		return page;
	}

	// EVERYTHING a caller of the add handler can see, in one comparable value. If two posts of the
	// same mode produce equal Observed, the caller cannot tell them apart — that is the whole
	// claim of fix (A), so it is asserted as one object rather than as scattered fields that a
	// later edit could quietly stop covering.
	sealed record Observed(string ResultType, string? RedirectPage, string? Notice, string? NoticeError, string? PageError);

	static Observed See(IActionResult result, WorkspaceUsersModel page) => new(
		result.GetType().Name,
		(result as RedirectToPageResult)?.PageName ?? "<same page>",
		page.TempData[Notice.SuccessKey] as string,
		page.TempData[Notice.ErrorKey] as string,
		page.ErrorMessage);

	async Task<(Observed Seen, IActionResult Result)> PostAsync(
		string username, AddMemberMode mode, string? password, int? quota)
	{
		var page = Page();
		var result = await page.OnPostAddAsync(Ws, username, mode, password, quota, WorkspaceRole.Member, default);
		return (See(result, page), result);
	}

	// ---- (A) the enumeration oracle --------------------------------------------------------

	// The CreateNew half. Same declared intent, one name that is taken and one that is free: the
	// observable answer must be byte-identical, while the substance underneath stays correct — the
	// free name becomes a new account, the taken name keeps its own password and its own
	// allowance and merely gains the membership.
	[Fact]
	public async Task CreateNew_answers_identically_whether_the_username_is_taken_or_free()
	{
		SeedUser("taken", quota: 5);

		var onTaken = await PostAsync("taken", AddMemberMode.CreateNew, "typed-password", quota: 7);
		var onFree = await PostAsync("free", AddMemberMode.CreateNew, "typed-password", quota: 7);

		onTaken.Seen.Should().Be(onFree.Seen,
			"a workspace admin must not be able to read 'this account already exists' out of the answer");
		onTaken.Result.Should().BeOfType<RedirectToPageResult>("both are Post/Redirect/Get successes");
		onTaken.Seen.PageError.Should().BeNull();

		// ...and the substance is still right, which is the other half of the acceptance criterion.
		var taken = _db.Users.Single(u => u.Username == "taken");
		taken.PasswordHash.Should().Be(TakenHash, "a supplied password is discarded, never an overwrite");
		taken.WorkspaceQuota.Should().Be(5, "the existing allowance is not re-decided by this page");
		_db.Users.Count(u => u.Username == "taken").Should().Be(1, "no duplicate account was created");

		var free = _db.Users.Single(u => u.Username == "free");
		free.WorkspaceQuota.Should().Be(7);
		free.PasswordHash.Should().NotBeNullOrEmpty();

		_db.MembershipRows().Select(m => m.UserId).Should().BeEquivalentTo([taken.Id, free.Id],
			"both intents ended with the account in the workspace");
	}

	// The AddExisting half. A name nobody holds writes nothing at all — and says exactly what a
	// name somebody holds says.
	[Fact]
	public async Task AddExisting_answers_identically_whether_the_username_is_taken_or_free()
	{
		var takenId = SeedUser("taken", quota: 5);

		var onTaken = await PostAsync("taken", AddMemberMode.AddExisting, password: null, quota: null);
		var onGhost = await PostAsync("ghost", AddMemberMode.AddExisting, password: null, quota: null);

		onTaken.Seen.Should().Be(onGhost.Seen,
			"'no such account' must be indistinguishable from 'added' — that difference IS the oracle");
		onTaken.Result.Should().BeOfType<RedirectToPageResult>();

		_db.Users.Any(u => u.Username == "ghost").Should().BeFalse(
			"AddExisting creates nothing — the quiet answer must not become a quiet account");
		_db.MembershipRows().Should().ContainSingle().Which.UserId.Should().Be(takenId);
	}

	// The refusals must be existence-blind too. Both of these are decided from the POST alone,
	// before the service reads a row — if either ever started depending on the table, the oracle
	// would be back with a different mouth.
	[Fact]
	public async Task CreateNew_refusals_do_not_depend_on_whether_the_username_is_taken()
	{
		SeedUser("taken", quota: 5);

		var noPasswordOnTaken = await PostAsync("taken", AddMemberMode.CreateNew, password: null, quota: 1);
		var noPasswordOnFree = await PostAsync("free", AddMemberMode.CreateNew, password: null, quota: 1);
		var noQuotaOnTaken = await PostAsync("taken", AddMemberMode.CreateNew, "pw", quota: null);
		var noQuotaOnFree = await PostAsync("free", AddMemberMode.CreateNew, "pw", quota: null);

		noPasswordOnTaken.Seen.Should().Be(noPasswordOnFree.Seen);
		noQuotaOnTaken.Seen.Should().Be(noQuotaOnFree.Seen);
		noPasswordOnTaken.Result.Should().BeOfType<PageResult>("a refusal re-renders the page");
		noPasswordOnTaken.Seen.PageError.Should().NotBeNullOrEmpty("a refusal nobody can see is a silent failure");

		_db.Users.Count().Should().Be(1, "a refused post writes nothing");
		_db.MembershipRows().Should().BeEmpty();
	}

	// A post with no declared mode is refused rather than defaulted. Guessing a mode would put the
	// choice of required fields back in the server's hands, which is where it caused the leak.
	[Fact]
	public async Task A_post_without_a_declared_mode_is_refused()
	{
		var page = Page();

		var result = await page.OnPostAddAsync(Ws, "someone", Mode: null, "pw", 1, WorkspaceRole.Member, default);

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().NotBeNullOrEmpty();
		_db.Users.Should().BeEmpty();
	}

	// THE TIMING CHANNEL. Closing the oracle in the status/text domain and leaving it in the time
	// domain would be no fix at all: PBKDF2 at 100_000 iterations costs tens of milliseconds, far
	// more than HTTP jitter, so "the reply came back fast" would still spell "that name is taken".
	// The service therefore hashes BEFORE it looks, and throws the hash away when the name turns
	// out to be taken. Asserted as a LOWER bound against the measured cost of one hash — a lower
	// bound is the robust shape here: a loaded machine makes things slower, never faster, so this
	// cannot flake upward.
	[Fact]
	public async Task CreateNew_pays_the_password_hash_even_when_the_username_is_taken()
	{
		SeedUser("taken", quota: 5);

		AdminPasswordHasher.Hash("warm-up-the-jit");
		var hashCost = TimeSpan.MaxValue;
		for (var i = 0; i < 3; i++)
		{
			var sw = Stopwatch.StartNew();
			AdminPasswordHasher.Hash("baseline");
			if (sw.Elapsed < hashCost) hashCost = sw.Elapsed;
		}

		// Warm the query path too, so the first-call cost of building the linq2db query is not what
		// this ends up measuring.
		await _svc.AddMemberAsync(Ws, "taken", AddMemberMode.CreateNew, "pw", 1, WorkspaceRole.Member);

		var takenCost = TimeSpan.MaxValue;
		for (var i = 0; i < 3; i++)
		{
			var sw = Stopwatch.StartNew();
			var outcome = await _svc.AddMemberAsync(Ws, "taken", AddMemberMode.CreateNew, "pw", 1, WorkspaceRole.Member);
			if (sw.Elapsed < takenCost) takenCost = sw.Elapsed;
			outcome.Should().Be(AddMemberOutcome.AlreadyMember);
		}

		takenCost.Should().BeGreaterThan(hashCost * 0.5,
			$"the taken-name path must still pay for the hash (one hash ≈ {hashCost.TotalMilliseconds:F0} ms, "
			+ $"this path took {takenCost.TotalMilliseconds:F0} ms) — a fast answer here is the oracle, restated as a stopwatch");
	}

	// ---- (B) the silent WorkspaceQuota = 0 -------------------------------------------------

	// The refusal is pinned to UserAdminService.CreateAsync's OWN words, read out of the other
	// service at run time rather than copied here as a literal: the point of the card is that the
	// two account-creating paths obey ONE invariant, so the day someone reworded one of them this
	// test should notice.
	[Fact]
	public async Task CreateNew_without_an_allowance_is_refused_in_the_same_words_as_UserAdminService()
	{
		var users = new UserAdminService(_dbf, Options.Create(new AdminOptions()), _svc);
		var reference = (await users.CreateAsync("reference-user", "pw", workspaceQuota: null))
			.Should().BeOfType<UserChangeResult.Refused>().Subject;

		var page = Page();
		var result = await page.OnPostAddAsync(
			Ws, "newcomer", AddMemberMode.CreateNew, "pw", WorkspaceQuota: null, WorkspaceRole.Member, default);

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().Be(reference.Reason,
			"both paths create the same kind of account, so both owe the admin the same explicit decision");

		_db.Users.Any(u => u.Username == "newcomer").Should().BeFalse(
			"the account must not exist at all — least of all with an allowance nobody chose");
	}

	[Fact]
	public async Task CreateNew_writes_the_allowance_the_admin_typed()
	{
		(await _svc.AddMemberAsync(Ws, "three", AddMemberMode.CreateNew, "pw", 3, WorkspaceRole.Member))
			.Should().Be(AddMemberOutcome.Added);
		(await _svc.AddMemberAsync(Ws, "none", AddMemberMode.CreateNew, "pw", 0, WorkspaceRole.Member))
			.Should().Be(AddMemberOutcome.Added);

		_db.Users.Single(u => u.Username == "three").WorkspaceQuota.Should().Be(3);
		_db.Users.Single(u => u.Username == "none").WorkspaceQuota.Should().Be(0,
			"0 is a legitimate ANSWER — what the fix forbids is 0 arriving because nobody was asked");
	}

	[Fact]
	public async Task A_negative_allowance_is_refused_and_writes_nothing()
	{
		(await _svc.AddMemberAsync(Ws, "negative", AddMemberMode.CreateNew, "pw", -1, WorkspaceRole.Member))
			.Should().Be(AddMemberOutcome.QuotaInvalid);

		_db.Users.Should().BeEmpty();
		_db.MembershipRows().Should().BeEmpty();
	}

	[Fact]
	public async Task AddExisting_never_re_decides_an_existing_allowance()
	{
		SeedUser("taken", quota: 4);

		(await _svc.AddMemberAsync(Ws, "taken", AddMemberMode.AddExisting, null, null, WorkspaceRole.Viewer))
			.Should().Be(AddMemberOutcome.Added);

		_db.Users.Single(u => u.Username == "taken").WorkspaceQuota.Should().Be(4);
	}

	// ---- (C) the missing transaction -------------------------------------------------------

	// INJECTED, not argued. A BEFORE INSERT trigger on WorkspaceMembers makes the SECOND write
	// fail while the FIRST one has already happened — precisely the gap the card describes, and
	// the read of AlreadyMember sits inside it. Before the fix this left "orphan" in Users with no
	// membership row and a quota of 0; with both writes under one transaction it must leave
	// nothing at all.
	//
	// RAISE(ABORT) on purpose, not RAISE(ROLLBACK): ABORT undoes only the failing statement and
	// leaves the transaction open, so what this test observes is OUR rollback doing the work, not
	// SQLite doing it for us.
	[Fact]
	public async Task A_failure_between_the_two_inserts_leaves_no_orphan_account()
	{
		_db.Execute(
			"CREATE TRIGGER injected_member_insert_failure BEFORE INSERT ON WorkspaceMembers "
			+ "BEGIN SELECT RAISE(ABORT, 'injected failure between the two inserts'); END;");

		var act = async () => await _svc.AddMemberAsync(
			Ws, "orphan", AddMemberMode.CreateNew, "pw", 3, WorkspaceRole.Member);

		(await act.Should().ThrowAsync<SqliteException>()).WithMessage("*injected failure*",
			"the injection must be what failed — otherwise this test proves nothing");

		_db.Users.Any(u => u.Username == "orphan").Should().BeFalse(
			"the account insert and the membership insert are one act: if the second cannot happen, "
			+ "the first must not survive — an account with no membership is invisible on the members "
			+ "page and cannot be cleaned up from it");
		_db.MembershipRows().Should().BeEmpty();
	}

	// The control for the test above: without the trigger the very same call writes BOTH rows. It
	// rules out a green that comes from the call failing for some unrelated reason, or from the
	// create path having quietly stopped working.
	[Fact]
	public async Task Without_the_injected_failure_the_same_call_writes_both_rows()
	{
		(await _svc.AddMemberAsync(Ws, "orphan", AddMemberMode.CreateNew, "pw", 3, WorkspaceRole.Member))
			.Should().Be(AddMemberOutcome.Added);

		var user = _db.Users.Single(u => u.Username == "orphan");
		user.WorkspaceQuota.Should().Be(3);
		_db.MembershipRows().Should().ContainSingle().Which.UserId.Should().Be(user.Id);
	}

	// ---- (D) username trim (add-member-hardening-cluster #1) --------------------------------

	// THE scenario named in the card: a post of " alice" in CreateNew mode, against an EXISTING
	// "alice", must find her and grant membership — not miss her (untrimmed comparison) and insert
	// a second, visually-indistinguishable account.
	[Fact]
	public async Task CreateNew_with_a_padded_username_finds_the_existing_account_instead_of_duplicating_it()
	{
		var aliceId = SeedUser("alice", quota: 5);

		var outcome = await _svc.AddMemberAsync(Ws, " alice", AddMemberMode.CreateNew, "pw", 7, WorkspaceRole.Member);

		outcome.Should().Be(AddMemberOutcome.Added, "the trimmed name matches the existing account");
		_db.Users.Count(u => u.Username == "alice").Should().Be(1,
			"a padded post must not create a second, visually-indistinguishable account");
		_db.Users.Any(u => u.Username == " alice").Should().BeFalse("the untrimmed name must never be stored");
		_db.Users.Single().WorkspaceQuota.Should().Be(5, "the existing account's own allowance is untouched");
		_db.MembershipRows().Should().ContainSingle().Which.UserId.Should().Be(aliceId);
	}

	// The AddExisting half of the same defect: untrimmed, " alice" would not match "alice" and the
	// mode would answer NoSuchUser (silently) instead of granting the membership that was asked for.
	[Fact]
	public async Task AddExisting_with_a_padded_username_finds_the_existing_account()
	{
		var aliceId = SeedUser("alice", quota: 5);

		var outcome = await _svc.AddMemberAsync(Ws, "alice ", AddMemberMode.AddExisting, null, null, WorkspaceRole.Member);

		outcome.Should().Be(AddMemberOutcome.Added);
		_db.MembershipRows().Should().ContainSingle().Which.UserId.Should().Be(aliceId);
	}

	// Padding must not leak through into a NEW account's stored name either.
	[Fact]
	public async Task CreateNew_with_a_padded_username_stores_the_trimmed_name_for_a_brand_new_account()
	{
		(await _svc.AddMemberAsync(Ws, "  fresh  ", AddMemberMode.CreateNew, "pw", 1, WorkspaceRole.Member))
			.Should().Be(AddMemberOutcome.Added);

		_db.Users.Single().Username.Should().Be("fresh");
	}

	// ---- (E) workspace existence (add-member-hardening-cluster #2) --------------------------

	// A workspaceKey with no Workspaces row must refuse before writing anything — a membership row
	// on a key nobody owns would be an Admin-slot spend the ledger can never reclaim.
	[Fact]
	public async Task A_nonexistent_workspace_is_refused_and_writes_nothing()
	{
		SeedUser("alice", quota: 5);

		var outcome = await _svc.AddMemberAsync(
			"no-such-workspace", "alice", AddMemberMode.AddExisting, null, null, WorkspaceRole.Member);

		outcome.Should().Be(AddMemberOutcome.NoSuchWorkspace);
		_db.MembershipRows().Should().BeEmpty("nothing is granted against a workspace that does not exist");
	}

	[Fact]
	public async Task A_nonexistent_workspace_in_CreateNew_mode_creates_no_account_either()
	{
		var outcome = await _svc.AddMemberAsync(
			"no-such-workspace", "newcomer", AddMemberMode.CreateNew, "pw", 3, WorkspaceRole.Member);

		outcome.Should().Be(AddMemberOutcome.NoSuchWorkspace);
		_db.Users.Any(u => u.Username == "newcomer").Should().BeFalse(
			"refusing the workspace must not still spend an account creation — nothing is written at all");
	}

	sealed class NoopTempDataProvider : ITempDataProvider
	{
		public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

		public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
	}
}

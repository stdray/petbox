using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Settings;
using PetBox.Web.Rendering;

namespace PetBox.Web.Settings;

// board-filters-server-state: active-only/sort are GLOBAL, cross-device [Setting] properties on
// BrowserState (Scope.User) — unlike the cookie-branch pin toggles (sidebar/kql), a DB write needs
// a server round trip, so ts/board.ts's persistFilterPrefs fires a fire-and-forget POST here on
// change instead of writing a cookie through ui-state.ts. Modeled on WorkspaceSwitchEndpoint/
// ProjectSwitchEndpoint (same DisableAntiforgery() call — a lightweight preference toggle, not a
// state-changing action worth a token round trip) rather than a TaskBoardModel page handler: this
// isn't board-scoped (the preference applies to every board), so it doesn't belong on one board's
// page model.
public static class BoardFilterPrefsEndpoint
{
	public static void MapBoardFilterPrefs(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/ui/board-filter-prefs", Save)
			.RequireAuthorization()
			.DisableAntiforgery();
	}

	static async Task<IResult> Save(
		HttpContext ctx, ISettingsResolver settings,
		[FromForm] bool? activeOnly, [FromForm] string? sortBy, [FromForm] bool? sortDesc,
		CancellationToken ct)
	{
		var userIdRaw = ctx.User.FindFirst(PetBoxClaims.UserId)?.Value;
		if (!long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
			return Results.Unauthorized();
		var userIdString = userId.ToString(CultureInfo.InvariantCulture);

		// An unrecognized sortBy (typo, stale client, a removed key) is rejected rather than
		// silently stored — TaskBoardModel's own resolution already tolerates a stale DB value
		// (falls back to "priority"), but there's no reason to let a bad write happen when we can
		// catch it here instead.
		if (sortBy is not null && !BoardSortKeys.IsKnown(sortBy))
			return Results.BadRequest(new { error = $"Unknown sortBy '{sortBy}'." });

		var old = await settings.GetAsync<BoardPreferences>(Scope.User, userIdString, ct);
		var updated = old with
		{
			ActiveOnly = activeOnly ?? old.ActiveOnly,
			SortBy = sortBy ?? old.SortBy,
			SortDesc = sortDesc ?? old.SortDesc,
		};
		await settings.SetAsync(Scope.User, userIdString, updated, old, userId, ct);
		return Results.Ok();
	}
}

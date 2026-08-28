using PetBox.Tasks.Contract;

namespace PetBox.Web.Pages.Shared;

// The observation-only recurrence/regression signal (spec observation-recurrence-visible-on-card
// / observation-regression-signalled-on-card), shared by the board card (_TaskNodeCard), the flat
// table row (_TaskTable) and the node detail page (TaskBoardNode) so the "what makes an
// observation different from a task" rendering rule lives in ONE place, like _DeliveryBadge /
// _BlockedByChips before it. `Observation` is null on every non-observation board (TaskNodeView's
// own contract — TasksService only loads it when the OWNING board's kind is `observation`), so
// every caller can pass it unconditionally and the partials below are simply no-ops elsewhere.
//
// Two independent signals, rendered by two SEPARATE partials (spec
// observation-regression-signalled-on-card: a regression must be "отличим от обычного счётчика
// рецидива" — a distinct, noticeable element, not just another badge sharing the row):
//   - _ObservationRecurrenceBadge: a compact "×N, last seen …" badge, shown only when
//     RecurrenceCount > 1.
//   - _ObservationRegressionBanner: a standalone alert banner ("recurred after fix"), linking to
//     the node that (supposedly) fixed it, shown only when RecurredAfterFixAt is set — the single
//     highest-value signal the whole mechanism exists to surface (work
//     observation-recurrence-after-fix-signal).
// WorkspaceKey/ProjectKey are needed for routing (the regression banner's fixed-by link, whichever
// form it takes). `FixedByLink` is the CALLER-resolved slug for Observation.FixedByNodeId
// (ObservationFixedByResolver, reusing the same ITasksService.GetNodeAsync door the exhaustive
// relations panel resolves through) — resolution is I/O, so it happens once per page in the page
// model, never inside this pure-render partial. Null when there is nothing to resolve (no
// FixedByNodeId) OR the resolver wasn't wired by this caller; the banner then falls back to the
// opaque Routes.TaskBoardNode(id) route rather than failing to render.
public sealed record ObservationSignalModel(ObservationSignalView? Observation, string WorkspaceKey, string ProjectKey, LinkDto? FixedByLink = null);

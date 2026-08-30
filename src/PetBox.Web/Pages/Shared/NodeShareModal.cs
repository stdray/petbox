namespace PetBox.Web.Pages.Shared;

// The identity _NodeShareModal.cshtml puts on the <dialog> so every opener on the page (the node
// header button and each per-comment share button) carries only what DIFFERS between them —
// the scope, and a comment id — instead of repeating project/board/node on every comment row.
//
// A record rather than three loose ViewData entries so a caller cannot silently omit one: the
// mint request (POST /api/share/node) needs all three, and a missing board would fail server-side
// at click time instead of at compile time here.
public sealed record NodeShareModalModel(string ProjectKey, string Board, string NodeId);

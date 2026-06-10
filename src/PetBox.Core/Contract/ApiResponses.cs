namespace PetBox.Core.Contract;

// The uniform REST error envelope. Every PetBox-owned endpoint answers errors as
// {"error": "..."}; endpoints with structured extras declare their own record with
// `Error` as the first property so the base shape stays recognizable to clients.
public sealed record ErrorResponse(string Error);

// Bare acknowledgement: {"ok": true}.
public sealed record OkResponse(bool Ok);

// Soft-delete acknowledgement: {"deleted": true}.
public sealed record DeletedResponse(bool Deleted);

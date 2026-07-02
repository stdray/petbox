using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// The board kind formerly called `free` is renamed `simple` (a lightweight preset with a
// fixed status/type vocab + free transitions, not "anything goes"). Rename stored rows so
// the UI badge + reads are consistent; new boards default to `simple`. MethodologyPresets.ParseKind
// also maps a stray "free" → Simple, so this is belt-and-suspenders, not load-bearing.
[Migration(29, "Rename TaskBoards.Kind 'free' -> 'simple'")]
public sealed class M029_RenameFreeKindToSimple : Migration
{
	public override void Up() =>
		Execute.Sql("UPDATE TaskBoards SET Kind = 'simple' WHERE Kind = 'free';");

	public override void Down() =>
		Execute.Sql("UPDATE TaskBoards SET Kind = 'free' WHERE Kind = 'simple';");
}

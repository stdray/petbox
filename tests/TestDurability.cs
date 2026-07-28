using System.Runtime.CompilerServices;
using PetBox.Core.Data;

namespace PetBox.TestSupport;

// THE ONLY place in the repository that relaxes SQLite durability. Compiled into the test
// assemblies (linked from tests/PetBox.Tests and tests/PetBox.E2ETests — see their csproj);
// nothing under src/ assigns SqliteDurability.Relaxed, which is what keeps a deployed PetBox on the
// durability each tier CHOSE for itself (SqliteTier.Durable → FULL for user data and state,
// SqliteTier.Telemetry → NORMAL for logs). SqliteDurabilityTests proves both halves by reading the
// pragma back through the production factory of every tier.
//
// Setting this property is a blunt instrument on purpose: it outranks every tier at once, which is
// only ever right for a host whose entire database is disposable. A test host is that; nothing else
// in the repository is.
//
// OFF rather than NORMAL: NORMAL still fsyncs at every WAL checkpoint, and the suite checkpoints
// constantly (TestDirs.ResetDbFile does a wal_checkpoint(TRUNCATE) per reset). The thing a test
// would buy with either is durability across a crash of the test host, and a test host that
// crashes leaves a half-populated temp directory that the next run deletes regardless.
static class TestDurability
{
	public const string Synchronous = "OFF";

	[ModuleInitializer]
	internal static void Relax() => SqliteDurability.Relaxed = Synchronous;
}

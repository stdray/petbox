namespace PetBox.Tests.Auth;

// Isolates AddMemberCompositeFixTests from every OTHER xUnit collection in this process — not
// for a shared fixture (there is none), but because CreateNew_pays_the_password_hash_even_when
// _the_username_is_taken asserts a DELTA on AdminPasswordHasher.HashCallCount, a process-global
// counter. Other test classes in this assembly call AdminPasswordHasher.Hash directly or
// indirectly (AdminPasswordHasherTests, CredentialAuthenticatorTests, and anything that drives
// UserAdminService.CreateAsync/ChangePasswordAsync or AccountSelfService.ChangePasswordAsync),
// and xunit.runner.json turns on parallelizeTestCollections — every class with no [Collection]
// attribute gets its own collection and those collections run CONCURRENTLY with each other by
// default. Without this isolation, a concurrent unrelated Hash() call landing inside this one
// test's measurement window would push the delta up by exactly the amount needed to hide a
// REAL regression (the taken-name branch skipping its own hash) behind someone else's hash —
// see auth-hash-cost-test-is-a-wallclock-flake-in-the-gate for the review round that caught
// this: the ">= 1, never == 1" framing correctly rejects noise but does NOT, on its own, stop
// noise from masking a true zero.
//
// DisableParallelization = true on a named collection is the fix for the right, narrower
// problem than it looks: the ORIGINAL flake in this same test (Stopwatch-based, see git
// history) was INTER-process — PetBox.Tests vs. the separately-run PetBox.E2ETests binary — and
// no xUnit attribute reaches across a process boundary, which is why that approach was rejected
// for THAT problem. This counter is process-global (a `static long` in PetBox.Core, loaded once
// per PetBox.Tests process), so the contamination it can see is only ever INTRA-process, from
// sibling collections in the same run — exactly what DisableParallelization stops. Verified
// empirically (a throwaway xUnit v3 project, three concurrent default-collection classes plus
// one DisableParallelization=true collection) that the isolated collection's tests never
// overlap, in either direction, with any other collection's tests.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AddMemberCompositeFixCollectionDef
{
	public const string Name = "AddMemberCompositeFix-HashCallCount";
}

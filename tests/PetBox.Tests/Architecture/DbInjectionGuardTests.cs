using System.Reflection;
using LinqToDB.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Deploy.Data;
using PetBox.Web;

namespace PetBox.Tests.Architecture;

// A linq2db DataConnection is NOT thread-safe. Registering one as AddScoped hands the SAME
// connection to every thread a request fans out onto — which is how the cross-scope search 500'd
// ("Must add values for the following parameters: @projectKey, @board", "Collection was modified",
// ObjectDisposedException: one race, several faces).
//
// Auditing call sites only makes that bug DETECTABLE. This guard makes it UNREPRESENTABLE: a db
// context that is NOT REGISTERED IN DI cannot be injected ANYWHERE — not into a constructor, not
// as a minimal-API handler parameter, not into an MCP tool method, not via GetRequiredService.
// The only way to a connection is `using var db = factory.Open()`, which is caller-owned by
// construction. That is the whole point: you cannot share what you cannot obtain.
//
// WHY NOT NetArchTest (which every other guard in this folder uses): it reasons about type
// DEPENDENCIES, not about constructor SIGNATURES or DI registrations. A class taking `DeployDb db`
// in its ctor "depends on" DeployDb exactly as much as one that calls a static on it, and a
// minimal-API handler taking it as a parameter is not a type dependency of any class at all. So the
// arch-test cannot see the categories that matter here. These two tests can:
//
//   1. DI-GUARD (the iron one) — compose the REAL production service collection and assert the type
//      has no descriptor. Closes every injection channel at once, including the ones reflection
//      over ctors never sees.
//   2. CTOR-GUARD (for a legible failure) — walk every product ctor and name the offending class.
//      Redundant with (1) by construction, but "DeployService takes a DeployDb" is a far better
//      error message than "a descriptor exists".
public sealed class DbInjectionGuardTests
{
	// Contexts that must be reachable ONLY through their factory.
	//
	// TODO(core-db-behind-factory): PetBoxDb belongs in this list too — it is the other half of the
	// same bug and the other half of this refactor. It stays OUT until its ~190 call sites are moved
	// onto ICoreDbFactory; the moment the last `AddScoped<PetBoxDb>` consumer is gone, add
	// typeof(PetBoxDb) here and delete this comment. Until then PetBoxDb is knowingly allowlisted:
	// the guard is honest about what it does and does not yet hold.
	static readonly Type[] MustNotBeInjectable = [typeof(DeployDb)];

	// Types permitted to mention a guarded context in a ctor parameter. A FACTORY is the one thing
	// that legitimately produces them — though note the factories take DataOptions/a connection
	// string, not a context, so this list is empty in practice. It exists so that the intended
	// escape hatch is explicit rather than improvised.
	static readonly string[] CtorAllowlist =
	[
		typeof(DeployDbFactory).FullName!,
		typeof(CoreDbFactory).FullName!,
	];

	// Every PetBox product assembly: Web plus each PetBox.* it references. Anchoring on Web is
	// enough — it is the composition root, so it references them all.
	static readonly Assembly[] ProductAssemblies = LoadProductAssemblies();

	static Assembly[] LoadProductAssemblies()
	{
		var web = typeof(Program).Assembly;
		return web.GetReferencedAssemblies()
			.Where(n => n.Name?.StartsWith("PetBox.", StringComparison.Ordinal) == true)
			.Select(Assembly.Load)
			.Append(web)
			.DistinctBy(a => a.FullName)
			.ToArray();
	}

	// The REAL production composition, exactly as CaptiveDependencyTests builds it: every feature on,
	// so a registration cannot hide behind a disabled module.
	static IServiceCollection ComposeProduction()
	{
		var builder = WebApplication.CreateBuilder();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
			["Features:Config"] = "true",
			["Features:Logging"] = "true",
			["Features:Data"] = "true",
			["Features:Dashboard"] = "true",
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
			["Features:LlmRouter"] = "true",
			["Features:Deploy"] = "true",
		});

		Program.ConfigureServices(builder);
		return builder.Services;
	}

	[Fact]
	public void GuardedContexts_AreNotRegisteredInDi()
	{
		var services = ComposeProduction();

		var offenders = services
			.Where(d => MustNotBeInjectable.Contains(d.ServiceType)
				|| (d.ImplementationType is { } impl && MustNotBeInjectable.Contains(impl)))
			.Select(d => $"{d.ServiceType.Name} ({d.Lifetime})")
			.ToList();

		offenders.Should().BeEmpty(
			"a db context registered in DI can be injected ANYWHERE — a ctor, a minimal-API handler "
			+ "parameter, an MCP tool method, GetRequiredService — and a linq2db DataConnection is not "
			+ "thread-safe, so a scoped one is shared by every thread the request fans out onto. Reach "
			+ "it through its factory instead (`using var db = factory.Open()`). Offenders: "
			+ string.Join(", ", offenders));
	}

	[Fact]
	public void NoConstructor_TakesAGuardedContext()
	{
		var offenders = (
			from asm in ProductAssemblies
			from type in SafeGetTypes(asm)
			where !CtorAllowlist.Contains(type.FullName)
			from ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
			from p in ctor.GetParameters()
			where MustNotBeInjectable.Contains(p.ParameterType)
			select $"{type.FullName}..ctor({p.ParameterType.Name} {p.Name})")
			.Distinct()
			.ToList();

		offenders.Should().BeEmpty(
			"a guarded db context must be reached through its factory, never taken as a constructor "
			+ "dependency — an injected DataConnection is a connection you did not open and therefore "
			+ "do not own, shared with whoever else got the same scope. Take the factory and "
			+ "`using var db = factory.Open()` per call. Offenders:\n" + string.Join("\n", offenders));
	}

	// Guard-the-guard: if the assembly sweep or the composition ever silently produced nothing, both
	// tests above would pass by vacuity and stop protecting anything.
	[Fact]
	public void TheGuard_ActuallyInspectsSomething()
	{
		ProductAssemblies.Should().HaveCountGreaterThan(5, "the ctor sweep must cover the product assemblies");

		SafeGetTypes(typeof(DeployDb).Assembly).Should().Contain(typeof(DeployDb),
			"the Deploy assembly must be in the swept set");

		ComposeProduction().Should().HaveCountGreaterThan(50, "the DI guard must inspect the real composition");

		// The guarded contexts really are DataConnections — i.e. this guard is pointed at the
		// thread-unsafe thing it claims to be pointed at.
		MustNotBeInjectable.Should().OnlyContain(t => typeof(DataConnection).IsAssignableFrom(t));
	}

	static IEnumerable<Type> SafeGetTypes(Assembly asm)
	{
		try { return asm.GetTypes(); }
		catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
	}
}

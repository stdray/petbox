// ILProbe — worker A, 2026-08-20.
// The control experiment for the NDepend failure cases: read the SAME repository from the IL side
// (bin/**, System.Reflection.Metadata) and show which member-level edges the compiler actually
// wrote down. Not a Roslyn measurement — this is the `IL` source of 01-legend.md, measured directly,
// so the claim "IL cannot carry this edge" stops being an assertion.

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var root = args.Length > 0 ? args[0] : throw new ArgumentException("pass the repo root");
var dlls = Directory.EnumerateFiles(root, "PetBox*.dll", SearchOption.AllDirectories)
	.Where(p => p.Replace('\\', '/').Contains("/bin/Debug/", StringComparison.Ordinal))
	.Where(p => !p.Replace('\\', '/').Contains("/ref/", StringComparison.Ordinal))
	.DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
	.OrderBy(p => p, StringComparer.Ordinal)
	.ToList();

Console.WriteLine($"assemblies scanned: {dlls.Count}");

// Member-level references the IL of every PetBox assembly actually contains, keyed by "Type.Member".
var refs = new Dictionary<string, int>(StringComparer.Ordinal);
var fieldDefs = new HashSet<string>(StringComparer.Ordinal);

foreach (var dll in dlls)
{
	using var fs = File.OpenRead(dll);
	using var pe = new PEReader(fs);
	if (!pe.HasMetadata) continue;
	var md = pe.GetMetadataReader();

	foreach (var h in md.MemberReferences)
	{
		var mr = md.GetMemberReference(h);
		var name = md.GetString(mr.Name);
		var parent = ParentName(md, mr.Parent);
		Add($"{parent}.{name}");
	}
	// intra-assembly member access shows up as FieldDefinition/MethodDefinition handles in IL bodies;
	// record every field DEFINITION so we can tell "field exists" from "field is referenced".
	foreach (var th in md.TypeDefinitions)
	{
		var td = md.GetTypeDefinition(th);
		var tn = $"{md.GetString(td.Namespace)}.{md.GetString(td.Name)}";
		foreach (var fh in td.GetFields())
			fieldDefs.Add($"{tn}.{md.GetString(md.GetFieldDefinition(fh).Name)}");
	}
}

string[] probes =
[
	// (a) const string members — the PetBoxClaims case
	"PetBox.Core.Auth.PetBoxClaims.UserId",
	"PetBox.Core.Auth.PetBoxClaims.IsSysAdmin",
	"PetBox.Core.Auth.PetBoxClaims.WorkspaceRoles",
	"PetBox.Core.Auth.PetBoxClaims.ActiveWorkspace",
	// (b) enum members reached through `case`, `return` and a switch-expression arm
	"PetBox.Core.Auth.ProjectAccess.SandboxContainment",
	"PetBox.Core.Auth.ProjectAccess.ClaimMismatch",
	"PetBox.Core.Settings.Scope.Service",
	"PetBox.Core.Settings.Scope.Project",
	// (c) CONTROL — ordinary methods/types used exactly as often, which IL DOES record
	"PetBox.Core.Auth.ProjectScope.EvaluateAsync",
	"PetBox.Core.Auth.ApiKeyScopes.Granted",
	"PetBox.Core.Auth.TenantAuthorizer.AuthorizeAsync",
];

Console.WriteLine();
Console.WriteLine("member                                                        il_refs  field_def_exists");
foreach (var p in probes)
	Console.WriteLine($"{p,-60}  {refs.GetValueOrDefault(p),7}  {(fieldDefs.Contains(p) ? "yes" : "-")}");

Console.WriteLine();
Console.WriteLine($"total distinct member-refs across all PetBox assemblies: {refs.Count}");
Console.WriteLine($"total field definitions across all PetBox assemblies:    {fieldDefs.Count}");

void Add(string k) => refs[k] = refs.GetValueOrDefault(k) + 1;

static string ParentName(MetadataReader md, EntityHandle h) => h.Kind switch
{
	HandleKind.TypeReference => TypeRefName(md, (TypeReferenceHandle)h),
	HandleKind.TypeDefinition => TypeDefName(md, (TypeDefinitionHandle)h),
	HandleKind.TypeSpecification => "<typespec>",
	_ => "<other>",
};

static string TypeRefName(MetadataReader md, TypeReferenceHandle h)
{
	var tr = md.GetTypeReference(h);
	var ns = md.GetString(tr.Namespace);
	var n = md.GetString(tr.Name);
	return string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}";
}

static string TypeDefName(MetadataReader md, TypeDefinitionHandle h)
{
	var td = md.GetTypeDefinition(h);
	var ns = md.GetString(td.Namespace);
	var n = md.GetString(td.Name);
	return string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}";
}

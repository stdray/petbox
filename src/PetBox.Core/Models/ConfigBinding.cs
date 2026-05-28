using LinqToDB.Mapping;

namespace PetBox.Core.Models;

public enum BindingKind { Plain, Secret }

[Table("ConfigBindings")]
public sealed record ConfigBinding
{
	[Column, Identity, PrimaryKey]
	public long Id { get; init; }
	[Column, NotNull]
	public string Path { get; init; } = string.Empty;
	[Column, NotNull]
	public string Value { get; init; } = string.Empty;
	[Column, NotNull]
	public string Tags { get; init; } = string.Empty;
	[Column]
	public BindingKind Kind { get; init; } = BindingKind.Plain;
	[Column]
	public string? Ciphertext { get; init; }
	[Column]
	public string? Iv { get; init; }
	[Column]
	public string? AuthTag { get; init; }
	[Column]
	public int Version { get; init; } = 1;
	[Column]
	public string ContentHash { get; init; } = string.Empty;
	[Column]
	public bool IsDeleted { get; init; }
	[Column]
	public DateTime? DeletedAt { get; init; }
	[Column]
	public DateTime CreatedAt { get; init; }
	[Column]
	public DateTime UpdatedAt { get; init; }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PetBox.Config;

public sealed record SecretBundle(string Ciphertext, string Iv, string AuthTag);

public sealed record SecretEncryptorOptions
{
	public string? MasterKey { get; init; }
}

public interface ISecretEncryptor
{
	bool IsAvailable { get; }
	SecretBundle Encrypt(string plaintext);
	string Decrypt(string ciphertextB64, string ivB64, string authTagB64);
}

public sealed class AesGcmSecretEncryptor : ISecretEncryptor
{
	const int IvBytes = 12;
	const int TagBytes = 16;

	readonly byte[]? _key;

	public AesGcmSecretEncryptor(IOptions<SecretEncryptorOptions> options)
	{
		var raw = options.Value.MasterKey;
		if (string.IsNullOrWhiteSpace(raw))
		{
			_key = null;
			return;
		}

		try
		{
			_key = Convert.FromBase64String(raw);
		}
		catch (FormatException)
		{
			_key = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
		}

		if (_key.Length != 32)
		{
			_key = SHA256.HashData(_key);
		}
	}

	public bool IsAvailable => _key is not null;

	public SecretBundle Encrypt(string plaintext)
	{
		if (_key is null)
			throw new InvalidOperationException("PETBOX_MASTER_KEY is not configured.");

		var iv = RandomNumberGenerator.GetBytes(IvBytes);
		var plainBytes = Encoding.UTF8.GetBytes(plaintext);
		var cipher = new byte[plainBytes.Length];
		var tag = new byte[TagBytes];

		using var aes = new AesGcm(_key, TagBytes);
		aes.Encrypt(iv, plainBytes, cipher, tag);

		return new SecretBundle(
			Convert.ToBase64String(cipher),
			Convert.ToBase64String(iv),
			Convert.ToBase64String(tag));
	}

	public string Decrypt(string ciphertextB64, string ivB64, string authTagB64)
	{
		if (_key is null)
			throw new InvalidOperationException("PETBOX_MASTER_KEY is not configured.");

		var cipher = Convert.FromBase64String(ciphertextB64);
		var iv = Convert.FromBase64String(ivB64);
		var tag = Convert.FromBase64String(authTagB64);
		var plain = new byte[cipher.Length];

		using var aes = new AesGcm(_key, TagBytes);
		aes.Decrypt(iv, cipher, tag, plain);

		return Encoding.UTF8.GetString(plain);
	}
}

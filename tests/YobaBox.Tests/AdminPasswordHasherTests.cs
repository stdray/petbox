using YobaBox.Core.Auth;

namespace YobaBox.Tests;

public sealed class AdminPasswordHasherTests
{
	[Fact]
	public void Hash_RoundTrips()
	{
		var h = AdminPasswordHasher.Hash("s3cret");
		AdminPasswordHasher.Verify("s3cret", h).Should().BeTrue();
	}

	[Fact]
	public void Hash_WrongPassword_Rejects()
	{
		var h = AdminPasswordHasher.Hash("s3cret");
		AdminPasswordHasher.Verify("nope", h).Should().BeFalse();
	}

	[Fact]
	public void Hash_SameInput_DifferentHashes()
	{
		AdminPasswordHasher.Hash("same")
			.Should().NotBe(AdminPasswordHasher.Hash("same"), "salt is random");
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-a-hash")]
	public void Verify_MalformedHash_ReturnsFalse(string h)
	{
		AdminPasswordHasher.Verify("any", h).Should().BeFalse();
	}

	[Fact]
	public void Verify_EmptyPassword_ReturnsFalse()
	{
		var h = AdminPasswordHasher.Hash("x");
		AdminPasswordHasher.Verify("", h).Should().BeFalse();
	}
}

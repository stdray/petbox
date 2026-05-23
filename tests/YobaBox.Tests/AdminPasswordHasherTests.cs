using YobaBox.Core.Auth;

namespace YobaBox.Tests;

public sealed class AdminPasswordHasherTests
{
	[Fact]
	public void HashAndVerify_Roundtrip_ReturnsTrue()
	{
		var hash = AdminPasswordHasher.Hash("test-password");
		AdminPasswordHasher.Verify("test-password", hash).Should().BeTrue();
	}

	[Fact]
	public void Verify_WrongPassword_ReturnsFalse()
	{
		var hash = AdminPasswordHasher.Hash("test-password");
		AdminPasswordHasher.Verify("wrong-password", hash).Should().BeFalse();
	}
}

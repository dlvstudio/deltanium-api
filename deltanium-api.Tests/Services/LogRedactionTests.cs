using DeltaniumApi.Middleware;
using FluentAssertions;
using Xunit;

namespace deltanium_api.Tests.Services;

public class LogRedactionTests
{
    [Theory]
    [InlineData("{\"mnemonic\":\"abandon ability able\",\"email\":\"a@b.c\"}", "abandon")]
    [InlineData("{\"PrivateKey\": \"0011aabb\"}", "0011aabb")]
    [InlineData("{\"private_key\":\"ff\\\"ee\"}", "ff")]
    [InlineData("{\"internalToken\":\"db9xyz\"}", "db9xyz")]
    [InlineData("{\"x\":{\"superAdminPrivateKey\":12345}}", "12345")]
    [InlineData("/api/x?token=db9xyz&a=1", "db9xyz")]
    public void Secrets_never_reach_the_log(string input, string secret)
    {
        var redacted = RequestResponseLoggingMiddleware.Redact(input);
        redacted.Should().NotContain(secret);
        redacted.Should().Contain("***");
    }

    [Fact]
    public void Ordinary_fields_are_kept()
    {
        var s = "{\"publicKey\":\"02ab\",\"amount\":100,\"signature\":\"MEQ\"}";
        RequestResponseLoggingMiddleware.Redact(s).Should().Be(s);
    }
}

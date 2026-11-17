using System;
using System.Security.Cryptography;
using System.Text;
using DeltaniumApi.Services;
using FluentAssertions;
using NBitcoin;
using Xunit;

namespace DeltaniumApi.Tests.Services;

[Collection("NetworkScope")]
public class NetworkScopeTests : IDisposable
{
    public void Dispose() => NetworkScope.Id = "";

    private static string SignRaw(Key key, string message) =>
        Convert.ToBase64String(key.Sign(new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(message)))).ToDER());

    [Fact]
    public void Mainnet_UserSignatureOverPlainMessage_StillVerifies()
    {
        var key = new Key();
        var msg = SignatureAuthHelper.BuildSignedMessage("POST", "/api/tip", "1700000000", "{}");
        EconomicsService.VerifyUserSignature(key.PubKey.ToHex(), msg, SignRaw(key, msg)).Should().BeTrue();
    }

    [Fact]
    public void TestnetSignature_IsRejectedOnMainnet_AndMainnetOnTestnet()
    {
        var key = new Key();
        var msg = SignatureAuthHelper.BuildSignedMessage("POST", "/api/wallet/transfer", "1700000000", "{\"amount\":1}");
        var mainSig = SignRaw(key, msg);
        var testSig = SignRaw(key, "deltanium-test\n" + msg);

        EconomicsService.VerifyUserSignature(key.PubKey.ToHex(), msg, testSig).Should().BeFalse();

        NetworkScope.Id = "deltanium-test";
        EconomicsService.VerifyUserSignature(key.PubKey.ToHex(), msg, testSig).Should().BeTrue();
        EconomicsService.VerifyUserSignature(key.PubKey.ToHex(), msg, mainSig).Should().BeFalse();
    }

    [Fact]
    public void BuildInfo_ReportsNetwork()
    {
        NetworkScope.Id = "deltanium-test";
        var json = System.Text.Json.JsonSerializer.Serialize(BuildInfo.Describe("api"));
        json.Should().Contain("\"network\":\"deltanium-test\"");
    }
}

[CollectionDefinition("NetworkScope", DisableParallelization = true)]
public class NetworkScopeCollection { }

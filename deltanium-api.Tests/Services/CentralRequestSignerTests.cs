using System;
using System.Net.Http;
using DeltaniumApi.Services;
using FluentAssertions;
using Xunit;

namespace deltanium_api.Tests.Services
{
    public class CentralRequestSignerTests
    {
        [Fact]
        public void Message_format_matches_the_Store_verifier()
        {
            // Must stay identical to DeltaniumStore.Security.CentralRequestAuth.BuildMessage.
            CentralRequestSigner.BuildMessage("post", "Store-1.Example", "/api/proof/challenge", "1700000000", "ab")
                .Should().Be("DELTANIUM-CENTRAL\nPOST\nstore-1.example\n/api/proof/challenge\n1700000000\nab");
        }

        [Fact]
        public void Signed_request_names_its_target_host_and_verifies_with_the_Central_key()
        {
            var keys = new KeyService();
            var req = new HttpRequestMessage(HttpMethod.Post, "https://store-1.example/api/proof/egress/drain");
            var now = DateTimeOffset.UtcNow;
            CentralRequestSigner.Sign(req, "", keys, now);

            var ts = string.Join("", req.Headers.GetValues(CentralRequestSigner.TimestampHeader));
            var sig = string.Join("", req.Headers.GetValues(CentralRequestSigner.SignatureHeader));
            ts.Should().Be(now.ToUnixTimeSeconds().ToString());
            var msg = CentralRequestSigner.BuildMessage("POST", "store-1.example", "/api/proof/egress/drain", ts, CentralRequestSigner.BodyHash(""));
            keys.VerifySignature(keys.GetCentralPublicKey(), msg, sig).Should().BeTrue();
            var otherStore = CentralRequestSigner.BuildMessage("POST", "store-2.example", "/api/proof/egress/drain", ts, CentralRequestSigner.BodyHash(""));
            keys.VerifySignature(keys.GetCentralPublicKey(), otherStore, sig).Should().BeFalse();
        }
    }
}

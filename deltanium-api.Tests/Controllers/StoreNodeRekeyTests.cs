using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumApi.Controllers;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NBitcoin;
using Xunit;

namespace deltanium_api.Tests.Controllers
{
    [Collection("SuperAdminEnv")]
    public class StoreNodeRekeyTests
    {
        private readonly Mock<IStorageService> _storage = new();
        private readonly Key _superAdmin = new();
        private readonly KeyService _keys;
        private readonly string _oldKey = new Key().PubKey.ToHex();
        private readonly string _newKey = new Key().PubKey.ToHex();

        public StoreNodeRekeyTests()
        {
            Environment.SetEnvironmentVariable("SUPER_ADMIN_PUBLIC_KEY", _superAdmin.PubKey.ToHex());
            Environment.SetEnvironmentVariable("SUPER_ADMIN_PRIVATE_KEY", null);
            _keys = new KeyService();
            _storage.Setup(s => s.GetNodesAsync()).ReturnsAsync(() => new List<StoreNode>
            {
                new() { Id = "s1", PublicKey = _oldKey, Endpoint = "https://store-1.example" },
                new() { Id = "s2", PublicKey = "02" + new string('1', 64), Endpoint = "https://store-2.example" },
            });
            _storage.Setup(s => s.UpdateNodePublicKeyAsync("s1", It.IsAny<string>())).ReturnsAsync(true);
        }

        private StoreNodeController Controller(string body, Key? signer, string? tsOverride = null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/storenode/admin/rekey";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            if (signer != null)
            {
                var ts = tsOverride ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var msg = SignatureAuthHelper.BuildSignedMessage("POST", "/api/storenode/admin/rekey", ts, body);
                var hash = new uint256(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(NetworkScope.Scope(msg))));
                ctx.Request.Headers["X-Timestamp"] = ts;
                ctx.Request.Headers["X-SuperAdmin-Request-Signature"] = Convert.ToBase64String(signer.Sign(hash).ToDER());
            }
            return new StoreNodeController(_keys, _storage.Object, null!, null!, new SignatureAuthHelper(_keys, _storage.Object))
            {
                ControllerContext = new ControllerContext { HttpContext = ctx }
            };
        }

        private string Body(string id, string pk) => JsonSerializer.Serialize(new { nodeId = id, publicKey = pk });

        [Fact]
        public async Task Super_Admin_can_replace_a_Store_key()
        {
            var r = await Controller(Body("s1", _newKey), _superAdmin).Rekey();
            r.Should().BeOfType<OkObjectResult>();
            _storage.Verify(s => s.UpdateNodePublicKeyAsync("s1", _newKey), Times.Once);
        }

        [Fact]
        public async Task Anyone_else_is_refused_and_nothing_changes()
        {
            (await Controller(Body("s1", _newKey), null).Rekey()).Should().BeOfType<UnauthorizedObjectResult>();
            (await Controller(Body("s1", _newKey), new Key()).Rekey()).Should().BeOfType<UnauthorizedObjectResult>();
            _storage.Verify(s => s.UpdateNodePublicKeyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Expired_signature_is_refused()
        {
            var old = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds().ToString();
            (await Controller(Body("s1", _newKey), _superAdmin, old).Rekey()).Should().BeOfType<UnauthorizedObjectResult>();
        }

        [Fact]
        public async Task A_key_owned_by_another_Store_is_refused()
        {
            var r = await Controller(Body("s1", "02" + new string('1', 64)), _superAdmin).Rekey();
            r.Should().Match<IActionResult>(x => x is ConflictObjectResult || x is BadRequestObjectResult);
            _storage.Verify(s => s.UpdateNodePublicKeyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Unknown_node_is_404()
        {
            (await Controller(Body("nope", _newKey), _superAdmin).Rekey()).Should().BeOfType<NotFoundObjectResult>();
        }
    }
}

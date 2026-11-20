using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using DeltaniumApi.Services.Dltp;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace DeltaniumApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BlockCreatorNodeController : ControllerBase
    {
        private readonly KeyService _keyService;
        private readonly IStorageService _storageService;

        public class NodeRegistrationRequest
        {
            /// <summary>HTTPS endpoint (optional when TcpEndpoint is given).</summary>
            public string? Endpoint { get; set; }
            /// <summary>DLTP endpoint tcps://host:port (optional when Endpoint is given).</summary>
            public string? TcpEndpoint { get; set; }
            public string Voucher { get; set; }
            public string PublicKey { get; set; }
        }

        public class NodeRegistrationResponse
        {
            public string Id { get; set; }
            public string PublicKey { get; set; }
            public string Endpoint { get; set; }
            public string? TcpEndpoint { get; set; }
        }

        public class DecisionRequest
        {
            public string TxId { get; set; } = string.Empty;
            public string BlockerPubKey { get; set; } = string.Empty;
        }

        public class DecisionPayload
        {
            public string TxId { get; set; } = string.Empty;
            public string BlockerPubKey { get; set; } = string.Empty;
            public bool Selected { get; set; }
            public long IssuedAt { get; set; }
            public long ExpiresAt { get; set; }
            public string Nonce { get; set; } = string.Empty;
        }

        public class DecisionResponse
        {
            public DecisionPayload Decision { get; set; } = new DecisionPayload();
            public string Signature { get; set; } = string.Empty;
            public string CentralPublicKey { get; set; } = string.Empty;
            /// <summary>Central's signed statement of the API build that decided this tx; the Blocker stores it in the tx.</summary>
            public VersionAttestation? Version { get; set; }
        }

        /// <summary>Message format shared with the Blocker (Core/VersionAttestation.cs).</summary>
        public record VersionAttestation(string Service, string Version, string Commit, string PublicKey, string Signature)
        {
            public static string Message(string txId, string service, string version, string commit) =>
                $"DELTANIUM-TX-VERSION\n{txId}\n{service}\n{version}\n{commit}";
        }

        private readonly EconomicsService _econ;
        private readonly IWalletService _wallet;
        private readonly IConfiguration? _config;

        public BlockCreatorNodeController(KeyService keyService, IStorageService storageService,
            EconomicsService econ, IWalletService wallet, IConfiguration? config = null)
        {
            _keyService = keyService;
            _storageService = storageService;
            _econ = econ;
            _wallet = wallet;
            _config = config;
        }

        public class DeactivateRequest
        {
            public string PublicKey { get; set; } = "";
            public string? Reason { get; set; }
        }

        /// <summary>
        /// Admin: take a Block Creator out of selection and tx submission (key rotation, compromised key,
        /// retired node). The record is kept, marked inactive, so its old blocks still verify. Same admin
        /// auth as the wallet admin endpoints: X-SuperAdmin-Request-Signature + X-Timestamp, or X-Admin-Token.
        /// </summary>
        [HttpPost("admin/deactivate")]
        public async Task<IActionResult> AdminDeactivate()
        {
            Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(Request.Body, leaveOpen: true)) body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (AdminDenied(body) is { } denied) return denied;

            var req = System.Text.Json.JsonSerializer.Deserialize<DeactivateRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || string.IsNullOrWhiteSpace(req.PublicKey)) return BadRequest("publicKey required");

            var node = await _storageService.GetBlockCreatorNodeByPublicKeyAsync(req.PublicKey.Trim());
            if (node == null) return NotFound("Block Creator not found");
            if (!node.IsActive) return Ok(new { node.Id, node.PublicKey, node.Endpoint, isActive = false, note = "already inactive" });

            node.IsActive = false;
            await _storageService.UpdateBlockCreatorNodeAsync(node);
            Console.WriteLine($"[ADMIN] Block Creator {node.PublicKey} ({node.Endpoint}) deactivated: {req.Reason}");
            return Ok(new { node.Id, node.PublicKey, node.Endpoint, isActive = false });
        }

        /// <summary>Admin sets a Block Creator's HTTPS and/or DLTP endpoint.</summary>
        [HttpPost("admin/endpoints")]
        public async Task<IActionResult> AdminSetEndpoints()
        {
            Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(Request.Body, leaveOpen: true)) body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (AdminDenied(body) is { } denied) return denied;

            var req = System.Text.Json.JsonSerializer.Deserialize<StoreNodeController.NodeEndpointsRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || string.IsNullOrWhiteSpace(req.PublicKey)) return BadRequest("publicKey required");
            var node = await _storageService.GetBlockCreatorNodeByPublicKeyAsync(req.PublicKey.Trim());
            if (node == null) return NotFound("Block Creator not found");
            var r = StoreNodeController.ApplyEndpoints(node.Endpoint, node.TcpEndpoint, req);
            if (!r.Ok) return BadRequest(r.Error);
            node.Endpoint = r.Https;
            node.TcpEndpoint = r.Tcp;
            await _storageService.UpdateBlockCreatorNodeAsync(node);
            return Ok(new { node.Id, node.PublicKey, endpoint = r.Https, tcpEndpoint = r.Tcp });
        }

        /// <summary>The Block Creator sets or clears its own DLTP endpoint, signed with its node key (see StoreNodeController.SelfEndpoints).</summary>
        [HttpPost("self/endpoints")]
        public async Task<IActionResult> SelfEndpoints()
        {
            Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(Request.Body, leaveOpen: true)) body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (HttpContext.RequestServices.GetService(typeof(SignatureAuthHelper)) is not SignatureAuthHelper auth)
                return StatusCode(500, "auth unavailable");
            var (ok, signer, err) = await auth.ValidateAsync(Request, body, requireRegisteredUser: false);
            if (!ok) return Unauthorized(err);
            var node = await _storageService.GetBlockCreatorNodeByPublicKeyAsync(signer!.Trim());
            if (node == null || !node.IsActive) return NotFound("not an active registered Block Creator");
            var r = StoreNodeController.ApplySelfEndpoint(node.Endpoint, node.TcpEndpoint, body, HttpContext?.Connection.RemoteIpAddress);
            if (!r.Ok) return BadRequest(r.Error);
            if (r.Tcp != node.TcpEndpoint)
            {
                node.TcpEndpoint = r.Tcp;
                await _storageService.UpdateBlockCreatorNodeAsync(node);
            }
            return Ok(new { node.Id, node.PublicKey, endpoint = node.Endpoint, tcpEndpoint = r.Tcp });
        }

        private IActionResult? AdminDenied(string body)
        {
            if (Request.Headers.TryGetValue("X-SuperAdmin-Request-Signature", out var saSig) &&
                Request.Headers.TryGetValue("X-Timestamp", out var tsHeader) &&
                long.TryParse(tsHeader.FirstOrDefault(), out var ts))
            {
                if ((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts)).Duration() > SignatureAuthHelper.MaxClockSkew)
                    return Unauthorized("Admin timestamp expired");
                var message = SignatureAuthHelper.BuildSignedMessage(Request.Method, $"{Request.Path}{Request.QueryString}", tsHeader.First()!, body);
                var superPk = _keyService.GetSuperAdminPublicKey();
                if (!_keyService.VerifySignature(superPk, message, saSig.FirstOrDefault() ?? ""))
                    return Unauthorized("Invalid Super Admin request signature");
                if (!SignatureAuthHelper.TryConsumeSignature("superadmin:" + superPk, message))
                    return Unauthorized("Request already used (replayed signature)");
                return null;
            }

            var adminToken = _config?["InternalApi:AdminToken"] is { Length: > 0 } t ? t : Environment.GetEnvironmentVariable("ADMIN_API_TOKEN");
            if (!string.IsNullOrWhiteSpace(adminToken) && Request.Headers.TryGetValue("X-Admin-Token", out var gotAdmin) &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(gotAdmin.FirstOrDefault() ?? ""), System.Text.Encoding.UTF8.GetBytes(adminToken)))
                return null;

            return Unauthorized("Admin authorization required (X-SuperAdmin-Request-Signature or X-Admin-Token)");
        }

        /// <summary>
        /// Canonical string Central signs for a block slot. Anchor fields are appended only when present
        /// so blockers running the pre-anchor format keep verifying.
        /// </summary>
        public static string BlockSlotSignString(BlockSlotPayload p)
        {
            var s = $"BLOCK_SLOT|{p.BlockerPubKey}|{p.IssuedAt}|{p.ExpiresAt}|{p.Nonce}|{p.Granted}";
            if (!string.IsNullOrEmpty(p.LedgerRoot))
                s += $"|{p.LedgerAnchorSeq}|{p.LedgerRoot}|{p.BalancesRoot}";
            return s;
        }

        [HttpPost("register")]
        public async Task<ActionResult<NodeRegistrationResponse>> Register([FromBody] NodeRegistrationRequest request)
        {
            // A node is reachable over HTTPS, over DLTP (TCP), or both — at least one.
            var httpsEndpoint = (request.Endpoint ?? "").Trim().TrimEnd('/');
            // "tcps://auto:PORT": the public IP this registration comes from (a node on a home computer has no domain).
            var tcpEndpoint = string.IsNullOrWhiteSpace(request.TcpEndpoint) ? null
                : NodeAddress.ResolveAuto(request.TcpEndpoint.Trim().TrimEnd('/'), HttpContext?.Connection.RemoteIpAddress);
            if (httpsEndpoint.Length == 0 && tcpEndpoint == null)
                return BadRequest("Endpoint (https) or TcpEndpoint (tcps://host:port) is required");
            if (httpsEndpoint.Length > 0 && !NodeAddress.IsAcceptableHttps(httpsEndpoint))
                return BadRequest("Endpoint must be an https:// URL");
            if (tcpEndpoint != null && !NodeAddress.IsAcceptableTcp(tcpEndpoint))
                return BadRequest("TcpEndpoint must look like tcps://host:port or tcps://<public IP>:port");
            if (string.IsNullOrWhiteSpace(request.Voucher))
            {
                return BadRequest("Voucher is required");
            }
            if (string.IsNullOrWhiteSpace(request.PublicKey))
            {
                return BadRequest("PublicKey is required");
            }

            // Validate public key format
            if (!_keyService.ValidatePublicKey(request.PublicKey))
            {
                return BadRequest("Invalid public key format");
            }

            // Check if node already registered by public key
            var existingNodes = await _storageService.GetBlockCreatorNodesAsync();
            if (existingNodes.Any(n => n.PublicKey == request.PublicKey))
            {
                return Conflict("Block Creator node already registered");
            }

            if (!await _storageService.TryMarkVoucherUsedAsync(request.Voucher))
            {
                return BadRequest("Invalid or already used voucher");
            }

            var node = new BlockCreatorNode
            {
                PublicKey = request.PublicKey,
                Endpoint = httpsEndpoint,
                TcpEndpoint = tcpEndpoint
            };

            await _storageService.SaveBlockCreatorNodeAsync(node);

            return new NodeRegistrationResponse
            {
                Id = node.Id,
                PublicKey = node.PublicKey,
                Endpoint = node.Endpoint,
                TcpEndpoint = node.TcpEndpoint
            };
        }

        [HttpPost("decision")]
        public async Task<ActionResult<DecisionResponse>> GetDecision([FromBody] DecisionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TxId))
                return BadRequest("TxId is required");
            if (string.IsNullOrWhiteSpace(request.BlockerPubKey))
                return BadRequest("BlockerPubKey is required");

            var nodes = await _storageService.GetActiveBlockCreatorNodesAsync();
            if (nodes.Count == 0)
                return BadRequest("No active blockers");

            var selected = nodes[new Random().Next(nodes.Count)];
            var isSelected = string.Equals(selected.PublicKey, request.BlockerPubKey, StringComparison.OrdinalIgnoreCase);

            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expiresAt = issuedAt + 60;
            var payload = new DecisionPayload
            {
                TxId = request.TxId,
                BlockerPubKey = request.BlockerPubKey,
                Selected = isSelected,
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
                Nonce = Guid.NewGuid().ToString("N")
            };

            var toSign = $"DECISION|{payload.TxId}|{payload.BlockerPubKey}|{payload.Selected}|{payload.IssuedAt}|{payload.ExpiresAt}|{payload.Nonce}";
            var signature = _keyService.SignDecision(toSign);

            var centralPublicKey = _keyService.GetCentralPublicKey();
            return new DecisionResponse
            {
                Decision = payload,
                Signature = signature,
                CentralPublicKey = centralPublicKey,
                Version = new VersionAttestation("api", BuildInfo.Version, BuildInfo.Commit, centralPublicKey,
                    _keyService.SignDecision(VersionAttestation.Message(payload.TxId, "api", BuildInfo.Version, BuildInfo.Commit)))
            };
        }

        [HttpGet("decision-key")]
        public ActionResult GetDecisionKey()
        {
            return Ok(new { publicKey = _keyService.GetCentralPublicKey() });
        }

        public class BlockSlotRequest
        {
            public string BlockerPubKey { get; set; } = string.Empty;
        }

        public class BlockSlotPayload
        {
            public string BlockerPubKey { get; set; } = string.Empty;
            public long IssuedAt { get; set; }
            public long ExpiresAt { get; set; }
            public string Nonce { get; set; } = string.Empty;
            public bool Granted { get; set; }
            /// <summary>TOKENOMICS_V2 §11: wallet ledger anchor to embed in the block (granted slots only).</summary>
            public long LedgerAnchorSeq { get; set; }
            public string LedgerRoot { get; set; } = string.Empty;
            public string BalancesRoot { get; set; } = string.Empty;
        }

        public class BlockSlotResponse
        {
            public BlockSlotPayload Payload { get; set; } = new BlockSlotPayload();
            public string Signature { get; set; } = string.Empty;
            public string CentralPublicKey { get; set; } = string.Empty;
        }

        /// <summary>Request authorization to create the next block. Central signs the slot grant; Blocker must verify the signature with trusted Central pubkey.</summary>
        [HttpPost("block-slot")]
        public async Task<ActionResult<BlockSlotResponse>> RequestBlockSlot([FromBody] BlockSlotRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.BlockerPubKey))
                return BadRequest("BlockerPubKey is required");

            var nodes = await _storageService.GetActiveBlockCreatorNodesAsync();
            var granted = false;
            await _wallet.EnsureReadyAsync();
            // v2 §8: weighted by (bond + 1) × uptime; nodes under the minimum bond are skipped when bonds are enforced.
            var selected = await _econ.PickBlockerAsync(nodes);
            if (selected != null)
                granted = string.Equals(selected.PublicKey, request.BlockerPubKey, StringComparison.OrdinalIgnoreCase);

            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expiresAt = issuedAt + 120;
            var nonce = Guid.NewGuid().ToString("N");
            var payload = new BlockSlotPayload
            {
                BlockerPubKey = request.BlockerPubKey,
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
                Nonce = nonce,
                Granted = granted
            };
            if (granted)
            {
                // v2 §11 phase A: anchor the wallet ledger into this block; §5: pay pending tx fees to this blocker.
                var anchor = await _econ.CreateAnchorAsync(request.BlockerPubKey);
                payload.LedgerAnchorSeq = anchor.Seq;
                payload.LedgerRoot = anchor.LedgerRoot;
                payload.BalancesRoot = anchor.BalancesRoot;
                await _econ.PayoutBlockerPoolAsync(selected!.PublicKey);
            }
            var toSign = BlockSlotSignString(payload);
            var signature = _keyService.SignDecision(toSign);

            return new BlockSlotResponse
            {
                Payload = payload,
                Signature = signature,
                CentralPublicKey = _keyService.GetCentralPublicKey()
            };
        }

        [HttpGet("list")]
        public async Task<ActionResult<List<BlockCreatorNode>>> ListNodes()
        {
            return await _storageService.GetActiveBlockCreatorNodesAsync();
        }
    }
}












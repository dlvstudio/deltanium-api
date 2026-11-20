using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using DeltaniumApi.Services.Dltp;
using Microsoft.AspNetCore.Mvc;
using System.Linq;

namespace DeltaniumApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StoreNodeController : ControllerBase
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
            /// <summary>Unix seconds; with Signature, proof that the registrant holds the node key.</summary>
            public long Timestamp { get; set; }
            /// <summary>Node key signature (base64 DER) over NodeRegistrationService.RegistrationMessage.</summary>
            public string? Signature { get; set; }
        }

        public class NodeRegistrationResponse
        {
            public string Id { get; set; }
            public string PublicKey { get; set; }
            public string Endpoint { get; set; }
            public string? TcpEndpoint { get; set; }
        }

        private readonly EconomicsService _econ;
        private readonly IWalletService _wallet;
        private readonly SignatureAuthHelper? _auth;

        public class NodeRekeyRequest
        {
            public string NodeId { get; set; } = "";
            public string PublicKey { get; set; } = "";
        }

        public StoreNodeController(KeyService keyService, IStorageService storageService,
            EconomicsService econ, IWalletService wallet, SignatureAuthHelper? auth = null)
        {
            _auth = auth;
            _econ = econ;
            _wallet = wallet;
            _keyService = keyService;
            _storageService = storageService;
        }

        /// <summary>
        /// Ask to join (see NodeRegistrationService): signed with the node key, checked voucher, then 202 with the
        /// registration fee to pay. The node becomes active after the Super Admin approves the fee and it answers.
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] NodeRegistrationRequest request)
        {
            if (HttpContext?.RequestServices.GetService(typeof(NodeRegistrationService)) is not NodeRegistrationService regs)
                return StatusCode(500, "registration unavailable");
            var r = await regs.SubmitAsync("store", request.Endpoint, request.TcpEndpoint, request.Voucher, request.PublicKey,
                request.Timestamp, request.Signature, HttpContext.Connection.RemoteIpAddress);
            if (r.Registration == null) return StatusCode(r.Status, r.Error);
            var config = (Microsoft.Extensions.Configuration.IConfiguration)HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration))!;
            return StatusCode(202, NodeRegistrationController.PublicView(r.Registration, config));
        }

        /// <summary>
        /// Super Admin replaces a registered Store's node key (lost or wrong key on the server). Signed on the
        /// Super Admin's machine (X-Timestamp + X-SuperAdmin-Request-Signature). The Store must then run with the
        /// matching NODE_PRIVATE_KEY, or Central refuses its signed calls.
        /// </summary>
        public class NodeEndpointsRequest
        {
            public string PublicKey { get; set; } = "";
            /// <summary>New HTTPS endpoint; "" removes it; omitted keeps it.</summary>
            public string? Endpoint { get; set; }
            /// <summary>New DLTP endpoint (tcps://host:port); "" removes it; omitted keeps it.</summary>
            public string? TcpEndpoint { get; set; }
        }

        /// <summary>Validate and apply an endpoints change; the node must keep at least one way in.</summary>
        public static (bool Ok, string Error, string Https, string? Tcp) ApplyEndpoints(string currentHttps, string? currentTcp, NodeEndpointsRequest req)
        {
            var https = req.Endpoint == null ? (currentHttps ?? "") : req.Endpoint.Trim().TrimEnd('/');
            var tcp = req.TcpEndpoint == null ? currentTcp : (req.TcpEndpoint.Trim().Length == 0 ? null : req.TcpEndpoint.Trim().TrimEnd('/'));
            if (https.Length == 0 && tcp == null) return (false, "A node needs an https Endpoint or a TcpEndpoint", "", null);
            if (https.Length > 0 && !NodeAddress.IsAcceptableHttps(https)) return (false, "Endpoint must be an https:// URL", "", null);
            if (tcp != null && !NodeAddress.IsAcceptableTcp(tcp)) return (false, "TcpEndpoint must look like tcps://host:port or tcps://<public IP>:port", "", null);
            return (true, "", https, tcp);
        }

        /// <summary>Super Admin sets a registered Store's HTTPS and/or DLTP endpoint (e.g. adding tcps://host:7333).</summary>
        [HttpPost("admin/endpoints")]
        public async Task<IActionResult> SetEndpoints()
        {
            Request.EnableBuffering();
            string raw;
            using (var reader = new System.IO.StreamReader(Request.Body, leaveOpen: true)) raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (_auth == null) return StatusCode(500, "auth unavailable");
            var (ok, err) = _auth.ValidateSuperAdminRequest(Request, raw);
            if (!ok) return Unauthorized(err);

            NodeEndpointsRequest? req;
            try { req = System.Text.Json.JsonSerializer.Deserialize<NodeEndpointsRequest>(raw, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch { return BadRequest("invalid JSON"); }
            if (req == null || string.IsNullOrWhiteSpace(req.PublicKey)) return BadRequest("publicKey required");
            var node = (await _storageService.GetNodesAsync()).FirstOrDefault(n => n.PublicKey == req.PublicKey.Trim());
            if (node == null) return NotFound("node not found");
            var r = ApplyEndpoints(node.Endpoint, node.TcpEndpoint, req);
            if (!r.Ok) return BadRequest(r.Error);
            await _storageService.UpdateNodeEndpointsAsync(node.Id, r.Https, r.Tcp);
            return Ok(new { node.Id, node.PublicKey, endpoint = r.Https, tcpEndpoint = r.Tcp });
        }

        /// <summary>
        /// The node itself sets or clears its DLTP endpoint, signed with its node key (X-User-PubKey / X-Timestamp /
        /// X-Signature). "tcps://auto:PORT" means "the public IP you see me calling from", for nodes on a home or
        /// personal computer whose IP changes. The HTTPS endpoint is not changed here.
        /// </summary>
        [HttpPost("self/endpoints")]
        public async Task<IActionResult> SelfEndpoints()
        {
            Request.EnableBuffering();
            string raw;
            using (var reader = new System.IO.StreamReader(Request.Body, leaveOpen: true)) raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (_auth == null) return StatusCode(500, "auth unavailable");
            var (ok, signer, err) = await _auth.ValidateAsync(Request, raw, requireRegisteredUser: false);
            if (!ok) return Unauthorized(err);
            var node = (await _storageService.GetNodesAsync()).FirstOrDefault(n => n.IsActive &&
                string.Equals(SignatureAuthHelper.NormalizePubKey(n.PublicKey.Trim()), SignatureAuthHelper.NormalizePubKey(signer!), System.StringComparison.OrdinalIgnoreCase));
            if (node == null) return NotFound("not an active registered Store");
            var r = ApplySelfEndpoint(node.Endpoint, node.TcpEndpoint, raw, HttpContext?.Connection.RemoteIpAddress);
            if (!r.Ok) return BadRequest(r.Error);
            if (r.Tcp != node.TcpEndpoint) await _storageService.UpdateNodeEndpointsAsync(node.Id, r.Https, r.Tcp);
            return Ok(new { node.Id, node.PublicKey, endpoint = r.Https, tcpEndpoint = r.Tcp });
        }

        /// <summary>Parse {"tcpEndpoint": "..."} from a node, fill in "auto" from the caller's IP, and validate.</summary>
        public static (bool Ok, string Error, string Https, string? Tcp) ApplySelfEndpoint(string currentHttps, string? currentTcp, string rawJson, System.Net.IPAddress? remote)
        {
            string? requested;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
                requested = doc.RootElement.TryGetProperty("tcpEndpoint", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
            }
            catch (System.Text.Json.JsonException) { return (false, "invalid JSON", "", null); }
            if (requested == null) return (false, "tcpEndpoint required (\"\" removes it)", "", null);
            var tcp = requested.Trim().Length == 0 ? "" : NodeAddress.ResolveAuto(requested, remote);
            if (tcp == null) return (false, "could not tell your public IP", "", null);
            return ApplyEndpoints(currentHttps, currentTcp, new NodeEndpointsRequest { TcpEndpoint = tcp });
        }

        /// <summary>Super Admin turns a Store node off (e.g. a test node, or one that left the network).</summary>
        [HttpPost("admin/deactivate")]
        public async Task<IActionResult> Deactivate()
        {
            Request.EnableBuffering();
            string raw;
            using (var reader = new System.IO.StreamReader(Request.Body, leaveOpen: true)) raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (_auth == null) return StatusCode(500, "auth unavailable");
            var (ok, err) = _auth.ValidateSuperAdminRequest(Request, raw);
            if (!ok) return Unauthorized(err);

            string? publicKey, reason;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                publicKey = doc.RootElement.TryGetProperty("publicKey", out var p) ? p.GetString() : null;
                reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            }
            catch (System.Text.Json.JsonException) { return BadRequest("invalid JSON"); }
            if (string.IsNullOrWhiteSpace(publicKey)) return BadRequest("publicKey required");
            var node = (await _storageService.GetNodesAsync()).FirstOrDefault(n => n.PublicKey == publicKey.Trim());
            if (node == null) return NotFound("node not found");
            if (!node.IsActive) return Ok(new { node.Id, node.PublicKey, isActive = false, note = "already inactive" });
            if (!await _storageService.SetNodeActiveAsync(node.Id, false)) return StatusCode(500, "could not update node");
            System.Console.WriteLine($"[ADMIN] Store {node.PublicKey} ({node.Endpoint} {node.TcpEndpoint}) deactivated: {reason}");
            return Ok(new { node.Id, node.PublicKey, isActive = false });
        }

        /// <summary>
        /// Super Admin issues one-shot node vouchers (Store or Block Creator registration). Body {"codes":["…"]};
        /// a code must be 8–64 characters of A–Z, 0–9 and '-'. Returns which codes were added.
        /// </summary>
        [HttpPost("admin/vouchers")]
        public async Task<IActionResult> AddVouchers()
        {
            Request.EnableBuffering();
            string raw;
            using (var reader = new System.IO.StreamReader(Request.Body, leaveOpen: true)) raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (_auth == null) return StatusCode(500, "auth unavailable");
            var (ok, err) = _auth.ValidateSuperAdminRequest(Request, raw);
            if (!ok) return Unauthorized(err);

            var codes = new List<string>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("codes", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var c in arr.EnumerateArray()) if (c.GetString() is { } v) codes.Add(v.Trim());
            }
            catch (System.Text.Json.JsonException) { return BadRequest("invalid JSON"); }
            if (codes.Count is 0 or > 100) return BadRequest("codes: 1 to 100 vouchers");
            var bad = codes.Where(c => !System.Text.RegularExpressions.Regex.IsMatch(c, "^[A-Z0-9-]{8,64}$")).ToList();
            if (bad.Count > 0) return BadRequest("invalid code format (A-Z, 0-9, '-', 8-64 chars)");
            var added = new List<string>();
            foreach (var c in codes.Distinct())
                if (await _storageService.AddVoucherAsync(c)) added.Add(c);
            return Ok(new { added = added.Count, skipped = codes.Distinct().Count() - added.Count });
        }

        [HttpPost("admin/rekey")]
        public async Task<IActionResult> Rekey()
        {
            Request.EnableBuffering();
            string raw;
            using (var reader = new System.IO.StreamReader(Request.Body, leaveOpen: true)) raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (_auth == null) return StatusCode(500, "auth unavailable");
            var (ok, err) = _auth.ValidateSuperAdminRequest(Request, raw);
            if (!ok) return Unauthorized(err);

            NodeRekeyRequest? req;
            try { req = System.Text.Json.JsonSerializer.Deserialize<NodeRekeyRequest>(raw, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch { return BadRequest("invalid JSON"); }
            if (req == null || string.IsNullOrWhiteSpace(req.NodeId) || string.IsNullOrWhiteSpace(req.PublicKey))
                return BadRequest("nodeId and publicKey required");
            var pk = req.PublicKey.Trim();
            if (!_keyService.ValidatePublicKey(pk)) return BadRequest("invalid publicKey");

            var nodes = await _storageService.GetNodesAsync();
            var norm = SignatureAuthHelper.NormalizePubKey(pk);
            if (nodes.Any(n => n.Id != req.NodeId && !string.IsNullOrWhiteSpace(n.PublicKey) &&
                               string.Equals(SignatureAuthHelper.NormalizePubKey(n.PublicKey.Trim()), norm, System.StringComparison.OrdinalIgnoreCase)))
                return Conflict("publicKey already belongs to another Store");
            var old = nodes.FirstOrDefault(n => n.Id == req.NodeId);
            if (old == null || !await _storageService.UpdateNodePublicKeyAsync(req.NodeId, pk)) return NotFound("node not found");
            return Ok(new { id = req.NodeId, endpoint = old.Endpoint, publicKey = pk, previousPublicKey = old.PublicKey });
        }

        [HttpGet("list")]
        public async Task<ActionResult<List<StoreNode>>> ListNodes()
        {
            var nodes = NodeOnline.Filter(await _storageService.GetActiveNodesAsync(), n => n.LastSeen,
                HttpContext?.RequestServices.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration)) as Microsoft.Extensions.Configuration.IConfiguration);
            // v2 §8: when bonds are enforced, only nodes with the minimum bond are offered to clients.
            if (!_econ.EnforceBonds) return nodes;
            await _wallet.EnsureReadyAsync();
            return await _econ.FilterStoreNodesByBondAsync(nodes);
        }
    }
} 
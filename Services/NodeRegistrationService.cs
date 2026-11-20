using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services.Dltp;
using Microsoft.Extensions.Configuration;

namespace DeltaniumApi.Services
{
    /// <summary>A node asking to join: kept until the Super Admin approves its registration fee and it answers.</summary>
    public class NodeRegistration
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Role { get; set; } = "store";          // store | blocker
        public string PublicKey { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string? TcpEndpoint { get; set; }
        public string Voucher { get; set; } = "";
        public decimal FeeVnd { get; set; }
        /// <summary>awaiting_payment, approved (fee confirmed, waiting for the node to answer), active, rejected.</summary>
        public string Status { get; set; } = "awaiting_payment";
        public string? Reason { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ApprovedAt { get; set; }
        public string? ApprovalSignature { get; set; }
        public DateTime? ActivatedAt { get; set; }
        public DateTime? LastProbeAt { get; set; }
        public string? LastProbeError { get; set; }
    }

    /// <summary>
    /// Joining the network, for Store nodes and Block Creators:
    /// 1. The node registers with a one-shot voucher and signs the request with its own key (proof it holds the key
    ///    it registers). The voucher is checked but not spent yet.
    /// 2. The operator pays the registration fee (Nodes:Registration:FeeVnd, 100,000 VND) by bank transfer or PayPal,
    ///    with the transfer content shown.
    /// 3. The Super Admin approves: signs the registration's SignPayload on their own machine (admin page). Only then
    ///    is the voucher spent, so fake registrations cannot burn vouchers.
    /// 4. Central calls the node (DLTP with its key, or HTTPS) and activates it once it answers; it keeps trying.
    /// </summary>
    public class NodeRegistrationService
    {
        public const string Collection = "nodereg";
        private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);

        private readonly EconDocStore _docs;
        private readonly IStorageService _storage;
        private readonly KeyService _keys;
        private readonly IConfiguration _config;

        public NodeRegistrationService(EconDocStore docs, IStorageService storage, KeyService keys, IConfiguration config)
        {
            _docs = docs;
            _storage = storage;
            _keys = keys;
            _config = config;
        }

        public decimal FeeVnd => decimal.TryParse(_config["Nodes:Registration:FeeVnd"], out var f) && f >= 0 ? f : 100_000m;

        /// <summary>What the node signs (network-scoped by KeyService): role, key, both endpoints as sent, voucher, time.</summary>
        public static string RegistrationMessage(string role, string publicKey, string? endpoint, string? tcpEndpoint, string voucher, long timestamp) =>
            $"NODE-REGISTER|{role}|{publicKey.Trim()}|{(endpoint ?? "").Trim().TrimEnd('/')}|{(tcpEndpoint ?? "").Trim().TrimEnd('/')}|{voucher.Trim()}|{timestamp}";

        /// <summary>What the Super Admin signs to approve: the registration, its key and the fee received.</summary>
        public static string ApprovalPayload(NodeRegistration r) =>
            $"NODE-REGISTRATION-FEE|{r.Id}|{r.Role}|{r.PublicKey}|{r.FeeVnd:0}VND";

        public static string TransferContent(NodeRegistration r) => "NODE" + (r.Id.Length >= 10 ? r.Id[..10] : r.Id).ToUpperInvariant();

        public record SubmitResult(int Status, string? Error, NodeRegistration? Registration);

        public async Task<SubmitResult> SubmitAsync(string role, string? endpoint, string? tcpEndpoint, string? voucher,
            string? publicKey, long timestamp, string? signature, IPAddress? remote)
        {
            var https = (endpoint ?? "").Trim().TrimEnd('/');
            var tcp = string.IsNullOrWhiteSpace(tcpEndpoint) ? null : NodeAddress.ResolveAuto(tcpEndpoint.Trim().TrimEnd('/'), remote);
            if (https.Length == 0 && tcp == null) return new(400, "Endpoint (https) or TcpEndpoint (tcps://host:port) is required", null);
            if (https.Length > 0 && !NodeAddress.IsAcceptableHttps(https)) return new(400, "Endpoint must be an https:// URL", null);
            if (tcp != null && !NodeAddress.IsAcceptableTcp(tcp)) return new(400, "TcpEndpoint must look like tcps://host:port or tcps://<public IP>:port", null);
            if (string.IsNullOrWhiteSpace(voucher)) return new(400, "Voucher is required", null);
            if (string.IsNullOrWhiteSpace(publicKey) || !_keys.ValidatePublicKey(publicKey.Trim())) return new(400, "Invalid public key", null);
            var pk = publicKey.Trim();

            // A. Proof the registrant holds the key it registers.
            if (string.IsNullOrWhiteSpace(signature) || timestamp <= 0)
                return new(400, "Registration must be signed with the node key (update the node software)", null);
            if ((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp)).Duration() > MaxClockSkew)
                return new(400, "Registration timestamp too old or in the future", null);
            if (!_keys.VerifySignature(pk, RegistrationMessage(role, pk, endpoint, tcpEndpoint, voucher, timestamp), signature))
                return new(401, "Invalid registration signature", null);

            if (await IsRegisteredNodeAsync(role, pk)) return new(409, "Node already registered", null);
            if (!await _storage.IsVoucherValidAsync(voucher.Trim())) return new(400, "Invalid or already used voucher", null);

            await _docs.EnsureReadyAsync();
            var existing = (await _docs.ListAsync<NodeRegistration>(Collection, r =>
                r.Role == role && SameKey(r.PublicKey, pk) && (r.Status == "awaiting_payment" || r.Status == "approved"))).FirstOrDefault();
            var reg = existing ?? new NodeRegistration { Role = role, PublicKey = pk, FeeVnd = FeeVnd };
            reg.Endpoint = https;
            reg.TcpEndpoint = tcp;
            if (reg.Status == "awaiting_payment") reg.Voucher = voucher.Trim(); // spent only on approval
            reg.UpdatedAt = DateTime.UtcNow;
            await _docs.PutAsync(Collection, reg.Id, reg);
            return new(202, null, reg);
        }

        public async Task<NodeRegistration?> GetAsync(string id)
        {
            await _docs.EnsureReadyAsync();
            return await _docs.GetAsync<NodeRegistration>(Collection, id);
        }

        /// <summary>The newest registration of a node (any role).</summary>
        public async Task<NodeRegistration?> FindByKeyAsync(string publicKey)
        {
            await _docs.EnsureReadyAsync();
            return (await _docs.ListAsync<NodeRegistration>(Collection, r => SameKey(r.PublicKey, publicKey)))
                .OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        }

        public async Task<List<NodeRegistration>> ListAsync(string? status)
        {
            await _docs.EnsureReadyAsync();
            return (await _docs.ListAsync<NodeRegistration>(Collection, r => string.IsNullOrEmpty(status) || status == "all" || r.Status == status))
                .OrderByDescending(r => r.CreatedAt).ToList();
        }

        /// <summary>Super Admin approval: signature over <see cref="ApprovalPayload"/>. Spends the voucher, then tries to activate.</summary>
        public async Task<SubmitResult> ApproveAsync(string id, string superAdminSignature)
        {
            var reg = await GetAsync(id);
            if (reg == null) return new(404, "Registration not found", null);
            if (reg.Status is "approved" or "active") return new(200, null, reg);
            if (reg.Status != "awaiting_payment") return new(400, $"Registration is {reg.Status}", reg);
            var sa = _keys.GetSuperAdminPublicKey();
            if (string.IsNullOrWhiteSpace(sa) || !_keys.VerifySignature(sa, ApprovalPayload(reg), superAdminSignature))
                return new(401, "Invalid Super Admin signature on the approval payload", reg);
            if (await IsRegisteredNodeAsync(reg.Role, reg.PublicKey))
            {
                reg.Status = "rejected"; reg.Reason = "node already registered";
                await SaveAsync(reg);
                return new(409, reg.Reason, reg);
            }
            if (!await _storage.TryMarkVoucherUsedAsync(reg.Voucher))
            {
                reg.Status = "rejected"; reg.Reason = "voucher already used";
                await SaveAsync(reg);
                return new(409, "Voucher already used: ask the node for a new voucher", reg);
            }
            reg.Status = "approved";
            reg.ApprovedAt = DateTime.UtcNow;
            reg.ApprovalSignature = superAdminSignature;
            await SaveAsync(reg);
            await TryActivateAsync(reg);
            return new(200, null, reg);
        }

        public async Task<SubmitResult> RejectAsync(string id, string? reason)
        {
            var reg = await GetAsync(id);
            if (reg == null) return new(404, "Registration not found", null);
            if (reg.Status == "active") return new(400, "Already active: deactivate the node instead", reg);
            reg.Status = "rejected";
            reg.Reason = string.IsNullOrWhiteSpace(reason) ? "rejected by Super Admin" : reason.Trim();
            await SaveAsync(reg);
            return new(200, null, reg);
        }

        /// <summary>Approved registrations: activate the ones whose node now answers (run by NodeLivenessWorker).</summary>
        public async Task<int> ActivateApprovedAsync()
        {
            var activated = 0;
            foreach (var reg in await ListAsync("approved"))
                if (await TryActivateAsync(reg)) activated++;
            return activated;
        }

        /// <summary>B. Call the node where it says it is; create it as an active node only if it answers as its role.</summary>
        public async Task<bool> TryActivateAsync(NodeRegistration reg)
        {
            reg.LastProbeAt = DateTime.UtcNow;
            var (ok, error) = await ProbeAsync(reg);
            reg.LastProbeError = ok ? null : error;
            if (!ok)
            {
                await SaveAsync(reg);
                return false;
            }
            if (!await IsRegisteredNodeAsync(reg.Role, reg.PublicKey))
            {
                if (reg.Role == "blocker")
                    await _storage.SaveBlockCreatorNodeAsync(new BlockCreatorNode
                    { PublicKey = reg.PublicKey, Endpoint = reg.Endpoint, TcpEndpoint = reg.TcpEndpoint, LastSeen = DateTime.UtcNow });
                else
                    await _storage.SaveNodeAsync(new StoreNode
                    { PublicKey = reg.PublicKey, Endpoint = reg.Endpoint, TcpEndpoint = reg.TcpEndpoint, LastSeen = DateTime.UtcNow });
            }
            reg.Status = "active";
            reg.ActivatedAt = DateTime.UtcNow;
            await SaveAsync(reg);
            return true;
        }

        /// <summary>
        /// GET /api/version at the node: over DLTP when it has a TCP endpoint (an IP endpoint also proves the node key in the
        /// HELLO), otherwise over HTTPS. The answer must name the expected service.
        /// </summary>
        public virtual async Task<(bool Ok, string Error)> ProbeAsync(NodeRegistration reg)
        {
            try
            {
                var tcpFirst = !string.IsNullOrEmpty(reg.TcpEndpoint) ? NodeAddress.Of("", reg.TcpEndpoint, reg.PublicKey) : "";
                var baseUrl = tcpFirst.Length > 0 ? tcpFirst : NodeAddress.Of(reg.Endpoint, null, reg.PublicKey);
                if (baseUrl.Length == 0) return (false, "no endpoint");
                using var http = new HttpClient(new DltpRoutingHandler(new HttpClientHandler())) { Timeout = TimeSpan.FromSeconds(20) };
                using var res = await http.GetAsync(baseUrl + "/api/version");
                if (!res.IsSuccessStatusCode) return (false, $"HTTP {(int)res.StatusCode}");
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var service = doc.RootElement.TryGetProperty("service", out var s) ? s.GetString() : null;
                return service == reg.Role ? (true, "") : (false, $"answered as '{service}'");
            }
            catch (Exception ex)
            {
                return (false, ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
            }
        }

        private async Task SaveAsync(NodeRegistration reg)
        {
            reg.UpdatedAt = DateTime.UtcNow;
            await _docs.PutAsync(Collection, reg.Id, reg);
        }

        private async Task<bool> IsRegisteredNodeAsync(string role, string pk) => role == "blocker"
            ? (await _storage.GetBlockCreatorNodesAsync()).Any(n => SameKey(n.PublicKey, pk))
            : (await _storage.GetNodesAsync()).Any(n => SameKey(n.PublicKey, pk));

        private static bool SameKey(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
            string.Equals(SignatureAuthHelper.NormalizePubKey(a.Trim()), SignatureAuthHelper.NormalizePubKey(b.Trim()), StringComparison.OrdinalIgnoreCase);
    }
}

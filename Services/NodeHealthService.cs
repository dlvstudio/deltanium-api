using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Public node status for the explorer: probes every active Blocker and Store node's /api/version
    /// (Blockers also /api/explorer/status) from the server side, so browsers need no CORS on the nodes.
    /// Results are cached briefly so the public endpoint cannot be used to hammer the nodes.
    /// </summary>
    public interface INodeHealthService
    {
        Task<NodeHealthReport> GetReportAsync(CancellationToken ct = default);
    }

    public class NodeHealthService : INodeHealthService
    {
        public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

        private readonly IStorageService _storage;
        private readonly IHttpClientFactory _http;

        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static NodeHealthReport? _cached;

        public NodeHealthService(IStorageService storage, IHttpClientFactory http)
        {
            _storage = storage;
            _http = http;
        }

        public async Task<NodeHealthReport> GetReportAsync(CancellationToken ct = default)
        {
            var hit = _cached;
            if (hit != null && DateTime.UtcNow - hit.CheckedAt < CacheFor) return hit;

            await Gate.WaitAsync(ct);
            try
            {
                hit = _cached;
                if (hit != null && DateTime.UtcNow - hit.CheckedAt < CacheFor) return hit;

                var blockers = await _storage.GetActiveBlockCreatorNodesAsync();
                var stores = await _storage.GetActiveNodesAsync();

                var blockerTasks = blockers.Select(b => ProbeAsync("blocker", b.PublicKey, b.Endpoint, b.LastSeen, true));
                var storeTasks = stores.Select(s => ProbeAsync("store", s.PublicKey, s.Endpoint, s.LastSeen, false));

                var report = new NodeHealthReport
                {
                    Network = NetworkScope.Name,
                    CheckedAt = DateTime.UtcNow,
                    Blockers = (await Task.WhenAll(blockerTasks)).OrderBy(n => n.Endpoint).ToList(),
                    Stores = (await Task.WhenAll(storeTasks)).OrderBy(n => n.Endpoint).ToList(),
                };
                _cached = report;
                return report;
            }
            finally
            {
                Gate.Release();
            }
        }

        private async Task<NodeHealth> ProbeAsync(string role, string? publicKey, string? endpoint, DateTime lastSeen, bool chain)
        {
            var baseUrl = (endpoint ?? "").Trim().TrimEnd('/');
            var n = new NodeHealth
            {
                Role = role,
                PublicKey = publicKey ?? "",
                Endpoint = baseUrl,
                LastSeen = DateTime.SpecifyKind(lastSeen, DateTimeKind.Utc),
            };
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                n.Error = "invalid endpoint";
                return n;
            }

            var client = _http.CreateClient("explorer");
            using var cts = new CancellationTokenSource(ProbeTimeout);
            var sw = Stopwatch.StartNew();
            try
            {
                using var doc = JsonDocument.Parse(await client.GetStringAsync($"{baseUrl}/api/version", cts.Token));
                n.LatencyMs = (int)sw.ElapsedMilliseconds;
                n.Online = true;
                var r = doc.RootElement;
                n.Version = Str(r, "version");
                n.Commit = Str(r, "commitShort");
                n.Network = Str(r, "network") ?? "mainnet";
            }
            catch (Exception ex)
            {
                n.Error = ex is OperationCanceledException ? "timeout" : "unreachable";
                return n;
            }

            if (chain)
            {
                try
                {
                    using var doc = JsonDocument.Parse(await client.GetStringAsync($"{baseUrl}/api/explorer/status", cts.Token));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("height", out var h) && h.TryGetInt64(out var height)) n.Height = height;
                    if (r.TryGetProperty("mempoolCount", out var m) && m.TryGetInt32(out var mempool)) n.Mempool = mempool;
                }
                catch { /* version answered; chain status is optional */ }
            }
            return n;
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }

    public class NodeHealthReport
    {
        public string Network { get; set; } = "mainnet";
        public DateTime CheckedAt { get; set; }
        public List<NodeHealth> Blockers { get; set; } = new();
        public List<NodeHealth> Stores { get; set; } = new();
    }

    public class NodeHealth
    {
        public string Role { get; set; } = "";
        public string PublicKey { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public DateTime LastSeen { get; set; }
        public bool Online { get; set; }
        public int? LatencyMs { get; set; }
        public string? Version { get; set; }
        public string? Commit { get; set; }
        public string? Network { get; set; }
        public long? Height { get; set; }
        public int? Mempool { get; set; }
        public string? Error { get; set; }
    }
}

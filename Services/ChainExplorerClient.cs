using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DeltaniumApi.Services
{
    public interface IChainExplorerClient
    {
        Task<ExplorerStatusDto> GetStatusAsync();
        Task<string> SearchTxsJsonAsync(
            string? q, string? type, int limit, int offset, bool includePending);
        Task<JsonElement?> GetTxAsync(string txId);
        Task<string?> ListBlocksJsonAsync(int limit, int offset);
    }

    public class ChainExplorerClient : IChainExplorerClient
    {
        private readonly IStorageService _storage;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ChainExplorerClient> _logger;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public ChainExplorerClient(
            IStorageService storage,
            IHttpClientFactory httpClientFactory,
            ILogger<ChainExplorerClient> logger)
        {
            _storage = storage;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<ExplorerStatusDto> GetStatusAsync()
        {
            long tip = -1;
            var reachable = 0;
            var endpoints = await GetBlockerEndpointsAsync();
            foreach (var endpoint in endpoints)
            {
                try
                {
                    var client = _httpClientFactory.CreateClient("explorer");
                    var json = await client.GetStringAsync($"{endpoint}/api/explorer/status");
                    using var doc = JsonDocument.Parse(json);
                    reachable++;
                    if (doc.RootElement.TryGetProperty("height", out var h) && h.TryGetInt64(out var height))
                        tip = Math.Max(tip, height);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Explorer status failed for {Endpoint}", endpoint);
                }
            }

            return new ExplorerStatusDto
            {
                TipHeight = tip,
                ActiveBlockers = endpoints.Count,
                ReachableBlockers = reachable
            };
        }

        public async Task<string> SearchTxsJsonAsync(
            string? q, string? type, int limit, int offset, bool includePending)
        {
            if (limit <= 0) limit = 50;
            if (limit > 200) limit = 200;
            if (offset < 0) offset = 0;

            var byId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in await GetBlockerEndpointsAsync())
            {
                try
                {
                    var client = _httpClientFactory.CreateClient("explorer");
                    var fetchLimit = Math.Min(200, Math.Max(limit + offset, 50));
                    var url =
                        $"{endpoint}/api/explorer/txs?limit={fetchLimit}&offset=0" +
                        $"&includePending={(includePending ? "true" : "false")}" +
                        (string.IsNullOrWhiteSpace(q) ? "" : $"&q={Uri.EscapeDataString(q)}") +
                        (string.IsNullOrWhiteSpace(type) ? "" : $"&type={Uri.EscapeDataString(type)}");
                    var json = await client.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("items", out var items) ||
                        items.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out var idEl)) continue;
                        var id = idEl.GetString();
                        if (string.IsNullOrWhiteSpace(id)) continue;

                        if (!byId.TryGetValue(id, out var existing))
                        {
                            byId[id] = item.Clone();
                            continue;
                        }

                        if (GetHeight(item) > GetHeight(existing))
                            byId[id] = item.Clone();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Explorer search failed for {Endpoint}", endpoint);
                }
            }

            var ordered = byId.Values
                .OrderByDescending(GetHeight)
                .ThenByDescending(GetTimestamp)
                .ToList();

            var page = ordered.Skip(offset).Take(limit)
                .Select(e => JsonSerializer.Deserialize<JsonElement>(e.GetRawText()))
                .ToList();

            return JsonSerializer.Serialize(new
            {
                total = ordered.Count,
                offset,
                limit,
                items = page
            });
        }

        public async Task<JsonElement?> GetTxAsync(string txId)
        {
            foreach (var endpoint in await GetBlockerEndpointsAsync())
            {
                try
                {
                    var client = _httpClientFactory.CreateClient("explorer");
                    var resp = await client.GetAsync($"{endpoint}/api/explorer/txs/{Uri.EscapeDataString(txId)}");
                    if (!resp.IsSuccessStatusCode) continue;
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.Clone();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Explorer get tx failed for {Endpoint}", endpoint);
                }
            }
            return null;
        }

        public async Task<string?> ListBlocksJsonAsync(int limit, int offset)
        {
            if (limit <= 0) limit = 20;
            if (limit > 100) limit = 100;

            string? bestEndpoint = null;
            long bestHeight = -1;
            foreach (var endpoint in await GetBlockerEndpointsAsync())
            {
                try
                {
                    var client = _httpClientFactory.CreateClient("explorer");
                    var json = await client.GetStringAsync($"{endpoint}/api/explorer/status");
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("height", out var h) && h.TryGetInt64(out var height) &&
                        height > bestHeight)
                    {
                        bestHeight = height;
                        bestEndpoint = endpoint;
                    }
                }
                catch { /* try next */ }
            }

            if (bestEndpoint == null) return null;

            var client2 = _httpClientFactory.CreateClient("explorer");
            return await client2.GetStringAsync(
                $"{bestEndpoint}/api/explorer/blocks?limit={limit}&offset={offset}");
        }

        private async Task<List<string>> GetBlockerEndpointsAsync()
        {
            var nodes = await _storage.GetActiveBlockCreatorNodesAsync();
            return nodes
                .Select(n => n.Endpoint?.Trim().TrimEnd('/'))
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();
        }

        private static long GetHeight(JsonElement e)
        {
            if (e.TryGetProperty("blockHeight", out var h) && h.ValueKind == JsonValueKind.Number &&
                h.TryGetInt64(out var v))
                return v;
            return -1;
        }

        private static long GetTimestamp(JsonElement e)
        {
            if (e.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number &&
                t.TryGetInt64(out var v))
                return v;
            return 0;
        }
    }

    public class ExplorerStatusDto
    {
        public long TipHeight { get; set; }
        public int ActiveBlockers { get; set; }
        public int ReachableBlockers { get; set; }
    }

}

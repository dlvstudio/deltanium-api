using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DeltaniumApi.Services.Dltp;

namespace DeltaniumApi.Services
{
    public interface IBlockerTxSubmitter
    {
        /// <summary>Submit to active blockers. Succeeds if at least one accepts (200) or reports duplicate (409).</summary>
        Task<(bool Ok, string Detail)> SubmitAsync(object txBody);
    }

    public class BlockerTxSubmitter : IBlockerTxSubmitter
    {
        private readonly IStorageService _storage;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<BlockerTxSubmitter> _logger;

        public BlockerTxSubmitter(
            IStorageService storage,
            IHttpClientFactory httpClientFactory,
            ILogger<BlockerTxSubmitter> logger)
        {
            _storage = storage;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<(bool Ok, string Detail)> SubmitAsync(object txBody)
        {
            var blockers = await _storage.GetActiveBlockCreatorNodesAsync();
            if (blockers == null || blockers.Count == 0)
                return (false, "No active Block Creator nodes");

            var json = JsonSerializer.Serialize(txBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var client = _httpClientFactory.CreateClient("blocker-tx");
            var accepted = 0;
            var duplicate = 0;
            var errors = new System.Collections.Generic.List<string>();

            foreach (var node in blockers)
            {
                var endpoint = NodeAddress.Of(node.Endpoint, node.TcpEndpoint, node.PublicKey);
                if (string.IsNullOrEmpty(endpoint)) continue;
                try
                {
                    var url = $"{endpoint}/api/tx/submit";
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync(url, content);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode)
                    {
                        accepted++;
                        _logger.LogInformation("TopUp tx accepted by {Endpoint}", endpoint);
                    }
                    else if ((int)resp.StatusCode == 409)
                    {
                        duplicate++;
                        _logger.LogInformation("TopUp tx already in pool at {Endpoint}", endpoint);
                    }
                    else
                    {
                        errors.Add($"{endpoint}: {(int)resp.StatusCode} {body}");
                        _logger.LogWarning("TopUp tx rejected by {Endpoint}: {Status} {Body}", endpoint, (int)resp.StatusCode, body);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{endpoint}: {ex.Message}");
                    _logger.LogWarning(ex, "TopUp tx submit error to {Endpoint}", endpoint);
                }
            }

            if (accepted > 0 || duplicate > 0)
                return (true, $"accepted={accepted} duplicate={duplicate}");

            return (false, errors.Count > 0 ? string.Join("; ", errors.Take(3)) : "All blockers rejected TopUp");
        }
    }
}

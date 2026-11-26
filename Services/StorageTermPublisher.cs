using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Publishes queued storage terms as StorageTerm txs (Central-signed, up to 200 entries each). Entries leave the
    /// queue only once a Blocker accepted the tx; otherwise they go out on the next run.
    /// </summary>
    public class StorageTermPublisher
    {
        private readonly EconomicsService _econ;
        private readonly KeyService _keys;
        private readonly IBlockerTxSubmitter _submitter;
        private readonly ILogger<StorageTermPublisher> _log;

        public StorageTermPublisher(EconomicsService econ, KeyService keys, IBlockerTxSubmitter submitter, ILogger<StorageTermPublisher> log)
        {
            _econ = econ;
            _keys = keys;
            _submitter = submitter;
            _log = log;
        }

        /// <summary>Returns how many entries went on chain in this run.</summary>
        public async Task<int> PublishPendingAsync(int maxTxs = 20)
        {
            var queued = await _econ.QueueAllTermsOnceAsync();
            if (queued > 0) _log.LogInformation("StorageTerm: queued {Count} existing escrows for the chain", queued);
            var published = 0;
            for (var i = 0; i < maxTxs; i++)
            {
                var entries = await _econ.PendingTermsAsync();
                if (entries.Count == 0) break;
                var id = "st-" + Guid.NewGuid().ToString("N");
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var terms = StorageTermNetworkTx.Serialize(entries);
                var payload = StorageTermNetworkTx.SignPayload(id, ts, terms);
                var sig = _keys.SignDecision(payload);
                var body = StorageTermNetworkTx.BuildSubmitBody(id, _keys.GetCentralPublicKey(), ts, terms, entries.Count, sig, payload);
                var (ok, detail) = await _submitter.SubmitAsync(body);
                if (!ok)
                {
                    _log.LogWarning("StorageTerm tx {TxId} ({Count} entries) not accepted: {Detail}", id, entries.Count, detail);
                    break;
                }
                await _econ.MarkTermsPublishedAsync(entries);
                published += entries.Count;
                _log.LogInformation("StorageTerm tx {TxId}: {Count} entries", id, entries.Count);
            }
            return published;
        }
    }
}

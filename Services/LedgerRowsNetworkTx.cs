using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeltaniumApi.Models;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// LedgerRows: one anchor window of the wallet ledger, published on chain so Blockers replay balances from the
    /// chain alone (plan phase 1). Each row carries exactly the fields of <see cref="EconomicsService.LedgerLeaf"/>
    /// (amount F10, time to the microsecond), so a Blocker re-hashes them to the anchor's LedgerRoot. Memos are left
    /// out (free text). <c>data.rows</c> is a JSON string; SignPayload =
    /// <c>LEDGERROWS|{id}|{timestamp}|{anchorSeq}|{part}|{parts}|{sha256hex(rows)}</c>, signed by Central.
    /// </summary>
    public static class LedgerRowsNetworkTx
    {
        public const string TypeName = "LedgerRows";
        public const int RowsPerPart = 200;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        public static Dictionary<string, string?> Row(WalletLedgerEntry e) => new()
        {
            ["id"] = e.Id,
            ["from"] = e.FromPublicKey,
            ["to"] = e.ToPublicKey,
            ["amount"] = e.Amount.ToString("F10", CultureInfo.InvariantCulture),
            ["kind"] = e.Kind,
            ["refId"] = e.RefId,
            ["at"] = DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture),
        };

        /// <summary>The window's rows (canonical order, as anchored) in parts of <see cref="RowsPerPart"/>; at least one part.</summary>
        public static List<string> Parts(IReadOnlyList<WalletLedgerEntry> entries)
        {
            var parts = new List<string>();
            for (var i = 0; i < entries.Count; i += RowsPerPart)
                parts.Add(JsonSerializer.Serialize(entries.Skip(i).Take(RowsPerPart).Select(Row).ToList(), JsonOptions));
            if (parts.Count == 0) parts.Add("[]");
            return parts;
        }

        /// <summary>Same id on every retry, so a Blocker that already has it answers "duplicate" instead of storing it twice.</summary>
        public static string TxId(long anchorSeq, int part) => $"lr-{anchorSeq:D12}-{part:D4}";

        public static string SignPayload(string id, long timestamp, long anchorSeq, int part, int parts, string rows) =>
            $"LEDGERROWS|{id}|{timestamp}|{anchorSeq}|{part}|{parts}|{StorageTermNetworkTx.Hash(rows)}";

        public static object BuildSubmitBody(string id, string centralPublicKey, long timestamp, long anchorSeq, int part, int parts,
            string rows, string signature, string signPayload) => new
        {
            id,
            type = TypeName,
            from = centralPublicKey,
            to = "",
            data = new Dictionary<string, object> { ["anchorSeq"] = anchorSeq, ["part"] = part, ["parts"] = parts, ["rows"] = rows },
            timestamp,
            fee = StorageTermNetworkTx.FeeString,
            signature,
            signPayload
        };
    }

    /// <summary>
    /// Publishes every anchored ledger window on chain, oldest first, resuming after the last window a Blocker accepted.
    /// Anchor 1 starts at the very first ledger row, so the chain ends up with the whole history.
    /// </summary>
    public class LedgerRowsPublisher
    {
        private readonly EconomicsService _econ;
        private readonly KeyService _keys;
        private readonly IBlockerTxSubmitter _submitter;
        private readonly Microsoft.Extensions.Logging.ILogger<LedgerRowsPublisher> _log;

        public LedgerRowsPublisher(EconomicsService econ, KeyService keys, IBlockerTxSubmitter submitter,
            Microsoft.Extensions.Logging.ILogger<LedgerRowsPublisher> log)
        {
            _econ = econ;
            _keys = keys;
            _submitter = submitter;
            _log = log;
        }

        /// <summary>Returns how many windows went on chain in this run.</summary>
        public async System.Threading.Tasks.Task<int> PublishPendingAsync(int maxWindows = 50)
        {
            var cursor = await _econ.GetLedgerRowsCursorAsync();
            var anchors = (await _econ.ListAnchorsAsync(500)).Where(a => a.Seq > cursor).OrderBy(a => a.Seq).Take(maxWindows).ToList();
            var done = 0;
            foreach (var a in anchors)
            {
                if (a.Seq != cursor + 1)
                {
                    _log.LogWarning("LedgerRows: anchor {Seq} missing; windows must go on chain in order", cursor + 1);
                    break;
                }
                var (_, entries) = await _econ.GetAnchorEntriesAsync(a.Seq);
                var parts = LedgerRowsNetworkTx.Parts(entries);
                var ts = StorageTermNetworkTx.Unix(a.UpToInclusive);
                var ok = true;
                for (var p = 0; p < parts.Count && ok; p++)
                {
                    var id = LedgerRowsNetworkTx.TxId(a.Seq, p);
                    var payload = LedgerRowsNetworkTx.SignPayload(id, ts, a.Seq, p, parts.Count, parts[p]);
                    var body = LedgerRowsNetworkTx.BuildSubmitBody(id, _keys.GetCentralPublicKey(), ts, a.Seq, p, parts.Count,
                        parts[p], _keys.SignDecision(payload), payload);
                    var (accepted, detail) = await _submitter.SubmitAsync(body);
                    if (!accepted)
                    {
                        _log.LogWarning("LedgerRows {TxId} not accepted: {Detail}", id, detail);
                        ok = false;
                    }
                }
                if (!ok) break;
                await _econ.SetLedgerRowsCursorAsync(a.Seq);
                cursor = a.Seq;
                done++;
            }
            if (done > 0) _log.LogInformation("LedgerRows: {Count} window(s) on chain, through anchor {Seq}", done, cursor);
            return done;
        }
    }
}

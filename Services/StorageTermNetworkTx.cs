using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// StorageTerm: Central publishes on chain how long each stored item is paid for, so a Store (or anyone)
    /// reads from the chain alone when an item may be deleted, without asking Central.
    ///
    /// <c>data.terms</c> is a JSON string (kept as a string so it survives any re-serialization byte for byte):
    /// an array of entries { fileId, status, paidUntil, deleteAfter, blocks? } (unix seconds). The latest entry
    /// for a fileId wins. SignPayload = <c>STORAGETERM|{id}|{timestamp}|{sha256hex(terms)}</c>, signed by Central,
    /// so anyone holding Central's public key can check an entry was published by Central and not altered.
    /// </summary>
    public static class StorageTermNetworkTx
    {
        public const string TypeName = "StorageTerm";
        public const string FeeString = "0.0000000000";
        public const int MaxEntries = 200;

        /// <summary>active: paid; grace: renewal unpaid, still kept; stopped: deleted by the owner, runs out the paid
        /// month; ended: Expired / Closed / Lost. A Store deletes the item once now ≥ deleteAfter.</summary>
        public sealed record Entry(
            [property: JsonPropertyName("fileId")] string FileId,
            [property: JsonPropertyName("status")] string Status,
            [property: JsonPropertyName("paidUntil")] long PaidUntil,
            [property: JsonPropertyName("deleteAfter")] long DeleteAfter,
            [property: JsonPropertyName("blocks")] List<string>? Blocks = null);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        public static string Serialize(IEnumerable<Entry> entries) => JsonSerializer.Serialize(entries.ToList(), JsonOptions);

        public static List<Entry> Parse(string terms) => JsonSerializer.Deserialize<List<Entry>>(terms, JsonOptions) ?? new();

        public static string Hash(string terms) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terms))).ToLowerInvariant();

        public static string SignPayload(string id, long timestamp, string terms) => $"STORAGETERM|{id}|{timestamp}|{Hash(terms)}";

        public static object BuildSubmitBody(string id, string centralPublicKey, long timestamp, string terms, int count,
            string signature, string signPayload) => new
        {
            id,
            type = TypeName,
            from = centralPublicKey,
            to = "",
            data = new Dictionary<string, object> { ["terms"] = terms, ["count"] = count },
            timestamp,
            fee = FeeString,
            signature,
            signPayload
        };

        public static long Unix(DateTime t) =>
            new DateTimeOffset(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime()).ToUnixTimeSeconds();
    }
}

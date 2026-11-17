using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// TOKENOMICS_V2 §11 phase B: user value moves (Transfer, Donate = tip) are mirrored on chain.
    /// The tx carries the user's own signed HTTP request, so a Blocker can check the user authorised
    /// exactly this amount and recipient without trusting Central, and then check the balance against
    /// the BalancesRoot anchored in its own chain.
    /// </summary>
    public static class ValueNetworkTx
    {
        public const string FeeString = "0.0000000000";

        public static long ToUnixMs(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        public static object BuildSubmitBody(
            string type,
            string id,
            string from,
            string to,
            decimal amount,
            DateTime ledgerAtUtc,
            string method,
            string pathAndQuery,
            string timestamp,
            string requestBody,
            string userSignature) => new
        {
            id,
            type,
            from,
            to,
            data = new Dictionary<string, object>
            {
                ["amount"] = amount.ToString("F10", CultureInfo.InvariantCulture),
                ["ledgerAtMs"] = ToUnixMs(ledgerAtUtc),
                ["auth"] = new Dictionary<string, object>
                {
                    ["method"] = method,
                    ["path"] = pathAndQuery,
                    ["timestamp"] = timestamp,
                    ["body"] = requestBody
                }
            },
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            fee = FeeString,
            signature = userSignature,
            signPayload = SignatureAuthHelper.BuildSignedMessage(method, pathAndQuery, timestamp, requestBody)
        };
    }
}

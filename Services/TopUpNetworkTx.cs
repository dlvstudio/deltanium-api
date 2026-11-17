using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaniumApi.Models;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Builds the deterministic TopUp SignPayload shared by Central, Super Admin, and blockers.
    /// Tx Id = order.Id. From = Central public key (treasury authority on-network).
    /// </summary>
    public static class TopUpNetworkTx
    {
        public const string TypeName = "TopUp";
        public const string FeeString = "0.0000000000";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        public static long OrderTimestampUnix(TopUpOrder order)
        {
            var utc = order.CreatedAt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(order.CreatedAt, DateTimeKind.Utc)
                : order.CreatedAt.ToUniversalTime();
            return new DateTimeOffset(utc).ToUnixTimeSeconds();
        }

        public static Dictionary<string, object> BuildData(TopUpOrder order) => new()
        {
            ["orderId"] = order.Id,
            ["dCredited"] = order.DCredited,
            ["fiatAmountVnd"] = order.FiatAmountVnd,
            ["feePercent"] = order.FeePercent
        };

        public static string BuildSignPayload(TopUpOrder order, string centralPublicKey)
        {
            var payload = new Dictionary<string, object?>
            {
                ["id"] = order.Id,
                ["type"] = TypeName,
                ["from"] = centralPublicKey,
                ["to"] = order.BuyerPublicKey,
                ["data"] = BuildData(order),
                ["timestamp"] = OrderTimestampUnix(order),
                ["fee"] = FeeString
            };
            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        public static object BuildSubmitBody(
            TopUpOrder order,
            string centralPublicKey,
            string centralSignature,
            string superAdminSignature,
            string signPayload) => new
        {
            id = order.Id,
            type = TypeName,
            from = centralPublicKey,
            to = order.BuyerPublicKey,
            data = BuildData(order),
            timestamp = OrderTimestampUnix(order),
            fee = FeeString,
            signature = centralSignature,
            signPayload,
            superAdminSignature
        };

        public static string FormatD(decimal value) =>
            value.ToString(CultureInfo.InvariantCulture);
    }
}

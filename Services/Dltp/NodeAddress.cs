using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DeltaniumApi.Services.Dltp
{
    /// <summary>
    /// A node is reachable over HTTPS, over DLTP (TCP), or both. Central code builds request URLs from
    /// <see cref="Of"/>: the HTTPS endpoint when there is one, otherwise a stand-in https://host:port URL
    /// for the TCP endpoint, which <see cref="DltpRoutingHandler"/> recognises and sends over DLTP.
    /// </summary>
    public static class NodeAddress
    {
        private static readonly Regex TcpRe = new(@"^(tcps?)://([A-Za-z0-9.\-]+|\[[0-9a-fA-F:]+\]):(\d{1,5})/?$", RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, (bool Tls, string Host, int Port)> TcpOnly = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Base URL Central uses to reach a node ("" when it has neither endpoint).</summary>
        public static string Of(string? httpsEndpoint, string? tcpEndpoint)
        {
            var https = (httpsEndpoint ?? "").Trim().TrimEnd('/');
            if (https.Length > 0) return https;
            if (!TryParseTcp(tcpEndpoint, out var tls, out var host, out var port)) return "";
            var authority = $"{host}:{port}";
            TcpOnly[authority] = (tls, host, port);
            return "https://" + authority;
        }

        public static bool TryParseTcp(string? value, out bool tls, out string host, out int port)
        {
            tls = false; host = ""; port = 0;
            var m = TcpRe.Match((value ?? "").Trim());
            if (!m.Success || !int.TryParse(m.Groups[3].Value, out port) || port is < 1 or > 65535) return false;
            tls = m.Groups[1].Value == "tcps";
            host = m.Groups[2].Value;
            return true;
        }

        /// <summary>Plain tcp:// is only accepted for local development hosts.</summary>
        public static bool IsAcceptableTcp(string? value) =>
            TryParseTcp(value, out var tls, out var host, out _) &&
            (tls || host is "localhost" or "127.0.0.1" or "[::1]");

        public static bool IsAcceptableHttps(string? value) =>
            Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

        /// <summary>True when this host:port stands for a TCP-only node (see <see cref="Of"/>).</summary>
        internal static bool TryGetTcp(Uri uri, out (bool Tls, string Host, int Port) target) =>
            TcpOnly.TryGetValue($"{uri.Host}:{uri.Port}", out target) ||
            TcpOnly.TryGetValue($"[{uri.Host}]:{uri.Port}", out target);
    }
}

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DeltaniumApi.Services.Dltp
{
    /// <summary>
    /// A node is reachable over HTTPS, over DLTP (TCP), or both. Central code builds request URLs from
    /// <see cref="Of"/>: the HTTPS endpoint when there is one, otherwise a stand-in https://host:port URL
    /// for the TCP endpoint, which <see cref="DltpRoutingHandler"/> recognises and sends over DLTP.
    ///
    /// A DLTP endpoint names either a host (tcps://store.example.com:7333, TLS checked against the public CA
    /// like HTTPS) or a public IP address (tcps://203.0.113.7:7333, for nodes on a home or personal computer
    /// with no domain). With an IP address the node proves who it is with its registered node key
    /// (see <see cref="DltpIdentity"/>), so <see cref="Of"/> needs the node's public key.
    /// </summary>
    public static class NodeAddress
    {
        private static readonly Regex TcpRe = new(@"^(tcps?)://([A-Za-z0-9.\-]+|\[[0-9a-fA-F:.]+\]):(\d{1,5})/?$", RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, DltpTarget> TcpOnly = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Host placeholder a node may register ("tcps://auto:7333"): Central fills in the IP it sees the node call from.</summary>
        public const string AutoHost = "auto";

        /// <summary>Base URL Central uses to reach a node ("" when it has neither endpoint).</summary>
        public static string Of(string? httpsEndpoint, string? tcpEndpoint, string? nodePublicKey = null)
        {
            var https = (httpsEndpoint ?? "").Trim().TrimEnd('/');
            if (https.Length > 0) return https;
            if (!TryParseTcp(tcpEndpoint, out var tls, out var host, out var port)) return "";
            var authority = $"{host}:{port}";
            TcpOnly[authority] = new DltpTarget(tls, host, port, string.IsNullOrWhiteSpace(nodePublicKey) ? null : nodePublicKey.Trim());
            return "https://" + authority;
        }

        public static bool TryParseTcp(string? value, out bool tls, out string host, out int port)
        {
            tls = false; host = ""; port = 0;
            var m = TcpRe.Match((value ?? "").Trim());
            if (!m.Success || !int.TryParse(m.Groups[3].Value, out port) || port is < 1 or > 65535) return false;
            tls = m.Groups[1].Value == "tcps";
            host = m.Groups[2].Value.ToLowerInvariant();
            return true;
        }

        /// <summary>
        /// tcps://host:port or tcps://public-ip:port. Plain tcp:// only for local development hosts, and an IP
        /// address must be public: no loopback, private, link-local or carrier-grade NAT ranges, so a node
        /// cannot point Central at its own internal network.
        /// </summary>
        public static bool IsAcceptableTcp(string? value)
        {
            if (!TryParseTcp(value, out var tls, out var host, out _)) return false;
            if (!tls) return host is "localhost" or "127.0.0.1" or "[::1]";
            if (host == AutoHost || host == "localhost") return false;
            if (IsIpLiteral(host, out var ip)) return IsPublicIp(ip);
            return host.Contains('.');
        }

        public static bool IsAcceptableHttps(string? value) =>
            Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

        /// <summary>"tcps://auto:7333" becomes "tcps://&lt;the node's public IP&gt;:7333"; other values are returned as they are.</summary>
        public static string? ResolveAuto(string? tcpEndpoint, IPAddress? remote)
        {
            if (!TryParseTcp(tcpEndpoint, out var tls, out var host, out var port) || host != AutoHost) return tcpEndpoint;
            if (remote == null) return null;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            var h = remote.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{remote}]" : remote.ToString();
            return $"{(tls ? "tcps" : "tcp")}://{h}:{port}";
        }

        public static bool IsIpLiteral(string host, out IPAddress ip) =>
            IPAddress.TryParse(host.Trim('[', ']'), out ip!);

        public static bool IsPublicIp(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
                         (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||  // carrier-grade NAT
                         (b[0] == 169 && b[1] == 254) ||
                         (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                         (b[0] == 192 && b[1] == 168) ||
                         (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                         (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = ip.GetAddressBytes();
                return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast ||
                         (b[0] & 0xFE) == 0xFC);  // unique local fc00::/7
            }
            return false;
        }

        /// <summary>True when this host:port stands for a TCP-only node (see <see cref="Of"/>).</summary>
        internal static bool TryGetTcp(Uri uri, out DltpTarget target) =>
            TcpOnly.TryGetValue($"{uri.Host}:{uri.Port}", out target!) ||
            TcpOnly.TryGetValue($"[{uri.Host}]:{uri.Port}", out target!);
    }

    /// <summary>Where to dial a TCP-only node, and the node key that must answer when the address is an IP.</summary>
    public record DltpTarget(bool Tls, string Host, int Port, string? NodePublicKey)
    {
        /// <summary>IP-address endpoints authenticate with the node key instead of a CA certificate.</summary>
        public bool KeyMode => Tls && NodeAddress.IsIpLiteral(Host, out _);
    }
}

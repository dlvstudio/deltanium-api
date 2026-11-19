using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DeltaniumApi.Services.Dltp
{
    /// <summary>
    /// DLTP v1 — Deltanium's TCP transport, carrying the same requests as the HTTPS API.
    ///
    ///   Address:  tcps://host:port  (TLS, the node's normal certificate)   tcp://host:port  (plain, local dev only)
    ///   Preface:  after connect (+TLS) the client writes "DLTP/1\n"; the server answers "DLTP/1\n".
    ///   Frame:    4-byte big-endian length, then that many bytes of UTF-8 JSON. Max 16 MiB.
    ///   Request:  {"id":1,"method":"GET","path":"/api/version","headers":{"X-Timestamp":"..."},"body":"&lt;base64&gt;"}
    ///   Response: {"id":1,"status":200,"headers":{"Content-Type":"application/json"},"body":"&lt;base64&gt;"}
    ///
    /// Requests are answered by the node's own HTTP pipeline, so paths, headers and signatures
    /// (METHOD + path + timestamp + sha256(body)) are exactly those of the HTTPS API.
    /// </summary>
    public static class DltpWire
    {
        public const string Preface = "DLTP/1\n";
        public const int MaxFrameBytes = 16 * 1024 * 1024;

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static async Task WritePrefaceAsync(Stream s, CancellationToken ct)
        {
            var b = Encoding.ASCII.GetBytes(Preface);
            await s.WriteAsync(b, ct);
            await s.FlushAsync(ct);
        }

        public static async Task ExpectPrefaceAsync(Stream s, CancellationToken ct)
        {
            var expected = Encoding.ASCII.GetBytes(Preface);
            var got = new byte[expected.Length];
            await ReadExactlyAsync(s, got, ct);
            if (!got.AsSpan().SequenceEqual(expected)) throw new IOException("Not a DLTP/1 peer");
        }

        public static async Task WriteFrameAsync<T>(Stream s, T message, CancellationToken ct)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            if (payload.Length > MaxFrameBytes) throw new IOException("DLTP frame too large");
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
            await s.WriteAsync(header, ct);
            await s.WriteAsync(payload, ct);
            await s.FlushAsync(ct);
        }

        public static async Task<T> ReadFrameAsync<T>(Stream s, CancellationToken ct)
        {
            var header = new byte[4];
            await ReadExactlyAsync(s, header, ct);
            var len = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (len == 0 || len > MaxFrameBytes) throw new IOException("Bad DLTP frame length");
            var payload = new byte[len];
            await ReadExactlyAsync(s, payload, ct);
            return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new IOException("Empty DLTP frame");
        }

        private static async Task ReadExactlyAsync(Stream s, byte[] buffer, CancellationToken ct)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await s.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0) throw new EndOfStreamException("DLTP connection closed");
                read += n;
            }
        }
    }

    public class DltpRequest
    {
        public long Id { get; set; }
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "/";
        public Dictionary<string, string> Headers { get; set; } = new();
        public string? Body { get; set; }
    }

    public class DltpResponse
    {
        public long Id { get; set; }
        public int Status { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new();
        public string? Body { get; set; }
    }
}

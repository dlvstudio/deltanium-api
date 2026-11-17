using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DeltaniumApi.Services
{
    /// <summary>Minimal PDF writer for offline Super Admin key backup (no third-party PDF package).</summary>
    public static class SuperAdminKeyPdf
    {
        public static string Write(string pdfPath, string publicKey, string privateKey, DateTime? generatedAtUtc = null)
        {
            var when = (generatedAtUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
            var lines = new List<string>
            {
                "Deltanium — Super Admin key backup",
                "",
                "KEEP THIS FILE OFFLINE. Do not upload PrivateKey to the API host.",
                "Set only PublicKey as SUPER_ADMIN_PUBLIC_KEY on API + blockers.",
                "",
                "Generated: " + when,
                "",
                "PublicKey:",
                publicKey,
                "",
                "PrivateKey:",
                privateKey,
                "",
                "Approve top-ups with /admin/topup.html or Scripts/Complete-TopUp.ps1",
                "using this PrivateKey. Never commit this PDF to git."
            };

            // Wrap long hex lines for Courier ~80 chars printable width
            var wrapped = new List<string>();
            foreach (var line in lines)
            {
                if (line.Length <= 90)
                    wrapped.Add(line);
                else
                {
                    for (var i = 0; i < line.Length; i += 90)
                        wrapped.Add(line.Substring(i, Math.Min(90, line.Length - i)));
                }
            }

            var content = new StringBuilder();
            content.AppendLine("BT");
            content.AppendLine("/F1 11 Tf");
            content.AppendLine("50 750 Td");
            content.AppendLine("14 TL");
            for (var i = 0; i < wrapped.Count; i++)
            {
                if (i > 0) content.AppendLine("T*");
                content.AppendLine($"({EscapePdf(wrapped[i])}) Tj");
            }
            content.AppendLine("ET");
            var stream = content.ToString();
            var streamBytes = Encoding.ASCII.GetBytes(stream);

            var sb = new StringBuilder();
            var offsets = new List<long> { 0 };

            void Mark() => offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));

            sb.Append("%PDF-1.4\n");
            Mark(); // obj 1
            sb.Append("1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj\n");
            Mark();
            sb.Append("2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj\n");
            Mark();
            sb.Append("3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ");
            sb.Append("/Contents 4 0 R /Resources<< /Font<< /F1 5 0 R >> >> >>endobj\n");
            Mark();
            sb.Append($"4 0 obj<< /Length {streamBytes.Length} >>stream\n");
            sb.Append(stream);
            if (!stream.EndsWith("\n")) sb.Append('\n');
            sb.Append("endstream\nendobj\n");
            Mark();
            sb.Append("5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>endobj\n");

            var xrefPos = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append($"xref\n0 {offsets.Count}\n");
            sb.Append("0000000000 65535 f \n");
            for (var i = 1; i < offsets.Count; i++)
                sb.Append($"{offsets[i]:D10} 00000 n \n");
            sb.Append($"trailer<< /Size {offsets.Count} /Root 1 0 R >>\n");
            sb.Append($"startxref\n{xrefPos}\n%%EOF\n");

            var dir = Path.GetDirectoryName(pdfPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(pdfPath, Encoding.ASCII.GetBytes(sb.ToString()));
            return pdfPath;
        }

        private static string EscapePdf(string s) =>
            s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }
}

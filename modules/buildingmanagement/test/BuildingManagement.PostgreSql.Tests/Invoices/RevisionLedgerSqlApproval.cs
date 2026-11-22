using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BuildingManagement.PostgreSqlTests;

internal static class RevisionLedgerSqlApproval
{
    private const string UpFileName =
        "Phase2G3A7_RevisionLedger_Patched_Review.sql";
    private const string DownFileName =
        "Phase2G3A7_RevisionLedger_Down_Review.sql";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string LoadApprovedSql(
        string variable,
        string filename,
        string expectedCanonicalSha256)
    {
        var canonicalBytes = LoadCanonicalBytes(
            variable, filename, expectedCanonicalSha256);
        return StrictUtf8.GetString(canonicalBytes);
    }

    public static string LoadApprovedSqlBody(
        string variable,
        string filename,
        string expectedCanonicalSha256)
    {
        var sql = LoadApprovedSql(variable, filename, expectedCanonicalSha256);
        var match = Regex.Match(sql,
            @"\A\s*START TRANSACTION;\s*(.*?)\s*COMMIT;\s*\z",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidOperationException(
                "Safety stop: reviewed SQL must have one START/COMMIT wrapper.");

        var body = match.Groups[1].Value;
        if (string.Equals(filename, UpFileName, StringComparison.Ordinal) &&
            (!body.Contains("TR_BM_InvoiceDraftRevision_ProtectTruncate",
                    StringComparison.Ordinal) ||
             !body.Contains("CK_BM_InvoiceDraftRevision_HexSha256",
                    StringComparison.Ordinal)))
            throw new InvalidOperationException("Safety stop: Up protections missing.");

        if (string.Equals(filename, DownFileName, StringComparison.Ordinal))
        {
            var lockAt = body.IndexOf(
                "LOCK TABLE \"BuildingManagementInvoiceDraftRevisions\"",
                StringComparison.Ordinal);
            var checkAt = body.IndexOf("IF EXISTS", StringComparison.Ordinal);
            var dropAt = body.IndexOf("DROP TABLE", StringComparison.Ordinal);
            if (lockAt < 0 || checkAt <= lockAt || dropAt <= checkAt)
                throw new InvalidOperationException(
                    "Safety stop: rollback lock/check/drop ordering is wrong.");
        }

        return body;
    }

    private static byte[] LoadCanonicalBytes(
        string variable,
        string filename,
        string expectedCanonicalSha256)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), filename, StringComparison.Ordinal) ||
            !File.Exists(path))
            throw new InvalidOperationException(
                "Safety stop: reviewed SQL must be supplied as an absolute local file: " +
                filename);

        var canonicalBytes = Canonicalize(File.ReadAllBytes(path));
        var actual = Convert.ToHexString(SHA256.HashData(canonicalBytes))
            .ToLowerInvariant();
        if (!string.Equals(actual, expectedCanonicalSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Safety stop: SQL checksum changed; a new human review is required: " +
                filename);

        return canonicalBytes;
    }

    private static byte[] Canonicalize(byte[] bytes)
    {
        var start = bytes.Length >= 3 &&
                    bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? 3
            : 0;
        var text = StrictUtf8.GetString(bytes, start, bytes.Length - start)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        return StrictUtf8.GetBytes(text);
    }
}

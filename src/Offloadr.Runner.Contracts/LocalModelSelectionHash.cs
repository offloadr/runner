using System.Security.Cryptography;
using System.Text;

namespace Offloadr.Common.V1;

public static class LocalModelSelectionHash
{
    public const int Sha256HexLength = 64;

    public static string Compute(string? filename, long sizeBytes, ModelCategory category)
    {
        var normalizedFileName = NormalizeFileName(filename);
        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            throw new ArgumentException("Filename is required.", nameof(filename));
        }

        var normalizedCategory = NormalizeCategory(category);
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(normalizedFileName));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(sizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(((int)normalizedCategory).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeSnapshotDigest(IEnumerable<string> selectionHashes)
    {
        ArgumentNullException.ThrowIfNull(selectionHashes);

        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var selectionHash in selectionHashes
                     .Select(NormalizeSelectionHash)
                     .Where(static value => value.Length > 0)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static value => value, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(selectionHash));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool IsValidSelectionHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Length == Sha256HexLength
               && normalized.All(static c => char.IsAsciiHexDigit(c));
    }

    public static string NormalizeSelectionHash(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    public static ModelCategory NormalizeCategory(ModelCategory category)
        => category == ModelCategory.Unspecified ? ModelCategory.Other : category;

    public static string NormalizeFileName(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

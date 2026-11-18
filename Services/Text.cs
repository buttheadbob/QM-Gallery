using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QM_Gallery.Services;

public static partial class Text
{
    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex ManyNewlines();

    public static string Kebab(string? value, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "plugin";

        string slug = NonSlug().Replace(value.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
            slug = "plugin";
        if (slug.Length > maxLength)
            slug = slug[..maxLength].Trim('-');
        return slug;
    }

    public static string ShortHash(string? value, int length = 8)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(hash)[..length].ToLowerInvariant();
    }

    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    public static string CleanDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string[] lines = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .ToArray();

        string joined = string.Join('\n', lines).Trim();
        return ManyNewlines().Replace(joined, "\n\n");
    }

    public static string OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return Whitespace().Replace(value.ReplaceLineEndings(" "), " ").Trim();
    }

    public static string Truncate(string? value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= length)
            return value ?? "";
        return value[..Math.Max(0, length - 1)].TrimEnd() + "…";
    }

    public static List<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string RelativeTime(DateTimeOffset? value)
    {
        if (value is null)
            return "not yet";

        TimeSpan delta = DateTimeOffset.UtcNow - value.Value;
        if (delta.TotalSeconds < 60)
            return "just now";
        if (delta.TotalMinutes < 60)
            return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 24)
            return $"{(int)delta.TotalHours} h ago";
        if (delta.TotalDays < 30)
            return $"{(int)delta.TotalDays} d ago";
        if (delta.TotalDays < 365)
            return $"{(int)(delta.TotalDays / 30)} mo ago";
        return $"{(int)(delta.TotalDays / 365)} y ago";
    }

    public static string Date(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "unknown";

    public static string Number(int value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);
}

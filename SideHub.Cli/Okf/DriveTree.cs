using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SideHub.Cli.Okf;

/// <summary>
/// The workspace drive tree (<c>GET /api/workspaces/{id}/drive</c>) read for OKF: filters on what the backend read in each
/// page's front matter (<c>okf</c>), and the page an OKF path names. Same path rule as the Mémoire: a segment is the
/// item's kept path segment (<c>slug</c>) or its title (or uploaded file name) in lowercase with dashes.
/// </summary>
public static class DriveTree
{
    /// <summary>Latin letters NFD splits into a base letter and accents, folded here so the result does not depend on ICU
    /// (sidehub-cli runs with invariant globalization, where Normalize changes nothing).</summary>
    private const string Accented = "àáâãäåāăąçćĉċčďèéêëēĕėęěĝğġģĥìíîïĩīĭįĵķĺļľñńņňòóôõöōŏőŕŗřśŝşšţťùúûüũūŭůűųŵýÿŷźżž";
    private const string Folded = "aaaaaaaaacccccdeeeeeeeeegggghiiiiiiiijklllnnnnoooooooorrrssssttuuuuuuuuuuwyyyzzz";

    public sealed record OkfFilter(string? Type, string? Tag, string? Status, bool Stale)
    {
        public bool Any => Type is not null || Tag is not null || Status is not null || Stale;
    }

    public static IEnumerable<JsonElement> Flatten(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in items.EnumerateArray())
        {
            yield return item;
            if (item.TryGetProperty("children", out var children))
                foreach (var child in Flatten(children)) yield return child;
        }
    }

    /// <summary>The pages whose OKF facts match every given criterion (case ignored).</summary>
    public static IEnumerable<JsonElement> Concepts(JsonElement items, OkfFilter filter, DateTimeOffset now) =>
        Flatten(items).Where(i => i.TryGetProperty("okf", out var okf) && okf.ValueKind == JsonValueKind.Object && Matches(okf, filter, now));

    private static bool Matches(JsonElement okf, OkfFilter filter, DateTimeOffset now)
    {
        if (filter.Type is { } type && !string.Equals(Str(okf, "type"), type, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.Status is { } status && !string.Equals(Str(okf, "status"), status, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.Tag is { } tag && !(okf.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                && tags.EnumerateArray().Any(t => string.Equals(t.GetString(), tag, StringComparison.OrdinalIgnoreCase)))) return false;
        if (filter.Stale && !IsStale(okf, now)) return false;
        return true;
    }

    public static bool IsStale(JsonElement okf, DateTimeOffset now) =>
        Str(okf, "staleAfter") is { } at
        && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
        && now >= instant;

    /// <summary>
    /// The item an OKF path names from the workspace root (<c>/payments/refund-flow.md</c>, <c>payments/</c>,
    /// <c>payments/index.md</c>), or null. Archived items are left out.
    /// </summary>
    public static JsonElement? FindByPath(JsonElement items, string path)
    {
        var clean = path.Trim().Split('#', '?')[0];
        var folder = clean.EndsWith('/');
        var segments = clean.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0) return null;
        if (!folder)
        {
            var last = segments[^1];
            if (last.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) last = last[..^3];
            segments[^1] = last;
            if (last.Equals("index", StringComparison.OrdinalIgnoreCase)) { folder = true; segments.RemoveAt(segments.Count - 1); }
        }
        if (segments.Count == 0) return null;

        var level = items;
        for (var i = 0; i < segments.Count; i++)
        {
            var name = Slug(segments[i]);
            var wantFolder = i < segments.Count - 1 || folder;
            var candidates = level.ValueKind == JsonValueKind.Array
                ? level.EnumerateArray().Where(n => Str(n, "status") != "archived" && (Str(n, "type") == "folder") == wantFolder
                    && (Str(n, "slug") == name || Slug(Str(n, "fileName") ?? Str(n, "title")) == name)).ToList()
                : [];
            if (candidates.Count == 0) return null;
            var match = candidates.FirstOrDefault(n => Str(n, "type") == "page", candidates[0]);
            if (i == segments.Count - 1) return match;
            if (!match.TryGetProperty("children", out level)) return null;
        }
        return null;
    }

    /// <summary>Lowercase ASCII letters and digits joined by dashes, accents dropped (same as the backend's DriveSlug).</summary>
    public static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        if (text.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) text = text[..^3];
        var sb = new StringBuilder(text.Length);
        var dash = false;
        var folded = new string(text.ToLowerInvariant().Select(c => Accented.IndexOf(c) is var i and >= 0 ? Folded[i] : c).ToArray());
        foreach (var c in folded.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (dash && sb.Length > 0) sb.Append('-');
                sb.Append(lower);
                dash = false;
            }
            else dash = true;
        }
        return sb.ToString();
    }

    public static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

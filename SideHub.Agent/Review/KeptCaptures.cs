using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SideHub.Agent.Conversation;
using SideHub.Agent.Models;

namespace SideHub.Agent.Review;

/// <summary>
/// Images a CLI saw, copied to a folder of their own (doc 70): a screenshot file is often overwritten or deleted soon
/// after, what kept them (a review round, a run of tests) is read days later. Named <c>r&lt;round&gt;-&lt;n&gt;</c>, read back one
/// by one.
/// </summary>
public static class KeptCaptures
{
    /// <summary>Images kept at once: the last ones the CLI saw.</summary>
    public const int Max = 20;
    private const long MaxTotalBytes = 40 * 1024 * 1024;

    private static readonly Regex Name = new(@"^r\d{1,4}-\d{1,3}\.(?:png|jpg|webp)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Copies the last images of <paramref name="seen"/> to <paramref name="folder"/>, in the order they came. Same image
    /// twice kept once. Empty when there is none: the folder is then not created.
    /// </summary>
    public static IReadOnlyList<ReviewCapture> Keep(string folder, int round, IReadOnlyList<WorkReviewTracker.SeenImage> seen)
    {
        var kept = new List<(WorkReviewTracker.SeenImage Seen, byte[] Bytes)>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        for (var i = seen.Count - 1; i >= 0 && kept.Count < Max; i--)
        {
            if (ToolImages.Load(seen[i].Image).Bytes is not { } bytes || total + bytes.Length > MaxTotalBytes
                || !hashes.Add(Convert.ToHexString(SHA256.HashData(bytes))))
                continue;
            total += bytes.Length;
            kept.Add((seen[i], bytes));
        }
        if (kept.Count == 0)
            return [];
        kept.Reverse();
        Directory.CreateDirectory(folder);
        var captures = new List<ReviewCapture>(kept.Count);
        foreach (var (image, bytes) in kept)
        {
            var name = $"r{round}-{captures.Count + 1}{Path.GetExtension(image.Image.Name)}";
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            captures.Add(new ReviewCapture
            {
                Name = name, At = image.Image.At, Tool = image.Tool, Title = image.Title, Size = bytes.Length,
            });
        }
        return captures;
    }

    /// <summary>
    /// One image kept in <paramref name="folder"/> as base64, or why not: <c>invalid</c> (not the name of a capture),
    /// <see cref="ReviewDiffErrors.Purged"/> (no longer there, or never kept).
    /// </summary>
    public static async Task<(string? Data, string? Error)> ReadAsync(string folder, string image, CancellationToken ct)
    {
        if (!Name.IsMatch(image))
            return (null, "invalid");
        try
        {
            var path = Path.Combine(folder, image);
            if (!File.Exists(path) || new FileInfo(path).Length > ToolImages.MaxBytes)
                return (null, ReviewDiffErrors.Purged);
            return (Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ReviewDiffErrors.Purged);
        }
    }
}

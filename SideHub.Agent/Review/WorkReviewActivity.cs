using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHub.Agent.Review;

/// <summary>
/// When each round of a reviewed work started and ended, in a JSON-lines file of the run directory
/// (<c>.sidehub/run/reviews/activity.jsonl</c>): shared by every agent of that folder, so a round can tell that another
/// one changed the same files at the same time. A round's start says where it ran (<see cref="Entry.Folder"/>): the
/// queue's worktree (doc 71) never overlaps the repository's folder.
/// </summary>
public sealed class WorkReviewActivity(string path)
{
    /// <summary>A round that never wrote its end (its agent crashed) stops counting as running after this.</summary>
    public static readonly TimeSpan MaxRoundDuration = TimeSpan.FromHours(12);

    private static readonly object FileLock = new();

    public string Path => path;

    public sealed record Entry(
        [property: JsonPropertyName("reviewId")] Guid ReviewId,
        [property: JsonPropertyName("round")] int Round,
        [property: JsonPropertyName("agent")] string Agent,
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("at")] DateTimeOffset At,
        // The top level of the work tree the round photographs, on its start; null in the entries of older agents,
        // which count for every folder.
        [property: JsonPropertyName("folder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Folder = null);

    public const string Start = "start";
    public const string End = "end";

    public void Append(Entry entry)
    {
        var line = JsonSerializer.Serialize(entry) + "\n";
        lock (FileLock)
        {
            PrivateFiles.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // Appends of one short line by several agents stay whole (O_APPEND).
            using var writer = PrivateFiles.AppendText(path);
            writer.Write(line);
        }
    }

    /// <summary>Whether another round ran in <paramref name="folder"/> (any folder when null) between
    /// <paramref name="from"/> and <paramref name="to"/>.</summary>
    public bool Overlaps(Guid reviewId, int round, DateTimeOffset from, DateTimeOffset to, string? folder = null)
    {
        var rounds = new Dictionary<(Guid, int), (DateTimeOffset? Start, DateTimeOffset? End, string? Folder)>();
        foreach (var entry in Read())
        {
            if (entry.ReviewId == reviewId && entry.Round == round)
                continue;
            var key = (entry.ReviewId, entry.Round);
            var known = rounds.GetValueOrDefault(key);
            rounds[key] = entry.Event == Start ? known with { Start = entry.At, Folder = entry.Folder } : known with { End = entry.At };
        }

        foreach (var (start, end, other) in rounds.Values)
        {
            if (start is not { } s)
                continue;
            if (folder is not null && other is not null && !SameFolder(folder, other))
                continue;
            var e = end ?? s + MaxRoundDuration;
            if (s < to && e > from)
                return true;
        }
        return false;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Drops the entries older than <paramref name="cutoff"/>.</summary>
    public void Prune(DateTimeOffset cutoff)
    {
        lock (FileLock)
        {
            if (!File.Exists(path))
                return;
            var kept = Read().Where(e => e.At >= cutoff).Select(e => JsonSerializer.Serialize(e) + "\n");
            var tmp = path + ".tmp";
            PrivateFiles.WriteAllText(tmp, string.Concat(kept));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private List<Entry> Read()
    {
        var entries = new List<Entry>();
        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return entries;
        }

        foreach (var line in lines)
        {
            try
            {
                if (JsonSerializer.Deserialize<Entry>(line) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // A line cut by a crash: the others still count.
            }
        }
        return entries;
    }
}

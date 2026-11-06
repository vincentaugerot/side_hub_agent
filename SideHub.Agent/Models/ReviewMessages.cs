using System.Text.Json.Serialization;

namespace SideHub.Agent.Models;

/// <summary>
/// What one round of a delegated work changed in the repository (work review, see
/// <c>side_hub/doc/features/56-revue-du-travail.md</c>): sent when the round ends. File contents never leave the
/// machine here, only paths, line counts and commit subjects; the diff is asked for separately
/// (<c>review.diff.request</c>).
/// </summary>
public class ReviewChangesMessage
{
    [JsonPropertyName("type")]
    public string Type => "review.changes";

    [JsonPropertyName("reviewId")]
    public required Guid ReviewId { get; init; }

    /// <summary>1 for the work itself, then one per fix asked for.</summary>
    [JsonPropertyName("round")]
    public required int Round { get; init; }

    /// <summary><see cref="ReviewKinds.Run"/> or <see cref="ReviewKinds.Task"/>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    [JsonPropertyName("runId")]
    public Guid? RunId { get; init; }

    [JsonPropertyName("taskId")]
    public Guid? TaskId { get; init; }

    /// <summary>The CLI the round ran (the last one started in its terminal), and its session.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("cliSessionId")]
    public string? CliSessionId { get; init; }

    [JsonPropertyName("startedAt")]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("endedAt")]
    public required DateTimeOffset EndedAt { get; init; }

    [JsonPropertyName("startBranch")]
    public string? StartBranch { get; init; }

    [JsonPropertyName("startHead")]
    public string? StartHead { get; init; }

    [JsonPropertyName("endBranch")]
    public string? EndBranch { get; init; }

    [JsonPropertyName("endHead")]
    public string? EndHead { get; init; }

    /// <summary>At most <c>WorkReviewGit.MaxFiles</c>; <see cref="FilesTruncated"/> when there were more.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<ReviewFileChange> Files { get; init; } = [];

    [JsonPropertyName("filesTruncated")]
    public bool FilesTruncated { get; init; }

    [JsonPropertyName("filesChanged")]
    public int FilesChanged { get; init; }

    [JsonPropertyName("insertions")]
    public int Insertions { get; init; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; init; }

    /// <summary>Newest first, at most <c>WorkReviewGit.MaxCommits</c>; <see cref="CommitsTruncated"/> when there were more.</summary>
    [JsonPropertyName("commits")]
    public IReadOnlyList<ReviewCommit> Commits { get; init; } = [];

    [JsonPropertyName("commitsTruncated")]
    public bool CommitsTruncated { get; init; }

    /// <summary>The CLI's last message, capped; null when none was found.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>Another work (or a CLI of another terminal) was busy in the same folder during the round: its changes
    /// may be in this one's.</summary>
    [JsonPropertyName("overlapping")]
    public bool Overlapping { get; init; }

    /// <summary>Why there are no changes to show (<see cref="ReviewErrors"/>); null when the round was measured.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>
    /// The images the CLI saw during the round (doc 70: screenshots, image files it read), copied next to the review's
    /// record when the round ended; asked one by one (<c>review.capture.request</c>). Null when there is none.
    /// </summary>
    [JsonPropertyName("captures")]
    public IReadOnlyList<ReviewCapture>? Captures { get; init; }
}

/// <summary>An image the CLI saw during a round, kept on this machine with the review's photos.</summary>
public sealed record ReviewCapture
{
    /// <summary>Its file name in the review's folder: <c>r1-3.png</c> (round, rank).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>When the tool call that showed it was made.</summary>
    [JsonPropertyName("at")]
    public DateTime? At { get; init; }

    /// <summary>The CLI's name of that tool (<c>Read</c>, <c>mcp__playwright__browser_take_screenshot</c>, <c>ImageView</c>).</summary>
    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    /// <summary>What the call worked on: the file it read or saved, the page's URL.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

/// <summary>Answer to <c>review.capture.request</c>: one image of a round (<see cref="ReviewCapture"/>), as base64.</summary>
public class ReviewCaptureMessage
{
    [JsonPropertyName("type")]
    public string Type => "review.capture";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("reviewId")]
    public required Guid ReviewId { get; init; }

    [JsonPropertyName("image")]
    public required string Image { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    /// <summary><c>invalid</c>, <c>purged</c> (or never kept); null on success.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record ReviewFileChange
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>The previous path of a renamed or copied file.</summary>
    [JsonPropertyName("oldPath")]
    public string? OldPath { get; init; }

    /// <summary>A (added), M (modified), D (deleted), R (renamed), C (copied), T (type changed).</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Null for a binary file.</summary>
    [JsonPropertyName("insertions")]
    public int? Insertions { get; init; }

    [JsonPropertyName("deletions")]
    public int? Deletions { get; init; }

    [JsonPropertyName("binary")]
    public bool Binary { get; init; }
}

public class ReviewCommit
{
    [JsonPropertyName("sha")]
    public required string Sha { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }
}

/// <summary>Answer to <c>review.round-start</c>: the photo the new round starts from is taken.</summary>
public class ReviewRoundStartedMessage
{
    [JsonPropertyName("type")]
    public string Type => "review.round-started";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("reviewId")]
    public required Guid ReviewId { get; init; }

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    /// <summary>The round started; null when it could not be (see <see cref="Error"/>).</summary>
    [JsonPropertyName("round")]
    public int? Round { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Answer to <c>review.round-end</c>: the round a review asked from a terminal ended (doc 63), after its
/// <c>review.changes</c>.</summary>
public class ReviewRoundEndedMessage
{
    [JsonPropertyName("type")]
    public string Type => "review.round-ended";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("reviewId")]
    public required Guid ReviewId { get; init; }

    [JsonPropertyName("ptySessionId")]
    public required string PtySessionId { get; init; }

    /// <summary>The round reported; null when none was (see <see cref="Error"/>).</summary>
    [JsonPropertyName("round")]
    public int? Round { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Answer to <c>review.diff.request</c>: the diff of one round, read from the photos on this machine.</summary>
public class ReviewDiffMessage
{
    [JsonPropertyName("type")]
    public string Type => "review.diff";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("reviewId")]
    public required Guid ReviewId { get; init; }

    [JsonPropertyName("round")]
    public required int Round { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The unified diff; null when too large (<see cref="Truncated"/>) or on error.</summary>
    [JsonPropertyName("diff")]
    public string? Diff { get; init; }

    /// <summary>Too large to send whole: ask file by file (<see cref="Path"/>).</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary><see cref="ReviewDiffErrors"/>; null on success.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public static class ReviewKinds
{
    /// <summary>A run launched by the backend (workflow step, scheduler).</summary>
    public const string Run = "run";
    /// <summary>A task given to the agent (task queue, or injected in a terminal).</summary>
    public const string Task = "task";
    /// <summary>The work of a terminal, reviewed on request from its base (doc 63).</summary>
    public const string Session = "session";
}

public static class ReviewErrors
{
    /// <summary>The terminal's folder is not in a git work tree, or git is missing.</summary>
    public const string NotARepository = "not-a-repository";
    /// <summary>The photo of the start or of the end could not be taken (git failed, too slow).</summary>
    public const string SnapshotFailed = "snapshot-failed";
    /// <summary>The agent stopped during the round (its terminal died with it).</summary>
    public const string Interrupted = "interrupted";
    /// <summary>The agent could not follow the round (an error of its own, see its log).</summary>
    public const string Failed = "failed";
    /// <summary>A round asked from a terminal: nothing changed since its base (round-end).</summary>
    public const string NoChanges = "no-changes";
    /// <summary>A round asked from a terminal that has no base: no CLI started in it since the agent runs (round-end).</summary>
    public const string NoBase = "no-base";
}

public static class ReviewDiffErrors
{
    /// <summary>No such review on this machine (another agent's, or never started).</summary>
    public const string UnknownReview = "unknown-review";
    public const string UnknownRound = "unknown-round";
    /// <summary>The round is still going on: there is no end photo yet.</summary>
    public const string RoundOpen = "round-open";
    /// <summary>The photos are gone (purged, or the repository was cleaned).</summary>
    public const string Purged = "purged";
    public const string InvalidPath = "invalid-path";
    public const string GitFailed = "git-failed";
    /// <summary>The review's terminal is not known to this agent (round-start).</summary>
    public const string UnknownTerminal = "unknown-terminal";
    /// <summary>A round is already going on in this terminal (round-start).</summary>
    public const string RoundInProgress = "round-in-progress";
}

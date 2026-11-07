using System.Text.Json.Serialization;

namespace SideHub.Agent.Models;

/// <summary>
/// The commits of the agent's branch that are not pushed yet (see
/// <c>side_hub/doc/features/58-pousser-le-travail.md</c>): sent at connection, when it changes, after a task is done
/// and after a push. Only local git state, never fetched.
/// </summary>
public sealed record GitOutgoingMessage
{
    [JsonPropertyName("type")]
    public string Type => "git.outgoing";

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    /// <summary>The remote a push goes to: the branch's upstream remote, else <c>origin</c> or the only remote.</summary>
    [JsonPropertyName("remote")]
    public string? Remote { get; init; }

    /// <summary>The branch of <see cref="Remote"/> a push updates, short name (<c>main</c>).</summary>
    [JsonPropertyName("remoteBranch")]
    public string? RemoteBranch { get; init; }

    [JsonPropertyName("hasUpstream")]
    public bool HasUpstream { get; init; }

    [JsonPropertyName("head")]
    public string? Head { get; init; }

    [JsonPropertyName("ahead")]
    public int Ahead { get; init; }

    /// <summary>Commits of the upstream not in HEAD, as of the machine's last fetch.</summary>
    [JsonPropertyName("behind")]
    public int Behind { get; init; }

    /// <summary>Newest first, at most <see cref="Outgoing.OutgoingCommitsReader.MaxCommits"/>.</summary>
    [JsonPropertyName("commits")]
    public IReadOnlyList<OutgoingCommit> Commits { get; init; } = [];

    [JsonPropertyName("commitsTruncated")]
    public bool CommitsTruncated { get; init; }

    /// <summary>The queue's worktree the commits are in (doc 71), absolute path; null when they are in the agent's
    /// folder. A tab opened there (to integrate before a push) works on them.</summary>
    [JsonPropertyName("workTree")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkTree { get; init; }

    /// <summary><see cref="OutgoingErrors"/>; null when the state was read (<see cref="Ahead"/> may be 0).</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record OutgoingCommit
{
    [JsonPropertyName("sha")]
    public required string Sha { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }
}

/// <summary>Answer to <c>git.push.request</c>; a fresh <c>git.outgoing</c> follows.</summary>
public sealed class GitPushMessage
{
    [JsonPropertyName("type")]
    public string Type => "git.push";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    /// <summary><see cref="OutgoingErrors"/>; null when pushed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>The last lines git wrote, credentials of URLs masked, capped.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public static class OutgoingErrors
{
    public const string NotARepository = "not-a-repository";
    /// <summary>HEAD is not on a branch.</summary>
    public const string Detached = "detached";
    /// <summary>No upstream and no remote to push to.</summary>
    public const string NoRemote = "no-remote";
    public const string GitFailed = "git-failed";
    public const string NothingToPush = "nothing-to-push";
    /// <summary>HEAD is not the commit the push was confirmed for.</summary>
    public const string HeadMoved = "head-moved";
    /// <summary>The remote branch has commits HEAD does not have.</summary>
    public const string Rejected = "rejected";
    /// <summary>The remote refused the update (protected branch, server hook).</summary>
    public const string RemoteRejected = "remote-rejected";
    public const string Timeout = "timeout";
    /// <summary>Anything else: authentication, network…</summary>
    public const string Failed = "failed";
}

using System.Text.RegularExpressions;
using SideHub.Agent.Models;

namespace SideHub.Agent.Outgoing;

/// <summary>
/// The commits of the current branch that are not pushed yet, and the push itself (see
/// <c>side_hub/doc/features/58-pousser-le-travail.md</c>). Reading only runs local git commands, never a fetch:
/// "behind" is as of the machine's last fetch. A push is never forced and only sends the commit a person confirmed.
/// </summary>
public static partial class OutgoingCommitsReader
{
    /// <summary>Most commits listed; <see cref="GitOutgoingMessage.Ahead"/> still counts them all.</summary>
    public const int MaxCommits = 20;
    /// <summary>Longest <see cref="GitPushMessage.Detail"/>, in characters (the end of git's output is kept).</summary>
    public const int MaxDetailLength = 1_000;

    public static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    public static async Task<GitOutgoingMessage> ReadAsync(string directory, CancellationToken ct)
    {
        var inside = await RunAsync(directory, ["rev-parse", "--is-inside-work-tree"], ct);
        if (inside is null)
            return Failed(OutgoingErrors.GitFailed);
        if (!inside.Succeeded || inside.Output.Trim() != "true")
            return Failed(OutgoingErrors.NotARepository);

        var symbolic = await RunAsync(directory, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct);
        if (symbolic is null)
            return Failed(OutgoingErrors.GitFailed);
        if (!symbolic.Succeeded)
            return Failed(OutgoingErrors.Detached);
        var branch = symbolic.Output.Trim();

        // No commit yet on this branch: nothing to push.
        var headResult = await RunAsync(directory, ["rev-parse", "--verify", "--quiet", "HEAD"], ct);
        if (headResult is null)
            return Failed(OutgoingErrors.GitFailed, branch);
        if (!headResult.Succeeded)
            return new GitOutgoingMessage { Branch = branch };
        var head = headResult.Output.Trim();

        var upstream = await UpstreamAsync(directory, branch, ct);
        string remote;
        string remoteBranch;
        if (upstream is not null)
        {
            remote = upstream.Value.Remote;
            remoteBranch = upstream.Value.RemoteBranch;
            // Counted against the remote-tracking branch, when the machine has it (it may never have been fetched).
            var counts = await RunAsync(directory, ["rev-list", "--left-right", "--count", $"{upstream.Value.Ref}...HEAD"], ct);
            if (counts is { Succeeded: true }
                && counts.Output.Split('\t', StringSplitOptions.TrimEntries) is [var behindText, var aheadText]
                && int.TryParse(behindText, out var behind) && int.TryParse(aheadText, out var ahead))
            {
                var (commits, failed) = await CommitsAsync(directory, ["HEAD", "--not", upstream.Value.Ref], ct);
                if (failed)
                    return Failed(OutgoingErrors.GitFailed, branch);
                return Message(branch, remote, remoteBranch, hasUpstream: true, head, ahead, behind, commits);
            }
        }
        else
        {
            var remotes = await RunAsync(directory, ["remote"], ct);
            if (remotes is not { Succeeded: true })
                return Failed(OutgoingErrors.GitFailed, branch);
            var names = remotes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var chosen = names.Contains("origin") ? "origin" : names.Length == 1 ? names[0] : null;
            if (chosen is null)
                return Failed(OutgoingErrors.NoRemote, branch);
            remote = chosen;
            remoteBranch = branch;
        }

        // No usable upstream: what none of the remote's branches has yet.
        var count = await RunAsync(directory, ["rev-list", "--count", "HEAD", "--not", $"--remotes={remote}"], ct);
        if (count is not { Succeeded: true } || !int.TryParse(count.Output.Trim(), out var notOnRemote))
            return Failed(OutgoingErrors.GitFailed, branch);
        var (remoteCommits, logFailed) = await CommitsAsync(directory, ["HEAD", "--not", $"--remotes={remote}"], ct);
        if (logFailed)
            return Failed(OutgoingErrors.GitFailed, branch);
        return Message(branch, remote, remoteBranch, hasUpstream: upstream is not null, head, notOnRemote, 0, remoteCommits);
    }

    private static GitOutgoingMessage Message(
        string branch, string remote, string remoteBranch, bool hasUpstream, string head, int ahead, int behind,
        IReadOnlyList<OutgoingCommit> commits) => new()
    {
        Branch = branch,
        Remote = remote,
        RemoteBranch = remoteBranch,
        HasUpstream = hasUpstream,
        Head = head,
        Ahead = ahead,
        Behind = behind,
        Commits = commits.Take(MaxCommits).ToList(),
        CommitsTruncated = ahead > MaxCommits || commits.Count > MaxCommits,
    };

    private static GitOutgoingMessage Failed(string error, string? branch = null) =>
        new() { Branch = branch, Error = error };

    /// <summary>The branch's upstream on a remote (a local upstream, remote ".", does not count).</summary>
    internal static async Task<(string Ref, string Remote, string RemoteBranch)?> UpstreamAsync(string directory, string branch, CancellationToken ct)
    {
        // A pattern also matches the refs below it (refs/heads/feat matches refs/heads/feat/x): the line is picked by name.
        var refName = $"refs/heads/{branch}";
        var result = await RunAsync(directory,
            ["for-each-ref", "--format=%(refname)%00%(upstream)%00%(upstream:remotename)%00%(upstream:remoteref)", refName], ct);
        if (result is not { Succeeded: true })
            return null;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Split('\0') is not [var name, var upstream, var remote, var remoteRef] || name != refName)
                continue;
            if (upstream.Length == 0 || remote.Length == 0 || remote == ".")
                return null;
            var remoteBranch = remoteRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? remoteRef["refs/heads/".Length..] : branch;
            return (upstream, remote, remoteBranch);
        }
        return null;
    }

    /// <summary>The commits of <paramref name="range"/>, newest first, one more than listed (to know it was cut).</summary>
    private static async Task<(IReadOnlyList<OutgoingCommit> Commits, bool Failed)> CommitsAsync(
        string directory, IEnumerable<string> range, CancellationToken ct)
    {
        var log = await RunAsync(directory, ["log", "--no-color", "--format=%H%x09%s", "-n", (MaxCommits + 1).ToString(), .. range], ct);
        if (log is not { Succeeded: true })
            return ([], true);
        var commits = log.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts[0].Length > 0)
            .Select(parts => new OutgoingCommit { Sha = parts[0], Subject = parts.Length > 1 ? parts[1].TrimEnd('\r') : "" })
            .ToList();
        return (commits, false);
    }

    public sealed record PushResult(string? Error, string? Detail, string? Branch, string? Remote);

    /// <summary>
    /// Pushes <paramref name="expectedHead"/> to the branch's remote branch, only when it is still HEAD: what was
    /// confirmed is what is sent. Never forced. Sets the upstream when the branch had none.
    /// </summary>
    public static async Task<PushResult> PushAsync(string directory, string expectedHead, CancellationToken ct)
    {
        var state = await ReadAsync(directory, ct);
        if (state.Error is { } error)
            return new PushResult(error, null, state.Branch, state.Remote);
        if (state.Ahead == 0 || state.Head is null)
            return new PushResult(OutgoingErrors.NothingToPush, null, state.Branch, state.Remote);
        if (!string.Equals(state.Head, expectedHead?.Trim(), StringComparison.Ordinal))
            return new PushResult(OutgoingErrors.HeadMoved, null, state.Branch, state.Remote);

        // "--" before the remote: a remote name read from the config is never taken for an option.
        var push = await GitCommand.RunAsync(directory, PushTimeout,
            ["push", "--porcelain", "--", state.Remote!, $"{state.Head}:refs/heads/{state.RemoteBranch}"],
            keepSshEnvironment: true, ct);
        if (push is null)
            return new PushResult(OutgoingErrors.Timeout, null, state.Branch, state.Remote);
        if (!push.Succeeded)
        {
            var output = push.Output + "\n" + push.Error;
            var failure = output.Contains("[remote rejected]", StringComparison.Ordinal) ? OutgoingErrors.RemoteRejected
                : output.Contains("[rejected]", StringComparison.Ordinal) ? OutgoingErrors.Rejected
                : OutgoingErrors.Failed;
            return new PushResult(failure, Detail(output), state.Branch, state.Remote);
        }

        if (!state.HasUpstream)
            // Best effort: the push is done whether or not the branch now follows it.
            await RunAsync(directory, ["branch", $"--set-upstream-to={state.Remote}/{state.RemoteBranch}", state.Branch!], ct);
        return new PushResult(null, null, state.Branch, state.Remote);
    }

    /// <summary>The end of git's output, credentials of URLs masked (<c>https://***@host</c>).</summary>
    public static string? Detail(string output)
    {
        var text = MaskCredentials(output).Trim();
        if (text.Length == 0)
            return null;
        return text.Length <= MaxDetailLength ? text : text[^MaxDetailLength..];
    }

    public static string MaskCredentials(string text) => UrlCredentials().Replace(text, "***@");

    [GeneratedRegex(@"(?<=://)[^/\s@]+@")]
    private static partial Regex UrlCredentials();

    private static Task<GitCommand.Result?> RunAsync(string directory, IEnumerable<string> args, CancellationToken ct) =>
        GitCommand.RunAsync(directory, QueryTimeout, args, ct: ct);
}

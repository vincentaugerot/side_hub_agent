using System.Globalization;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// A branch of <c>origin</c> as the machine knows it (<c>refs/remotes/origin/&lt;branch&gt;</c>): fetched alone, and its
/// tip read. Shared by the question checkout and the report of the default branch's tip (side_hub doc 51).
/// </summary>
public static partial class OriginBranch
{
    /// <summary>
    /// Updates <c>origin/&lt;branch&gt;</c> only: no tag, no submodule, no FETCH_HEAD, no gc. Returns null when fetched,
    /// else why not (offline, slow remote, no credentials), first line only and without credentials.
    /// </summary>
    /// <param name="directory">A folder of the repository (the refs are shared by all its worktrees).</param>
    public static async Task<string?> FetchAsync(string directory, string branch, TimeSpan timeout, CancellationToken ct)
    {
        var result = await GitCommand.RunAsync(directory, timeout,
            ["-c", "gc.auto=0", "-c", "maintenance.auto=false",
             "fetch", "--quiet", "--no-tags", "--no-recurse-submodules", "--no-write-fetch-head",
             "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"],
            keepSshEnvironment: true, ct);
        return result is { Succeeded: true } ? null : FirstLine(result?.Error) ?? "timed out";
    }

    /// <summary>The commit <c>origin/&lt;branch&gt;</c> is at and the number of commits it holds; null when the
    /// machine has no such branch.</summary>
    public static async Task<(string Sha, long CommitCount)?> ReadTipAsync(string directory, string branch, CancellationToken ct)
    {
        var tip = $"refs/remotes/origin/{branch}";
        if (await GitCommand.RunAsync(directory, GitCommand.DefaultTimeout, ["rev-parse", "--verify", "--quiet", tip + "^{commit}"], ct: ct)
            is not { Succeeded: true } parsed)
            return null;
        var count = await CountAsync(directory, tip, ct);
        return count is { } commits ? (parsed.Output.Trim(), commits) : null;
    }

    /// <summary>The number of commits reachable from <paramref name="revision"/>; null when git could not tell.</summary>
    public static async Task<long?> CountAsync(string directory, string revision, CancellationToken ct) =>
        await GitCommand.RunAsync(directory, TimeSpan.FromSeconds(20), ["rev-list", "--count", revision], ct: ct)
            is { Succeeded: true } counted
        && long.TryParse(counted.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;

    /// <summary>The first line of git's error, without the credentials a remote URL may carry.</summary>
    public static string? FirstLine(string? error) =>
        error?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } line
            ? UrlCredentials().Replace(line, "://***@")
            : null;

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex UrlCredentials();
}

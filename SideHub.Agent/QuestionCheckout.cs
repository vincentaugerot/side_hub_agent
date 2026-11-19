using System.Globalization;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>The checkout of a question or peer review run, ready: its real path and the commit it is at.</summary>
/// <param name="CommitCount">Commits reachable from <paramref name="CommitSha"/>, null when not read: it lets the backend
/// tell how many commits the default branch gained since (the repository card's refresh, side_hub doc 51).</param>
public sealed record PreparedCheckout(string Path, string CommitSha, DateTimeOffset CommitDate, long? CommitCount = null);

/// <summary>
/// Where question runs (a read-only CLI answering a question about the repository) read the code: a detached git
/// worktree of the agent's repository at <c>~/.sidehub/qa/&lt;agentId&gt;/checkout</c>, moved to the tip of
/// <c>origin/&lt;branch&gt;</c> before each run. The answer must describe what was pushed, not the developer's
/// uncommitted work, and the run must not touch that work: in the developer's repository only
/// <c>refs/remotes/origin/*</c> (fetch) and the worktree's own metadata (<c>.git/worktrees/</c>) change.
/// </summary>
public sealed partial class QuestionCheckout
{
    public const string RunKindKey = "SIDEHUB_RUN_KIND";
    public const string QuestionKind = "question";
    public const string BaseBranchKey = "SIDEHUB_BASE_BRANCH";
    public const string CommitKey = "SIDEHUB_QUESTION_COMMIT";

    // The backend waits 60 s for pty.started: a slow remote must not use it all, the last fetched state will do.
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(20);

    private readonly string _agentKey;
    private readonly string _agentDirectory;
    private readonly Action<string> _log;
    // One preparation at a time per agent: they all move the same checkout.
    private readonly SemaphoreSlim _lock = new(1, 1);
    // PTYs of question runs still reading the checkout: it must not move under them. Guarded by itself.
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public string CheckoutPath { get; }

    /// <param name="agentKey">The agent's id: each agent of a repository has its own checkout.</param>
    /// <param name="baseDirectory">Folder of the agents' checkouts; <c>~/.sidehub/qa</c> by default.</param>
    public QuestionCheckout(string agentKey, Action<string> log, string? baseDirectory = null)
    {
        _agentKey = agentKey;
        _log = log;
        baseDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sidehub", "qa");
        _agentDirectory = Path.Combine(baseDirectory, agentKey);
        CheckoutPath = Path.Combine(_agentDirectory, "checkout");
    }

    /// <summary>
    /// Brings the checkout to the tip of <c>origin/<paramref name="branch"/></c> (fetched first, the last known
    /// tip when the fetch fails) with no file outside of it, and marks it in use by <paramref name="ptySessionId"/>
    /// until <see cref="Release"/>. While another question run is still in it, it stays where it is. Null (logged)
    /// when it cannot be prepared.
    /// </summary>
    /// <param name="repositoryDirectory">The agent's working directory, in the developer's repository.</param>
    public async Task<PreparedCheckout?> PrepareAsync(
        string repositoryDirectory, string branch, string ptySessionId, CancellationToken ct)
    {
        // The branch comes from the backend and ends up in git arguments.
        if (!IsValidBranch(branch))
            return Fail("invalid branch name");
        if (!IsSafeSegment(_agentKey))
            return Fail("invalid agent id");

        try { await _lock.WaitAsync(ct); }
        catch (OperationCanceledException) { return Fail("canceled"); }
        try
        {
            if (await GitAsync(repositoryDirectory, LocalTimeout, ct, "rev-parse", "--path-format=absolute", "--git-common-dir")
                is not { Succeeded: true } common)
                return Fail($"{repositoryDirectory} is not in a git repository");
            var commonDirectory = common.Output.Trim();

            bool inUse;
            lock (_active)
                inUse = _active.Any(id => id != ptySessionId);
            if (inUse)
            {
                _log($"Question checkout in use by another run: {ptySessionId} reads it as it is");
            }
            else
            {
                if (!await IsWorktreeOfAsync(commonDirectory, ct) && !await CreateAsync(repositoryDirectory, ct))
                    return Fail($"could not create the worktree {CheckoutPath}");
                await FetchAsync(branch, ct);
                var tip = $"refs/remotes/origin/{branch}";
                if (await GitAsync(CheckoutPath, LocalTimeout, ct,
                        "-c", "advice.detachedHead=false", "checkout", "--quiet", "--detach", "--force", tip)
                    is not { Succeeded: true })
                    return Fail($"could not check out origin/{branch}");
                // -ff: nested repositories (an untracked clone) go too.
                if (await GitAsync(CheckoutPath, LocalTimeout, ct, "clean", "-ffdxq") is not { Succeeded: true })
                    return Fail("could not clean the checkout");
            }

            if (await GitAsync(CheckoutPath, LocalTimeout, ct, "log", "-1", "--format=%H%n%cI") is not { Succeeded: true } head
                || head.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is not [var sha, var date]
                || !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var commitDate))
                return Fail("could not read the checkout's commit");

            // The real path: the CLI records it as its cwd, and the run's transcript is matched against it.
            var prepared = new PreparedCheckout(PathConfinement.RealPath(CheckoutPath), sha, commitDate,
                await OriginBranch.CountAsync(CheckoutPath, sha, ct));
            lock (_active)
                _active.Add(ptySessionId);
            return prepared;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The question run of <paramref name="ptySessionId"/> is over: the checkout may move again.</summary>
    public void Release(string ptySessionId)
    {
        lock (_active)
            _active.Remove(ptySessionId);
    }

    /// <summary>
    /// A branch name as git accepts it (<c>git check-ref-format --branch</c>), restricted further to plain ASCII
    /// names: no leading '-' (an option), no "..", no empty, '.'-led or ".lock" component, no trailing '.' or '/'.
    /// </summary>
    public static bool IsValidBranch(string? branch) =>
        !string.IsNullOrEmpty(branch)
        && branch.Length <= 255
        && BranchCharacters().IsMatch(branch)
        && !branch.StartsWith('-')
        && !branch.EndsWith('.')
        && !branch.Contains("..", StringComparison.Ordinal)
        && branch.Split('/').All(c => c.Length > 0 && !c.StartsWith('.') && !c.EndsWith(".lock", StringComparison.Ordinal));

    [GeneratedRegex(@"\A[A-Za-z0-9._/-]+\z")]
    private static partial Regex BranchCharacters();

    private static bool IsSafeSegment(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains('/') && !name.Contains('\\');

    /// <summary>Whether the checkout folder is a worktree of the developer's repository (it may have been deleted,
    /// or be left from a repository the agent no longer runs in).</summary>
    private async Task<bool> IsWorktreeOfAsync(string commonDirectory, CancellationToken ct)
    {
        // Without its .git file, git would look for a repository in the folders above the checkout.
        if (!File.Exists(Path.Combine(CheckoutPath, ".git")))
            return false;
        return await GitAsync(CheckoutPath, LocalTimeout, ct, "rev-parse", "--path-format=absolute", "--git-common-dir")
            is { Succeeded: true } result
            && SamePath(result.Output.Trim(), commonDirectory);
    }

    private async Task<bool> CreateAsync(string repositoryDirectory, CancellationToken ct)
    {
        // Agent-owned and private, with no link on the way, so the checkout cannot be redirected.
        PrivateFiles.CreateDirectory(_agentDirectory);
        if (Directory.Exists(CheckoutPath))
            Directory.Delete(CheckoutPath, recursive: true);
        else if (File.Exists(CheckoutPath))
            File.Delete(CheckoutPath);

        // --force reuses the registration of a checkout deleted by hand. Not "worktree prune": it would also drop the
        // developer's own worktrees that are only missing for now (an unmounted drive). --no-checkout: the files come
        // with the checkout of origin/<branch> that follows, no need to write those of the developer's HEAD first.
        var result = await GitAsync(repositoryDirectory, LocalTimeout, ct,
            "worktree", "add", "--force", "--detach", "--no-checkout", CheckoutPath);
        if (result is { Succeeded: true })
        {
            _log($"Question checkout created at {CheckoutPath}");
            return true;
        }
        _log($"git worktree add failed: {FirstLine(result?.Error) ?? "timed out"}");
        return false;
    }

    /// <summary>Updates <c>origin/&lt;branch&gt;</c> only. A failure (offline, slow remote) is logged and the last
    /// fetched tip is used.</summary>
    private async Task FetchAsync(string branch, CancellationToken ct)
    {
        if (await OriginBranch.FetchAsync(CheckoutPath, branch, FetchTimeout, ct) is { } error)
            _log($"git fetch origin {branch} failed ({error}); using the last fetched origin/{branch}");
    }

    private static Task<GitCommand.Result?> GitAsync(string directory, TimeSpan timeout, CancellationToken ct, params string[] args) =>
        GitCommand.RunAsync(directory, timeout, args, ct: ct);

    // Links resolved: the project may be reached through a symbolic link, git does not always say it the same way.
    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(PathConfinement.RealPath(a)), Path.TrimEndingDirectorySeparator(PathConfinement.RealPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? FirstLine(string? error) => OriginBranch.FirstLine(error);

    private PreparedCheckout? Fail(string reason)
    {
        _log($"Question checkout not prepared: {reason}");
        return null;
    }
}

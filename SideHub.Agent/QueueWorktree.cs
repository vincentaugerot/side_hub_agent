using System.Text.Json;
using SideHub.Agent.Outgoing;

namespace SideHub.Agent;

/// <summary>The queue's worktree, ready for a task: its path, its branch, and the setup command to run in the task's
/// terminal before the CLI (null when there is none, or when it already succeeded there).</summary>
public sealed record PreparedWorktree(string Path, string Branch, string? Setup);

/// <summary>
/// Where the tasks of the agent's queue run (doc 71): a git worktree of the agent's repository at
/// <c>~/.sidehub/worktrees/&lt;agentId&gt;</c>, on its own branch <c>sidehub/queue-&lt;id&gt;</c> that follows the
/// upstream of the branch checked out in the repository's folder (usually <c>origin/main</c>), so that its push lands
/// there. Kept from one task to the next: dependencies are installed once (<see cref="ConfigFile"/>). The tabs opened by
/// hand stay in the repository's folder, so the queue's work never mixes with the user's, nor in its reviews.
/// <para>Before each task the branch is fetched and fast-forwarded, never rebased nor merged: commits already reviewed
/// keep their hashes (doc 58 links them to their review). A branch that diverged (unpushed work while the remote moved
/// on) stays where it is; "Intégrer et pousser" integrates it.</para>
/// </summary>
public sealed class QueueWorktree
{
    /// <summary>In a pty.start's environment: the terminal runs in the queue's worktree (<see cref="QueuePlace"/>).</summary>
    public const string PlaceVariable = "SIDEHUB_WORKTREE";
    public const string QueuePlace = "queue";
    /// <summary>The setup command and the file its success is written to, for the line typed before the CLI.</summary>
    public const string SetupVariable = "SIDEHUB_WORKTREE_SETUP";
    public const string SetupMarkerVariable = "SIDEHUB_WORKTREE_SETUP_MARKER";
    /// <summary>The repository's folder, for a setup that copies or links files from it (a <c>.env</c>).</summary>
    public const string RepositoryVariable = "SIDEHUB_REPOSITORY_ROOT";
    /// <summary><c>&lt;repository&gt;/.sidehub/worktree.json</c>: <c>{ "setup": "&lt;shell command&gt;" }</c>, local to the
    /// machine like the agent's own config.</summary>
    public const string ConfigFile = "worktree.json";

    // The backend waits 10 s for a task's pty.started: creating the worktree takes about a second, the fetch must not
    // use the rest (the last fetched state will do).
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(4);

    private readonly Action<string> _log;
    // One preparation at a time: they all move the same worktree.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Path { get; }
    public string Branch { get; }
    private string SetupMarker => Path + ".setup";

    /// <param name="agentKey">The agent's id: each agent of a repository has its own worktree and branch.</param>
    /// <param name="baseDirectory">Folder of the agents' worktrees; <c>~/.sidehub/worktrees</c> by default.</param>
    public QueueWorktree(string agentKey, Action<string> log, string? baseDirectory = null)
    {
        _log = log;
        baseDirectory ??= System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sidehub", "worktrees");
        Path = System.IO.Path.Combine(baseDirectory, agentKey);
        var id = new string(agentKey.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
        Branch = $"sidehub/queue-{(id.Length > 0 ? id : "agent")}";
    }

    /// <summary>Whether the worktree was created (its <c>.git</c> file is there).</summary>
    public bool Exists => File.Exists(System.IO.Path.Combine(Path, ".git"));

    /// <summary>
    /// Creates the worktree from <paramref name="repositoryDirectory"/> the first time, then brings it up to date.
    /// Null when it cannot be used (not a git repository, git failing, the folder taken by something else): the task then
    /// runs in the repository's folder, as before.
    /// </summary>
    public async Task<PreparedWorktree?> PrepareAsync(string repositoryDirectory, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (await GitRepository.OpenAsync(repositoryDirectory) is not { } repository)
                return Fail("the agent's folder is not a git repository");
            var top = repository.TopLevel;
            var folderBranch = await Git(top, LocalTimeout, ct, "symbolic-ref", "--quiet", "--short", "HEAD")
                is { Succeeded: true } symbolic && symbolic.Output.Trim() is { Length: > 0 } name ? name : null;
            var upstream = folderBranch is null ? null : await OutgoingCommitsReader.UpstreamAsync(top, folderBranch, ct);

            if (!Exists && await CreateAsync(top, folderBranch, upstream, ct) is { } failure)
                return Fail(failure);

            // Never rewrites: a diverged branch, or local changes in the way, leave it where it is.
            if (upstream is { } remote)
            {
                var fetched = await Git(Path, FetchTimeout, ct, keepSsh: true, "fetch", "--quiet", "--no-tags", remote.Remote, remote.RemoteBranch);
                if (fetched is not { Succeeded: true })
                    _log($"Queue worktree {Path}: fetch of {remote.Remote}/{remote.RemoteBranch} failed ({FirstLine(fetched?.Error) ?? "timed out"}), the last fetched state is used");
            }
            var target = upstream?.Ref ?? (folderBranch is null ? null : $"refs/heads/{folderBranch}");
            if (target is not null)
            {
                var merged = await Git(Path, LocalTimeout, ct, "merge", "--ff-only", "--quiet", target);
                if (merged is not { Succeeded: true })
                    _log($"Queue worktree {Path} stays where it is: no fast-forward to {target} ({FirstLine(merged?.Error) ?? "timed out"})");
            }

            await LinkClaudeLocalSettingsAsync(top, ct);
            return new PreparedWorktree(Path, Branch, PendingSetup(top));
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

    /// <summary>The worktree on its branch, made from the upstream of the folder's branch (else the folder's branch);
    /// null when done, else why not.</summary>
    private async Task<string?> CreateAsync(
        string top, string? folderBranch, (string Ref, string Remote, string RemoteBranch)? upstream, CancellationToken ct)
    {
        if (Directory.Exists(Path) && Directory.EnumerateFileSystemEntries(Path).Any())
            return $"{Path} exists and is not a worktree";
        if (System.IO.Path.GetDirectoryName(Path) is { } parent)
            PrivateFiles.CreateDirectory(parent);

        var hasBranch = await Git(top, LocalTimeout, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{Branch}")
            is { Succeeded: true };
        // --force reuses the registration of a worktree deleted by hand. Never "worktree prune": it would also drop the
        // developer's own worktrees that are only missing for now. But never for a branch checked out elsewhere.
        if (hasBranch && await CheckedOutElsewhereAsync(top, ct) is { } elsewhere)
            return $"its branch {Branch} is checked out in {elsewhere}";
        var add = hasBranch
            ? await Git(top, LocalTimeout * 3, ct, "worktree", "add", "--quiet", "--force", Path, Branch)
            : await Git(top, LocalTimeout * 3, ct, "worktree", "add", "--quiet", "--force", "-b", Branch, Path,
                upstream?.Ref ?? folderBranch ?? "HEAD");
        if (add is not { Succeeded: true })
            return $"git worktree add failed ({FirstLine(add?.Error) ?? "timed out"})";
        if (upstream is { } remote)
        {
            var tracked = await Git(Path, LocalTimeout, ct, "branch", $"--set-upstream-to={remote.Remote}/{remote.RemoteBranch}", Branch);
            if (tracked is not { Succeeded: true })
                _log($"Queue worktree {Path}: its branch does not follow {remote.Remote}/{remote.RemoteBranch} ({FirstLine(tracked?.Error) ?? "timed out"})");
        }
        _log($"Queue worktree created at {Path} on {Branch}{(upstream is { } u ? $", following {u.Remote}/{u.RemoteBranch}" : "")}");
        return null;
    }

    /// <summary>
    /// claude keeps the project's own permissions in <c>.claude/settings.local.json</c>, never versioned: the worktree
    /// links to the folder's, so the queue's claude asks no more than in the folder (and an "always allow" given in either
    /// counts for both). Only when git ignores it there, so that it never shows in a review.
    /// </summary>
    private async Task LinkClaudeLocalSettingsAsync(string top, CancellationToken ct)
    {
        const string relative = ".claude/settings.local.json";
        var source = System.IO.Path.Combine(top, ".claude", "settings.local.json");
        var target = System.IO.Path.Combine(Path, ".claude", "settings.local.json");
        if (!File.Exists(source) || File.Exists(target) || new FileInfo(target).LinkTarget is not null)
            return;
        if (await Git(Path, LocalTimeout, ct, "check-ignore", "--quiet", relative) is not { ExitCode: 0 })
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".claude"));
            File.CreateSymbolicLink(target, source);
            _log($"Queue worktree {Path}: {relative} links to the folder's");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Queue worktree {Path}: {relative} not linked ({ex.Message}), claude may ask more there");
        }
    }

    /// <summary>The worktree, other than ours, where <see cref="Branch"/> is checked out; null when none.</summary>
    private async Task<string?> CheckedOutElsewhereAsync(string top, CancellationToken ct)
    {
        if (await Git(top, LocalTimeout, ct, "worktree", "list", "--porcelain") is not { Succeeded: true } list)
            return null;
        string? worktree = null;
        foreach (var line in list.Output.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                worktree = line["worktree ".Length..];
            else if (line == $"branch refs/heads/{Branch}" && worktree is not null
                     && !string.Equals(System.IO.Path.GetFullPath(worktree), System.IO.Path.GetFullPath(Path), StringComparison.Ordinal))
                return worktree;
        }
        return null;
    }

    /// <summary>The setup of <see cref="ConfigFile"/>, unless it already succeeded in the worktree as it is written.</summary>
    private string? PendingSetup(string top)
    {
        var config = System.IO.Path.Combine(top, ".sidehub", ConfigFile);
        if (!File.Exists(config))
            return null;
        string? setup;
        try
        {
            setup = JsonDocument.Parse(File.ReadAllText(config)).RootElement is { ValueKind: JsonValueKind.Object } root
                    && root.TryGetProperty("setup", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;
        }
        catch (JsonException ex)
        {
            _log($"Queue worktree setup ignored: {config} is not valid JSON ({ex.Message})");
            return null;
        }
        if (string.IsNullOrEmpty(setup))
            return null;
        return File.Exists(SetupMarker) && File.ReadAllText(SetupMarker) == setup ? null : setup;
    }

    /// <summary>
    /// The line typed in the task's terminal before the CLI, for a POSIX shell (null for another: the setup is then left
    /// to the user). The command comes from the environment (<see cref="SetupVariable"/>), so it is never quoted here;
    /// its success is written to <see cref="SetupMarkerVariable"/>, and a failure is said without stopping the task.
    /// </summary>
    public static string? SetupLine(string shell) =>
        shell is "bash" or "zsh" or "sh" or "dash" or "ksh"
            ? $"sh -c \"${SetupVariable}\" && printf '%s' \"${SetupVariable}\" > \"${SetupMarkerVariable}\""
              + " || echo 'SideHub: the setup of the queue worktree failed (see .sidehub/worktree.json), the task goes on.'\r"
            : null;

    /// <summary>The environment of a task's terminal in the worktree.</summary>
    public IReadOnlyDictionary<string, string> TerminalEnvironment(string repositoryDirectory, string? setup)
    {
        var environment = new Dictionary<string, string> { [RepositoryVariable] = repositoryDirectory };
        if (setup is not null)
        {
            environment[SetupVariable] = setup;
            environment[SetupMarkerVariable] = SetupMarker;
        }
        return environment;
    }

    private PreparedWorktree? Fail(string reason)
    {
        _log($"Queue worktree not used, the task runs in the agent's folder: {reason}");
        return null;
    }

    private static Task<GitCommand.Result?> Git(string directory, TimeSpan timeout, CancellationToken ct, params string[] args) =>
        GitCommand.RunAsync(directory, timeout, args, ct: ct);

    private static Task<GitCommand.Result?> Git(string directory, TimeSpan timeout, CancellationToken ct, bool keepSsh, params string[] args) =>
        GitCommand.RunAsync(directory, timeout, args, keepSshEnvironment: keepSsh, ct: ct);

    private static string? FirstLine(string? error) =>
        error?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}

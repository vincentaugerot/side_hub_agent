using System.Diagnostics;
using SideHub.Agent.Review;

namespace SideHub.Agent.Tests;

/// <summary>The tasks of the agent's queue run in a worktree of their own, kept from one task to the next (doc 71).</summary>
public sealed class QueueWorktreeTests : IDisposable
{
    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sidehub-queue-worktree-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Remote => Path.Combine(_root.FullName, "remote.git");
    private string Work => Path.Combine(_root.FullName, "work");
    private string Other => Path.Combine(_root.FullName, "other");
    private string Worktrees => Path.Combine(_root.FullName, "worktrees");

    private QueueWorktree NewWorktree(List<string>? log = null) =>
        new("4a3a6adc-6054-4cf5-9d05-e9569345bb17", line => log?.Add(line), Worktrees);

    /// <summary>A bare remote with one commit on main, cloned into <see cref="Work"/> (main follows origin/main).</summary>
    private void CloneWithRemote()
    {
        Git(_root.FullName, "init", "-q", "--bare", "-b", "main", Remote);
        Git(_root.FullName, "clone", "-q", Remote, Work);
        Git(Work, "checkout", "-q", "-b", "main");
        Commit(Work, "readme.txt", "initial");
        Git(Work, "push", "-q", "-u", "origin", "main");
    }

    private void PushFromElsewhere(string file, string message)
    {
        if (!Directory.Exists(Other))
            Git(_root.FullName, "clone", "-q", Remote, Other);
        Git(Other, "pull", "-q", "--ff-only");
        Commit(Other, file, message);
        Git(Other, "push", "-q", "origin", "main");
    }

    [Fact]
    public async Task The_first_task_creates_the_worktree_on_its_branch_following_the_folders_upstream()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        var worktree = NewWorktree();

        var prepared = await worktree.PrepareAsync(Work, CancellationToken.None);

        Assert.NotNull(prepared);
        Assert.Equal(worktree.Path, prepared.Path);
        Assert.Equal("sidehub/queue-4a3a6adc", prepared.Branch);
        Assert.True(worktree.Exists);
        Assert.Equal("sidehub/queue-4a3a6adc", Git(worktree.Path, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal("origin/main", Git(worktree.Path, "rev-parse", "--abbrev-ref", "@{u}"));
        Assert.Equal(Git(Work, "rev-parse", "HEAD"), Git(worktree.Path, "rev-parse", "HEAD"));
        // The user's folder is left as it was.
        Assert.Equal("main", Git(Work, "symbolic-ref", "--short", "HEAD"));
        Assert.Null(prepared.Setup);
    }

    [Fact]
    public async Task Before_each_task_the_worktree_catches_up_with_the_remote()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        var worktree = NewWorktree();
        await worktree.PrepareAsync(Work, CancellationToken.None);
        PushFromElsewhere("other.txt", "pushed meanwhile");

        await worktree.PrepareAsync(Work, CancellationToken.None);

        Assert.Equal(Git(Other, "rev-parse", "HEAD"), Git(worktree.Path, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Unpushed_work_is_never_rewritten_when_the_remote_moved_on()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        var log = new List<string>();
        var worktree = NewWorktree(log);
        await worktree.PrepareAsync(Work, CancellationToken.None);
        Commit(worktree.Path, "task.txt", "reviewed task");
        var reviewed = Git(worktree.Path, "rev-parse", "HEAD");
        PushFromElsewhere("other.txt", "pushed meanwhile");

        var prepared = await worktree.PrepareAsync(Work, CancellationToken.None);

        Assert.NotNull(prepared);
        Assert.Equal(reviewed, Git(worktree.Path, "rev-parse", "HEAD"));
        Assert.Contains(log, line => line.Contains("stays where it is"));
    }

    [Fact]
    public async Task Work_already_pushed_is_kept_and_the_worktree_goes_on_from_it()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        var worktree = NewWorktree();
        await worktree.PrepareAsync(Work, CancellationToken.None);
        Commit(worktree.Path, "task.txt", "first task");
        Git(worktree.Path, "push", "-q", "origin", "HEAD:refs/heads/main");
        PushFromElsewhere("other.txt", "pushed after it");

        await worktree.PrepareAsync(Work, CancellationToken.None);

        Assert.Equal(Git(Other, "rev-parse", "HEAD"), Git(worktree.Path, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task A_worktree_removed_by_hand_is_created_again_on_its_branch()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        var worktree = NewWorktree();
        await worktree.PrepareAsync(Work, CancellationToken.None);
        Commit(worktree.Path, "task.txt", "kept on the branch");
        var head = Git(worktree.Path, "rev-parse", "HEAD");
        Directory.Delete(worktree.Path, recursive: true);

        var prepared = await worktree.PrepareAsync(Work, CancellationToken.None);

        Assert.NotNull(prepared);
        Assert.Equal(head, Git(worktree.Path, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Its_branch_checked_out_elsewhere_leaves_the_task_in_the_folder()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        var worktree = NewWorktree();
        await worktree.PrepareAsync(Work, CancellationToken.None);
        Git(Work, "worktree", "remove", "--force", worktree.Path);
        Git(Work, "checkout", "-q", "sidehub/queue-4a3a6adc");

        Assert.Null(await worktree.PrepareAsync(Work, CancellationToken.None));
        Assert.False(worktree.Exists);
    }

    [Fact]
    public async Task Outside_git_the_task_stays_in_the_folder()
    {
        if (!GitAvailable)
            return;
        Directory.CreateDirectory(Work);
        var worktree = NewWorktree();

        Assert.Null(await worktree.PrepareAsync(Work, CancellationToken.None));
    }

    [Fact]
    public async Task A_setup_is_run_until_it_succeeded_as_written()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        Directory.CreateDirectory(Path.Combine(Work, ".sidehub"));
        File.WriteAllText(Path.Combine(Work, ".sidehub", QueueWorktree.ConfigFile), """{ "setup": "npm ci" }""");
        var worktree = NewWorktree();

        var first = await worktree.PrepareAsync(Work, CancellationToken.None);
        Assert.Equal("npm ci", first!.Setup);
        var environment = worktree.TerminalEnvironment(Work, first.Setup);
        Assert.Equal("npm ci", environment[QueueWorktree.SetupVariable]);
        Assert.Equal(Work, environment[QueueWorktree.RepositoryVariable]);

        // What the typed line writes once the setup succeeded.
        File.WriteAllText(environment[QueueWorktree.SetupMarkerVariable], "npm ci");
        Assert.Null((await worktree.PrepareAsync(Work, CancellationToken.None))!.Setup);

        File.WriteAllText(Path.Combine(Work, ".sidehub", QueueWorktree.ConfigFile), """{ "setup": "npm ci && npm run build" }""");
        Assert.Equal("npm ci && npm run build", (await worktree.PrepareAsync(Work, CancellationToken.None))!.Setup);
    }

    [Fact]
    public async Task The_queue_s_claude_gets_the_folder_s_own_permissions_when_git_ignores_them()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        File.WriteAllText(Path.Combine(Work, ".gitignore"), ".claude/\n");
        Git(Work, "add", ".gitignore");
        Git(Work, "commit", "-q", "-m", "ignore claude");
        Git(Work, "push", "-q", "origin", "main");
        Directory.CreateDirectory(Path.Combine(Work, ".claude"));
        File.WriteAllText(Path.Combine(Work, ".claude", "settings.local.json"), """{ "permissions": { "allow": ["Bash(npm run build)"] } }""");
        var worktree = NewWorktree();

        await worktree.PrepareAsync(Work, CancellationToken.None);

        var linked = new FileInfo(Path.Combine(worktree.Path, ".claude", "settings.local.json"));
        Assert.Equal(Path.Combine(Work, ".claude", "settings.local.json"), linked.LinkTarget);
        Assert.Equal("", Git(worktree.Path, "status", "--porcelain"));
    }

    [Fact]
    public async Task Settings_git_would_show_are_not_linked()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        // Whatever the machine's own ignore file says (claude adds its settings to ~/.config/git/ignore).
        Git(Work, "config", "core.excludesFile", Path.Combine(_root.FullName, "no-ignore"));
        Directory.CreateDirectory(Path.Combine(Work, ".claude"));
        File.WriteAllText(Path.Combine(Work, ".claude", "settings.local.json"), "{}");
        var worktree = NewWorktree();

        await worktree.PrepareAsync(Work, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(worktree.Path, ".claude", "settings.local.json")));
    }

    [Theory]
    [InlineData("bash", true)]
    [InlineData("zsh", true)]
    [InlineData("cmd", false)]
    [InlineData("pwsh", false)]
    public void The_setup_line_is_typed_for_a_posix_shell_only(string shell, bool typed)
    {
        var line = QueueWorktree.SetupLine(shell);

        Assert.Equal(typed, line is not null);
        if (line is not null)
        {
            Assert.EndsWith("\r", line);
            Assert.Contains("$" + QueueWorktree.SetupVariable, line);
            Assert.Contains("$" + QueueWorktree.SetupMarkerVariable, line);
        }
    }

    [Fact]
    public void A_round_in_the_queue_worktree_does_not_overlap_one_in_the_folder()
    {
        var activity = new WorkReviewActivity(Path.Combine(_root.FullName, "activity.jsonl"));
        var at = DateTimeOffset.UtcNow;
        var inFolder = Guid.NewGuid();
        var inWorktree = Guid.NewGuid();
        var olderAgent = Guid.NewGuid();
        activity.Append(new(inFolder, 1, "agent2", WorkReviewActivity.Start, at, "/srv/repo"));

        Assert.False(activity.Overlaps(inWorktree, 1, at.AddMinutes(-1), at.AddMinutes(5), "/home/u/.sidehub/worktrees/agent1"));
        Assert.True(activity.Overlaps(inWorktree, 1, at.AddMinutes(-1), at.AddMinutes(5), "/srv/repo/"));

        // An older agent's entry says no folder: it counts for every one.
        activity.Append(new(olderAgent, 1, "agent3", WorkReviewActivity.Start, at));
        Assert.True(activity.Overlaps(inWorktree, 1, at.AddMinutes(-1), at.AddMinutes(5), "/home/u/.sidehub/worktrees/agent1"));
    }

    private static void Commit(string repository, string file, string message)
    {
        File.WriteAllText(Path.Combine(repository, file), message);
        Git(repository, "add", file);
        Git(repository, "commit", "-q", "-m", message);
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(ExecutableResolver.Resolve("git")!)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Commits need an identity; the machine's may be unset.
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "Test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output.Trim();
    }
}

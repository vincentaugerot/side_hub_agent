using System.Diagnostics;

namespace SideHub.Agent.Tests;

/// <summary>
/// A real git setup: a bare repository as origin, a "pusher" clone that moves origin/main, and the developer's clone
/// the agent runs in, with work in progress everywhere (local branch, staged, modified and untracked files, a stash
/// entry) that preparing the question checkout must leave exactly as it was.
/// </summary>
public sealed class QuestionCheckoutTests : IDisposable
{
    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    private readonly string _root = Directory.CreateTempSubdirectory("sidehub-qa-").FullName;
    private readonly List<string> _logs = [];
    private string Origin => Path.Combine(_root, "origin.git");
    private string Pusher => Path.Combine(_root, "pusher");
    private string Developer => Path.Combine(_root, "dev");

    public QuestionCheckoutTests()
    {
        if (!GitAvailable)
            return;

        Git(_root, "init", "-q", "--bare", "-b", "main", Origin);
        Git(_root, "clone", "-q", Origin, Pusher);
        Commit(Pusher, "README.md", "v1");
        Git(Pusher, "push", "-q", "origin", "HEAD:main");

        Git(_root, "clone", "-q", Origin, Developer);
        Git(Developer, "checkout", "-q", "-b", "feature");
        Commit(Developer, "feature.txt", "local only");
        File.WriteAllText(Path.Combine(Developer, "README.md"), "stashed");
        Git(Developer, "stash", "-q");
        File.WriteAllText(Path.Combine(Developer, "README.md"), "uncommitted");
        File.WriteAllText(Path.Combine(Developer, "staged.txt"), "staged");
        Git(Developer, "add", "staged.txt");
        File.WriteAllText(Path.Combine(Developer, "untracked.txt"), "untracked");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private QuestionCheckout Checkout() => new("agent-1", _logs.Add, Path.Combine(_root, "qa"));

    private string ExpectedPath => PathConfinement.RealPath(Path.Combine(_root, "qa", "agent-1", "checkout"));

    [Fact]
    public async Task First_prepare_creates_the_worktree_at_origin_main()
    {
        if (!GitAvailable)
            return;
        var before = DeveloperState();

        var prepared = await Checkout().PrepareAsync(Developer, "main", "run-a", CancellationToken.None);

        Assert.NotNull(prepared);
        Assert.Equal(ExpectedPath, prepared.Path);
        Assert.Equal(Git(Pusher, "rev-parse", "HEAD"), prepared.CommitSha);
        Assert.Equal(DateTimeOffset.Parse(Git(Pusher, "log", "-1", "--format=%cI")), prepared.CommitDate);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(prepared.Path, "README.md")));
        Assert.False(File.Exists(Path.Combine(prepared.Path, "feature.txt")));
        Assert.Equal(before, DeveloperState());
    }

    [Fact]
    public async Task A_new_commit_pushed_to_origin_is_picked_up()
    {
        if (!GitAvailable)
            return;
        var checkout = Checkout();
        var first = await checkout.PrepareAsync(Developer, "main", "run-a", CancellationToken.None);
        checkout.Release("run-a");
        var before = DeveloperState();

        Commit(Pusher, "README.md", "v2");
        Git(Pusher, "push", "-q", "origin", "HEAD:main");
        var second = await checkout.PrepareAsync(Developer, "main", "run-b", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.CommitSha, second.CommitSha);
        Assert.Equal(Git(Pusher, "rev-parse", "HEAD"), second.CommitSha);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(second.Path, "README.md")));
        // The card's refresh counts the commits the default branch gained since the one a question read
        Assert.Equal(1, first.CommitCount);
        Assert.Equal(2, second.CommitCount);
        Assert.Equal(before, DeveloperState());
    }

    [Fact]
    public async Task When_the_fetch_fails_the_last_fetched_commit_is_used()
    {
        if (!GitAvailable)
            return;
        var checkout = Checkout();
        var first = await checkout.PrepareAsync(Developer, "main", "run-a", CancellationToken.None);
        checkout.Release("run-a");

        Commit(Pusher, "README.md", "v2");
        Git(Pusher, "push", "-q", "origin", "HEAD:main");
        Directory.Move(Origin, Origin + ".moved");
        var second = await checkout.PrepareAsync(Developer, "main", "run-b", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.CommitSha, second.CommitSha);
        Assert.Contains(_logs, l => l.Contains("git fetch origin main failed"));
    }

    [Fact]
    public async Task Files_left_in_the_checkout_are_cleaned()
    {
        if (!GitAvailable)
            return;
        var checkout = Checkout();
        var first = await checkout.PrepareAsync(Developer, "main", "run-a", CancellationToken.None);
        Assert.NotNull(first);
        File.WriteAllText(Path.Combine(first.Path, "README.md"), "edited by the run");
        File.WriteAllText(Path.Combine(first.Path, "notes.md"), "untracked");
        Directory.CreateDirectory(Path.Combine(first.Path, "bin"));
        File.WriteAllText(Path.Combine(first.Path, "bin", "out.dll"), "build output");
        checkout.Release("run-a");

        var second = await checkout.PrepareAsync(Developer, "main", "run-b", CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(second.Path, "README.md")));
        Assert.False(File.Exists(Path.Combine(second.Path, "notes.md")));
        Assert.False(Directory.Exists(Path.Combine(second.Path, "bin")));
    }

    [Fact]
    public async Task A_deleted_checkout_is_recreated()
    {
        if (!GitAvailable)
            return;
        var checkout = Checkout();
        var first = await checkout.PrepareAsync(Developer, "main", "run-a", CancellationToken.None);
        Assert.NotNull(first);
        checkout.Release("run-a");
        Directory.Delete(first.Path, recursive: true);
        var before = DeveloperState();

        var second = await checkout.PrepareAsync(Developer, "main", "run-b", CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(second.Path, "README.md")));
        // Registered once, under the same name: the old registration was reused.
        var worktrees = Git(Developer, "worktree", "list", "--porcelain").Split('\n')
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, worktrees.Count);
        Assert.Equal(before, DeveloperState());
    }

    [Fact]
    public async Task The_checkout_does_not_move_while_another_question_run_reads_it()
    {
        if (!GitAvailable)
            return;
        var checkout = Checkout();
        var first = await checkout.PrepareAsync(Developer, "main", "run-a", CancellationToken.None);
        File.WriteAllText(Path.Combine(first!.Path, "scratch.txt"), "the running question's file");
        Commit(Pusher, "README.md", "v2");
        Git(Pusher, "push", "-q", "origin", "HEAD:main");

        var whileBusy = await checkout.PrepareAsync(Developer, "main", "run-b", CancellationToken.None);
        checkout.Release("run-a");
        checkout.Release("run-b");
        var afterwards = await checkout.PrepareAsync(Developer, "main", "run-c", CancellationToken.None);

        Assert.Equal(first.CommitSha, whileBusy!.CommitSha);
        Assert.Equal(Git(Pusher, "rev-parse", "HEAD"), afterwards!.CommitSha);
        Assert.False(File.Exists(Path.Combine(afterwards.Path, "scratch.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-upload-pack=evil")]
    [InlineData("main..other")]
    [InlineData("../main")]
    [InlineData("feature/.hidden")]
    [InlineData("main.lock")]
    [InlineData("a//b")]
    [InlineData("main/")]
    [InlineData("main.")]
    [InlineData("has space")]
    [InlineData("main@{1}")]
    [InlineData("main\nother")]
    [InlineData("main\n")]
    public async Task An_invalid_branch_is_refused(string branch)
    {
        Assert.False(QuestionCheckout.IsValidBranch(branch));
        if (!GitAvailable)
            return;

        Assert.Null(await Checkout().PrepareAsync(Developer, branch, "run-a", CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_root, "qa")));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("feature/login-v2")]
    [InlineData("release/1.2.3")]
    public void Usual_branch_names_are_valid(string branch) =>
        Assert.True(QuestionCheckout.IsValidBranch(branch));

    [Fact]
    public async Task An_unknown_branch_is_not_prepared()
    {
        if (!GitAvailable)
            return;
        var before = DeveloperState();

        Assert.Null(await Checkout().PrepareAsync(Developer, "no-such-branch", "run-a", CancellationToken.None));
        Assert.Equal(before, DeveloperState());
    }

    /// <summary>Everything of the developer's that must not change: branch, HEAD, index and working tree, local
    /// branches, stash.</summary>
    private string DeveloperState() => string.Join("\n---\n",
        Git(Developer, "symbolic-ref", "HEAD"),
        Git(Developer, "rev-parse", "HEAD"),
        Git(Developer, "status", "--porcelain=v2", "--untracked-files=all"),
        Git(Developer, "diff"),
        Git(Developer, "diff", "--cached"),
        Git(Developer, "for-each-ref", "refs/heads", "refs/stash"),
        Git(Developer, "stash", "list"),
        File.ReadAllText(Path.Combine(Developer, "README.md")));

    private static void Commit(string repository, string file, string contents)
    {
        File.WriteAllText(Path.Combine(repository, file), contents);
        Git(repository, "add", file);
        Git(repository, "commit", "-q", "-m", contents);
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(ExecutableResolver.Resolve("git")!)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Commits and stashes need an identity; the machine's may be unset.
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "Test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }
}

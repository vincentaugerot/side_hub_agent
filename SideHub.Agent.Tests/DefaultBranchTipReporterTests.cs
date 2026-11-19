using System.Diagnostics;
using SideHub.Agent.Models;
using SideHub.Agent.Outgoing;

namespace SideHub.Agent.Tests;

/// <summary>
/// The tip of origin's default branch sent to the backend (side_hub doc 51): a bare origin, a "pusher" clone that moves
/// main, and the developer's clone the agent runs in, which never fetches by itself.
/// </summary>
public sealed class DefaultBranchTipReporterTests : IDisposable
{
    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    private readonly string _root = Directory.CreateTempSubdirectory("sidehub-tip-").FullName;
    private readonly List<string> _logs = [];
    private readonly List<GitDefaultBranchMessage> _sent = [];
    private string Origin => Path.Combine(_root, "origin.git");
    private string Pusher => Path.Combine(_root, "pusher");
    private string Developer => Path.Combine(_root, "dev");

    public DefaultBranchTipReporterTests()
    {
        if (!GitAvailable)
            return;
        Git(_root, "init", "-q", "--bare", "-b", "main", Origin);
        Git(_root, "clone", "-q", Origin, Pusher);
        Commit(Pusher, "README.md", "v1");
        Git(Pusher, "push", "-q", "origin", "HEAD:main");
        Git(_root, "clone", "-q", Origin, Developer);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DefaultBranchTipReporter Reporter(TimeSpan fetchInterval, string? branch = "main") =>
        new(Developer, () => Task.FromResult(branch), (message, _) => { _sent.Add(message); return Task.FromResult(true); },
            _logs.Add, fetchInterval);

    [Fact]
    public async Task Sends_the_tip_and_its_commit_count_once_until_it_changes()
    {
        if (!GitAvailable)
            return;
        var reporter = Reporter(TimeSpan.Zero);

        await reporter.RefreshAsync(CancellationToken.None);
        await reporter.RefreshAsync(CancellationToken.None);

        var sent = Assert.Single(_sent);
        Assert.Equal("main", sent.Branch);
        Assert.Equal(Git(Pusher, "rev-parse", "HEAD"), sent.Sha);
        Assert.Equal(1, sent.CommitCount);
    }

    [Fact]
    public async Task Fetches_the_branch_so_commits_pushed_elsewhere_are_seen()
    {
        if (!GitAvailable)
            return;
        var reporter = Reporter(TimeSpan.Zero);
        await reporter.RefreshAsync(CancellationToken.None);

        Commit(Pusher, "README.md", "v2");
        Commit(Pusher, "README.md", "v3");
        Git(Pusher, "push", "-q", "origin", "HEAD:main");
        await reporter.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, _sent.Count);
        Assert.Equal(3, _sent[1].CommitCount);
        Assert.Equal(Git(Pusher, "rev-parse", "HEAD"), _sent[1].Sha);
        Assert.Equal(Git(Pusher, "rev-parse", "HEAD"), Git(Developer, "rev-parse", "refs/remotes/origin/main"));
    }

    [Fact]
    public async Task Does_not_fetch_again_before_the_interval()
    {
        if (!GitAvailable)
            return;
        var reporter = Reporter(TimeSpan.FromHours(1));
        await reporter.RefreshAsync(CancellationToken.None);

        Commit(Pusher, "README.md", "v2");
        Git(Pusher, "push", "-q", "origin", "HEAD:main");
        await reporter.RefreshAsync(CancellationToken.None);

        Assert.Single(_sent);
        Assert.Equal(1, _sent[0].CommitCount);
    }

    [Fact]
    public async Task A_failed_fetch_still_sends_the_last_fetched_tip()
    {
        if (!GitAvailable)
            return;
        Git(Developer, "remote", "set-url", "origin", Path.Combine(_root, "missing.git"));
        var reporter = Reporter(TimeSpan.Zero);

        await reporter.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, Assert.Single(_sent).CommitCount);
        Assert.Contains(_logs, line => line.Contains("git fetch origin main failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("--upload-pack=x")]
    [InlineData("unknown")]
    public async Task Sends_nothing_without_a_known_valid_branch(string? branch)
    {
        if (!GitAvailable)
            return;
        await Reporter(TimeSpan.Zero, branch).RefreshAsync(CancellationToken.None);

        Assert.Empty(_sent);
    }

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

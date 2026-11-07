using System.Diagnostics;
using SideHub.Agent.Models;
using SideHub.Agent.Outgoing;

namespace SideHub.Agent.Tests;

/// <summary>The commits of the agent's branch not pushed yet (git.outgoing), and the push confirmed from SideHub (doc 58).</summary>
public sealed class OutgoingCommitsTests : IDisposable
{
    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sidehub-outgoing-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Remote => Path.Combine(_root.FullName, "remote.git");
    private string Work => Path.Combine(_root.FullName, "work");
    private string Other => Path.Combine(_root.FullName, "other");

    /// <summary>A bare remote with one commit on main, cloned into <see cref="Work"/> (main follows origin/main).</summary>
    private void CloneWithRemote()
    {
        Git(_root.FullName, "init", "-q", "--bare", "-b", "main", Remote);
        Git(_root.FullName, "clone", "-q", Remote, Work);
        Git(Work, "checkout", "-q", "-b", "main");
        Commit(Work, "readme.txt", "initial");
        Git(Work, "push", "-q", "-u", "origin", "main");
    }

    [Fact]
    public async Task Counts_the_commits_ahead_of_and_behind_the_upstream()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        Git(_root.FullName, "clone", "-q", Remote, Other);
        Commit(Other, "other.txt", "from elsewhere");
        Git(Other, "push", "-q", "origin", "main");
        Git(Work, "fetch", "-q");
        Commit(Work, "a.txt", "first task");
        Commit(Work, "b.txt", "second task");

        var state = await OutgoingCommitsReader.ReadAsync(Work, CancellationToken.None);

        Assert.Null(state.Error);
        Assert.Equal("main", state.Branch);
        Assert.Equal("origin", state.Remote);
        Assert.Equal("main", state.RemoteBranch);
        Assert.True(state.HasUpstream);
        Assert.Equal(2, state.Ahead);
        Assert.Equal(1, state.Behind);
        Assert.Equal(Git(Work, "rev-parse", "HEAD"), state.Head);
        Assert.Equal(["second task", "first task"], state.Commits.Select(c => c.Subject));
        Assert.Equal(40, state.Commits[0].Sha.Length);
        Assert.False(state.CommitsTruncated);
    }

    [Fact]
    public async Task Lists_twenty_commits_at_most()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        for (var i = 0; i < 22; i++)
            Commit(Work, $"f{i}.txt", $"commit {i}");

        var state = await OutgoingCommitsReader.ReadAsync(Work, CancellationToken.None);

        Assert.Equal(22, state.Ahead);
        Assert.Equal(OutgoingCommitsReader.MaxCommits, state.Commits.Count);
        Assert.Equal("commit 21", state.Commits[0].Subject);
        Assert.True(state.CommitsTruncated);
    }

    [Fact]
    public async Task Without_an_upstream_counts_what_no_branch_of_origin_has()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        Git(Work, "checkout", "-q", "-b", "feature");
        Commit(Work, "f.txt", "feature work");

        var state = await OutgoingCommitsReader.ReadAsync(Work, CancellationToken.None);

        Assert.Null(state.Error);
        Assert.Equal("feature", state.Branch);
        Assert.Equal("origin", state.Remote);
        Assert.Equal("feature", state.RemoteBranch);
        Assert.False(state.HasUpstream);
        Assert.Equal(1, state.Ahead);
        Assert.Equal(0, state.Behind);
        Assert.Equal(["feature work"], state.Commits.Select(c => c.Subject));
    }

    [Fact]
    public async Task A_repository_without_a_remote_has_nothing_to_push_to()
    {
        if (!GitAvailable)
            return;
        Directory.CreateDirectory(Work);
        Git(Work, "init", "-q", "-b", "main");
        Commit(Work, "a.txt", "local");

        var state = await OutgoingCommitsReader.ReadAsync(Work, CancellationToken.None);

        Assert.Equal(OutgoingErrors.NoRemote, state.Error);
        Assert.Equal("main", state.Branch);
        Assert.Equal(0, state.Ahead);
    }

    [Fact]
    public async Task A_detached_head_and_a_plain_folder_are_reported()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        Commit(Work, "a.txt", "work");
        Git(Work, "checkout", "-q", "--detach", "HEAD");
        var plain = Directory.CreateDirectory(Path.Combine(_root.FullName, "plain")).FullName;

        Assert.Equal(OutgoingErrors.Detached, (await OutgoingCommitsReader.ReadAsync(Work, CancellationToken.None)).Error);
        Assert.Equal(OutgoingErrors.NotARepository, (await OutgoingCommitsReader.ReadAsync(plain, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Pushes_the_confirmed_head()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        Commit(Work, "a.txt", "task");
        var head = Git(Work, "rev-parse", "HEAD");

        var result = await OutgoingCommitsReader.PushAsync(Work, head, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(head, Git(Remote, "rev-parse", "main"));
        var state = await OutgoingCommitsReader.ReadAsync(Work, CancellationToken.None);
        Assert.Equal(0, state.Ahead);
        Assert.Empty(state.Commits);
    }

    [Fact]
    public async Task A_branch_without_upstream_follows_the_pushed_branch()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        Git(Work, "checkout", "-q", "-b", "feature");
        Commit(Work, "f.txt", "feature work");

        var result = await OutgoingCommitsReader.PushAsync(Work, Git(Work, "rev-parse", "HEAD"), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("origin/feature", Git(Work, "rev-parse", "--abbrev-ref", "feature@{upstream}"));
        var state = await OutgoingCommitsReader.ReadAsync(Work, CancellationToken.None);
        Assert.True(state.HasUpstream);
        Assert.Equal(0, state.Ahead);
    }

    [Fact]
    public async Task Refuses_to_push_when_head_moved_or_nothing_is_ahead()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        Assert.Equal(OutgoingErrors.NothingToPush,
            (await OutgoingCommitsReader.PushAsync(Work, Git(Work, "rev-parse", "HEAD"), CancellationToken.None)).Error);

        Commit(Work, "a.txt", "confirmed");
        var confirmed = Git(Work, "rev-parse", "HEAD");
        Commit(Work, "b.txt", "made after the confirmation");
        var remoteBefore = Git(Remote, "rev-parse", "main");

        var result = await OutgoingCommitsReader.PushAsync(Work, confirmed, CancellationToken.None);

        Assert.Equal(OutgoingErrors.HeadMoved, result.Error);
        Assert.Equal(remoteBefore, Git(Remote, "rev-parse", "main"));
    }

    [Fact]
    public async Task A_push_behind_the_remote_is_rejected_never_forced()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        Git(_root.FullName, "clone", "-q", Remote, Other);
        Commit(Other, "other.txt", "from elsewhere");
        Git(Other, "push", "-q", "origin", "main");
        var remoteHead = Git(Remote, "rev-parse", "main");
        Commit(Work, "a.txt", "task");

        var result = await OutgoingCommitsReader.PushAsync(Work, Git(Work, "rev-parse", "HEAD"), CancellationToken.None);

        Assert.Equal(OutgoingErrors.Rejected, result.Error);
        Assert.NotNull(result.Detail);
        Assert.Equal(remoteHead, Git(Remote, "rev-parse", "main"));
    }

    [Fact]
    public void Masks_the_credentials_of_urls()
    {
        var detail = OutgoingCommitsReader.Detail(
            "To https://x-access-token:ghp_secret@github.com/acme/app.git\n ! [rejected] main -> main (fetch first)\n");

        Assert.Equal("To https://***@github.com/acme/app.git\n ! [rejected] main -> main (fetch first)", detail);
        Assert.Equal("git@github.com:acme/app.git", OutgoingCommitsReader.MaskCredentials("git@github.com:acme/app.git"));
        Assert.Equal(OutgoingCommitsReader.MaxDetailLength, OutgoingCommitsReader.Detail(new string('x', 5_000))!.Length);
    }

    [Fact]
    public async Task The_reporter_sends_a_state_only_when_it_changed()
    {
        if (!GitAvailable)
            return;
        CloneWithRemote();
        var sent = new List<GitOutgoingMessage>();
        using var reporter = new OutgoingCommitsReporter(() => (Work, false), (message, _) =>
        {
            sent.Add(message);
            return Task.FromResult(true);
        }, _ => { });

        await reporter.RefreshAsync(CancellationToken.None);
        await reporter.RefreshAsync(CancellationToken.None);
        Commit(Work, "a.txt", "task");
        await reporter.RefreshAsync(CancellationToken.None);
        var push = await reporter.PushAsync("request-1", Git(Work, "rev-parse", "HEAD"), CancellationToken.None);

        Assert.Null(push.Error);
        Assert.Equal("request-1", push.RequestId);
        Assert.Equal([0, 1, 0], sent.Select(s => s.Ahead));
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
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }
}

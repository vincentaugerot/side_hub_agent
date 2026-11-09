using System.Diagnostics;
using SideHub.Agent.Models;

namespace SideHub.Agent.Tests;

/// <summary>Where a terminal works, as the cockpit shows it: repository, branch, linked worktree or main checkout.</summary>
public sealed class PtyGitPlacesTests : IDisposable
{
    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sidehub-git-place-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Work => Path.Combine(_root.FullName, "work");

    private void InitWithCommit()
    {
        Directory.CreateDirectory(Work);
        Git(Work, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Work, "readme.txt"), "initial");
        Git(Work, "add", "readme.txt");
        Git(Work, "commit", "-q", "-m", "initial");
    }

    [Fact]
    public async Task The_main_checkout_reads_its_branch_and_top_folder_from_a_subfolder()
    {
        if (!GitAvailable)
            return;
        InitWithCommit();
        var sub = Directory.CreateDirectory(Path.Combine(Work, "src")).FullName;

        var place = await PtyGitPlaces.ReadAsync(sub, CancellationToken.None);

        Assert.NotNull(place);
        Assert.True(place.Repository);
        Assert.Equal("main", place.Branch);
        Assert.Null(place.Head);
        Assert.False(place.Worktree);
        Assert.Equal(Real(Work), Real(place.Directory));
    }

    [Fact]
    public async Task A_linked_worktree_is_told_apart_from_the_main_checkout()
    {
        if (!GitAvailable)
            return;
        InitWithCommit();
        var linked = Path.Combine(_root.FullName, "linked");
        Git(Work, "worktree", "add", "-q", "-b", "sidehub/queue-4a3a6adc", linked);

        var place = await PtyGitPlaces.ReadAsync(linked, CancellationToken.None);

        Assert.NotNull(place);
        Assert.True(place.Worktree);
        Assert.Equal("sidehub/queue-4a3a6adc", place.Branch);
        Assert.Equal(Real(linked), Real(place.Directory));
    }

    [Fact]
    public async Task A_detached_head_gives_its_short_sha_instead_of_a_branch()
    {
        if (!GitAvailable)
            return;
        InitWithCommit();
        Git(Work, "checkout", "-q", "--detach");

        var place = await PtyGitPlaces.ReadAsync(Work, CancellationToken.None);

        Assert.NotNull(place);
        Assert.Null(place.Branch);
        Assert.Equal(Git(Work, "rev-parse", "--short", "HEAD"), place.Head);
    }

    [Fact]
    public async Task A_folder_outside_any_repository_is_sent_as_such()
    {
        if (!GitAvailable)
            return;
        var plain = Directory.CreateDirectory(Path.Combine(_root.FullName, "plain")).FullName;

        var place = await PtyGitPlaces.ReadAsync(plain, CancellationToken.None);

        Assert.NotNull(place);
        Assert.False(place.Repository);
        Assert.Null(place.Branch);
        Assert.Equal(plain, place.Directory);
    }

    [Fact]
    public async Task A_place_is_sent_again_only_when_it_changed_or_after_a_reconnection()
    {
        if (!GitAvailable)
            return;
        InitWithCommit();
        var sent = new List<PtyGitPlaceMessage>();
        using var places = new PtyGitPlaces(() => [], (message, _) => { sent.Add(message); return Task.FromResult(true); }, _ => { });

        await places.RefreshAsync("pty-1", Work, CancellationToken.None);
        await places.RefreshAsync("pty-1", Work, CancellationToken.None);
        Assert.Single(sent);

        Git(Work, "checkout", "-q", "-b", "feature");
        await places.RefreshAsync("pty-1", Work, CancellationToken.None);
        Assert.Equal(2, sent.Count);
        Assert.Equal("feature", sent[1].Branch);
        Assert.Equal("pty-1", sent[1].PtySessionId);

        places.Forget("pty-1");
        await places.RefreshAsync("pty-1", Work, CancellationToken.None);
        Assert.Equal(3, sent.Count);
    }

    // Temp folders can sit behind a symlink (macOS /var): compare the real paths.
    private static string Real(string path) =>
        Path.TrimEndingDirectorySeparator(new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path));

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
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
        return output.Trim();
    }
}

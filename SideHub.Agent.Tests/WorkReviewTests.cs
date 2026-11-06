using System.Diagnostics;
using SideHub.Agent.Models;
using SideHub.Agent.Review;

namespace SideHub.Agent.Tests;

/// <summary>Work review: photos of the folder at the start and end of each round, and what changed between them.</summary>
public sealed class WorkReviewTests : IDisposable
{
    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    private readonly DirectoryInfo _repo = Directory.CreateTempSubdirectory("sidehub-review-repo-");
    private readonly DirectoryInfo _run = Directory.CreateTempSubdirectory("sidehub-review-run-");
    private readonly List<ReviewChangesMessage> _sent = [];
    private bool _backendUp = true;

    public void Dispose()
    {
        _repo.Delete(recursive: true);
        _run.Delete(recursive: true);
    }

    private string RepoPath(string name) => Path.Combine(_repo.FullName, name);

    private WorkReviewTracker Tracker(string agent = "agent-a", WorkReviewTracker.FinalMessageReader? reader = null,
        WorkReviewTracker.CaptureReader? captures = null) => new(
        Path.Combine(_run.FullName, "reviews"), agent,
        new PendingReviewChangesStore(Path.Combine(_run.FullName, "pending", agent)),
        (report, _) =>
        {
            if (!_backendUp)
                return Task.FromResult(false);
            lock (_sent)
                _sent.Add(report);
            return Task.FromResult(true);
        },
        reader ?? ((_, _, _, _) => null),
        _ => { }, captures);

    private void InitRepository()
    {
        Git("init", "-q", "-b", "main");
        // As in a real agent folder: .sidehub/ is ignored.
        File.WriteAllText(RepoPath(".gitignore"), "*.log\n.sidehub/\n");
        File.WriteAllText(RepoPath("app.txt"), "one\ntwo\n");
        File.WriteAllText(RepoPath("old-name.txt"), string.Join("\n", Enumerable.Range(1, 20)) + "\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "initial");
    }

    private Task<WorkReviewTracker.StartResult> StartAsync(WorkReviewTracker tracker, Guid reviewId, string pty, string kind = ReviewKinds.Task) =>
        tracker.StartRoundAsync(reviewId, pty, _repo.FullName, kind, runId: null, taskId: null,
            otherCliBusy: false, WorkReviewTracker.SnapshotTimeout, CancellationToken.None);

    [Fact]
    public async Task A_photo_leaves_the_files_the_index_and_the_branches_as_they_were()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        File.AppendAllText(RepoPath("app.txt"), "three\n");
        File.WriteAllText(RepoPath("new.txt"), "new");
        File.WriteAllText(RepoPath("debug.log"), "ignored");
        Directory.CreateDirectory(RepoPath(".sidehub"));
        File.WriteAllText(RepoPath(".sidehub/agent.json"), "{}");
        // Not ignored by this repository, left out all the same.
        Directory.CreateDirectory(RepoPath(".sidehub-images"));
        File.WriteAllText(RepoPath(".sidehub-images/shot.png"), "png");
        Git("add", "app.txt");
        var status = Git("status", "--porcelain");
        var index = await File.ReadAllBytesAsync(RepoPath(".git/index"));
        var branches = Git("branch", "-a");

        var photo = await WorkReviewGit.SnapshotAsync(_repo.FullName, WorkReviewGit.RefName(Guid.NewGuid(), 1, "start"),
            WorkReviewTracker.SnapshotTimeout, CancellationToken.None);

        Assert.Null(photo.Failure);
        Assert.NotNull(photo.Commit);
        Assert.Equal(status, Git("status", "--porcelain"));
        Assert.Equal(index, await File.ReadAllBytesAsync(RepoPath(".git/index")));
        Assert.Equal(branches, Git("branch", "-a"));
        Assert.Equal("initial", Git("log", "-1", "--format=%s").Trim());
        var files = Git("ls-tree", "-r", "--name-only", photo.Commit!).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("new.txt", files);
        Assert.Contains("app.txt", files);
        Assert.DoesNotContain("debug.log", files);
        Assert.DoesNotContain(".sidehub/agent.json", files);
        Assert.DoesNotContain(".sidehub-images/shot.png", files);
    }

    [Fact]
    public async Task A_round_reports_its_commits_and_what_it_left_uncommitted_but_not_what_was_there_before()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        // Already in progress before the work: never part of its round.
        File.WriteAllText(RepoPath("before.txt"), "mine");
        var tracker = Tracker(reader: (_, _, _, _) => "Renamed the file and added a line.");
        var reviewId = Guid.NewGuid();

        var start = await StartAsync(tracker, reviewId, "pty-1");
        File.AppendAllText(RepoPath("app.txt"), "three\n");
        Git("mv", "old-name.txt", "new-name.txt");
        Git("commit", "-q", "-am", "rename and add");
        File.WriteAllText(RepoPath("added.txt"), "added\n");
        await File.WriteAllBytesAsync(RepoPath("image.bin"), [0, 1, 2, 0, 255]);
        await tracker.EndRoundAsync("pty-1", "task-done", ReviewKinds.Task, CancellationToken.None);

        Assert.Equal(1, start.Round);
        Assert.Null(start.Error);
        var report = Assert.Single(_sent);
        Assert.Null(report.Error);
        Assert.Equal(reviewId, report.ReviewId);
        Assert.Equal(1, report.Round);
        Assert.Equal("main", report.StartBranch);
        Assert.Equal("Renamed the file and added a line.", report.Summary);
        Assert.False(report.Overlapping);
        Assert.Equal(["added.txt", "app.txt", "image.bin", "new-name.txt"], report.Files.Select(f => f.Path).Order());
        Assert.Equal("old-name.txt", report.Files.Single(f => f.Path == "new-name.txt").OldPath);
        Assert.Equal("R", report.Files.Single(f => f.Path == "new-name.txt").Status);
        Assert.True(report.Files.Single(f => f.Path == "image.bin").Binary);
        Assert.Equal(2, report.Insertions);
        Assert.Equal("rename and add", Assert.Single(report.Commits).Subject);

        var diff = await tracker.DiffAsync("r1", reviewId, 1, path: null, CancellationToken.None);
        Assert.Null(diff.Error);
        Assert.Contains("+three", diff.Diff);
        Assert.DoesNotContain("before.txt", diff.Diff);
        var oneFile = await tracker.DiffAsync("r2", reviewId, 1, "added.txt", CancellationToken.None);
        Assert.Contains("+added", oneFile.Diff);
        Assert.DoesNotContain("three", oneFile.Diff);
    }

    [Fact]
    public async Task Each_round_keeps_its_own_diff_whatever_happened_in_between()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();
        var task1 = Guid.NewGuid();

        await StartAsync(tracker, task1, "pty-1");
        File.WriteAllText(RepoPath("task1.txt"), "first version\n");
        await tracker.EndRoundAsync("pty-1", "task-done", ReviewKinds.Task, CancellationToken.None);

        // The next task of the queue works in the same folder before the fix of task 1.
        var task2 = Guid.NewGuid();
        await StartAsync(tracker, task2, "pty-2");
        File.WriteAllText(RepoPath("task2.txt"), "other work\n");
        await tracker.EndRoundAsync("pty-2", "task-done", ReviewKinds.Task, CancellationToken.None);

        // The fix of task 1, typed in its terminal (no kind: it keeps the work's own).
        var fix = await tracker.StartRoundAsync(task1, "pty-1", _repo.FullName, kind: null, runId: null, taskId: null,
            otherCliBusy: false, WorkReviewTracker.SnapshotTimeout, CancellationToken.None);
        File.WriteAllText(RepoPath("task1.txt"), "fixed version\n");
        await tracker.EndRoundAsync("pty-1", "task-done", ReviewKinds.Task, CancellationToken.None);

        Assert.Equal(2, fix.Round);
        var round1 = await tracker.DiffAsync("a", task1, 1, null, CancellationToken.None);
        var round2 = await tracker.DiffAsync("b", task1, 2, null, CancellationToken.None);
        Assert.Contains("+first version", round1.Diff);
        Assert.DoesNotContain("task2.txt", round1.Diff);
        Assert.Contains("+fixed version", round2.Diff);
        Assert.Contains("-first version", round2.Diff);
        Assert.DoesNotContain("task2.txt", round2.Diff);
        Assert.Equal(ReviewKinds.Task, _sent.Last().Kind);
        Assert.All(_sent, r => Assert.False(r.Overlapping));
    }

    [Fact]
    public async Task Another_agent_working_in_the_folder_at_the_same_time_is_reported()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var mine = Tracker("agent-a");
        var theirs = Tracker("agent-b");

        await StartAsync(mine, Guid.NewGuid(), "pty-1");
        await StartAsync(theirs, Guid.NewGuid(), "pty-2");
        File.WriteAllText(RepoPath("theirs.txt"), "x\n");
        await theirs.EndRoundAsync("pty-2", "exit", null, CancellationToken.None);
        await mine.EndRoundAsync("pty-1", "exit", null, CancellationToken.None);

        Assert.All(_sent, r => Assert.True(r.Overlapping));
    }

    [Fact]
    public async Task A_cli_working_in_another_terminal_of_the_folder_is_reported()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();

        await StartAsync(tracker, Guid.NewGuid(), "pty-1");
        tracker.NoteCliWorking("pty-hand-opened", _repo.FullName);
        await tracker.EndRoundAsync("pty-1", "exit", null, CancellationToken.None);

        Assert.True(Assert.Single(_sent).Overlapping);
    }

    [Fact]
    public async Task Task_done_does_not_end_the_round_of_a_run()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();

        await StartAsync(tracker, Guid.NewGuid(), "run-1", ReviewKinds.Run);
        await tracker.EndRoundAsync("run-1", "task-done", ReviewKinds.Task, CancellationToken.None);
        Assert.Empty(_sent);
        Assert.True(tracker.HasOpenRound("run-1"));

        await tracker.EndRoundAsync("run-1", "exit", null, CancellationToken.None);
        Assert.Equal(ReviewKinds.Run, Assert.Single(_sent).Kind);
    }

    [Fact]
    public async Task A_repository_without_commits_is_photographed_too()
    {
        if (!GitAvailable)
            return;
        Git("init", "-q");
        var tracker = Tracker();

        await StartAsync(tracker, Guid.NewGuid(), "pty-1");
        File.WriteAllText(RepoPath("first.txt"), "hello\n");
        await tracker.EndRoundAsync("pty-1", "exit", null, CancellationToken.None);

        var report = Assert.Single(_sent);
        Assert.Null(report.Error);
        Assert.Equal("first.txt", Assert.Single(report.Files).Path);
    }

    [Fact]
    public async Task Outside_a_repository_the_round_reports_why_there_is_nothing_to_review()
    {
        var tracker = Tracker();

        var start = await StartAsync(tracker, Guid.NewGuid(), "pty-1");
        await tracker.EndRoundAsync("pty-1", "exit", null, CancellationToken.None);

        Assert.Equal(ReviewErrors.NotARepository, start.Error);
        Assert.Equal(ReviewErrors.NotARepository, Assert.Single(_sent).Error);
    }

    [Fact]
    public async Task A_report_the_backend_missed_is_kept_and_replayed()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();
        _backendUp = false;

        await StartAsync(tracker, Guid.NewGuid(), "pty-1");
        File.WriteAllText(RepoPath("x.txt"), "x\n");
        await tracker.EndRoundAsync("pty-1", "exit", null, CancellationToken.None);
        Assert.Empty(_sent);

        _backendUp = true;
        await tracker.ReplayPendingAsync(CancellationToken.None);
        Assert.Equal("x.txt", Assert.Single(Assert.Single(_sent).Files).Path);
        await tracker.ReplayPendingAsync(CancellationToken.None);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task A_round_cut_by_an_agent_restart_is_reported_as_interrupted()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var reviewId = Guid.NewGuid();
        await StartAsync(Tracker("agent-a"), reviewId, "pty-1");
        await StartAsync(Tracker("agent-b"), Guid.NewGuid(), "pty-2");

        // agent-a restarts: its terminal died with it, agent-b's round goes on.
        var restarted = Tracker("agent-a");
        await restarted.ReplayPendingAsync(CancellationToken.None);

        var report = Assert.Single(_sent);
        Assert.Equal(reviewId, report.ReviewId);
        Assert.Equal(ReviewErrors.Interrupted, report.Error);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b")]
    [InlineData("bad\nname")]
    public async Task A_diff_is_only_asked_for_a_file_of_the_repository(string path)
    {
        var diff = await Tracker().DiffAsync("r", Guid.NewGuid(), 1, path, CancellationToken.None);

        Assert.Equal(ReviewDiffErrors.InvalidPath, diff.Error);
    }

    [Fact]
    public async Task A_diff_of_a_round_still_going_on_or_unknown_says_so()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();
        var reviewId = Guid.NewGuid();
        await StartAsync(tracker, reviewId, "pty-1");

        Assert.Equal(ReviewDiffErrors.RoundOpen, (await tracker.DiffAsync("a", reviewId, 1, null, CancellationToken.None)).Error);
        Assert.Equal(ReviewDiffErrors.UnknownRound, (await tracker.DiffAsync("b", reviewId, 2, null, CancellationToken.None)).Error);
        Assert.Equal(ReviewDiffErrors.UnknownReview, (await tracker.DiffAsync("c", Guid.NewGuid(), 1, null, CancellationToken.None)).Error);
        Assert.Equal(ReviewDiffErrors.RoundInProgress, (await StartAsync(tracker, reviewId, "pty-1")).Error);
    }

    [Fact]
    public async Task An_old_review_is_purged_with_its_photos()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();
        var reviewId = Guid.NewGuid();
        await StartAsync(tracker, reviewId, "pty-1");
        await tracker.EndRoundAsync("pty-1", "exit", null, CancellationToken.None);
        Assert.NotEmpty(Git("for-each-ref", WorkReviewGit.RefPrefix).Trim());

        await tracker.PurgeAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Empty(Git("for-each-ref", WorkReviewGit.RefPrefix).Trim());
        Assert.Equal(ReviewDiffErrors.UnknownReview, (await tracker.DiffAsync("a", reviewId, 1, null, CancellationToken.None)).Error);
    }

    [Fact]
    public void Renames_are_read_from_the_nul_separated_outputs()
    {
        var files = WorkReviewGit.ParseNameStatus("M\0app.txt\0R087\0old.txt\0new.txt\0A\0added.txt\0");
        var counts = WorkReviewGit.ParseNumstat("1\t0\tapp.txt\0" + "2\t1\t\0old.txt\0new.txt\0" + "-\t-\timage.bin\0");

        Assert.Equal(["app.txt", "new.txt", "added.txt"], files.Select(f => f.Path));
        Assert.Equal("old.txt", files[1].OldPath);
        Assert.Equal((2, 1), (counts["new.txt"].Insertions, counts["new.txt"].Deletions));
        Assert.True(counts["image.bin"].Binary);
    }

    [Fact]
    public async Task A_review_asked_from_a_terminal_covers_what_changed_since_its_first_cli_then_since_the_last_review()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        // Already in progress before the session: never part of its rounds.
        File.WriteAllText(RepoPath("before.txt"), "mine");
        var tracker = Tracker();
        await tracker.NoteCliStartedAsync("pty-s", _repo.FullName, CancellationToken.None);
        Assert.True(tracker.HasBase("pty-s"));
        Assert.Contains(WorkReviewGit.SessionRefName("pty-s"), Git("for-each-ref", "--format=%(refname)"));
        var reviewId = Guid.NewGuid();

        File.AppendAllText(RepoPath("app.txt"), "three\n");
        var first = await tracker.EndRoundOnRequestAsync(reviewId, "pty-s", CancellationToken.None);
        File.WriteAllText(RepoPath("fix.txt"), "fix\n");
        var second = await tracker.EndRoundOnRequestAsync(reviewId, "pty-s", CancellationToken.None);

        Assert.Equal(new WorkReviewTracker.EndResult(1, null), first);
        Assert.Equal(new WorkReviewTracker.EndResult(2, null), second);
        Assert.Equal(2, _sent.Count);
        Assert.All(_sent, r => Assert.Equal(ReviewKinds.Session, r.Kind));
        Assert.Equal(["app.txt"], _sent[0].Files.Select(f => f.Path));
        Assert.Equal(["fix.txt"], _sent[1].Files.Select(f => f.Path));
        // The second round starts where the first one ended.
        Assert.Equal(_sent[0].EndedAt, _sent[1].StartedAt);
        var diff = await tracker.DiffAsync("req", reviewId, 2, path: null, CancellationToken.None);
        Assert.Contains("+fix", diff.Diff);
        Assert.DoesNotContain("three", diff.Diff);
    }

    [Fact]
    public async Task A_review_asked_when_nothing_changed_is_dropped_and_keeps_the_base()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();
        await tracker.NoteCliStartedAsync("pty-s", _repo.FullName, CancellationToken.None);
        var reviewId = Guid.NewGuid();

        var empty = await tracker.EndRoundOnRequestAsync(reviewId, "pty-s", CancellationToken.None);
        File.WriteAllText(RepoPath("later.txt"), "later\n");
        var later = await tracker.EndRoundOnRequestAsync(reviewId, "pty-s", CancellationToken.None);

        Assert.Equal(new WorkReviewTracker.EndResult(null, ReviewErrors.NoChanges), empty);
        Assert.Equal(new WorkReviewTracker.EndResult(1, null), later);
        var report = Assert.Single(_sent);
        Assert.Equal(1, report.Round);
        Assert.Equal(["later.txt"], report.Files.Select(f => f.Path));
        Assert.Equal(ReviewDiffErrors.UnknownRound, (await tracker.DiffAsync("req", reviewId, 2, path: null, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task A_review_asked_from_a_terminal_without_a_cli_is_refused()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();

        var result = await tracker.EndRoundOnRequestAsync(Guid.NewGuid(), "pty-none", CancellationToken.None);

        Assert.Equal(new WorkReviewTracker.EndResult(null, ReviewErrors.NoBase), result);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_review_asked_from_a_task_terminal_ends_its_round_early_then_continues_from_there()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();
        var taskReview = Guid.NewGuid();
        await StartAsync(tracker, taskReview, "pty-t");
        // A CLI starting in a terminal with a round going on takes no base: the round has its own photo.
        await tracker.NoteCliStartedAsync("pty-t", _repo.FullName, CancellationToken.None);
        Assert.False(tracker.HasBase("pty-t"));
        File.WriteAllText(RepoPath("task.txt"), "task\n");

        var other = await tracker.EndRoundOnRequestAsync(Guid.NewGuid(), "pty-t", CancellationToken.None);
        var first = await tracker.EndRoundOnRequestAsync(taskReview, "pty-t", CancellationToken.None);
        // task done comes later: the round already ended.
        await tracker.EndRoundAsync("pty-t", "task-done", ReviewKinds.Task, CancellationToken.None);
        File.WriteAllText(RepoPath("more.txt"), "more\n");
        var second = await tracker.EndRoundOnRequestAsync(taskReview, "pty-t", CancellationToken.None);

        Assert.Equal(new WorkReviewTracker.EndResult(null, ReviewDiffErrors.RoundInProgress), other);
        Assert.Equal(new WorkReviewTracker.EndResult(1, null), first);
        Assert.Equal(new WorkReviewTracker.EndResult(2, null), second);
        Assert.Equal(2, _sent.Count);
        Assert.All(_sent, r => Assert.Equal(ReviewKinds.Task, r.Kind));
        Assert.Equal(["more.txt"], _sent[1].Files.Select(f => f.Path));
    }

    [Fact]
    public async Task The_base_of_a_terminal_goes_with_it()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var tracker = Tracker();
        await tracker.NoteCliStartedAsync("pty-s", _repo.FullName, CancellationToken.None);

        await tracker.ForgetTerminalAsync("pty-s", CancellationToken.None);

        Assert.False(tracker.HasBase("pty-s"));
        Assert.DoesNotContain(WorkReviewGit.SessionRefPrefix, Git("for-each-ref", "--format=%(refname)"));
        Assert.Equal(ReviewErrors.NoBase, (await tracker.EndRoundOnRequestAsync(Guid.NewGuid(), "pty-s", CancellationToken.None)).Error);
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo(ExecutableResolver.Resolve("git")!)
        {
            WorkingDirectory = _repo.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com" }.Concat(args))
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public async Task A_round_keeps_a_copy_of_the_images_the_cli_saw_served_until_the_purge()
    {
        if (!GitAvailable)
            return;
        InitRepository();
        var screenshot = RepoPath("shot.png");
        await File.WriteAllBytesAsync(screenshot, Png);
        var tracker = Tracker(captures: (_, _, sessions, _) =>
        [
            new(new Conversation.ToolImage("toolu_1-1.png", "toolu_1", DateTime.UtcNow, null, null, screenshot), "mcp__playwright__browser_take_screenshot", "shot.png"),
            // The same image read again: kept once
            new(new Conversation.ToolImage("toolu_2-1.png", "toolu_2", DateTime.UtcNow, Convert.ToBase64String(Png), "image/png", null), "Read", "shot.png"),
            new(new Conversation.ToolImage("toolu_3-1.png", "toolu_3", DateTime.UtcNow, null, null, RepoPath("gone.png")), "Read", "gone.png"),
        ]);
        var reviewId = Guid.NewGuid();
        await StartAsync(tracker, reviewId, "pty-1");
        tracker.RecordCliSession("pty-1", "claude", Guid.NewGuid().ToString());
        File.AppendAllText(RepoPath("app.txt"), "three\n");
        await tracker.EndRoundAsync("pty-1", "task-done", ReviewKinds.Task, CancellationToken.None);
        // The screenshot is overwritten after the round: the review keeps what the CLI saw
        File.Delete(screenshot);

        var capture = Assert.Single(Assert.Single(_sent).Captures!);
        Assert.Equal("r1-1.png", capture.Name);
        Assert.Equal("Read", capture.Tool);
        Assert.Equal(Png.Length, capture.Size);
        var served = await tracker.CaptureAsync("c1", reviewId, "r1-1.png", CancellationToken.None);
        Assert.Null(served.Error);
        Assert.Equal("image/png", served.MimeType);
        Assert.Equal(Png, Convert.FromBase64String(served.Data!));
        Assert.Equal("invalid", (await tracker.CaptureAsync("c2", reviewId, "../r1-1.png", CancellationToken.None)).Error);
        Assert.Equal(ReviewDiffErrors.Purged, (await tracker.CaptureAsync("c3", reviewId, "r1-2.png", CancellationToken.None)).Error);

        await tracker.PurgeAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(ReviewDiffErrors.Purged, (await tracker.CaptureAsync("c4", reviewId, "r1-1.png", CancellationToken.None)).Error);
    }
}

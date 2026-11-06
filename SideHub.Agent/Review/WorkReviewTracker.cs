using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SideHub.Agent.Conversation;
using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent.Review;

/// <summary>
/// Follows the rounds of the works the backend asked to review (a run, a task, then each fix asked for): a photo of
/// the folder when a round starts, another when it ends, and what changed between them sent as
/// <c>review.changes</c>. The photos stay on this machine (refs under <c>refs/sidehub/reviews/</c>, records in
/// <c>.sidehub/run/reviews/</c>); the diff is read from them on demand (<c>review.diff.request</c>).
/// <para>Comparing two photos, never the folder as it is now, keeps a round's diff the same however late it is read,
/// and leaves out what other works changed between two rounds.</para>
/// </summary>
public sealed class WorkReviewTracker
{
    /// <summary>A round started by pty.start holds the terminal's start: the backend waits 10 s for pty.started.</summary>
    public static readonly TimeSpan LaunchSnapshotTimeout = TimeSpan.FromSeconds(8);
    /// <summary>Other photos (end of a round, start of a fix in an open terminal).</summary>
    public static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(30);
    /// <summary>A review's photos and record are deleted this long after its last round.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    /// <summary>The activity of the folder is only needed while rounds can overlap.</summary>
    public static readonly TimeSpan ActivityRetention = TimeSpan.FromDays(7);
    public const int MaxSummaryLength = 4000;
    /// <summary>Images kept for a round (doc 70): the last ones the CLI saw.</summary>
    public const int MaxCaptures = 20;
    private const long MaxCapturesBytes = 40 * 1024 * 1024;

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions RecordJson = new() { WriteIndented = true };

    /// <summary>The CLI's last message in a terminal since a moment (transcript of its sessions), or null.</summary>
    public delegate string? FinalMessageReader(
        string ptySessionId, string cwd, IReadOnlyList<KeyValuePair<string, string>> cliSessions, DateTimeOffset since);

    /// <summary>An image a tool call showed the CLI, with that call's tool and what it worked on.</summary>
    public sealed record SeenImage(ToolImage Image, string? Tool, string? Title);

    /// <summary>The images the CLIs of a terminal saw since a moment (their transcripts' tool calls), in the order they came.</summary>
    public delegate IReadOnlyList<SeenImage> CaptureReader(
        string ptySessionId, string cwd, IReadOnlyList<KeyValuePair<string, string>> cliSessions, DateTimeOffset since);

    private readonly string _directory;
    private readonly string _agentKey;
    private readonly WorkReviewActivity _activity;
    private readonly PendingReviewChangesStore _pending;
    private readonly Func<ReviewChangesMessage, CancellationToken, Task<bool>> _trySend;
    private readonly FinalMessageReader _readFinalMessage;
    private readonly CaptureReader? _readCaptures;
    private readonly Action<string> _log;
    // The round going on in each terminal.
    private readonly ConcurrentDictionary<string, OpenRound> _open = new();
    // CLI sessions started in each terminal (cliSessionId → provider), in order: the last one is the round's CLI.
    private readonly ConcurrentDictionary<string, List<KeyValuePair<string, string>>> _cliSessions = new();
    // The photo a round asked from each terminal starts from (doc 63): taken when its first CLI starts, then moved to the
    // end of each of its rounds. Nothing is sent until someone asks for a review.
    private readonly ConcurrentDictionary<string, SessionBase> _bases = new();
    // Terminals whose base is being taken: Taking, or Dropped once the terminal ended meanwhile.
    private readonly ConcurrentDictionary<string, int> _basing = new();
    private const int Taking = 0;
    private const int Dropped = 1;
    // Records are read and written by starts, ends and diffs of any terminal.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    // What the FIFO announces is untrusted: a terminal flooding it must not grow this list without bound.
    private const int MaxCliSessionsPerTerminal = 64;

    /// <param name="directory">The agents' shared <c>.sidehub/run/reviews</c> folder.</param>
    /// <param name="agentKey">Names this agent in the folder's activity.</param>
    /// <param name="trySend">Sends a report; false when the backend is unreachable.</param>
    public WorkReviewTracker(
        string directory, string agentKey, PendingReviewChangesStore pending,
        Func<ReviewChangesMessage, CancellationToken, Task<bool>> trySend,
        FinalMessageReader readFinalMessage, Action<string> log, CaptureReader? readCaptures = null)
    {
        _readCaptures = readCaptures;
        _directory = directory;
        _agentKey = agentKey;
        _activity = new WorkReviewActivity(Path.Combine(directory, "activity.jsonl"));
        _pending = pending;
        _trySend = trySend;
        _readFinalMessage = readFinalMessage;
        _log = log;
        CloseInterruptedRounds();
    }

    public sealed record StartResult(int? Round, string? Error);

    /// <summary>
    /// Starts a round of <paramref name="reviewId"/> in a terminal: round 1 for a work this agent has no record of,
    /// the next one for a fix. Without git or a photo, the round still opens and its report carries the error.
    /// </summary>
    /// <param name="kind"><see cref="ReviewKinds"/>; a fix keeps the work's own when null.</param>
    /// <param name="otherCliBusy">A CLI of another terminal is working in the same folder right now.</param>
    public async Task<StartResult> StartRoundAsync(
        Guid reviewId, string ptySessionId, string cwd, string? kind, Guid? runId, Guid? taskId,
        bool otherCliBusy, TimeSpan timeout, CancellationToken ct)
    {
        if (_open.ContainsKey(ptySessionId))
            return new StartResult(null, ReviewDiffErrors.RoundInProgress);

        await _lock.WaitAsync(ct);
        try
        {
            return await StartLockedAsync(reviewId, ptySessionId, cwd, kind, runId, taskId, otherCliBusy, timeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never in the way of the terminal itself: it starts without a review.
            _log($"Review {reviewId} round could not start in PTY {ptySessionId}: {ex.Message}");
            return new StartResult(null, ReviewErrors.Failed);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<StartResult> StartLockedAsync(
        Guid reviewId, string ptySessionId, string cwd, string? kind, Guid? runId, Guid? taskId,
        bool otherCliBusy, TimeSpan timeout, CancellationToken ct)
    {
        {
            var record = Load(reviewId);
            var previous = record?.Rounds.LastOrDefault();
            var round = new WorkReviewRound
            {
                Round = (previous?.Round ?? 0) + 1,
                Kind = kind ?? previous?.Kind ?? ReviewKinds.Task,
                PtySessionId = ptySessionId,
                RunId = runId ?? previous?.RunId,
                TaskId = taskId ?? previous?.TaskId,
                Agent = _agentKey,
                StartedAt = DateTimeOffset.UtcNow,
                Overlapping = otherCliBusy,
            };

            var repository = await GitRepository.OpenAsync(cwd);
            if (repository is null)
                round.Error = ReviewErrors.NotARepository;
            else
            {
                round.StartHead = await WorkReviewGit.HeadAsync(repository.TopLevel, ct);
                round.StartBranch = await WorkReviewGit.BranchAsync(repository.TopLevel, ct);
                var photo = await WorkReviewGit.SnapshotAsync(
                    repository.TopLevel, WorkReviewGit.RefName(reviewId, round.Round, "start"), timeout, ct);
                round.Start = photo.Commit;
                if (round.Start is null)
                {
                    round.Error = ReviewErrors.SnapshotFailed;
                    _log($"Review {reviewId} round {round.Round}: snapshot failed ({photo.Failure})");
                }
            }

            record ??= new WorkReviewRecord { ReviewId = reviewId, TopLevel = repository?.TopLevel ?? cwd };
            if (repository is not null)
                record.TopLevel = repository.TopLevel;
            record.Rounds.Add(round);
            Save(record);

            _open[ptySessionId] = new OpenRound(reviewId, round.Round, record.TopLevel, cwd);
            _activity.Append(new WorkReviewActivity.Entry(reviewId, round.Round, _agentKey, WorkReviewActivity.Start, round.StartedAt));
            _log(round.Error is null
                ? $"Review {reviewId} round {round.Round} started in PTY {ptySessionId} from {Short(round.Start)}"
                : $"Review {reviewId} round {round.Round} started in PTY {ptySessionId} without a photo ({round.Error})");
            return new StartResult(round.Round, round.Error);
        }
    }

    public bool HasOpenRound(string ptySessionId) => _open.ContainsKey(ptySessionId);

    public bool HasBase(string ptySessionId) => _bases.ContainsKey(ptySessionId);

    /// <summary>The terminal could not start after all: its round is dropped without a report.</summary>
    public void Abandon(string ptySessionId)
    {
        _cliSessions.TryRemove(ptySessionId, out _);
        if (_open.TryRemove(ptySessionId, out var open))
            TryAppendActivity(open.ReviewId, open.Round, WorkReviewActivity.End, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A CLI started in a terminal: the first one gives the terminal its base, the photo a review asked from it starts
    /// from (doc 63). Not while a round goes on there (a task's starts with its own photo), nor once it has a base.
    /// </summary>
    public async Task NoteCliStartedAsync(string ptySessionId, string cwd, CancellationToken ct)
    {
        if (_bases.ContainsKey(ptySessionId) || _open.ContainsKey(ptySessionId) || !_basing.TryAdd(ptySessionId, Taking))
            return;
        try
        {
            if (await GitRepository.OpenAsync(cwd) is not { } repository)
                return;
            var head = await WorkReviewGit.HeadAsync(repository.TopLevel, ct);
            var branch = await WorkReviewGit.BranchAsync(repository.TopLevel, ct);
            var refName = WorkReviewGit.SessionRefName(ptySessionId);
            var photo = await WorkReviewGit.SnapshotAsync(repository.TopLevel, refName, SnapshotTimeout, ct);
            if (photo.Commit is null)
            {
                _log($"Session base of PTY {ptySessionId}: snapshot failed ({photo.Failure})");
                return;
            }
            // The terminal ended while the photo was taken: nothing to keep.
            if (_basing.TryGetValue(ptySessionId, out var state) && state == Dropped)
            {
                await WorkReviewGit.DeleteRefAsync(repository.TopLevel, refName, ct);
                return;
            }
            _bases[ptySessionId] = new SessionBase(repository.TopLevel, cwd, photo.Commit, head, branch, DateTimeOffset.UtcNow);
            _log($"Session base of PTY {ptySessionId}: {Short(photo.Commit)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never in the way of the CLI: the terminal just has no base, a review asked from it is refused.
            _log($"Session base of PTY {ptySessionId} could not be taken: {ex.Message}");
        }
        finally
        {
            _basing.TryRemove(ptySessionId, out _);
        }
    }

    public sealed record EndResult(int? Round, string? Error);

    /// <summary>
    /// Someone asked for a review of a terminal's work (doc 63): the round of <paramref name="reviewId"/> going on there
    /// ends now, or one is made from the terminal's base to now. A round made from the base that changed nothing is
    /// dropped (<see cref="ReviewErrors.NoChanges"/>): the base stays where it was.
    /// </summary>
    public async Task<EndResult> EndRoundOnRequestAsync(Guid reviewId, string ptySessionId, CancellationToken ct)
    {
        try
        {
            if (_open.TryGetValue(ptySessionId, out var open))
            {
                if (open.ReviewId != reviewId)
                    return new EndResult(null, ReviewDiffErrors.RoundInProgress);
                return await EndRoundCoreAsync(ptySessionId, "review-request", kind: null, dropIfEmpty: false, ct)
                       ?? new EndResult(null, ReviewErrors.Failed);
            }
            if (!_bases.TryGetValue(ptySessionId, out var @base))
                return new EndResult(null, ReviewErrors.NoBase);

            await _lock.WaitAsync(ct);
            try
            {
                if (_open.ContainsKey(ptySessionId))
                    return new EndResult(null, ReviewDiffErrors.RoundInProgress);
                await StartFromBaseLockedAsync(reviewId, ptySessionId, @base, ct);
            }
            finally
            {
                _lock.Release();
            }
            return await EndRoundCoreAsync(ptySessionId, "review-request", kind: null, dropIfEmpty: true, ct)
                   ?? new EndResult(null, ReviewErrors.Failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Review {reviewId} round could not end on request in PTY {ptySessionId}: {ex.Message}");
            return new EndResult(null, ReviewErrors.Failed);
        }
    }

    /// <summary>A round of <paramref name="reviewId"/> that started at the terminal's base: same photo, same time.</summary>
    private async Task StartFromBaseLockedAsync(Guid reviewId, string ptySessionId, SessionBase @base, CancellationToken ct)
    {
        var record = Load(reviewId);
        var previous = record?.Rounds.LastOrDefault();
        var round = new WorkReviewRound
        {
            Round = (previous?.Round ?? 0) + 1,
            Kind = previous?.Kind ?? ReviewKinds.Session,
            PtySessionId = ptySessionId,
            RunId = previous?.RunId,
            TaskId = previous?.TaskId,
            Agent = _agentKey,
            StartedAt = @base.At,
            Start = @base.Commit,
            StartHead = @base.Head,
            StartBranch = @base.Branch,
        };
        // The base is kept by the terminal's ref, which moves: the round keeps its own.
        if (!await WorkReviewGit.SetRefAsync(@base.TopLevel, WorkReviewGit.RefName(reviewId, round.Round, "start"), @base.Commit, ct))
        {
            round.Start = null;
            round.Error = ReviewErrors.SnapshotFailed;
            _log($"Review {reviewId} round {round.Round}: the base of PTY {ptySessionId} could not be kept");
        }
        record ??= new WorkReviewRecord { ReviewId = reviewId, TopLevel = @base.TopLevel };
        record.TopLevel = @base.TopLevel;
        record.Rounds.Add(round);
        Save(record);
        _open[ptySessionId] = new OpenRound(reviewId, round.Round, record.TopLevel, @base.Cwd) { FromBase = true };
        _log($"Review {reviewId} round {round.Round} started in PTY {ptySessionId} from its base {Short(round.Start)}");
    }

    public void RecordCliSession(string ptySessionId, string provider, string cliSessionId)
    {
        var sessions = _cliSessions.GetOrAdd(ptySessionId, _ => []);
        lock (sessions)
        {
            sessions.RemoveAll(s => s.Key == cliSessionId);
            if (sessions.Count >= MaxCliSessionsPerTerminal)
                sessions.RemoveAt(0);
            sessions.Add(new(cliSessionId, provider));
        }
    }

    /// <summary>A CLI started working in <paramref name="cwd"/>: the rounds of the other terminals of that folder
    /// will say that their changes may include its own.</summary>
    public void NoteCliWorking(string ptySessionId, string cwd)
    {
        foreach (var (otherPty, open) in _open)
            if (otherPty != ptySessionId && IsWithin(open.TopLevel, cwd))
                open.OtherCliBusy = true;
    }

    /// <summary>
    /// Ends the round going on in a terminal: photo of the end, report sent (or kept until the backend is back).
    /// Nothing when no round is going on there, or when it is not of <paramref name="kind"/> (a task's
    /// <c>task done</c> never ends a run's round).
    /// </summary>
    public async Task EndRoundAsync(string ptySessionId, string trigger, string? kind, CancellationToken ct)
    {
        try
        {
            await EndRoundCoreAsync(ptySessionId, trigger, kind, dropIfEmpty: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never in the way of what follows (the run's usage).
            _log($"Review round of PTY {ptySessionId} could not end ({trigger}): {ex.Message}");
        }
    }

    /// <param name="dropIfEmpty">A round that changed nothing is taken out of the review instead of being reported.</param>
    /// <returns>Null when no round of <paramref name="kind"/> was going on in the terminal.</returns>
    private async Task<EndResult?> EndRoundCoreAsync(string ptySessionId, string trigger, string? kind, bool dropIfEmpty, CancellationToken ct)
    {
        if (!_open.TryGetValue(ptySessionId, out var open))
            return null;

        ReviewChangesMessage report;
        await _lock.WaitAsync(ct);
        try
        {
            if (Load(open.ReviewId) is not { } record
                || record.Rounds.FirstOrDefault(r => r.Round == open.Round) is not { } round
                || (kind is not null && round.Kind != kind)
                || !_open.TryRemove(new KeyValuePair<string, OpenRound>(ptySessionId, open)))
                return null;

            round.EndedAt = DateTimeOffset.UtcNow;
            round.Overlapping |= open.OtherCliBusy;
            IReadOnlyList<ReviewFileChange> files = [];
            IReadOnlyList<ReviewCommit> commits = [];
            if (round.Error is null)
            {
                round.EndHead = await WorkReviewGit.HeadAsync(record.TopLevel, ct);
                round.EndBranch = await WorkReviewGit.BranchAsync(record.TopLevel, ct);
                var photo = await WorkReviewGit.SnapshotAsync(
                    record.TopLevel, WorkReviewGit.RefName(record.ReviewId, round.Round, "end"), SnapshotTimeout, ct);
                round.End = photo.Commit;
                if (photo.Failure is not null)
                    _log($"Review {record.ReviewId} round {round.Round}: snapshot failed ({photo.Failure})");
                if (round.End is null
                    || await WorkReviewGit.ChangesAsync(record.TopLevel, round.Start!, round.End, ct) is not { } changed)
                    round.Error = ReviewErrors.SnapshotFailed;
                else
                {
                    files = changed;
                    commits = await WorkReviewGit.CommitsAsync(record.TopLevel, round.StartHead, round.EndHead, ct) ?? [];
                }
            }

            if (dropIfEmpty && round.Error is null && files.Count == 0)
            {
                record.Rounds.Remove(round);
                if (record.Rounds.Count == 0)
                    File.Delete(RecordPath(record.ReviewId));
                else
                    Save(record);
                foreach (var side in new[] { "start", "end" })
                    await WorkReviewGit.DeleteRefAsync(record.TopLevel, WorkReviewGit.RefName(record.ReviewId, round.Round, side), ct);
                _log($"Review {record.ReviewId} round {round.Round} ({trigger}) changed nothing: dropped");
                return new EndResult(null, ReviewErrors.NoChanges);
            }

            round.Overlapping |= _activity.Overlaps(record.ReviewId, round.Round, round.StartedAt, round.EndedAt.Value);
            Save(record);
            // A round made from the base was never in the folder's activity: it ran from the base to now.
            if (open.FromBase)
                _activity.Append(new WorkReviewActivity.Entry(
                    record.ReviewId, round.Round, _agentKey, WorkReviewActivity.Start, round.StartedAt));
            _activity.Append(new WorkReviewActivity.Entry(
                record.ReviewId, round.Round, _agentKey, WorkReviewActivity.End, round.EndedAt.Value));
            // The terminal's next round asked from it starts where this one ended.
            if (round.End is { } end && round.Error is null)
                await MoveBaseAsync(ptySessionId, record.TopLevel, open.Cwd, end, round.EndHead, round.EndBranch, round.EndedAt.Value, ct);

            var sessions = CliSessions(ptySessionId);
            var last = sessions.Count > 0 ? sessions[^1] : (KeyValuePair<string, string>?)null;
            report = new ReviewChangesMessage
            {
                ReviewId = record.ReviewId,
                Round = round.Round,
                Kind = round.Kind,
                PtySessionId = ptySessionId,
                RunId = round.RunId,
                TaskId = round.TaskId,
                Provider = last?.Value,
                CliSessionId = last?.Key,
                StartedAt = round.StartedAt,
                EndedAt = round.EndedAt.Value,
                StartBranch = round.StartBranch,
                StartHead = round.StartHead,
                EndBranch = round.EndBranch,
                EndHead = round.EndHead,
                Files = files.Take(WorkReviewGit.MaxFiles).ToList(),
                FilesTruncated = files.Count > WorkReviewGit.MaxFiles,
                FilesChanged = files.Count,
                Insertions = files.Sum(f => f.Insertions ?? 0),
                Deletions = files.Sum(f => f.Deletions ?? 0),
                Commits = commits.Take(WorkReviewGit.MaxCommits).ToList(),
                CommitsTruncated = commits.Count > WorkReviewGit.MaxCommits,
                Summary = Summary(ptySessionId, open.Cwd, sessions, round.StartedAt),
                Overlapping = round.Overlapping,
                Error = round.Error,
                Captures = KeepCaptures(record.ReviewId, round.Round, ptySessionId, open.Cwd, sessions, round.StartedAt),
            };
        }
        finally
        {
            _lock.Release();
        }

        _log($"Review {report.ReviewId} round {report.Round} ended ({trigger}): " + (report.Error ??
            $"files={report.FilesChanged}, +{report.Insertions} -{report.Deletions}, commits={report.Commits.Count}" +
            (report.Overlapping ? ", overlapping" : "")));
        await SendOrKeepAsync(report, ct);
        await PurgeIfDueAsync(ct);
        return new EndResult(report.Round, report.Error ?? (report.FilesChanged == 0 ? ReviewErrors.NoChanges : null));
    }

    /// <summary>The terminal's base is now <paramref name="commit"/>, kept by its ref.</summary>
    private async Task MoveBaseAsync(
        string ptySessionId, string topLevel, string cwd, string commit, string? head, string? branch, DateTimeOffset at, CancellationToken ct)
    {
        if (!await WorkReviewGit.SetRefAsync(topLevel, WorkReviewGit.SessionRefName(ptySessionId), commit, ct))
        {
            _log($"Session base of PTY {ptySessionId} could not move to {Short(commit)}");
            return;
        }
        _bases[ptySessionId] = new SessionBase(topLevel, cwd, commit, head, branch, at);
    }

    /// <summary>The terminal is gone: forget its CLI sessions and its base (after its round ended).</summary>
    public async Task ForgetTerminalAsync(string ptySessionId, CancellationToken ct)
    {
        _cliSessions.TryRemove(ptySessionId, out _);
        // A base being taken is dropped as soon as its photo is done.
        _basing.TryUpdate(ptySessionId, Dropped, Taking);
        if (_bases.TryRemove(ptySessionId, out var @base))
        {
            try
            {
                await WorkReviewGit.DeleteRefAsync(@base.TopLevel, WorkReviewGit.SessionRefName(ptySessionId), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Session base of PTY {ptySessionId} could not be deleted: {ex.Message}");
            }
        }
    }

    /// <summary>The diff of one round of a review, read from its photos.</summary>
    public async Task<ReviewDiffMessage> DiffAsync(string requestId, Guid reviewId, int round, string? path, CancellationToken ct)
    {
        ReviewDiffMessage Answer(string? diff = null, bool truncated = false, string? error = null) => new()
        {
            RequestId = requestId, ReviewId = reviewId, Round = round, Path = path,
            Diff = diff, Truncated = truncated, Error = error,
        };

        if (path is not null && !WorkReviewGit.IsValidPath(path))
            return Answer(error: ReviewDiffErrors.InvalidPath);

        var found = await GetRoundSnapshotsAsync(reviewId, round, ct);
        if (found.Snapshots is not { } snapshots)
            return Answer(error: found.Error);

        var diff = await WorkReviewGit.DiffAsync(snapshots.TopLevel, snapshots.Start, snapshots.End, path, ct);
        return Answer(diff.Text, diff.Truncated, diff.Error);
    }

    /// <summary>The repository and the photos of the start and of the end of a round.</summary>
    public sealed record RoundSnapshots(string TopLevel, string Start, string End);

    /// <summary>The photos of a round, or why there are none (<see cref="ReviewDiffErrors"/>, or the round's own
    /// <see cref="ReviewErrors"/>).</summary>
    public sealed record RoundSnapshotsResult(RoundSnapshots? Snapshots, string? Error);

    /// <summary>
    /// The photos of one round of a review, still in the repository: what its diff (and a peer review of it) is read
    /// from. Unknown review or round, round in error, still going on, or photos purged: the reason instead.
    /// </summary>
    public async Task<RoundSnapshotsResult> GetRoundSnapshotsAsync(Guid reviewId, int round, CancellationToken ct)
    {
        static RoundSnapshotsResult Error(string error) => new(null, error);

        await _lock.WaitAsync(ct);
        try
        {
            if (Load(reviewId) is not { } record)
                return Error(ReviewDiffErrors.UnknownReview);
            if (record.Rounds.FirstOrDefault(r => r.Round == round) is not { } found)
                return Error(ReviewDiffErrors.UnknownRound);
            if (found.Error is not null)
                return Error(found.Error);
            if (found.Start is null || found.End is null)
                return Error(ReviewDiffErrors.RoundOpen);
            if (!await WorkReviewGit.ExistsAsync(record.TopLevel, found.Start, ct)
                || !await WorkReviewGit.ExistsAsync(record.TopLevel, found.End, ct))
                return Error(ReviewDiffErrors.Purged);
            return new RoundSnapshotsResult(new RoundSnapshots(record.TopLevel, found.Start, found.End), null);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sends the reports kept while disconnected; stops at the first failure.</summary>
    public async Task ReplayPendingAsync(CancellationToken ct)
    {
        foreach (var report in _pending.LoadAll(_log))
        {
            if (!await _trySend(report, ct))
                return;
            _pending.Delete(report);
            _log($"Replayed pending review changes {report.ReviewId} round {report.Round}");
        }
    }

    /// <summary>Deletes the reviews (record and photos) whose last round is older than <see cref="Retention"/>, at most
    /// once a day.</summary>
    public async Task PurgeIfDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPurge < PurgeInterval)
            return;
        _lastPurge = now;
        await PurgeAsync(now - Retention, ct);
    }

    public async Task PurgeAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        if (!Directory.Exists(_directory))
            return;
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var reviewId)
                    || Load(reviewId) is not { } record)
                    continue;
                var lastActivity = record.Rounds.Select(r => r.EndedAt ?? r.StartedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
                if (lastActivity >= cutoff || record.Rounds.Any(r => _open.Values.Any(o => o.ReviewId == reviewId && o.Round == r.Round)))
                    continue;
                if (Directory.Exists(record.TopLevel))
                    await WorkReviewGit.DeleteRefsAsync(record.TopLevel, reviewId, ct);
                File.Delete(file);
                if (Directory.Exists(CapturesFolder(reviewId)))
                    Directory.Delete(CapturesFolder(reviewId), recursive: true);
                _log($"Review {reviewId} purged (last round {lastActivity:yyyy-MM-dd})");
            }
            _activity.Prune(DateTimeOffset.UtcNow - ActivityRetention);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Review purge failed: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// At start: the rounds this agent left open when it stopped (its terminals died with it) are closed, and the
    /// backend is told (<see cref="ReviewErrors.Interrupted"/>) at the next connection.
    /// </summary>
    private void CloseInterruptedRounds()
    {
        try
        {
            if (!Directory.Exists(_directory))
                return;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var reviewId)
                    || Load(reviewId) is not { } record)
                    continue;
                var interrupted = record.Rounds.Where(r => r.EndedAt is null && r.Agent == _agentKey).ToList();
                if (interrupted.Count == 0)
                    continue;
                var now = DateTimeOffset.UtcNow;
                foreach (var round in interrupted)
                {
                    round.EndedAt = now;
                    round.Error ??= ReviewErrors.Interrupted;
                    TryAppendActivity(reviewId, round.Round, WorkReviewActivity.End, now);
                    _pending.Save(new ReviewChangesMessage
                    {
                        ReviewId = reviewId, Round = round.Round, Kind = round.Kind, PtySessionId = round.PtySessionId,
                        RunId = round.RunId, TaskId = round.TaskId, StartedAt = round.StartedAt, EndedAt = now,
                        StartBranch = round.StartBranch, StartHead = round.StartHead, Error = ReviewErrors.Interrupted,
                    });
                    _log($"Review {reviewId} round {round.Round} was interrupted by an agent restart");
                }
                Save(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"Could not close the interrupted review rounds: {ex.Message}");
        }
    }

    private void TryAppendActivity(Guid reviewId, int round, string @event, DateTimeOffset at)
    {
        try { _activity.Append(new WorkReviewActivity.Entry(reviewId, round, _agentKey, @event, at)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Could not write the review activity: {ex.Message}");
        }
    }

    private string? Summary(string ptySessionId, string cwd, IReadOnlyList<KeyValuePair<string, string>> sessions, DateTimeOffset since)
    {
        try
        {
            if (_readFinalMessage(ptySessionId, cwd, sessions, since) is not { } text)
                return null;
            return text.Length <= MaxSummaryLength ? text : RunUsageCollector.Truncate(text, MaxSummaryLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"Could not read the last message of PTY {ptySessionId}: {ex.Message}");
            return null;
        }
    }

    private List<KeyValuePair<string, string>> CliSessions(string ptySessionId)
    {
        if (!_cliSessions.TryGetValue(ptySessionId, out var sessions))
            return [];
        lock (sessions)
            return [.. sessions];
    }

    private async Task SendOrKeepAsync(ReviewChangesMessage report, CancellationToken ct)
    {
        if (await _trySend(report, ct))
        {
            _pending.Delete(report);
            return;
        }
        _pending.Save(report);
        _log($"Backend unreachable; kept review changes {report.ReviewId} round {report.Round} in {_pending.Directory}");
    }

    /// <summary>The images of a review's rounds (doc 70), purged with its record.</summary>
    private string CapturesFolder(Guid reviewId) => Path.Combine(_directory, $"{reviewId:N}");

    /// <summary>
    /// Copies the last images the CLI saw during the round next to the review's record (doc 70): a screenshot file is
    /// often overwritten or deleted soon after, the review is read days later. Same image twice kept once. Null when
    /// there is none, or it could not be done (logged, never in the way of the report).
    /// </summary>
    private IReadOnlyList<ReviewCapture>? KeepCaptures(
        Guid reviewId, int round, string ptySessionId, string cwd, IReadOnlyList<KeyValuePair<string, string>> sessions, DateTimeOffset since)
    {
        if (_readCaptures is null || sessions.Count == 0)
            return null;
        try
        {
            var seen = _readCaptures(ptySessionId, cwd, sessions, since);
            var kept = new List<(SeenImage Seen, byte[] Bytes)>();
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            for (var i = seen.Count - 1; i >= 0 && kept.Count < MaxCaptures; i--)
            {
                if (ToolImages.Load(seen[i].Image).Bytes is not { } bytes || total + bytes.Length > MaxCapturesBytes
                    || !hashes.Add(Convert.ToHexString(SHA256.HashData(bytes))))
                    continue;
                total += bytes.Length;
                kept.Add((seen[i], bytes));
            }
            if (kept.Count == 0)
                return null;
            kept.Reverse();
            var folder = CapturesFolder(reviewId);
            Directory.CreateDirectory(folder);
            var captures = new List<ReviewCapture>(kept.Count);
            foreach (var (image, bytes) in kept)
            {
                var name = $"r{round}-{captures.Count + 1}{Path.GetExtension(image.Image.Name)}";
                File.WriteAllBytes(Path.Combine(folder, name), bytes);
                captures.Add(new ReviewCapture
                {
                    Name = name, At = image.Image.At, Tool = image.Tool, Title = image.Title, Size = bytes.Length,
                });
            }
            _log($"Review {reviewId} round {round}: {captures.Count} capture(s) kept ({total / 1024} KB)");
            return captures;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"Review {reviewId} round {round}: captures not kept ({ex.Message})");
            return null;
        }
    }

    private static readonly Regex CaptureName = new(@"^r\d{1,4}-\d{1,3}\.(?:png|jpg|webp)$", RegexOptions.CultureInvariant);

    /// <summary>One image of a round, kept when it ended (<see cref="KeepCaptures"/>), as base64.</summary>
    public async Task<ReviewCaptureMessage> CaptureAsync(string requestId, Guid reviewId, string image, CancellationToken ct)
    {
        ReviewCaptureMessage Answer(string? data = null, string? error = null) => new()
        {
            RequestId = requestId, ReviewId = reviewId, Image = image, Data = data, Error = error,
            MimeType = data is null ? null : ToolImages.MediaTypeOf(image),
        };
        if (!CaptureName.IsMatch(image))
            return Answer(error: "invalid");
        try
        {
            var path = Path.Combine(CapturesFolder(reviewId), image);
            if (!File.Exists(path) || new FileInfo(path).Length > ToolImages.MaxBytes)
                return Answer(error: ReviewDiffErrors.Purged);
            return Answer(Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Answer(error: ReviewDiffErrors.Purged);
        }
    }

    private string RecordPath(Guid reviewId) => Path.Combine(_directory, $"{reviewId:N}.json");

    private WorkReviewRecord? Load(Guid reviewId)
    {
        var path = RecordPath(reviewId);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WorkReviewRecord>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log($"Unreadable review record {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private void Save(WorkReviewRecord record)
    {
        PrivateFiles.CreateDirectory(_directory);
        var path = RecordPath(record.ReviewId);
        var tmp = path + ".tmp";
        PrivateFiles.WriteAllText(tmp, JsonSerializer.Serialize(record, RecordJson));
        File.Move(tmp, path, overwrite: true);
    }

    private static bool IsWithin(string topLevel, string path)
    {
        var relative = Path.GetRelativePath(topLevel, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static string Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha ?? "?";

    private sealed class OpenRound(Guid reviewId, int round, string topLevel, string cwd)
    {
        public Guid ReviewId { get; } = reviewId;
        public int Round { get; } = round;
        public string TopLevel { get; } = topLevel;
        public string Cwd { get; } = cwd;
        /// <summary>Started at the terminal's base, on request: not in the folder's activity yet.</summary>
        public bool FromBase { get; init; }
        public volatile bool OtherCliBusy;
    }

    /// <summary>The photo the next round asked from a terminal starts from, with what it was taken on.</summary>
    private sealed record SessionBase(string TopLevel, string Cwd, string Commit, string? Head, string? Branch, DateTimeOffset At);
}

/// <summary>What the agent keeps about a review on this machine (<c>.sidehub/run/reviews/{reviewId:N}.json</c>).</summary>
public sealed class WorkReviewRecord
{
    public Guid ReviewId { get; set; }
    /// <summary>The repository the photos are in.</summary>
    public string TopLevel { get; set; } = "";
    public List<WorkReviewRound> Rounds { get; set; } = [];
}

public sealed class WorkReviewRound
{
    public int Round { get; set; }
    public string Kind { get; set; } = ReviewKinds.Task;
    public string PtySessionId { get; set; } = "";
    /// <summary>The agent of the round's terminal (several agents share the folder).</summary>
    public string Agent { get; set; } = "";
    public Guid? RunId { get; set; }
    public Guid? TaskId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Photos (snapshot commits) of the start and of the end.</summary>
    public string? Start { get; set; }
    public string? End { get; set; }
    public string? StartHead { get; set; }
    public string? StartBranch { get; set; }
    public string? EndHead { get; set; }
    public string? EndBranch { get; set; }
    public bool Overlapping { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// <c>review.changes</c> reports that could not be sent, one file per round (<c>{reviewId:N}-{round}.json</c>),
/// replayed at the next connection.
/// </summary>
public sealed class PendingReviewChangesStore(string directory) : PendingReportStore<ReviewChangesMessage>(directory)
{
    public void Delete(ReviewChangesMessage report) => Delete(KeyOf(report));

    protected override string KeyOf(ReviewChangesMessage report) => $"{report.ReviewId:N}-{report.Round}";
}

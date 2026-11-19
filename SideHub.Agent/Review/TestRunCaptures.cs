using System.Text.Json;
using SideHub.Agent.Conversation;
using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent.Review;

/// <summary>
/// The images the CLI of a run of tests saw (doc 73): the browser as the agent saw it when it said a step passed or
/// failed. Kept when the run ends, like those of a review round (<see cref="KeptCaptures"/>), in
/// <c>.sidehub/run/tests/{runId:N}/</c>; their names are sent as <c>run.captures</c> (kept until the backend is back),
/// each image is read on demand (<c>run.capture.request</c>).
/// </summary>
public sealed class TestRunCaptures
{
    /// <summary><c>SIDEHUB_RUN_KIND</c> of a run of a test execution.</summary>
    public const string RunKind = "test";

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);

    private readonly string _directory;
    private readonly PendingRunCapturesStore _pending;
    private readonly Func<RunCapturesMessage, CancellationToken, Task<bool>> _trySend;
    private readonly WorkReviewTracker.CaptureReader _readCaptures;
    private readonly Action<string> _log;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    /// <param name="directory">The agents' shared <c>.sidehub/run/tests</c> folder.</param>
    /// <param name="trySend">Sends a report; false when the backend is unreachable.</param>
    public TestRunCaptures(
        string directory, PendingRunCapturesStore pending, Func<RunCapturesMessage, CancellationToken, Task<bool>> trySend,
        WorkReviewTracker.CaptureReader readCaptures, Action<string> log)
    {
        _directory = directory;
        _pending = pending;
        _trySend = trySend;
        _readCaptures = readCaptures;
        _log = log;
    }

    private string Folder(Guid runId) => Path.Combine(_directory, $"{runId:N}");

    /// <summary>
    /// The run ended: copies the last images its CLIs saw since it started and names them to the backend. A run of
    /// scripts starts no CLI: nothing is kept, nothing is sent. Never throws for images that cannot be read or copied.
    /// </summary>
    /// <param name="cliSessions">cliSessionId → provider, as announced in the terminal of the run.</param>
    public async Task KeepAsync(
        Guid runId, string ptySessionId, string cwd, IReadOnlyList<KeyValuePair<string, string>> cliSessions,
        DateTimeOffset since, CancellationToken ct)
    {
        if (cliSessions.Count == 0)
            return;
        IReadOnlyList<ReviewCapture> captures;
        try
        {
            captures = KeptCaptures.Keep(Folder(runId), round: 1, _readCaptures(ptySessionId, cwd, cliSessions, since));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"Test run {runId}: captures not kept ({ex.Message})");
            return;
        }
        if (captures.Count == 0)
            return;
        _log($"Test run {runId}: {captures.Count} capture(s) kept ({captures.Sum(c => c.Size) / 1024} KB)");

        var report = new RunCapturesMessage { RunId = runId, Captures = captures };
        if (!await _trySend(report, ct))
        {
            _pending.Save(report);
            _log($"Backend unreachable; kept the captures of test run {runId} in {_pending.Directory}");
        }
        PurgeIfDue();
    }

    /// <summary>Sends the reports kept while disconnected; stops at the first failure.</summary>
    public async Task ReplayPendingAsync(CancellationToken ct)
    {
        foreach (var report in _pending.LoadAll(_log))
        {
            if (!await _trySend(report, ct))
                return;
            _pending.Delete(report.RunId);
            _log($"Replayed the pending captures of test run {report.RunId}");
        }
    }

    /// <summary>One image of a run, kept when it ended (<see cref="KeepAsync"/>), as base64.</summary>
    public async Task<RunCaptureMessage> CaptureAsync(string requestId, Guid runId, string image, CancellationToken ct)
    {
        var (data, error) = await KeptCaptures.ReadAsync(Folder(runId), image, ct);
        return new RunCaptureMessage
        {
            RequestId = requestId, RunId = runId, Image = image, Data = data, Error = error,
            MimeType = data is null ? null : ToolImages.MediaTypeOf(image),
        };
    }

    /// <summary>Deletes the images of the runs written more than <see cref="WorkReviewTracker.Retention"/> ago, at most
    /// once a day.</summary>
    public void PurgeIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPurge < PurgeInterval)
            return;
        _lastPurge = now;
        Purge(now - WorkReviewTracker.Retention);
    }

    public void Purge(DateTimeOffset cutoff)
    {
        try
        {
            if (!Directory.Exists(_directory))
                return;
            foreach (var folder in new DirectoryInfo(_directory).EnumerateDirectories())
            {
                var written = folder.LastWriteTimeUtc;
                if (!Guid.TryParseExact(folder.Name, "N", out var runId) || written >= cutoff.UtcDateTime)
                    continue;
                folder.Delete(recursive: true);
                _log($"Test run {runId}: captures purged (kept {written:yyyy-MM-dd})");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Test run captures purge failed: {ex.Message}");
        }
    }
}

/// <summary>
/// <c>run.captures</c> reports that could not be sent, one file per run (<c>{runId}.json</c>), replayed at the next
/// connection.
/// </summary>
public sealed class PendingRunCapturesStore(string directory) : PendingReportStore<RunCapturesMessage>(directory)
{
    public void Delete(Guid runId) => Delete(runId.ToString());

    protected override string KeyOf(RunCapturesMessage report) => report.RunId.ToString();
}

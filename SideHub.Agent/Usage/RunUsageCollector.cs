using System.Collections.Concurrent;
using SideHub.Agent.Models;

namespace SideHub.Agent.Usage;

/// <summary>
/// Measures the token usage of SideHub-launched runs (<c>run-{runId:N}</c> PTYs) and reports it as
/// <c>run.usage</c>. A run is harvested when its step ends (the CLI may stay open) and again when its
/// PTY exits or is stopped; each report replaces the previous one on the backend. Reports that cannot be
/// sent are kept in a <see cref="PendingUsageStore"/> and replayed at the next connection.
/// <para>A question or peer review run also reports its answer (<c>run.answer</c>, the CLI's last message) once, when
/// its PTY is gone, kept in a <see cref="PendingRunAnswerStore"/> the same way.</para>
/// </summary>
public sealed class RunUsageCollector
{
    private const string RunPtyPrefix = "run-";
    // A launch outside any tracked run only matters to disambiguate runs started around the same time.
    private static readonly TimeSpan LaunchRetention = TimeSpan.FromHours(24);
    // What the FIFO announces is untrusted: a terminal flooding it must not grow these lists without bound.
    private const int MaxCliSessionsPerRun = 64;
    private const int MaxLaunchesPerPty = 64;
    // An answer is read by a person in the backend; past this, the rest is cut.
    public const int MaxAnswerLength = 20_000;
    public const string TruncationMarker = "\n\n[truncated]";

    private readonly IReadOnlyDictionary<string, IUsageHarvester> _harvestersByProvider;
    private readonly IUsageHarvester _unavailable = new NullHarvester();
    private readonly PendingUsageStore _pending;
    private readonly Func<RunUsageMessage, CancellationToken, Task<bool>> _trySend;
    private readonly PendingRunAnswerStore _pendingAnswers;
    private readonly Func<RunAnswerMessage, CancellationToken, Task<bool>> _trySendAnswer;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, TrackedRun> _runs = new();
    // CLI launches in every PTY (runs and interactive terminals), guarded by itself.
    private readonly List<CliLaunch> _launches = [];
    // Harvests of one run can race (step end vs exit); serializing them keeps the latest report last.
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="harvestersByProvider">Keyed by the CLI provider reported by the wrappers ("claude", "codex").</param>
    /// <param name="trySend">Sends a report; false when the backend is unreachable.</param>
    /// <param name="trySendAnswer">Sends a question run's answer; false when the backend is unreachable.</param>
    public RunUsageCollector(
        IReadOnlyDictionary<string, IUsageHarvester> harvestersByProvider,
        PendingUsageStore pending,
        Func<RunUsageMessage, CancellationToken, Task<bool>> trySend,
        PendingRunAnswerStore pendingAnswers,
        Func<RunAnswerMessage, CancellationToken, Task<bool>> trySendAnswer,
        Action<string> log)
    {
        _harvestersByProvider = new Dictionary<string, IUsageHarvester>(harvestersByProvider, StringComparer.OrdinalIgnoreCase);
        _pending = pending;
        _trySend = trySend;
        _pendingAnswers = pendingAnswers;
        _trySendAnswer = trySendAnswer;
        _log = log;
    }

    /// <summary>
    /// The run id of a backend-launched run: <c>SIDEHUB_RUN_ID</c> from the pty.start env, else the
    /// <c>run-{runId:N}</c> PTY id. Null for any other PTY (interactive terminals are not measured).
    /// </summary>
    public static Guid? ResolveRunId(string ptySessionId, IReadOnlyDictionary<string, string>? additionalEnv)
    {
        if (!ptySessionId.StartsWith(RunPtyPrefix, StringComparison.Ordinal))
            return null;
        if (additionalEnv is not null
            && additionalEnv.TryGetValue("SIDEHUB_RUN_ID", out var envId)
            && Guid.TryParse(envId, out var fromEnv))
            return fromEnv;
        return Guid.TryParse(ptySessionId[RunPtyPrefix.Length..], out var fromId) ? fromId : null;
    }

    /// <param name="question">The checkout of a question or peer review run, whose answer is reported when it ends.</param>
    public void TrackRun(string ptySessionId, Guid runId, string cwd, PreparedCheckout? question = null) =>
        _runs[ptySessionId] = new TrackedRun(ptySessionId, runId, cwd, question);

    public bool IsTracked(string ptySessionId) => _runs.ContainsKey(ptySessionId);

    public void RecordCliSession(string ptySessionId, string provider, string cliSessionId)
    {
        if (!_runs.TryGetValue(ptySessionId, out var run))
            return;
        if (run.CliSessions.Count >= MaxCliSessionsPerRun && !run.CliSessions.ContainsKey(cliSessionId))
        {
            _log($"Run {run.RunId} already has {MaxCliSessionsPerRun} CLI sessions; {cliSessionId} ignored");
            return;
        }
        run.CliSessions[cliSessionId] = provider;
    }

    /// <summary>
    /// Records a CLI started in any PTY. A run's own launches tell which CLI it ran; the others let the
    /// harvester detect a session that could belong to another PTY.
    /// </summary>
    public void RecordCliLaunch(
        string ptySessionId, string provider, string cwd, DateTimeOffset at, LaunchObservation? observation = null)
    {
        lock (_launches)
        {
            _launches.RemoveAll(l => l.At < at - LaunchRetention && !_runs.ContainsKey(l.PtySessionId));
            if (_launches.Count(l => l.PtySessionId == ptySessionId) >= MaxLaunchesPerPty)
            {
                _log($"PTY {ptySessionId} already has {MaxLaunchesPerPty} CLI launches; one more ignored");
                return;
            }
            _launches.Add(new CliLaunch(ptySessionId, provider, cwd, at, observation));
        }
    }

    /// <param name="final">The PTY is gone: stop tracking the run after this report.</param>
    public async Task HarvestAsync(string ptySessionId, string trigger, bool final, CancellationToken ct)
    {
        var found = final ? _runs.TryRemove(ptySessionId, out var run) : _runs.TryGetValue(ptySessionId, out run);
        if (!found || run is null)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            try
            {
                var report = await Task.Run(() => BuildReport(run), ct);
                var tokens = report.Models.Sum(m => m.InputTokens + m.OutputTokens + m.CacheReadTokens + m.CacheWriteTokens);
                _log($"Run {run.RunId} usage ({trigger}): source={report.Source}, models={report.Models.Count}, tokens={tokens}");
                await SendOrKeepAsync(report, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Usage harvest failed for run {run.RunId} ({trigger}): {ex.Message}");
            }

            // Once, when the run is over: the final harvest is the last one (the run is no longer tracked).
            if (final && run.Question is { } question)
                await ReportAnswerAsync(run, question, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sends the reports kept while disconnected; stops at the first failure.</summary>
    public async Task ReplayPendingAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var report in _pending.LoadAll(_log))
            {
                if (!await _trySend(report, ct))
                    return;
                _pending.Delete(report.RunId);
                _log($"Replayed pending usage for run {report.RunId}");
            }
            foreach (var answer in _pendingAnswers.LoadAll(_log))
            {
                if (!await _trySendAnswer(answer, ct))
                    return;
                _pendingAnswers.Delete(answer.RunId);
                _log($"Replayed pending answer for run {answer.RunId}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private RunUsageMessage BuildReport(TrackedRun run)
    {
        foreach (var (harvester, context) in Contexts(run))
        {
            if (harvester.Harvest(context) is { } models)
                return Report(run.RunId, harvester.Source, models);
        }
        return Report(run.RunId, _unavailable.Source, []);
    }

    /// <summary>The harvester of each CLI the run started, with what it needs to find the run's sessions.</summary>
    /// <summary>
    /// The limit a tracked run's CLI wrote in its own files that it reached (codex, see <see cref="ICliLimitReader"/>);
    /// null for none or an unknown run. Read before the run's final harvest, which stops tracking it.
    /// </summary>
    public CliLimitReport? ReadRunLimit(string ptySessionId)
    {
        if (!_runs.TryGetValue(ptySessionId, out var run))
            return null;
        foreach (var (harvester, context) in Contexts(run))
        {
            if (harvester is ICliLimitReader reader && reader.ReadLimit(context) is { } limit)
                return limit;
        }
        return null;
    }

    /// <summary>The CLIs a tracked run started (as announced in its terminal), empty for an unknown run.</summary>
    public IReadOnlyList<string> RunProviders(string ptySessionId)
    {
        if (!_runs.TryGetValue(ptySessionId, out var run))
            return [];
        lock (_launches)
            return run.CliSessions.Values
                .Concat(_launches.Where(l => l.PtySessionId == ptySessionId).Select(l => l.Provider))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private IEnumerable<(IUsageHarvester Harvester, RunUsageContext Context)> Contexts(TrackedRun run) =>
        Contexts(run.PtySessionId, run.RunId, run.Cwd, run.CliSessions.ToArray(), run.StartedAt);

    /// <summary>
    /// The last message of the CLIs started in a terminal since <paramref name="since"/>, run or not: what a work
    /// review shows as the agent's own summary. Null when no transcript holds one.
    /// </summary>
    /// <param name="cliSessions">cliSessionId → provider, as announced in the terminal.</param>
    public string? ReadFinalMessage(
        string ptySessionId, string cwd, IReadOnlyList<KeyValuePair<string, string>> cliSessions, DateTimeOffset since)
    {
        foreach (var (harvester, context) in Contexts(ptySessionId, Guid.Empty, cwd, cliSessions, since))
        {
            if (harvester is IFinalMessageReader reader && reader.ReadFinalMessage(context) is { } text)
                return text;
        }
        return null;
    }

    private IEnumerable<(IUsageHarvester Harvester, RunUsageContext Context)> Contexts(
        string ptySessionId, Guid runId, string cwd, IReadOnlyList<KeyValuePair<string, string>> sessions,
        DateTimeOffset startedAt)
    {
        CliLaunch[] launches;
        lock (_launches)
            launches = _launches.ToArray();
        bool IsOwn(CliLaunch l) => l.PtySessionId == ptySessionId;

        var providers = sessions.Select(s => s.Value)
            .Concat(launches.Where(IsOwn).Select(l => l.Provider))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (!_harvestersByProvider.TryGetValue(provider, out var harvester))
                continue;
            var ids = sessions
                .Where(s => string.Equals(s.Value, provider, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Key)
                .ToList();
            var sameCli = launches
                .Where(l => string.Equals(l.Provider, provider, StringComparison.OrdinalIgnoreCase))
                .ToList();
            yield return (harvester, new RunUsageContext(
                runId, cwd, ids, sameCli.Where(IsOwn).ToList(), sameCli.Where(l => !IsOwn(l)).ToList(), startedAt));
        }
    }

    private async Task ReportAnswerAsync(TrackedRun run, PreparedCheckout question, CancellationToken ct)
    {
        try
        {
            var answer = await Task.Run(() => BuildAnswer(run, question), ct);
            _log($"Run {run.RunId} answer: {(answer.Text is { } text ? $"{text.Length} chars" : answer.Error)}");
            if (await _trySendAnswer(answer, ct))
                return;
            _pendingAnswers.Save(answer);
            _log($"Backend unreachable; kept the answer of run {run.RunId} in {_pendingAnswers.Directory}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Answer report failed for run {run.RunId}: {ex.Message}");
        }
    }

    private RunAnswerMessage BuildAnswer(TrackedRun run, PreparedCheckout question)
    {
        string? text = null;
        var cliFound = false;
        foreach (var (harvester, context) in Contexts(run))
        {
            if (harvester is not IFinalMessageReader reader)
                continue;
            cliFound = true;
            if ((text = reader.ReadFinalMessage(context)) is not null)
                break;
        }
        return new RunAnswerMessage
        {
            RunId = run.RunId,
            Text = text is null ? null : Truncate(text),
            CommitSha = question.CommitSha,
            CommitDate = question.CommitDate,
            CommitCount = question.CommitCount,
            Error = text is not null ? null : cliFound ? "no-final-message" : "no-cli-session",
        };
    }

    /// <summary>At most <see cref="MaxAnswerLength"/> characters (marker included), never splitting a surrogate pair.</summary>
    public static string Truncate(string text) => Truncate(text, MaxAnswerLength);

    /// <summary>At most <paramref name="maxLength"/> characters (marker included), never splitting a surrogate pair.</summary>
    public static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        var cut = maxLength - TruncationMarker.Length;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + TruncationMarker;
    }

    private async Task SendOrKeepAsync(RunUsageMessage report, CancellationToken ct)
    {
        if (await _trySend(report, ct))
        {
            // An older report kept while disconnected must not overwrite this one at the next replay.
            _pending.Delete(report.RunId);
            return;
        }

        _pending.Save(report);
        _log($"Backend unreachable; kept usage for run {report.RunId} in {_pending.Directory}");
    }

    private static RunUsageMessage Report(Guid runId, string source, IReadOnlyList<ModelUsageReport> models) => new()
    {
        RunId = runId,
        Source = source,
        CollectedAt = DateTimeOffset.UtcNow,
        Models = models,
    };

    private sealed record TrackedRun(string PtySessionId, Guid RunId, string Cwd, PreparedCheckout? Question)
    {
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        /// <summary>cliSessionId → provider, as announced by the CLI wrappers.</summary>
        public ConcurrentDictionary<string, string> CliSessions { get; } = new();
    }
}

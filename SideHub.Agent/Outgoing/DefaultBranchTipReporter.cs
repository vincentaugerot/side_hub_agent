using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent.Outgoing;

/// <summary>
/// Keeps the backend told where the repository's default branch is on <c>origin</c> (<c>git.default-branch</c>): its
/// tip and commit count, so the repository card is regenerated every N commits (side_hub doc 51). Fetches that branch
/// alone at most every <see cref="FetchInterval"/> (a machine where nobody fetches would never see new commits), reads
/// it at each connection and every <see cref="PollInterval"/>, and sends it when it changed. A failed fetch keeps the
/// last fetched state.
/// </summary>
public sealed class DefaultBranchTipReporter(
    string directory,
    Func<Task<string?>> defaultBranch,
    Func<GitDefaultBranchMessage, CancellationToken, Task<bool>> send,
    Action<string> log,
    TimeSpan? fetchInterval = null) : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan FetchInterval { get; } = fetchInterval ?? TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _polling;
    private DateTimeOffset? _fetchedAt;
    // The last state the backend received, as JSON: a state is only sent again when it differs.
    private string? _lastSent;

    /// <summary>Connected: sends the state now, whatever was sent before, then polls.</summary>
    public void Start(CancellationToken ct)
    {
        Stop();
        _lastSent = null;
        var polling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _polling = polling;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(PollInterval);
            try
            {
                do
                {
                    try { await RefreshAsync(polling.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log($"Default branch tip not read: {ex.Message}"); }
                }
                while (await timer.WaitForNextTickAsync(polling.Token));
            }
            catch (OperationCanceledException) { }
        });
    }

    public void Stop()
    {
        var polling = Interlocked.Exchange(ref _polling, null);
        if (polling is null)
            return;
        polling.Cancel();
        polling.Dispose();
    }

    /// <summary>Fetches the default branch when due, reads its tip and sends it when it changed.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (await defaultBranch() is not { } branch || !QuestionCheckout.IsValidBranch(branch))
                return;

            if (_fetchedAt is not { } fetchedAt || DateTimeOffset.UtcNow - fetchedAt >= FetchInterval)
            {
                // Tried once per interval, failed or not: an unreachable remote is not hammered every poll.
                _fetchedAt = DateTimeOffset.UtcNow;
                if (await OriginBranch.FetchAsync(directory, branch, FetchTimeout, ct) is { } error)
                    log($"git fetch origin {branch} failed ({error}); the default branch tip stays as last fetched");
            }

            if (await OriginBranch.ReadTipAsync(directory, branch, ct) is not { } tip)
                return;
            var state = new GitDefaultBranchMessage { Branch = branch, Sha = tip.Sha, CommitCount = tip.CommitCount };
            var json = JsonSerializer.Serialize(state);
            if (json == _lastSent)
                return;
            if (await send(state, ct))
            {
                _lastSent = json;
                log($"Default branch tip of {branch}: {tip.Sha[..Math.Min(12, tip.Sha.Length)]} ({tip.CommitCount} commits)");
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose() => Stop();
}

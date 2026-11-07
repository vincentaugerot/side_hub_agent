using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent.Outgoing;

/// <summary>
/// Keeps the backend told which commits of the agent's branch are not pushed (<c>git.outgoing</c>): at each connection,
/// every <see cref="PollInterval"/> when it changed (commits made or pushed by hand), after a task is done and after a
/// push. Pushes one at a time (<c>git.push.request</c>). The commits are read in <paramref name="place"/>'s folder: the
/// queue's worktree once there is one (doc 71), else the agent's folder.
/// </summary>
public sealed class OutgoingCommitsReporter(
    Func<(string Directory, bool QueueWorktree)> place,
    Func<GitOutgoingMessage, CancellationToken, Task<bool>> send,
    Action<string> log) : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _pushLock = new(1, 1);
    private CancellationTokenSource? _polling;
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
                    catch (Exception ex) when (ex is not OperationCanceledException) { log($"Outgoing commits not read: {ex.Message}"); }
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

    /// <summary>Reads the state and sends it when it changed since the last one the backend received.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var (directory, queueWorktree) = place();
            var state = await OutgoingCommitsReader.ReadAsync(directory, ct);
            if (queueWorktree)
                state = state with { WorkTree = directory };
            var json = JsonSerializer.Serialize(state);
            if (json == _lastSent)
                return;
            if (await send(state, ct))
                _lastSent = json;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Pushes <paramref name="head"/>, then sends the fresh state before the answer is returned: the backend
    /// handles messages in order and reads the state when the answer arrives.</summary>
    public async Task<GitPushMessage> PushAsync(string requestId, string head, CancellationToken ct)
    {
        await _pushLock.WaitAsync(ct);
        try
        {
            var result = await OutgoingCommitsReader.PushAsync(place().Directory, head, ct);
            log($"Push of {result.Branch ?? "HEAD"} to {result.Remote ?? "?"}: {result.Error ?? "pushed"}");
            try { await RefreshAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log($"Outgoing commits not read after the push: {ex.Message}"); }
            return new GitPushMessage { RequestId = requestId, Error = result.Error, Detail = result.Detail };
        }
        finally
        {
            _pushLock.Release();
        }
    }

    public void Dispose() => Stop();
}

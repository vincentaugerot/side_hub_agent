using System.Collections.Concurrent;
using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent;

/// <summary>
/// Keeps the backend told where each terminal works (<c>pty.git-place</c>): the repository its folder is in, the branch
/// checked out there, and whether that checkout is a linked worktree (the task queue's, doc 71) rather than the main
/// one. Read in the folder the PTY was started in. Sent after its <c>pty.started</c>, again at each connection, after
/// each turn of its CLI and every <see cref="PollInterval"/>, only when it changed (a branch switched by hand).
/// </summary>
public sealed class PtyGitPlaces(
    Func<IEnumerable<KeyValuePair<string, string>>> terminals,
    Func<PtyGitPlaceMessage, CancellationToken, Task<bool>> send,
    Action<string> log) : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The longest folder sent (a longer one is not sent) and branch (cut), in characters, as the backend takes them.</summary>
    public const int MaxDirectoryLength = 1024;
    public const int MaxBranchLength = 255;

    // The last place the backend received for each PTY, as JSON: a place is only sent again when it differs.
    private readonly ConcurrentDictionary<string, string> _sent = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _polling;

    /// <summary>Connected: the backend forgot every place, each live terminal's is sent again, then polled.</summary>
    public void Start(CancellationToken ct)
    {
        Stop();
        _sent.Clear();
        var polling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _polling = polling;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(PollInterval);
            try
            {
                do
                {
                    foreach (var (ptySessionId, directory) in terminals().ToList())
                    {
                        try { await RefreshAsync(ptySessionId, directory, polling.Token); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { log($"Git place of PTY {ptySessionId} not read: {ex.Message}"); }
                    }
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

    /// <summary>Reads where the PTY works and sends it when it differs from what the backend last received.</summary>
    public async Task RefreshAsync(string ptySessionId, string directory, CancellationToken ct)
    {
        var place = await ReadAsync(directory, ct);
        if (place is null)
            return;
        var message = new PtyGitPlaceMessage
        {
            PtySessionId = ptySessionId,
            Directory = place.Directory,
            Repository = place.Repository,
            Branch = place.Branch,
            Head = place.Head,
            Worktree = place.Worktree,
        };
        var json = JsonSerializer.Serialize(message);
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_sent.TryGetValue(ptySessionId, out var last) && last == json)
                return;
            if (await send(message, ct))
            {
                _sent[ptySessionId] = json;
                log($"Git place of PTY {ptySessionId}: {(place.Repository ? $"{place.Branch ?? $"detached at {place.Head}"}{(place.Worktree ? " (worktree)" : "")}" : "not a repository")} in {place.Directory}");
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>The PTY is gone: its place is forgotten.</summary>
    public void Forget(string ptySessionId) => _sent.TryRemove(ptySessionId, out _);

    public void Dispose() => Stop();

    /// <param name="Directory">The repository's top folder, or the terminal's folder when it is not in one.</param>
    /// <param name="Branch">Null when HEAD is detached (then <paramref name="Head"/> is its short sha) or outside a repository.</param>
    /// <param name="Worktree">The folder is a linked worktree (<c>git worktree add</c>), not the repository's main checkout.</param>
    public sealed record Place(string Directory, bool Repository, string? Branch, string? Head, bool Worktree);

    /// <summary>Where <paramref name="directory"/> stands in git; null when git could not answer (missing, timed out).</summary>
    public static async Task<Place?> ReadAsync(string directory, CancellationToken ct)
    {
        // A folder too long for the backend is not sent rather than sent cut (it would name another folder)
        if (!System.IO.Directory.Exists(directory) || directory.Length > MaxDirectoryLength)
            return null;
        var dirs = await GitCommand.RunAsync(directory, QueryTimeout,
            ["rev-parse", "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir"], ct: ct);
        if (dirs is null)
            return null;
        if (!dirs.Succeeded
            || dirs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is not [var top, var gitDir, var commonDir])
            return new Place(directory, false, null, null, false);

        if (top.Length > MaxDirectoryLength)
            return null;
        var worktree = !SamePath(gitDir, commonDir);
        var symbolic = await GitCommand.RunAsync(directory, QueryTimeout, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct: ct);
        if (symbolic is null)
            return null;
        string? branch = null, head = null;
        if (symbolic.Succeeded && symbolic.Output.Trim() is { Length: > 0 } name)
            branch = Clip(name, MaxBranchLength);
        else
        {
            var sha = await GitCommand.RunAsync(directory, QueryTimeout, ["rev-parse", "--verify", "--quiet", "--short", "HEAD"], ct: ct);
            if (sha is null)
                return null;
            head = sha.Succeeded && sha.Output.Trim() is { Length: > 0 } s ? s : null;
        }
        return new Place(top, true, branch, head, worktree);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return a == b;
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}

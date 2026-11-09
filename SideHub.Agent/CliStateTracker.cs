using System.Collections.Concurrent;
using SideHub.Agent.Models;

namespace SideHub.Agent;

/// <summary>What a coding CLI is doing in a terminal, as its hooks report it (<c>sidehub-cli cli-state</c>).</summary>
public static class CliStates
{
    /// <summary>A turn is in progress: the CLI is thinking or running tools.</summary>
    public const string Working = "working";

    /// <summary>The CLI needs a human: a permission prompt or a question it asked.</summary>
    public const string WaitingInput = "waiting-input";

    /// <summary>The turn is over.</summary>
    public const string Idle = "idle";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Working, WaitingInput, Idle };
}

/// <summary>A CLI's last reported state in a PTY, and when the agent learned it (UTC). <paramref name="Prompt"/>: what
/// it asks while waiting-input, when its hook said.</summary>
public sealed record PtyCliState(string Provider, string State, string? CliSessionId, DateTime At, PtyCliPrompt? Prompt = null)
{
    public PtyCliStateMessage Message(string ptySessionId) => new()
    {
        PtySessionId = ptySessionId,
        CliSessionId = CliSessionId,
        Provider = Provider,
        State = State,
        At = At,
        Prompt = Prompt,
    };
}

/// <summary>
/// The latest CLI state of each PTY, reported to the backend as <c>pty.cli-state</c>: a report that repeats the
/// last one (same state, same session) is not sent again, unless it brings what a waiting CLI asks (Claude reports
/// waiting-input twice for a permission: its PermissionRequest hook says what, its Notification hook does not); the
/// latest state is replayed after a backend reconnect, and forgotten when the CLI or the PTY ends.
/// </summary>
public sealed class CliStateTracker
{
    private readonly ConcurrentDictionary<string, PtyCliState> _states = new();
    // PTYs whose queued task said `task done` during the turn going on: that turn only finishes the task
    private readonly ConcurrentDictionary<string, byte> _finishingTurns = new();

    /// <summary>Records a report; returns the message to send, or null when it repeats the PTY's current state.</summary>
    public PtyCliStateMessage? Record(string ptySessionId, string provider, string state, string? cliSessionId, DateTime at,
        PtyCliPrompt? prompt = null)
    {
        if (!CliStates.All.Contains(state))
            throw new ArgumentException($"Unknown CLI state '{state}'.", nameof(state));
        if (state != CliStates.WaitingInput)
            prompt = null;
        if (_states.TryGetValue(ptySessionId, out var current)
            && current.State == state && current.Provider == provider && current.CliSessionId == cliSessionId
            && (prompt is null || prompt.SameAs(current.Prompt)))
            return null;
        var next = new PtyCliState(provider, state, cliSessionId, at, prompt);
        _states[ptySessionId] = next;
        if (state == CliStates.Idle)
            _finishingTurns.TryRemove(ptySessionId, out _);
        return next.Message(ptySessionId);
    }

    /// <summary>The PTY's current state, to replay after a backend reconnect; null when none is known.</summary>
    public PtyCliStateMessage? Current(string ptySessionId) =>
        _states.TryGetValue(ptySessionId, out var state) ? state.Message(ptySessionId) : null;

    /// <summary>
    /// The PTY's queued task said <c>sidehub-cli task done</c>. The CLI says it from inside its turn, so it keeps
    /// working a few seconds more while the queue already starts the next task: until that turn ends, the PTY is not
    /// <see cref="Busy"/>.
    /// </summary>
    public void TaskDone(string ptySessionId)
    {
        if (_states.TryGetValue(ptySessionId, out var state) && state.State == CliStates.Working)
            _finishingTurns[ptySessionId] = 0;
    }

    /// <summary>Whether the PTY's CLI is working on something, other than the end of a task already done.</summary>
    public bool Busy(string ptySessionId) =>
        _states.TryGetValue(ptySessionId, out var state) && state.State == CliStates.Working
        && !_finishingTurns.ContainsKey(ptySessionId);

    /// <summary>The CLI exited or the PTY closed: nothing is known any more.</summary>
    public void Clear(string ptySessionId)
    {
        _states.TryRemove(ptySessionId, out _);
        _finishingTurns.TryRemove(ptySessionId, out _);
    }

    public void ClearAll()
    {
        _states.Clear();
        _finishingTurns.Clear();
    }
}

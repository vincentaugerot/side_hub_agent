using System.Text.Json;
using SideHub.Agent.Models;

namespace SideHub.Agent.Tests;

/// <summary>pty.cli-state: sent on each change of a PTY's CLI state, replayed after a backend reconnect, forgotten
/// when the CLI or the PTY ends.</summary>
public class CliStateTrackerTests
{
    private const string Pty = "terminal-1";
    private const string Session = "8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14";
    private static readonly DateTime T0 = new(2026, 10, 2, 7, 15, 30, DateTimeKind.Utc);

    private readonly CliStateTracker _tracker = new();

    [Fact]
    public void Each_change_is_sent_with_the_pty_session_provider_and_time()
    {
        var message = _tracker.Record(Pty, "claude", CliStates.Working, Session, T0);

        Assert.NotNull(message);
        Assert.Equal(Pty, message.PtySessionId);
        Assert.Equal("claude", message.Provider);
        Assert.Equal(CliStates.Working, message.State);
        Assert.Equal(Session, message.CliSessionId);
        Assert.Equal(T0, message.At);
        Assert.NotNull(_tracker.Record(Pty, "claude", CliStates.WaitingInput, Session, T0.AddSeconds(1)));
        Assert.NotNull(_tracker.Record(Pty, "claude", CliStates.Working, Session, T0.AddSeconds(2)));
        Assert.NotNull(_tracker.Record(Pty, "claude", CliStates.Idle, Session, T0.AddSeconds(3)));
    }

    [Fact]
    public void A_repeated_state_is_not_sent_again()
    {
        _tracker.Record(Pty, "claude", CliStates.Working, Session, T0);

        // Claude's PostToolUse reports working after every tool call.
        Assert.Null(_tracker.Record(Pty, "claude", CliStates.Working, Session, T0.AddSeconds(5)));
        // The replay keeps the time of the change, not of the repeat.
        Assert.Equal(T0, _tracker.Current(Pty)!.At);
    }

    [Fact]
    public void The_same_state_of_another_session_is_sent()
    {
        _tracker.Record(Pty, "claude", CliStates.Idle, Session, T0);

        // e.g. /clear in claude starts a new session.
        var other = _tracker.Record(Pty, "claude", CliStates.Idle, "01a0f8a0-0e7f-7860-88b7-30a5ee25df7c", T0.AddSeconds(1));

        Assert.NotNull(other);
        Assert.Equal("01a0f8a0-0e7f-7860-88b7-30a5ee25df7c", other.CliSessionId);
    }

    [Fact]
    public void Ptys_are_tracked_apart()
    {
        _tracker.Record(Pty, "claude", CliStates.Working, Session, T0);

        Assert.NotNull(_tracker.Record("terminal-2", "claude", CliStates.Working, Session, T0));
    }

    [Fact]
    public void The_latest_state_is_replayed_after_a_reconnect()
    {
        _tracker.Record(Pty, "codex", CliStates.Working, null, T0);
        _tracker.Record(Pty, "codex", CliStates.Idle, null, T0.AddSeconds(9));

        var replay = _tracker.Current(Pty);

        Assert.NotNull(replay);
        Assert.Equal(CliStates.Idle, replay.State);
        Assert.Equal(T0.AddSeconds(9), replay.At);
        Assert.Null(_tracker.Current("terminal-2"));
    }

    [Fact]
    public void An_ended_cli_or_pty_is_forgotten_and_its_next_state_sent_again()
    {
        _tracker.Record(Pty, "claude", CliStates.Idle, Session, T0);

        _tracker.Clear(Pty);

        Assert.Null(_tracker.Current(Pty));
        Assert.NotNull(_tracker.Record(Pty, "claude", CliStates.Idle, Session, T0.AddMinutes(1)));
    }

    [Fact]
    public void An_unknown_state_is_refused() =>
        Assert.Throws<ArgumentException>(() => _tracker.Record(Pty, "claude", "busy", Session, T0));

    [Fact]
    public void The_message_follows_the_contract()
    {
        var message = _tracker.Record(Pty, "codex", CliStates.WaitingInput, null, T0)!;

        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("pty.cli-state", root.GetProperty("type").GetString());
        Assert.Equal(Pty, root.GetProperty("ptySessionId").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cliSessionId").ValueKind);
        Assert.Equal("codex", root.GetProperty("provider").GetString());
        Assert.Equal("waiting-input", root.GetProperty("state").GetString());
        Assert.Equal("2026-10-02T07:15:30Z", root.GetProperty("at").GetString());
    }

    [Fact]
    public void A_working_cli_is_busy_until_its_turn_ends()
    {
        Assert.False(_tracker.Busy(Pty));
        _tracker.Record(Pty, "claude", CliStates.Working, Session, T0);
        Assert.True(_tracker.Busy(Pty));
        _tracker.Record(Pty, "claude", CliStates.Idle, Session, T0.AddSeconds(5));
        Assert.False(_tracker.Busy(Pty));
    }

    [Fact]
    public void The_turn_that_says_task_done_no_longer_counts_as_busy()
    {
        _tracker.Record(Pty, "claude", CliStates.Working, Session, T0);

        // The queue's next task starts while this turn still writes its last answer.
        _tracker.TaskDone(Pty);
        Assert.False(_tracker.Busy(Pty));
        Assert.Equal(CliStates.Working, _tracker.Current(Pty)!.State);
        _tracker.Record(Pty, "claude", CliStates.WaitingInput, Session, T0.AddSeconds(1));
        _tracker.Record(Pty, "claude", CliStates.Working, Session, T0.AddSeconds(2));
        Assert.False(_tracker.Busy(Pty));

        // A new turn typed in that terminal afterwards is work again.
        _tracker.Record(Pty, "claude", CliStates.Idle, Session, T0.AddSeconds(3));
        _tracker.Record(Pty, "claude", CliStates.Working, Session, T0.AddSeconds(60));
        Assert.True(_tracker.Busy(Pty));
    }

    [Fact]
    public void Task_done_from_an_idle_terminal_leaves_its_next_turn_busy()
    {
        _tracker.Record(Pty, "claude", CliStates.Idle, Session, T0);
        _tracker.TaskDone(Pty);

        _tracker.Record(Pty, "claude", CliStates.Working, Session, T0.AddSeconds(10));
        Assert.True(_tracker.Busy(Pty));
    }
}

namespace SideHub.Agent.Tests;

/// <summary>The state of a codex session, read from its rollout: a turn starts with task_started and ends with
/// task_complete or turn_aborted, whatever its terminal prints after.</summary>
public class CodexTurnStateTests
{
    private const string Started = """{"timestamp":"2026-11-04T18:26:25.505Z","type":"event_msg","payload":{"type":"task_started","turn_id":"1"}}""";
    private const string Message = """{"timestamp":"2026-11-04T18:26:29.864Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"task_complete"}]}}""";
    private const string Tokens = """{"timestamp":"2026-11-04T18:26:29.875Z","type":"event_msg","payload":{"type":"token_count","info":null}}""";
    private const string Complete = """{"timestamp":"2026-11-04T18:26:29.896Z","type":"event_msg","payload":{"type":"task_complete","last_agent_message":"Salut ! Sur quoi veux-tu qu'on travaille ?"}}""";
    private const string Aborted = """{"timestamp":"2026-11-04T18:27:02.000Z","type":"event_msg","payload":{"type":"turn_aborted","reason":"interrupted"}}""";

    [Fact]
    public void A_turn_in_progress_is_working()
    {
        Assert.Equal(CliStates.Working, CodexTurnState.Of([Started, Message, Tokens]));
    }

    [Fact]
    public void A_completed_or_interrupted_turn_is_idle()
    {
        Assert.Equal(CliStates.Idle, CodexTurnState.Of([Started, Message, Tokens, Complete]));
        Assert.Equal(CliStates.Idle, CodexTurnState.Of([Started, Aborted]));
    }

    [Fact]
    public void Lines_that_neither_start_nor_end_a_turn_tell_nothing()
    {
        Assert.Null(CodexTurnState.Of([Message, Tokens]));
        Assert.Null(CodexTurnState.Of([]));
        Assert.Null(CodexTurnState.OfLine("""{"type":"event_msg","payload":{"type":"task_started"""));
        Assert.Null(CodexTurnState.OfLine("""["event_msg","task_started"]"""));
    }

    [Fact]
    public void The_next_turn_started_after_the_last_one_ended_is_working()
    {
        Assert.Equal(CliStates.Working, CodexTurnState.Of([Started, Complete, Started]));
    }
}

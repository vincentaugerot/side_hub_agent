namespace SideHub.Agent.Tests;

/// <summary>Anything in the terminal can write to the notification FIFO: only the events the wrappers and sidehub-cli
/// write are accepted, with a UUID session id, a known provider, an absolute cwd and a positive pid.</summary>
public class FifoNotificationTests
{
    private const string SessionId = "8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14";

    private const string Secret = "4f1c0d2e9b8a7c6d5e4f3a2b1c0d9e8f";

    private static FifoNotification? Parse(string line) => FifoNotification.Parse(WithSecret(line), Secret, out _);

    private static string Rejection(string line) => RawRejection(WithSecret(line));

    private static string RawRejection(string line)
    {
        Assert.Null(FifoNotification.Parse(line, Secret, out var rejection));
        return Assert.IsType<string>(rejection);
    }

    /// <summary>The line as sidehub-cli writes it: a JSON object also carries the PTY's secret.</summary>
    private static string WithSecret(string line) => line.StartsWith('{')
        ? $$"""{"secret":"{{Secret}}"{{(line[1..].StartsWith('}') ? "" : ",")}}{{line[1..]}}"""
        : line;

    [Theory]
    [InlineData("""{"event":"run-step-ended"}""")]
    [InlineData("""{"event":"run-step-ended","secret":""}""")]
    [InlineData("""{"event":"run-step-ended","secret":"4f1c0d2e9b8a7c6d5e4f3a2b1c0d9e8e"}""")]
    [InlineData("""{"event":"run-step-ended","secret":42}""")]
    public void A_line_without_the_secret_of_its_pty_is_rejected(string line) =>
        Assert.Equal("missing or wrong secret", RawRejection(line));

    [Fact]
    public void Without_a_secret_to_expect_nothing_is_accepted() =>
        Assert.Null(FifoNotification.Parse("""{"event":"run-step-ended","secret":""}""", "", out _));

    [Fact]
    public void What_the_wrappers_write_is_accepted()
    {
        Assert.Equal(new FifoNotification.CliSessionStarted("claude", SessionId),
            Parse($$"""{"event":"cli-session-started","provider":"claude","cliSessionId":"{{SessionId}}"}"""));
        Assert.Equal(new FifoNotification.CliLaunched("codex", "/root/Github/side_hub", 4242),
            Parse("""{"event":"cli-launched","provider":"codex","cwd":"/root/Github/side_hub","pid":4242}"""));
        Assert.IsType<FifoNotification.RunStepEnded>(Parse("""{"event":"run-step-ended"}"""));
        Assert.IsType<FifoNotification.TaskDone>(Parse("""{"event":"task-done"}"""));
    }

    [Fact]
    public void A_session_the_cli_switched_to_is_told_apart_from_its_launch()
    {
        Assert.Equal(new FifoNotification.CliSessionStarted("claude", SessionId, Switched: true),
            Parse($$"""{"event":"cli-session-started","provider":"claude","cliSessionId":"{{SessionId}}","switched":true}"""));
        Assert.Equal(new FifoNotification.CliSessionStarted("claude", SessionId),
            Parse($$"""{"event":"cli-session-started","provider":"claude","cliSessionId":"{{SessionId}}","switched":"yes"}"""));
    }

    [Fact]
    public void A_cli_exit_is_accepted_with_or_without_its_session_id()
    {
        Assert.Equal(new FifoNotification.CliExited("claude", SessionId),
            Parse($$"""{"event":"cli-exited","provider":"claude","cliSessionId":"{{SessionId}}"}"""));
        Assert.Equal(new FifoNotification.CliExited("codex", null),
            Parse("""{"event":"cli-exited","provider":"codex","cliSessionId":null}"""));
        Assert.Equal(new FifoNotification.CliExited("codex", null),
            Parse("""{"event":"cli-exited","provider":"codex"}"""));
        Assert.Equal("invalid cliSessionId",
            Rejection("""{"event":"cli-exited","provider":"claude","cliSessionId":"../x"}"""));
        Assert.Equal("invalid cliSessionId",
            Rejection("""{"event":"cli-exited","provider":"claude","cliSessionId":42}"""));
        Assert.Equal("unknown provider", Rejection("""{"event":"cli-exited","provider":"bash"}"""));
    }

    [Theory]
    [InlineData("working")]
    [InlineData("waiting-input")]
    [InlineData("idle")]
    public void A_cli_state_is_accepted_with_or_without_its_session_id(string state)
    {
        Assert.Equal(new FifoNotification.CliStateChanged("claude", state, SessionId),
            Parse($$"""{"event":"cli-state","provider":"claude","state":"{{state}}","cliSessionId":"{{SessionId}}"}"""));
        Assert.Equal(new FifoNotification.CliStateChanged("codex", state, null),
            Parse($$"""{"event":"cli-state","provider":"codex","state":"{{state}}","cliSessionId":null}"""));
        Assert.Equal(new FifoNotification.CliStateChanged("codex", state, null),
            Parse($$"""{"event":"cli-state","provider":"codex","state":"{{state}}"}"""));
    }

    [Theory]
    [InlineData("""{"event":"cli-state","provider":"claude","state":"busy"}""", "unknown state")]
    [InlineData("""{"event":"cli-state","provider":"claude","state":"Working"}""", "unknown state")]
    [InlineData("""{"event":"cli-state","provider":"claude"}""", "unknown state")]
    [InlineData("""{"event":"cli-state","provider":"claude","state":1}""", "unknown state")]
    [InlineData("""{"event":"cli-state","provider":"bash","state":"idle"}""", "unknown provider")]
    [InlineData("""{"event":"cli-state","provider":"claude","state":"idle","cliSessionId":"../../etc"}""", "invalid cliSessionId")]
    [InlineData("""{"event":"cli-state","provider":"claude","state":"idle","cliSessionId":42}""", "invalid cliSessionId")]
    public void A_cli_state_is_rejected_unless_every_field_is_valid(string line, string expected) =>
        Assert.Equal(expected, Rejection(line));

    [Fact]
    public void A_launch_without_pid_is_accepted_without_one() =>
        Assert.Equal(new FifoNotification.CliLaunched("codex", "/work", null),
            Parse("""{"event":"cli-launched","provider":"codex","cwd":"/work"}"""));

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("../../.ssh/authorized_keys")]
    [InlineData("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14/../../x")]
    [InlineData("8c1e7a520d3b4f6e9a215b7c3d9e0f14")]
    [InlineData("{8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14}")]
    [InlineData("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14\n")]
    [InlineData("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f1z")]
    [InlineData("")]
    public void A_session_id_that_is_not_a_uuid_is_rejected(string cliSessionId)
    {
        var line = $$"""{"event":"cli-session-started","provider":"claude","cliSessionId":{{System.Text.Json.JsonSerializer.Serialize(cliSessionId)}}}""";
        Assert.Equal("invalid cliSessionId", Rejection(line));
        Assert.False(FifoNotification.IsValidCliSessionId(cliSessionId));
    }

    [Fact]
    public void A_session_id_that_is_not_a_string_is_rejected() =>
        Assert.Equal("invalid cliSessionId",
            Rejection("""{"event":"cli-session-started","provider":"claude","cliSessionId":42}"""));

    [Theory]
    [InlineData("""{"event":"cli-session-started","provider":"../claude","cliSessionId":"8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14"}""")]
    [InlineData("""{"event":"cli-session-started","provider":"Claude","cliSessionId":"8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14"}""")]
    [InlineData("""{"event":"cli-session-started","cliSessionId":"8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14"}""")]
    [InlineData("""{"event":"cli-launched","provider":"bash","cwd":"/work","pid":1}""")]
    public void An_unknown_provider_is_rejected(string line) => Assert.Equal("unknown provider", Rejection(line));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    [InlineData("\"4242\"")]
    [InlineData("null")]
    public void A_pid_that_is_not_positive_is_rejected(string pid) =>
        Assert.Equal("invalid pid",
            Rejection($$"""{"event":"cli-launched","provider":"codex","cwd":"/work","pid":{{pid}}}"""));

    [Theory]
    [InlineData("work")]
    [InlineData("./work")]
    [InlineData("/work/../etc")]
    [InlineData("/work/./x")]
    [InlineData("/work\nINJECTED log line")]
    [InlineData("")]
    public void A_cwd_that_is_not_an_absolute_normalized_path_is_rejected(string cwd)
    {
        var line = $$"""{"event":"cli-launched","provider":"codex","cwd":{{System.Text.Json.JsonSerializer.Serialize(cwd)}},"pid":1}""";
        Assert.Equal("invalid cwd", Rejection(line));
    }

    [Fact]
    public void A_cwd_longer_than_path_max_is_rejected()
    {
        Assert.True(FifoNotification.IsValidCwd("/" + new string('a', FifoNotification.MaxCwdLength - 1)));
        Assert.False(FifoNotification.IsValidCwd("/" + new string('a', FifoNotification.MaxCwdLength)));
    }

    [Theory]
    [InlineData("not json", "malformed JSON")]
    [InlineData("[1,2]", "not a JSON object")]
    [InlineData("""{"event":"pty.stop"}""", "unknown event")]
    [InlineData("""{"provider":"claude"}""", "unknown event")]
    public void Anything_else_is_rejected(string line, string expected) => Assert.Equal(expected, Rejection(line));

    [Fact]
    public void A_line_longer_than_the_limit_is_rejected() =>
        Assert.Equal("line too long", Rejection(new string(' ', FifoNotification.MaxLineLength + 1)));
}

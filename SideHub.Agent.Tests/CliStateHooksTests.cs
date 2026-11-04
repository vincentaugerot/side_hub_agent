using System.Text;
using System.Text.Json;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>`sidehub-cli launch` adds per-invocation hooks (claude --settings, codex -c notify) that call
/// `sidehub-cli cli-state` back, without touching the user's settings nor dropping their arguments.</summary>
public class CliStateHooksTests : IDisposable
{
    private static readonly Guid Minted = Guid.Parse("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14");
    private const string Session = "8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14";
    private const string Program = "/usr/local/lib/sidehub-agent/sidehub-cli";
    private static readonly CliLaunchPlan.StateReporting Reporting = new(Program, CodexNotifyTaken: false);

    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-state-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CliLaunchPlan Plan(string cli, string[] args, string? prompt = null, CliLaunchPlan.StateReporting? reporting = null) =>
        CliLaunchPlan.For(cli, args, prompt, new Version(0, 62, 0), () => Minted, reporting ?? Reporting);

    // ---- claude ----

    [Fact]
    public void Claude_gets_the_hooks_in_front_of_the_callers_arguments_and_the_prompt_stays_last()
    {
        var plan = Plan("claude", ["--model", "opus", "--dangerously-skip-permissions"], "fix it");

        Assert.Equal("--settings", plan.Arguments[0]);
        Assert.Equal(CliStateHooks.ClaudeSettings(Program), plan.Arguments[1]);
        Assert.Equal(["--session-id", Session, "--model", "opus", "--dangerously-skip-permissions", "fix it"], plan.Arguments.Skip(2));
        Assert.Equal(Session, plan.SessionId);
    }

    [Fact]
    public void Claude_settings_hold_only_hooks_that_run_sidehub_cli_without_a_shell()
    {
        using var doc = JsonDocument.Parse(CliStateHooks.ClaudeSettings(@"C:\Program Files\SideHub\sidehub-cli.exe"));
        var root = doc.RootElement;
        Assert.Equal(["hooks"], root.EnumerateObject().Select(p => p.Name));

        var states = new Dictionary<string, (string? Matcher, string State)>();
        foreach (var hookEvent in root.GetProperty("hooks").EnumerateObject())
        {
            var group = Assert.Single(hookEvent.Value.EnumerateArray());
            var hook = Assert.Single(group.GetProperty("hooks").EnumerateArray());
            Assert.Equal("command", hook.GetProperty("type").GetString());
            // Exec form: the path is one argument, spaces and backslashes included, on every OS.
            Assert.Equal(@"C:\Program Files\SideHub\sidehub-cli.exe", hook.GetProperty("command").GetString());
            var args = hook.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
            Assert.Equal("cli-state", args[0]);
            Assert.Equal("claude", args[1]);
            Assert.True(hook.GetProperty("timeout").GetInt32() is > 0 and <= 10);
            states[hookEvent.Name] = (group.TryGetProperty("matcher", out var m) ? m.GetString() : null, args[2]!);
        }

        Assert.Equal((null, "working"), states["UserPromptSubmit"]);
        Assert.Equal((null, "working"), states["PostToolUse"]);
        Assert.Equal(("AskUserQuestion", "waiting-input"), states["PreToolUse"]);
        Assert.Equal(("permission_prompt|elicitation_dialog|elicitation_url_dialog", "waiting-input"), states["Notification"]);
        Assert.Equal((null, "waiting-input"), states["PermissionRequest"]);
        Assert.Equal((null, "idle"), states["Stop"]);
        Assert.Equal(("rate_limit|billing_error", "limit"), states["StopFailure"]);
        Assert.Equal(("resume|clear", "session"), states["SessionStart"]);
        Assert.Equal(8, states.Count);
    }

    [Theory]
    [InlineData("--settings", "./mine.json")]
    [InlineData("--settings={\"model\":\"opus\"}", null)]
    public void Claude_keeps_the_callers_own_settings_which_ours_would_replace(string option, string? value)
    {
        string[] args = value is null ? [option] : [option, value];

        var plan = Plan("claude", args);

        Assert.Equal(["--session-id", Session, .. args], plan.Arguments);
    }

    [Theory]
    [InlineData("mcp", "list")]
    [InlineData("--version", null)]
    public void Claude_commands_that_start_no_conversation_get_no_hooks(string first, string? second)
    {
        string[] args = second is null ? [first] : [first, second];

        Assert.DoesNotContain("--settings", Plan("claude", args).Arguments);
    }

    [Fact]
    public void A_resumed_claude_session_gets_the_hooks_too()
    {
        var plan = Plan("claude", ["--resume", "01a0f8a0-0e7f-7860-88b7-30a5ee25df7c"]);

        Assert.Equal(["--settings", CliStateHooks.ClaudeSettings(Program), "--resume", "01a0f8a0-0e7f-7860-88b7-30a5ee25df7c"], plan.Arguments);
    }

    [Fact]
    public void Nothing_is_added_outside_a_sidehub_terminal() =>
        Assert.Equal(["--session-id", Session, "-p", "x"],
            CliLaunchPlan.For("claude", ["-p", "x"], null, null, () => Minted).Arguments);

    // ---- codex ----

    [Fact]
    public void Codex_gets_a_notify_program_in_front_of_its_subcommand()
    {
        var plan = Plan("codex", ["exec", "--skip-git-repo-check"], "do it");

        Assert.Equal(["-c", """notify=["/usr/local/lib/sidehub-agent/sidehub-cli","cli-state","codex"]""", "exec", "--skip-git-repo-check", "do it"],
            plan.Arguments);
        Assert.True(plan.ReportLaunch);
    }

    [Fact]
    public void A_resumed_codex_session_is_still_announced_with_the_notify_program()
    {
        var plan = Plan("codex", ["resume", "01a0f8a0-0e7f-7860-88b7-30a5ee25df7c"]);

        Assert.Equal("01a0f8a0-0e7f-7860-88b7-30a5ee25df7c", plan.SessionId);
        Assert.Equal(["resume", "01a0f8a0-0e7f-7860-88b7-30a5ee25df7c"], plan.Arguments.Skip(4));
    }

    [Fact]
    public void The_codex_notify_value_is_valid_toml_for_a_windows_path() =>
        Assert.Equal("""notify=["C:\\Program Files\\SideHub\\sidehub-cli.exe","cli-state","codex"]""",
            CliStateHooks.CodexNotify(@"C:\Program Files\SideHub\sidehub-cli.exe"));

    [Theory]
    [InlineData("-c", "notify=[\"x\"]")]
    [InlineData("--config", " notify = [\"x\"]")]
    [InlineData("--config=notify=[\"x\"]", null)]
    [InlineData("-cnotify=[\"x\"]", null)]
    public void Codex_keeps_a_notify_the_caller_sets(string option, string? value)
    {
        string[] args = value is null ? [option] : [option, value];

        Assert.Equal(["-c", CliLaunchPlan.CodexInlineScreen, .. args], Plan("codex", args).Arguments);
    }

    [Fact]
    public void Codex_keeps_the_notify_of_the_users_config() =>
        Assert.Equal(["exec"], Plan("codex", ["exec"], reporting: Reporting with { CodexNotifyTaken = true }).Arguments);

    [Fact]
    public void Another_codex_override_does_not_count_as_notify() =>
        Assert.Equal(6, Plan("codex", ["-c", "notify_level=1"]).Arguments.Count);

    [Theory]
    [InlineData("model = \"o3\"\nnotify = [\"notify-send\"]\n", true)]
    [InlineData("  notify=[\"x\"]\n[tui]\nnotifications = true\n", true)]
    [InlineData("model = \"o3\"\n[tui]\nnotify = [\"x\"]\n", false)]
    [InlineData("model = \"o3\"\n[tui]\nnotifications = [\"agent-turn-complete\"]\n", false)]
    [InlineData("", false)]
    public void A_top_level_notify_in_the_codex_config_is_detected(string toml, bool defines)
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, toml);

        Assert.Equal(defines, CliStateHooks.CodexConfigDefinesNotify(path));
    }

    [Fact]
    public void A_missing_codex_config_sets_no_notify() =>
        Assert.False(CliStateHooks.CodexConfigDefinesNotify(Path.Combine(_dir, "none.toml")));

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public void Clis_without_command_line_hooks_get_none(string cli) =>
        Assert.Equal(["--session-id", Session, "-i", "x"], Plan(cli, ["-i", "x"]).Arguments);

    // ---- agy ----

    [Fact]
    public void Agy_takes_no_hook_argument_but_its_hooks_file_is_due_in_a_sidehub_terminal()
    {
        var plan = Plan("agy", ["-p"], "x");

        Assert.Equal(["-p", "x"], plan.Arguments);
        Assert.True(plan.AntigravityHooks);
        Assert.False(CliLaunchPlan.For("agy", ["-p"], "x", null, () => Minted).AntigravityHooks);
        Assert.False(Plan("agy", ["models"]).AntigravityHooks);
    }

    [Fact]
    public void The_agy_hooks_are_added_next_to_the_users_own_and_only_once()
    {
        var path = Path.Combine(_dir, "config", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "my-linter": { "PostToolUse": [ { "matcher": "run_command", "hooks": [ { "command": "./lint.sh" } ] } ] } }""");

        Assert.True(CliStateHooks.EnsureAntigravityHooks(path, Program));
        Assert.False(CliStateHooks.EnsureAntigravityHooks(path, Program));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("./lint.sh", doc.RootElement.GetProperty("my-linter").GetProperty("PostToolUse")[0]
            .GetProperty("hooks")[0].GetProperty("command").GetString());
        var ours = doc.RootElement.GetProperty(CliStateHooks.AntigravityHookName);
        Assert.Equal(["PreInvocation", "Stop"], ours.EnumerateObject().Select(p => p.Name));
        var stop = ours.GetProperty("Stop")[0].GetProperty("command").GetString()!;
        Assert.EndsWith($"'{Program}' cli-state agy idle", stop);
        Assert.StartsWith("[ -z \"$SIDEHUB_PTY_NOTIFY_FIFO\" ] ||", stop);
    }

    [Fact]
    public void The_agy_hooks_file_is_created_and_a_moved_program_updated()
    {
        var path = Path.Combine(_dir, "new", "hooks.json");

        Assert.True(CliStateHooks.EnsureAntigravityHooks(path, Program));
        Assert.True(CliStateHooks.EnsureAntigravityHooks(path, "/opt/it's/sidehub-cli"));

        var command = JsonDocument.Parse(File.ReadAllText(path)).RootElement
            .GetProperty("sidehub").GetProperty("PreInvocation")[0].GetProperty("command").GetString();
        Assert.EndsWith("'/opt/it'\\''s/sidehub-cli' cli-state agy working", command);
    }

    [Fact]
    public void An_invalid_agy_hooks_file_is_left_alone()
    {
        var path = Path.Combine(_dir, "hooks.json");
        File.WriteAllText(path, "[1, 2]");

        Assert.Throws<InvalidDataException>(() => CliStateHooks.EnsureAntigravityHooks(path, Program));
        Assert.Equal("[1, 2]", File.ReadAllText(path));
    }

    // ---- sidehub-cli cli-state ----

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData("working")]
    [InlineData("waiting-input")]
    [InlineData("idle")]
    public void A_claude_hook_reports_its_state_and_the_session_of_its_input(string state)
    {
        var input = Utf8($$"""{"session_id":"{{Session}}","transcript_path":"/x.jsonl","hook_event_name":"Stop"}""");

        Assert.Equal(new CliStateCommand.StateReport("claude", state, Session),
            CliStateCommand.Report(["claude", state], () => input));
    }

    [Fact]
    public void The_session_start_hook_tells_the_session_claude_switched_to_and_no_state()
    {
        var input = Utf8($$"""{"session_id":"{{Session}}","hook_event_name":"SessionStart","source":"resume"}""");

        Assert.Equal(Session, CliStateCommand.SwitchedSession(["claude", "session"], () => input));
        Assert.Null(CliStateCommand.SwitchedSession(["claude", "session"], () => null));
        Assert.Null(CliStateCommand.SwitchedSession(["claude", "idle"], () => input));
        Assert.Null(CliStateCommand.Report(["claude", "session"], () => input));
    }

    [Fact]
    public void The_session_is_found_after_large_values_and_without_input_the_state_is_still_reported()
    {
        var input = Utf8($$"""{"tool_input":{"content":"{{new string('x', 5000)}}","n":[1,2]},"session_id":"{{Session}}"}""");

        Assert.Equal(Session, CliStateCommand.SessionIdFromHookInput(input));
        Assert.Equal(new CliStateCommand.StateReport("claude", "idle", null), CliStateCommand.Report(["claude", "idle"], () => null));
    }

    [Theory]
    [InlineData("""{"session_id":"../../etc/passwd"}""")]
    [InlineData("""{"session_id":42}""")]
    [InlineData("""["8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14"]""")]
    [InlineData("""{"other":{"session_id":"8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14"}}""")]
    [InlineData("""{"tool_response":"cut short befo""")]
    [InlineData("not json")]
    public void A_session_id_is_only_taken_as_a_top_level_uuid(string input) =>
        Assert.Null(CliStateCommand.SessionIdFromHookInput(Utf8(input)));

    [Fact]
    public void A_hook_input_cut_short_after_the_session_id_still_gives_it() =>
        Assert.Equal(Session, CliStateCommand.SessionIdFromHookInput(Utf8($$"""{"session_id":"{{Session}}","tool_response":"abc""")));

    [Theory]
    [InlineData("claude", "busy")]
    [InlineData("claude", "Idle")]
    [InlineData("gemini", "idle")]
    [InlineData("agy", "waiting-input")]
    [InlineData("claude", null)]
    public void Unknown_arguments_report_nothing(string cli, string? state)
    {
        string[] args = state is null ? [cli] : [cli, state];

        Assert.Null(CliStateCommand.Report(args, () => throw new InvalidOperationException("stdin is not read")));
    }

    [Fact]
    public void An_agy_hook_reports_its_state_and_the_conversation_of_its_input()
    {
        var invocation = Utf8($$"""{"conversationId":"{{Session}}","invocationNum":0,"initialNumSteps":1,"modelName":"gemini-3.8-flash-low","transcriptPath":"/t.jsonl","workspacePaths":["/w"]}""");
        var stop = Utf8($$"""{"conversationId":"{{Session}}","error":"","executionNum":0,"fullyIdle":true,"terminationReason":"NO_TOOL_CALL"}""");

        Assert.Equal(new CliStateCommand.StateReport("agy", "working", Session), CliStateCommand.Report(["agy", "working"], () => invocation));
        Assert.Equal(new CliStateCommand.StateReport("agy", "idle", Session), CliStateCommand.Report(["agy", "idle"], () => stop));
    }

    [Fact]
    public void An_agy_stop_with_work_left_in_the_background_is_not_idle()
    {
        var stop = Utf8($$"""{"conversationId":"{{Session}}","fullyIdle":false}""");

        Assert.Null(CliStateCommand.Report(["agy", "idle"], () => stop));
    }

    [Theory]
    [InlineData("""{"conversationId":"../../x"}""")]
    [InlineData("""not json""")]
    [InlineData(null)]
    public void An_agy_hook_without_a_valid_conversation_still_reports_its_state(string? input) =>
        Assert.Equal(new CliStateCommand.StateReport("agy", "working", null),
            CliStateCommand.Report(["agy", "working"], () => input is null ? null : Utf8(input)));

    [Fact]
    public void A_codex_turn_end_reports_idle_with_its_thread()
    {
        const string thread = "01a0fcfe-cee1-76d2-b962-022530c812e2";
        var notify = $$"""{"type":"agent-turn-complete","thread-id":"{{thread}}","turn-id":"t","cwd":"/w","input-messages":["hi"],"last-assistant-message":"ok"}""";

        Assert.Equal(new CliStateCommand.StateReport("codex", "idle", thread), CliStateCommand.Report(["codex", notify], () => null));
    }

    [Theory]
    [InlineData("""{"type":"agent-turn-complete","thread-id":"nope"}""", true)]
    [InlineData("""{"type":"approval-requested","thread-id":"01a0fcfe-cee1-76d2-b962-022530c812e2"}""", false)]
    [InlineData("""not json""", false)]
    [InlineData("""[]""", false)]
    public void Other_codex_events_report_nothing_and_a_bad_thread_is_dropped(string notify, bool reported)
    {
        var report = CliStateCommand.Report(["codex", notify], () => null);

        Assert.Equal(reported, report is not null);
        Assert.Null(report?.CliSessionId);
    }

    [Fact]
    public void The_command_always_succeeds_silently()
    {
        var stdout = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            Assert.Equal(0, CliStateCommand.Run(["codex", "{}"]));
            Assert.Equal(0, CliStateCommand.Run([]));
            Assert.Equal(0, CliStateCommand.Run(["claude", "nonsense"]));
        }
        finally
        {
            Console.SetOut(stdout);
        }
        Assert.Equal("", writer.ToString());
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>A workflow's tool policy: `sidehub-cli launch` installs claude's PreToolUse hook `sidehub-cli policy check`
/// (or refuses to start the CLI when it cannot), and the hook turns SideHub's decision into Claude Code's answer,
/// denying the tool call whenever the policy could not be checked.</summary>
public class ToolPolicyTests
{
    private static readonly Guid Minted = Guid.Parse("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14");
    private const string Session = "8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14";
    private const string Program = "/usr/local/lib/sidehub-agent/sidehub-cli";
    private const string Matcher = "^(?:Bash|mcp__gmail__.*)$";
    private static readonly CliLaunchPlan.StateReporting Reporting = new(Program, CodexNotifyTaken: false);
    private static readonly CliLaunchPlan.ToolPolicy Policy = new(Matcher, Program);

    private static CliLaunchPlan Plan(string cli, string[] args, CliLaunchPlan.StateReporting? reporting = null,
        CliLaunchPlan.ToolPolicy? policy = null, string? prompt = null) =>
        CliLaunchPlan.For(cli, args, prompt, new Version(0, 62, 0), () => Minted, reporting, null, policy ?? Policy);

    /// <summary>The (matcher, args, timeout) of each hook of a claude --settings JSON, per event.</summary>
    private static Dictionary<string, List<(string? Matcher, string Args, int Timeout)>> Hooks(string settings)
    {
        using var doc = JsonDocument.Parse(settings);
        var hooks = new Dictionary<string, List<(string?, string, int)>>();
        foreach (var hookEvent in doc.RootElement.GetProperty("hooks").EnumerateObject())
        foreach (var group in hookEvent.Value.EnumerateArray())
        foreach (var hook in group.GetProperty("hooks").EnumerateArray())
        {
            Assert.Equal("command", hook.GetProperty("type").GetString());
            Assert.Equal(Program, hook.GetProperty("command").GetString());
            if (!hooks.TryGetValue(hookEvent.Name, out var list))
                hooks[hookEvent.Name] = list = [];
            list.Add((group.TryGetProperty("matcher", out var m) ? m.GetString() : null,
                string.Join(" ", hook.GetProperty("args").EnumerateArray().Select(a => a.GetString())),
                hook.GetProperty("timeout").GetInt32()));
        }
        return hooks;
    }

    // ---- sidehub-cli launch ----

    [Fact]
    public void The_policy_hook_joins_the_state_hooks_in_the_same_settings()
    {
        var plan = Plan("claude", ["--model", "opus", "--dangerously-skip-permissions"], Reporting, prompt: "do it");

        Assert.Null(plan.Refusal);
        Assert.Equal("--settings", plan.Arguments[0]);
        Assert.Equal(CliStateHooks.ClaudeSettings(Program, reportState: true, Matcher), plan.Arguments[1]);
        Assert.Equal(["--session-id", Session, "--model", "opus", "--dangerously-skip-permissions", "do it"], plan.Arguments.Skip(2));

        var hooks = Hooks(plan.Arguments[1]);
        Assert.Equal(["Notification", "PermissionRequest", "PostToolUse", "PreToolUse", "SessionStart", "Stop", "StopFailure", "UserPromptSubmit"], hooks.Keys.Order());
        Assert.Equal(
            [("AskUserQuestion", "cli-state claude waiting-input", 5), (Matcher, "policy check", 60)],
            hooks["PreToolUse"]);
    }

    [Fact]
    public void The_policy_hook_is_installed_without_state_reporting()
    {
        var plan = Plan("claude", ["-p"], reporting: null);

        Assert.Null(plan.Refusal);
        var hooks = Hooks(plan.Arguments[1]);
        Assert.Equal(["PreToolUse"], hooks.Keys);
        Assert.Equal([(Matcher, "policy check", 60)], hooks["PreToolUse"]);
    }

    [Fact]
    public void Without_a_policy_the_settings_are_the_state_hooks_only() =>
        Assert.Equal(CliStateHooks.ClaudeSettings(Program),
            CliLaunchPlan.For("claude", [], null, null, () => Minted, Reporting).Arguments[1]);

    [Fact]
    public void A_resumed_claude_session_keeps_the_policy_hook()
    {
        const string resumed = "01a0f8a0-0e7f-7860-88b7-30a5ee25df7c";

        var plan = Plan("claude", ["--resume", resumed], Reporting);

        Assert.Null(plan.Refusal);
        Assert.Equal(resumed, plan.SessionId);
        Assert.Equal(["--settings", CliStateHooks.ClaudeSettings(Program, reportState: true, Matcher), "--resume", resumed], plan.Arguments);
    }

    [Theory]
    [InlineData("codex", new[] { "exec" }, "this run has a tool policy that only claude can enforce: codex is not started.")]
    [InlineData("gemini", new[] { "-p", "x" }, "this run has a tool policy that only claude can enforce: gemini is not started.")]
    [InlineData("claude", new[] { "--settings", "./mine.json" }, "cannot install the tool policy hook: the arguments already pass --settings")]
    [InlineData("claude", new[] { "--settings={}" }, "cannot install the tool policy hook: the arguments already pass --settings")]
    public void A_policy_that_cannot_be_enforced_refuses_the_launch(string cli, string[] args, string refusal) =>
        Assert.StartsWith(refusal, Plan(cli, args, Reporting).Refusal);

    [Fact]
    public void A_policy_hook_without_a_program_to_run_refuses_the_launch() =>
        Assert.StartsWith("cannot install the tool policy hook: this sidehub-cli cannot be run by its path",
            Plan("claude", [], policy: Policy with { Program = null }).Refusal);

    [Theory]
    [InlineData("claude", new[] { "mcp", "list" })]
    [InlineData("claude", new[] { "--version" })]
    [InlineData("codex", new[] { "login" })]
    public void Commands_that_start_no_conversation_run_as_given_under_a_policy(string cli, string[] args)
    {
        var plan = Plan(cli, args, Reporting);

        Assert.Null(plan.Refusal);
        Assert.Equal(args, plan.Arguments);
    }

    // ---- sidehub-cli policy check: hook input ----

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void The_hook_input_gives_the_tool_call()
    {
        var input = PolicyCheckCommand.ParseHookInput(Utf8($$"""
            {"session_id":"{{Session}}","hook_event_name":"PreToolUse","tool_name":"Bash",
             "tool_input":{"command":"rm -rf build","description":"clean"},"tool_use_id":"toolu_01ABC"}
            """), out var problem);

        Assert.Null(problem);
        Assert.NotNull(input);
        Assert.Equal("Bash", input.ToolName);
        Assert.Equal("""{"command":"rm -rf build","description":"clean"}""", input.ToolInput.GetRawText());
        Assert.Equal("toolu_01ABC", input.ToolUseId);
        Assert.Equal(Session, input.CliSessionId);
    }

    [Fact]
    public void A_hook_input_without_tool_input_or_ids_still_gives_the_tool()
    {
        var input = PolicyCheckCommand.ParseHookInput(Utf8("""{"tool_name":"mcp__gmail__send","session_id":"not-a-uuid"}"""), out _);

        Assert.NotNull(input);
        Assert.Equal("{}", input.ToolInput.GetRawText());
        Assert.Null(input.ToolUseId);
        Assert.Null(input.CliSessionId);
    }

    [Theory]
    [InlineData(null, "no hook input on stdin")]
    [InlineData("", "no hook input on stdin")]
    [InlineData("not json", "hook input is not JSON")]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"cut short""", "hook input is not JSON")]
    [InlineData("""["Bash"]""", "hook input without tool_name")]
    [InlineData("""{"tool_input":{}}""", "hook input without tool_name")]
    [InlineData("""{"tool_name":""}""", "hook input without tool_name")]
    [InlineData("""{"tool_name":42}""", "hook input without tool_name")]
    [InlineData("""{"tool_name":"Bash","tool_input":"ls"}""", "hook input whose tool_input is not an object")]
    public void A_hook_input_without_a_tool_call_is_refused(string? input, string expected)
    {
        Assert.Null(PolicyCheckCommand.ParseHookInput(input is null ? null : Utf8(input), out var problem));
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void A_hook_input_too_large_is_refused()
    {
        var input = new byte[PolicyCheckCommand.MaxHookInputBytes + 1];

        Assert.Null(PolicyCheckCommand.ParseHookInput(input, out var problem));
        Assert.Equal("hook input larger than 8 MiB", problem);
    }

    // ---- sidehub-cli policy check: decision ----

    private static JsonElement Output(PolicyCheckCommand.Verdict verdict)
    {
        var output = PolicyCheckCommand.HookOutput(verdict);
        Assert.NotNull(output);
        using var doc = JsonDocument.Parse(output);
        return doc.RootElement.Clone();
    }

    private static void AssertPermission(JsonElement output, string decision, string reason)
    {
        var specific = output.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", specific.GetProperty("hookEventName").GetString());
        Assert.Equal(decision, specific.GetProperty("permissionDecision").GetString());
        Assert.Equal(reason, specific.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void Allow_leaves_claudes_own_permission_flow() =>
        Assert.Null(PolicyCheckCommand.HookOutput(new("allow", "Not covered.")));

    [Fact]
    public void Approved_allows_the_tool_call()
    {
        AssertPermission(Output(new("approved", "Approved by Vincent.")), "allow", "Approved by Vincent.");
        AssertPermission(Output(new("approved", null)), "allow", "Approved in SideHub.");
        Assert.Equal(["hookSpecificOutput"], Output(new("approved", null)).EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Deny_blocks_the_tool_call_with_its_reason()
    {
        var output = Output(new("deny", "Sending mail is denied by the workflow."));

        AssertPermission(output, "deny", "Sending mail is denied by the workflow.");
        Assert.Equal(["hookSpecificOutput"], output.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Pause_blocks_the_tool_call_and_stops_the_turn()
    {
        var output = Output(new("pause", "Waiting for approval in SideHub."));

        Assert.False(output.GetProperty("continue").GetBoolean());
        Assert.Equal("Waiting for approval in SideHub.", output.GetProperty("stopReason").GetString());
        AssertPermission(output, "deny", "Waiting for approval in SideHub.");
    }

    [Fact]
    public void A_failure_denies_the_tool_call_and_says_why() =>
        AssertPermission(Output(PolicyCheckCommand.Failure("SideHub is unreachable")), "deny",
            "SideHub could not check this action against the workflow's tool policy (SideHub is unreachable): it is blocked.");

    [Fact]
    public void An_unknown_decision_is_denied() =>
        Assert.Equal("deny", Output(new("maybe", null)).GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());

    [Theory]
    [InlineData(200, """{"decision":"allow","reason":null,"requestId":null}""", "allow", null)]
    [InlineData(200, """{"decision":"approved","reason":"ok","requestId":"0b8a2f8e-2b7e-4b0e-9d7a-3c1f8e9d2a10"}""", "approved", "ok")]
    [InlineData(200, """{"decision":"deny","reason":"no"}""", "deny", "no")]
    [InlineData(200, """{"decision":"pause","reason":"wait"}""", "pause", "wait")]
    [InlineData(200, """{"decision":"Allow"}""", "deny", "(SideHub's answer has an unknown decision)")]
    [InlineData(200, """{"reason":"x"}""", "deny", "(SideHub's answer has no decision)")]
    [InlineData(200, """<html>""", "deny", "(SideHub's answer is not JSON)")]
    [InlineData(403, """{"error":"This token belongs to another run."}""", "deny", "(HTTP 403: This token belongs to another run.)")]
    [InlineData(500, """oops""", "deny", "(HTTP 500)")]
    [InlineData(302, "", "deny", "(HTTP 302)")]
    public void Sidehubs_answer_gives_the_verdict(int status, string body, string decision, string? reason)
    {
        var verdict = PolicyCheckCommand.Interpret(status, body);

        Assert.Equal(decision, verdict.Decision);
        if (reason is not null && reason.StartsWith('('))
            Assert.Contains(reason, verdict.Reason);
        else
            Assert.Equal(reason, verdict.Reason);
    }

    [Theory]
    [InlineData(null, "sh_run_x", "SIDEHUB_API_URL or SIDEHUB_AGENT_TOKEN is not set")]
    [InlineData("https://api.sidehub.io", null, "SIDEHUB_API_URL or SIDEHUB_AGENT_TOKEN is not set")]
    [InlineData("http://api.sidehub.io", "sh_run_x", "invalid SIDEHUB_API_URL")]
    [InlineData("https://api.sidehub.io", "sh_pty_x", "this terminal has no run token")]
    [InlineData("https://api.sidehub.io", "sh_agent_x", "this terminal has no run token")]
    public void The_policy_is_only_checked_with_a_run_token_and_a_safe_url(string? apiUrl, string? token, string problem) =>
        Assert.StartsWith(problem, PolicyCheckCommand.EnvironmentProblem(apiUrl, token));

    private sealed class FakeApi(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private static readonly byte[] BashInput =
        Utf8($$"""{"session_id":"{{Session}}","tool_name":"Bash","tool_input":{"command":"ls -la"},"tool_use_id":"toolu_1"}""");

    [Fact]
    public async Task The_tool_call_is_posted_with_the_run_token()
    {
        var api = new FakeApi(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"decision":"deny","reason":"Not on Fridays.","requestId":null}"""),
        });

        var verdict = await PolicyCheckCommand.DecideAsync(BashInput, "https://api.sidehub.io/", "sh_run_abc", api);

        Assert.Equal(new PolicyCheckCommand.Verdict("deny", "Not on Fridays."), verdict);
        var (request, body) = Assert.Single(api.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.sidehub.io/api/tool-approvals/check", request.RequestUri!.ToString());
        Assert.Equal(["sh_run_abc"], request.Headers.GetValues("X-Agent-Token"));
        Assert.Equal($$"""{"toolName":"Bash","toolInput":{"command":"ls -la"},"toolUseId":"toolu_1","cliSessionId":"{{Session}}"}""", body);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_not_followed_and_denies()
    {
        var api = new FakeApi(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = new Uri("https://evil.example/api/tool-approvals/check") },
        });

        var verdict = await PolicyCheckCommand.DecideAsync(BashInput, "https://api.sidehub.io", "sh_run_abc", api);

        Assert.Equal("deny", verdict.Decision);
        Assert.Contains("(HTTP 307)", verdict.Reason);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task An_unreachable_api_denies()
    {
        var api = new FakeApi(_ => throw new HttpRequestException("connection refused"));

        var verdict = await PolicyCheckCommand.DecideAsync(BashInput, "https://api.sidehub.io", "sh_run_abc", api);

        Assert.Equal(PolicyCheckCommand.Failure("SideHub is unreachable"), verdict);
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_tool_call_or_a_run_token()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));

        Assert.Equal(PolicyCheckCommand.Failure("no hook input on stdin"),
            await PolicyCheckCommand.DecideAsync(null, "https://api.sidehub.io", "sh_run_abc", api));
        Assert.Equal(PolicyCheckCommand.Failure("this terminal has no run token"),
            await PolicyCheckCommand.DecideAsync(BashInput, "https://api.sidehub.io", "sh_pty_abc", api));
        Assert.Empty(api.Requests);
    }

    // ---- sidehub-cli policy allow: the machine's MCP tools in a permissionless run ----

    private static CliLaunchPlan MachineMcpPlan(string cli, string[] args, CliLaunchPlan.StateReporting? reporting,
        CliLaunchPlan.ToolPolicy? policy = null) =>
        CliLaunchPlan.For(cli, args, "do it", new Version(0, 62, 0), () => Minted, reporting, null, policy, machineMcpHook: Program);

    [Fact]
    public void The_machine_mcp_hook_joins_the_state_hooks()
    {
        var plan = MachineMcpPlan("claude", ["-p", "--permission-mode", "default", "--allowedTools=Bash,Edit"], Reporting);

        Assert.Null(plan.Refusal);
        Assert.Equal("--settings", plan.Arguments[0]);
        Assert.Equal(CliStateHooks.ClaudeSettings(Program, reportState: true, allowMachineMcp: true), plan.Arguments[1]);
        Assert.Equal(
            [("AskUserQuestion", "cli-state claude waiting-input", 5), ("mcp__.*", "policy allow", 10)],
            Hooks(plan.Arguments[1])["PreToolUse"]);
    }

    [Fact]
    public void The_machine_mcp_hook_is_installed_without_state_reporting()
    {
        var hooks = Hooks(MachineMcpPlan("claude", ["-p"], reporting: null).Arguments[1]);

        Assert.Equal(["PreToolUse"], hooks.Keys);
        Assert.Equal([("mcp__.*", "policy allow", 10)], hooks["PreToolUse"]);
    }

    [Fact]
    public void A_tool_policy_keeps_the_mcp_tools_to_itself()
    {
        var plan = MachineMcpPlan("claude", ["-p"], Reporting, Policy);

        Assert.Null(plan.Refusal);
        Assert.Equal(CliStateHooks.ClaudeSettings(Program, reportState: true, Matcher), plan.Arguments[1]);
        Assert.DoesNotContain(Hooks(plan.Arguments[1])["PreToolUse"], h => h.Args == "policy allow");
    }

    [Fact]
    public void Without_the_right_no_machine_mcp_hook_is_installed() =>
        Assert.Equal(CliStateHooks.ClaudeSettings(Program),
            CliLaunchPlan.For("claude", ["-p"], null, null, () => Minted, Reporting).Arguments[1]);

    [Fact]
    public void Other_clis_get_no_machine_mcp_hook()
    {
        var plan = MachineMcpPlan("codex", ["exec", "--dangerously-bypass-approvals-and-sandbox"], reporting: null);

        Assert.Null(plan.Refusal);
        Assert.Equal(["exec", "--dangerously-bypass-approvals-and-sandbox", "do it"], plan.Arguments);
    }

    [Fact]
    public void The_allow_hook_answers_allow()
    {
        using var doc = JsonDocument.Parse(PolicyAllowCommand.HookOutput());
        var output = doc.RootElement.GetProperty("hookSpecificOutput");

        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        Assert.Equal("SideHub permissionless run", output.GetProperty("permissionDecisionReason").GetString());
    }
}

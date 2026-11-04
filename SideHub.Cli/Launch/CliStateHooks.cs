using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SideHub.Cli.Launch;

/// <summary>
/// The hooks <c>sidehub-cli launch</c> adds to a coding CLI so that it reports what it is doing (working,
/// waiting-input, idle) to the agent, through <c>sidehub-cli cli-state</c>. Only per-invocation options are used:
/// the user's own settings files are never touched, and their hooks keep running.
/// <list type="bullet">
/// <item>claude: <c>--settings &lt;json&gt;</c>, whose hooks merge with those of every settings file
/// (https://code.claude.com/docs/en/hooks, "Hook entries merge across settings levels"). Exec form (<c>args</c>,
/// Claude Code 2.1.139+): no shell, so the same JSON works on bash, cmd and PowerShell hosts.</item>
/// <item>codex: <c>-c notify=[…]</c>, codex's program run on <c>agent-turn-complete</c> with the event as its last
/// argument (https://learn.chatgpt.com/docs/config-file/config-advanced). It replaces a <c>notify</c> of the user's
/// config, so it is only added when the user has none. Codex hooks would also report turn starts, but run only
/// once the user trusted them in <c>/hooks</c>: not usable from the command line.</item>
/// <item>agy: no per-invocation option, only hooks files. An entry of its own, <see cref="AntigravityHookName"/>, is
/// kept in the user's global <c>~/.gemini/config/hooks.json</c> (<see cref="EnsureAntigravityHooks"/>), next to the
/// user's own entries. It runs in every agy, also outside SideHub, where it does nothing: the shell guard skips
/// <c>sidehub-cli</c> without a SideHub terminal, and agy ignores a hook whose program is gone.</item>
/// </list>
/// gemini and copilot read hooks from settings files only: they report no state.
/// <para>
/// A run's tool policy is enforced through the same claude <c>--settings</c> (see <see cref="PolicyCheckCommand"/>), and
/// a permissionless run without one is allowed the machine's MCP tools there (see <see cref="PolicyAllowCommand"/>).
/// </para>
/// </summary>
public static partial class CliStateHooks
{
    /// <summary>Seconds Claude Code waits for a hook; the notifier itself gives up after about one.</summary>
    private const int HookTimeoutSeconds = 5;

    /// <summary>Claude's notifications that mean a human is needed: a permission dialog, an MCP server asking for
    /// input. <c>idle_prompt</c> (waiting for the next prompt after a while) is not one: the turn already ended.</summary>
    public const string WaitingInputNotifications = "permission_prompt|elicitation_dialog|elicitation_url_dialog";

    /// <summary>Claude's <c>StopFailure</c> errors that mean the plan ran out: the turn ended on them once the CLI's own
    /// retries gave up (https://code.claude.com/docs/en/hooks). <c>rate_limit</c> also covers an organization's limit: it
    /// is treated the same.</summary>
    public const string LimitErrors = "rate_limit|billing_error";

    /// <summary>Claude's <c>SessionStart</c> sources that may change the session: a startup keeps the one given at launch,
    /// a compaction keeps its session.</summary>
    public const string SwitchedSessionSources = "resume|clear";

    /// <summary>
    /// The <c>--settings</c> JSON for claude. UserPromptSubmit and PostToolUse: working (PostToolUse also ends a
    /// permission wait once the tool ran); Notification on a permission or input request, PermissionRequest and
    /// PreToolUse of AskUserQuestion: waiting-input (the last two also say what is asked, see <see cref="CliPrompt"/>);
    /// Stop: idle; StopFailure on a usage limit: limit (doc 60); SessionStart on resume and clear: the session claude
    /// switched to (<c>/resume</c>, <c>/clear</c> leave the one given at launch, a startup keeps it). With a tool policy, a PreToolUse group also runs
    /// <c>sidehub-cli policy check</c> (<see cref="PolicyCheckCommand"/>) on the tools it matches; with
    /// <paramref name="allowMachineMcp"/>, one runs <c>sidehub-cli policy allow</c> (<see cref="PolicyAllowCommand"/>)
    /// on the MCP tools.
    /// </summary>
    /// <param name="program">This program's absolute path (<c>sidehub-cli</c>).</param>
    /// <param name="reportState">Whether to add the state hooks (false: no agent to report to).</param>
    /// <param name="toolPolicyMatcher">The run's tool policy (a regex over tool names), null for none.</param>
    /// <param name="declinedProjectMcpServers">Project MCP servers (<c>.mcp.json</c>) to start without, as answering "Continue
    /// without using this MCP server" would (see <see cref="ClaudeProjectMcp"/>); null or empty for none.</param>
    /// <param name="allowMachineMcp">Whether to allow every MCP tool (a permissionless run without a tool policy).</param>
    public static string ClaudeSettings(string program, bool reportState = true, string? toolPolicyMatcher = null,
        IReadOnlyList<string>? declinedProjectMcpServers = null, bool allowMachineMcp = false)
    {
        object Hook(string state) => new
        {
            type = "command",
            command = program,
            args = new[] { CliStateCommand.Name, "claude", state },
            timeout = HookTimeoutSeconds,
        };
        object Group(string state, string? matcher = null) => matcher is null
            ? new { hooks = new[] { Hook(state) } }
            : new { matcher, hooks = new[] { Hook(state) } };

        var hooks = new Dictionary<string, List<object>>();
        void Add(string hookEvent, object group)
        {
            if (!hooks.TryGetValue(hookEvent, out var groups))
                hooks[hookEvent] = groups = [];
            groups.Add(group);
        }

        if (reportState)
        {
            Add("UserPromptSubmit", Group(CliStateCommand.Working));
            Add("PreToolUse", Group(CliStateCommand.WaitingInput, "AskUserQuestion"));
            Add("PostToolUse", Group(CliStateCommand.Working));
            Add("Notification", Group(CliStateCommand.WaitingInput, WaitingInputNotifications));
            // Before a permission dialog: same state, with the tool and what it would run (CliPrompt)
            Add("PermissionRequest", Group(CliStateCommand.WaitingInput));
            Add("Stop", Group(CliStateCommand.Idle));
            Add("StopFailure", Group(CliStateCommand.Limit, LimitErrors));
            Add("SessionStart", Group(CliStateCommand.Session, SwitchedSessionSources));
        }
        object PolicyGroup(string matcher, string action, int timeout) => new
        {
            matcher,
            hooks = new[]
            {
                new
                {
                    type = "command",
                    command = program,
                    args = new[] { PolicyCheckCommand.Domain, action },
                    timeout,
                },
            },
        };
        if (toolPolicyMatcher is not null)
            Add("PreToolUse", PolicyGroup(toolPolicyMatcher, PolicyCheckCommand.Action, PolicyCheckCommand.HookTimeoutSeconds));
        if (allowMachineMcp)
            Add("PreToolUse", PolicyGroup(PolicyAllowCommand.Matcher, PolicyAllowCommand.Action, PolicyAllowCommand.HookTimeoutSeconds));

        return declinedProjectMcpServers is { Count: > 0 } declined
            ? JsonSerializer.Serialize(new { hooks, disabledMcpjsonServers = declined })
            : JsonSerializer.Serialize(new { hooks });
    }

    /// <summary>The <c>-c</c> value for codex: <c>notify=["&lt;program&gt;","cli-state","codex"]</c>, a TOML array
    /// (JSON string escapes are valid TOML basic-string escapes).</summary>
    public static string CodexNotify(string program) =>
        "notify=" + JsonSerializer.Serialize(new[] { program, CliStateCommand.Name, "codex" });

    [GeneratedRegex(@"^\s*(notify|""notify"")\s*=", RegexOptions.Multiline)]
    private static partial Regex TopLevelNotify();

    [GeneratedRegex(@"^\s*\[", RegexOptions.Multiline)]
    private static partial Regex TableHeader();

    /// <summary>Whether the codex config (TOML) sets its own top-level <c>notify</c>, which ours would replace.
    /// Unreadable config: assumed to, so the user's notify is never dropped.</summary>
    public static bool CodexConfigDefinesNotify(string configPath)
    {
        string text;
        try
        {
            if (!File.Exists(configPath))
                return false;
            text = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        var firstTable = TableHeader().Match(text);
        var topLevel = firstTable.Success ? text[..firstTable.Index] : text;
        return TopLevelNotify().IsMatch(topLevel);
    }

    /// <summary>The key of SideHub's entry in agy's hooks file.</summary>
    public const string AntigravityHookName = "sidehub";

    /// <summary>
    /// SideHub's entry in agy's hooks file: PreInvocation (before each model call): working; Stop: idle (see
    /// <see cref="CliStateCommand"/>, which reads the conversation from the hook's stdin). agy runs a hook's
    /// <c>command</c> through a shell: the program is quoted, and on Linux and macOS only started in a SideHub terminal.
    /// </summary>
    public static JsonObject AntigravityHook(string program)
    {
        string Command(string state)
        {
            if (OperatingSystem.IsWindows())
                return $"\"{program}\" {CliStateCommand.Name} agy {state}";
            var quoted = "'" + program.Replace("'", "'\\''") + "'";
            return $"[ -z \"${AgentNotifier.ChannelVariable}\" ] || {quoted} {CliStateCommand.Name} agy {state}";
        }
        JsonArray Handler(string state) => [new JsonObject { ["command"] = Command(state), ["timeout"] = HookTimeoutSeconds }];
        return new JsonObject
        {
            ["PreInvocation"] = Handler(CliStateCommand.Working),
            ["Stop"] = Handler(CliStateCommand.Idle),
        };
    }

    /// <summary>agy's global hooks file: <c>~/.gemini/config/hooks.json</c>; null without a home.</summary>
    public static string? AntigravityHooksPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".gemini", "config", "hooks.json");
    }

    /// <summary>
    /// Puts SideHub's entry (<see cref="AntigravityHook"/>) in agy's hooks file at <paramref name="path"/>, keeping the
    /// user's entries. Returns false when it already was there; throws on an unreadable or invalid file (left alone).
    /// </summary>
    public static bool EnsureAntigravityHooks(string path, string program)
    {
        var root = File.Exists(path) && File.ReadAllText(path) is { } text && !string.IsNullOrWhiteSpace(text)
            ? JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{path} is not a JSON object.")
            : new JsonObject();
        var hook = AntigravityHook(program);
        if (JsonNode.DeepEquals(root[AntigravityHookName], hook))
            return false;
        root[AntigravityHookName] = hook;
        ConfigFiles.Replace(path, root);
        return true;
    }

    /// <summary>codex's config.toml: <c>$CODEX_HOME/config.toml</c>, else <c>~/.codex/config.toml</c>.</summary>
    public static string CodexConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome
            ? codexHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(home, "config.toml");
    }
}

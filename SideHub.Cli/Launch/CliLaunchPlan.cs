namespace SideHub.Cli.Launch;

/// <summary>
/// What <c>sidehub-cli launch</c> runs for a coding CLI, and what it reports to the agent: the CLI session id
/// (so SideHub can later run <c>--resume &lt;id&gt;</c>) or, for codex and agy, which take no pre-set id, their launch.
/// Pure: the arguments are only inspected and extended, never interpreted by a shell.
/// </summary>
public sealed record CliLaunchPlan(string Cli, IReadOnlyList<string> Arguments, string? SessionId, bool ReportLaunch)
{
    /// <summary>gemini: the system settings file to run it with (<see cref="McpServers.Gemini"/>), null for none.</summary>
    public string? GeminiSystemSettings { get; init; }

    /// <summary>agy: its state hooks must be in its hooks file (<see cref="CliStateHooks.EnsureAntigravityHooks"/>):
    /// it takes none on the command line.</summary>
    public bool AntigravityHooks { get; init; }

    /// <summary>MCP servers that could not be given to the CLI, to show in the terminal.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Why the CLI must not be started (its run's tool policy cannot be enforced), null when it may.</summary>
    public string? Refusal { get; init; }

    public static readonly IReadOnlySet<string> KnownClis = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude", "codex", "gemini", "copilot", "agy",
    };

    /// <summary>First gemini release that accepts <c>--session-id</c> (and resumes a session by its UUID).</summary>
    public static readonly Version GeminiSessionIdVersion = new(0, 41, 0);

    // Subcommands that start no conversation: no session id is minted for them.
    private static readonly Dictionary<string, HashSet<string>> NonSessionSubcommands = new(StringComparer.Ordinal)
    {
        ["claude"] = ["auth", "config", "doctor", "install", "mcp", "migrate-installer", "plugin", "plugins", "setup-token", "update", "upgrade"],
        ["copilot"] = ["help", "init", "login", "logout", "mcp", "memories", "plugin", "plugins", "sessions", "update", "version"],
        ["gemini"] = ["extension", "extensions", "hook", "hooks", "mcp", "skill", "skills"],
        ["agy"] = ["agent", "agents", "changelog", "help", "install", "mcp", "mic-serve", "models", "plugin", "plugins", "remote-control", "update"],
        ["codex"] = ["a", "app-server", "apply", "archive", "cloud", "completion", "debug", "features", "help", "login", "logout", "mcp", "mcp-server", "sandbox", "unarchive"],
    };

    // codex subcommands without a TUI: no screen mode to choose.
    internal static readonly HashSet<string> HeadlessCodexSubcommands = ["exec", "e", "review"];

    /// <summary>Turns off the update offer codex's TUI opens with when a new version is out: a choice nobody makes in
    /// an unattended launch, which would wait on it.</summary>
    public const string CodexNoUpdateCheck = "check_for_update_on_startup=false";

    /// <summary>
    /// codex's TUI runs inline, like claude's: in its alternate screen the terminal keeps no scrollback, so scrolling
    /// in SideHub's terminal (wheel, or a swipe sent as arrow keys) walked through the prompt history instead of the
    /// conversation. Inline, the conversation stays in the terminal's scrollback. Older codex ignore the unknown key.
    /// </summary>
    public const string CodexInlineScreen = "tui.alternate_screen=never";

    /// <summary>codex exec's option to write its last answer to a file.</summary>
    public const string CodexOutputLastMessage = "--output-last-message";

    private static readonly HashSet<string> InfoOptions = ["--help", "-h", "--version", "-v", "-V"];

    // Options through which the caller already chose the session: the launcher leaves it alone.
    private static readonly HashSet<string> SessionOptions = ["--session-id", "--resume", "-r", "--continue", "--session-file", "--connect", "--from-pr", "--conversation"];
    private static readonly HashSet<string> SessionIdOptions = ["--session-id", "--resume", "-r", "--conversation"];

    /// <param name="cli">One of <see cref="KnownClis"/>.</param>
    /// <param name="arguments">The CLI's arguments, as given.</param>
    /// <param name="prompt">Appended as the last argument when set.</param>
    /// <param name="geminiVersion">The installed gemini's version, null when unknown.</param>
    /// <param name="newSessionId">Mints a session UUID.</param>
    /// <param name="stateReporting">How the CLI's state is reported to the agent (see <see cref="CliStateHooks"/>),
    /// null to add no hooks.</param>
    /// <param name="mcp">The run's MCP servers (see <see cref="McpServers"/>), null for none.</param>
    /// <param name="toolPolicy">The run's tool policy (see <see cref="PolicyCheckCommand"/>), null for none.</param>
    /// <param name="declinedProjectMcpServers">claude: project MCP servers to start without (nobody is there to answer
    /// their approval dialog, see <see cref="ClaudeProjectMcp"/>), null for none.</param>
    /// <param name="machineMcpHook">claude: the <c>sidehub-cli</c> (absolute path) whose hook allows the machine's MCP
    /// tools (see <see cref="PolicyAllowCommand"/>), null to leave them to claude's permission mode. Ignored under a
    /// tool policy, which checks them call by call.</param>
    /// <param name="unattended">Launched by SideHub for a run or a task, with nobody to answer yet: codex's TUI starts
    /// without its update offer (<see cref="CodexNoUpdateCheck"/>).</param>
    /// <param name="runAnswerFile">The file where the agent reads a run's last answer (absolute path), null outside a
    /// run: <c>codex exec</c> writes it (<c>--output-last-message</c>), since it announces no session whose transcript
    /// the agent could read.</param>
    public static CliLaunchPlan For(string cli, IReadOnlyList<string> arguments, string? prompt, Version? geminiVersion,
        Func<Guid> newSessionId, StateReporting? stateReporting = null, McpSetup? mcp = null, ToolPolicy? toolPolicy = null,
        IReadOnlyList<string>? declinedProjectMcpServers = null, string? machineMcpHook = null, bool unattended = false,
        string? runAnswerFile = null)
    {
        if (!KnownClis.Contains(cli))
            throw new ArgumentException($"Unknown CLI '{cli}'.", nameof(cli));

        var args = arguments.ToList();
        var startsConversation = !args.Any(InfoOptions.Contains)
            && !(args.Count > 0 && NonSessionSubcommands[cli].Contains(args[0]));

        string? sessionId = null;
        if (cli == "codex")
        {
            // `codex resume <id>`: the session is known; a new one is found by the agent from the launch.
            if (args.Count >= 2 && args[0] == "resume" && IsSessionId(args[1]))
                sessionId = args[1];
        }
        else if (ChosenSession(args, cli, out var chosen))
        {
            sessionId = chosen;
        }
        else if (startsConversation && AcceptsSessionId(cli, geminiVersion))
        {
            sessionId = newSessionId().ToString("D");
            args.InsertRange(0, ["--session-id", sessionId]);
        }

        // An option of exec, so right after it (the -c below go before it). The caller's own file is left alone.
        if (cli == "codex" && runAnswerFile is not null && startsConversation && args.Count > 0 && args[0] is "exec" or "e"
            && !SetsCodexOutputLastMessage(args))
            args.InsertRange(1, [CodexOutputLastMessage, runAnswerFile]);

        if (cli == "codex" && startsConversation && !(args.Count > 0 && HeadlessCodexSubcommands.Contains(args[0]))
            && !args.Contains("--no-alt-screen") && !SetsCodexConfig(args, "tui.alternate_screen"))
            args.InsertRange(0, ["-c", CodexInlineScreen]);
        if (cli == "codex" && unattended && startsConversation && !(args.Count > 0 && HeadlessCodexSubcommands.Contains(args[0]))
            && !SetsCodexConfig(args, "check_for_update_on_startup"))
            args.InsertRange(0, ["-c", CodexNoUpdateCheck]);

        // A command that starts no conversation runs no tool: the policy has nothing to check.
        var refusal = startsConversation && toolPolicy is not null ? ToolPolicyRefusal(cli, args, toolPolicy) : null;
        if (startsConversation && refusal is null)
            AddHooks(cli, args, stateReporting, toolPolicy, declinedProjectMcpServers,
                cli == "claude" && toolPolicy is null ? machineMcpHook : null);
        var antigravityHooks = cli == "agy" && startsConversation && refusal is null && stateReporting is not null;

        var warnings = new List<string>();
        string? geminiSettings = null;
        if (startsConversation && mcp is { Servers.Count: > 0 })
            geminiSettings = AddMcpServers(cli, args, mcp, warnings);

        if (prompt is not null)
            args.Add(prompt);

        // agy's session is told by its hooks (see CliStateCommand), but only the launch says when it exits.
        return new CliLaunchPlan(cli, args, sessionId, ReportLaunch: cli is "codex" or "agy" && startsConversation)
        {
            AntigravityHooks = antigravityHooks,
            GeminiSystemSettings = geminiSettings,
            Warnings = warnings,
            Refusal = refusal,
        };
    }

    /// <param name="Servers">The servers to give the CLI.</param>
    /// <param name="PosixShell">codex may start a stdio server through <c>/bin/sh</c> (not on Windows).</param>
    /// <param name="GeminiSystemSettings">The machine's gemini system settings, null when there are none.</param>
    public sealed record McpSetup(IReadOnlyList<McpServers.Server> Servers, bool PosixShell, string? GeminiSystemSettings);

    /// <summary>Adds the MCP options in front of the caller's arguments; returns gemini's system settings.</summary>
    private static string? AddMcpServers(string cli, List<string> args, McpSetup mcp, List<string> warnings)
    {
        switch (cli)
        {
            case "claude":
                args.InsertRange(0, McpServers.ClaudeArguments(mcp.Servers));
                return null;
            case "codex":
                args.InsertRange(0, McpServers.CodexArguments(mcp.Servers, mcp.PosixShell, warnings));
                return null;
            case "gemini":
                if (McpServers.Gemini(mcp.Servers, mcp.GeminiSystemSettings, warnings) is not { } gemini)
                    return null;
                args.InsertRange(0, gemini.Arguments);
                return gemini.Settings;
            default:
                warnings.Add($"{cli}: MCP servers {string.Join(", ", mcp.Servers.Select(s => s.Name))} left out: not supported for this CLI.");
                return null;
        }
    }

    /// <param name="Program">The <c>sidehub-cli</c> the hooks run (absolute path).</param>
    /// <param name="CodexNotifyTaken">The user's codex config sets its own <c>notify</c>, which ours would replace.</param>
    public sealed record StateReporting(string Program, bool CodexNotifyTaken);

    /// <param name="Matcher">The tools the policy covers: a regex over Claude tool names
    /// (<c>$SIDEHUB_TOOL_POLICY_MATCHER</c>).</param>
    /// <param name="Program">The <c>sidehub-cli</c> the policy hook runs (absolute path), null when it cannot be run
    /// by path.</param>
    public sealed record ToolPolicy(string Matcher, string? Program);

    /// <summary>
    /// Why a CLI with a tool policy cannot be started, null when the policy hook can be installed. The hook is what
    /// enforces the policy: without it the CLI would run every tool unchecked, so the launch fails closed.
    /// </summary>
    private static string? ToolPolicyRefusal(string cli, List<string> args, ToolPolicy policy) =>
        cli != "claude"
            ? $"this run has a tool policy that only claude can enforce: {cli} is not started."
        : policy.Program is null
            ? "cannot install the tool policy hook: this sidehub-cli cannot be run by its path (started through dotnet?): claude is not started."
        : SetsClaudeSettings(args)
            ? "cannot install the tool policy hook: the arguments already pass --settings, which claude would keep instead of ours: claude is not started."
        : null;

    /// <summary>
    /// Adds the hooks in front of the caller's arguments. Not when the caller already passes the same option:
    /// claude keeps only the last <c>--settings</c>, codex's <c>notify</c> is a single program. A tool policy is
    /// only given here once its hook can be installed (see <see cref="ToolPolicyRefusal"/>).
    /// </summary>
    private static void AddHooks(string cli, List<string> args, StateReporting? reporting, ToolPolicy? policy,
        IReadOnlyList<string>? declinedMcp, string? machineMcpHook)
    {
        switch (cli)
        {
            case "claude" when (reporting is not null || policy is not null || declinedMcp is { Count: > 0 } || machineMcpHook is not null)
                               && !SetsClaudeSettings(args):
                // Without hooks, the program is never written to the settings.
                var program = reporting?.Program ?? policy?.Program ?? machineMcpHook ?? "";
                args.InsertRange(0, ["--settings", CliStateHooks.ClaudeSettings(
                    program, reportState: reporting is not null, policy?.Matcher, declinedMcp, allowMachineMcp: machineMcpHook is not null)]);
                break;
            case "codex" when reporting is { CodexNotifyTaken: false } && !SetsCodexNotify(args):
                args.InsertRange(0, ["-c", CliStateHooks.CodexNotify(reporting.Program)]);
                break;
        }
    }

    private static bool SetsClaudeSettings(List<string> args) => args.Any(a => SplitOption(a).Name == "--settings");

    private static bool SetsCodexNotify(List<string> args) => SetsCodexConfig(args, "notify");

    /// <summary>A <c>-o</c> / <c>--output-last-message</c> (or <c>--output-last-message=…</c>, <c>-o…</c>) among the arguments.</summary>
    private static bool SetsCodexOutputLastMessage(List<string> args) =>
        args.Any(a => SplitOption(a).Name == CodexOutputLastMessage || a.StartsWith("-o", StringComparison.Ordinal));

    /// <summary>A <c>-c key=…</c> / <c>--config key=…</c> (or <c>--config=key=…</c>) among the arguments.</summary>
    private static bool SetsCodexConfig(List<string> args, string key)
    {
        for (var i = 0; i < args.Count; i++)
        {
            string? value = args[i] is "-c" or "--config" ? (i + 1 < args.Count ? args[i + 1] : null)
                : args[i].StartsWith("--config=", StringComparison.Ordinal) ? args[i]["--config=".Length..]
                : args[i].StartsWith("-c", StringComparison.Ordinal) && args[i].Length > 2 ? args[i][2..]
                : null;
            if (value is not null && value.TrimStart().StartsWith(key, StringComparison.Ordinal)
                && value.TrimStart()[key.Length..].TrimStart().StartsWith('='))
                return true;
        }
        return false;
    }

    private static bool AcceptsSessionId(string cli, Version? geminiVersion) => cli switch
    {
        "agy" => false,
        "gemini" => geminiVersion is not null && geminiVersion >= GeminiSessionIdVersion,
        _ => true,
    };

    /// <summary>Whether the arguments already pick the session; <paramref name="sessionId"/> is its UUID when given.</summary>
    private static bool ChosenSession(List<string> args, string cli, out string? sessionId)
    {
        sessionId = null;
        var chosen = false;
        for (var i = 0; i < args.Count; i++)
        {
            var (name, value) = SplitOption(args[i]);
            // claude's and agy's -c is --continue; other CLIs give -c another meaning.
            if (!SessionOptions.Contains(name) && !(cli is "claude" or "agy" && name == "-c"))
                continue;
            chosen = true;
            if (!SessionIdOptions.Contains(name))
                continue;
            value ??= i + 1 < args.Count ? args[i + 1] : null;
            if (IsSessionId(value))
                sessionId = value;
        }
        return chosen;
    }

    private static (string Name, string? Value) SplitOption(string arg)
    {
        var eq = arg.IndexOf('=');
        return arg.StartsWith("--", StringComparison.Ordinal) && eq > 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
    }

    /// <summary>The agent only accepts canonical UUIDs (they end up in file names).</summary>
    public static bool IsSessionId(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);
}

using SideHub.Agent.Usage;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>`sidehub-cli launch` pre-sets the CLI session id where it can (so SideHub can resume it on any OS),
/// hands the prompt over as one argument, and never resolves a CLI to SideHub's own wrappers.</summary>
public class CliLaunchTests : IDisposable
{
    private static readonly Guid Minted = Guid.Parse("8c1e7a52-0d3b-4f6e-9a21-5b7c3d9e0f14");
    private const string Existing = "01a0f8a0-0e7f-7860-88b7-30a5ee25df7c";
    private static readonly Version RecentGemini = new(0, 62, 0);

    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-launch-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CliLaunchPlan Plan(string cli, string[] args, string? prompt = null, Version? gemini = null) =>
        CliLaunchPlan.For(cli, args, prompt, gemini ?? RecentGemini, () => Minted);

    [Theory]
    [InlineData("claude")]
    [InlineData("copilot")]
    [InlineData("gemini")]
    public void A_new_conversation_gets_a_pre_set_session_id(string cli)
    {
        var plan = Plan(cli, ["--model", "m"]);

        Assert.Equal(["--session-id", Minted.ToString("D"), "--model", "m"], plan.Arguments);
        Assert.Equal(Minted.ToString("D"), plan.SessionId);
        Assert.False(plan.ReportLaunch);
    }

    [Fact]
    public void Agy_gets_no_session_id_and_reports_its_launch_and_exit()
    {
        var plan = Plan("agy", ["--model", "gemini-3.8-flash-high", "-p"], "fix it");

        Assert.Equal(["--model", "gemini-3.8-flash-high", "-p", "fix it"], plan.Arguments);
        Assert.Null(plan.SessionId);
        Assert.True(plan.ReportLaunch);
    }

    [Theory]
    [InlineData("agy", new[] { "--conversation", Existing })]
    [InlineData("agy", new[] { "--conversation=" + Existing })]
    public void A_resumed_agy_conversation_is_announced(string cli, string[] args)
    {
        var plan = Plan(cli, args);

        Assert.Equal(args, plan.Arguments);
        Assert.Equal(Existing, plan.SessionId);
    }

    [Theory]
    [InlineData("models")]
    [InlineData("mcp")]
    [InlineData("update")]
    public void Agy_subcommands_start_no_conversation(string subcommand)
    {
        var plan = Plan("agy", [subcommand]);

        Assert.Equal([subcommand], plan.Arguments);
        Assert.False(plan.ReportLaunch);
    }

    [Fact]
    public void The_prompt_is_the_last_argument_untouched()
    {
        const string prompt = "line \"one\"\nline two with $HOME & %PATH% ' `x`";

        var plan = Plan("copilot", ["--allow-all", "-i"], prompt);

        Assert.Equal(["--session-id", Minted.ToString("D"), "--allow-all", "-i", prompt], plan.Arguments);
    }

    [Theory]
    [InlineData("claude", new[] { "--resume", Existing })]
    [InlineData("claude", new[] { "-r", Existing })]
    [InlineData("copilot", new[] { "--resume=" + Existing })]
    [InlineData("gemini", new[] { "--session-id", Existing })]
    public void A_session_given_by_the_caller_is_kept_and_announced(string cli, string[] args)
    {
        var plan = Plan(cli, args);

        Assert.Equal(args, plan.Arguments);
        Assert.Equal(Existing, plan.SessionId);
    }

    [Theory]
    [InlineData("claude", new[] { "--continue" })]
    [InlineData("claude", new[] { "-c" })]
    [InlineData("copilot", new[] { "--resume" })]
    [InlineData("gemini", new[] { "--resume", "latest" })]
    public void A_session_picked_otherwise_is_left_alone(string cli, string[] args)
    {
        var plan = Plan(cli, args);

        Assert.Equal(args, plan.Arguments);
        Assert.Null(plan.SessionId);
    }

    [Theory]
    [InlineData("claude", new[] { "--version" })]
    [InlineData("claude", new[] { "mcp", "list" })]
    [InlineData("copilot", new[] { "login" })]
    [InlineData("gemini", new[] { "-h" })]
    [InlineData("codex", new[] { "login" })]
    public void Commands_that_start_no_conversation_are_run_as_given(string cli, string[] args)
    {
        var plan = Plan(cli, args);

        Assert.Equal(args, plan.Arguments);
        Assert.Null(plan.SessionId);
        Assert.False(plan.ReportLaunch);
    }

    [Fact]
    public void An_old_gemini_gets_no_session_id()
    {
        var plan = Plan("gemini", [], gemini: new Version(0, 40, 1));

        Assert.Empty(plan.Arguments);
        Assert.Null(plan.SessionId);
    }

    [Fact]
    public void A_gemini_of_unknown_version_gets_no_session_id()
    {
        var plan = CliLaunchPlan.For("gemini", [], null, geminiVersion: null, () => Minted);

        Assert.Null(plan.SessionId);
    }

    [Fact]
    public void A_new_codex_session_is_reported_as_a_launch()
    {
        var plan = Plan("codex", ["--model", "m"], "do it");

        Assert.Equal(["-c", CliLaunchPlan.CodexInlineScreen, "--model", "m", "do it"], plan.Arguments);
        Assert.Null(plan.SessionId);
        Assert.True(plan.ReportLaunch);
    }

    [Fact]
    public void A_resumed_codex_session_is_announced()
    {
        var plan = Plan("codex", ["resume", Existing]);

        Assert.Equal(["-c", CliLaunchPlan.CodexInlineScreen, "resume", Existing], plan.Arguments);
        Assert.Equal(Existing, plan.SessionId);
        Assert.True(plan.ReportLaunch);
    }

    [Fact]
    public void An_unattended_codex_starts_without_its_update_offer()
    {
        var plan = CliLaunchPlan.For("codex", ["--model", "m"], "do it", null, () => Minted, unattended: true);

        Assert.Equal(["-c", CliLaunchPlan.CodexNoUpdateCheck, "-c", CliLaunchPlan.CodexInlineScreen, "--model", "m", "do it"],
            plan.Arguments);
    }

    [Theory]
    [InlineData("exec", "--json")]
    [InlineData("-c", "check_for_update_on_startup=true")]
    public void The_update_offer_is_left_to_codex_without_a_tui_and_to_the_caller(params string[] args) =>
        Assert.DoesNotContain(CliLaunchPlan.CodexNoUpdateCheck, CliLaunchPlan.For("codex", args, null, null, () => Minted, unattended: true).Arguments);

    [Theory]
    [InlineData("exec", "--json")]
    [InlineData("e")]
    [InlineData("review")]
    [InlineData("--no-alt-screen")]
    [InlineData("-c", "tui.alternate_screen=always")]
    [InlineData("--config=tui.alternate_screen=auto")]
    public void Codex_screen_mode_is_left_to_commands_without_a_tui_and_to_the_caller(params string[] args) =>
        Assert.Equal(args, Plan("codex", args).Arguments);

    [Fact]
    public void An_id_that_is_not_a_uuid_is_never_announced()
    {
        var plan = Plan("claude", ["--resume", "../../etc/passwd"]);

        Assert.Null(plan.SessionId);
    }

    [Fact]
    public void Unknown_clis_are_refused() =>
        Assert.Throws<ArgumentException>(() => Plan("bash", []));

    [Fact]
    public void The_real_cli_is_found_after_the_wrappers()
    {
        var wrappers = Executable("wrappers", "claude");
        var real = Executable("bin", "claude");

        var found = RealCli.FindUnix("claude", $"relative:{Path.GetDirectoryName(wrappers)}:{Path.GetDirectoryName(real)}",
            [Path.GetDirectoryName(wrappers)!]);

        Assert.Equal(real, found);
    }

    [Fact]
    public void A_link_to_a_wrapper_is_skipped()
    {
        var wrapper = Executable("wrappers", "claude");
        var links = Directory.CreateDirectory(Path.Combine(_dir, "links")).FullName;
        File.CreateSymbolicLink(Path.Combine(links, "claude"), wrapper);

        Assert.Null(RealCli.FindUnix("claude", links, [Path.GetDirectoryName(wrapper)!]));
    }

    [Fact]
    public void An_npm_shim_is_run_through_its_script()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "npm")).FullName;
        var script = Path.Combine(bin, "node_modules", "@github", "copilot", "index.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "");
        var shim = Path.Combine(bin, "copilot.cmd");
        File.WriteAllText(shim, "@ECHO off\r\nendLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@github\\copilot\\index.js\" %*\r\n");

        var found = RealCli.NpmShimScript(shim);

        Assert.Equal(script, found);
    }

    [Fact]
    public void The_version_comes_from_the_cli_package()
    {
        var package = Directory.CreateDirectory(Path.Combine(_dir, "node_modules", "@google", "gemini-cli")).FullName;
        File.WriteAllText(Path.Combine(package, "package.json"), """{"name":"@google/gemini-cli","version":"0.41.2-nightly"}""");
        var dist = Directory.CreateDirectory(Path.Combine(package, "dist")).FullName;
        File.WriteAllText(Path.Combine(dist, "package.json"), """{"type":"module"}""");

        Assert.Equal(new Version(0, 41, 2), RealCli.PackageVersion(Path.Combine(dist, "index.js"), "@google/gemini-cli"));
    }

    [Theory]
    [InlineData("/s/2026/10/01/rollout-2026-10-01T18-00-34-01a0f8a0-0e7f-7860-88b7-30a5ee25df7c.jsonl", Existing)]
    [InlineData("/s/2026/10/01/rollout-2026-10-01T18-00-34-not-a-uuid.jsonl", null)]
    [InlineData("/s/other-01a0f8a0-0e7f-7860-88b7-30a5ee25df7c.jsonl", null)]
    public void The_codex_session_id_is_read_from_the_rollout_name(string path, string? expected) =>
        Assert.Equal(expected, CodexRolloutHarvester.SessionIdOf(path));

    [Fact]
    public void New_codex_sessions_are_found_by_directory_and_start_time()
    {
        using var codex = new CodexTemp();
        var launchedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        codex.AddRollout(launchedAt.AddMinutes(-10), "/repo");            // before the launch
        codex.AddRollout(launchedAt.AddMinutes(2), "/elsewhere");         // another directory
        codex.AddRollout(launchedAt.AddMinutes(1), "/repo", source: "vscode"); // not a CLI session
        var mine = codex.AddRollout(launchedAt.AddMinutes(3), "/repo");

        var found = new CodexRolloutHarvester(codex.Root).SessionsStartedIn("/repo/", launchedAt);

        Assert.Equal([mine], found);
        Assert.NotNull(CodexRolloutHarvester.SessionIdOf(mine));
    }

    [Fact]
    public void Pipe_names_only_take_plain_ids()
    {
        Assert.Equal("sidehub-a1-run-1", NotifyFifo.PipeNameFor("a1", "run-1"));
        Assert.Throws<ArgumentException>(() => NotifyFifo.PipeNameFor("a1", @"..\x"));
        Assert.Throws<ArgumentException>(() => NotifyFifo.PipeNameFor("a b", "run-1"));
    }

    [Fact]
    public void Sidehub_cli_reads_the_secret_the_agent_gives_the_terminal() =>
        Assert.Equal(NotifyFifo.SecretVariable, AgentNotifier.SecretVariable);

    [Fact]
    public async Task Notifications_reach_the_agent_through_a_named_pipe()
    {
        // Windows has no FIFO: the agent listens on a pipe (emulated by .NET on Unix) with the same options.
        var name = NotifyFifo.PipeNameFor("test", Guid.NewGuid().ToString("N"));
        await using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.In, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        var connected = server.WaitForConnectionAsync();

        var secret = NotifyFifo.NewSecret();
        var previous = Environment.GetEnvironmentVariable(AgentNotifier.ChannelVariable);
        var previousSecret = Environment.GetEnvironmentVariable(AgentNotifier.SecretVariable);
        Environment.SetEnvironmentVariable(AgentNotifier.ChannelVariable, NotifyFifo.PipePrefix + name);
        Environment.SetEnvironmentVariable(AgentNotifier.SecretVariable, secret);
        try
        {
            AgentNotifier.SessionStarted("copilot", Existing);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentNotifier.ChannelVariable, previous);
            Environment.SetEnvironmentVariable(AgentNotifier.SecretVariable, previousSecret);
        }

        await connected;
        using var reader = new StreamReader(server);
        var notification = FifoNotification.Parse((await reader.ReadLineAsync())!, secret, out _);
        Assert.Equal(new FifoNotification.CliSessionStarted("copilot", Existing), notification);
    }

    [Fact]
    public void A_base64_prompt_is_decoded_and_removed_from_the_arguments()
    {
        const string prompt = "Traite \"ça\" & 100%\nfin";
        string[] args = ["--prompt-base64", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prompt)), "claude", "--model", "m"];

        Assert.True(LaunchCommand.TryReadPrompt(ref args, out var decoded, out _));
        Assert.Equal(prompt, decoded);
        Assert.Equal(["claude", "--model", "m"], args);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("")]
    [InlineData("/w==")] // a lone 0xFF byte: not UTF-8
    public void A_bad_base64_prompt_is_refused(string value)
    {
        string[] args = ["--prompt-base64", value, "claude"];

        Assert.False(LaunchCommand.TryReadPrompt(ref args, out _, out var problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("e")]
    public void A_runs_codex_exec_writes_its_last_answer_where_the_agent_reads_it(string exec)
    {
        var plan = CliLaunchPlan.For("codex", [exec, "--json", "--model", "m"], "do it", null, () => Minted,
            unattended: true, runAnswerFile: "/repo/.sidehub/run/answers/a/run-1.txt");

        Assert.Equal([exec, CliLaunchPlan.CodexOutputLastMessage, "/repo/.sidehub/run/answers/a/run-1.txt", "--json", "--model", "m", "do it"],
            plan.Arguments);
    }

    [Theory]
    [InlineData("--model", "m")]                                     // the TUI: no such option
    [InlineData("exec", "-o", "/mine.txt")]                         // the caller's own file
    [InlineData("exec", "--output-last-message=/mine.txt")]
    [InlineData("exec", "--help")]
    public void The_answer_file_is_only_given_to_codex_exec_that_sets_none(params string[] args) =>
        Assert.DoesNotContain("/answer.txt",
            CliLaunchPlan.For("codex", args, null, null, () => Minted, runAnswerFile: "/answer.txt").Arguments);

    [Fact]
    public void A_cli_never_sees_the_runs_answer_file()
    {
        // A codex exec the run's CLI launches (nested) must not write its own answer in place of the run's
        var env = new Dictionary<string, string?> { [LaunchCommand.RunAnswerFileVariable] = "/repo/.sidehub/run/answers/a/run-1.txt" };

        LaunchCommand.CliEnvironment(env, "codex", nested: false);

        Assert.False(env.ContainsKey(LaunchCommand.RunAnswerFileVariable));
    }

    [Fact]
    public void The_terminal_cli_is_marked_and_keeps_its_way_to_the_agent()
    {
        var env = new Dictionary<string, string?>
        {
            [LaunchCommand.PromptVariable] = "do it",
            [AgentNotifier.ChannelVariable] = "/tmp/fifo",
            [AgentNotifier.SecretVariable] = "s3cret",
        };

        LaunchCommand.CliEnvironment(env, "claude", nested: false);

        Assert.False(env.ContainsKey(LaunchCommand.PromptVariable));
        Assert.Equal("claude", env[LaunchCommand.LaunchedCliVariable]);
        Assert.Equal("/tmp/fifo", env[AgentNotifier.ChannelVariable]);
        Assert.Equal("s3cret", env[AgentNotifier.SecretVariable]);
    }

    [Fact]
    public void A_cli_started_by_the_terminal_cli_cannot_speak_for_it()
    {
        // e.g. the run's claude runs `codex exec`: codex's session, state and exit must not replace claude's.
        var env = new Dictionary<string, string?>
        {
            [LaunchCommand.LaunchedCliVariable] = "claude",
            [AgentNotifier.ChannelVariable] = "/tmp/fifo",
            [AgentNotifier.SecretVariable] = "s3cret",
        };

        LaunchCommand.CliEnvironment(env, "codex", nested: true);

        Assert.False(env.ContainsKey(AgentNotifier.ChannelVariable));
        Assert.False(env.ContainsKey(AgentNotifier.SecretVariable));
        Assert.Equal("claude", env[LaunchCommand.LaunchedCliVariable]);
    }

    private string Executable(string folder, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, folder)).FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SideHub.Cli.Launch;

/// <summary>
/// <c>sidehub-cli launch [--prompt-env | --prompt-base64 &lt;b64&gt;] &lt;cli&gt; [args…]</c>: starts a coding CLI in
/// the current terminal and tells the agent which session it runs (see <see cref="CliLaunchPlan"/>).
/// <para>
/// The line SideHub types to start a CLI is then the same in bash, cmd and PowerShell: no quotes, no
/// <c>$</c>. With <c>--prompt-env</c> the prompt comes from <c>$SIDEHUB_PTY_PROMPT</c> and is handed to the CLI
/// as one argument, never parsed by a shell (cmd.exe cannot carry quotes, <c>%</c> or newlines in an argument).
/// </para>
/// </summary>
public static class LaunchCommand
{
    public const string PromptVariable = "SIDEHUB_PTY_PROMPT";
    private const string WrappersVariable = "SIDEHUB_CLI_WRAPPERS";

    /// <summary>Set in the environment of the CLI a launch starts (to its name): a launch that finds it was started
    /// by that CLI, or by a process of it (see <see cref="CliEnvironment"/>).</summary>
    public const string LaunchedCliVariable = "SIDEHUB_LAUNCHED_CLI";

    /// <summary>Set by the agent in a run's terminal: the file where it reads the run's last answer (see
    /// <see cref="CliLaunchPlan.For"/>). Only the terminal's CLI writes it: the CLI does not see it.</summary>
    public const string RunAnswerFileVariable = "SIDEHUB_RUN_ANSWER_FILE";

    public static int Run(string[] args)
    {
        if (!TryReadPrompt(ref args, out var prompt, out var problem))
        {
            Console.Error.WriteLine($"sidehub-cli launch: {problem}");
            return 2;
        }
        if (args.Length == 0 || !CliLaunchPlan.KnownClis.Contains(args[0]))
        {
            Console.Error.WriteLine($"Usage: sidehub-cli launch [--prompt-env | --prompt-base64 <utf-8 base64>] <{string.Join("|", CliLaunchPlan.KnownClis)}> [args...]");
            return 2;
        }
        var cli = args[0];
        // Started by the CLI of this terminal (a sub-agent, a review it asks another CLI for): the terminal's CLI
        // stays the one SideHub follows, this one is neither announced nor reports its state.
        var nested = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LaunchedCliVariable));

        var target = RealCli.Resolve(cli, Environment.GetEnvironmentVariable("PATH"), WrapperDirectories());
        if (target is null)
        {
            Console.Error.WriteLine($"sidehub-cli launch: '{cli}' is not installed (not found in PATH).");
            return 127;
        }

        var geminiVersion = cli == "gemini" ? RealCli.PackageVersion(target.ScriptPath, "@google/gemini-cli") : null;
        var warnings = new List<string>();
        // Launched by SideHub while nobody watches (a run, a task of the queue): claude's first-launch questions in this
        // folder get the answer a person would give (start without its undecided MCP servers; the trust question below).
        var unattended = ClaudeWorkspaceTrust.Applies(cli, args[1..],
            Environment.GetEnvironmentVariable(ClaudeWorkspaceTrust.RunIdVariable),
            Environment.GetEnvironmentVariable(ClaudeWorkspaceTrust.TaskIdVariable));
        var declinedMcp = unattended ? DeclinedProjectMcpServers() : null;
        var runId = Environment.GetEnvironmentVariable(ClaudeWorkspaceTrust.RunIdVariable);
        var taskId = Environment.GetEnvironmentVariable(ClaudeWorkspaceTrust.TaskIdVariable);
        var unattendedCodex = CodexWorkspaceTrust.Applies(cli, args[1..], runId, taskId);
        var plan = CliLaunchPlan.For(cli, args[1..], prompt, geminiVersion, Guid.NewGuid, nested ? null : StateReporting(cli), McpSetup(cli, warnings),
            ToolPolicy(), declinedMcp, MachineMcpHook(), unattended: unattendedCodex, runAnswerFile: nested ? null : RunAnswerFile());
        if (plan.Refusal is { } refusal)
        {
            Console.Error.WriteLine($"sidehub-cli launch: {refusal}");
            return 2;
        }
        foreach (var warning in warnings.Concat(plan.Warnings))
            Console.Error.WriteLine($"sidehub-cli launch: {warning}");

        // Started in the folder SideHub opened this terminal in (the agent's repository): the trust question is answered
        // too, as the person sees it neither in Conversation mode nor before their first message ends the session.
        var openedFolder = ClaudeWorkspaceTrust.InOpenedFolder(
            Environment.GetEnvironmentVariable(ClaudeWorkspaceTrust.OpenedFolderVariable), Directory.GetCurrentDirectory());
        if (ClaudeWorkspaceTrust.Applies(cli, args[1..], runId, taskId, openedFolder))
            TrustWorkspace();
        if (AntigravityWorkspaceTrust.Applies(cli, args[1..], runId, taskId, openedFolder))
            TrustAntigravityWorkspace();
        if (CodexWorkspaceTrust.Applies(cli, args[1..], runId, taskId, openedFolder))
            TrustRepository("codex", CodexWorkspaceTrust.ConfigPath(), CodexWorkspaceTrust.Accept);
        if (CopilotWorkspaceTrust.Applies(cli, args[1..], runId, taskId, openedFolder))
            TrustRepository("copilot", CopilotWorkspaceTrust.ConfigPath(), CopilotWorkspaceTrust.Accept);
        if (plan.AntigravityHooks && HookProgram() is { } hookProgram)
            InstallAntigravityHooks(hookProgram);

        if (plan.SessionId is { } sessionId && !nested)
            AgentNotifier.SessionStarted(cli, sessionId);

        var startInfo = new ProcessStartInfo(target.FileName) { UseShellExecute = false };
        foreach (var arg in target.LeadingArguments.Concat(plan.Arguments))
            startInfo.ArgumentList.Add(arg);
        CliEnvironment(startInfo.Environment, cli, nested);

        var geminiSettingsDirectory = plan.GeminiSystemSettings is { } settings ? WriteGeminiSettings(settings, startInfo) : null;
        try
        {
            return Start(cli, startInfo, target, plan, announce: !nested);
        }
        finally
        {
            if (geminiSettingsDirectory is not null)
                try { Directory.Delete(geminiSettingsDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private static int Start(string cli, ProcessStartInfo startInfo, RealCli.Target target, CliLaunchPlan plan, bool announce)
    {
        // Ctrl+C reaches every process of the terminal: it is the CLI's to handle, the launcher waits.
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context => context.Cancel = true);

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // e.g. Windows caps a command line at 32,767 characters, prompt included.
            Console.Error.WriteLine($"sidehub-cli launch: could not start {target.FileName}: {ex.Message}");
            return 126;
        }
        using var _ = process;
        if (plan.ReportLaunch && announce)
            AgentNotifier.Launched(cli, Directory.GetCurrentDirectory(), process.Id);
        process.WaitForExit();
        // The session's files are complete: the agent reports its final usage.
        if (announce && (plan.SessionId is not null || plan.ReportLaunch))
            AgentNotifier.Exited(cli, plan.SessionId);
        return process.ExitCode;
    }

    /// <summary>
    /// The environment the CLI starts with. The terminal's CLI is marked (<see cref="LaunchedCliVariable"/>): whatever it
    /// launches in turn knows it is nested. A nested CLI loses the way to the agent, so nothing it runs (agy's global
    /// hooks, a CLI it launches itself) speaks for the terminal's CLI: its session, state, usage and exit stay that one's.
    /// </summary>
    public static void CliEnvironment(IDictionary<string, string?> environment, string cli, bool nested)
    {
        // The CLI must not see the prompt twice (some read their environment into sub-processes).
        environment.Remove(PromptVariable);
        // Nor the run's answer file: a CLI it launches would write its own answer there.
        environment.Remove(RunAnswerFileVariable);
        if (nested)
        {
            environment.Remove(AgentNotifier.ChannelVariable);
            environment.Remove(AgentNotifier.SecretVariable);
        }
        else
        {
            environment[LaunchedCliVariable] = cli;
        }
    }

    /// <summary>The run's answer file (<c>$SIDEHUB_RUN_ANSWER_FILE</c>, set by the agent), null outside a run.</summary>
    private static string? RunAnswerFile() =>
        Environment.GetEnvironmentVariable(RunAnswerFileVariable) is { Length: > 0 } path && Path.IsPathFullyQualified(path)
            ? path
            : null;

    /// <summary>Best effort: without it, claude asks about each of them and someone answers in the terminal.</summary>
    private static IReadOnlyList<string>? DeclinedProjectMcpServers()
    {
        try
        {
            return ClaudeProjectMcp.Undecided(Directory.GetCurrentDirectory(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"sidehub-cli launch: claude may ask about the project's MCP servers: {ex.Message}");
            return null;
        }
    }

    /// <summary>Best effort: without it, claude asks whether to trust the folder and someone answers in the terminal.</summary>
    private static void TrustWorkspace()
    {
        if (ClaudeWorkspaceTrust.ConfigPath() is not { } configPath)
            return;
        try
        {
            ClaudeWorkspaceTrust.Accept(configPath, Directory.GetCurrentDirectory());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"sidehub-cli launch: claude may ask whether to trust this folder: {ex.Message}");
        }
    }

    /// <summary>Best effort: without it, agy asks whether to trust the folder and someone answers in the terminal.</summary>
    private static void TrustAntigravityWorkspace()
    {
        if (AntigravityWorkspaceTrust.SettingsPath() is not { } settingsPath)
            return;
        try
        {
            AntigravityWorkspaceTrust.Accept(settingsPath, Directory.GetCurrentDirectory());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"sidehub-cli launch: agy may ask whether to trust this folder: {ex.Message}");
        }
    }

    /// <summary>Best effort: without it, <paramref name="cli"/> asks whether to trust the repository and someone answers
    /// in the terminal. The repository is the one codex and copilot check (<see cref="GitTrustRoot"/>).</summary>
    private static void TrustRepository(string cli, string? configPath, Func<string, string, bool> accept)
    {
        if (configPath is null)
            return;
        try
        {
            accept(configPath, GitTrustRoot.Of(Directory.GetCurrentDirectory()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"sidehub-cli launch: {cli} may ask whether to trust this folder: {ex.Message}");
        }
    }

    /// <summary>Best effort: without them, SideHub does not see what agy is doing nor which conversation it runs.</summary>
    private static void InstallAntigravityHooks(string program)
    {
        if (CliStateHooks.AntigravityHooksPath() is not { } hooksPath)
            return;
        try
        {
            CliStateHooks.EnsureAntigravityHooks(hooksPath, program);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"sidehub-cli launch: agy's state stays unknown to SideHub: cannot add its hooks to {hooksPath}: {ex.Message}");
        }
    }

    /// <summary>The CLI's hooks call this program back (<c>sidehub-cli cli-state</c>) from inside the terminal, to
    /// tell the agent what the CLI is doing. Only in a SideHub terminal: elsewhere there is no agent to tell.</summary>
    private static CliLaunchPlan.StateReporting? StateReporting(string cli)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AgentNotifier.ChannelVariable)) || HookProgram() is not { } program)
            return null;
        var codexNotifyTaken = cli == "codex" && CliStateHooks.CodexConfigDefinesNotify(CliStateHooks.CodexConfigPath());
        return new CliLaunchPlan.StateReporting(program, codexNotifyTaken);
    }

    /// <summary>The run's tool policy (<c>$SIDEHUB_TOOL_POLICY_MATCHER</c>, set by the backend for a workflow step),
    /// null when it has none. Unlike the state hooks, it needs no agent channel: only this program.</summary>
    private static CliLaunchPlan.ToolPolicy? ToolPolicy() =>
        Environment.GetEnvironmentVariable(PolicyCheckCommand.MatcherVariable) is { Length: > 0 } matcher
            ? new CliLaunchPlan.ToolPolicy(matcher, HookProgram())
            : null;

    /// <summary>This program, to allow the machine's MCP tools in a permissionless claude run
    /// (<c>$SIDEHUB_ALLOW_MACHINE_MCP</c>, set by the backend); null when the run has no such right or the hook cannot
    /// be run. A tool policy wins over it (see <see cref="CliLaunchPlan.For"/>).</summary>
    private static string? MachineMcpHook() =>
        Environment.GetEnvironmentVariable(PolicyAllowCommand.Variable) == "1" ? HookProgram() : null;

    /// <summary>This program's absolute path, for the CLI's hooks to run it; null when it cannot be run by path.</summary>
    private static string? HookProgram() =>
        Environment.ProcessPath is { } program && Path.IsPathFullyQualified(program)
            // Run through `dotnet sidehub-cli.dll` (development), the process is dotnet itself.
            && Path.GetFileNameWithoutExtension(program).Equals("sidehub-cli", StringComparison.OrdinalIgnoreCase)
            ? program
            : null;

    /// <summary>The run's MCP servers (<c>$SIDEHUB_PTY_MCP_SERVERS</c>, set by the agent), null when there are none.</summary>
    private static CliLaunchPlan.McpSetup? McpSetup(string cli, List<string> warnings)
    {
        var servers = McpServers.Parse(Environment.GetEnvironmentVariable(McpServers.Variable), warnings);
        if (servers.Count == 0)
            return null;
        string? geminiSettings = null;
        if (cli == "gemini")
        {
            var path = McpServers.GeminiSystemSettingsPath();
            try
            {
                geminiSettings = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"gemini: MCP servers left out: cannot read the machine's gemini system settings ({path}).");
                return null;
            }
        }
        return new CliLaunchPlan.McpSetup(servers, PosixShell: !OperatingSystem.IsWindows(), geminiSettings);
    }

    /// <summary>gemini's system settings for this launch, in a private temporary folder (never in the repository):
    /// they hold the servers' definitions, whose secrets stay <c>${NAME}</c> references. Returns the folder.</summary>
    private static string? WriteGeminiSettings(string settings, ProcessStartInfo startInfo)
    {
        try
        {
            var directory = Directory.CreateTempSubdirectory("sidehub-gemini-").FullName;
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, settings);
            startInfo.Environment[McpServers.GeminiSystemSettingsVariable] = path;
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"sidehub-cli launch: gemini: MCP servers left out: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The launcher's own options, before the CLI name: the prompt to append, read from <c>$SIDEHUB_PTY_PROMPT</c>
    /// (<c>--prompt-env</c>: set when the PTY was created) or given in UTF-8 base64 (<c>--prompt-base64</c>: for a
    /// shell already running; its alphabet means the same in every shell).
    /// </summary>
    public static bool TryReadPrompt(ref string[] args, out string? prompt, out string? problem)
    {
        prompt = null;
        problem = null;
        if (args.Length > 0 && args[0] == "--prompt-env")
        {
            prompt = Environment.GetEnvironmentVariable(PromptVariable);
            args = args[1..];
            if (string.IsNullOrEmpty(prompt))
                problem = $"--prompt-env given but ${PromptVariable} is empty.";
        }
        else if (args.Length > 0 && args[0] == "--prompt-base64")
        {
            try
            {
                prompt = args.Length > 1 ? new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(args[1])) : null;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                prompt = null;
            }
            args = args.Length > 1 ? args[2..] : [];
            if (string.IsNullOrEmpty(prompt))
                problem = "--prompt-base64 needs a non-empty UTF-8 text in base64.";
        }
        return problem is null;
    }

    /// <summary>SideHub's wrappers (<c>cli-wrappers/</c> next to this program, or the agent's in the terminal):
    /// they call the launcher, so resolving to one would loop.</summary>
    private static List<string> WrapperDirectories()
    {
        var dirs = new List<string> { Path.Combine(AppContext.BaseDirectory, "cli-wrappers") };
        if (Environment.GetEnvironmentVariable(WrappersVariable) is { Length: > 0 } fromAgent)
            dirs.Add(fromAgent);
        return dirs;
    }
}

using SideHub.Agent;

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[SideHub] Received shutdown signal...");
    cts.Cancel();
};

var baseDirectory = Directory.GetCurrentDirectory();

try
{
    return await RunCommand(args, baseDirectory, cts.Token);
}
catch (DirectoryNotFoundException ex)
{
    Console.WriteLine($"[SideHub] Error: {ex.Message}");
    return 1;
}
catch (FileNotFoundException ex)
{
    Console.WriteLine($"[SideHub] Error: {ex.Message}");
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"[SideHub] Configuration error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine($"[SideHub] Unexpected error: {ex.Message}");
    return 1;
}

static async Task<int> RunCommand(string[] args, string baseDirectory, CancellationToken ct)
{
    var command = args.Length > 0 ? args[0].ToLowerInvariant() : "start";

    if (RootPolicy.IsCurrentUserRoot() && RootPolicy.IsGuarded(command))
    {
        var refusal = RootPolicy.Check(command, args, isRoot: true, Environment.GetEnvironmentVariable(RootPolicy.AllowEnvVar));
        if (refusal != null)
        {
            Console.WriteLine($"[SideHub] Error: {refusal}");
            await DiagnosticReporter.ReportRootRefusalAsync(command, args, baseDirectory);
            return 1;
        }
        if (command != "--foreground-daemon") // its stdout is discarded: it logs the warning itself
            Console.WriteLine($"[SideHub] {RootPolicy.Warning}");
    }

    return command switch
    {
        "start" => await HandleStart(args, baseDirectory, ct),
        "setup" => await HandleSetup(args, baseDirectory, ct),
        "stop" => HandleStop(args, baseDirectory),
        "restart" => await HandleRestart(args, baseDirectory, ct),
        "logs" => await HandleLogs(args, baseDirectory),
        "status" => HandleStatus(args, baseDirectory),
        "doctor" => await HandleDoctor(baseDirectory, ct),
        "service" => AgentService.Run(args, baseDirectory),
        "version" or "--version" => ShowVersion(),
        "update" when args.Length > 1 && args[1] == "apply" => await SideHub.Agent.Update.UpdateApplier.RunAsync(ct),
        "help" or "--help" or "-h" => ShowHelp(),
        "--foreground-daemon" => await HandleForegroundDaemon(args, baseDirectory, ct),
        _ => await HandleStart(args, baseDirectory, ct) // Default: treat unknown as start with possible flags
    };
}

static async Task<int> HandleForegroundDaemon(string[] args, string baseDirectory, CancellationToken ct)
{
    // Expected: --foreground-daemon <logFile> <pidFile>
    if (args.Length < 3)
    {
        Console.WriteLine("[SideHub] Error: Missing log file and pid file arguments");
        return 1;
    }

    var logFile = args[1];
    var pidFile = args[2];
    return await Commands.RunForegroundDaemon(baseDirectory, logFile, pidFile, ct);
}

static async Task<int> HandleStart(string[] args, string baseDirectory, CancellationToken ct)
{
    var daemon = args.Contains("-d") || args.Contains("--daemon");
    var all = args.Contains("--all");

    if (all)
        return await Commands.StartAll(daemon, ct);

    return await Commands.Start(baseDirectory, daemon, ct);
}

/// <summary>setup [--token &lt;token&gt; | --token-stdin] [--api &lt;url&gt;] [--no-start]: write .sidehub config here,
/// then start in the background (restart the agent already running here). Without a flag the token comes from
/// SIDEHUB_SETUP_TOKEN.</summary>
static async Task<int> HandleSetup(string[] args, string baseDirectory, CancellationToken ct)
{
    string? Value(string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    var fromStdin = args.Contains("--token-stdin") || Value("--token") == "-";
    if (fromStdin && !Console.IsInputRedirected)
        Console.Write("[SideHub] Paste the agent token, then press Enter: ");
    var token = AgentSetup.ResolveToken(
        Value("--token"),
        fromStdin,
        Console.In,
        Environment.GetEnvironmentVariable(AgentSetup.TokenEnvVar));
    DaemonEnvironmentPolicy.ClearSetupToken();

    // A failed setup tells SideHub why, with the error it printed: the install scripts only know that it failed
    var output = new OutputTail(Console.Out, maxLines: 2);
    Console.SetOut(output);
    var reporter = DiagnosticReporter.Create(
        AgentSetup.ApiBase(Value("--api") ?? Environment.GetEnvironmentVariable("SIDEHUB_API")), token, _ => { });
    try
    {
        var code = await SetupAndStart(args, baseDirectory, token, Value("--api"), ct);
        if (code != 0 && reporter is not null)
            await reporter.ReportAsync(DiagnosticReasons.InstallSetupFailed, output.Text);
        return code;
    }
    catch (Exception ex)
    {
        if (reporter is not null)
            await reporter.ReportAsync(DiagnosticReasons.InstallSetupFailed, ex.Message);
        throw;
    }
}

static async Task<int> SetupAndStart(string[] args, string baseDirectory, string token, string? api, CancellationToken ct)
{
    var code = await AgentSetup.Run(baseDirectory, token, api, ct);
    if (code != 0 || args.Contains("--no-start")) return code;
    // Run again in a folder whose agent is running (a second install, a new token): it must load the new config
    code = new DaemonManager(baseDirectory).IsRunning() || AgentService.IsSupervised(baseDirectory)
        ? Commands.Restart(baseDirectory, daemon: true, ct)
        : await Commands.Start(baseDirectory, daemon: true, ct);
    if (code != 0) return code;
    // The install command ends on what the agent will run, and with which sign-in: no model key goes to SideHub
    Console.WriteLine();
    return await HandleDoctor(baseDirectory, ct);
}

/// <summary>The CLIs the agent runs, then whether the agents come back after a reboot of the machine.</summary>
static async Task<int> HandleDoctor(string baseDirectory, CancellationToken ct)
{
    var code = await CliReport.PrintAsync(ct);
    AgentService.PrintRebootReport(baseDirectory);
    return code;
}

static int HandleStop(string[] args, string baseDirectory)
{
    if (args.Contains("--all"))
        return Commands.StopAll();

    return Commands.Stop(baseDirectory);
}

static async Task<int> HandleRestart(string[] args, string baseDirectory, CancellationToken ct)
{
    var daemon = args.Contains("-d") || args.Contains("--daemon");
    var all = args.Contains("--all");

    if (all)
        return await Commands.RestartAll(daemon, ct);

    return Commands.Restart(baseDirectory, daemon, ct);
}

static int HandleStatus(string[] args, string baseDirectory)
{
    if (args.Contains("--all"))
        return Commands.StatusAll();

    return Commands.Status(baseDirectory);
}

static async Task<int> HandleLogs(string[] args, string baseDirectory)
{
    var follow = !args.Contains("--no-follow"); // Follow by default
    return await Commands.Logs(baseDirectory, follow);
}

static int ShowVersion()
{
    Console.WriteLine(VersionInfo.AgentVersion);
    return 0;
}

static int ShowHelp()
{
    Commands.PrintHelp();
    return 0;
}

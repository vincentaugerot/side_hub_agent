using System.Diagnostics;
using SideHub.Agent.Update;

namespace SideHub.Agent;

public static class Commands
{
    public static async Task<int> Start(string baseDirectory, bool daemon, CancellationToken ct)
    {
        var manager = new DaemonManager(baseDirectory);

        if (manager.IsRunning())
        {
            var pid = manager.ReadPid();
            Console.WriteLine($"[SideHub] Agent is already running (PID: {pid})");
            Console.WriteLine($"[SideHub] Use 'sidehub-agent stop' to stop it first");
            return 1;
        }

        if (daemon && AgentService.IsSupervised(baseDirectory))
            return StartService(baseDirectory);

        if (daemon)
        {
            return StartDaemon(baseDirectory, manager);
        }

        return await RunForeground(baseDirectory, ct);
    }

    private static int StartService(string baseDirectory)
    {
        if (!AgentService.Start(baseDirectory))
        {
            Console.WriteLine("[SideHub] Error: the service didn't start (`sidehub-agent service status`)");
            return 1;
        }
        Console.WriteLine("[SideHub] Agent started by its service");
        Console.WriteLine("[SideHub] Use 'sidehub-agent logs' to view logs");
        return 0;
    }

    /// <summary>
    /// Stops this folder's agent; through its service when one runs it (systemd and launchd would restart a killed
    /// agent). Returns false when it was running and couldn't be stopped.
    /// </summary>
    private static bool StopInstance(string baseDirectory, DaemonManager manager)
    {
        if (AgentService.IsSupervised(baseDirectory) && AgentService.Stop(baseDirectory))
        {
            manager.RemovePidFile();
            return true;
        }
        return !manager.IsRunning() || manager.StopDaemon();
    }

    private static int StartDaemon(string baseDirectory, DaemonManager manager)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            Console.WriteLine("[SideHub] Error: Could not determine executable path");
            return 1;
        }

        manager.EnsureRunDirectory();

        // Pass the log file path to the daemon process
        var logFile = manager.LogFile;
        var pidFile = manager.PidFile;

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            // Reaching here as root means root was allowed explicitly (RootPolicy): the daemon re-checks, so forward it
            Arguments = $"--foreground-daemon \"{logFile}\" \"{pidFile}\"" + (RootPolicy.IsCurrentUserRoot() ? $" {RootPolicy.AllowFlag}" : ""),
            WorkingDirectory = baseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };

        // The agent token (setup) must not outlive setup: the daemon, its git calls, commands and
        // pty-helper would inherit it, and a terminal could read it in /proc/<pid>/environ.
        startInfo.Environment.Remove(AgentSetup.TokenEnvVar);

        // Set environment to prevent terminal attachment
        startInfo.Environment["DOTNET_RUNNING_IN_CONTAINER"] = "true";

        try
        {
            var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.WriteLine("[SideHub] Error: Failed to start daemon process");
                return 1;
            }

            // Give it a moment to start and write its PID
            Thread.Sleep(1000);

            // Check if process started successfully by reading PID file
            var pid = manager.ReadPid();
            if (pid == null)
            {
                Console.WriteLine("[SideHub] Error: Daemon process failed to start");
                return 1;
            }

            if (process.HasExited)
            {
                Console.WriteLine("[SideHub] Error: Daemon process exited immediately");
                Console.WriteLine("[SideHub] Check logs for details: " + logFile);
                manager.RemovePidFile();
                return 1;
            }

            Console.WriteLine($"[SideHub] Agent started in background (PID: {pid})");
            Console.WriteLine($"[SideHub] Logs: {logFile}");
            Console.WriteLine($"[SideHub] Use 'sidehub-agent logs' to view logs");
            Console.WriteLine($"[SideHub] Use 'sidehub-agent stop' to stop");

            InstanceRegistry.Register(baseDirectory);

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SideHub] Error starting daemon: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> RunForeground(string baseDirectory, CancellationToken ct)
    {
        // Started by hand with the setup token still exported: keep it away from the children.
        DaemonEnvironmentPolicy.ClearSetupToken();

        var configs = await AgentConfig.LoadAllAsync(baseDirectory, warning => Console.WriteLine($"[SideHub] Warning: {warning}"));
        foreach (var warning in AgentConfig.RestrictPermissions(baseDirectory, configs))
            Console.WriteLine($"[SideHub] Warning: {warning}");

        Console.WriteLine($"[SideHub] Found {configs.Count} agent(s) in .sidehub/");

        // One per process: the agents of this folder share the install, and so its updates
        using var updates = new UpdateCoordinator(baseDirectory, message => Console.WriteLine($"[SideHub] {message}"));
        updates.Start(ct);

        var tasks = configs.Select(config =>
        {
            var runner = new AgentRunner(config, baseDirectory, updates);
            return runner.RunAsync(ct);
        }).ToList();

        await Task.WhenAll(tasks);

        Console.WriteLine("[SideHub] All agents shut down");
        return 0;
    }

    public static async Task<int> RunForegroundDaemon(string baseDirectory, string logFile, string pidFile, CancellationToken ct)
    {
        // Started by hand too (see CLAUDE.md): tighten .sidehub/ and .sidehub/run/ left by older versions
        new DaemonManager(baseDirectory).EnsureRunDirectory();

        // Write our PID to the file
        PrivateFiles.WriteAllText(pidFile, Environment.ProcessId.ToString());
        // Started by its service too, not only by `start -d`: `--all` commands and updates must find it
        InstanceRegistry.Register(baseDirectory);

        // Redirect console output to log file with automatic rotation
        using var logWriter = new RotatingLogWriter(logFile);
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        var timestampWriter = new TimestampTextWriter(logWriter);
        Console.SetOut(timestampWriter);
        Console.SetError(timestampWriter);

        if (RootPolicy.IsCurrentUserRoot())
            Console.WriteLine($"[SideHub] {RootPolicy.Warning}");

        try
        {
            return await RunForeground(baseDirectory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Before the finally: once the console is restored (/dev/null for a daemon), Program's message is lost
            Console.WriteLine($"[SideHub] Startup failed: {ex.Message}");
            ReportStartupFailure(baseDirectory, ex.Message);
            throw;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);

            // Clean up PID file on exit
            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }
        }
    }

    /// <summary>
    /// The updater waits for <c>health.json</c> after a restart: with the reason there, it says why this daemon did not
    /// come up instead of waiting for it until its timeout.
    /// </summary>
    private static void ReportStartupFailure(string baseDirectory, string problem)
    {
        try
        {
            UpdateFiles.Write(UpdateFiles.HealthPath(baseDirectory),
                new InstanceHealth(Environment.ProcessId, VersionInfo.AgentVersion, false, problem, DateTime.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[SideHub] Health file not written: {ex.Message}");
        }
    }

    /// <summary>
    /// TextWriter that prefixes each line with a timestamp.
    /// </summary>
    private class TimestampTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private bool _atLineStart = true;

        public TimestampTextWriter(TextWriter inner) => _inner = inner;

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            if (_atLineStart && value != '\r' && value != '\n')
            {
                _inner.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ");
                _atLineStart = false;
            }

            _inner.Write(value);

            if (value == '\n')
            {
                _atLineStart = true;
            }
        }

        public override void WriteLine(string? value)
        {
            if (_atLineStart)
            {
                _inner.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ");
            }
            _inner.WriteLine(value);
            _atLineStart = true;
        }

        public override void Flush() => _inner.Flush();
    }

    public static async Task<int> Logs(string baseDirectory, bool follow)
    {
        var manager = new DaemonManager(baseDirectory);

        if (!File.Exists(manager.LogFile))
        {
            Console.WriteLine("[SideHub] No log file found");
            Console.WriteLine($"[SideHub] Expected at: {manager.LogFile}");
            return 1;
        }

        using var stream = manager.OpenLogForReading();
        if (stream == null)
        {
            Console.WriteLine("[SideHub] Could not open log file");
            return 1;
        }

        using var reader = new StreamReader(stream);

        // Print existing content
        var existingContent = await reader.ReadToEndAsync();
        if (!string.IsNullOrEmpty(existingContent))
        {
            Console.Write(existingContent);
        }

        if (!follow)
            return 0;

        // Follow mode: keep reading new lines
        Console.WriteLine("[SideHub] Following logs (Ctrl+C to stop)...");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line != null)
                {
                    Console.WriteLine(line);
                }
                else
                {
                    await Task.Delay(100, cts.Token);
                }
            }
        }
        catch (OperationCanceledException) { }

        return 0;
    }

    public static int Stop(string baseDirectory)
    {
        var manager = new DaemonManager(baseDirectory);

        if (AgentService.IsSupervised(baseDirectory) && manager.IsRunning())
        {
            Console.WriteLine($"[SideHub] Stopping agent through its service (PID: {manager.ReadPid()})...");
            if (StopInstance(baseDirectory, manager))
            {
                Console.WriteLine("[SideHub] Agent stopped (it starts again at the next boot; `sidehub-agent service uninstall` to stop that)");
                return 0;
            }
            Console.WriteLine("[SideHub] Failed to stop agent");
            return 1;
        }

        if (!manager.IsRunning())
        {
            Console.WriteLine("[SideHub] No agent is running");
            manager.RemovePidFile(); // Clean up stale PID file
            return 0;
        }

        var pid = manager.ReadPid();
        Console.WriteLine($"[SideHub] Stopping agent (PID: {pid})...");

        if (manager.StopDaemon())
        {
            Console.WriteLine("[SideHub] Agent stopped");
            return 0;
        }
        else
        {
            Console.WriteLine("[SideHub] Failed to stop agent");
            return 1;
        }
    }

    public static int Status(string baseDirectory)
    {
        var manager = new DaemonManager(baseDirectory);
        var pid = manager.ReadPid();

        if (manager.IsRunning())
        {
            Console.WriteLine($"[SideHub] Agent is running (PID: {pid})");
            Console.WriteLine($"[SideHub] Logs: {manager.LogFile}");

            // Show log file size (current + archives)
            if (File.Exists(manager.LogFile))
            {
                var fileInfo = new FileInfo(manager.LogFile);
                var totalSize = RotatingLogWriter.GetTotalLogSize(manager.LogFile);
                var archiveFiles = RotatingLogWriter.GetAllLogFiles(manager.LogFile);

                Console.WriteLine($"[SideHub] Log size: {FormatBytes(fileInfo.Length)}");
                if (archiveFiles.Count > 1)
                {
                    Console.WriteLine($"[SideHub] Total log size ({archiveFiles.Count} files): {FormatBytes(totalSize)}");
                }
            }

            return 0;
        }
        else
        {
            Console.WriteLine("[SideHub] Agent is not running");
            if (pid != null)
            {
                Console.WriteLine($"[SideHub] Stale PID file found ({pid}), cleaning up...");
                manager.RemovePidFile();
            }
            return 1;
        }
    }

    public static int Restart(string baseDirectory, bool daemon, CancellationToken ct)
    {
        var manager = new DaemonManager(baseDirectory);

        if (daemon && AgentService.IsSupervised(baseDirectory))
        {
            Console.WriteLine($"[SideHub] Restarting agent in {baseDirectory} through its service...");
            if (AgentService.Restart(baseDirectory))
            {
                Console.WriteLine("[SideHub] Agent restarted");
                return 0;
            }
            Console.WriteLine("[SideHub] Error: the service didn't restart (`sidehub-agent service status`)");
            return 1;
        }

        if (manager.IsRunning())
        {
            var pid = manager.ReadPid();
            Console.WriteLine($"[SideHub] Stopping agent (PID: {pid})...");
            manager.StopDaemon();
            Thread.Sleep(1000);
        }

        Console.WriteLine($"[SideHub] Starting agent in {baseDirectory}...");
        return daemon ? StartDaemon(baseDirectory, manager) : RunForeground(baseDirectory, ct).GetAwaiter().GetResult();
    }

    public static async Task<int> StartAll(bool daemon, CancellationToken ct)
    {
        if (!daemon)
        {
            Console.WriteLine("[SideHub] Error: --all requires -d (daemon mode)");
            return 1;
        }

        var instances = InstanceRegistry.LoadValid();
        if (instances.Count == 0)
        {
            Console.WriteLine("[SideHub] No registered instances found");
            Console.WriteLine("[SideHub] Start agents individually first: cd /path/to/repo && sidehub-agent start -d");
            return 0;
        }

        int started = 0, skipped = 0;

        foreach (var instance in instances)
        {
            var manager = new DaemonManager(instance.Directory);
            if (manager.IsRunning())
            {
                var pid = manager.ReadPid();
                Console.WriteLine($"[SideHub] {instance.Directory}: already running (PID: {pid}), skipping");
                skipped++;
                continue;
            }

            Console.WriteLine($"[SideHub] Starting {instance.Directory}...");
            var result = await Start(instance.Directory, daemon: true, ct);
            if (result == 0) started++;
        }

        Console.WriteLine($"[SideHub] Done: {started} started, {skipped} already running");
        return 0;
    }

    public static int StopAll()
    {
        var instances = InstanceRegistry.LoadValid();
        if (instances.Count == 0)
        {
            Console.WriteLine("[SideHub] No registered instances found");
            return 0;
        }

        int stopped = 0, failures = 0;

        foreach (var instance in instances)
        {
            var manager = new DaemonManager(instance.Directory);
            if (!manager.IsRunning())
            {
                Console.WriteLine($"[SideHub] {instance.Directory}: not running");
                manager.RemovePidFile();
                continue;
            }

            var pid = manager.ReadPid();
            Console.WriteLine($"[SideHub] Stopping {instance.Directory} (PID: {pid})...");
            if (StopInstance(instance.Directory, manager))
            {
                stopped++;
            }
            else
            {
                Console.WriteLine($"[SideHub] Failed to stop {instance.Directory}");
                failures++;
            }
        }

        Console.WriteLine($"[SideHub] Done: {stopped} stopped");
        return failures > 0 ? 1 : 0;
    }

    public static async Task<int> RestartAll(bool daemon, CancellationToken ct)
    {
        if (!daemon)
        {
            Console.WriteLine("[SideHub] Error: --all requires -d (daemon mode)");
            return 1;
        }

        var instances = InstanceRegistry.LoadValid();
        if (instances.Count == 0)
        {
            Console.WriteLine("[SideHub] No registered instances found");
            return 0;
        }

        // Phase 1: Stop all running agents
        Console.WriteLine("[SideHub] Phase 1: Stopping all agents...");
        foreach (var instance in instances)
        {
            var manager = new DaemonManager(instance.Directory);
            if (manager.IsRunning())
            {
                var pid = manager.ReadPid();
                Console.WriteLine($"[SideHub]   Stopping {instance.Directory} (PID: {pid})...");
                StopInstance(instance.Directory, manager);
            }
        }

        Thread.Sleep(1000);

        // Phase 2: Start all agents
        Console.WriteLine("[SideHub] Phase 2: Starting all agents...");
        int started = 0;

        foreach (var instance in instances)
        {
            Console.WriteLine($"[SideHub]   Starting {instance.Directory}...");
            var result = await Start(instance.Directory, daemon: true, ct);
            if (result == 0) started++;
        }

        Console.WriteLine($"[SideHub] Done: {started}/{instances.Count} restarted");
        return 0;
    }

    public static int StatusAll()
    {
        var instances = InstanceRegistry.LoadValid();
        if (instances.Count == 0)
        {
            Console.WriteLine("[SideHub] No registered instances found");
            return 0;
        }

        Console.WriteLine($"[SideHub] Registered instances ({instances.Count}):");
        Console.WriteLine();

        foreach (var instance in instances)
        {
            var manager = new DaemonManager(instance.Directory);
            var pid = manager.ReadPid();
            var running = manager.IsRunning();
            var status = running ? $"running (PID: {pid})" : "stopped";
            Console.WriteLine($"  {instance.Directory}");
            Console.WriteLine($"    Status: {status}");
        }

        Console.WriteLine();
        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: sidehub-agent [command] [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  setup           Configure this folder for an agent, then start it");
        Console.WriteLine("    --token-stdin Read the agent's token (copied from SideHub) from stdin");
        Console.WriteLine("    --token <t>   Pass the token as an argument (visible in ps and shell history)");
        Console.WriteLine("                  Without either, the token is read from SIDEHUB_SETUP_TOKEN");
        Console.WriteLine("    --no-start    Only write .sidehub/agent.json (otherwise start, or restart the running agent)");
        Console.WriteLine("  start           Start the agent (default)");
        Console.WriteLine("    -d, --daemon  Run in background");
        Console.WriteLine("    --all         Operate on all registered instances");
        Console.WriteLine("  stop            Stop the running agent");
        Console.WriteLine("    --all         Stop all registered instances");
        Console.WriteLine("  restart         Stop then start the agent");
        Console.WriteLine("    -d, --daemon  Run in background");
        Console.WriteLine("    --all         Restart all registered instances");
        Console.WriteLine("  service install Start this folder's agent at boot (Linux), at login (macOS) or at logon (Windows)");
        Console.WriteLine("    --all         One boot entry for every agent of this machine (start --all -d)");
        Console.WriteLine("  service uninstall / service status [--all]");
        Console.WriteLine("  logs            Show agent logs");
        Console.WriteLine("    -f, --follow  Follow log output (default)");
        Console.WriteLine("    --no-follow   Don't follow, just print current logs");
        Console.WriteLine("  status          Show agent status");
        Console.WriteLine("    --all         Show all registered instances");
        Console.WriteLine("  doctor          Show the coding CLIs the agent runs, whether they are logged in and how they are paid for,");
        Console.WriteLine("                  and the agents that won't start again after a reboot");
        Console.WriteLine("  version         Print the agent's version");
        Console.WriteLine("  help            Show this help");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --allow-root    Allow setup/start/restart as root (refused by default; or SIDEHUB_ALLOW_ROOT=1)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  sidehub-agent setup --token-stdin  # Paste the token, then Enter: configure and start");
        Console.WriteLine("  sidehub-agent              # Start in foreground");
        Console.WriteLine("  sidehub-agent start -d     # Start in background");
        Console.WriteLine("  sidehub-agent service install  # Start it again after a reboot");
        Console.WriteLine("  sidehub-agent service install --all  # Same for every agent of this machine");
        Console.WriteLine("  sidehub-agent restart --all -d  # Restart all agents");
        Console.WriteLine("  sidehub-agent status --all # Show all instances");
        Console.WriteLine("  sidehub-agent logs         # View and follow logs");
        Console.WriteLine("  sidehub-agent doctor       # Check claude / codex / agy are installed and logged in");
        Console.WriteLine("  sidehub-agent stop --all   # Stop all agents");
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

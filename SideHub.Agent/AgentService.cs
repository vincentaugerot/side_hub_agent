using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// <c>sidehub-agent service install|uninstall|status</c>: starts this folder's agent again after a reboot, for the
/// current user, without a dedicated account. Linux: a systemd user unit, with lingering so that it runs without an
/// open session. macOS: a LaunchAgent (runs once the user is logged in). Windows: a scheduled task at logon.
/// systemd and launchd also restart the agent after a crash, so once installed they own the daemon: start, stop and
/// restart go through them (<see cref="IsSupervised"/>), otherwise a stopped agent would come back. With <c>--all</c>,
/// one boot entry starts every registered agent of the machine instead (<see cref="InstallBoot"/>).
/// </summary>
public static partial class AgentService
{
    public static int Run(string[] args, string baseDirectory)
    {
        var sub = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        var all = args.Contains("--all");
        switch (sub)
        {
            case "install": return all ? InstallBoot() : Install(baseDirectory);
            case "uninstall": return all ? UninstallBoot() : Uninstall(baseDirectory);
            case "status": return all ? StatusBoot() : Status(baseDirectory);
            default:
                Console.WriteLine("Usage: sidehub-agent service install|uninstall|status [--all]");
                Console.WriteLine("  install    Start this folder's agent at boot (Linux), at login (macOS) or at logon (Windows)");
                Console.WriteLine("  uninstall  Remove it");
                Console.WriteLine("  status     Show whether it is installed and running");
                Console.WriteLine("  --all      One entry for every agent of this machine (`start --all -d` at boot), not one per folder");
                return sub is null ? 0 : 1;
        }
    }

    /// <summary>
    /// Unique per project folder, readable in <c>systemctl --user</c> / <c>launchctl list</c>: the folder name and a
    /// hash of the full path (two projects can share a folder name).
    /// </summary>
    public static string ServiceName(string baseDirectory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var slug = NonSlugPattern().Replace(Path.GetFileName(path).ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 32) slug = slug[..32].TrimEnd('-');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];
        return slug.Length == 0 ? $"sidehub-agent-{hash}" : $"sidehub-agent-{slug}-{hash}";
    }

    /// <summary>systemd (Linux) or launchd (macOS) runs this folder's agent: they restart it if it is killed.</summary>
    public static bool IsSupervised(string baseDirectory) =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && File.Exists(DefinitionPath(baseDirectory));

    /// <summary>
    /// Whether this folder's agent starts again once the machine restarts: its own service or scheduled task, or the
    /// boot entry of <c>service install --all</c> while the folder is registered (<see cref="InstanceRegistry"/>). On
    /// Linux a systemd user unit only runs at boot with lingering. Reported in <c>agent.connected.supervised</c>.
    /// </summary>
    public static bool StartsAfterReboot(string baseDirectory)
    {
        if (OperatingSystem.IsWindows())
            return Exec("schtasks", "/Query", "/TN", TaskName(baseDirectory)).Ok
                || (IsRegistered(baseDirectory) && Exec("schtasks", "/Query", "/TN", BootTaskName).Ok);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
        var installed = File.Exists(DefinitionPath(baseDirectory)) || (File.Exists(BootDefinitionPath()) && IsRegistered(baseDirectory));
        return installed && (OperatingSystem.IsMacOS() || LingerEnabled());
    }

    public static bool Start(string baseDirectory) => OperatingSystem.IsMacOS()
        ? Exec("launchctl", "kickstart", LaunchdTarget(baseDirectory)).Ok
        : Exec("systemctl", "--user", "start", UnitName(baseDirectory)).Ok;

    /// <summary>A graceful stop (SIGINT): systemd and launchd leave a clean exit alone.</summary>
    public static bool Stop(string baseDirectory) => OperatingSystem.IsMacOS()
        ? Exec("launchctl", "kill", "SIGINT", LaunchdTarget(baseDirectory)).Ok
        : Exec("systemctl", "--user", "stop", UnitName(baseDirectory)).Ok;

    /// <summary>
    /// Runs a command as a transient systemd user unit, outside the cgroup of the service that launches it: stopping
    /// that service does not kill it (the updater). <c>KillMode=process</c>: the daemons it starts outlive it.
    /// </summary>
    public static bool RunTransient(string unitName, IReadOnlyList<string> command, IEnumerable<KeyValuePair<string, string>> environment)
    {
        var arguments = new List<string> { "--user", "--collect", "--quiet", "--property=KillMode=process", $"--unit={unitName}" };
        arguments.AddRange(environment.Select(variable => $"--setenv={variable.Key}={variable.Value}"));
        arguments.AddRange(command);
        return Exec("systemd-run", [.. arguments]).Ok;
    }

    public static bool Restart(string baseDirectory) => OperatingSystem.IsMacOS()
        ? Exec("launchctl", "kickstart", "-k", LaunchdTarget(baseDirectory)).Ok
        : Exec("systemctl", "--user", "restart", UnitName(baseDirectory)).Ok;

    private static int Install(string baseDirectory)
    {
        var configDir = Path.Combine(baseDirectory, AgentConfig.ConfigFolder);
        if (!Directory.Exists(configDir) || Directory.GetFiles(configDir, "*.json").Length == 0)
        {
            Console.WriteLine($"[SideHub] Error: no agent configured in {baseDirectory}.");
            Console.WriteLine("[SideHub] Run `sidehub-agent setup --token-stdin` in this folder first.");
            return 1;
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            Console.WriteLine("[SideHub] Error: could not determine the agent's executable path");
            return 1;
        }

        if (OperatingSystem.IsLinux()) return InstallSystemd(baseDirectory, executable);
        if (OperatingSystem.IsMacOS()) return InstallLaunchd(baseDirectory, executable);
        if (OperatingSystem.IsWindows()) return InstallScheduledTask(baseDirectory, executable);
        Console.WriteLine("[SideHub] Error: services are supported on Linux (systemd), macOS and Windows only");
        return 1;
    }

    private static int Uninstall(string baseDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var deleted = Exec("schtasks", "/Delete", "/TN", TaskName(baseDirectory), "/F");
            Console.WriteLine(deleted.Ok ? "[SideHub] Scheduled task removed" : "[SideHub] No scheduled task for this folder");
            return 0;
        }

        var definition = DefinitionPath(baseDirectory);
        if (!File.Exists(definition))
        {
            Console.WriteLine("[SideHub] No service installed for this folder");
            return 0;
        }
        if (OperatingSystem.IsMacOS())
            Exec("launchctl", "bootout", LaunchdTarget(baseDirectory));
        else
            Exec("systemctl", "--user", "disable", "--now", UnitName(baseDirectory));
        File.Delete(definition);
        if (OperatingSystem.IsLinux())
            Exec("systemctl", "--user", "daemon-reload");
        Console.WriteLine($"[SideHub] Service removed ({definition}); the agent is stopped.");
        Console.WriteLine("[SideHub] Start it by hand with `sidehub-agent start -d`.");
        return 0;
    }

    private static int Status(string baseDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var query = Exec("schtasks", "/Query", "/TN", TaskName(baseDirectory));
            Console.WriteLine(query.Ok
                ? $"[SideHub] Scheduled task \"{TaskName(baseDirectory)}\" installed: the agent starts at logon"
                : "[SideHub] No scheduled task for this folder (`sidehub-agent service install`)");
            return query.Ok ? 0 : 1;
        }

        var definition = DefinitionPath(baseDirectory);
        if (!File.Exists(definition))
        {
            Console.WriteLine("[SideHub] No service installed for this folder (`sidehub-agent service install`)");
            return 1;
        }
        Console.WriteLine($"[SideHub] Service installed: {definition}");
        if (OperatingSystem.IsLinux())
        {
            var active = Exec("systemctl", "--user", "is-active", UnitName(baseDirectory));
            Console.WriteLine($"[SideHub] State: {(active.Output.Length > 0 ? active.Output : "unknown")}");
            if (!LingerEnabled())
                Console.WriteLine($"[SideHub] Lingering is off: the agent only runs while you are logged in (sudo loginctl enable-linger {Environment.UserName})");
        }
        else
        {
            var listed = Exec("launchctl", "print", LaunchdTarget(baseDirectory));
            Console.WriteLine($"[SideHub] State: {(listed.Ok ? "loaded" : "not loaded")}");
        }
        Console.WriteLine($"[SideHub] Agent: {(new DaemonManager(baseDirectory).IsRunning() ? "running" : "not running")}");
        return 0;
    }

    // --- Linux: systemd user unit -------------------------------------------------------------------------------

    private static string UnitName(string baseDirectory) => ServiceName(baseDirectory) + ".service";

    private static string SystemdUserDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "systemd", "user");
    }

    private static int InstallSystemd(string baseDirectory, string executable)
    {
        var unit = UnitName(baseDirectory);
        if (!Exec("systemctl", "--user", "show-environment").Ok)
        {
            Console.WriteLine("[SideHub] Error: no systemd user session for this user (`systemctl --user` fails).");
            Console.WriteLine($"[SideHub] Enable lingering (sudo loginctl enable-linger {Environment.UserName}) or log in as this user directly (ssh), then run this again.");
            return 1;
        }

        var path = DefinitionPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SystemdUnit(baseDirectory, executable, Environment.GetEnvironmentVariable("PATH"), RootPolicy.IsCurrentUserRoot()));
        Exec("systemctl", "--user", "daemon-reload");

        StopUnsupervisedDaemon(baseDirectory);
        if (!Exec("systemctl", "--user", "enable", unit).Ok || !Exec("systemctl", "--user", "restart", unit).Ok)
        {
            Console.WriteLine($"[SideHub] Error: systemd couldn't start {unit}: see `systemctl --user status {unit}`");
            return 1;
        }
        Console.WriteLine($"[SideHub] Service installed and started: {unit}");
        Console.WriteLine($"[SideHub] Unit file: {path}");

        if (EnsureLinger())
            Console.WriteLine("[SideHub] It starts at boot, and again after a crash.");
        return 0;
    }

    /// <summary>Without lingering a systemd user unit only runs while the user is logged in, never at boot.</summary>
    private static bool EnsureLinger()
    {
        if (LingerEnabled() || Exec("loginctl", "enable-linger", Environment.UserName).Ok) return true;
        Console.WriteLine("[SideHub] Warning: lingering is off, so the agent stops when you log out and only starts at your next login.");
        Console.WriteLine($"[SideHub] To start it at boot: sudo loginctl enable-linger {Environment.UserName}");
        return false;
    }

    private static bool LingerEnabled()
    {
        var linger = Exec("loginctl", "show-user", Environment.UserName, "--property=Linger", "--value");
        return linger.Ok && linger.Output == "yes";
    }

    /// <summary>
    /// The unit running <c>--foreground-daemon</c> (the same log and PID files as <c>start -d</c>, so <c>status</c>
    /// and <c>logs</c> work as usual) with the PATH of the shell that installed it: the CLIs (nvm, ~/.local/bin) must
    /// resolve as they do for the user.
    /// </summary>
    public static string SystemdUnit(string baseDirectory, string executable, string? pathVariable, bool allowRoot)
    {
        var run = Path.Combine(baseDirectory, AgentConfig.ConfigFolder, "run");
        var exec = string.Join(' ', new[] { executable, "--foreground-daemon", Path.Combine(run, "sidehub-agent.log"), Path.Combine(run, "sidehub-agent.pid") }
            .Select(SystemdQuote)) + (allowRoot ? $" {RootPolicy.AllowFlag}" : "");
        var environment = string.IsNullOrEmpty(pathVariable) ? "" : $"Environment={SystemdQuote("PATH=" + pathVariable)}\n";
        return $"""
            # Written by `sidehub-agent service install`; remove with `sidehub-agent service uninstall`.
            [Unit]
            Description=SideHub agent ({SystemdEscape(baseDirectory)})

            [Service]
            Type=simple
            WorkingDirectory={SystemdEscape(baseDirectory)}
            ExecStart={exec}
            {environment}# SIGINT is the agent's graceful shutdown (closes terminals, flushes usage reports)
            KillSignal=SIGINT
            TimeoutStopSec=20
            Restart=on-failure
            RestartSec=5
            UMask=0077

            [Install]
            WantedBy=default.target

            """;
    }

    /// <summary>% starts a specifier in every unit setting.</summary>
    private static string SystemdEscape(string value) => value.Replace("%", "%%");

    private static string SystemdQuote(string value) =>
        "\"" + SystemdEscape(value).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // --- macOS: LaunchAgent -------------------------------------------------------------------------------------

    private static string LaunchdLabel(string baseDirectory) => "io.sidehub.agent." + ServiceName(baseDirectory)["sidehub-agent-".Length..];

    private static string LaunchdTarget(string baseDirectory) => $"gui/{FileOwnership.CurrentUser}/{LaunchdLabel(baseDirectory)}";

    private static int InstallLaunchd(string baseDirectory, string executable)
    {
        var path = DefinitionPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A previous version of the definition may be loaded: unload it first, bootstrap refuses a loaded label
        Exec("launchctl", "bootout", LaunchdTarget(baseDirectory));
        File.WriteAllText(path, LaunchdPlist(LaunchdLabel(baseDirectory), baseDirectory, executable,
            Environment.GetEnvironmentVariable("PATH"), RootPolicy.IsCurrentUserRoot()));

        StopUnsupervisedDaemon(baseDirectory);
        var bootstrap = Exec("launchctl", "bootstrap", $"gui/{FileOwnership.CurrentUser}", path);
        if (!bootstrap.Ok)
        {
            Console.WriteLine($"[SideHub] Error: launchctl couldn't load {path}: {bootstrap.Output}");
            return 1;
        }
        Console.WriteLine($"[SideHub] Service installed and started: {LaunchdLabel(baseDirectory)}");
        Console.WriteLine($"[SideHub] Definition: {path}");
        Console.WriteLine("[SideHub] It starts when you log in, and again after a crash.");
        return 0;
    }

    public static string LaunchdPlist(string label, string baseDirectory, string executable, string? pathVariable, bool allowRoot)
    {
        var run = Path.Combine(baseDirectory, AgentConfig.ConfigFolder, "run");
        var arguments = new List<string> { executable, "--foreground-daemon", Path.Combine(run, "sidehub-agent.log"), Path.Combine(run, "sidehub-agent.pid") };
        if (allowRoot) arguments.Add(RootPolicy.AllowFlag);
        var environment = string.IsNullOrEmpty(pathVariable) ? "" : $"""

                <key>EnvironmentVariables</key>
                <dict>
                    <key>PATH</key>
                    <string>{Xml(pathVariable)}</string>
                </dict>
            """;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <!-- Written by `sidehub-agent service install`; remove with `sidehub-agent service uninstall`. -->
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Xml(label)}</string>
                <key>ProgramArguments</key>
                <array>
            {string.Join('\n', arguments.Select(a => $"        <string>{Xml(a)}</string>"))}
                </array>
                <key>WorkingDirectory</key>
                <string>{Xml(baseDirectory)}</string>{environment}
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
                <key>ThrottleInterval</key>
                <integer>10</integer>
                <key>Umask</key>
                <integer>63</integer>
                <key>ProcessType</key>
                <string>Background</string>
            </dict>
            </plist>

            """;
    }

    // --- Windows: scheduled task at logon -----------------------------------------------------------------------

    private static string TaskName(string baseDirectory) => "SideHub Agent " + ServiceName(baseDirectory)["sidehub-agent-".Length..];

    private static int InstallScheduledTask(string baseDirectory, string executable)
    {
        if (!CreateScheduledTask(TaskName(baseDirectory), ScheduledTaskXml(TaskUser, baseDirectory, executable)))
            return 1;
        Console.WriteLine($"[SideHub] Scheduled task created: \"{TaskName(baseDirectory)}\"");
        Console.WriteLine("[SideHub] The agent starts in the background each time you log on to Windows.");
        if (!new DaemonManager(baseDirectory).IsRunning())
            Console.WriteLine("[SideHub] It is not running now: start it with `sidehub-agent start -d`.");
        return 0;
    }

    private static string TaskUser => $"{Environment.UserDomainName}\\{Environment.UserName}";

    private static bool CreateScheduledTask(string name, string xml)
    {
        var xmlFile = Path.Combine(Path.GetTempPath(), $"sidehub-agent-task-{Guid.NewGuid():N}.xml");
        try
        {
            // Task Scheduler reads its XML as UTF-16
            File.WriteAllText(xmlFile, xml, Encoding.Unicode);
            var created = Exec("schtasks", "/Create", "/TN", name, "/XML", xmlFile, "/F");
            if (!created.Ok)
                Console.WriteLine($"[SideHub] Error: couldn't create the scheduled task: {created.Output}");
            return created.Ok;
        }
        finally
        {
            File.Delete(xmlFile);
        }
    }

    /// <summary>
    /// Runs <c>start -d</c> from the project folder at logon (or <c>start --all -d</c>, see <see cref="InstallBoot"/>):
    /// the task ends once the daemons are started.
    /// </summary>
    public static string ScheduledTaskXml(string user, string baseDirectory, string executable, string arguments = "start -d",
        string? description = null) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>{Xml(description ?? $"SideHub agent for {baseDirectory} (sidehub-agent service install)")}</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Xml(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Xml(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Xml(executable)}</Command>
              <Arguments>{Xml(arguments)}</Arguments>
              <WorkingDirectory>{Xml(baseDirectory)}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    // --- Shared -------------------------------------------------------------------------------------------------

    private static string DefinitionPath(string baseDirectory) => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LaunchdLabel(baseDirectory) + ".plist")
        : Path.Combine(SystemdUserDirectory(), UnitName(baseDirectory));

    /// <summary>The service takes over: a daemon started by <c>setup</c> or <c>start -d</c> would run twice.</summary>
    private static void StopUnsupervisedDaemon(string baseDirectory)
    {
        var manager = new DaemonManager(baseDirectory);
        if (!manager.IsRunning()) return;
        Console.WriteLine($"[SideHub] Stopping the agent started by hand (PID: {manager.ReadPid()}): the service runs it from now on");
        manager.StopDaemon();
    }

    private static string Xml(string value) => SecurityElement.Escape(value);

    private readonly record struct ExecResult(bool Ok, string Output);

    private static ExecResult Exec(string file, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        // Never hand the setup token to systemctl / launchctl / schtasks
        startInfo.Environment.Remove(AgentSetup.TokenEnvVar);
        // `su - user` leaves XDG_RUNTIME_DIR unset: systemctl --user then can't find a user manager that runs anyway
        // (lingering, or another session of the user)
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))
            && FileOwnership.CurrentUser is { } uid && Directory.Exists($"/run/user/{uid}"))
            startInfo.Environment["XDG_RUNTIME_DIR"] = $"/run/user/{uid}";
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new ExecResult(false, "");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return new ExecResult(false, $"{file} timed out");
            }
            var output = (stdout.Result + stderr.Result).Trim();
            return new ExecResult(process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return new ExecResult(false, ex.Message);
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugPattern();
}

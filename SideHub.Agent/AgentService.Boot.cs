namespace SideHub.Agent;

/// <summary>
/// <c>sidehub-agent service install|uninstall|status --all</c>: one boot entry for the whole machine that runs
/// <c>start --all -d</c>, so every registered agent (<see cref="InstanceRegistry"/>) starts again after a reboot, even
/// one set up after it, without a service per folder. It only starts them: unlike a folder's own service it does not
/// restart a crashed agent, and it never owns the daemons (<see cref="IsSupervised"/> stays false for them). Installed
/// without running it: it must not bring back an agent stopped on purpose.
/// </summary>
public static partial class AgentService
{
    public const string BootCommand = "sidehub-agent service install --all";

    private const string BootServiceName = "sidehub-agent-all";
    private const string BootUnitName = BootServiceName + ".service";
    private const string BootLaunchdLabel = "io.sidehub.agent.all";
    private const string BootTaskName = "SideHub Agent (all)";

    private static int InstallBoot()
    {
        var registered = InstanceRegistry.LoadValid();
        if (registered.Count == 0)
        {
            Console.WriteLine("[SideHub] Error: no agent registered on this machine.");
            Console.WriteLine("[SideHub] Run `sidehub-agent setup --token-stdin` in a repository folder first.");
            return 1;
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            Console.WriteLine("[SideHub] Error: could not determine the agent's executable path");
            return 1;
        }

        int code;
        if (OperatingSystem.IsLinux()) code = InstallBootSystemd(executable);
        else if (OperatingSystem.IsMacOS()) code = InstallBootLaunchd(executable);
        else if (OperatingSystem.IsWindows())
            code = CreateScheduledTask(BootTaskName, ScheduledTaskXml(TaskUser, Home, executable, "start --all -d",
                "SideHub agents of this machine (sidehub-agent service install --all)")) ? 0 : 1;
        else
        {
            Console.WriteLine("[SideHub] Error: services are supported on Linux (systemd), macOS and Windows only");
            return 1;
        }
        if (code != 0) return code;

        Console.WriteLine($"[SideHub] It starts the {registered.Count} agent(s) registered on this machine, and the ones set up later:");
        foreach (var instance in registered)
            Console.WriteLine($"            {instance.Directory}");
        Console.WriteLine("[SideHub] The agents running now keep running: nothing was restarted.");
        return 0;
    }

    private static int InstallBootSystemd(string executable)
    {
        if (!Exec("systemctl", "--user", "show-environment").Ok)
        {
            Console.WriteLine("[SideHub] Error: no systemd user session for this user (`systemctl --user` fails).");
            Console.WriteLine($"[SideHub] Enable lingering (sudo loginctl enable-linger {Environment.UserName}) or log in as this user directly (ssh), then run this again.");
            return 1;
        }
        var path = BootDefinitionPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SystemdBootUnit(executable, Environment.GetEnvironmentVariable("PATH"), RootPolicy.IsCurrentUserRoot()));
        Exec("systemctl", "--user", "daemon-reload");
        if (!Exec("systemctl", "--user", "enable", BootUnitName).Ok)
        {
            Console.WriteLine($"[SideHub] Error: systemd couldn't enable {BootUnitName}: see `systemctl --user status {BootUnitName}`");
            return 1;
        }
        Console.WriteLine($"[SideHub] Boot entry installed: {BootUnitName} ({path})");
        if (EnsureLinger())
            Console.WriteLine("[SideHub] It runs at each boot.");
        return 0;
    }

    private static int InstallBootLaunchd(string executable)
    {
        var path = BootDefinitionPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A previous version may be loaded; it is not bootstrapped again, since that would run it now
        Exec("launchctl", "bootout", $"gui/{FileOwnership.CurrentUser}/{BootLaunchdLabel}");
        File.WriteAllText(path, LaunchdBootPlist(executable, Home, Environment.GetEnvironmentVariable("PATH"), RootPolicy.IsCurrentUserRoot()));
        Console.WriteLine($"[SideHub] Boot entry installed: {path}");
        Console.WriteLine("[SideHub] It runs each time you log in.");
        return 0;
    }

    private static int UninstallBoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var deleted = Exec("schtasks", "/Delete", "/TN", BootTaskName, "/F");
            Console.WriteLine(deleted.Ok ? "[SideHub] Scheduled task removed" : "[SideHub] No scheduled task for the agents of this machine");
            return 0;
        }
        var path = BootDefinitionPath();
        if (!File.Exists(path))
        {
            Console.WriteLine("[SideHub] No boot entry installed for the agents of this machine");
            return 0;
        }
        if (OperatingSystem.IsMacOS())
            Exec("launchctl", "bootout", $"gui/{FileOwnership.CurrentUser}/{BootLaunchdLabel}");
        else
            Exec("systemctl", "--user", "disable", BootUnitName);
        File.Delete(path);
        if (OperatingSystem.IsLinux())
            Exec("systemctl", "--user", "daemon-reload");
        Console.WriteLine($"[SideHub] Boot entry removed ({path}); the agents running now keep running.");
        return 0;
    }

    private static int StatusBoot()
    {
        var installed = OperatingSystem.IsWindows() ? Exec("schtasks", "/Query", "/TN", BootTaskName).Ok : File.Exists(BootDefinitionPath());
        Console.WriteLine(installed
            ? "[SideHub] Boot entry installed: the registered agents start with the machine"
            : $"[SideHub] No boot entry for the agents of this machine (`{BootCommand}`)");
        PrintRebootReport(null);
        return installed ? 0 : 1;
    }

    /// <summary>
    /// Which agents of the machine won't start again after a reboot, with the command that fixes it (<c>doctor</c>, end of
    /// <c>setup</c>, <c>service status --all</c>). <paramref name="baseDirectory"/>: the folder the command runs in, counted
    /// when it has an agent even before it is registered.
    /// </summary>
    public static void PrintRebootReport(string? baseDirectory)
    {
        var directories = InstanceRegistry.LoadValid().Select(i => i.Directory).ToList();
        if (baseDirectory is not null && Directory.Exists(Path.Combine(baseDirectory, AgentConfig.ConfigFolder))
            && !directories.Any(d => SamePath(d, baseDirectory)))
            directories.Add(Path.GetFullPath(baseDirectory));
        var agents = directories.Select(d => (d, StartsAfterReboot(d))).ToList();
        var lingerOff = OperatingSystem.IsLinux() && agents.Any(a => !a.Item2) && !LingerEnabled();
        foreach (var line in RebootLines(agents, lingerOff, Environment.UserName))
            Console.WriteLine(line);
    }

    /// <summary>The report, one line each; pure so that it can be tested.</summary>
    public static IReadOnlyList<string> RebootLines(IReadOnlyList<(string Directory, bool StartsAfterReboot)> agents, bool lingerOff, string user)
    {
        if (agents.Count == 0) return [];
        var missing = agents.Where(a => !a.StartsAfterReboot).Select(a => a.Directory).ToList();
        if (missing.Count == 0)
            return [agents.Count == 1
                ? "[SideHub] The agent starts again after a reboot of the machine."
                : $"[SideHub] The {agents.Count} agents start again after a reboot of the machine."];

        var lines = new List<string>
        {
            missing.Count == 1
                ? "[SideHub] ! Won't start again after a reboot of the machine:"
                : $"[SideHub] ! {missing.Count} agents won't start again after a reboot of the machine:",
        };
        lines.AddRange(missing.Select(d => $"            {d}"));
        lines.Add(agents.Count == 1
            ? $"          run in that folder: sidehub-agent service install"
            : $"          run: {BootCommand}   (one entry starts every agent of this machine)");
        if (lingerOff)
            lines.Add($"          and, as lingering is off: sudo loginctl enable-linger {user}");
        return lines;
    }

    /// <summary>
    /// A oneshot unit: <c>start --all -d</c> starts the daemons and exits. <c>KillMode=process</c>: the daemons outlive
    /// it, stopping the unit stops none of them.
    /// </summary>
    public static string SystemdBootUnit(string executable, string? pathVariable, bool allowRoot)
    {
        var exec = string.Join(' ', new[] { executable, "start", "--all", "-d" }.Select(SystemdQuote))
            + (allowRoot ? $" {RootPolicy.AllowFlag}" : "");
        var environment = string.IsNullOrEmpty(pathVariable) ? "" : $"Environment={SystemdQuote("PATH=" + pathVariable)}\n";
        return $"""
            # Written by `{BootCommand}`; remove with `sidehub-agent service uninstall --all`.
            [Unit]
            Description=SideHub agents of this machine (start --all)

            [Service]
            Type=oneshot
            RemainAfterExit=yes
            WorkingDirectory=~
            ExecStart={exec}
            {environment}KillMode=process
            TimeoutStartSec=300
            UMask=0077

            [Install]
            WantedBy=default.target

            """;
    }

    /// <summary>Runs <c>start --all -d</c> at login; <c>AbandonProcessGroup</c>: launchd leaves the daemons alone when it exits.</summary>
    public static string LaunchdBootPlist(string executable, string home, string? pathVariable, bool allowRoot)
    {
        var arguments = new List<string> { executable, "start", "--all", "-d" };
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
            <!-- Written by sidehub-agent (service install, all agents); removed by its service uninstall. -->
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{BootLaunchdLabel}</string>
                <key>ProgramArguments</key>
                <array>
            {string.Join('\n', arguments.Select(a => $"        <string>{Xml(a)}</string>"))}
                </array>
                <key>WorkingDirectory</key>
                <string>{Xml(home)}</string>{environment}
                <key>RunAtLoad</key>
                <true/>
                <key>AbandonProcessGroup</key>
                <true/>
                <key>ProcessType</key>
                <string>Background</string>
            </dict>
            </plist>

            """;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string BootDefinitionPath() => OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "LaunchAgents", BootLaunchdLabel + ".plist")
        : Path.Combine(SystemdUserDirectory(), BootUnitName);

    private static bool IsRegistered(string baseDirectory) =>
        InstanceRegistry.LoadValid().Any(i => SamePath(i.Directory, baseDirectory));

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

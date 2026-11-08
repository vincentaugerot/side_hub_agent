using System.Xml.Linq;

namespace SideHub.Agent.Tests;

public class AgentServiceTests
{
    [Fact]
    public void Service_name_is_readable_and_unique_per_folder()
    {
        var a = AgentService.ServiceName("/home/me/My Project");
        var b = AgentService.ServiceName("/srv/other/My Project");

        Assert.StartsWith("sidehub-agent-my-project-", a);
        Assert.Matches("^sidehub-agent-my-project-[0-9a-f]{8}$", a);
        Assert.NotEqual(a, b);
        Assert.Equal(a, AgentService.ServiceName("/home/me/My Project/"));
    }

    [Fact]
    public void Service_name_without_a_usable_folder_name_is_the_hash()
    {
        Assert.Matches("^sidehub-agent-[0-9a-f]{8}$", AgentService.ServiceName("/home/me/项目"));
    }

    [Fact]
    public void Systemd_unit_runs_the_daemon_from_the_folder_with_the_user_path()
    {
        var unit = AgentService.SystemdUnit("/home/me/app", "/usr/local/lib/sidehub-agent/sidehub-agent", "/home/me/.nvm/bin:/usr/bin", allowRoot: false);

        Assert.Contains("WorkingDirectory=/home/me/app\n", unit);
        Assert.Contains("ExecStart=\"/usr/local/lib/sidehub-agent/sidehub-agent\" \"--foreground-daemon\" " +
                        "\"/home/me/app/.sidehub/run/sidehub-agent.log\" \"/home/me/app/.sidehub/run/sidehub-agent.pid\"\n", unit);
        Assert.Contains("Environment=\"PATH=/home/me/.nvm/bin:/usr/bin\"\n", unit);
        Assert.Contains("KillSignal=SIGINT", unit);
        Assert.Contains("WantedBy=default.target", unit);
        Assert.DoesNotContain(RootPolicy.AllowFlag, unit);
    }

    [Fact]
    public void Systemd_unit_escapes_specifiers_and_quotes()
    {
        var unit = AgentService.SystemdUnit("/home/me/100%", "/opt/a \"b\"/sidehub-agent", null, allowRoot: true);

        Assert.Contains("WorkingDirectory=/home/me/100%%\n", unit);
        Assert.Contains("ExecStart=\"/opt/a \\\"b\\\"/sidehub-agent\"", unit);
        Assert.Contains($"sidehub-agent.pid\" {RootPolicy.AllowFlag}\n", unit);
        Assert.DoesNotContain("Environment=", unit);
    }

    [Fact]
    public void Launchd_plist_is_valid_xml_with_escaped_values()
    {
        var plist = AgentService.LaunchdPlist("io.sidehub.agent.x", "/Users/me/R&D", "/usr/local/bin/sidehub-agent", "/opt/homebrew/bin:/usr/bin", allowRoot: false);

        var doc = XDocument.Parse(plist);
        var strings = doc.Descendants("string").Select(e => e.Value).ToList();
        Assert.Contains("/Users/me/R&D", strings);
        Assert.Contains("/Users/me/R&D/.sidehub/run/sidehub-agent.pid", strings);
        Assert.Contains("/opt/homebrew/bin:/usr/bin", strings);
        Assert.DoesNotContain(RootPolicy.AllowFlag, strings);
    }

    [Fact]
    public void Scheduled_task_starts_the_daemon_from_the_folder_at_logon()
    {
        var xml = AgentService.ScheduledTaskXml("PC\\me", @"C:\Users\me\R&D", @"C:\Users\me\AppData\Local\Programs\sidehub-agent\sidehub-agent.exe");

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("PC\\me", doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        var exec = doc.Descendants(ns + "Exec").Single();
        Assert.Equal("start -d", exec.Element(ns + "Arguments")!.Value);
        Assert.Equal(@"C:\Users\me\R&D", exec.Element(ns + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void Boot_unit_starts_every_registered_agent_and_leaves_them_running_when_stopped()
    {
        var unit = AgentService.SystemdBootUnit("/usr/local/lib/sidehub-agent/sidehub-agent", "/home/me/.nvm/bin:/usr/bin", allowRoot: true);

        Assert.Contains("ExecStart=\"/usr/local/lib/sidehub-agent/sidehub-agent\" \"start\" \"--all\" \"-d\" " + RootPolicy.AllowFlag + "\n", unit);
        Assert.Contains("Type=oneshot\n", unit);
        Assert.Contains("RemainAfterExit=yes\n", unit);
        Assert.Contains("KillMode=process\n", unit);
        Assert.Contains("Environment=\"PATH=/home/me/.nvm/bin:/usr/bin\"\n", unit);
        Assert.Contains("WantedBy=default.target", unit);
        Assert.DoesNotContain("Restart=", unit);
    }

    [Fact]
    public void Boot_plist_runs_start_all_at_login_and_abandons_the_daemons()
    {
        var plist = AgentService.LaunchdBootPlist("/usr/local/bin/sidehub-agent", "/Users/me", "/opt/homebrew/bin", allowRoot: false);

        var doc = XDocument.Parse(plist);
        var arguments = doc.Descendants("array").Single().Elements("string").Select(e => e.Value).ToList();
        Assert.Equal(["/usr/local/bin/sidehub-agent", "start", "--all", "-d"], arguments);
        var keys = doc.Descendants("key").Select(e => e.Value).ToList();
        Assert.Contains("AbandonProcessGroup", keys);
        Assert.DoesNotContain("KeepAlive", keys);
    }

    [Fact]
    public void Boot_scheduled_task_runs_start_all_from_the_home_folder()
    {
        var xml = AgentService.ScheduledTaskXml("PC\\me", @"C:\Users\me", @"C:\sidehub-agent.exe", "start --all -d", "SideHub agents of this machine");

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("start --all -d", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("SideHub agents of this machine", doc.Descendants(ns + "Description").Single().Value);
    }

    [Fact]
    public void Reboot_report_names_the_agents_that_wont_come_back_and_the_command()
    {
        var lines = AgentService.RebootLines([("/srv/a", true), ("/srv/b", false), ("/srv/c", false)], lingerOff: true, "me");

        Assert.Contains(lines, l => l.Contains("2 agents won't start again after a reboot"));
        Assert.Contains(lines, l => l.Trim() == "/srv/b");
        Assert.DoesNotContain(lines, l => l.Trim() == "/srv/a");
        Assert.Contains(lines, l => l.Contains(AgentService.BootCommand));
        Assert.Contains(lines, l => l.Contains("sudo loginctl enable-linger me"));
    }

    [Fact]
    public void Reboot_report_for_one_folder_proposes_its_own_service()
    {
        var lines = AgentService.RebootLines([("/srv/a", false)], lingerOff: false, "me");

        Assert.Contains(lines, l => l.Contains("sidehub-agent service install") && !l.Contains("--all"));
        Assert.DoesNotContain(lines, l => l.Contains("linger"));
    }

    [Fact]
    public void Reboot_report_says_so_when_every_agent_comes_back_and_nothing_without_agents()
    {
        Assert.Single(AgentService.RebootLines([("/srv/a", true), ("/srv/b", true)], lingerOff: false, "me"), "[SideHub] The 2 agents start again after a reboot of the machine.");
        Assert.Empty(AgentService.RebootLines([], lingerOff: false, "me"));
    }
}

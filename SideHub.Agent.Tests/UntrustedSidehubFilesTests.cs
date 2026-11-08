using System.Diagnostics;
using System.Runtime.Versioning;
using SideHub.Agent;
using SideHub.Agent.Models;
using SideHub.Agent.Usage;

namespace SideHub.Agent.Tests;

/// <summary>
/// .sidehub/ lives in the work tree, so a commit can fill it: a config there would point the agent to another
/// backend, a link would make the agent truncate, append to or chmod any file. Neither is trusted.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class UntrustedSidehubFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-untrusted-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("sidehub-target-").FullName;
    private string SidehubDir => Path.Combine(_dir, ".sidehub");
    private string RunDir => Path.Combine(SidehubDir, "run");
    private AgentTokenStore Tokens => new(Path.Combine(_outside, "tokens"));

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private static AgentSetup.SetupInfo Info(string name) =>
        new(name, "wss://api.sidehub.io/ws/agent", $"agent-{Path.GetFileNameWithoutExtension(name)}", "w1", null, ["shell"]);

    private string WriteConfig(string fileName)
    {
        var path = AgentSetup.WriteConfig(_dir, Info(fileName), "sh_agent_x", Tokens);
        var target = Path.Combine(SidehubDir, fileName);
        if (path != target) File.Move(path, target);
        return target;
    }

    private string Target(string content = "precious")
    {
        var path = Path.Combine(_outside, "target");
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        return path;
    }

    private static void AssertUntouched(string target, string content = "precious")
    {
        Assert.Equal(content, File.ReadAllText(target));
        Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public async Task A_config_tracked_by_git_is_ignored_with_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;
        Git("init", "-q");
        var tracked = WriteConfig("evil.json");
        Git("add", "-f", tracked);
        var local = WriteConfig("agent.json");
        var warnings = new List<string>();

        var configs = await AgentConfig.LoadAllAsync(_dir, warnings.Add, Tokens);

        Assert.Equal(local, Assert.Single(configs).ConfigFilePath);
        Assert.Contains(tracked, Assert.Single(warnings));
        Assert.Contains("tracked by git", warnings[0]);
    }

    [Fact]
    public async Task The_queue_worktree_setup_is_not_read_as_an_agent_config()
    {
        if (OperatingSystem.IsWindows()) return;
        var local = WriteConfig("agent.json");
        File.WriteAllText(Path.Combine(SidehubDir, QueueWorktree.ConfigFile), """{ "setup": "npm ci" }""");
        var warnings = new List<string>();

        var configs = await AgentConfig.LoadAllAsync(_dir, warnings.Add, Tokens);

        Assert.Equal(local, Assert.Single(configs).ConfigFilePath);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Loading_fails_when_every_config_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        Git("init", "-q");
        Git("add", "-f", WriteConfig("evil.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentConfig.LoadAllAsync(_dir, tokens: Tokens));
    }

    [Fact]
    public async Task A_config_symlink_is_ignored()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = WriteConfig("agent.json");
        var link = Path.Combine(SidehubDir, "link.json");
        File.CreateSymbolicLink(link, real);
        var warnings = new List<string>();

        var configs = await AgentConfig.LoadAllAsync(_dir, warnings.Add, Tokens);

        Assert.Equal(real, Assert.Single(configs).ConfigFilePath);
        Assert.Contains("symbolic link", Assert.Single(warnings));
    }

    [Fact]
    public async Task A_config_belonging_to_another_user_is_ignored()
    {
        if (OperatingSystem.IsWindows() || FileOwnership.CurrentUser != 0) return; // chown needs root
        var foreign = WriteConfig("foreign.json");
        Chown(foreign, 65534);
        WriteConfig("agent.json");
        var warnings = new List<string>();

        var configs = await AgentConfig.LoadAllAsync(_dir, warnings.Add, Tokens);

        Assert.Single(configs);
        Assert.Contains("another user", Assert.Single(warnings));
    }

    [Fact]
    public void A_symlinked_pid_file_is_not_truncated()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Target();
        var manager = new DaemonManager(_dir);
        manager.EnsureRunDirectory();
        File.CreateSymbolicLink(manager.PidFile, target);

        Assert.Throws<IOException>(() => manager.WritePidFile(42));
        AssertUntouched(target);
    }

    [Fact]
    public void A_symlinked_log_is_not_appended_to()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Target();
        var manager = new DaemonManager(_dir);
        manager.EnsureRunDirectory();
        File.CreateSymbolicLink(manager.LogFile, target);

        Assert.Throws<IOException>(() => manager.CreateLogWriter());
        AssertUntouched(target);
    }

    [Fact]
    public void A_symlinked_log_archive_is_not_chmodded()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Target();
        var manager = new DaemonManager(_dir);
        manager.EnsureRunDirectory();
        File.CreateSymbolicLink(manager.LogFile + ".1", target);

        Assert.Throws<IOException>(() => manager.CreateLogWriter());
        AssertUntouched(target);
    }

    [Fact]
    public void A_symlinked_run_folder_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(SidehubDir);
        Directory.CreateSymbolicLink(RunDir, _outside);
        File.SetUnixFileMode(_outside, File.GetUnixFileMode(_outside) | UnixFileMode.OtherRead);

        Assert.Throws<IOException>(() => new DaemonManager(_dir).WritePidFile(42));
        Assert.Empty(Directory.GetFiles(_outside));
        Assert.True(File.GetUnixFileMode(_outside).HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public void A_symlinked_pending_usage_folder_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        new DaemonManager(_dir).EnsureRunDirectory();
        Directory.CreateSymbolicLink(Path.Combine(RunDir, "pending-usage"), _outside);
        var store = new PendingUsageStore(Path.Combine(RunDir, "pending-usage", "a1"));

        Assert.Throws<IOException>(() => store.Save(
            new RunUsageMessage { RunId = Guid.NewGuid(), Source = "test", CollectedAt = DateTimeOffset.UtcNow, Models = [] }));
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
    }

    [Fact]
    public void The_fifo_is_not_created_through_a_symlinked_folder()
    {
        if (OperatingSystem.IsWindows()) return;
        new DaemonManager(_dir).EnsureRunDirectory();
        Directory.CreateSymbolicLink(Path.Combine(RunDir, "fifo"), _outside);

        Assert.False(NotifyFifo.TryCreate(Path.Combine(RunDir, "fifo", "a1"), "run-1", out _, out var error));
        Assert.Contains("symbolic link", error);
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
    }

    [Fact]
    public void Lstat_reports_links_and_the_owner_without_following()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Target();
        var link = Path.Combine(_dir, "link");
        File.CreateSymbolicLink(link, target);

        Assert.Equal(new FileOwnership.Status(false, FileOwnership.CurrentUser), FileOwnership.Lstat(target));
        Assert.True(FileOwnership.Lstat(link)!.Value.IsSymbolicLink);
        Assert.Null(FileOwnership.Lstat(Path.Combine(_dir, "missing")));
        Assert.NotNull(FileOwnership.CurrentUser);
    }

    [Fact]
    public void Folders_above_sidehub_may_be_links()
    {
        if (OperatingSystem.IsWindows()) return;
        var project = Path.Combine(_outside, "project");
        Directory.CreateDirectory(project);
        var linkedProject = Path.Combine(_dir, "linked-project");
        Directory.CreateSymbolicLink(linkedProject, project);
        var manager = new DaemonManager(linkedProject);

        manager.WritePidFile(42);

        Assert.Equal(42, manager.ReadPid());
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
    }

    private static void Chown(string path, int uid)
    {
        using var process = Process.Start("chown", [uid.ToString(), path])!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

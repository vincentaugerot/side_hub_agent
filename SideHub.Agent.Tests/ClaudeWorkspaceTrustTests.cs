using System.Text.Json.Nodes;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>An interactive claude launched by a SideHub run must not stop on "Do you trust this folder?".</summary>
public class ClaudeWorkspaceTrustTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-trust-").FullName;
    private string Config => Path.Combine(_dir, ".claude.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("claude", new[] { "--session-id", "x" }, "run-id", true)]
    [InlineData("claude", new[] { "-p", "--model", "m" }, "run-id", false)]
    [InlineData("claude", new[] { "--print" }, "run-id", false)]
    [InlineData("claude", new string[0], null, false)]
    [InlineData("codex", new string[0], "run-id", false)]
    public void Only_an_interactive_claude_of_a_run_is_concerned(string cli, string[] args, string? runId, bool expected) =>
        Assert.Equal(expected, ClaudeWorkspaceTrust.Applies(cli, args, runId));

    [Theory]
    [InlineData(new[] { "--session-id", "x" }, "task-id", true)]
    [InlineData(new[] { "-p" }, "task-id", false)]
    [InlineData(new string[0], "", false)]
    public void An_interactive_claude_of_a_task_is_concerned_too(string[] args, string taskId, bool expected) =>
        Assert.Equal(expected, ClaudeWorkspaceTrust.Applies("claude", args, runId: null, taskId));

    [Theory]
    [InlineData(new[] { "--session-id", "x" }, true)]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "-p" }, false)]
    public void An_interactive_claude_in_the_folder_SideHub_opened_its_terminal_in_is_concerned(string[] args, bool expected)
    {
        Assert.Equal(expected, ClaudeWorkspaceTrust.Applies("claude", args, runId: null, taskId: null, openedFolder: true));
        // Its other first-launch questions (project MCP servers) stay the person's: it is not unattended.
        Assert.False(ClaudeWorkspaceTrust.Applies("claude", args, runId: null, taskId: null));
    }

    [Fact]
    public void Only_the_folder_the_terminal_was_opened_in_counts()
    {
        var repo = Path.Combine(_dir, "repo");
        Assert.True(ClaudeWorkspaceTrust.InOpenedFolder(repo, repo));
        Assert.True(ClaudeWorkspaceTrust.InOpenedFolder(repo + Path.DirectorySeparatorChar, repo));
        Assert.False(ClaudeWorkspaceTrust.InOpenedFolder(repo, Path.Combine(_dir, "elsewhere")));
        Assert.False(ClaudeWorkspaceTrust.InOpenedFolder(repo, Path.Combine(repo, "sub")));
        Assert.False(ClaudeWorkspaceTrust.InOpenedFolder(null, repo));
        Assert.False(ClaudeWorkspaceTrust.InOpenedFolder("", repo));
    }

    [Fact]
    public void The_folder_is_trusted_and_the_rest_of_the_config_kept()
    {
        File.WriteAllText(Config, """
            { "numStartups": 12, "projects": { "/srv/other": { "hasTrustDialogAccepted": false, "allowedTools": ["Bash"] },
              "/srv/repo": { "lastCost": 1.5 } } }
            """);

        Assert.True(ClaudeWorkspaceTrust.Accept(Config, "/srv/repo"));

        var root = JsonNode.Parse(File.ReadAllText(Config))!;
        Assert.Equal(12, root["numStartups"]!.GetValue<int>());
        Assert.True(root["projects"]!["/srv/repo"]!["hasTrustDialogAccepted"]!.GetValue<bool>());
        Assert.Equal(1.5, root["projects"]!["/srv/repo"]!["lastCost"]!.GetValue<double>());
        Assert.False(root["projects"]!["/srv/other"]!["hasTrustDialogAccepted"]!.GetValue<bool>());
        Assert.Equal("Bash", root["projects"]!["/srv/other"]!["allowedTools"]![0]!.GetValue<string>());
        Assert.Single(Directory.GetFiles(_dir));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Config));
    }

    [Fact]
    public void An_already_trusted_folder_is_left_untouched()
    {
        File.WriteAllText(Config, """{"projects":{"/srv/repo":{"hasTrustDialogAccepted":true}}}""");
        var before = File.GetLastWriteTimeUtc(Config);

        Assert.False(ClaudeWorkspaceTrust.Accept(Config, "/srv/repo"));
        Assert.Equal("""{"projects":{"/srv/repo":{"hasTrustDialogAccepted":true}}}""", File.ReadAllText(Config));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Config));
    }

    [Fact]
    public void A_missing_config_is_created()
    {
        Assert.True(ClaudeWorkspaceTrust.Accept(Config, "/srv/repo"));
        Assert.True(JsonNode.Parse(File.ReadAllText(Config))!["projects"]!["/srv/repo"]!["hasTrustDialogAccepted"]!.GetValue<bool>());
    }

    [Fact]
    public void An_invalid_config_is_left_alone()
    {
        File.WriteAllText(Config, "[1, 2]");

        Assert.Throws<InvalidDataException>(() => ClaudeWorkspaceTrust.Accept(Config, "/srv/repo"));
        Assert.Equal("[1, 2]", File.ReadAllText(Config));
        Assert.Single(Directory.GetFiles(_dir));
    }
}

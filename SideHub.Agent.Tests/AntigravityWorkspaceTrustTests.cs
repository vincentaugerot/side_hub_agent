using System.Text.Json.Nodes;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>An interactive agy launched by a SideHub run or task must not stop on "Do you trust the contents of this project?".</summary>
public class AntigravityWorkspaceTrustTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-agy-trust-").FullName;
    private string Settings => Path.Combine(_dir, "antigravity-cli", "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("agy", new[] { "--model", "m" }, "run-id", null, true)]
    [InlineData("agy", new[] { "-i" }, null, "task-id", true)]
    [InlineData("agy", new[] { "-p" }, "run-id", null, false)]
    [InlineData("agy", new[] { "--prompt" }, "run-id", null, false)]
    [InlineData("agy", new string[0], null, null, false)]
    [InlineData("claude", new string[0], "run-id", null, false)]
    public void Only_an_interactive_agy_of_a_run_or_a_task_is_concerned(string cli, string[] args, string? runId, string? taskId, bool expected) =>
        Assert.Equal(expected, AntigravityWorkspaceTrust.Applies(cli, args, runId, taskId));

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "-p" }, false)]
    public void An_interactive_agy_in_the_folder_SideHub_opened_its_terminal_in_is_concerned(string[] args, bool expected) =>
        Assert.Equal(expected, AntigravityWorkspaceTrust.Applies("agy", args, runId: null, taskId: null, openedFolder: true));

    [Fact]
    public void The_folder_is_added_once_and_the_rest_of_the_settings_kept()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, """{ "trustedWorkspaces": ["/srv/other"], "toolPermission": "request-review" }""");

        Assert.True(AntigravityWorkspaceTrust.Accept(Settings, "/srv/repo"));
        Assert.False(AntigravityWorkspaceTrust.Accept(Settings, "/srv/repo"));

        var root = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.Equal(["/srv/other", "/srv/repo"], root["trustedWorkspaces"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("request-review", root["toolPermission"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_settings_are_created()
    {
        Assert.True(AntigravityWorkspaceTrust.Accept(Settings, "/srv/repo"));

        Assert.Equal(["/srv/repo"], JsonNode.Parse(File.ReadAllText(Settings))!["trustedWorkspaces"]!.AsArray().Select(n => n!.GetValue<string>()));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Settings));
    }

    [Theory]
    [InlineData("""{ "trustedWorkspaces": "/srv" }""")]
    [InlineData("""[]""")]
    [InlineData("""{ broken""")]
    public void Settings_it_cannot_read_are_left_alone(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllText(Settings, content);

        Assert.ThrowsAny<Exception>(() => AntigravityWorkspaceTrust.Accept(Settings, "/srv/repo"));
        Assert.Equal(content, File.ReadAllText(Settings));
    }
}

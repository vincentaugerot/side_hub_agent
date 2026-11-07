using System.Text.Json.Nodes;
using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>
/// An interactive codex or copilot launched by a SideHub run or task must not stop on its folder trust question. Both
/// check the main working tree of the repository, also from a sub-folder or a linked worktree.
/// </summary>
public class RepositoryTrustTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-repo-trust-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void A_sub_folder_is_trusted_with_its_repository()
    {
        var repo = Folder("repo");
        Folder("repo", ".git");

        Assert.Equal(repo, GitTrustRoot.Of(repo));
        Assert.Equal(repo, GitTrustRoot.Of(Folder("repo", "src", "app")));
    }

    [Fact]
    public void A_linked_worktree_is_trusted_with_its_main_working_tree()
    {
        var repo = Folder("repo");
        var gitDir = Folder("repo", ".git", "worktrees", "agent");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        var worktree = Folder("elsewhere", "agent");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");

        Assert.Equal(repo, GitTrustRoot.Of(Folder("elsewhere", "agent", "src")));
    }

    [Fact]
    public void A_submodule_is_its_own_repository()
    {
        Folder("repo", ".git", "modules", "lib");
        var submodule = Folder("repo", "lib");
        File.WriteAllText(Path.Combine(submodule, ".git"), "gitdir: ../.git/modules/lib\n");

        Assert.Equal(submodule, GitTrustRoot.Of(submodule));
    }

    [Fact]
    public void Outside_git_the_folder_itself_is_trusted()
    {
        var folder = Folder("plain");

        Assert.Equal(folder, GitTrustRoot.Of(folder));
    }

    [Theory]
    [InlineData("codex", new[] { "--model", "m" }, "run-id", null, true)]
    [InlineData("codex", new[] { "resume", "id" }, null, "task-id", true)]
    [InlineData("codex", new[] { "exec", "--json" }, "run-id", null, false)]
    [InlineData("codex", new[] { "e" }, "run-id", null, false)]
    [InlineData("codex", new string[0], null, null, false)]
    [InlineData("claude", new string[0], "run-id", null, false)]
    public void Only_an_interactive_codex_of_a_run_or_a_task_is_concerned(string cli, string[] args, string? runId, string? taskId, bool expected) =>
        Assert.Equal(expected, CodexWorkspaceTrust.Applies(cli, args, runId, taskId));

    [Theory]
    [InlineData("copilot", new[] { "-i" }, null, "task-id", true)]
    [InlineData("copilot", new[] { "-p" }, "run-id", null, false)]
    [InlineData("copilot", new string[0], null, null, false)]
    [InlineData("codex", new string[0], "run-id", null, false)]
    public void Only_an_interactive_copilot_of_a_run_or_a_task_is_concerned(string cli, string[] args, string? runId, string? taskId, bool expected) =>
        Assert.Equal(expected, CopilotWorkspaceTrust.Applies(cli, args, runId, taskId));

    [Fact]
    public void Codex_gets_the_trusted_table_once_and_keeps_the_rest_of_its_config()
    {
        var config = Path.Combine(_dir, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        const string existing = "model = \"gpt-5\"\n\n[projects.\"/srv/repo_agent\"]\ntrust_level = \"trusted\"";
        File.WriteAllText(config, existing);

        Assert.True(CodexWorkspaceTrust.Accept(config, "/srv/repo"));
        Assert.False(CodexWorkspaceTrust.Accept(config, "/srv/repo"));

        Assert.Equal(existing + "\n\n[projects.\"/srv/repo\"]\ntrust_level = \"trusted\"\n", File.ReadAllText(config));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config));
    }

    [Theory]
    [InlineData("[projects.\"/srv/repo\"]\ntrust_level = \"untrusted\"\n")]
    [InlineData("[projects]\n\"/srv/repo\" = { trust_level = \"untrusted\" }\n")]
    public void A_folder_codex_already_names_is_left_to_its_decision(string content)
    {
        var config = Path.Combine(_dir, "config.toml");
        File.WriteAllText(config, content);

        Assert.False(CodexWorkspaceTrust.Accept(config, "/srv/repo"));
        Assert.Equal(content, File.ReadAllText(config));
    }

    [Fact]
    public void A_codex_config_with_inline_projects_is_left_alone()
    {
        var config = Path.Combine(_dir, "config.toml");
        const string content = "projects = { \"/srv/other\" = { trust_level = \"trusted\" } }\n";
        File.WriteAllText(config, content);

        Assert.ThrowsAny<Exception>(() => CodexWorkspaceTrust.Accept(config, "/srv/repo"));
        Assert.Equal(content, File.ReadAllText(config));
    }

    [Fact]
    public void A_missing_codex_config_is_created()
    {
        var config = Path.Combine(_dir, ".codex", "config.toml");

        Assert.True(CodexWorkspaceTrust.Accept(config, @"C:\src\my ""repo"""));

        Assert.Equal("\n[projects.\"C:\\\\src\\\\my \\\"repo\\\"\"]\ntrust_level = \"trusted\"\n", File.ReadAllText(config));
    }

    [Fact]
    public void Copilot_gets_the_folder_once_and_keeps_its_sign_in_and_header()
    {
        var config = Path.Combine(_dir, "config.json");
        const string header = "// User settings belong in settings.json.\n// This file is managed automatically.\n";
        File.WriteAllText(config, header + """{ "authTokens": { "github": "secret" }, "trustedFolders": ["/srv/other"] }""");

        Assert.True(CopilotWorkspaceTrust.Accept(config, "/srv/repo"));
        Assert.False(CopilotWorkspaceTrust.Accept(config, "/srv/repo"));

        var text = File.ReadAllText(config);
        Assert.StartsWith(header + "{", text);
        var root = JsonNode.Parse(text[header.Length..])!;
        Assert.Equal(["/srv/other", "/srv/repo"], root["trustedFolders"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("secret", root["authTokens"]!["github"]!.GetValue<string>());
    }

    [Fact]
    public void A_missing_copilot_config_is_created()
    {
        var config = Path.Combine(_dir, ".copilot", "config.json");

        Assert.True(CopilotWorkspaceTrust.Accept(config, "/srv/repo"));

        Assert.Equal(["/srv/repo"], JsonNode.Parse(File.ReadAllText(config))!["trustedFolders"]!.AsArray().Select(n => n!.GetValue<string>()));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config));
    }

    [Theory]
    [InlineData("""{ "trustedFolders": "/srv" }""")]
    [InlineData("""[]""")]
    [InlineData("""{ broken""")]
    [InlineData("not json { \"trustedFolders\": [] }")]
    public void A_copilot_config_it_cannot_read_is_left_alone(string content)
    {
        var config = Path.Combine(_dir, "config.json");
        File.WriteAllText(config, content);

        Assert.ThrowsAny<Exception>(() => CopilotWorkspaceTrust.Accept(config, "/srv/repo"));
        Assert.Equal(content, File.ReadAllText(config));
    }
}

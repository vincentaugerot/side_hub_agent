namespace SideHub.Cli.Launch;

/// <summary>
/// The first time codex's TUI runs in a repository, it asks "Trust this folder?", even with
/// <c>--dangerously-bypass-approvals-and-sandbox</c>; <c>codex exec</c> never asks. Like
/// <see cref="ClaudeWorkspaceTrust"/>, what SideHub launches interactively for a run or a task has nobody to answer yet:
/// the repository (<see cref="GitTrustRoot"/>, which codex also checks for a sub-folder or a linked worktree) gets the
/// <c>[projects."&lt;folder&gt;"] trust_level = "trusted"</c> table accepting the dialog would write in codex's config.
/// </summary>
public static class CodexWorkspaceTrust
{
    /// <summary>An interactive codex launched for a run or a task, or (<paramref name="openedFolder"/>) started in the
    /// folder SideHub opened its terminal in, where the dialog is out of sight in Conversation mode.</summary>
    public static bool Applies(string cli, IReadOnlyList<string> arguments, string? runId, string? taskId, bool openedFolder = false) =>
        cli == "codex"
        && (openedFolder || !string.IsNullOrEmpty(runId) || !string.IsNullOrEmpty(taskId))
        && !(arguments.Count > 0 && CliLaunchPlan.HeadlessCodexSubcommands.Contains(arguments[0]));

    /// <summary><c>$CODEX_HOME/config.toml</c>, else <c>~/.codex/config.toml</c>; null without a home.</summary>
    public static string? ConfigPath()
    {
        if (Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome)
            return Path.Combine(codexHome, "config.toml");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".codex", "config.toml");
    }

    /// <summary>
    /// Appends the trusted table of <paramref name="directory"/> to <paramref name="configPath"/>, keeping the rest of the
    /// file as is. Returns false when the file already names the folder (trusted or not: the user's decision stands, and
    /// a second table for it would make the whole file invalid for codex); throws when it holds its projects in a form
    /// a table cannot be added to (it is then left alone).
    /// </summary>
    public static bool Accept(string configPath, string directory)
    {
        var text = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        var key = TomlString(directory);
        if (text.Contains(key, StringComparison.Ordinal) || text.Contains($"'{directory}'", StringComparison.Ordinal))
            return false;
        if (text.Split('\n').Any(line => line.TrimStart().StartsWith("projects", StringComparison.Ordinal)
                && line.TrimStart()["projects".Length..].TrimStart().StartsWith('=')))
            throw new InvalidDataException($"{configPath}: projects is an inline table.");
        var separator = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
        ConfigFiles.ReplaceText(configPath, $"{text}{separator}\n[projects.{key}]\ntrust_level = \"trusted\"\n");
        return true;
    }

    /// <summary>A TOML basic string (codex writes its project keys this way, Windows paths with doubled backslashes).</summary>
    internal static string TomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

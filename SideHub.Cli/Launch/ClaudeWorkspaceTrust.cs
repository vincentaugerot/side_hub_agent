using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>
/// The first time claude runs interactively in a folder, it asks whether to trust it, "No, exit" selected; headless
/// (<c>-p</c>) it never asks. What SideHub launches interactively has nobody to answer yet: an interactive workflow
/// step (its agent drafts first, and the validation SideHub types later would pick "No, exit"), a task of the agent's
/// queue (launched while nobody watches), a terminal the person opened from SideHub (in Conversation mode, the dialog
/// is out of sight: the first message would answer it, pick "No, exit" and end the session). Its folder is the agent's
/// repository, where SideHub's headless runs already work without the dialog: it is marked trusted, as accepting the
/// dialog would (<c>projects.&lt;folder&gt;.hasTrustDialogAccepted</c> in claude's global config).
/// </summary>
public static class ClaudeWorkspaceTrust
{
    public const string RunIdVariable = "SIDEHUB_RUN_ID";
    public const string TaskIdVariable = "SIDEHUB_TASK_ID";

    /// <summary>Set by the agent in each terminal it opens: the folder it opened it in (real path), inside the agent's
    /// repository. Only a CLI started right there gets its trust question answered (<see cref="InOpenedFolder"/>).</summary>
    public const string OpenedFolderVariable = "SIDEHUB_PTY_FOLDER";

    /// <summary>A claude launched by SideHub for a run (<c>$SIDEHUB_RUN_ID</c>) or a task (<c>$SIDEHUB_TASK_ID</c>), or
    /// started in the folder SideHub opened its terminal in (<paramref name="openedFolder"/>), interactive: the one that
    /// would ask.</summary>
    public static bool Applies(string cli, IReadOnlyList<string> arguments, string? runId, string? taskId = null, bool openedFolder = false) =>
        cli == "claude" && Answered(arguments, runId, taskId, openedFolder);

    /// <summary>A CLI launched by SideHub for a run or a task, interactive (not <c>-p</c> / <c>--print</c>): nobody is
    /// there yet to answer its first-launch questions.</summary>
    public static bool Unattended(IReadOnlyList<string> arguments, string? runId, string? taskId) =>
        (!string.IsNullOrEmpty(runId) || !string.IsNullOrEmpty(taskId)) && Interactive(arguments);

    /// <summary>A CLI whose question about trusting its folder SideHub answers: unattended, or interactive in the folder
    /// SideHub opened its terminal in. Its other first-launch questions (project MCP servers) stay the person's.</summary>
    public static bool Answered(IReadOnlyList<string> arguments, string? runId, string? taskId, bool openedFolder) =>
        Unattended(arguments, runId, taskId) || openedFolder && Interactive(arguments);

    private static bool Interactive(IReadOnlyList<string> arguments) =>
        !arguments.Any(a => a is "-p" or "--print" or "--prompt");

    /// <summary>
    /// Whether <paramref name="currentDirectory"/> is the folder the agent opened the terminal in
    /// (<see cref="OpenedFolderVariable"/>): the agent's repository, or the folder of it SideHub asked for. After a
    /// <c>cd</c> elsewhere, the CLI asks as it would anywhere.
    /// </summary>
    public static bool InOpenedFolder(string? openedFolder, string currentDirectory)
    {
        if (string.IsNullOrEmpty(openedFolder) || string.IsNullOrEmpty(currentDirectory))
            return false;
        static string Normalized(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(Normalized(openedFolder), Normalized(currentDirectory),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary><c>$CLAUDE_CONFIG_DIR/.claude.json</c>, else <c>~/.claude.json</c>; null without a home.</summary>
    public static string? ConfigPath()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configDir)
            return Path.Combine(configDir, ".claude.json");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".claude.json");
    }

    /// <summary>claude keys its projects by path with forward slashes, on Windows too.</summary>
    public static string ProjectKey(string directory) =>
        OperatingSystem.IsWindows() ? directory.Replace('\\', '/') : directory;

    /// <summary>
    /// Marks <paramref name="directory"/> trusted in <paramref name="configPath"/>, keeping everything else. Written to a
    /// private temporary file then moved over the config, so claude never reads half a file. Returns false when it already
    /// was; throws on an unreadable or invalid config (it is then left alone).
    /// </summary>
    public static bool Accept(string configPath, string directory)
    {
        var root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                ?? throw new InvalidDataException($"{configPath} is not a JSON object.")
            : new JsonObject();
        if (root["projects"] is not JsonObject projects)
            root["projects"] = projects = new JsonObject();
        var key = ProjectKey(directory);
        if (projects[key] is not JsonObject project)
            projects[key] = project = new JsonObject();
        if (project["hasTrustDialogAccepted"] is JsonValue accepted && accepted.TryGetValue<bool>(out var yes) && yes)
            return false;
        project["hasTrustDialogAccepted"] = true;
        ConfigFiles.Replace(configPath, root);
        return true;
    }
}

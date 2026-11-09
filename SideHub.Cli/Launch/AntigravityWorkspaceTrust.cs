using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>
/// The first time agy runs interactively in a folder, it asks whether to trust it ("Do you trust the contents of this
/// project?"); headless (<c>-p</c>) it never asks. Trusting a folder does not trust its sub-folders. Like
/// <see cref="ClaudeWorkspaceTrust"/>, what SideHub launches interactively for a run or a task has nobody to answer yet
/// (nor is the dialog seen in Conversation mode, in the folder SideHub opened the terminal in):
/// its folder is added to <c>trustedWorkspaces</c> in agy's settings, as accepting the dialog would.
/// </summary>
public static class AntigravityWorkspaceTrust
{
    public static bool Applies(string cli, IReadOnlyList<string> arguments, string? runId, string? taskId, bool openedFolder = false) =>
        cli == "agy" && ClaudeWorkspaceTrust.Answered(arguments, runId, taskId, openedFolder);

    /// <summary><c>~/.gemini/antigravity-cli/settings.json</c>; null without a home.</summary>
    public static string? SettingsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".gemini", "antigravity-cli", "settings.json");
    }

    /// <summary>
    /// Adds <paramref name="directory"/> to <c>trustedWorkspaces</c> in <paramref name="settingsPath"/>, keeping everything
    /// else. Returns false when it already was there; throws on an unreadable or invalid file (it is then left alone).
    /// </summary>
    public static bool Accept(string settingsPath, string directory)
    {
        var root = File.Exists(settingsPath) && File.ReadAllText(settingsPath) is { } text && !string.IsNullOrWhiteSpace(text)
            ? JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{settingsPath} is not a JSON object.")
            : new JsonObject();
        if (root["trustedWorkspaces"] is not JsonArray trusted)
        {
            if (root["trustedWorkspaces"] is not null)
                throw new InvalidDataException($"{settingsPath}: trustedWorkspaces is not a list.");
            root["trustedWorkspaces"] = trusted = [];
        }
        if (trusted.Any(t => t is JsonValue value && value.TryGetValue<string>(out var path) && path == directory))
            return false;
        trusted.Add(directory);
        ConfigFiles.Replace(settingsPath, root);
        return true;
    }
}

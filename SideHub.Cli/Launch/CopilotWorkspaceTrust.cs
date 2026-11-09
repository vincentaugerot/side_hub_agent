using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>
/// The first time copilot runs interactively in a repository, it asks "Do you trust the files in this folder?";
/// headless (<c>-p</c>) it never asks. Like <see cref="ClaudeWorkspaceTrust"/>, what SideHub launches interactively for a
/// run or a task has nobody to answer yet: the repository (<see cref="GitTrustRoot"/>, which copilot also checks for a
/// sub-folder or a linked worktree) is added to <c>trustedFolders</c> in copilot's <c>config.json</c>, as "Yes, and
/// remember this folder" would.
/// </summary>
public static class CopilotWorkspaceTrust
{
    public static bool Applies(string cli, IReadOnlyList<string> arguments, string? runId, string? taskId, bool openedFolder = false) =>
        cli == "copilot" && ClaudeWorkspaceTrust.Answered(arguments, runId, taskId, openedFolder);

    /// <summary><c>$COPILOT_HOME/config.json</c>, else <c>~/.copilot/config.json</c>; null without a home.</summary>
    public static string? ConfigPath()
    {
        if (Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } copilotHome)
            return Path.Combine(copilotHome, "config.json");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".copilot", "config.json");
    }

    /// <summary>
    /// Adds <paramref name="directory"/> to <c>trustedFolders</c> in <paramref name="configPath"/>, keeping the rest of
    /// the file (its sign-in among it) and the <c>//</c> lines copilot heads it with. Returns false when it already was
    /// there; throws on an unreadable or invalid file (it is then left alone).
    /// </summary>
    public static bool Accept(string configPath, string directory)
    {
        var text = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        var start = text.IndexOf('{');
        var header = start < 0 ? text : text[..start];
        if (header.Split('\n').Any(line => line.Trim() is { Length: > 0 } content && !content.StartsWith("//", StringComparison.Ordinal)))
            throw new InvalidDataException($"{configPath} is not a JSON object.");
        var root = start < 0
            ? new JsonObject()
            : JsonNode.Parse(text[start..], documentOptions: new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidDataException($"{configPath} is not a JSON object.");
        if (root["trustedFolders"] is not JsonArray trusted)
        {
            if (root["trustedFolders"] is not null)
                throw new InvalidDataException($"{configPath}: trustedFolders is not a list.");
            root["trustedFolders"] = trusted = [];
        }
        if (trusted.Any(t => t is JsonValue value && value.TryGetValue<string>(out var path) && path == directory))
            return false;
        trusted.Add(directory);
        ConfigFiles.Replace(configPath, root, header);
        return true;
    }
}

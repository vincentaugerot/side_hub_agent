using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SideHub.Agent;

public class AgentConfig
{
    public const string ConfigFolder = ".sidehub";

    [JsonPropertyName("sidehubUrl")]
    public string? SidehubUrl { get; init; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("repositoryId")]
    public string? RepositoryId { get; init; }

    /// <summary>
    /// Read from the <see cref="AgentTokenStore"/>, outside the project. Configs written before it held the token
    /// inline: <see cref="Load"/> moves it to the store and removes it from the file.
    /// </summary>
    [JsonPropertyName("agentToken")]
    public string? AgentToken { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("capabilities")]
    public string[]? Capabilities { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Whether the backend may run one-shot commands (<c>command.execute</c>).
    /// Defaults to true. Turning it off does not sandbox the agent: PTYs still run what the
    /// backend types (see SECURITY.md).</summary>
    [JsonPropertyName("allowCommandExecute")]
    public bool AllowCommandExecute { get; init; } = true;

    /// <summary>Whether the backend may write files into the working directory
    /// (<c>file.write.*</c> and <c>terminal.attachment.enqueue</c>, used for terminal image
    /// uploads). Defaults to true.</summary>
    [JsonPropertyName("allowFileWrite")]
    public bool AllowFileWrite { get; init; } = true;

    [JsonIgnore]
    public string? ConfigFilePath { get; private set; }

    /// <summary>Where the token is expected, for the error when it is missing.</summary>
    private string? _tokenPath;

    /// <summary>
    /// Loads every <c>.sidehub/*.json</c> the agent may trust. A config decides which backend the agent obeys, and
    /// the backend runs commands in its PTYs: a config arriving through a commit (<c>sidehubUrl</c> pointing to an
    /// attacker's server) would hand the machine over after the next pull and restart. So configs tracked by git,
    /// symbolic links and files belonging to another user are ignored, with one warning each. Throws when no
    /// config is left.
    /// </summary>
    public static async Task<List<AgentConfig>> LoadAllAsync(
        string baseDirectory, Action<string>? warn = null, AgentTokenStore? tokens = null)
    {
        var configDir = Path.Combine(baseDirectory, ConfigFolder);

        if (!Directory.Exists(configDir))
        {
            throw new DirectoryNotFoundException(
                $"Configuration directory not found: {configDir}\n" +
                $"Please create a .sidehub folder with agent configuration files (*.json)");
        }

        if (PrivateFiles.UntrustedReason(configDir) is { } dirReason)
            throw new InvalidOperationException($"Refusing to load agent configurations: {dirReason}");

        var configFiles = Directory.GetFiles(configDir, "*.json");

        if (configFiles.Length == 0)
        {
            throw new FileNotFoundException(
                $"No agent configuration files found in {configDir}\n" +
                "Please create at least one .json configuration file");
        }

        var repository = await GitRepository.OpenAsync(configDir);
        var configs = new List<AgentConfig>();

        foreach (var file in configFiles.Order(StringComparer.Ordinal))
        {
            // Not an agent config: the queue worktree's setup lives next to them (doc 71).
            if (Path.GetFileName(file).Equals(QueueWorktree.ConfigFile, StringComparison.OrdinalIgnoreCase))
                continue;

            var reason = PrivateFiles.UntrustedReason(file);
            if (reason is null && repository is not null && await repository.IsTrackedAsync(file))
                reason = "it is tracked by git (a commit could point the agent to another backend); " +
                         $"git rm --cached it and keep {ConfigFolder}/ in .gitignore";
            if (reason is not null)
            {
                warn?.Invoke($"Ignoring {file}: {reason}");
                continue;
            }

            configs.Add(Load(file, tokens, warn));
        }

        if (configs.Count == 0)
        {
            throw new InvalidOperationException(
                $"No trusted agent configuration in {configDir} (see the warnings above)");
        }

        return configs;
    }

    /// <summary>
    /// Tightens .sidehub/ to 0700 and every config to 0600, so other users of the machine can't read them (older
    /// versions wrote them with the umask, usually 0644, with the token inline). Returns one warning per file corrected.
    /// </summary>
    public static List<string> RestrictPermissions(string baseDirectory, IEnumerable<AgentConfig> configs)
    {
        var warnings = new List<string>();
        var configDir = Path.Combine(baseDirectory, ConfigFolder);
        try
        {
            if (PrivateFiles.IsExposed(configDir))
                warnings.Add($"{configDir} was accessible to other users, restricted to its owner (0700)");
            PrivateFiles.CreateDirectory(configDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"couldn't restrict {configDir} to its owner: {ex.Message}");
        }

        foreach (var path in configs.Select(c => c.ConfigFilePath))
        {
            if (path is null || !PrivateFiles.IsExposed(path)) continue;
            try
            {
                PrivateFiles.RestrictFile(path);
                warnings.Add($"{path} was readable by other users, restricted to its owner (0600)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{path} is readable by other users (chmod 600 it): {ex.Message}");
            }
        }
        return warnings;
    }

    /// <param name="tokens">Where the token is read from (and moved to): <see cref="AgentTokenStore.ForCurrentUser"/> by default.</param>
    public static AgentConfig Load(string path, AgentTokenStore? tokens = null, Action<string>? warn = null)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file not found: {path}");
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AgentConfig>(json);

        if (config == null)
        {
            throw new InvalidOperationException($"Failed to parse {Path.GetFileName(path)}");
        }

        config.ConfigFilePath = path;
        if (!string.IsNullOrWhiteSpace(config.AgentId))
        {
            tokens ??= AgentTokenStore.ForCurrentUser();
            if (!string.IsNullOrWhiteSpace(config.AgentToken))
                MoveTokenToStore(path, config.AgentId, config.AgentToken, tokens, warn);
            else
                config.AgentToken = tokens.Read(config.AgentId);
            config._tokenPath = tokens.PathFor(config.AgentId);
        }
        config.Validate();
        return config;
    }

    /// <summary>
    /// A config written before the token store: the token goes to the store, then out of the file. When the file
    /// can't be rewritten, the agent still runs with the token it read, and says so.
    /// </summary>
    private static void MoveTokenToStore(string path, string agentId, string token, AgentTokenStore tokens, Action<string>? warn)
    {
        tokens.Save(agentId, token);
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json.Remove("agentToken");
            PrivateFiles.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            warn?.Invoke($"Moved the agent token out of {path}, to {tokens.PathFor(agentId)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"The agent token was copied to {tokens.PathFor(agentId)} but couldn't be removed from {path} ({ex.Message}): delete its \"agentToken\" line");
        }
    }

    public void Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(SidehubUrl))
        {
            errors.Add("sidehubUrl is required");
        }
        else if (!Uri.TryCreate(SidehubUrl, UriKind.Absolute, out var uri)
                 || (uri.Scheme != "wss" && uri.Scheme != "ws"))
        {
            errors.Add("sidehubUrl must be a valid WebSocket URL (wss:// or ws://)");
        }
        else if (uri.Scheme == "ws"
                 && uri.Host != "localhost"
                 && uri.Host != "127.0.0.1"
                 && uri.Host != "::1")
        {
            errors.Add(
                "sidehubUrl uses ws:// (unencrypted) with a remote host. " +
                "Use wss:// to protect the agent token in transit. " +
                "ws:// is only allowed for localhost development.");
        }

        if (string.IsNullOrWhiteSpace(AgentId))
            errors.Add("agentId is required");

        if (string.IsNullOrWhiteSpace(WorkspaceId))
            errors.Add("workspaceId is required");

        // repositoryId is optional (agents are now at workspace level)

        if (string.IsNullOrWhiteSpace(AgentToken))
            errors.Add(_tokenPath is null
                ? "agentToken is required"
                : $"no agent token in {_tokenPath}: run `sidehub-agent setup` again with the command copied from SideHub");

        if (string.IsNullOrWhiteSpace(WorkingDirectory))
            errors.Add("workingDirectory is required");

        if (Capabilities == null || Capabilities.Length == 0)
            errors.Add("capabilities is required and must not be empty");

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid configuration:\n- {string.Join("\n- ", errors)}"
            );
        }
    }

    public string GetAbsoluteWorkingDirectory(string basePath)
    {
        if (Path.IsPathRooted(WorkingDirectory!))
        {
            return WorkingDirectory!;
        }
        return Path.GetFullPath(Path.Combine(basePath, WorkingDirectory!));
    }

    public string GetDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(Name))
            return Name;

        if (!string.IsNullOrWhiteSpace(ConfigFilePath))
            return Path.GetFileNameWithoutExtension(ConfigFilePath);

        return AgentId ?? "unknown";
    }
}

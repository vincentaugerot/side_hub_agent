using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHub.Agent.Update;

/// <summary>States reported to the backend in <c>agent.update-status</c>.</summary>
public static class UpdateStates
{
    public const string Downloading = "downloading";
    public const string WaitingIdle = "waiting-idle";
    public const string Applying = "applying";
    public const string Succeeded = "succeeded";
    public const string RolledBack = "rolled-back";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

/// <summary>Why an update could not run, reported as <c>error</c> (and <c>selfUpdate.reason</c> for the first three).</summary>
public static class UpdateErrors
{
    public const string NotInstalled = "not-installed";
    public const string NotWritable = "not-writable";
    public const string UnsupportedPlatform = "unsupported-platform";
    public const string InvalidRequest = "invalid-request";
    public const string DownloadFailed = "download-failed";
    public const string VerificationFailed = "verification-failed";
    public const string StagingFailed = "staging-failed";
    public const string LaunchFailed = "launch-failed";
    public const string StopFailed = "stop-failed";
    public const string SwapFailed = "swap-failed";
    public const string Unhealthy = "unhealthy";
}

public sealed class UpdateException(string error, string detail) : Exception(detail)
{
    public string Error { get; } = error;
}

public static class UpdateModes
{
    /// <summary>Wait until no agent of the machine is working (default).</summary>
    public const string WhenIdle = "when-idle";
    public const string Now = "now";
}

/// <summary><c>~/.sidehub/update/pending.json</c>: an update staged and waiting to be applied.</summary>
public sealed record PendingUpdate(
    string RequestId,
    string Version,
    string InstallDirectory,
    string StagingDirectory,
    string Mode,
    DateTime RequestedAt,
    int RequestedByPid);

/// <summary><c>~/.sidehub/update/state.json</c>: what the updater is doing, then how it ended.</summary>
public sealed record UpdateOutcome(string RequestId, string Version, string State, string? Error, DateTime At, string? Step = null)
{
    [JsonIgnore]
    public bool IsFinished => State is UpdateStates.Succeeded or UpdateStates.RolledBack or UpdateStates.Failed;
}

/// <summary>
/// <c>&lt;project&gt;/.sidehub/run/activity.json</c>, written by each daemon while an update is pending. <c>Tasks</c>: the
/// tasks still open in its terminals (absent from an agent older than 1.0.132).
/// </summary>
public sealed record InstanceActivity(int Pid, bool Busy, IReadOnlyList<string> Reasons, DateTime At, IReadOnlyList<OpenTask>? Tasks = null);

/// <summary>A task whose terminal has not called <c>sidehub-cli task done</c> yet; <c>InTurn</c>: its CLI is working.</summary>
public sealed record OpenTask(string TaskId, bool InTurn);

/// <summary><c>&lt;project&gt;/.sidehub/run/health.json</c>, written by each daemon once its startup checks ran: the
/// updater waits for it to decide whether the new version works.</summary>
public sealed record InstanceHealth(int Pid, string Version, bool Ok, string? Problem, DateTime At);

/// <summary>Small JSON files shared by the daemons of a machine and the updater; written atomically (temp + rename).</summary>
public static class UpdateFiles
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string UpdateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sidehub", "update");

    public static string PendingPath(string updateDirectory) => Path.Combine(updateDirectory, "pending.json");
    public static string OutcomePath(string updateDirectory) => Path.Combine(updateDirectory, "state.json");
    public static string LockPath(string updateDirectory) => Path.Combine(updateDirectory, "apply.lock");
    public static string LogPath(string updateDirectory) => Path.Combine(updateDirectory, "update.log");

    /// <summary>
    /// Where the updater runs from: a copy of the staged binary. A single-file binary loads its embedded assemblies from
    /// its own path, and the updater renames the staging folder: run from there, it would fail half way.
    /// </summary>
    public static string UpdaterPath(string updateDirectory) => Path.Combine(updateDirectory, "updater", "sidehub-agent");

    public static string ActivityPath(string projectDirectory) => Path.Combine(projectDirectory, ".sidehub", "run", "activity.json");
    public static string HealthPath(string projectDirectory) => Path.Combine(projectDirectory, ".sidehub", "run", "health.json");

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        PrivateFiles.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        PrivateFiles.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

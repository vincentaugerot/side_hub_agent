using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SideHub.Agent.Models;

namespace SideHub.Agent.Update;

/// <summary>Whether this agent can update itself from SideHub, reported in <c>agent.connected.selfUpdate</c>.</summary>
public sealed record SelfUpdateSupport(bool Supported, string? Reason, string? InstallDirectory)
{
    public SelfUpdateInfo Message() => new() { Supported = Supported, Reason = Reason };
}

/// <summary>
/// Where the agent is installed and whether it can replace its own installation (see doc
/// <c>54-mise-a-jour-agent-en-un-clic.md</c> in side_hub). The release comes from a repository compiled in here,
/// never from the backend.
/// </summary>
public static class SelfUpdate
{
    public const string GitHubRepository = "vincentaugerot/side_hub_agent";

    /// <summary>Written by install.sh / install.ps1 in every install folder.</summary>
    public const string InstallMarker = ".sidehub-agent-install";

    private static readonly Lazy<SelfUpdateSupport> Current = new(() => Detect(AppContext.BaseDirectory, OperatingSystem.IsWindows(), CanWrite));

    public static SelfUpdateSupport Support => Current.Value;

    public static SelfUpdateSupport Detect(string executableDirectory, bool isWindows, Func<string, bool> canWrite)
    {
        var installDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
        if (!File.Exists(Path.Combine(installDirectory, InstallMarker)))
            return new SelfUpdateSupport(false, UpdateErrors.NotInstalled, null);
        // Phase 1: the running exe of a Windows agent cannot be replaced in place (see the doc, phase 2)
        if (isWindows)
            return new SelfUpdateSupport(false, UpdateErrors.UnsupportedPlatform, installDirectory);
        // The swap renames the folder: its parent must be writable too
        if (!canWrite(installDirectory) || Path.GetDirectoryName(installDirectory) is not { } parent || !canWrite(parent))
            return new SelfUpdateSupport(false, UpdateErrors.NotWritable, installDirectory);
        return new SelfUpdateSupport(true, null, installDirectory);
    }

    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, $".sidehub-write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>The release asset of this platform, as named by the release workflow.</summary>
    public static string AssetName()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return OperatingSystem.IsWindows() ? $"sidehub-agent-{os}-{Arch}.zip" : $"sidehub-agent-{os}-{Arch}.tar.gz";
    }

    /// <summary>
    /// The same for every agent sharing this installation and this user (the agents one update restarts), different
    /// across machines even with the same host name and paths: a random id kept in <c>~/.sidehub/machine-id</c>,
    /// hashed with the install folder.
    /// </summary>
    public static string? InstallId(string? installDirectory, string? sidehubDirectory = null)
    {
        if (installDirectory is null)
            return null;
        try
        {
            var directory = sidehubDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sidehub");
            var path = Path.Combine(directory, "machine-id");
            var machineId = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            if (machineId.Length < 16)
            {
                machineId = Guid.NewGuid().ToString("N");
                PrivateFiles.CreateDirectory(directory);
                PrivateFiles.WriteAllText(path, machineId);
            }
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{machineId}\n{installDirectory}")))[..32];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

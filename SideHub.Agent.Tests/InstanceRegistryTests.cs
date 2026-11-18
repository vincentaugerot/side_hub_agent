using System.Text.Json;
using SideHub.Agent;

namespace SideHub.Agent.Tests;

/// <summary>
/// A grouped restart (an update, a reboot, <c>start --all</c>) has every daemon register itself in the same second:
/// no write may be lost, no reader may see a half written registry, and an unreadable registry must not be
/// overwritten with a single entry (the other agents would be forgotten by the next update and the boot entry).
/// </summary>
public class InstanceRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-registry-").FullName;
    private string RegistryPath => Path.Combine(_dir, ".sidehub", "instances.json");
    private string Project(int i) => Path.Combine(_dir, "projects", $"p{i}");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Parallel_registrations_keep_every_entry_and_readers_never_see_a_partial_file()
    {
        InstanceRegistry.Register(Project(-1), RegistryPath);
        var unparsable = 0;
        using var stop = new CancellationTokenSource();
        using var reading = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var _ = JsonDocument.Parse(File.ReadAllText(RegistryPath));
                    reading.Set();
                }
                catch (JsonException)
                {
                    unparsable++;
                }
            }
        });

        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 },
            i => InstanceRegistry.Register(Project(i), RegistryPath));
        stop.Cancel();
        reader.Wait();

        var directories = InstanceRegistry.LoadAll(RegistryPath).Select(e => e.Directory).ToHashSet();
        Assert.Equal(21, directories.Count);
        Assert.All(Enumerable.Range(0, 20), i => Assert.Contains(Project(i), directories));
        Assert.Equal(0, unparsable);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(RegistryPath)!, "*.tmp"));
    }

    [Fact]
    public void Registering_again_updates_the_entry_instead_of_adding_one()
    {
        InstanceRegistry.Register(Project(1), RegistryPath);
        var first = InstanceRegistry.LoadAll(RegistryPath).Single().RegisteredAt;

        InstanceRegistry.Register(Project(1), RegistryPath);

        var entry = Assert.Single(InstanceRegistry.LoadAll(RegistryPath));
        Assert.True(entry.RegisteredAt >= first);
    }

    [Fact]
    public void An_unreadable_registry_is_kept_aside_rather_than_erased()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        const string truncated = "[\n  {\n    \"directory\": \"/root/Github/side_hub\",\n    \"regist";
        File.WriteAllText(RegistryPath, truncated);

        InstanceRegistry.Register(Project(1), RegistryPath);

        Assert.Equal(truncated, File.ReadAllText(RegistryPath + ".corrupt"));
        var entry = Assert.Single(InstanceRegistry.LoadAll(RegistryPath));
        Assert.Equal(Project(1), entry.Directory);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(PrivateFiles.PrivateFileMode, File.GetUnixFileMode(RegistryPath + ".corrupt"));
    }

    [Fact]
    public void Unregister_removes_only_that_project()
    {
        InstanceRegistry.Register(Project(1), RegistryPath);
        InstanceRegistry.Register(Project(2), RegistryPath);

        InstanceRegistry.Unregister(Project(1), RegistryPath);

        Assert.Equal(Project(2), Assert.Single(InstanceRegistry.LoadAll(RegistryPath)).Directory);
    }

    [Fact]
    public void The_registry_and_its_lock_are_private()
    {
        if (OperatingSystem.IsWindows()) return;

        InstanceRegistry.Register(Project(1), RegistryPath);

        Assert.Equal(PrivateFiles.PrivateFileMode, File.GetUnixFileMode(RegistryPath));
        Assert.Equal(PrivateFiles.PrivateFileMode,
            File.GetUnixFileMode(Path.Combine(Path.GetDirectoryName(RegistryPath)!, "instances.lock")));
    }
}

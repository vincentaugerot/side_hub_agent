using SideHub.Agent.Models;
using SideHub.Agent.Update;

namespace SideHub.Agent.Tests.Update;

/// <summary>
/// The daemons of a machine agree, through files, on when the updater may run: when none of them is busy, or at
/// once when asked "now".
/// </summary>
public class UpdateCoordinatorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeClient _client = new();
    private readonly List<PendingUpdate> _launched = [];
    private readonly List<AgentInstance> _instances = [];
    private readonly List<string> _logs = [];
    private readonly string _install;
    private readonly string _project;
    private readonly string _updates;
    private bool _launchSucceeds = true;

    public UpdateCoordinatorTests()
    {
        _install = _temp.Combine("sidehub-agent");
        _project = _temp.Combine("project");
        _updates = _temp.Combine("home", ".sidehub", "update");
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(_project);
        _instances.Add(new AgentInstance(_project, Environment.ProcessId, Supervised: false));
    }

    private UpdateCoordinator Coordinator(SelfUpdateSupport? support = null)
    {
        var coordinator = new UpdateCoordinator(_project, _logs.Add, support ?? new SelfUpdateSupport(true, null, _install), _updates,
            stage: (install, version, _, _) =>
            {
                var staging = UpdateStager.StagingDirectory(install, version);
                Directory.CreateDirectory(staging);
                return Task.FromResult(staging);
            },
            launchUpdater: (pending, _) =>
            {
                _launched.Add(pending);
                return _launchSucceeds;
            },
            runningInstances: _ => _instances,
            currentVersion: "1.0.90");
        coordinator.Attach(_client);
        return coordinator;
    }

    private PendingUpdate? Pending => UpdateFiles.Read<PendingUpdate>(UpdateFiles.PendingPath(_updates));

    [Fact]
    public async Task An_idle_machine_is_updated_at_once()
    {
        using var coordinator = Coordinator();

        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", null, CancellationToken.None);

        Assert.Equal([UpdateStates.Downloading, UpdateStates.Applying], _client.States);
        var launched = Assert.Single(_launched);
        Assert.Equal("1.0.91", launched.Version);
        Assert.Equal(UpdateModes.WhenIdle, launched.Mode);
        Assert.True(coordinator.IsApplying);
        Assert.Contains("Machine idle: launching the updater to 1.0.91", _logs);
    }

    [Fact]
    public async Task A_busy_agent_holds_the_update_until_it_is_idle()
    {
        _client.Busy = ["run"];
        using var coordinator = Coordinator();

        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", null, CancellationToken.None);

        Assert.Empty(_launched);
        Assert.Equal(UpdateStates.WaitingIdle, _client.States[^1]);
        Assert.Equal(["run"], _client.Sent[^1].BusyReasons);
        Assert.True(UpdateFiles.Read<InstanceActivity>(UpdateFiles.ActivityPath(_project))!.Busy);

        _client.Busy = [];
        await coordinator.TickAsync();

        Assert.Single(_launched);
    }

    [Fact]
    public async Task The_same_waiting_reasons_are_reported_once()
    {
        _client.Busy = ["cli-working"];
        using var coordinator = Coordinator();
        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", null, CancellationToken.None);

        await coordinator.TickAsync();
        await coordinator.TickAsync();

        Assert.Single(_client.States, state => state == UpdateStates.WaitingIdle);
    }

    [Fact]
    public async Task Another_daemon_busy_or_silent_holds_the_update_and_now_overrides_it()
    {
        var other = _temp.Combine("other-project");
        _instances.Add(new AgentInstance(other, 424242, Supervised: true));
        using var coordinator = Coordinator();

        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", null, CancellationToken.None);
        Assert.Equal([UpdateCoordinator.UnresponsiveReason], _client.Sent[^1].BusyReasons);

        UpdateFiles.Write(UpdateFiles.ActivityPath(other), new InstanceActivity(424242, true, ["terminal-activity"], DateTime.UtcNow));
        await coordinator.TickAsync();
        Assert.Equal(["terminal-activity"], _client.Sent[^1].BusyReasons);
        Assert.Empty(_launched);

        await coordinator.NowAsync("r1");

        Assert.Single(_launched);
    }

    [Fact]
    public async Task An_idle_report_from_another_daemon_counts()
    {
        var other = _temp.Combine("other-project");
        _instances.Add(new AgentInstance(other, 424242, Supervised: false));
        UpdateFiles.Write(UpdateFiles.ActivityPath(other), new InstanceActivity(424242, false, [], DateTime.UtcNow));
        using var coordinator = Coordinator();

        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", null, CancellationToken.None);

        Assert.Single(_launched);
    }

    [Fact]
    public async Task Mode_now_does_not_wait_and_says_what_it_cuts()
    {
        _client.Busy = ["queued-task", "run"];
        using var coordinator = Coordinator();

        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", UpdateModes.Now, CancellationToken.None);

        Assert.Single(_launched);
        Assert.Contains("Update forced (machine busy: queued-task, run): launching the updater to 1.0.91", _logs);
        Assert.DoesNotContain(_logs, line => line.StartsWith("Machine idle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_forgets_the_staged_release()
    {
        _client.Busy = ["run"];
        using var coordinator = Coordinator();
        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", null, CancellationToken.None);
        var staging = Pending!.StagingDirectory;

        await coordinator.CancelAsync(_client, "r1", CancellationToken.None);

        Assert.Equal(UpdateStates.Canceled, _client.States[^1]);
        Assert.Null(Pending);
        Assert.False(Directory.Exists(staging));
        Assert.False(File.Exists(UpdateFiles.ActivityPath(_project)));
    }

    [Fact]
    public async Task A_second_request_for_the_same_version_adopts_the_staged_one()
    {
        _client.Busy = ["run"];
        using var coordinator = Coordinator();
        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", null, CancellationToken.None);

        await coordinator.RequestAsync(_client, "r2", "1.0.91", "v1.0.91", null, CancellationToken.None);

        Assert.Single(_client.States, s => s == UpdateStates.Downloading);
        Assert.Equal("r2", Pending!.RequestId);
        Assert.Equal("r2", _client.Sent[^1].RequestId);
    }

    [Theory]
    [InlineData("1.0.90")]
    [InlineData("1.0.89")]
    [InlineData("latest")]
    public async Task Only_a_newer_version_is_installed(string version)
    {
        using var coordinator = Coordinator();

        await coordinator.RequestAsync(_client, "r1", version, $"v{version}", null, CancellationToken.None);

        Assert.Equal(UpdateErrors.InvalidRequest, Assert.Single(_client.Sent).Error);
        Assert.Null(Pending);
    }

    [Fact]
    public async Task An_agent_that_cannot_update_itself_says_why()
    {
        using var coordinator = Coordinator(new SelfUpdateSupport(false, UpdateErrors.NotWritable, _install));

        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", null, CancellationToken.None);

        var sent = Assert.Single(_client.Sent);
        Assert.Equal(UpdateStates.Failed, sent.State);
        Assert.Equal(UpdateErrors.NotWritable, sent.Error);
    }

    [Fact]
    public async Task An_updater_that_does_not_start_fails_the_update_and_frees_the_machine()
    {
        _launchSucceeds = false;
        using var coordinator = Coordinator();

        await coordinator.RequestAsync(_client, "r1", "1.0.91", "v1.0.91", null, CancellationToken.None);

        Assert.Equal(UpdateErrors.LaunchFailed, _client.Sent[^1].Error);
        Assert.False(coordinator.IsApplying);
        Assert.Null(Pending);
    }

    [Fact]
    public async Task The_outcome_is_reported_once_for_the_machine()
    {
        UpdateFiles.Write(UpdateFiles.OutcomePath(_updates),
            new UpdateOutcome("r1", "1.0.91", UpdateStates.RolledBack, UpdateErrors.Unhealthy, DateTime.UtcNow, "project: pty-helper failed"));
        using var coordinator = Coordinator();

        await coordinator.OnConnectedAsync();
        await coordinator.OnConnectedAsync();

        var sent = Assert.Single(_client.Sent);
        Assert.Equal(UpdateStates.RolledBack, sent.State);
        Assert.Equal("project: pty-helper failed", sent.Detail);
        Assert.False(File.Exists(UpdateFiles.OutcomePath(_updates)));
    }

    [Fact]
    public async Task An_outcome_waits_for_a_connection()
    {
        UpdateFiles.Write(UpdateFiles.OutcomePath(_updates), new UpdateOutcome("r1", "1.0.91", UpdateStates.Succeeded, null, DateTime.UtcNow));
        _client.IsConnected = false;
        using var coordinator = Coordinator();

        await coordinator.TickAsync();

        Assert.Empty(_client.Sent);
        Assert.True(File.Exists(UpdateFiles.OutcomePath(_updates)));
    }

    [Fact]
    public void The_updater_runs_from_a_copy_of_the_staged_binary()
    {
        var staging = UpdateStager.StagingDirectory(_install, "1.0.91");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "sidehub-agent"), "new");
        var pending = new PendingUpdate("r1", "1.0.91", _install, staging, UpdateModes.Now, DateTime.UtcNow, 1);
        File.WriteAllText(Path.Combine(_install, "sidehub-agent"), "old");
        UpdateCoordinator.CopyUpdater(pending with { StagingDirectory = _install }, _updates); // a previous update's

        var updater = UpdateCoordinator.CopyUpdater(pending, _updates);

        Assert.Equal(UpdateFiles.UpdaterPath(_updates), updater);
        Assert.Equal("new", File.ReadAllText(updater));
        Assert.False(updater.StartsWith(staging, StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(updater).HasFlag(UnixFileMode.UserExecute));
    }

    public void Dispose() => _temp.Dispose();

    private sealed class FakeClient : IUpdateClient
    {
        public bool IsConnected { get; set; } = true;
        public List<string> Busy { get; set; } = [];
        public List<AgentUpdateStatusMessage> Sent { get; } = [];
        public List<string> States => Sent.Select(m => m.State).ToList();

        public IReadOnlyList<string> BusyReasons() => Busy;

        public Task<bool> SendUpdateStatusAsync(AgentUpdateStatusMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(IsConnected);
        }
    }
}

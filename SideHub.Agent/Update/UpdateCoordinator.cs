using System.Collections.Concurrent;
using System.Diagnostics;
using SideHub.Agent.Models;

namespace SideHub.Agent.Update;

/// <summary>An agent connection of this process: says what keeps it busy and reports the update's progress.</summary>
public interface IUpdateClient
{
    bool IsConnected { get; }

    /// <summary>Why updating now would cut work in progress ("run", "cli-working"…); empty when idle.</summary>
    IReadOnlyList<string> BusyReasons();

    /// <summary>The tasks still open in its terminals, and whether their CLI is in the middle of a turn.</summary>
    IReadOnlyList<OpenTask> OpenTasks();

    Task<bool> SendUpdateStatusAsync(AgentUpdateStatusMessage message, CancellationToken ct);
}

/// <summary>
/// One per daemon process (shared by its agent connections). Receives <c>agent.update</c>, stages the release, then
/// waits with the other daemons of the machine until none is busy and launches the updater
/// (<c>sidehub-agent update apply</c>, see <see cref="UpdateApplier"/>). The daemons coordinate through files in
/// <c>~/.sidehub/update/</c>, without the backend: whichever daemon first sees the whole machine idle launches the
/// updater, guarded by <c>apply.lock</c>. "Now" does not wait for an idle machine, only for every daemon to report what
/// it cuts (<see cref="ReportGrace"/>) and for the CLIs of queued tasks to end their turn (<see cref="TurnGrace"/>).
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    /// <summary>The pace of the ticks while "now" holds: the end of a turn is seen within seconds.</summary>
    public static readonly TimeSpan HoldTickInterval = TimeSpan.FromSeconds(3);
    /// <summary>
    /// "Now" waits this long for the other daemons to write their activity: they see the pending update at their next
    /// tick (<see cref="TickInterval"/>), and until then their work would read as <see cref="UnresponsiveReason"/>.
    /// </summary>
    public static readonly TimeSpan ReportGrace = TimeSpan.FromSeconds(20);
    /// <summary>
    /// "Now" waits this long for the CLIs of queued tasks to be between two turns: a turn cut by the restart is lost, its
    /// task is resumed with a new prompt.
    /// </summary>
    public static readonly TimeSpan TurnGrace = TimeSpan.FromMinutes(2);
    /// <summary>An activity file older than this says nothing: its daemon stopped ticking.</summary>
    private static readonly TimeSpan ActivityFreshness = TimeSpan.FromSeconds(45);
    /// <summary>A lock whose process is gone for this long means the updater died.</summary>
    private static readonly TimeSpan StaleLockAge = TimeSpan.FromMinutes(2);

    public const string UnresponsiveReason = "unresponsive-agent";

    private readonly string _projectDirectory;
    private readonly string _updateDirectory;
    private readonly Action<string> _log;
    private readonly Func<string, string, string, CancellationToken, Task<string>> _stage;
    private readonly Func<PendingUpdate, string, bool> _launchUpdater;
    private readonly Func<string, List<AgentInstance>> _runningInstances;
    private readonly string _currentVersion;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<IUpdateClient, byte> _clients = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private string? _lastWaitingReport;
    // When this daemon first saw the pending request in "now" mode: the start of its holds
    private (string RequestId, DateTime Since)? _forcedSince;
    private string? _lastHold;
    private bool _holdPace;
    private CancellationToken _ct;

    public SelfUpdateSupport Support { get; }

    public UpdateCoordinator(string projectDirectory, Action<string> log, SelfUpdateSupport? support = null,
        string? updateDirectory = null, Func<string, string, string, CancellationToken, Task<string>>? stage = null,
        Func<PendingUpdate, string, bool>? launchUpdater = null, Func<string, List<AgentInstance>>? runningInstances = null,
        string? currentVersion = null, Func<DateTime>? utcNow = null)
    {
        _projectDirectory = Path.GetFullPath(projectDirectory);
        _log = log;
        Support = support ?? SelfUpdate.Support;
        _updateDirectory = updateDirectory ?? UpdateFiles.UpdateDirectory;
        _stage = stage ?? ((installDirectory, version, tag, ct) => new UpdateStager().StageAsync(installDirectory, version, tag, ct));
        _launchUpdater = launchUpdater ?? LaunchUpdater;
        _runningInstances = runningInstances ?? AgentInstances.Running;
        _currentVersion = currentVersion ?? VersionInfo.AgentVersion;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    private string PendingPath => UpdateFiles.PendingPath(_updateDirectory);
    private string LockPath => UpdateFiles.LockPath(_updateDirectory);
    private string OutcomePath => UpdateFiles.OutcomePath(_updateDirectory);

    /// <summary>The updater is running: the agents are about to stop, nothing new should start.</summary>
    public bool IsApplying => File.Exists(LockPath);

    public void Attach(IUpdateClient client) => _clients[client] = 0;

    public void Detach(IUpdateClient client) => _clients.TryRemove(client, out _);

    public void Start(CancellationToken ct)
    {
        _ct = ct;
        _timer ??= new Timer(_ => _ = TickSafeAsync(), null, TickInterval, TickInterval);
        _ = WriteHealthAsync(ct);
    }

    /// <summary><c>agent.update</c>: stage the release, then apply it when the machine is idle (or now).</summary>
    public async Task RequestAsync(IUpdateClient from, string? requestId, string? version, string? tag, string? mode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(version))
            return;
        mode = mode == UpdateModes.Now ? UpdateModes.Now : UpdateModes.WhenIdle;
        Task Report(string state, string? error = null, string? detail = null) =>
            from.SendUpdateStatusAsync(new AgentUpdateStatusMessage { RequestId = requestId, Version = version, State = state, Error = error, Detail = detail }, ct);

        if (!Support.Supported || Support.InstallDirectory is not { } installDirectory)
        {
            await Report(UpdateStates.Failed, Support.Reason);
            return;
        }
        if (!Version.TryParse(version, out var target) || !Version.TryParse(_currentVersion, out var current) || target <= current)
        {
            await Report(UpdateStates.Failed, UpdateErrors.InvalidRequest, $"{version} is not newer than {_currentVersion}");
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (UpdateFiles.Read<PendingUpdate>(PendingPath) is { } pending)
            {
                // Asked again (a retry, another device): adopt the new request instead of staging twice
                if (pending.Version == version || IsApplying)
                {
                    var adopted = pending with
                    {
                        RequestId = requestId, Mode = mode == UpdateModes.Now ? mode : pending.Mode, RequestedByPid = Environment.ProcessId,
                    };
                    UpdateFiles.Write(PendingPath, adopted);
                    _lastWaitingReport = null;
                    if (IsApplying)
                    {
                        await Report(UpdateStates.Applying);
                        return;
                    }
                }
                else
                {
                    UpdateStager.DeleteDirectory(pending.StagingDirectory);
                    UpdateFiles.Delete(PendingPath);
                }
            }

            if (UpdateFiles.Read<PendingUpdate>(PendingPath) is null)
            {
                _log($"Update to {version} requested ({requestId}, {mode}): downloading");
                await Report(UpdateStates.Downloading);
                string staging;
                try
                {
                    staging = await _stage(installDirectory, version, tag ?? $"v{version}", ct);
                }
                catch (UpdateException ex)
                {
                    _log($"Update to {version} failed ({ex.Error}): {ex.Message}");
                    await Report(UpdateStates.Failed, ex.Error, ex.Message);
                    return;
                }
                UpdateFiles.Delete(OutcomePath);
                UpdateFiles.Write(PendingPath, new PendingUpdate(requestId, version, installDirectory, staging, mode,
                    _utcNow(), Environment.ProcessId));
                _lastWaitingReport = null;
                _log($"Update to {version} staged in {staging}");
            }
        }
        finally
        {
            _gate.Release();
        }
        await TickAsync();
    }

    /// <summary><c>agent.update.now</c>: stop waiting for the machine to be idle.</summary>
    public async Task NowAsync(string? requestId)
    {
        await _gate.WaitAsync(_ct);
        try
        {
            if (UpdateFiles.Read<PendingUpdate>(PendingPath) is { } pending && pending.RequestId == requestId && !IsApplying)
                UpdateFiles.Write(PendingPath, pending with { Mode = UpdateModes.Now });
        }
        finally
        {
            _gate.Release();
        }
        await TickAsync();
    }

    /// <summary><c>agent.update.cancel</c>: forget the staged release, unless the updater already started.</summary>
    public async Task CancelAsync(IUpdateClient from, string? requestId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (UpdateFiles.Read<PendingUpdate>(PendingPath) is not { } pending || pending.RequestId != requestId)
                return;
            var state = UpdateStates.Applying;
            if (!IsApplying)
            {
                UpdateFiles.Delete(PendingPath);
                UpdateStager.DeleteDirectory(pending.StagingDirectory);
                UpdateFiles.Delete(UpdateFiles.ActivityPath(_projectDirectory));
                state = UpdateStates.Canceled;
                _log($"Update to {pending.Version} canceled");
            }
            await from.SendUpdateStatusAsync(new AgentUpdateStatusMessage { RequestId = pending.RequestId, Version = pending.Version, State = state }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A connection (re)opened: report how an update ended, if no daemon did yet.</summary>
    public Task OnConnectedAsync() => TickSafeAsync();

    /// <summary>The CLI of a task ended its turn: an update held for it can go now, before the next turn starts.</summary>
    public Task OnTaskTurnEndedAsync() => File.Exists(PendingPath) ? TickSafeAsync() : Task.CompletedTask;

    private async Task TickSafeAsync()
    {
        try { await TickAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log($"Update check failed: {ex.Message}"); }
    }

    public async Task TickAsync()
    {
        if (!await _gate.WaitAsync(0))
            return;
        try
        {
            await ReportOutcomeAsync();
            var pending = UpdateFiles.Read<PendingUpdate>(PendingPath);
            var activityPath = UpdateFiles.ActivityPath(_projectDirectory);
            if (pending is null || pending.InstallDirectory != Support.InstallDirectory)
            {
                UpdateFiles.Delete(activityPath);
                Pace(hold: false);
                return;
            }

            var ownReasons = OwnBusyReasons();
            var ownTasks = OwnTasks();
            UpdateFiles.Write(activityPath, new InstanceActivity(Environment.ProcessId, ownReasons.Count > 0, ownReasons, _utcNow(), ownTasks));
            if (IsApplying)
            {
                Pace(hold: false);
                ClearStaleLock(pending);
                return;
            }

            var machine = MachineActivity(pending.InstallDirectory, ownReasons, ownTasks);
            var busy = machine.Reasons;
            if (busy.Count == 0)
            {
                Pace(hold: false);
                await LaunchAsync(pending, "Machine idle");
                return;
            }
            if (pending.Mode == UpdateModes.Now)
            {
                var hold = ForcedHold(pending, machine);
                Pace(hold is not null);
                if (hold is null)
                {
                    // The log and the update's detail say what it cuts, so a stopped task is not taken for an idle machine
                    var cut = DescribeCut(machine);
                    await LaunchAsync(pending, $"Update forced ({cut})", cut);
                    return;
                }
                if (hold != _lastHold)
                    _log($"Update to {pending.Version} forced: {hold}");
                _lastHold = hold;
            }
            // The daemon that received the request reports what the machine waits for, when it changes
            var report = string.Join(",", busy);
            if (pending.RequestedByPid == Environment.ProcessId && report != _lastWaitingReport
                && await SendAsync(new AgentUpdateStatusMessage { RequestId = pending.RequestId, Version = pending.Version, State = UpdateStates.WaitingIdle, BusyReasons = busy }))
                _lastWaitingReport = report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<string> OwnBusyReasons() =>
        _clients.Keys.SelectMany(client => client.BusyReasons()).Distinct().Order(StringComparer.Ordinal).ToList();

    private List<OpenTask> OwnTasks() => _clients.Keys.SelectMany(client => client.OpenTasks()).ToList();

    private sealed record Machine(List<string> Reasons, List<OpenTask> Tasks, bool Unresponsive);

    /// <summary>What keeps the daemons of this installation busy, from their activity files (ours from memory).</summary>
    private Machine MachineActivity(string installDirectory, List<string> ownReasons, List<OpenTask> ownTasks)
    {
        var reasons = new SortedSet<string>(ownReasons, StringComparer.Ordinal);
        var tasks = new List<OpenTask>(ownTasks);
        var unresponsive = false;
        foreach (var instance in _runningInstances(installDirectory))
        {
            if (instance.Pid == Environment.ProcessId)
                continue;
            var activity = UpdateFiles.Read<InstanceActivity>(UpdateFiles.ActivityPath(instance.Directory));
            if (activity is null || activity.Pid != instance.Pid || _utcNow() - activity.At > ActivityFreshness)
            {
                unresponsive = true; // an older agent, one that stopped ticking, or one that did not see the request yet
                reasons.Add(UnresponsiveReason);
            }
            else
            {
                reasons.UnionWith(activity.Reasons);
                tasks.AddRange(activity.Tasks ?? []);
            }
        }
        return new Machine(reasons.ToList(), tasks, unresponsive);
    }

    /// <summary>What "now" still waits for (null: launch): every daemon's report, then the end of the tasks' turns.</summary>
    private string? ForcedHold(PendingUpdate pending, Machine machine)
    {
        var now = _utcNow();
        if (_forcedSince is not { } forced || forced.RequestId != pending.RequestId)
            _forcedSince = forced = (pending.RequestId, now);
        var waited = now - forced.Since;
        if (machine.Unresponsive && waited < ReportGrace)
            return $"waiting up to {ReportGrace.TotalSeconds:0} s for the other agents to say what they are doing";
        var inTurn = machine.Tasks.Where(t => t.InTurn).Select(t => t.TaskId).Distinct().ToList();
        if (inTurn.Count > 0 && waited < TurnGrace)
            return $"waiting up to {TurnGrace.TotalMinutes:0} min for the turn of task(s) {string.Join(", ", inTurn)} to end";
        return null;
    }

    /// <summary>"machine busy: queued-task, run; tasks cut: a (in turn), b": the reasons, then the tasks restarted.</summary>
    private static string DescribeCut(Machine machine)
    {
        var text = $"machine busy: {string.Join(", ", machine.Reasons)}";
        var tasks = machine.Tasks.GroupBy(t => t.TaskId)
            .Select(g => g.Any(t => t.InTurn) ? $"{g.Key} (in turn)" : g.Key).ToList();
        return tasks.Count == 0 ? text : $"{text}; tasks cut: {string.Join(", ", tasks)}";
    }

    /// <summary>Ticks faster while "now" holds, back to <see cref="TickInterval"/> otherwise.</summary>
    private void Pace(bool hold)
    {
        if (hold == _holdPace)
            return;
        _holdPace = hold;
        if (!hold)
        {
            _forcedSince = null;
            _lastHold = null;
        }
        var interval = hold ? HoldTickInterval : TickInterval;
        try { _timer?.Change(interval, interval); }
        catch (ObjectDisposedException) { }
    }

    private async Task LaunchAsync(PendingUpdate pending, string why, string? detail = null)
    {
        try
        {
            using var lockFile = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(lockFile);
            writer.Write(Environment.ProcessId);
        }
        catch (IOException)
        {
            return; // another daemon got there first
        }

        _log($"{why}: launching the updater to {pending.Version}");
        await SendAsync(new AgentUpdateStatusMessage { RequestId = pending.RequestId, Version = pending.Version, State = UpdateStates.Applying, Detail = detail });
        if (_launchUpdater(pending, _projectDirectory))
            return;

        _log("The updater did not start");
        UpdateFiles.Delete(LockPath);
        UpdateFiles.Delete(PendingPath);
        UpdateStager.DeleteDirectory(pending.StagingDirectory);
        await SendAsync(new AgentUpdateStatusMessage
        {
            RequestId = pending.RequestId, Version = pending.Version, State = UpdateStates.Failed, Error = UpdateErrors.LaunchFailed,
        });
    }

    /// <summary>The updater died (lock left behind, its process gone): give up so a new request can run.</summary>
    private void ClearStaleLock(PendingUpdate pending)
    {
        var info = new FileInfo(LockPath);
        if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc < StaleLockAge)
            return;
        var text = File.ReadAllText(LockPath).Trim();
        if (int.TryParse(text, out var pid) && AgentInstances.IsAlive(pid))
            return;
        _log("The updater stopped before the end: update abandoned");
        if (UpdateFiles.Read<UpdateOutcome>(OutcomePath) is not { IsFinished: true })
            UpdateFiles.Write(OutcomePath, new UpdateOutcome(pending.RequestId, pending.Version, UpdateStates.Failed,
                UpdateErrors.LaunchFailed, DateTime.UtcNow, "the updater stopped before the end"));
        UpdateFiles.Delete(PendingPath);
        UpdateFiles.Delete(LockPath);
    }

    /// <summary>
    /// Reports how the last update ended (written by the updater) through one connection, once for the machine: the
    /// first daemon to rename <c>state.json</c> owns the report.
    /// </summary>
    private async Task ReportOutcomeAsync()
    {
        if (UpdateFiles.Read<UpdateOutcome>(OutcomePath) is not { IsFinished: true } outcome || !_clients.Keys.Any(c => c.IsConnected))
            return;
        var claimed = $"{OutcomePath}.{Environment.ProcessId}.reporting";
        try { File.Move(OutcomePath, claimed); }
        catch (IOException) { return; }

        var sent = await SendAsync(new AgentUpdateStatusMessage
        {
            RequestId = outcome.RequestId, Version = outcome.Version, State = outcome.State, Error = outcome.Error, Detail = outcome.Step,
        });
        if (sent)
            UpdateFiles.Delete(claimed);
        else
            try { File.Move(claimed, OutcomePath, overwrite: false); } catch (IOException) { /* a newer outcome won */ }
    }

    private async Task<bool> SendAsync(AgentUpdateStatusMessage message)
    {
        foreach (var client in _clients.Keys.Where(c => c.IsConnected))
        {
            if (await client.SendUpdateStatusAsync(message, _ct))
                return true;
        }
        return false;
    }

    /// <summary>Tells the updater whether this daemon works once started (its startup checks, pty-helper first).</summary>
    private async Task WriteHealthAsync(CancellationToken ct)
    {
        try
        {
            var problems = await StartupChecks.RunOnceAsync(ct);
            var helper = problems.FirstOrDefault(p => p.Reason == DiagnosticReasons.PtyHelperFailed);
            UpdateFiles.Write(UpdateFiles.HealthPath(_projectDirectory),
                new InstanceHealth(Environment.ProcessId, VersionInfo.AgentVersion, helper is null, helper?.Detail, DateTime.UtcNow));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Health file not written: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the staged binary as the updater, detached from this daemon: the updater stops it. Under a systemd user
    /// service, a transient unit (outside the service's cgroup); otherwise a background job of a shell that exits at
    /// once (the updater is no longer our child, and it leaves our session itself).
    /// </summary>
    private static bool LaunchUpdater(PendingUpdate pending, string projectDirectory)
    {
        string updater;
        try
        {
            updater = CopyUpdater(pending, UpdateFiles.UpdateDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var command = new List<string> { updater, "update", "apply" };
        if (RootPolicy.IsCurrentUserRoot())
            command.Add(RootPolicy.AllowFlag);

        if (OperatingSystem.IsLinux() && AgentService.IsSupervised(projectDirectory) && ExecutableResolver.Resolve("systemd-run") is not null)
        {
            // A transient unit starts with the user manager's environment: the daemons it restarts need ours
            var environment = new[] { "PATH", "HOME", "LANG" }
                .Select(name => (name, value: Environment.GetEnvironmentVariable(name)))
                .Where(v => !string.IsNullOrEmpty(v.value))
                .Select(v => new KeyValuePair<string, string>(v.name, v.value!));
            return AgentService.RunTransient($"sidehub-agent-update-{DateTime.UtcNow:yyyyMMddHHmmss}", command, environment);
        }

        var psi = new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "\"$0\" \"$@\" </dev/null >/dev/null 2>&1 &" },
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(pending.InstallDirectory)!,
        };
        foreach (var argument in command)
            psi.ArgumentList.Add(argument);
        psi.Environment.Remove(AgentSetup.TokenEnvVar);
        try
        {
            using var shell = Process.Start(psi);
            return shell is not null && shell.WaitForExit(15_000) && shell.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the staged binary to <see cref="UpdateFiles.UpdaterPath"/> (temp file then rename: a previous updater
    /// may still be mapped from there).
    /// </summary>
    public static string CopyUpdater(PendingUpdate pending, string updateDirectory)
    {
        var path = UpdateFiles.UpdaterPath(updateDirectory);
        PrivateFiles.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.Copy(Path.Combine(pending.StagingDirectory, "sidehub-agent"), temp, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

using System.Diagnostics;

namespace SideHub.Agent.Update;

/// <summary>
/// <c>sidehub-agent update apply</c>, run from the staged release, detached from the daemons (see
/// <see cref="UpdateCoordinator"/>): stops every daemon of the installation, swaps the install folder, starts them
/// again the way they ran (service or daemon) and rolls back if one does not come up healthy. Logs to
/// <c>~/.sidehub/update/update.log</c>; the outcome goes to <c>state.json</c>, which the restarted daemons report.
/// </summary>
public static class UpdateApplier
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(90);
    /// <summary>Healthy daemons must still run after this: a crash right after the startup checks rolls back too.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(20);

    public static async Task<int> RunAsync(CancellationToken ct)
    {
        ProcessSignals.DetachFromSession();
        var updateDirectory = UpdateFiles.UpdateDirectory;
        PrivateFiles.CreateDirectory(updateDirectory);
        await using var logFile = PrivateFiles.AppendText(UpdateFiles.LogPath(updateDirectory));
        logFile.AutoFlush = true;
        // Agents stop in parallel: the log is written from several threads
        void Log(string message)
        {
            lock (logFile)
                logFile.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} [update] {message}");
        }

        if (UpdateFiles.Read<PendingUpdate>(UpdateFiles.PendingPath(updateDirectory)) is not { } pending)
        {
            Log("No pending update");
            UpdateFiles.Delete(UpdateFiles.LockPath(updateDirectory));
            return 1;
        }
        PrivateFiles.WriteAllText(UpdateFiles.LockPath(updateDirectory), Environment.ProcessId.ToString());
        Log($"Updating {pending.InstallDirectory} to {pending.Version} (request {pending.RequestId})");

        UpdateOutcome outcome;
        try
        {
            outcome = await ApplyAsync(pending, updateDirectory, Log, ct);
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex}");
            outcome = new UpdateOutcome(pending.RequestId, pending.Version, UpdateStates.Failed, UpdateErrors.SwapFailed, DateTime.UtcNow, ex.Message);
        }

        Log($"Update {outcome.State}{(outcome.Error is null ? "" : $" ({outcome.Error}: {outcome.Step})")}");
        UpdateFiles.Write(UpdateFiles.OutcomePath(updateDirectory), outcome);
        UpdateFiles.Delete(UpdateFiles.PendingPath(updateDirectory));
        UpdateFiles.Delete(UpdateFiles.LockPath(updateDirectory));
        return outcome.State == UpdateStates.Succeeded ? 0 : 1;
    }

    private static async Task<UpdateOutcome> ApplyAsync(PendingUpdate pending, string updateDirectory, Action<string> log, CancellationToken ct)
    {
        var installDirectory = pending.InstallDirectory;
        UpdateOutcome Outcome(string state, string? error = null, string? detail = null) =>
            new(pending.RequestId, pending.Version, state, error, DateTime.UtcNow, detail);
        void Progress(string step) => UpdateFiles.Write(UpdateFiles.OutcomePath(updateDirectory), Outcome(UpdateStates.Applying, detail: step));

        if (!Directory.Exists(pending.StagingDirectory))
            return Outcome(UpdateStates.Failed, UpdateErrors.StagingFailed, "the staged release is gone");

        var instances = AgentInstances.Running(installDirectory);
        log($"Agents to restart: {(instances.Count == 0 ? "none" : string.Join(", ", instances.Select(Describe)))}");

        // In parallel: a daemon that ignores SIGINT (started from a shell's background job) takes the whole timeout
        Progress("stopping");
        var stops = await Task.WhenAll(instances.Select(instance => Task.Run(() => StopAsync(instance, log, ct), ct)));
        if (stops.Contains(false))
        {
            var running = instances.Where((_, i) => !stops[i]).Select(i => Path.GetFileName(i.Directory)).ToList();
            await StartAllAsync(instances.Where((_, i) => stops[i]), installDirectory, log);
            return Outcome(UpdateStates.Failed, UpdateErrors.StopFailed, $"did not stop: {string.Join(", ", running)}");
        }

        // From here on the agents are stopped: whatever happens, they must run again (the previous release if needed)
        var swapped = false;
        string problem;
        try
        {
            Progress("swapping");
            try
            {
                InstallSwap.Swap(installDirectory, pending.StagingDirectory);
                swapped = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Swap failed: {ex.Message}");
                await StartAllAsync(instances, installDirectory, log);
                return Outcome(UpdateStates.Failed, UpdateErrors.SwapFailed, ex.Message);
            }
            log($"Installed {pending.Version}");

            Progress("starting");
            var startedAt = DateTime.UtcNow;
            await StartAllAsync(instances, installDirectory, log);
            if (await WaitHealthyAsync(instances, pending.Version, startedAt, log, ct) is not { } unhealthy)
            {
                InstallSwap.CleanUp(installDirectory);
                return Outcome(UpdateStates.Succeeded);
            }
            problem = unhealthy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Unexpected error: {ex}");
            problem = ex.Message;
            if (!swapped)
            {
                await StartAllAsync(instances, installDirectory, log);
                return Outcome(UpdateStates.Failed, UpdateErrors.SwapFailed, problem);
            }
        }

        log($"Rolling back: {problem}");
        Progress("rolling-back");
        await Task.WhenAll(AgentInstances.Running(installDirectory).Select(instance => Task.Run(() => StopAsync(instance, log, ct), ct)));
        try
        {
            InstallSwap.Rollback(installDirectory, pending.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Rollback failed: {ex.Message}");
            await StartAllAsync(instances, installDirectory, log);
            return Outcome(UpdateStates.Failed, UpdateErrors.Unhealthy, $"{problem}; rollback failed: {ex.Message}");
        }
        var stillDown = await StartAllAsync(instances, installDirectory, log);
        if (stillDown.Count > 0)
        {
            // Rolling back was no repair: the cause is on this machine (a file of the repo, say), not in the release
            var names = string.Join(", ", stillDown);
            log($"The previous release does not start {names} either: fix the reason above, the release is not the cause");
            problem += $"; the previous release does not start {names} either";
        }
        return Outcome(UpdateStates.RolledBack, UpdateErrors.Unhealthy, problem);
    }

    private static string Describe(AgentInstance instance) =>
        $"{instance.Directory} (pid {instance.Pid}, {(instance.Supervised ? "service" : "daemon")})";

    /// <summary>A graceful stop (SIGINT, through the service when there is one), then a kill.</summary>
    private static async Task<bool> StopAsync(AgentInstance instance, Action<string> log, CancellationToken ct)
    {
        var graceful = instance.Supervised ? AgentService.Stop(instance.Directory) : ProcessSignals.Interrupt(instance.Pid);
        if (!graceful)
            log($"Graceful stop of {Describe(instance)} failed");
        if (!await WaitExitAsync(instance.Pid, StopTimeout, ct))
        {
            log($"{Describe(instance)} still running: killing it");
            try
            {
                using var process = Process.GetProcessById(instance.Pid);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (!await WaitExitAsync(instance.Pid, TimeSpan.FromSeconds(5), ct))
                return false;
        }
        new DaemonManager(instance.Directory).RemovePidFile();
        return true;
    }

    private static async Task<bool> WaitExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (AgentInstances.IsAlive(pid))
        {
            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(250, ct);
        }
        return true;
    }

    /// <summary>
    /// Each agent comes back the way it ran: through its service, or as a daemon (<c>start -d</c>). Returns the names of
    /// those that could not start.
    /// </summary>
    private static async Task<List<string>> StartAllAsync(IEnumerable<AgentInstance> instances, string installDirectory, Action<string> log)
    {
        var failed = new List<string>();
        foreach (var instance in instances)
        {
            var since = DateTime.UtcNow;
            var started = instance.Supervised
                ? AgentService.Start(instance.Directory)
                : await StartDaemonAsync(instance.Directory, installDirectory);
            if (started)
            {
                log($"Started {instance.Directory}");
                continue;
            }
            log($"Could not start {instance.Directory}{(StartupFailure(instance.Directory, since) is { } reason ? $": {reason}" : "")}");
            failed.Add(Path.GetFileName(instance.Directory));
        }
        return failed;
    }

    /// <summary>The problem a daemon of <paramref name="directory"/> reported in its health file since
    /// <paramref name="since"/> (a startup failure, or a failed startup check), if any.</summary>
    public static string? StartupFailure(string directory, DateTime since) =>
        UpdateFiles.Read<InstanceHealth>(UpdateFiles.HealthPath(directory)) is { Ok: false } health && health.At >= since
            ? (health.Problem ?? "its startup checks failed").ReplaceLineEndings(" ") // one line in update.log
            : null;

    private static async Task<bool> StartDaemonAsync(string directory, string installDirectory)
    {
        // Not redirected: the daemon `start -d` spawns inherits its output, and a pipe would stay open as long as the
        // daemon runs. The updater's own output goes nowhere (/dev/null, or the journal of its transient unit).
        var psi = new ProcessStartInfo(Path.Combine(installDirectory, "sidehub-agent"))
        {
            ArgumentList = { "start", "-d" },
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (RootPolicy.IsCurrentUserRoot())
            psi.ArgumentList.Add(RootPolicy.AllowFlag);
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Null once every restarted daemon runs <paramref name="version"/>, passed its startup checks and still runs
    /// <see cref="SettleTime"/> later; otherwise what went wrong. The backend connection is not required: a SideHub
    /// outage must not roll back a good release. A daemon that failed since <paramref name="startedAt"/> says why in
    /// its health file, even once it has exited (no need to wait for the timeout then).
    /// </summary>
    private static async Task<string?> WaitHealthyAsync(
        List<AgentInstance> instances, string version, DateTime startedAt, Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + HealthTimeout;
        while (true)
        {
            var waiting = new List<string>();
            foreach (var instance in instances)
            {
                var name = Path.GetFileName(instance.Directory);
                var pid = new DaemonManager(instance.Directory).ReadPid();
                if (StartupFailure(instance.Directory, startedAt) is { } failure)
                    return $"{name}: {failure}";
                var health = UpdateFiles.Read<InstanceHealth>(UpdateFiles.HealthPath(instance.Directory));
                if (pid is null || health is null || health.Pid != pid || health.Version != version || !AgentInstances.IsAlive(pid.Value))
                    waiting.Add(name);
            }

            if (waiting.Count == 0)
                break;
            if (DateTime.UtcNow > deadline)
                return $"not started after {HealthTimeout.TotalSeconds:0}s: {string.Join(", ", waiting)}";
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        log("All agents started, checking they keep running");
        await Task.Delay(SettleTime, ct);
        var died = instances
            .Where(instance => new DaemonManager(instance.Directory).ReadPid() is not { } pid || !AgentInstances.IsAlive(pid))
            .Select(instance => Path.GetFileName(instance.Directory))
            .ToList();
        return died.Count == 0 ? null : $"stopped after starting: {string.Join(", ", died)}";
    }
}

/// <summary>The install folder swap: two renames on the same file system, the previous release kept beside.</summary>
public static class InstallSwap
{
    public static string PreviousDirectory(string installDirectory) => $"{installDirectory}.previous";

    public static void Swap(string installDirectory, string stagingDirectory)
    {
        var previous = PreviousDirectory(installDirectory);
        UpdateStager.DeleteDirectory(previous);
        Directory.Move(installDirectory, previous);
        try
        {
            Directory.Move(stagingDirectory, installDirectory);
        }
        catch
        {
            Directory.Move(previous, installDirectory);
            throw;
        }
    }

    /// <summary>Puts the previous release back; the failed one is kept as <c>.failed-&lt;version&gt;</c> to look at.</summary>
    public static void Rollback(string installDirectory, string version)
    {
        var previous = PreviousDirectory(installDirectory);
        if (!Directory.Exists(previous))
            throw new IOException($"{previous} is missing");
        var failed = $"{installDirectory}.failed-{version}";
        UpdateStager.DeleteDirectory(failed);
        Directory.Move(installDirectory, failed);
        Directory.Move(previous, installDirectory);
    }

    /// <summary>After a successful update: drop the releases that failed before. The previous one stays.</summary>
    public static void CleanUp(string installDirectory)
    {
        var parent = Path.GetDirectoryName(installDirectory);
        if (parent is null || !Directory.Exists(parent))
            return;
        foreach (var directory in Directory.GetDirectories(parent, $"{Path.GetFileName(installDirectory)}.failed-*"))
            UpdateStager.DeleteDirectory(directory);
    }
}

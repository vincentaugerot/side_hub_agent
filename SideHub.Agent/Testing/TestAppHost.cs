using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SideHub.Agent.Review;

namespace SideHub.Agent.Testing;

/// <summary>
/// What <c>&lt;repository&gt;/.sidehub/worktree.json</c> says of the application a local test execution plays:
/// <c>{ "test": { "start": "&lt;shell command&gt;", "ready": "&lt;url&gt;", "timeout": &lt;seconds&gt; } }</c>, next to the
/// queue's <c>setup</c> (<see cref="QueueWorktree.ConfigFile"/>) and local to the machine like it.
/// </summary>
/// <param name="Start">The command that starts the application and keeps running while it serves.</param>
/// <param name="Ready">The address that answers once the application is up; the address the tests visit by default.</param>
public sealed record TestAppConfig(string Start, Uri Ready, TimeSpan Timeout)
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MinTimeoutSeconds = 5;
    // The backend gives up on the run's pty.started after 300 s: the refusal must reach it before.
    public const int MaxTimeoutSeconds = 280;

    private const string Example = "{ \"test\": { \"start\": \"npm run dev\" } }";

    /// <summary>Reads the file; null with <paramref name="problem"/> (what to write, and where) when it cannot start anything.</summary>
    /// <param name="testUrl">The address the tests visit (<c>SIDEHUB_TEST_URL</c>), waited for when the file names no other.</param>
    public static TestAppConfig? Read(string file, string? testUrl, out string problem)
    {
        problem = "";
        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            problem = $"No {file}: create it with the command that starts the application, e.g. {Example}.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{file} could not be read ({ex.Message}).";
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var test = document.RootElement is { ValueKind: JsonValueKind.Object } root
                       && root.TryGetProperty("test", out var value) && value.ValueKind == JsonValueKind.Object
                ? value
                : default;
            var start = Text(test, "start");
            if (start is null)
            {
                problem = $"No test.start in {file}: write there the command that starts the application, e.g. {Example}.";
                return null;
            }

            var ready = Text(test, "ready");
            if (!TryHttpAddress(ready ?? testUrl, out var address))
            {
                problem = ready is not null
                    ? $"test.ready in {file} is not an http(s) address: \"{ready}\"."
                    : $"No address to wait for: the execution gave none ({TestAppHost.UrlVariable}), and {file} has no test.ready.";
                return null;
            }

            var seconds = test.TryGetProperty("timeout", out var timeout) && timeout.ValueKind == JsonValueKind.Number
                          && timeout.TryGetDouble(out var asked)
                ? ClampTimeout(asked)
                : DefaultTimeoutSeconds;
            return new TestAppConfig(start, address, TimeSpan.FromSeconds(seconds));
        }
        catch (JsonException ex)
        {
            problem = $"{file} is not valid JSON ({ex.Message}): it holds the command that starts the application, e.g. {Example}.";
            return null;
        }
    }

    /// <summary>The seconds asked for, kept between <see cref="MinTimeoutSeconds"/> and <see cref="MaxTimeoutSeconds"/>.</summary>
    public static int ClampTimeout(double seconds) =>
        double.IsNaN(seconds) ? DefaultTimeoutSeconds : (int)Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds);

    private static string? Text(JsonElement test, string name) =>
        test.ValueKind == JsonValueKind.Object && test.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    private static bool TryHttpAddress(string? text, out Uri address) =>
        Uri.TryCreate(text, UriKind.Absolute, out address!) && address.Scheme is "http" or "https";
}

/// <summary>The application of a test execution, answering: where it plays, and what is checked out there.</summary>
/// <param name="Directory">The folder played, absolute.</param>
/// <param name="Worktree">It is the queue's worktree, not the agent's folder.</param>
/// <param name="Branch">Null when HEAD is detached, or outside a git repository.</param>
/// <param name="Head">The full sha of HEAD; null outside a git repository.</param>
/// <param name="Reused">It was already running for this execution (then <paramref name="ReadyAfterMs"/> is 0).</param>
public sealed record TestAppStarted(string Directory, bool Worktree, string? Branch, string? Head, long ReadyAfterMs, bool Reused);

/// <summary>Either the application answers (<see cref="Started"/>), or why not (<see cref="Failure"/>, as sent to the backend).</summary>
public sealed record TestAppResult(TestAppStarted? Started, string? Failure);

/// <summary>
/// The application a local test execution plays (doc 73): started by the agent on its machine with the command of
/// <see cref="TestAppConfig"/>, in the folder that holds the work of its task queue (the queue's worktree as it is once
/// there is one, else the agent's folder). An execution is several runs, one PTY after the other: the application
/// belongs to the execution, not to a PTY. It is started before the first run, found again by the next ones, and
/// stopped when the backend says the execution is over (<c>test.app.stop</c>), when another execution starts (one
/// application at a time per agent), when the agent stops, or when no terminal of the execution has lived for
/// <see cref="IdleLimit"/> (the backend may be gone).
/// </summary>
public sealed class TestAppHost : IAsyncDisposable
{
    /// <summary>In a pty.start's environment: the run plays the application the agent starts (<see cref="LocalApp"/>).</summary>
    public const string AppVariable = "SIDEHUB_TEST_APP";
    public const string LocalApp = "local";
    public const string ExecutionVariable = "SIDEHUB_TEST_EXECUTION_ID";
    /// <summary>The address the tests visit, given to the application too: it may read its port there.</summary>
    public const string UrlVariable = "SIDEHUB_TEST_URL";

    /// <summary>The longest refusal sent to the backend, in characters.</summary>
    public const int MaxDetailLength = 600;
    /// <summary>What is kept of the application's output on disk; the rest is read and dropped.</summary>
    public const long MaxLogBytes = 5 * 1024 * 1024;
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutputWait = TimeSpan.FromSeconds(1);

    private readonly string _repositoryDirectory;
    private readonly Func<(string Directory, bool Worktree)> _place;
    private readonly string _logDirectory;
    private readonly Func<Guid, bool> _hasLiveTerminal;
    private readonly Action<string> _log;
    private readonly TimeSpan _pollInterval;
    private readonly HttpClient _http;
    private readonly Timer _watch;
    private readonly PosixSignalRegistration _terminated;
    // One start or stop at a time: they all move the same application.
    private readonly SemaphoreSlim _lock = new(1, 1);
    // The starts going on or waiting for their turn, so that a stop or another execution interrupts them.
    private readonly ConcurrentDictionary<Start, byte> _starts = new();
    // Written under the lock; read without it only to kill the process when the agent's own exits.
    private volatile App? _app;
    private bool _disposed;

    /// <param name="repositoryDirectory">The agent's folder: its <c>.sidehub/worktree.json</c> is read, never the worktree's.</param>
    /// <param name="place">The folder that holds the queue's work now, and whether it is the queue's worktree.</param>
    /// <param name="logDirectory">Where the application's output is written (<c>.sidehub/run/tests</c>).</param>
    /// <param name="hasLiveTerminal">Whether a PTY of this execution is alive.</param>
    /// <param name="pollInterval">Time between two looks at the ready address; a second by default.</param>
    public TestAppHost(
        string repositoryDirectory, Func<(string Directory, bool Worktree)> place, string logDirectory,
        Func<Guid, bool> hasLiveTerminal, Action<string> log, TimeSpan? pollInterval = null)
    {
        _repositoryDirectory = repositoryDirectory;
        _place = place;
        _logDirectory = logDirectory;
        _hasLiveTerminal = hasLiveTerminal;
        _log = log;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        // A redirect is an answer, not a page to follow; the application is on this machine, never behind the proxy.
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = ProbeTimeout })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _watch = new Timer(_ => _ = WatchAsync(), null, WatchInterval, WatchInterval);
        // The application must not outlive the agent and keep its port. A SIGTERM (kill <pid>) disposes nothing and,
        // since .NET 10, runs no exit handler either: it is caught here, then ends the process as it would have.
        _terminated = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => _app?.Kill());
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>The file the output (stdout and stderr) of the application of an execution is written to, rewritten at each start.</summary>
    public string LogFile(Guid executionId) => Path.Combine(_logDirectory, $"app-{executionId:N}.log");

    /// <summary>The file the start command is read from: the repository's when the agent's folder is in one.</summary>
    public async Task<string> ConfigFileAsync() =>
        Path.Combine((await GitRepository.OpenAsync(_repositoryDirectory))?.TopLevel ?? Path.GetFullPath(_repositoryDirectory),
            ".sidehub", QueueWorktree.ConfigFile);

    /// <summary>
    /// Makes the application of <paramref name="executionId"/> answer: found running, or started and waited for (up to
    /// <see cref="TestAppConfig.Timeout"/>). The application of another execution is stopped first. Never throws for an
    /// application that does not come up: the failure says why, with the end of its output.
    /// </summary>
    /// <param name="masker">Masks the secrets of the run's PTY in the failure; a masker of its own, never the PTY reader's.</param>
    public async Task<TestAppResult> EnsureAsync(Guid executionId, string? testUrl, SecretMasker? masker, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var other in _starts.Keys.Where(s => s.ExecutionId != executionId))
            other.Interrupt($"execution {executionId} starts on this agent");
        using var start = new Start(executionId, ct);
        _starts[start] = 0;
        try
        {
            await _lock.WaitAsync(start.Token);
            try
            {
                return await EnsureLockedAsync(start, testUrl, masker);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (OperationCanceledException) when (start.Interrupted is { } why && !ct.IsCancellationRequested)
        {
            _log($"Test app of execution {executionId} not started: {why}");
            return new TestAppResult(null, Detail($"The application was not started: {why}.", null, masker));
        }
        finally
        {
            _starts.TryRemove(start, out _);
        }
    }

    private async Task<TestAppResult> EnsureLockedAsync(Start start, string? testUrl, SecretMasker? masker)
    {
        var id = start.ExecutionId;
        if (_app is { } running)
        {
            if (running.ExecutionId == id && !running.HasExited)
            {
                running.LastAlive = DateTime.UtcNow;
                var (reusedBranch, reusedHead) = await ReadHeadAsync(running.Directory, start.Token);
                _log($"Test app of execution {id} reused in {running.Directory} ({Describe(reusedBranch, reusedHead)})");
                return new TestAppResult(new TestAppStarted(running.Directory, running.Worktree, reusedBranch, reusedHead, 0, Reused: true), null);
            }
            await StopLockedAsync(running.ExecutionId == id ? "started again for the next run" : $"execution {id} starts");
        }

        var configFile = await ConfigFileAsync();
        if (TestAppConfig.Read(configFile, testUrl, out var problem) is not { } config)
            return Refuse(id, problem, problem, null, masker);

        var (directory, worktree) = _place();
        directory = Path.GetFullPath(directory);
        // As written in the file or given by the execution, in what is logged and told.
        var ready = config.Ready.OriginalString;
        var limit = $"{config.Timeout.TotalSeconds:0} s";
        // Answering before anything is started: another application holds the address, and the tests would play it
        // instead of this folder (the command then fails to bind, or worse, starts on another port unseen).
        if (await ProbeAsync(config.Ready, start.Token) is < 500)
            return Refuse(id, $"Something already answers at {ready} before the application is started: the tests would not play {directory}. Stop it, or change the port.",
                $"something already answers at {ready}", null, masker);

        App app;
        try
        {
            app = Launch(id, config.Start, directory, worktree, testUrl);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Refuse(id, $"The command of test.start ({configFile}) could not be run in {directory}: {ex.Message}",
                $"its command could not be run ({ex.Message})", null, masker);
        }
        _app = app;
        _log($"Test app of execution {id} starting in {directory}, waiting for {ready} (up to {limit})");

        string why, summary;
        try
        {
            var clock = Stopwatch.StartNew();
            var exited = app.Exited(start.Token);
            while (true)
            {
                if (!app.HasExited && await ProbeAsync(config.Ready, start.Token) is < 500 && !app.HasExited)
                {
                    app.LastAlive = DateTime.UtcNow;
                    var (branch, head) = await ReadHeadAsync(directory, start.Token);
                    _log($"Test app of execution {id} ready at {ready} after {Seconds(clock.Elapsed)} in {directory} ({Describe(branch, head)})");
                    return new TestAppResult(new TestAppStarted(directory, worktree, branch, head, clock.ElapsedMilliseconds, Reused: false), null);
                }
                if (app.HasExited)
                {
                    why = $"The command of test.start exited with code {app.ExitCode} before the application answered at {ready}: it must keep running while the tests play ({configFile}).";
                    summary = $"its command exited with code {app.ExitCode} after {Seconds(clock.Elapsed)}";
                    break;
                }
                if (clock.Elapsed >= config.Timeout)
                {
                    why = $"The application did not answer at {ready} within {limit} (test.ready and test.timeout in {configFile}).";
                    summary = $"no answer at {ready} within {limit}";
                    break;
                }
                // Its exit is seen at once, not at the next look.
                await Task.WhenAny(exited, Task.Delay(_pollInterval, start.Token));
                start.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            _app = null;
            await app.StopAsync();
            throw;
        }

        _app = null;
        await app.StopAsync();
        return Refuse(id, why, summary, app.Output, masker);
    }

    private TestAppResult Refuse(Guid executionId, string why, string summary, string? output, SecretMasker? masker)
    {
        // Its output is never logged here (it is in its file): only why.
        _log($"Test app of execution {executionId} failed: {summary}{(output is null ? "" : $" (output in {LogFile(executionId)})")}");
        return new TestAppResult(null, Detail(why, output, masker));
    }

    /// <summary>The execution is over: its application is stopped if it runs, and a start of it going on gives up.</summary>
    public async Task StopAsync(Guid executionId, string why)
    {
        foreach (var start in _starts.Keys.Where(s => s.ExecutionId == executionId))
            start.Interrupt(why);
        await _lock.WaitAsync();
        try
        {
            if (_app?.ExecutionId == executionId)
                await StopLockedAsync(why);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Stops the application when no terminal of its execution has lived for <see cref="IdleLimit"/>: the backend
    /// that should have said the execution is over may be gone. Called every minute; <paramref name="now"/> is UTC.
    /// </summary>
    public async Task StopIfIdleAsync(DateTime now)
    {
        // Taken: an application is being started or stopped, so it is not idle.
        if (!await _lock.WaitAsync(0))
            return;
        try
        {
            if (_app is not { } app)
                return;
            if (app.HasExited)
                await StopLockedAsync("found at the watch");
            else if (_hasLiveTerminal(app.ExecutionId))
                app.LastAlive = now;
            else if (now - app.LastAlive >= IdleLimit)
                await StopLockedAsync($"no terminal of its execution for {IdleLimit.TotalMinutes:0} min");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WatchAsync()
    {
        try { await StopIfIdleAsync(DateTime.UtcNow); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log($"Test app watch failed: {ex.Message}"); }
    }

    private async Task StopLockedAsync(string why)
    {
        if (_app is not { } app)
            return;
        _app = null;
        var exitedBefore = app.HasExited;
        await app.StopAsync();
        _log(exitedBefore
            ? $"Test app of execution {app.ExecutionId} had exited with code {app.ExitCode} ({why})"
            : $"Test app of execution {app.ExecutionId} stopped ({why})");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _watch.DisposeAsync();
        foreach (var start in _starts.Keys)
            start.Interrupt("the agent stops");
        await _lock.WaitAsync();
        try
        {
            await StopLockedAsync("the agent stops");
        }
        finally
        {
            _lock.Release();
        }
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _terminated.Dispose();
        _http.Dispose();
    }

    private void OnProcessExit(object? sender, EventArgs e) => _app?.Kill();

    /// <summary>
    /// What the backend is told of an application that is not ready: why, then the last lines of its output, secrets
    /// masked, <see cref="MaxDetailLength"/> characters at most. The reason is kept whole; the output keeps its end,
    /// where the error is.
    /// </summary>
    public static string Detail(string why, string? output, SecretMasker? masker)
    {
        string Masked(string text) => masker is null ? text : masker.Process(text) + masker.Flush();

        why = Masked(why);
        if (why.Length > MaxDetailLength)
            return why[..(MaxDetailLength - 1)] + "…";
        // Masked before it is cut: half a secret would not be recognized.
        var room = MaxDetailLength - why.Length - 1;
        if (room < 20 || TerminalText.LastLines(output is null ? null : Masked(output), maxLines: 10, maxChars: room) is not { } lines)
            return why;
        return $"{why}\n{lines}";
    }

    /// <summary>The branch checked out in <paramref name="directory"/> (null when detached) and the full sha of its
    /// HEAD; both null outside a git repository, or when git does not answer.</summary>
    public static async Task<(string? Branch, string? Head)> ReadHeadAsync(string directory, CancellationToken ct)
    {
        var symbolic = await GitCommand.RunAsync(directory, GitCommand.DefaultTimeout, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct: ct);
        var sha = await GitCommand.RunAsync(directory, GitCommand.DefaultTimeout, ["rev-parse", "--verify", "--quiet", "HEAD"], ct: ct);
        return (symbolic is { Succeeded: true } && symbolic.Output.Trim() is { Length: > 0 } branch ? branch : null,
            sha is { Succeeded: true } && sha.Output.Trim() is { Length: > 0 } head ? head : null);
    }

    private static string Describe(string? branch, string? head) =>
        head is null ? branch ?? "not a git repository" : $"{branch ?? "detached"} {(head.Length > 7 ? head[..7] : head)}";

    private static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";

    /// <summary>The status the address answers with; null when it does not (refused, TLS error, no answer in time).</summary>
    private async Task<int?> ProbeAsync(Uri address, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private App Launch(Guid executionId, string command, string directory, bool worktree, string? testUrl)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            // Its input stays open and silent: some servers stop when theirs ends (esbuild's watch mode).
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            if (!ShellPolicy.TryResolve("cmd", out var cmd))
                throw new InvalidOperationException("cmd.exe was not found.");
            psi.FileName = cmd;
            // /s: cmd strips these two quotes and reads the command as written, its own quotes included.
            psi.Arguments = $"/d /s /c \"{command}\"";
            psi.Environment[ExecutableResolver.NoCurrentDirectoryLookupVariable] = "1";
        }
        else
        {
            if (!ShellPolicy.TryResolve("sh", out var sh))
                throw new InvalidOperationException("sh was not found.");
            psi.FileName = sh;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        // The daemon's environment, as the machine's owner set it for the command he wrote. Minus what a daemon
        // restarted from a SideHub terminal inherited from it (that terminal's token): never the application's.
        foreach (var name in psi.Environment.Keys.Where(k => k.StartsWith("SIDEHUB_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(name);
        if (!string.IsNullOrEmpty(testUrl))
            psi.Environment[UrlVariable] = testUrl;
        psi.Environment[ExecutionVariable] = executionId.ToString();
        psi.Environment[QueueWorktree.RepositoryVariable] = Path.GetFullPath(_repositoryDirectory);

        PrivateFiles.CreateDirectory(_logDirectory);
        PurgeOldLogs();
        var path = LogFile(executionId);
        PrivateFiles.RestrictFile(path);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 0 };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFiles.PrivateFileMode;
        var log = new AppLog(new FileStream(path, options));
        try
        {
            var process = new Process { StartInfo = psi };
            process.Start();
            return new App(executionId, process, log, directory, worktree);
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    /// <summary>One file per execution: those not written for <see cref="WorkReviewTracker.Retention"/> are deleted,
    /// like the images of the runs kept in the same folder.</summary>
    private void PurgeOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow - WorkReviewTracker.Retention;
            foreach (var file in new DirectoryInfo(_logDirectory).EnumerateFiles("app-*.log"))
                if (file.LastWriteTimeUtc < cutoff)
                    file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Old test app outputs not purged: {ex.Message}");
        }
    }

    /// <summary>A start going on or waiting for its turn, and why it was told to give up.</summary>
    private sealed class Start : IDisposable
    {
        private readonly CancellationTokenSource _cancel;
        private volatile string? _interrupted;

        public Start(Guid executionId, CancellationToken ct)
        {
            ExecutionId = executionId;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Token = _cancel.Token;
        }

        public Guid ExecutionId { get; }
        /// <summary>Canceled when the agent stops, or by <see cref="Interrupt"/>.</summary>
        public CancellationToken Token { get; }
        public string? Interrupted => _interrupted;

        public void Interrupt(string why)
        {
            _interrupted ??= why;
            try { _cancel.Cancel(); }
            catch (ObjectDisposedException) { /* it just ended */ }
        }

        public void Dispose() => _cancel.Dispose();
    }

    /// <summary>The application's process, with the shell that started it and everything under it.</summary>
    private sealed class App
    {
        private readonly Process _process;
        private readonly AppLog _log;
        private readonly Task _output;
        private int _exitCode;

        public App(Guid executionId, Process process, AppLog log, string directory, bool worktree)
        {
            ExecutionId = executionId;
            Directory = directory;
            Worktree = worktree;
            _process = process;
            _log = log;
            _output = Task.WhenAll(PumpAsync(process.StandardOutput.BaseStream, log), PumpAsync(process.StandardError.BaseStream, log));
        }

        public Guid ExecutionId { get; }
        public string Directory { get; }
        public bool Worktree { get; }
        /// <summary>When a terminal of the execution was last seen alive (UTC).</summary>
        public DateTime LastAlive { get; set; } = DateTime.UtcNow;

        public bool HasExited
        {
            get
            {
                try
                {
                    if (!_process.HasExited)
                        return false;
                    _exitCode = _process.ExitCode;
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // Disposed by StopAsync: the code read before is kept.
                    return true;
                }
            }
        }

        /// <summary>The code the command exited with, once <see cref="HasExited"/> said so.</summary>
        public int ExitCode => _exitCode;

        /// <summary>The end of what the application printed.</summary>
        public string Output => _log.Tail();

        public Task Exited(CancellationToken ct) => _process.WaitForExitAsync(ct);

        public void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }

        /// <summary>Kills the whole tree, waits briefly for it and for its last output, then lets go of both.</summary>
        public async Task StopAsync()
        {
            _ = HasExited;
            Kill();
            try
            {
                using var limit = new CancellationTokenSource(ExitWait);
                await _process.WaitForExitAsync(limit.Token);
                _ = HasExited;
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            // The last lines say why it failed. A process that escaped the kill keeps the pipes open: not waited for.
            await Task.WhenAny(_output, Task.Delay(OutputWait));
            _process.Dispose();
            _log.Dispose();
        }

        private static async Task PumpAsync(Stream source, AppLog log)
        {
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                    log.Write(buffer.AsSpan(0, read));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    /// <summary>The application's output: written to its file up to <see cref="MaxLogBytes"/>, and its end kept in
    /// memory for the refusal. Its two streams write here, each from its own reader.</summary>
    private sealed class AppLog(FileStream file) : IDisposable
    {
        private const int TailBytes = 8 * 1024;
        private static readonly byte[] CutNotice = Encoding.UTF8.GetBytes("\n[SideHub: the output past 5 MB is not kept]\n");

        private readonly object _gate = new();
        private readonly byte[] _tail = new byte[TailBytes];
        private int _tailLength;
        private FileStream? _file = file;
        private long _written;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                Keep(bytes);
                if (_file is null || _written >= MaxLogBytes)
                    return;
                var kept = bytes[..(int)Math.Min(bytes.Length, MaxLogBytes - _written)];
                try
                {
                    _file.Write(kept);
                    _written += kept.Length;
                    if (_written >= MaxLogBytes)
                        _file.Write(CutNotice);
                }
                catch (IOException)
                {
                    // Disk full: the application goes on, its output is only kept in memory.
                    _file.Dispose();
                    _file = null;
                }
            }
        }

        public string Tail()
        {
            lock (_gate)
                return Encoding.UTF8.GetString(_tail, 0, _tailLength);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _file?.Dispose();
                _file = null;
            }
        }

        private void Keep(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= _tail.Length)
            {
                bytes[^_tail.Length..].CopyTo(_tail);
                _tailLength = _tail.Length;
                return;
            }
            var dropped = _tailLength + bytes.Length - _tail.Length;
            if (dropped > 0)
            {
                _tail.AsSpan(dropped, _tailLength - dropped).CopyTo(_tail);
                _tailLength -= dropped;
            }
            bytes.CopyTo(_tail.AsSpan(_tailLength));
            _tailLength += bytes.Length;
        }
    }
}

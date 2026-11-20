using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SideHub.Agent.Testing;

namespace SideHub.Agent.Tests;

/// <summary>What <c>.sidehub/worktree.json</c> says of the application of a local test execution, and what is told when it says nothing usable.</summary>
public sealed class TestAppConfigTests : IDisposable
{
    private const string TestUrl = "http://127.0.0.1:4173";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sidehub-test-app-config-");

    public void Dispose() => _root.Delete(recursive: true);

    private string ConfigFile => Path.Combine(_root.FullName, "worktree.json");

    private TestAppConfig? Read(string? json, out string problem, string? testUrl = TestUrl)
    {
        if (json is not null)
            File.WriteAllText(ConfigFile, json);
        return TestAppConfig.Read(ConfigFile, testUrl, out problem);
    }

    [Fact]
    public void The_command_alone_waits_for_the_address_of_the_tests_for_two_minutes()
    {
        var config = Read("""{ "setup": "npm ci", "test": { "start": " npm run dev " } }""", out var problem);

        Assert.NotNull(config);
        Assert.Equal("", problem);
        Assert.Equal("npm run dev", config.Start);
        Assert.Equal(new Uri(TestUrl), config.Ready);
        Assert.Equal(TimeSpan.FromSeconds(120), config.Timeout);
    }

    [Fact]
    public void Its_own_ready_address_and_timeout_are_taken()
    {
        var config = Read("""{ "test": { "start": "npm run dev", "ready": "http://127.0.0.1:4173/health", "timeout": 45 } }""", out _);

        Assert.NotNull(config);
        Assert.Equal(new Uri("http://127.0.0.1:4173/health"), config.Ready);
        Assert.Equal(TimeSpan.FromSeconds(45), config.Timeout);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    [InlineData(5, 5)]
    [InlineData(90.9, 90)]
    [InlineData(280, 280)]
    [InlineData(300, 280)]
    [InlineData(86400, 280)]
    public void The_timeout_stays_under_what_the_backend_waits(double asked, int kept)
    {
        Assert.Equal(kept, TestAppConfig.ClampTimeout(asked));
        var config = Read($$"""{ "test": { "start": "x", "timeout": {{asked.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } }""", out _);
        Assert.Equal(TimeSpan.FromSeconds(kept), config!.Timeout);
    }

    [Fact]
    public void A_timeout_that_is_not_a_number_is_the_default_one()
    {
        var config = Read("""{ "test": { "start": "x", "timeout": "soon" } }""", out _);

        Assert.Equal(TimeSpan.FromSeconds(TestAppConfig.DefaultTimeoutSeconds), config!.Timeout);
    }

    [Fact]
    public void Without_the_file_the_refusal_says_what_to_create_and_where()
    {
        Assert.Null(Read(null, out var problem));

        Assert.StartsWith($"No {ConfigFile}: create it", problem);
        Assert.Contains("""{ "test": { "start": "npm run dev" } }""", problem);
    }

    [Fact]
    public void A_file_that_is_not_json_is_said_so()
    {
        Assert.Null(Read("{ test: ", out var problem));

        Assert.StartsWith($"{ConfigFile} is not valid JSON (", problem);
        Assert.Contains("""{ "test": { "start": "npm run dev" } }""", problem);
    }

    [Theory]
    [InlineData("""{ "setup": "npm ci" }""")]
    [InlineData("""{ "test": { "ready": "http://127.0.0.1:4173" } }""")]
    [InlineData("""{ "test": { "start": "  " } }""")]
    [InlineData("""{ "test": "npm run dev" }""")]
    [InlineData("[]")]
    public void Without_a_start_command_the_refusal_says_what_to_write_and_where(string json)
    {
        Assert.Null(Read(json, out var problem));

        Assert.Equal($"No test.start in {ConfigFile}: write there the command that starts the application, e.g. "
                     + """{ "test": { "start": "npm run dev" } }.""", problem);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("localhost:4173")]
    [InlineData("ftp://127.0.0.1/")]
    public void A_ready_address_that_is_not_http_is_refused(string ready)
    {
        Assert.Null(Read(JsonSerializer.Serialize(new { test = new { start = "x", ready } }), out var problem));

        Assert.Equal($"test.ready in {ConfigFile} is not an http(s) address: \"{ready}\".", problem);
    }

    [Fact]
    public void Without_any_address_to_wait_for_nothing_is_started()
    {
        Assert.Null(Read("""{ "test": { "start": "x" } }""", out var problem, testUrl: null));

        Assert.Contains("SIDEHUB_TEST_URL", problem);
        Assert.Contains("test.ready", problem);
    }
}

/// <summary>What the backend is told of an application that is not ready.</summary>
public sealed class TestAppDetailTests
{
    [Fact]
    public void The_reason_comes_first_then_the_last_lines_of_the_output_without_their_colors()
    {
        var detail = TestAppHost.Detail("The command exited with code 1.", "building\r\n\u001b[31mError: port 4173 in use\u001b[0m\n\n", null);

        Assert.Equal("The command exited with code 1.\nbuilding\nError: port 4173 in use", detail);
    }

    [Fact]
    public void Without_output_the_reason_stands_alone()
    {
        Assert.Equal("No test.start.", TestAppHost.Detail("No test.start.", null, null));
        Assert.Equal("No test.start.", TestAppHost.Detail("No test.start.", " \n", null));
    }

    [Fact]
    public void The_secrets_of_the_run_are_masked_in_the_reason_and_in_the_output()
    {
        var masker = SecretMasker.For(
            new Dictionary<string, string> { ["QA_PASSWORD"] = "hunter2-hunter2", ["SIDEHUB_TEST_URL"] = "http://127.0.0.1:1" }, ["QA_PASSWORD"]);

        var detail = TestAppHost.Detail("No answer with hunter2-hunter2.", "login as qa / hunter2-hunter2 failed", masker);

        Assert.Equal("No answer with ***.\nlogin as qa / *** failed", detail);
    }

    [Fact]
    public void A_long_output_keeps_its_end_within_600_characters()
    {
        var why = "The application did not answer within 120 s.";
        var output = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i} of the output of the application"));

        var detail = TestAppHost.Detail(why, output, null);

        Assert.True(detail.Length <= TestAppHost.MaxDetailLength, $"{detail.Length} characters");
        Assert.StartsWith(why + "\n", detail);
        Assert.EndsWith("line 200 of the output of the application", detail);
        Assert.DoesNotContain("line 150 ", detail);
    }

    [Fact]
    public void A_reason_longer_than_600_characters_is_cut()
    {
        var detail = TestAppHost.Detail(new string('a', 700), "output", null);

        Assert.Equal(TestAppHost.MaxDetailLength, detail.Length);
        Assert.EndsWith("a…", detail);
    }

    [Fact]
    public void A_secret_is_masked_before_the_output_is_cut()
    {
        var masker = SecretMasker.For(new Dictionary<string, string> { ["TOKEN"] = new string('s', 300) }, ["TOKEN"]);
        var output = new string('x', 400) + new string('s', 300) + " end";

        var detail = TestAppHost.Detail("Why.", output, masker);

        Assert.DoesNotContain("ss", detail);
        Assert.EndsWith("*** end", detail);
    }
}

/// <summary>A folder, a start command and a free port for an application started for real (a tiny python server).</summary>
public abstract class TestAppHostFixture : IAsyncLifetime
{
    protected static bool CanRun =>
        !OperatingSystem.IsWindows() && File.Exists("/bin/sh") && ExecutableResolver.Resolve("python3") is not null;

    /// <summary>Serves the folder on the port of <c>SIDEHUB_TEST_URL</c>, after noting that it started, with what, and as which process.</summary>
    protected const string Server =
        "echo start >> starts.txt;"
        + " echo \"$SIDEHUB_TEST_URL|$SIDEHUB_TEST_EXECUTION_ID|$SIDEHUB_REPOSITORY_ROOT|${SIDEHUB_INHERITED_BY_THE_DAEMON:-none}\" > env.txt;"
        + " echo $$ > pid.txt;"
        + " exec python3 -m http.server \"${SIDEHUB_TEST_URL##*:}\" --bind 127.0.0.1";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sidehub-test-app-");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    protected readonly List<string> Logs = [];
    protected readonly HashSet<Guid> LiveTerminals = [];
    protected TestAppHost Host = null!;
    protected bool InWorktree;

    protected string Repository => Path.Combine(_root.FullName, "repository");
    protected string Played => Path.Combine(_root.FullName, "played");
    protected string LogDirectory => Path.Combine(_root.FullName, "run", "tests");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Repository, ".sidehub"));
        Directory.CreateDirectory(Played);
        Host = new TestAppHost(Repository, () => (Played, InWorktree), LogDirectory,
            id => LiveTerminals.Contains(id), line => { lock (Logs) Logs.Add(line); }, pollInterval: TimeSpan.FromMilliseconds(50));
        return Task.CompletedTask;
    }

    /// <summary>Whatever the test did or asserted, nothing it started outlives it.</summary>
    public async Task DisposeAsync()
    {
        await Host.DisposeAsync();
        _http.Dispose();
        _root.Delete(recursive: true);
    }

    protected void Configure(string start, string? ready = null, int? timeout = null) =>
        File.WriteAllText(Path.Combine(Repository, ".sidehub", "worktree.json"),
            JsonSerializer.Serialize(new { test = new { start, ready, timeout } },
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));

    protected static string FreeAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    protected async Task<bool> AnswersAsync(string address)
    {
        try
        {
            using var response = await _http.GetAsync(address);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    protected string[] LogLines()
    {
        lock (Logs) return Logs.ToArray();
    }

    protected string PlayedFile(string name) => File.ReadAllText(Path.Combine(Played, name)).Trim();

    /// <summary>Whether the process still runs (a zombie nobody reaped yet does not).</summary>
    protected static bool Runs(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0] != "Z";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    protected static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        return condition();
    }
}

public sealed class TestAppHostTests : TestAppHostFixture
{
    [Fact]
    public async Task The_application_is_started_in_the_folder_played_and_waited_for()
    {
        if (!CanRun)
            return;
        Configure(Server);
        InWorktree = true;
        var address = FreeAddress();
        var execution = Guid.NewGuid();
        Environment.SetEnvironmentVariable("SIDEHUB_INHERITED_BY_THE_DAEMON", "a terminal's token");
        TestAppResult result;
        try
        {
            result = await Host.EnsureAsync(execution, address, null, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIDEHUB_INHERITED_BY_THE_DAEMON", null);
        }

        Assert.Null(result.Failure);
        var started = Assert.IsType<TestAppStarted>(result.Started);
        Assert.Equal(Played, started.Directory);
        Assert.True(started.Worktree);
        Assert.False(started.Reused);
        Assert.True(started.ReadyAfterMs > 0);
        Assert.True(await AnswersAsync(address));
        // Its environment: the address, the execution and the repository; nothing of SideHub the daemon inherited
        Assert.Equal($"{address}|{execution}|{Repository}|none", PlayedFile("env.txt"));
        Assert.Contains(LogLines(), line => line.StartsWith($"Test app of execution {execution} ready at {address} after ", StringComparison.Ordinal)
                                            && line.EndsWith($" s in {Played} (not a git repository)", StringComparison.Ordinal));
        // Its output (python logs the request that found it ready) is in the file of the execution
        var log = Host.LogFile(execution);
        Assert.Equal(Path.Combine(LogDirectory, $"app-{execution:N}.log"), log);
        Assert.True(await EventuallyAsync(() => File.ReadAllText(log).Contains("GET / HTTP/1.1")), File.ReadAllText(log));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(PrivateFiles.PrivateFileMode, File.GetUnixFileMode(log));
    }

    [Fact]
    public async Task The_next_run_of_the_execution_finds_it_running_and_its_end_stops_it()
    {
        if (!CanRun)
            return;
        Configure(Server);
        var address = FreeAddress();
        var execution = Guid.NewGuid();
        Assert.NotNull((await Host.EnsureAsync(execution, address, null, CancellationToken.None)).Started);
        var pid = int.Parse(PlayedFile("pid.txt"));

        var again = await Host.EnsureAsync(execution, address, null, CancellationToken.None);

        Assert.True(again.Started!.Reused);
        Assert.Equal(0, again.Started.ReadyAfterMs);
        Assert.Equal("start", PlayedFile("starts.txt"));
        Assert.True(Runs(pid));

        await Host.StopAsync(Guid.NewGuid(), "its execution is over");
        Assert.True(Runs(pid), "the end of another execution stopped it");

        await Host.StopAsync(execution, "its execution is over");

        Assert.False(Runs(pid));
        Assert.False(await AnswersAsync(address));
        Assert.Contains($"Test app of execution {execution} stopped (its execution is over)", LogLines());
    }

    [Fact]
    public async Task An_application_that_died_between_two_runs_is_started_again()
    {
        if (!CanRun)
            return;
        Configure(Server);
        var address = FreeAddress();
        var execution = Guid.NewGuid();
        Assert.NotNull((await Host.EnsureAsync(execution, address, null, CancellationToken.None)).Started);
        var pid = int.Parse(PlayedFile("pid.txt"));
        using (var process = Process.GetProcessById(pid))
            process.Kill();
        Assert.True(await EventuallyAsync(() => !Runs(pid)));

        var again = await Host.EnsureAsync(execution, address, null, CancellationToken.None);

        Assert.False(again.Started!.Reused);
        Assert.Equal(2, PlayedFile("starts.txt").Split('\n').Length);
        Assert.True(await AnswersAsync(address));
    }

    [Fact]
    public async Task Another_execution_stops_the_application_of_the_previous_one_first()
    {
        if (!CanRun)
            return;
        Configure(Server);
        var first = FreeAddress();
        var previous = Guid.NewGuid();
        Assert.NotNull((await Host.EnsureAsync(previous, first, null, CancellationToken.None)).Started);
        var second = FreeAddress();
        var next = Guid.NewGuid();

        var result = await Host.EnsureAsync(next, second, null, CancellationToken.None);

        Assert.False(result.Started!.Reused);
        Assert.False(await AnswersAsync(first));
        Assert.True(await AnswersAsync(second));
        Assert.Contains($"Test app of execution {previous} stopped (execution {next} starts)", LogLines());
    }

    [Fact]
    public async Task A_command_that_exits_before_answering_fails_at_once_with_its_code_and_its_last_lines()
    {
        if (!CanRun)
            return;
        Configure("echo compiling; echo 'Error: no such script' >&2; exit 3", timeout: 60);
        var execution = Guid.NewGuid();
        var address = FreeAddress();
        var clock = Stopwatch.StartNew();

        var result = await Host.EnsureAsync(execution, address, null, CancellationToken.None);

        Assert.Null(result.Started);
        Assert.StartsWith($"The command of test.start exited with code 3 before the application answered at {address}: it must keep running", result.Failure);
        Assert.Contains("Error: no such script", result.Failure);
        Assert.True(result.Failure!.Length <= TestAppHost.MaxDetailLength);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"{clock.Elapsed}");
        // Its output is in its file, never in the agent's log
        Assert.DoesNotContain(LogLines(), line => line.Contains("no such script"));
        Assert.Contains(LogLines(), line => line.StartsWith($"Test app of execution {execution} failed: its command exited with code 3 after ", StringComparison.Ordinal)
                                            && line.EndsWith($"(output in {Host.LogFile(execution)})", StringComparison.Ordinal));
        Assert.Contains("Error: no such script", File.ReadAllText(Host.LogFile(execution)));
    }

    [Fact]
    public async Task Without_a_start_command_nothing_is_started_and_the_refusal_says_where_to_write_it()
    {
        if (!CanRun)
            return;
        var execution = Guid.NewGuid();

        var result = await Host.EnsureAsync(execution, FreeAddress(), null, CancellationToken.None);

        Assert.Null(result.Started);
        Assert.StartsWith($"No {Path.Combine(Repository, ".sidehub", "worktree.json")}: create it", result.Failure);
        Assert.False(File.Exists(Host.LogFile(execution)));
    }

    [Fact]
    public async Task An_address_that_already_answers_is_refused_before_anything_is_started()
    {
        if (!CanRun)
            return;
        Configure(Server);
        var address = FreeAddress();
        using var other = new HttpListener();
        other.Prefixes.Add(address + "/");
        other.Start();
        _ = Task.Run(async () =>
        {
            try { while (true) (await other.GetContextAsync()).Response.Close(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        });

        var result = await Host.EnsureAsync(Guid.NewGuid(), address, null, CancellationToken.None);

        Assert.Null(result.Started);
        Assert.StartsWith($"Something already answers at {address} before the application is started", result.Failure);
        Assert.False(File.Exists(Path.Combine(Played, "starts.txt")));
    }

    [Fact]
    public async Task The_end_of_the_execution_interrupts_a_start_going_on_and_kills_what_it_started()
    {
        if (!CanRun)
            return;
        Configure("echo $$ > pid.txt; sleep 30 & echo $! > child.txt; wait", timeout: 60);
        var execution = Guid.NewGuid();
        var starting = Host.EnsureAsync(execution, FreeAddress(), null, CancellationToken.None);
        Assert.True(await EventuallyAsync(() => File.Exists(Path.Combine(Played, "child.txt")) && PlayedFile("child.txt").Length > 0));
        var shell = int.Parse(PlayedFile("pid.txt"));
        var child = int.Parse(PlayedFile("child.txt"));

        await Host.StopAsync(execution, "its execution is over");
        var result = await starting;

        Assert.Equal("The application was not started: its execution is over.", result.Failure);
        Assert.True(await EventuallyAsync(() => !Runs(shell) && !Runs(child)), "the shell or its child still runs");
    }

    [Fact]
    public async Task Left_without_a_terminal_of_its_execution_for_ten_minutes_it_stops_by_itself()
    {
        if (!CanRun)
            return;
        Configure(Server);
        var address = FreeAddress();
        var execution = Guid.NewGuid();
        Assert.NotNull((await Host.EnsureAsync(execution, address, null, CancellationToken.None)).Started);
        var now = DateTime.UtcNow;

        // A run plays for a long time: its terminal keeps the application
        LiveTerminals.Add(execution);
        await Host.StopIfIdleAsync(now.AddMinutes(30));
        Assert.True(await AnswersAsync(address));

        // Between two runs, then the backend never says the execution is over
        LiveTerminals.Clear();
        await Host.StopIfIdleAsync(now.AddMinutes(39));
        Assert.True(await AnswersAsync(address));
        await Host.StopIfIdleAsync(now.AddMinutes(40));

        Assert.False(await AnswersAsync(address));
        Assert.Contains($"Test app of execution {execution} stopped (no terminal of its execution for 10 min)", LogLines());
    }

    [Fact]
    public async Task The_agent_stopping_stops_the_application()
    {
        if (!CanRun)
            return;
        Configure(Server);
        var address = FreeAddress();
        Assert.NotNull((await Host.EnsureAsync(Guid.NewGuid(), address, null, CancellationToken.None)).Started);

        await Host.DisposeAsync();

        Assert.False(await AnswersAsync(address));
    }

    [Fact]
    public async Task The_branch_and_the_full_sha_of_the_folder_played_are_read()
    {
        if (!CanRun || ExecutableResolver.Resolve("git") is null)
            return;
        Assert.Equal((null, null), await TestAppHost.ReadHeadAsync(Played, CancellationToken.None));
        Git("init", "-q", "-b", "sidehub/queue-4a3a6adc");
        File.WriteAllText(Path.Combine(Played, "readme.txt"), "initial");
        Git("add", "readme.txt");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "initial");
        var sha = Git("rev-parse", "HEAD");

        Assert.Equal(("sidehub/queue-4a3a6adc", sha), await TestAppHost.ReadHeadAsync(Played, CancellationToken.None));

        Git("checkout", "-q", "--detach");
        Assert.Equal((null, sha), await TestAppHost.ReadHeadAsync(Played, CancellationToken.None));
        Assert.Equal(40, sha.Length);
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Played, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output.Trim();
    }
}

/// <summary>Apart from the others: the shortest wait the config allows is 5 s, and test classes run side by side.</summary>
public sealed class TestAppHostTimeoutTests : TestAppHostFixture
{
    [Fact]
    public async Task An_application_that_never_answers_is_killed_with_everything_under_it_after_its_timeout()
    {
        if (!CanRun)
            return;
        Configure("echo $$ > pid.txt; echo listening soon; sleep 30 & echo $! > child.txt; wait", timeout: 1);
        var execution = Guid.NewGuid();
        var address = FreeAddress();

        var result = await Host.EnsureAsync(execution, address, null, CancellationToken.None);

        Assert.Null(result.Started);
        Assert.StartsWith($"The application did not answer at {address} within 5 s", result.Failure);
        Assert.EndsWith("\nlistening soon", result.Failure);
        Assert.False(Runs(int.Parse(PlayedFile("pid.txt"))));
        Assert.True(await EventuallyAsync(() => !Runs(int.Parse(PlayedFile("child.txt")))), "the child of the shell still runs");
        Assert.Contains($"Test app of execution {execution} failed: no answer at {address} within 5 s (output in {Host.LogFile(execution)})", LogLines());
    }
}

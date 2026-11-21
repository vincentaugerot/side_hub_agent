using System.Net;
using System.Net.Sockets;
using SideHub.Agent.Update;

namespace SideHub.Agent.Tests;

/// <summary>
/// A server that accepts the TCP connection and never answers the handshake (a proxy cut half way): the attempt is
/// given up after <see cref="WebSocketClient.ConnectTimeout"/> and the agent tries again, instead of waiting for ever.
/// </summary>
[Collection(nameof(ConsoleOutputCollection))]
public sealed class ConnectTimeoutTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sidehub-connect-timeout-").FullName;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _accepted = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly TextWriter _stdout = Console.Out;
    private readonly StringWriter _written = new();
    private readonly TextWriter _output;

    public ConnectTimeoutTests()
    {
        _listener.Start();
        _ = AcceptAndStaySilentAsync();
        Console.SetOut(_output = TextWriter.Synchronized(_written));
    }

    public void Dispose()
    {
        Console.SetOut(_stdout);
        _stop.Cancel();
        _listener.Stop();
        lock (_accepted)
            _accepted.ForEach(client => client.Dispose());
        Directory.Delete(_directory, recursive: true);
    }

    private async Task AcceptAndStaySilentAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_accepted)
                    _accepted.Add(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private string Output
    {
        // The synchronized writer locks on itself
        get { lock (_output) return _written.ToString(); }
    }

    private async Task WaitForOutputAsync(Func<string, bool> seen)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!seen(Output))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Not seen in the agent's output:\n{Output}");
            await Task.Delay(50);
        }
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public async Task An_unanswered_handshake_times_out_and_the_agent_connects_again()
    {
        using var stop = new CancellationTokenSource();
        await using var client = new WebSocketClient(Config(), new CommandExecutor(_directory), _directory, "test", RunDirectory)
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
        };

        var run = client.RunAsync(stop.Token);
        await WaitForOutputAsync(output => Count(output, "Connecting to") >= 2);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains("Error: Connection attempt timed out after", Output);
        Assert.Contains("Reconnecting in 1000ms (attempt 1", Output);
    }

    [Fact]
    public async Task Stopping_the_daemon_during_an_attempt_is_a_shutdown_not_a_timeout()
    {
        using var stop = new CancellationTokenSource();
        await using var client = new WebSocketClient(Config(), new CommandExecutor(_directory), _directory, "test", RunDirectory);

        var run = client.RunAsync(stop.Token);
        await WaitForOutputAsync(output => output.Contains("Connecting to"));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains("Shutting down...", Output);
        Assert.DoesNotContain("timed out", Output);
        Assert.DoesNotContain("Reconnecting", Output);
    }

    [Fact]
    public async Task The_health_file_says_since_when_the_agent_has_no_connection()
    {
        using var stop = new CancellationTokenSource();
        using var updates = new UpdateCoordinator(_directory, _ => { }, new SelfUpdateSupport(false, "test", null), Path.Combine(_directory, "update"));
        updates.ReportStartupChecks([]);
        var before = DateTime.UtcNow;
        await using var client = new WebSocketClient(Config(), new CommandExecutor(_directory), _directory, "test", RunDirectory, updates)
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
        };

        var run = client.RunAsync(stop.Token);
        // Two attempts later, the agent is still said offline since before the first one
        await WaitForOutputAsync(output => Count(output, "Connecting to") >= 1);
        var firstAttempt = DateTime.UtcNow;
        await WaitForOutputAsync(output => Count(output, "Connecting to") >= 2);
        var health = UpdateFiles.Read<InstanceHealth>(UpdateFiles.HealthPath(_directory));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        var connecting = Assert.Single(health!.Connecting!);
        Assert.Equal("test", connecting.Agent);
        Assert.InRange(connecting.Since, before, firstAttempt);
    }

    private string RunDirectory => Path.Combine(_directory, ".sidehub", "run");

    private AgentConfig Config() => new()
    {
        AgentId = $"test-{Guid.NewGuid():N}", WorkspaceId = "workspace", Name = "test", AgentToken = "sh_agent_test",
        Capabilities = ["shell"], SidehubUrl = $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/ws/agent",
    };
}

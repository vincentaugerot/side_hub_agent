using System.Collections;
using System.Reflection;
using System.Text.Json;
using SideHub.Agent.Models;
using SideHub.Agent.Testing;

namespace SideHub.Agent.Tests;

/// <summary>What the agent and the backend tell each other about the application of a local test execution, as written on the wire.</summary>
public sealed class TestAppMessagesTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void The_application_that_answers_is_told_with_where_it_plays()
    {
        var executionId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var json = JsonSerializer.Serialize(new TestAppStartedMessage
        {
            ExecutionId = executionId, RunId = runId, Directory = "/home/dev/.sidehub/worktrees/a1", Worktree = true,
            Branch = "sidehub/queue-a1", Head = "0123456789abcdef0123456789abcdef01234567", ReadyAfterMs = 1234, Reused = false,
        }, Options);

        Assert.Equal(
            $$"""{"type":"test.app.started","executionId":"{{executionId}}","runId":"{{runId}}","directory":"/home/dev/.sidehub/worktrees/a1","worktree":true,"branch":"sidehub/queue-a1","head":"0123456789abcdef0123456789abcdef01234567","readyAfterMs":1234,"reused":false}""",
            json);
    }

    [Fact]
    public void A_detached_head_or_a_folder_outside_git_is_told_with_nulls()
    {
        var json = JsonSerializer.Serialize(new TestAppStartedMessage
        {
            ExecutionId = Guid.Empty, RunId = Guid.Empty, Directory = "/srv/app", Reused = true,
        }, Options);

        Assert.Contains("\"worktree\":false,\"branch\":null,\"head\":null,\"readyAfterMs\":0,\"reused\":true", json);
    }

    [Fact]
    public void A_refusal_carries_its_detail_only_when_it_has_one()
    {
        var updating = JsonSerializer.Serialize(new PtyStartRefusedMessage { PtySessionId = "run-1", Reason = PtyStartRefusedMessage.AgentUpdating }, Options);
        var failed = JsonSerializer.Serialize(new PtyStartRefusedMessage
        {
            PtySessionId = "run-1", Reason = PtyStartRefusedMessage.TestAppFailed, Detail = "No test.start.\nlast line",
        }, Options);

        Assert.Equal("""{"type":"pty.start-refused","ptySessionId":"run-1","reason":"agent-updating"}""", updating);
        Assert.Equal("""{"type":"pty.start-refused","ptySessionId":"run-1","reason":"test-app-failed","detail":"No test.start.\nlast line"}""", failed);
    }

    [Fact]
    public void The_end_of_an_execution_names_it()
    {
        var message = JsonSerializer.Deserialize<IncomingMessage>(
            """{ "type": "test.app.stop", "executionId": "5b0e6f0e-9d1c-4a51-8a7e-0d6a5b1c2d3e" }""", Options);

        Assert.Equal("test.app.stop", message!.Type);
        Assert.Equal("5b0e6f0e-9d1c-4a51-8a7e-0d6a5b1c2d3e", message.ExecutionId);
    }
}

/// <summary>The <c>pty.start</c> of a run of a local test execution whose application cannot be started.</summary>
[Collection(nameof(ConsoleOutputCollection))]
public sealed class LocalTestRunStartTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sidehub-local-test-run-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Without_its_application_the_run_starts_no_terminal_and_leaves_nothing_behind()
    {
        if (OperatingSystem.IsWindows())
            return;
        var agentId = $"test-{Guid.NewGuid():N}";
        var run = Path.Combine(_directory, "run");
        await using var client = new WebSocketClient(
            new AgentConfig { AgentId = agentId, Name = "test" }, new CommandExecutor(_directory), _directory, "test", run);
        var handle = typeof(WebSocketClient).GetMethod("HandlePtyStartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var executionId = Guid.NewGuid();
        var pty = $"run-{Guid.NewGuid():N}";
        var message = new IncomingMessage
        {
            Type = "pty.start", PtySessionId = pty, Shell = "sh",
            AdditionalEnv = new Dictionary<string, string>
            {
                ["SIDEHUB_RUN_KIND"] = "test",
                [TestAppHost.AppVariable] = TestAppHost.LocalApp,
                [TestAppHost.ExecutionVariable] = executionId.ToString(),
                [TestAppHost.UrlVariable] = "http://127.0.0.1:4173",
            },
        };

        var stdout = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(writer));
        try
        {
            await (Task)handle.Invoke(client, [message, CancellationToken.None])!;
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var config = Path.Combine(_directory, ".sidehub", "worktree.json");
        Assert.Contains($"Test app of execution {executionId} failed: No {config}: create it", writer.ToString());
        Assert.Contains($"PTY {pty} not started: the application of its test execution is not ready", writer.ToString());
        var sessions = (ICollection)typeof(WebSocketClient)
            .GetField("_ptySessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Assert.Empty(sessions);
        // Refused before anything of the terminal was set up: no notification FIFO to clean
        Assert.False(Directory.Exists(Path.Combine(run, "fifo", agentId)));
    }
}

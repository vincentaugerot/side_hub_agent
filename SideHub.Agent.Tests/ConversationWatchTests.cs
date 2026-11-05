using System.Collections.Concurrent;
using System.Reflection;
using SideHub.Agent.Models;

namespace SideHub.Agent.Tests;

/// <summary>Captures the process-wide console: never run alongside other tests.</summary>
[CollectionDefinition(nameof(ConsoleOutputCollection), DisableParallelization = true)]
public class ConsoleOutputCollection;

/// <summary>
/// <c>pty.conversation.watch</c> on, off, on in a row (a viewer reconnecting to a tab in Conversation mode): the watch a
/// later message stops before its task started must end quietly, not as "conversation handler failed".
/// </summary>
[Collection(nameof(ConsoleOutputCollection))]
public class ConversationWatchTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sidehub-conversation-watch-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_watch_stopped_right_after_it_started_logs_no_failure()
    {
        await using var client = new WebSocketClient(
            new AgentConfig { AgentId = $"test-{Guid.NewGuid():N}", Name = "test" },
            new CommandExecutor(_directory), _directory, "test", Path.Combine(_directory, "run"));
        var handle = typeof(WebSocketClient).GetMethod("HandleConversationWatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Watch(string pty, bool on) =>
            handle.Invoke(client, [new IncomingMessage { Type = "pty.conversation.watch", PtySessionId = pty, On = on }, CancellationToken.None]);

        var stdout = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(writer));
        try
        {
            for (var i = 0; i < 200; i++)
            {
                Watch($"pty-{i}", on: true);
                Watch($"pty-{i}", on: false);
                Watch($"pty-{i}", on: true);
                Watch($"pty-{i}", on: false);
            }
            Watch("pty-last", on: true);
            Watch("pty-last", on: false);
            Watch("pty-last", on: true);
            // The background tasks of the stopped watches run and end
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            Console.SetOut(stdout);
        }

        Assert.DoesNotContain("handler failed", writer.ToString());
        var watches = (ConcurrentDictionary<string, CancellationTokenSource>)typeof(WebSocketClient)
            .GetField("_conversationWatches", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Assert.Equal(["pty-last"], watches.Keys);
        Assert.False(watches["pty-last"].IsCancellationRequested);
    }
}

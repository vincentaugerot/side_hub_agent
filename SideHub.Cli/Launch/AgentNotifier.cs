using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace SideHub.Cli.Launch;

/// <summary>
/// Writes one JSON line to the agent hosting this terminal, through <c>$SIDEHUB_PTY_NOTIFY_FIFO</c>: a FIFO on
/// Linux and macOS, a named pipe (<c>\\.\pipe\…</c>) on Windows. Best-effort: gives up after a second (opening a
/// FIFO without a reader blocks), and does nothing outside a SideHub terminal. Each line carries the terminal's secret
/// (<c>$SIDEHUB_PTY_NOTIFY_SECRET</c>): the agent drops lines without it, so another terminal can't write in this one's name.
/// </summary>
public static class AgentNotifier
{
    public const string ChannelVariable = "SIDEHUB_PTY_NOTIFY_FIFO";
    public const string SecretVariable = "SIDEHUB_PTY_NOTIFY_SECRET";

    private const string PipePrefix = @"\\.\pipe\";

    /// <summary>Non-ASCII text as is (accents are frequent in prompts): a line stays as long as its text.</summary>
    private static readonly JsonSerializerOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Longest cli-state line sent with a prompt, under the agent's limit (8192) with room for the secret.</summary>
    private const int MaxPromptLine = 7000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <param name="switched">The CLI itself moved to this session (claude's <c>/resume</c>, <c>/clear</c>): the agent
    /// ignores it when it is the session it already follows.</param>
    public static void SessionStarted(string provider, string cliSessionId, bool switched = false)
    {
        if (switched)
            Send(new { @event = "cli-session-started", provider, cliSessionId, switched });
        else
            Send(new { @event = "cli-session-started", provider, cliSessionId });
    }

    public static void Launched(string provider, string cwd, int pid) =>
        Send(new { @event = "cli-launched", provider, cwd, pid });

    /// <param name="cliSessionId">The session announced at launch, null when none was (a new codex session).</param>
    public static void Exited(string provider, string? cliSessionId) =>
        Send(new { @event = "cli-exited", provider, cliSessionId });

    /// <param name="state">working, waiting-input or idle (see <see cref="CliStateCommand"/>).</param>
    /// <param name="cliSessionId">The session the CLI's hook reported, null when it gave none.</param>
    /// <param name="prompt">What a waiting CLI asks (<see cref="CliPrompt"/>), null when unknown.</param>
    public static void State(string provider, string state, string? cliSessionId, JsonObject? prompt = null)
    {
        var notification = JsonSerializer.SerializeToNode(new { @event = "cli-state", provider, state, cliSessionId })!.AsObject();
        if (prompt is not null)
        {
            notification["prompt"] = prompt.DeepClone();
            // The agent drops a line that is too long: then the state goes without its prompt
            if (notification.ToJsonString(LineOptions).Length > MaxPromptLine)
                notification.Remove("prompt");
        }
        Send(notification);
    }

    /// <summary>The CLI's turn stopped on its plan's usage limit (see <see cref="CliStateCommand.LimitReport"/>).</summary>
    public static void Limit(string provider, string? cliSessionId, string error, string? message) =>
        Send(new { @event = "cli-limit", provider, cliSessionId, error, message });

    public static void StepEnded() => Send(new { @event = "run-step-ended" });

    public static void TaskDone() => Send(new { @event = "task-done" });

    private static void Send(object notification)
    {
        var channel = Environment.GetEnvironmentVariable(ChannelVariable);
        if (string.IsNullOrEmpty(channel))
            return;
        var json = notification as JsonObject ?? JsonSerializer.SerializeToNode(notification)!.AsObject();
        json["secret"] = Environment.GetEnvironmentVariable(SecretVariable) ?? "";
        var line = Encoding.UTF8.GetBytes(json.ToJsonString(LineOptions) + "\n");

        var write = Task.Run(() =>
        {
            try
            {
                if (channel.StartsWith(PipePrefix, StringComparison.Ordinal))
                {
                    using var pipe = new NamedPipeClientStream(".", channel[PipePrefix.Length..], PipeDirection.Out,
                        PipeOptions.CurrentUserOnly);
                    pipe.Connect(Timeout);
                    pipe.Write(line);
                }
                else if (File.Exists(channel))
                {
                    using var stream = new FileStream(channel, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    stream.Write(line);
                }
            }
            catch { /* agent gone: nothing to notify */ }
        });
        write.Wait(Timeout + TimeSpan.FromMilliseconds(200));
    }
}

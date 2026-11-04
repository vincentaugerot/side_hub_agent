using System.Globalization;
using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// An agent's task queue: the task it is on and the tasks it chains next, one terminal each. Every command works on the
/// agent of <c>--agent</c>, by default the one this terminal runs on (<c>SIDEHUB_AGENT_ID</c>).
/// </summary>
public static class QueueCommands
{
    private static readonly string[] Clis = ["claude", "codex", "agy", "copilot", "gemini"];

    public static async Task<int> ShowAsync(SideHubApiClient client, string[] args, bool json)
    {
        if (AgentOf(client, args, "Usage: sidehub-cli queue show [--agent <id>]") is not { } agentId)
            return 1;

        var queue = await client.GetTaskQueueAsync(agentId);
        return Print(queue, json);
    }

    public static async Task<int> AddAsync(SideHubApiClient client, string[] args, bool json)
    {
        const string usage = "Usage: sidehub-cli queue add <taskId>... [--cli claude|codex|agy|copilot|gemini] [--prompt \"...\"] [--no-commit] [--agent <id>]";
        var taskIds = Positionals(args, "--agent", "--cli", "--prompt");
        var cli = (GetOption(args, "--cli") ?? "claude").ToLowerInvariant();
        var prompt = GetOption(args, "--prompt");
        // One prompt is the first message of one task: without it, SideHub uses each task's own (TaskPrompt on the backend).
        if (taskIds.Count == 0 || !Clis.Contains(cli) || (prompt is not null && (taskIds.Count > 1 || string.IsNullOrWhiteSpace(prompt))))
        {
            Console.Error.WriteLine(usage);
            if (prompt is not null && taskIds.Count > 1)
                Console.Error.WriteLine("--prompt applies to one task: queue the tasks one by one, or let SideHub use each task's own prompt.");
            return 1;
        }
        if (AgentOf(client, args, usage) is not { } agentId)
            return 1;

        var commit = !args.Contains("--no-commit");
        JsonElement queue = default;
        foreach (var taskId in taskIds)
        {
            queue = await client.EnqueueTaskAsync(agentId, taskId, cli, prompt, commit);
            if (!json)
                Console.WriteLine($"Queued task {taskId} ({cli}{(commit ? "" : ", no commit")}).");
        }

        if (!json)
            Console.WriteLine();
        return Print(queue, json);
    }

    public static async Task<int> RemoveAsync(SideHubApiClient client, string[] args, bool json)
    {
        const string usage = "Usage: sidehub-cli queue remove <itemId> [--agent <id>]";
        var itemId = Positionals(args, "--agent").FirstOrDefault();
        if (itemId is null)
        {
            Console.Error.WriteLine(usage);
            return 1;
        }
        if (AgentOf(client, args, usage) is not { } agentId)
            return 1;

        var queue = await client.RemoveQueuedTaskAsync(agentId, itemId);
        if (!json)
            Console.WriteLine($"Removed {itemId} from the queue.\n");
        return Print(queue, json);
    }

    public static async Task<int> MoveAsync(SideHubApiClient client, string[] args, bool json)
    {
        const string usage = "Usage: sidehub-cli queue move <itemId> --to <index> [--agent <id>]   (index 0 = next to launch)\n"
            + "       sidehub-cli queue move <itemId> --to-agent <agentId> [--cli claude|codex|agy|copilot|gemini] [--agent <id>]"
            + "   (end of the queue of another agent of the same repository)";
        var itemId = Positionals(args, "--agent", "--to", "--to-agent", "--cli").FirstOrDefault();
        var toArg = GetOption(args, "--to");
        var toAgent = GetOption(args, "--to-agent");
        if (itemId is not null && toAgent is not null && toArg is null)
        {
            if (AgentOf(client, args, usage) is not { } sourceAgentId)
                return 1;
            var target = await client.MoveQueuedTaskToAgentAsync(sourceAgentId, itemId, toAgent, GetOption(args, "--cli"));
            if (!json)
                Console.WriteLine($"Moved {itemId} to the end of agent {toAgent}'s queue.\n");
            return Print(target, json);
        }
        if (itemId is null || toArg is null || toAgent is not null || !int.TryParse(toArg, out var index) || index < 0)
        {
            Console.Error.WriteLine(usage);
            return 1;
        }
        if (AgentOf(client, args, usage) is not { } agentId)
            return 1;

        var queue = await client.MoveQueuedTaskAsync(agentId, itemId, index);
        if (!json)
            Console.WriteLine($"Moved {itemId} to position {index}.\n");
        return Print(queue, json);
    }

    public static Task<int> PauseAsync(SideHubApiClient client, string[] args, bool json) => ChangeAsync(client, args, json, "pause");

    public static Task<int> ResumeAsync(SideHubApiClient client, string[] args, bool json) => ChangeAsync(client, args, json, "resume");

    public static Task<int> SkipAsync(SideHubApiClient client, string[] args, bool json) => ChangeAsync(client, args, json, "skip");

    private static async Task<int> ChangeAsync(SideHubApiClient client, string[] args, bool json, string action)
    {
        if (AgentOf(client, args, $"Usage: sidehub-cli queue {action} [--agent <id>]") is not { } agentId)
            return 1;

        var queue = await client.ChangeTaskQueueAsync(agentId, action);
        if (!json)
        {
            Console.WriteLine(action switch
            {
                "pause" => "Queue paused.",
                "resume" => "Queue resumed.",
                _ => "Current task left as it is: the next one launches."
            });
            Console.WriteLine();
        }
        return Print(queue, json);
    }

    private static int Print(JsonElement queue, bool json)
    {
        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(queue));
            return 0;
        }

        var state = Prop(queue, "state") ?? "-";
        if (Prop(queue, "pauseReason") is { } reason)
            state += $" ({reason}{(Prop(queue, "pausedAt") is null ? "" : $", since {PropDate(queue, "pausedAt")}")})";
        Console.WriteLine($"Agent:   {Prop(queue, "agentId")}");
        Console.WriteLine($"State:   {state}");
        if (queue.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.False)
            Console.WriteLine("         This agent's release cannot run a queue: update the agent.");

        if (queue.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"Current: {Prop(current, "taskTitle")} (task {Prop(current, "taskId")}, {Prop(current, "cli")}, " +
                $"terminal {Prop(current, "ptySessionId")}, since {PropDate(current, "startedAt")})");
        }
        else
        {
            Console.WriteLine("Current: none");
        }

        var items = queue.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array ? i.EnumerateArray().ToList() : [];
        if (items.Count == 0)
        {
            Console.WriteLine("Waiting: none");
            return 0;
        }

        Console.WriteLine($"Waiting ({items.Count}):");
        Console.WriteLine($"  {"POS",-4} {"ITEM",-38} {"TASK",-38} {"CLI",-8} {"TITLE"}");
        for (var position = 0; position < items.Count; position++)
        {
            var item = items[position];
            var commit = item.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.False ? " (no commit)" : "";
            Console.WriteLine($"  {position,-4} {Prop(item, "id"),-38} {Prop(item, "taskId"),-38} {Prop(item, "cli"),-8} {Prop(item, "taskTitle")}{commit}");
        }
        return 0;
    }

    private static string? AgentOf(SideHubApiClient client, string[] args, string usage)
    {
        var agentId = GetOption(args, "--agent") ?? client.DefaultAgentId;
        if (agentId is null)
        {
            Console.Error.WriteLine(usage);
            Console.Error.WriteLine("No agent: pass --agent <id> (SIDEHUB_AGENT_ID is not set in this session).");
        }
        return agentId;
    }

    /// <summary>The arguments that are neither a flag nor the value of one of <paramref name="valueFlags"/>.</summary>
    private static List<string> Positionals(string[] args, params string[] valueFlags)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (valueFlags.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith("--")) result.Add(args[i]);
        }
        return result;
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static string PropDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyy-MM-dd HH:mm UTC") : "-";
}

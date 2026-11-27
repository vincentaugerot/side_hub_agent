using System.Text.Json;
using SideHub.Cli.Launch;

namespace SideHub.Cli.Commands;

public static class TaskCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var status = GetOption(args, "--status");
        var result = await client.GetTasksAsync(status);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (!result.TryGetProperty("tasks", out var tasks) || tasks.GetArrayLength() == 0)
        {
            Console.WriteLine("No tasks found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STATUS",-14} {"TYPE",-12} {"TITLE"}");
        Console.WriteLine(new string('-', 100));
        foreach (var task in tasks.EnumerateArray())
        {
            var id = task.GetProperty("id").GetString() ?? "";
            var s = task.TryGetProperty("status", out var sv) ? sv.GetString() ?? "" : "";
            var type = task.TryGetProperty("type", out var tv) ? tv.GetString() ?? "" : "";
            var title = task.TryGetProperty("title", out var ttv) ? ttv.GetString() ?? "" : "";
            Console.WriteLine($"{id,-38} {s,-14} {type,-12} {title}");
        }
        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var title = GetOption(args, "--title");
        var description = GetOption(args, "--description");
        var type = GetOption(args, "--type");
        var repository = GetOption(args, "--repository");

        if (string.IsNullOrEmpty(title))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task create --title \"...\" [--description \"...\"] [--type <type>] [--repository <id|name>]");
            return 1;
        }

        var repositoryId = repository is null ? null : await client.ResolveRepositoryIdAsync(repository);
        var result = await client.CreateTaskAsync(title, description, type, repositoryId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var id = result.TryGetProperty("id", out var i) ? i.GetString() : "";
        Console.WriteLine($"Created task: {id}");
        return 0;
    }

    public static async Task<int> CommentAsync(SideHubApiClient client, string[] args, string? envTaskId, bool json)
    {
        var text = GetOption(args, "--text");
        // taskId: first positional arg (non-flag), or fallback to SIDEHUB_TASK_ID
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--") && a != text) ?? envTaskId;

        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(text))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task comment [<taskId>] --text \"...\"");
            Console.Error.WriteLine("If taskId is omitted, SIDEHUB_TASK_ID is used.");
            return 1;
        }

        var result = await client.AddCommentAsync(taskId, text);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Comment added to task: {taskId}");
        return 0;
    }

    public static async Task<int> BlockerAsync(SideHubApiClient client, string[] args, string? envTaskId, bool json)
    {
        var reason = GetOption(args, "--reason");
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--") && a != reason) ?? envTaskId;

        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(reason))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task blocker [<taskId>] --reason \"...\"");
            Console.Error.WriteLine("If taskId is omitted, SIDEHUB_TASK_ID is used.");
            return 1;
        }

        var result = await client.AddBlockerAsync(taskId, reason);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Blocker added to task: {taskId}");
        return 0;
    }

    public const string UncommittedChangesMessage = "Uncommitted changes: commit your work first, then run task done again.";

    public static async Task<int> DoneAsync(SideHubApiClient client, string[] args, string? envTaskId, bool json)
    {
        var (taskId, summary, openPoint) = ParseDone(args, envTaskId);

        if (string.IsNullOrEmpty(taskId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task done [<taskId>] [--summary \"...\"] [--open-point \"...\"]");
            Console.Error.WriteLine("If taskId is omitted, SIDEHUB_TASK_ID is used.");
            return 1;
        }

        // SIDEHUB_TASK_COMMIT=1: the task queue asked for one commit per task, so the next task starts on a clean tree.
        var directory = Directory.GetCurrentDirectory();
        var commitRequired = Environment.GetEnvironmentVariable("SIDEHUB_TASK_COMMIT") == "1";
        if (await DoneRefusalAsync(directory, commitRequired) is { } refusal)
        {
            Console.Error.WriteLine(refusal);
            return 1;
        }

        var commitSha = await GitWorkTree.HeadAsync(directory);
        var result = await client.CompleteTaskAsync(taskId, summary, commitSha, openPoint);
        // The agent hosting this terminal photographs what the task changed (work review). Best-effort.
        AgentNotifier.TaskDone();

        if (json)
        {
            Console.WriteLine(result.ValueKind == JsonValueKind.Undefined
                ? $"{{\"done\":\"{taskId}\"}}"
                : SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine((commitSha is null ? $"Task done: {taskId}" : $"Task done: {taskId} (commit {commitSha[..Math.Min(7, commitSha.Length)]})")
            + (openPoint is null ? "" : ", with an open point"));
        return 0;
    }

    /// <summary>The task (first positional argument, else <paramref name="envTaskId"/>), the optional summary and the
    /// optional open point (<c>--open-point</c>: done, but a question is left for later, the queue goes on). Option values
    /// are skipped, so a summary or an open point is never taken for the task id.</summary>
    public static (string? TaskId, string? Summary, string? OpenPoint) ParseDone(string[] args, string? envTaskId)
    {
        string? taskId = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--summary" or "--open-point")
                i++; // its value
            else if (!args[i].StartsWith("--"))
                taskId ??= args[i];
        }
        var summary = GetOption(args, "--summary");
        var openPoint = GetOption(args, "--open-point");
        return (taskId ?? envTaskId, string.IsNullOrWhiteSpace(summary) ? null : summary,
            string.IsNullOrWhiteSpace(openPoint) ? null : openPoint.Trim());
    }

    /// <summary>Why <c>task done</c> must not complete the task yet, or null. Only when a commit is required and
    /// <paramref name="directory"/> is a work tree with changes: outside a repository, or when git fails, it goes on.</summary>
    public static async Task<string?> DoneRefusalAsync(string directory, bool commitRequired) =>
        commitRequired && await GitWorkTree.HasUncommittedChangesAsync(directory) == true ? UncommittedChangesMessage : null;

    public static async Task<int> GetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(taskId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task get <taskId>");
            return 1;
        }

        var result = await client.GetTaskAsync(taskId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"ID:          {Prop(result, "id")}");
        Console.WriteLine($"Title:       {Prop(result, "title")}");
        Console.WriteLine($"Status:      {Prop(result, "status")}");
        Console.WriteLine($"Type:        {Prop(result, "type") ?? "-"}");
        var desc = Prop(result, "description");
        if (!string.IsNullOrEmpty(desc))
        {
            Console.WriteLine($"Description:");
            foreach (var line in desc.Split('\n')) Console.WriteLine($"  {line}");
        }
        return 0;
    }

    public static async Task<int> UpdateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var title = GetOption(args, "--title");
        var description = GetOption(args, "--description");
        var type = GetOption(args, "--type");
        var repository = GetOption(args, "--repository");

        if (string.IsNullOrEmpty(taskId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task update <taskId> [--title \"...\"] [--description \"...\"] [--type <type>] [--repository <id|name>]");
            return 1;
        }
        if (title is null && description is null && type is null && repository is null)
        {
            Console.Error.WriteLine("Error: at least one field to update is required.");
            return 1;
        }

        var repositoryId = repository is null ? null : await client.ResolveRepositoryIdAsync(repository);
        var result = await client.UpdateTaskAsync(taskId, title, description, type, repositoryId);

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Updated task: {taskId}");
        return 0;
    }

    public static async Task<int> DeleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(taskId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task delete <taskId> [--yes]");
            return 1;
        }

        if (!args.Contains("--yes"))
        {
            Console.Error.Write($"Delete task {taskId}? Type 'yes' to confirm: ");
            var input = Console.ReadLine();
            if (!string.Equals(input?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Aborted.");
                return 1;
            }
        }

        await client.DeleteTaskAsync(taskId);

        if (json) Console.WriteLine($"{{\"deleted\":\"{taskId}\"}}");
        else Console.WriteLine($"Deleted task: {taskId}");
        return 0;
    }

    public static async Task<int> StatusAsync(SideHubApiClient client, string[] args, bool json)
    {
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var status = GetOption(args, "--status");

        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(status))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task status <taskId> --status <status>");
            return 1;
        }

        var result = await client.SetTaskStatusAsync(taskId, status);

        if (json && result.ValueKind != JsonValueKind.Undefined)
            Console.WriteLine(SideHubApiClient.Serialize(result));
        else
            Console.WriteLine($"Set status of task {taskId} to {status}");
        return 0;
    }

    public static async Task<int> DriveLinkAddAsync(SideHubApiClient client, string[] args, bool json)
    {
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var driveItemId = GetOption(args, "--item");

        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(driveItemId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task drive-link-add <taskId> --item <driveItemId>");
            return 1;
        }

        var result = await client.AddTaskDriveLinkAsync(taskId, driveItemId);

        if (json && result.ValueKind != JsonValueKind.Undefined)
            Console.WriteLine(SideHubApiClient.Serialize(result));
        else
            Console.WriteLine($"Linked drive item {driveItemId} to task {taskId}");
        return 0;
    }

    public static async Task<int> DriveLinkRemoveAsync(SideHubApiClient client, string[] args, bool json)
    {
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var driveItemId = GetOption(args, "--item");

        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(driveItemId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task drive-link-remove <taskId> --item <driveItemId>");
            return 1;
        }

        await client.RemoveTaskDriveLinkAsync(taskId, driveItemId);

        if (json) Console.WriteLine($"{{\"unlinked\":\"{driveItemId}\"}}");
        else Console.WriteLine($"Unlinked drive item {driveItemId} from task {taskId}");
        return 0;
    }

    public static async Task<int> ResolveBlockerAsync(SideHubApiClient client, string[] args, bool json)
    {
        var taskId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(taskId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task resolve-blocker <taskId>");
            return 1;
        }

        await client.ResolveTaskBlockerAsync(taskId);

        if (json) Console.WriteLine($"{{\"resolved\":\"{taskId}\"}}");
        else Console.WriteLine($"Resolved blocker on task: {taskId}");
        return 0;
    }

    public static async Task<int> SearchAsync(SideHubApiClient client, string[] args, bool json)
    {
        var query = GetOption(args, "--query");
        var status = GetOption(args, "--status");
        var type = GetOption(args, "--type");

        if (string.IsNullOrEmpty(query))
        {
            Console.Error.WriteLine("Usage: sidehub-cli task search --query \"...\" [--status <s>] [--type <t>]");
            return 1;
        }

        var result = await client.SearchTasksAsync(query, status, type);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        var tasks = result.ValueKind == JsonValueKind.Array
            ? result
            : (result.TryGetProperty("tasks", out var t) ? t : default);

        if (tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() == 0)
        {
            Console.WriteLine("No matching tasks.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STATUS",-14} {"TYPE",-12} {"TITLE"}");
        Console.WriteLine(new string('-', 100));
        foreach (var task in tasks.EnumerateArray())
        {
            var id = Prop(task, "id") ?? "";
            var s = Prop(task, "status") ?? "";
            var ty = Prop(task, "type") ?? "";
            var title = Prop(task, "title") ?? "";
            Console.WriteLine($"{id,-38} {s,-14} {ty,-12} {title}");
        }
        return 0;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }
}

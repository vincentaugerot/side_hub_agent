using System.Text.Json;
using SideHub.Cli.Launch;

namespace SideHub.Cli.Commands;

public static class WorkflowCommands
{
    public static async Task<int> ListAsync(SideHubApiClient client, string[] args, bool json)
    {
        var result = await client.ListWorkflowsAsync();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            Console.WriteLine("No workflows found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STEPS",-6} {"NAME",-30} {"LAST EXEC",-19} {"STATUS"}");
        Console.WriteLine(new string('-', 110));
        foreach (var w in result.EnumerateArray())
        {
            var id = Prop(w, "id") ?? "";
            var name = Prop(w, "name") ?? "";
            var stepCount = w.TryGetProperty("stepCount", out var sc) ? sc.GetInt32().ToString() : "0";
            var lastAt = PropDate(w, "lastExecutionAt");
            var lastStatus = Prop(w, "lastExecutionStatus") ?? "-";
            Console.WriteLine($"{id,-38} {stepCount,-6} {Truncate(name, 30),-30} {lastAt,-19} {lastStatus}");
        }
        return 0;
    }

    public static async Task<int> GetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow get <id>");
            return 1;
        }

        var result = await client.GetWorkflowAsync(id);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"ID:          {Prop(result, "id")}");
        Console.WriteLine($"Name:        {Prop(result, "name")}");
        Console.WriteLine($"Description: {Prop(result, "description") ?? "-"}");
        var defaultTimeout = result.TryGetProperty("defaultStepTimeoutMinutes", out var dt) && dt.ValueKind == JsonValueKind.Number
            ? dt.GetInt32() + " min" : "-";
        Console.WriteLine($"Default timeout: {defaultTimeout}");
        var skipReview = result.TryGetProperty("skipWorkReview", out var sr) && sr.ValueKind == JsonValueKind.True;
        Console.WriteLine($"Work review: {WorkReviewOption.Describe(skipReview)}");
        if (result.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() > 0)
        {
            Console.WriteLine("Parameters (workflow run --input name=value):");
            foreach (var p in parameters.EnumerateArray())
            {
                var required = p.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True ? ", required" : "";
                var dflt = Prop(p, "default") is { } d ? $", default: {d}" : "";
                var options = p.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                    ? $", one of: {string.Join(" | ", o.EnumerateArray().Select(x => x.GetString()))}" : "";
                Console.WriteLine($"  {Prop(p, "name")} ({Prop(p, "type")}{required}{dflt}{options}): {Prop(p, "label")}");
            }
        }
        foreach (var line in WorkflowRoles.DescribeWorkflowRoles(result, await WorkflowRoles.MemberEmailsAsync(client, result)))
            Console.WriteLine(line);
        Console.WriteLine();
        if (result.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"Steps ({steps.GetArrayLength()}):");
            foreach (var s in steps.EnumerateArray())
            {
                var sid = Prop(s, "id") ?? "";
                var order = s.TryGetProperty("order", out var o) ? o.GetInt32() : 0;
                var name = Prop(s, "name") ?? "";
                var outputPath = Prop(s, "outputPath") ?? "";
                var outputDriveItemId = Prop(s, "outputDriveItemId");
                var timeout = s.TryGetProperty("timeoutMinutes", out var tm) && tm.ValueKind == JsonValueKind.Number
                    ? tm.GetInt32() + " min" : "-";
                Console.WriteLine($"  [{order}] {name} ({sid})");
                Console.WriteLine($"      timeout: {timeout}");
                if (WorkflowRoles.StepRole(s, result) is { } role)
                    Console.WriteLine($"      given to: {role}");
                if (!string.IsNullOrEmpty(outputDriveItemId))
                    Console.WriteLine($"      output → drive item: {outputDriveItemId}");
                else
                    Console.WriteLine($"      output path: {outputPath}");
                if (s.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array && inputs.GetArrayLength() > 0)
                {
                    Console.WriteLine("      inputs:");
                    foreach (var inp in inputs.EnumerateArray())
                    {
                        var t = Prop(inp, "type") ?? "?";
                        if (t == "drive_document")
                            Console.WriteLine($"        - drive: {Prop(inp, "driveItemId")}");
                        else if (t == "previous_step_output")
                            Console.WriteLine($"        - step:  {Prop(inp, "stepId")}");
                        else
                            Console.WriteLine($"        - {t}");
                    }
                }
            }
        }
        return 0;
    }

    public static async Task<int> CreateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var name = GetOption(args, "--name");
        var description = GetOption(args, "--description");
        var timeoutStr = GetOption(args, "--timeout");

        if (string.IsNullOrEmpty(name))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow create --name \"...\" [--description \"...\"] [--timeout <minutes>]");
            return 1;
        }

        int? timeout = null;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            if (!int.TryParse(timeoutStr, out var n) || n <= 0)
            {
                Console.Error.WriteLine("Error: --timeout must be a positive integer (minutes).");
                return 1;
            }
            timeout = n;
        }

        var result = await client.CreateWorkflowAsync(name, description, timeout);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Created workflow: {Prop(result, "id")}");
        return 0;
    }

    public static async Task<int> UpdateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        var name = GetOption(args, "--name");
        var description = GetOption(args, "--description");
        var timeoutStr = GetOption(args, "--timeout");
        var clearTimeout = args.Contains("--clear-timeout");

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow update <id> [--name \"...\"] [--description \"...\"] [--timeout <min> | --clear-timeout] [--work-review on|off]");
            return 1;
        }

        if (!WorkReviewOption.TryParse(GetOption(args, WorkReviewOption.Flag), out var skipWorkReview, out var reviewError))
        {
            Console.Error.WriteLine(reviewError);
            return 1;
        }

        if (name is null && description is null && timeoutStr is null && !clearTimeout && skipWorkReview is null)
        {
            Console.Error.WriteLine("Error: at least one field to update is required.");
            return 1;
        }

        int? timeout = null;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            if (!int.TryParse(timeoutStr, out var n) || n <= 0)
            {
                Console.Error.WriteLine("Error: --timeout must be a positive integer (minutes).");
                return 1;
            }
            timeout = n;
        }

        var result = await client.UpdateWorkflowAsync(id, name, description, timeout, clearTimeout, skipWorkReview);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Updated workflow: {id}");
        return 0;
    }

    public static async Task<int> DeleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow delete <id> [--yes]");
            return 1;
        }

        if (!args.Contains("--yes") && !Confirm($"Delete workflow {id}?"))
            return 1;

        await client.DeleteWorkflowAsync(id);

        if (json) Console.WriteLine($"{{\"deleted\":\"{id}\"}}");
        else Console.WriteLine($"Deleted workflow: {id}");
        return 0;
    }

    public static async Task<int> AddStepAsync(SideHubApiClient client, string[] args, bool json)
    {
        var workflowId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var name = GetOption(args, "--name");
        var prompt = GetOption(args, "--prompt");
        // A step that completes on exit ends with its CLI, its last answer as the result: no document to name.
        var completesOnExit = args.Contains("--completes-on-exit");
        var outputPath = GetOption(args, "--output-path") ?? (completesOnExit ? name ?? "" : "");
        var outputDrive = GetOption(args, "--output-drive");
        var timeoutStr = GetOption(args, "--timeout");

        // Every other step writes its result to a document: SideHub refuses a step without an output path.
        if (string.IsNullOrEmpty(workflowId) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(outputPath))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow add-step <workflowId> --name \"...\" --prompt \"...\" (--output-path \"...\" | --completes-on-exit) [--input-drive <itemId>]* [--input-step <stepId>]* [--output-drive <itemId>] [--timeout <min>]");
            return 1;
        }

        int? timeout = null;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            if (!int.TryParse(timeoutStr, out var n) || n <= 0)
            {
                Console.Error.WriteLine("Error: --timeout must be a positive integer (minutes).");
                return 1;
            }
            timeout = n;
        }

        var inputs = CollectInputs(args);

        var result = await client.AddWorkflowStepAsync(workflowId, name, prompt, inputs, outputPath, timeout, outputDrive,
            completesOnExit: completesOnExit);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        // The API answers with the whole workflow: the new step is the last one.
        var stepId = result.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array && steps.GetArrayLength() > 0
            ? Prop(steps[steps.GetArrayLength() - 1], "id")
            : null;
        Console.WriteLine($"Added step to workflow {workflowId}: {stepId ?? "?"}");
        return 0;
    }

    public static async Task<int> UpdateStepAsync(SideHubApiClient client, string[] args, bool json)
    {
        var positional = args.Where(a => !a.StartsWith("--") && !IsOptionValue(args, a)).ToArray();
        var workflowId = positional.ElementAtOrDefault(0);
        var stepId = positional.ElementAtOrDefault(1);
        var name = GetOption(args, "--name");
        var prompt = GetOption(args, "--prompt");
        var outputPath = GetOption(args, "--output-path");
        var outputDrive = GetOption(args, "--output-drive");
        var timeoutStr = GetOption(args, "--timeout");
        var clearOutputDrive = args.Contains("--clear-output-drive");

        if (string.IsNullOrEmpty(workflowId) || string.IsNullOrEmpty(stepId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow update-step <workflowId> <stepId> [--name] [--prompt] [--input-drive <id>]* [--input-step <id>]* [--output-path] [--output-drive <id> | --clear-output-drive] [--timeout <min>]");
            return 1;
        }

        int? timeout = null;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            if (!int.TryParse(timeoutStr, out var n) || n <= 0)
            {
                Console.Error.WriteLine("Error: --timeout must be a positive integer (minutes).");
                return 1;
            }
            timeout = n;
        }

        // Inputs are only included if at least one input flag was provided
        // (otherwise we leave them unchanged backend-side).
        var hasInputFlags = HasFlag(args, "--input-drive") || HasFlag(args, "--input-step") || args.Contains("--clear-inputs");
        IEnumerable<SideHubApiClient.StepInputArg>? inputs = null;
        if (hasInputFlags)
            inputs = args.Contains("--clear-inputs") ? Enumerable.Empty<SideHubApiClient.StepInputArg>() : CollectInputs(args);

        var result = await client.UpdateWorkflowStepAsync(workflowId, stepId, name, prompt, inputs, outputPath, timeout, outputDrive, clearOutputDrive);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Updated step {stepId} in workflow {workflowId}");
        return 0;
    }

    public static async Task<int> DeleteStepAsync(SideHubApiClient client, string[] args, bool json)
    {
        var positional = args.Where(a => !a.StartsWith("--") && !IsOptionValue(args, a)).ToArray();
        var workflowId = positional.ElementAtOrDefault(0);
        var stepId = positional.ElementAtOrDefault(1);

        if (string.IsNullOrEmpty(workflowId) || string.IsNullOrEmpty(stepId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow delete-step <workflowId> <stepId> [--yes]");
            return 1;
        }

        if (!args.Contains("--yes") && !Confirm($"Delete step {stepId} from workflow {workflowId}?"))
            return 1;

        var result = await client.RemoveWorkflowStepAsync(workflowId, stepId);

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Deleted step {stepId} from workflow {workflowId}");
        return 0;
    }

    public static async Task<int> ReorderStepsAsync(SideHubApiClient client, string[] args, bool json)
    {
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();
        var workflowId = positional.ElementAtOrDefault(0);
        var orderedStepIds = positional.Skip(1).ToArray();

        if (string.IsNullOrEmpty(workflowId) || orderedStepIds.Length < 2)
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow reorder-steps <workflowId> <stepId1> <stepId2> [...]");
            return 1;
        }

        var result = await client.ReorderWorkflowStepsAsync(workflowId, orderedStepIds);

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Reordered {orderedStepIds.Length} steps in workflow {workflowId}");
        return 0;
    }

    public static async Task<int> RunAsync(SideHubApiClient client, string[] args, bool json)
    {
        var workflowId = args.FirstOrDefault(a => !a.StartsWith("--") && !IsOptionValue(args, a));
        var agentId = GetOption(args, "--agent") ?? client.DefaultAgentId;
        var provider = GetOption(args, "--provider") ?? "claude";

        if (string.IsNullOrEmpty(workflowId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow run <workflowId> [--agent <id>] [--provider <p>] [--input key=value]* [--role key=<e-mail>]*");
            return 1;
        }
        if (!TryCollectPairs(args, "--input", out var inputs, out var inputError))
        {
            Console.Error.WriteLine($"Error: {inputError}");
            return 1;
        }
        if (!WorkflowRoles.TryCollect(args, out var roleHolders, out var roleError))
        {
            Console.Error.WriteLine($"Error: {roleError}");
            return 1;
        }
        if (string.IsNullOrEmpty(agentId))
        {
            Console.Error.WriteLine("Error: --agent <id> is required (or set SIDEHUB_AGENT_ID).");
            return 1;
        }

        var result = await client.RunWorkflowAsync(workflowId, agentId, provider, inputs, roleHolders);

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Started workflow execution: {Prop(result, "id") ?? Prop(result, "executionId") ?? "?"}");
        return 0;
    }

    public static async Task<int> ExecutionGetAsync(SideHubApiClient client, string[] args, bool json)
    {
        var execId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(execId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow execution-get <executionId>");
            return 1;
        }

        var result = await client.GetWorkflowExecutionAsync(execId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Execution: {Prop(result, "id")}");
        Console.WriteLine($"Workflow:  {Prop(result, "workflowName")} ({Prop(result, "workflowId")})");
        Console.WriteLine($"Status:    {Prop(result, "status")}");
        Console.WriteLine($"Started:   {PropDate(result, "startedAt")}");
        Console.WriteLine($"Completed: {PropDate(result, "completedAt")}");
        var err = Prop(result, "errorMessage");
        if (!string.IsNullOrEmpty(err)) Console.WriteLine($"Error:     {err}");
        var holders = WorkflowRoles.DescribeExecutionHolders(result).ToList();
        if (holders.Count > 0)
        {
            Console.WriteLine();
            foreach (var line in holders)
                Console.WriteLine(line);
        }

        if (result.TryGetProperty("stepExecutions", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine();
            Console.WriteLine("Steps:");
            foreach (var s in steps.EnumerateArray())
            {
                var order = s.TryGetProperty("order", out var o) ? o.GetInt32() : 0;
                var name = Prop(s, "stepName") ?? "";
                var status = Prop(s, "status") ?? "";
                var output = Prop(s, "outputDriveItemId");
                var role = Prop(s, "role") is { Length: > 0 } r ? $" [{r}]" : "";
                Console.WriteLine($"  [{order}] {name}{role}: {status}{(string.IsNullOrEmpty(output) ? "" : $" → drive {output}")}");
                if (s.TryGetProperty("approval", out var approval) && Prop(approval, "decision") is { } decision)
                    Console.WriteLine($"      {decision} by {Prop(approval, "decidedByEmail") ?? "?"}{WorkflowRoles.OnBehalfOf(approval)}");
                if (Prop(s, "validatedByEmail") is { } validatedBy)
                    Console.WriteLine($"      validated by {validatedBy}{WorkflowRoles.OnBehalfOf(s, "validatedOnBehalfOfRole")}");
            }
        }
        return 0;
    }

    public static async Task<int> ExecutionsAsync(SideHubApiClient client, string[] args, bool json)
    {
        var wfId = args.FirstOrDefault(a => !a.StartsWith("--"));
        var limitArg = GetOption(args, "--limit");
        var limit = 20;
        if (string.IsNullOrEmpty(wfId) || (limitArg is not null && (!int.TryParse(limitArg, out limit) || limit < 1)))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow executions <wfId> [--limit N]");
            return 1;
        }

        var result = await client.GetWorkflowExecutionsAsync(wfId, limit);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            Console.WriteLine("No executions found.");
            return 0;
        }

        Console.WriteLine($"{"ID",-38} {"STATUS",-18} {"TRIGGER",-10} {"STARTED",-22} {"COMPLETED",-22} {"STEPS"}");
        Console.WriteLine(new string('-', 125));
        foreach (var e in result.EnumerateArray())
        {
            var steps = e.TryGetProperty("stepExecutions", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().ToList() : [];
            var done = steps.Count(step => Prop(step, "status") is "completed" or "skipped");
            Console.WriteLine($"{Prop(e, "id"),-38} {Prop(e, "status"),-18} {Prop(e, "trigger"),-10} " +
                $"{PropDate(e, "startedAt"),-22} {PropDate(e, "completedAt"),-22} {done}/{steps.Count}");
            if (Prop(e, "errorMessage") is { } error)
                Console.WriteLine($"  {Truncate(error.ReplaceLineEndings(" "), 120)}");
        }
        return 0;
    }

    public static async Task<int> ExecutionRunsAsync(SideHubApiClient client, string[] args, bool json)
    {
        var execId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(execId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow execution-runs <executionId>");
            return 1;
        }

        var runs = await client.GetWorkflowExecutionRunsAsync(execId);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(runs));
            return 0;
        }

        if (runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() == 0)
        {
            Console.WriteLine("No runs for this execution yet.");
            return 0;
        }

        // Runs only carry their step id: name them from the execution.
        var execution = await client.GetWorkflowExecutionAsync(execId);
        var stepNames = execution.TryGetProperty("stepExecutions", out var steps) && steps.ValueKind == JsonValueKind.Array
            ? steps.EnumerateArray()
                .Where(step => Prop(step, "stepId") is not null)
                .ToDictionary(step => Prop(step, "stepId")!, step => Prop(step, "stepName") ?? "")
            : [];

        Console.WriteLine($"{"STEP",-30} {"TRY",-4} {"RUN",-38} {"STATUS",-10} {"END REASON",-17} {"CLI",-8} {"STARTED"}");
        Console.WriteLine(new string('-', 125));
        foreach (var run in runs.EnumerateArray())
        {
            var step = Prop(run, "stepId") is { } stepId && stepNames.TryGetValue(stepId, out var name) ? name : "-";
            var attempt = run.TryGetProperty("attempt", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 1;
            Console.WriteLine($"{Truncate(step, 30),-30} {attempt,-4} {Prop(run, "id"),-38} {Prop(run, "status"),-10} " +
                $"{Prop(run, "endReason") ?? "-",-17} {Prop(run, "runtime"),-8} {PropDate(run, "startedAt")}");
        }
        Console.WriteLine();
        Console.WriteLine("Details of a run (exit code, last output, usage): sidehub-cli run get <runId>");
        return 0;
    }

    public static async Task<int> ExportAsync(SideHubApiClient client, string[] args, bool json)
    {
        var wfId = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(wfId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow export <wfId> [--output <file>]");
            return 1;
        }

        var document = SideHubApiClient.Serialize(await client.ExportWorkflowAsync(wfId));
        if (GetOption(args, "--output") is not { } output)
        {
            Console.WriteLine(document);
            return 0;
        }

        await File.WriteAllTextAsync(output, document + Environment.NewLine);
        Console.WriteLine(json ? $"{{\"output\":{JsonSerializer.Serialize(Path.GetFullPath(output))}}}" : $"Exported to {Path.GetFullPath(output)}");
        return 0;
    }

    public static async Task<int> ImportAsync(SideHubApiClient client, string[] args, bool json)
    {
        var file = GetOption(args, "--file");
        if (string.IsNullOrEmpty(file))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow import --file <path> [--name \"...\"]");
            return 1;
        }
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"Error: file not found: {file}");
            return 1;
        }

        JsonElement document;
        try
        {
            document = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(file));
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Error: {file} is not JSON ({ex.Message}). Write it with: sidehub-cli workflow export <wfId> --output <file>");
            return 1;
        }
        if (document.ValueKind != JsonValueKind.Object)
        {
            Console.Error.WriteLine($"Error: {file} is not a workflow document. Write it with: sidehub-cli workflow export <wfId> --output <file>");
            return 1;
        }

        var result = await client.ImportWorkflowAsync(document, GetOption(args, "--name"));

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Imported workflow: {Prop(result, "name")} ({Prop(result, "id")})");
        return 0;
    }

    public static async Task<int> DuplicateAsync(SideHubApiClient client, string[] args, bool json)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith("--") && !IsOptionValue(args, a));
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow duplicate <id> [--name \"...\"]");
            return 1;
        }

        // The name the web app gives a copy.
        var name = GetOption(args, "--name") ?? $"{Prop(await client.GetWorkflowAsync(id), "name")} (copy)";
        var result = await client.DuplicateWorkflowAsync(id, name);

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Duplicated workflow: {Prop(result, "name")} ({Prop(result, "id")})");
        return 0;
    }

    public static async Task<int> CancelAsync(SideHubApiClient client, string[] args, bool json)
    {
        var execId = args.FirstOrDefault(a => !a.StartsWith("--") && !IsOptionValue(args, a));
        if (string.IsNullOrEmpty(execId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow cancel <executionId> [--reason \"...\"]");
            return 1;
        }

        var result = await client.CancelWorkflowExecutionAsync(execId, GetOption(args, "--reason"));

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Execution {execId} canceled.");
        return 0;
    }

    public static async Task<int> RetryAsync(SideHubApiClient client, string[] args, bool json)
    {
        var ids = args.Where(a => !a.StartsWith("--")).ToList();
        if (ids.Count < 2)
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow retry <executionId> <stepId>");
            return 1;
        }

        var result = await client.RetryWorkflowFromStepAsync(ids[0], ids[1]);

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Execution {ids[0]} runs again from step {ids[1]}.");
        return 0;
    }

    /// <summary>Ends a step by hand: <c>complete</c> with the Memory page it should have written, or <c>fail</c>.</summary>
    public static async Task<int> StepOverrideAsync(SideHubApiClient client, string[] args, bool json)
    {
        var positionals = args.Where(a => !a.StartsWith("--") && !IsOptionValue(args, a)).ToList();
        var outputId = GetOption(args, "--output-id");
        var outcome = positionals.ElementAtOrDefault(2);
        if (positionals.Count < 3 || outcome is not ("complete" or "fail") || (outcome == "complete" && string.IsNullOrEmpty(outputId)))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow step-override <executionId> <stepId> complete --output-id <driveItemId>");
            Console.Error.WriteLine("       sidehub-cli workflow step-override <executionId> <stepId> fail [--reason \"...\"]");
            return 1;
        }

        var (execId, stepId) = (positionals[0], positionals[1]);
        var result = outcome == "complete"
            ? await client.ManualCompleteWorkflowStepAsync(execId, stepId, outputId!)
            : await client.ManualFailWorkflowStepAsync(execId, stepId, GetOption(args, "--reason") ?? "Failed manually");

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine(outcome == "complete"
            ? $"Step {stepId} completed by hand (output: {outputId})."
            : $"Step {stepId} failed by hand.");
        return 0;
    }

    /// <summary>Validates an interactive step: its CLI waited for the user, the workflow moves on.</summary>
    public static async Task<int> ValidateStepAsync(SideHubApiClient client, string[] args, bool json)
    {
        var ids = args.Where(a => !a.StartsWith("--")).ToList();
        if (ids.Count < 2)
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow validate-step <executionId> <stepId>");
            return 1;
        }

        var result = await client.ValidateWorkflowStepAsync(ids[0], ids[1]);

        if (json) Console.WriteLine(SideHubApiClient.Serialize(result));
        else Console.WriteLine($"Step {ids[1]} validated: its CLI is asked to finish and report.");
        return 0;
    }

    private static List<SideHubApiClient.StepInputArg> CollectInputs(string[] args)
    {
        var inputs = new List<SideHubApiClient.StepInputArg>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--input-drive") inputs.Add(new SideHubApiClient.StepInputArg("drive", args[i + 1]));
            else if (args[i] == "--input-step") inputs.Add(new SideHubApiClient.StepInputArg("step", args[i + 1]));
        }
        return inputs;
    }

    /// <summary>
    /// Collects every <c>flag key=value</c> pair (repeatable; the value may itself contain '='). A key given twice keeps
    /// its last value.
    /// </summary>
    public static bool TryCollectPairs(string[] args, string flag, out Dictionary<string, string> pairs, out string? error)
    {
        pairs = new Dictionary<string, string>();
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != flag) continue;
            var pair = i + 1 < args.Length ? args[i + 1] : "";
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                error = $"{flag} expects key=value (got '{pair}').";
                return false;
            }
            pairs[pair[..separator].Trim()] = pair[(separator + 1)..];
            i++;
        }
        return true;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return true;
        return false;
    }

    private static bool IsOptionValue(string[] args, string token)
    {
        // token is at some index; if the previous token is a flag, this token is its value
        var idx = Array.IndexOf(args, token);
        if (idx <= 0) return false;
        var prev = args[idx - 1];
        return prev.StartsWith("--");
    }

    private static bool Confirm(string question)
    {
        Console.Error.Write($"{question} Type 'yes' to confirm: ");
        var input = Console.ReadLine();
        if (!string.Equals(input?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Aborted.");
            return false;
        }
        return true;
    }

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string PropDate(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return "-";
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "-";
        var s = v.GetString();
        return DateTime.TryParse(s, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm UTC") : (s ?? "-");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";

    public static async Task<int> StepCompleteAsync(SideHubApiClient client, string[] args, bool json)
    {
        var executionId = Environment.GetEnvironmentVariable("SIDEHUB_WORKFLOW_EXECUTION_ID");
        var stepId = Environment.GetEnvironmentVariable("SIDEHUB_WORKFLOW_STEP_ID");

        if (string.IsNullOrEmpty(executionId) || string.IsNullOrEmpty(stepId))
        {
            Console.Error.WriteLine("Error: SIDEHUB_WORKFLOW_EXECUTION_ID and SIDEHUB_WORKFLOW_STEP_ID must be set.");
            Console.Error.WriteLine("This command can only run inside a workflow step.");
            return 1;
        }

        var outputId = GetOption(args, "--output-id");
        if (string.IsNullOrEmpty(outputId))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow step-complete --output-id <guid> [--value key=value]*");
            return 1;
        }
        if (!TryCollectPairs(args, "--value", out var outputs, out var valueError))
        {
            Console.Error.WriteLine($"Error: {valueError}");
            return 1;
        }

        var result = await client.CompleteWorkflowStepAsync(executionId, stepId, outputId, outputs);
        NotifyAgentStepEnded();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine(outputs.Count == 0
            ? $"Workflow step {stepId} completed (output: {outputId})"
            : $"Workflow step {stepId} completed (output: {outputId}, values: {string.Join(", ", outputs.Keys)})");
        return 0;
    }

    public static async Task<int> StepFailAsync(SideHubApiClient client, string[] args, bool json)
    {
        var executionId = Environment.GetEnvironmentVariable("SIDEHUB_WORKFLOW_EXECUTION_ID");
        var stepId = Environment.GetEnvironmentVariable("SIDEHUB_WORKFLOW_STEP_ID");

        if (string.IsNullOrEmpty(executionId) || string.IsNullOrEmpty(stepId))
        {
            Console.Error.WriteLine("Error: SIDEHUB_WORKFLOW_EXECUTION_ID and SIDEHUB_WORKFLOW_STEP_ID must be set.");
            Console.Error.WriteLine("This command can only run inside a workflow step.");
            return 1;
        }

        // Reason is the first non-flag positional arg.
        var reason = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrEmpty(reason))
        {
            Console.Error.WriteLine("Usage: sidehub-cli workflow step-fail \"<reason>\"");
            return 1;
        }

        var result = await client.FailWorkflowStepAsync(executionId, stepId, reason);
        NotifyAgentStepEnded();

        if (json)
        {
            Console.WriteLine(SideHubApiClient.Serialize(result));
            return 0;
        }

        Console.WriteLine($"Workflow step {stepId} marked as failed: {reason}");
        return 0;
    }

    /// <summary>
    /// Tells the agent hosting this PTY that the step is over, so it reports the run's token usage
    /// while the CLI may still be open. Best-effort (see <see cref="AgentNotifier"/>).
    /// </summary>
    private static void NotifyAgentStepEnded() => AgentNotifier.StepEnded();

    private static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }
}

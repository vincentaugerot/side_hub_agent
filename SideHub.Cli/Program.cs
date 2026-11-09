using SideHub.Cli;
using SideHub.Cli.Commands;
using SideHub.Cli.Launch;

// Starting a coding CLI talks to the agent only, never to the API: no SideHub session is needed.
if (args.Length > 0 && args[0] == "launch")
    return LaunchCommand.Run(args[1..]);
// Run by a coding CLI's hooks to report its state to the agent: same, and it must never fail the CLI.
if (args.Length > 0 && args[0] == CliStateCommand.Name)
    return CliStateCommand.Run(args[1..]);
// Run by claude's PreToolUse hook when the run has a tool policy: it reads its own environment and always exits 0.
if (args.Length == 2 && args[0] == PolicyCheckCommand.Domain && args[1] == PolicyCheckCommand.Action)
    return await PolicyCheckCommand.RunAsync();
// Run by claude's PreToolUse hook on MCP tools when a permissionless run may use the machine's: always allows, exits 0.
if (args.Length == 2 && args[0] == PolicyCheckCommand.Domain && args[1] == PolicyAllowCommand.Action)
    return PolicyAllowCommand.Run();

var apiUrl = Environment.GetEnvironmentVariable("SIDEHUB_API_URL");
var agentToken = Environment.GetEnvironmentVariable("SIDEHUB_AGENT_TOKEN");
var workspaceId = Environment.GetEnvironmentVariable("SIDEHUB_WORKSPACE_ID");
var taskId = Environment.GetEnvironmentVariable("SIDEHUB_TASK_ID");
var defaultAgentId = Environment.GetEnvironmentVariable("SIDEHUB_AGENT_ID");
var pipelineMode = Environment.GetEnvironmentVariable("SIDEHUB_PIPELINE_MODE");

// Inside a SideHub terminal the token is the one the backend scoped to that PTY; the agent's own token is
// never exposed there. No token means the terminal was opened without one (older backend).
if (string.IsNullOrEmpty(agentToken) && !string.IsNullOrEmpty(apiUrl))
{
    Console.Error.WriteLine("sidehub-cli is unavailable in this terminal: it has no SideHub session token (SIDEHUB_AGENT_TOKEN). Open a new terminal from SideHub.");
    return 1;
}

if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(agentToken) || string.IsNullOrEmpty(workspaceId))
{
    Console.Error.WriteLine("Missing required environment variables: SIDEHUB_API_URL, SIDEHUB_AGENT_TOKEN, SIDEHUB_WORKSPACE_ID");
    return 1;
}

if (ApiUrlPolicy.RejectionReason(apiUrl) is { } apiUrlProblem)
{
    Console.Error.WriteLine($"Invalid SIDEHUB_API_URL: '{apiUrl}': {apiUrlProblem}");
    return 1;
}

// sh_agent_: workspace agent token (manual use outside SideHub terminals); sh_run_: ephemeral token of a
// backend-launched run; sh_pty_: session token of an interactive SideHub terminal.
if (!agentToken.StartsWith("sh_agent_") && !agentToken.StartsWith("sh_run_") && !agentToken.StartsWith("sh_pty_"))
{
    Console.Error.WriteLine("Invalid SIDEHUB_AGENT_TOKEN format: must start with 'sh_agent_', 'sh_run_' or 'sh_pty_'.");
    return 1;
}

// `whoami` is the one command without an action.
var isWhoami = args.Length > 0 && args[0] == "whoami";
if (args.Length < 2 && !isWhoami)
{
    Console.Error.WriteLine("Usage: sidehub-cli <domain> <action> [options]");
    Console.Error.WriteLine("       sidehub-cli launch [--prompt-env | --prompt-base64 <b64>] <claude|codex|agy|copilot|gemini> [args...]");
    Console.Error.WriteLine("       sidehub-cli whoami [--json]");
    Console.Error.WriteLine("Domains: cockpit, run, drive, sqlite, table, task, repository, automation, scheduler, workflow, review, approval, queue, outgoing, secret, mcp");
    Console.Error.WriteLine("  cockpit now");
    Console.Error.WriteLine("  cockpit usage [--since <30d|date>] [--until <date>] [--by day|workspace|repository|agent|model|user|origin] [--split origin|workspace|provider] [--tz <IANA zone>]");
    Console.Error.WriteLine("  cockpit reliability [--since <7d|date>] [--until <date>]");
    Console.Error.WriteLine("  run list [--status <status>] [--runtime <cli>] [--agent <id>] [--since <24h|7d|date>] [--until <date>] [--limit N] [--page N]");
    Console.Error.WriteLine("  run get <runId>");
    Console.Error.WriteLine("  drive list [--parent <id>]");
    Console.Error.WriteLine("  drive read <pageId>");
    Console.Error.WriteLine("  drive download <pageId> [--output <path>] [--stdout] [--url-only]");
    Console.Error.WriteLine("  drive create --title \"...\" [--content \"...\" | --file <path>] [--parent <id>] [--type page|spreadsheet|folder]");
    Console.Error.WriteLine("  drive update <pageId> [--title \"...\"] [--content \"...\" | --file <path>]");
    Console.Error.WriteLine("  drive delete <id> [--yes]");
    Console.Error.WriteLine("  drive move <id> --parent <newParentId|root> [--after <siblingId>]");
    Console.Error.WriteLine("  drive mkdir --title \"...\" [--parent <id>]");
    Console.Error.WriteLine("  drive upload <localPath> [--parent <id>] [--name \"...\"] [--markdown]");
    Console.Error.WriteLine("  drive recent [--limit N]");
    Console.Error.WriteLine("  drive usage");
    Console.Error.WriteLine("  drive archived");
    Console.Error.WriteLine("  drive restore <id>");
    Console.Error.WriteLine("  drive search <query>");
    Console.Error.WriteLine("  drive create-json --title \"file.json\" [--content '<json>' | --file <path>] [--parent <id>]");
    Console.Error.WriteLine("  drive query <id> --path \"$.foo.bar\" [--raw]");
    Console.Error.WriteLine("  drive patch <id> [--set <pointer>=<value>]* [--delete <pointer>]* [--ops-file <path>]");
    Console.Error.WriteLine("  sqlite query <itemId> --sql \"SELECT ...\" [--param V]* [--row-limit N] [--timeout SEC] [--json]");
    Console.Error.WriteLine("  sqlite exec <itemId> --sql \"INSERT/UPDATE/DELETE\" [--param V]* [--allow-ddl] [--timeout SEC] [--json]");
    Console.Error.WriteLine("  sqlite schema <itemId> [--json]");
    Console.Error.WriteLine("  sqlite create --title \"name\" [--schema \"CREATE TABLE ...\" | --schema-file <path>] [--parent <id>] [--json]");
    Console.Error.WriteLine("  table show <pageId> [--json]");
    Console.Error.WriteLine("  table append-row <pageId> --col \"Name=Value\" [--col ...]");
    Console.Error.WriteLine("  table set-cell <pageId> --row <rowId> --col <colName> --value \"...\"");
    Console.Error.WriteLine("  table delete-row <pageId> --row <rowId>");
    Console.Error.WriteLine("  table add-column <pageId> --name \"...\" [--type text|image|dropdown] [--options \"val=Label,val2=Label 2\"]");
    Console.Error.WriteLine("  task list [--status <status>]");
    Console.Error.WriteLine("  task get <taskId>");
    Console.Error.WriteLine("  task create --title \"...\" [--description \"...\"] [--type <type>] [--repository <id|name>]");
    Console.Error.WriteLine("  task update <taskId> [--title \"...\"] [--description \"...\"] [--type <type>] [--repository <id|name>]");
    Console.Error.WriteLine("  task delete <taskId> [--yes]");
    Console.Error.WriteLine("  task status <taskId> --status <status>");
    Console.Error.WriteLine("  task comment [<taskId>] --text \"...\"");
    Console.Error.WriteLine("  task blocker [<taskId>] --reason \"...\"");
    Console.Error.WriteLine("  task done [<taskId>] [--summary \"...\"]");
    Console.Error.WriteLine("  task resolve-blocker <taskId>");
    Console.Error.WriteLine("  task drive-link-add <taskId> --item <driveItemId>");
    Console.Error.WriteLine("  task drive-link-remove <taskId> --item <driveItemId>");
    Console.Error.WriteLine("  task search --query \"...\" [--status <s>] [--type <t>]");
    Console.Error.WriteLine("  repository list");
    Console.Error.WriteLine("  repository ask <repository id|name> \"question\" [--no-wait] [--timeout <minutes>]");
    Console.Error.WriteLine("  repository question <questionId>");
    Console.Error.WriteLine("  repository questions [--limit N]");
    Console.Error.WriteLine("  automation list");
    Console.Error.WriteLine("  automation get <id>");
    Console.Error.WriteLine("  automation create --name \"...\" --prompt \"...\" [--cron \"...\" [--description \"...\"]] [--agent <id>] [--provider <cli>] [--model <model>]");
    Console.Error.WriteLine("  automation run <id> [--agent <id>] [--provider <p>] [--input key=value]* [--role key=<e-mail>]*");
    Console.Error.WriteLine("  automation pause <id> | resume <id>");
    Console.Error.WriteLine("  automation delete <id> [--yes]");
    Console.Error.WriteLine("  automation schedule add <id> --cron \"...\" [--description \"...\"] [--agent <id>]");
    Console.Error.WriteLine("  automation schedule update <scheduleId> [--cron] [--description] [--agent] | remove <scheduleId> [--yes]");
    Console.Error.WriteLine("  automation executions <id> [--limit N]");
    Console.Error.WriteLine("  (an automation is a workflow and its schedules are schedulers: the workflow and scheduler commands below act on them too)");
    Console.Error.WriteLine("  scheduler list [--active | --paused]");
    Console.Error.WriteLine("  scheduler get <id>");
    Console.Error.WriteLine("  scheduler create --title \"...\" (--prompt \"...\" | --workflow <id>) --cron \"...\" [--agent <id>] [--description \"...\"] [--provider <provider>]");
    Console.Error.WriteLine("  scheduler update <id> [--title] [--prompt | --workflow <id>] [--cron] [--description] [--agent] [--provider] [--work-review on|off]");
    Console.Error.WriteLine("  scheduler delete <id> [--yes]");
    Console.Error.WriteLine("  scheduler pause <id>");
    Console.Error.WriteLine("  scheduler resume <id>");
    Console.Error.WriteLine("  scheduler trigger <id>");
    Console.Error.WriteLine("  scheduler executions <id>");
    Console.Error.WriteLine("  workflow list");
    Console.Error.WriteLine("  workflow get <id>");
    Console.Error.WriteLine("  workflow create --name \"...\" [--description \"...\"] [--timeout <min>]");
    Console.Error.WriteLine("  workflow update <id> [--name] [--description] [--timeout <min> | --clear-timeout] [--work-review on|off]");
    Console.Error.WriteLine("  workflow delete <id> [--yes]");
    Console.Error.WriteLine("  workflow add-step <wfId> --name \"...\" --prompt \"...\" (--output-path \"...\" | --completes-on-exit) [--input-drive <itemId>]* [--input-step <stepId>]* [--output-drive <itemId>] [--timeout <min>]");
    Console.Error.WriteLine("  workflow update-step <wfId> <stepId> [--name] [--prompt] [--input-drive]* [--input-step]* [--clear-inputs] [--output-path] [--output-drive | --clear-output-drive] [--timeout]");
    Console.Error.WriteLine("  workflow delete-step <wfId> <stepId> [--yes]");
    Console.Error.WriteLine("  workflow reorder-steps <wfId> <stepId1> <stepId2> [...]");
    Console.Error.WriteLine("  workflow run <wfId> [--agent <id>] [--provider <p>] [--input key=value]* [--role key=<e-mail>]*");
    Console.Error.WriteLine("  workflow executions <wfId> [--limit N]");
    Console.Error.WriteLine("  workflow execution-get <executionId>");
    Console.Error.WriteLine("  workflow execution-runs <executionId>");
    Console.Error.WriteLine("  workflow export <wfId> [--output <file>]");
    Console.Error.WriteLine("  workflow import --file <path> [--name \"...\"]");
    Console.Error.WriteLine("  workflow duplicate <wfId> [--name \"...\"]");
    Console.Error.WriteLine("  workflow cancel <executionId> [--reason \"...\"]");
    Console.Error.WriteLine("  workflow retry <executionId> <stepId>");
    Console.Error.WriteLine("  workflow step-override <executionId> <stepId> complete --output-id <driveItemId>");
    Console.Error.WriteLine("  workflow step-override <executionId> <stepId> fail [--reason \"...\"]");
    Console.Error.WriteLine("  workflow validate-step <executionId> <stepId>");
    Console.Error.WriteLine("  workflow step-complete --output-id <guid> [--value key=value]*");
    Console.Error.WriteLine("  workflow step-fail \"<reason>\"");
    Console.Error.WriteLine("  workflow triggers <wfId>");
    Console.Error.WriteLine("  workflow trigger-add <wfId> --type drive.item.created|task.status_changed|workflow.completed [--agent <id>] [--provider <p>] [--folder <id> [--subfolders] | --to-status completed|in-progress | --source-workflow <id> | --filter-file <json>] [--map param=path]*");
    Console.Error.WriteLine("  workflow trigger-update <wfId> <triggerId> [--active true|false] [--agent <id>] [--provider <p>] [--folder <id> [--subfolders] | --to-status <s> | --source-workflow <id> | --filter-file <json>] [--map param=path]* [--clear-map]");
    Console.Error.WriteLine("  workflow trigger-delete <wfId> <triggerId> [--yes]");
    Console.Error.WriteLine("  review list [--status pending|validated|fix-requested|dismissed] [--task <id>] [--run <id>] [--limit N]");
    Console.Error.WriteLine("  review get <reviewId>");
    Console.Error.WriteLine("  review diff <reviewId> [--round N] [--path <file>]");
    Console.Error.WriteLine("  review decide <reviewId> validate|dismiss [--comment \"...\"]");
    Console.Error.WriteLine("  review decide <reviewId> request-fix --comment \"...\"");
    Console.Error.WriteLine("  review peer <reviewId> [--round N] --cli claude|codex [--model <model>]");
    Console.Error.WriteLine("  approval list");
    Console.Error.WriteLine("  approval approve <executionId> <stepId> [--comment \"...\"]");
    Console.Error.WriteLine("  approval reject <executionId> <stepId> [--comment \"...\"]");
    Console.Error.WriteLine("  approval request-changes <executionId> <stepId> --comment \"...\"");
    Console.Error.WriteLine("  approval tool approve|deny <toolApprovalId> [--comment \"...\"]");
    Console.Error.WriteLine("  queue show [--agent <id>]");
    Console.Error.WriteLine("  queue add <taskId>... [--cli claude|codex|agy|copilot|gemini] [--prompt \"...\"] [--no-commit] [--agent <id>]");
    Console.Error.WriteLine("  queue remove <itemId> [--agent <id>]");
    Console.Error.WriteLine("  queue move <itemId> --to <index> [--agent <id>]");
    Console.Error.WriteLine("  queue move <itemId> --to-agent <agentId> [--cli claude|codex|agy|copilot|gemini] [--agent <id>]");
    Console.Error.WriteLine("  queue pause|resume|skip [--agent <id>]");
    Console.Error.WriteLine("  outgoing show [--agent <id>]");
    Console.Error.WriteLine("  outgoing push [--agent <id>]");
    Console.Error.WriteLine("  secret list");
    Console.Error.WriteLine("  mcp list");
    Console.Error.WriteLine("  mcp set <name> --command <cmd> [--arg <a>]* [--env KEY=VALUE]* [--secret NAME]* [--secrets-file dotenv|raw] [--description \"...\"]");
    Console.Error.WriteLine("  mcp set <name> --url <url> [--header KEY=VALUE]* [--description \"...\"]");
    Console.Error.WriteLine("  mcp set <name> --file <json>   (values reference workspace secrets as ${NAME}, never a literal secret)");
    Console.Error.WriteLine("  mcp delete <name> [--yes]");
    return 1;
}

var domain = args[0].ToLowerInvariant();
var action = isWhoami ? "" : args[1].ToLowerInvariant();
var restArgs = isWhoami ? args[1..] : args[2..];
var jsonOutput = restArgs.Contains("--json");

// Defense-in-depth: block write commands in plan mode
var writeActions = new HashSet<string>
{
    "create", "update", "comment", "blocker", "done", "delete", "pause", "resume", "trigger",
    "append-row", "set-cell", "delete-row", "add-column",
    "move", "mkdir", "upload", "restore",
    "add-step", "update-step", "delete-step", "reorder-steps", "run",
    "status", "drive-link-add", "drive-link-remove", "resolve-blocker",
    "exec",
    "import", "duplicate", "cancel", "retry", "step-override", "validate-step",
    "decide", "peer", "approve", "reject", "request-changes", "tool",
    "add", "remove", "skip", "push",
    "ask", "set", "trigger-add", "trigger-update", "trigger-delete"
};
if (pipelineMode == "plan" && writeActions.Contains(action))
{
    Console.Error.WriteLine($"Error: write command '{action}' is not allowed in plan mode");
    return 1;
}

using var client = new SideHubApiClient(apiUrl, agentToken, workspaceId, defaultAgentId);

try
{
    return (domain, action) switch
    {
        ("whoami", "") => await CockpitCommands.WhoamiAsync(client, jsonOutput),
        ("cockpit", "now") => await CockpitCommands.NowAsync(client, jsonOutput),
        ("cockpit", "usage") => await CostCommands.UsageAsync(client, restArgs, jsonOutput),
        ("cockpit", "reliability") => await CostCommands.ReliabilityAsync(client, restArgs, jsonOutput),
        ("run", "list") => await RunCommands.ListAsync(client, restArgs, jsonOutput),
        ("run", "get") => await RunCommands.GetAsync(client, restArgs, jsonOutput),
        ("drive", "list") => await DriveCommands.ListAsync(client, restArgs, jsonOutput),
        ("drive", "read") => await DriveCommands.ReadAsync(client, restArgs, jsonOutput),
        ("drive", "download") => await DriveCommands.DownloadAsync(client, restArgs, jsonOutput),
        ("drive", "create") => await DriveCommands.CreateAsync(client, restArgs, jsonOutput),
        ("drive", "update") => await DriveCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("drive", "delete") => await DriveCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("drive", "move") => await DriveCommands.MoveAsync(client, restArgs, jsonOutput),
        ("drive", "mkdir") => await DriveCommands.MkdirAsync(client, restArgs, jsonOutput),
        ("drive", "upload") => await DriveCommands.UploadAsync(client, restArgs, jsonOutput),
        ("drive", "recent") => await DriveCommands.RecentAsync(client, restArgs, jsonOutput),
        ("drive", "usage") => await DriveCommands.UsageAsync(client, restArgs, jsonOutput),
        ("drive", "archived") => await DriveCommands.ArchivedAsync(client, restArgs, jsonOutput),
        ("drive", "restore") => await DriveCommands.RestoreAsync(client, restArgs, jsonOutput),
        ("drive", "search") => await DriveCommands.SearchAsync(client, restArgs, jsonOutput),
        ("drive", "create-json") => await DriveCommands.CreateJsonAsync(client, restArgs, jsonOutput),
        ("drive", "query") => await DriveCommands.QueryAsync(client, restArgs, jsonOutput),
        ("drive", "patch") => await DriveCommands.PatchAsync(client, restArgs, jsonOutput),
        ("sqlite", "query") => await SqliteCommands.QueryAsync(client, restArgs, jsonOutput),
        ("sqlite", "exec") => await SqliteCommands.ExecAsync(client, restArgs, jsonOutput),
        ("sqlite", "schema") => await SqliteCommands.SchemaAsync(client, restArgs, jsonOutput),
        ("sqlite", "create") => await SqliteCommands.CreateAsync(client, restArgs, jsonOutput),
        ("table", "show") => await TableCommands.ShowAsync(client, restArgs, jsonOutput),
        ("table", "append-row") => await TableCommands.AppendRowAsync(client, restArgs, jsonOutput),
        ("table", "set-cell") => await TableCommands.SetCellAsync(client, restArgs, jsonOutput),
        ("table", "delete-row") => await TableCommands.DeleteRowAsync(client, restArgs, jsonOutput),
        ("table", "add-column") => await TableCommands.AddColumnAsync(client, restArgs, jsonOutput),
        ("task", "list") => await TaskCommands.ListAsync(client, restArgs, jsonOutput),
        ("task", "get") => await TaskCommands.GetAsync(client, restArgs, jsonOutput),
        ("task", "create") => await TaskCommands.CreateAsync(client, restArgs, jsonOutput),
        ("task", "update") => await TaskCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("task", "delete") => await TaskCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("task", "status") => await TaskCommands.StatusAsync(client, restArgs, jsonOutput),
        ("task", "comment") => await TaskCommands.CommentAsync(client, restArgs, taskId, jsonOutput),
        ("task", "blocker") => await TaskCommands.BlockerAsync(client, restArgs, taskId, jsonOutput),
        ("task", "done") => await TaskCommands.DoneAsync(client, restArgs, taskId, jsonOutput),
        ("task", "resolve-blocker") => await TaskCommands.ResolveBlockerAsync(client, restArgs, jsonOutput),
        ("task", "drive-link-add") => await TaskCommands.DriveLinkAddAsync(client, restArgs, jsonOutput),
        ("task", "drive-link-remove") => await TaskCommands.DriveLinkRemoveAsync(client, restArgs, jsonOutput),
        ("task", "search") => await TaskCommands.SearchAsync(client, restArgs, jsonOutput),
        ("repository", "list") => await RepositoryCommands.ListAsync(client, jsonOutput),
        ("repository", "ask") => await RepositoryCommands.AskAsync(client, restArgs, jsonOutput),
        ("repository", "question") => await RepositoryCommands.QuestionAsync(client, restArgs, jsonOutput),
        ("repository", "questions") => await RepositoryCommands.QuestionsAsync(client, restArgs, jsonOutput),
        ("automation", "list") => await AutomationCommands.ListAsync(client, restArgs, jsonOutput),
        ("automation", "get") => await AutomationCommands.GetAsync(client, restArgs, jsonOutput),
        ("automation", "create") => await AutomationCommands.CreateAsync(client, restArgs, jsonOutput),
        ("automation", "run") => await AutomationCommands.RunAsync(client, restArgs, jsonOutput),
        ("automation", "pause") => await AutomationCommands.SetSchedulesActiveAsync(client, restArgs, jsonOutput, active: false),
        ("automation", "resume") => await AutomationCommands.SetSchedulesActiveAsync(client, restArgs, jsonOutput, active: true),
        ("automation", "delete") => await AutomationCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("automation", "schedule") => await AutomationCommands.ScheduleAsync(client, restArgs, jsonOutput),
        ("automation", "executions") => await AutomationCommands.ExecutionsAsync(client, restArgs, jsonOutput),
        ("scheduler", "list") => await SchedulerCommands.ListAsync(client, restArgs, jsonOutput),
        ("scheduler", "get") => await SchedulerCommands.GetAsync(client, restArgs, jsonOutput),
        ("scheduler", "create") => await SchedulerCommands.CreateAsync(client, restArgs, jsonOutput),
        ("scheduler", "update") => await SchedulerCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("scheduler", "delete") => await SchedulerCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("scheduler", "pause") => await SchedulerCommands.PauseAsync(client, restArgs, jsonOutput),
        ("scheduler", "resume") => await SchedulerCommands.ResumeAsync(client, restArgs, jsonOutput),
        ("scheduler", "trigger") => await SchedulerCommands.TriggerAsync(client, restArgs, jsonOutput),
        ("scheduler", "executions") => await SchedulerCommands.ExecutionsAsync(client, restArgs, jsonOutput),
        ("workflow", "list") => await WorkflowCommands.ListAsync(client, restArgs, jsonOutput),
        ("workflow", "get") => await WorkflowCommands.GetAsync(client, restArgs, jsonOutput),
        ("workflow", "create") => await WorkflowCommands.CreateAsync(client, restArgs, jsonOutput),
        ("workflow", "update") => await WorkflowCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("workflow", "delete") => await WorkflowCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("workflow", "add-step") => await WorkflowCommands.AddStepAsync(client, restArgs, jsonOutput),
        ("workflow", "update-step") => await WorkflowCommands.UpdateStepAsync(client, restArgs, jsonOutput),
        ("workflow", "delete-step") => await WorkflowCommands.DeleteStepAsync(client, restArgs, jsonOutput),
        ("workflow", "reorder-steps") => await WorkflowCommands.ReorderStepsAsync(client, restArgs, jsonOutput),
        ("workflow", "run") => await WorkflowCommands.RunAsync(client, restArgs, jsonOutput),
        ("workflow", "executions") => await WorkflowCommands.ExecutionsAsync(client, restArgs, jsonOutput),
        ("workflow", "execution-get") => await WorkflowCommands.ExecutionGetAsync(client, restArgs, jsonOutput),
        ("workflow", "execution-runs") => await WorkflowCommands.ExecutionRunsAsync(client, restArgs, jsonOutput),
        ("workflow", "export") => await WorkflowCommands.ExportAsync(client, restArgs, jsonOutput),
        ("workflow", "step-complete") => await WorkflowCommands.StepCompleteAsync(client, restArgs, jsonOutput),
        ("workflow", "step-fail") => await WorkflowCommands.StepFailAsync(client, restArgs, jsonOutput),
        ("workflow", "import") => await WorkflowCommands.ImportAsync(client, restArgs, jsonOutput),
        ("workflow", "duplicate") => await WorkflowCommands.DuplicateAsync(client, restArgs, jsonOutput),
        ("workflow", "cancel") => await WorkflowCommands.CancelAsync(client, restArgs, jsonOutput),
        ("workflow", "retry") => await WorkflowCommands.RetryAsync(client, restArgs, jsonOutput),
        ("workflow", "step-override") => await WorkflowCommands.StepOverrideAsync(client, restArgs, jsonOutput),
        ("workflow", "validate-step") => await WorkflowCommands.ValidateStepAsync(client, restArgs, jsonOutput),
        ("workflow", "triggers") => await WorkflowTriggerCommands.ListAsync(client, restArgs, jsonOutput),
        ("workflow", "trigger-add") => await WorkflowTriggerCommands.AddAsync(client, restArgs, jsonOutput),
        ("workflow", "trigger-update") => await WorkflowTriggerCommands.UpdateAsync(client, restArgs, jsonOutput),
        ("workflow", "trigger-delete") => await WorkflowTriggerCommands.DeleteAsync(client, restArgs, jsonOutput),
        ("review", "list") => await ReviewCommands.ListAsync(client, restArgs, jsonOutput),
        ("review", "get") => await ReviewCommands.GetAsync(client, restArgs, jsonOutput),
        ("review", "diff") => await ReviewCommands.DiffAsync(client, restArgs, jsonOutput),
        ("review", "decide") => await ReviewCommands.DecideAsync(client, restArgs, jsonOutput),
        ("review", "peer") => await ReviewCommands.PeerAsync(client, restArgs, jsonOutput),
        ("approval", "list") => await ApprovalCommands.ListAsync(client, restArgs, jsonOutput),
        ("approval", "approve") => await ApprovalCommands.ApproveAsync(client, restArgs, jsonOutput),
        ("approval", "reject") => await ApprovalCommands.RejectAsync(client, restArgs, jsonOutput),
        ("approval", "request-changes") => await ApprovalCommands.RequestChangesAsync(client, restArgs, jsonOutput),
        ("approval", "tool") => await ApprovalCommands.ToolAsync(client, restArgs, jsonOutput),
        ("queue", "show") => await QueueCommands.ShowAsync(client, restArgs, jsonOutput),
        ("queue", "add") => await QueueCommands.AddAsync(client, restArgs, jsonOutput),
        ("queue", "remove") => await QueueCommands.RemoveAsync(client, restArgs, jsonOutput),
        ("queue", "move") => await QueueCommands.MoveAsync(client, restArgs, jsonOutput),
        ("queue", "pause") => await QueueCommands.PauseAsync(client, restArgs, jsonOutput),
        ("queue", "resume") => await QueueCommands.ResumeAsync(client, restArgs, jsonOutput),
        ("queue", "skip") => await QueueCommands.SkipAsync(client, restArgs, jsonOutput),
        ("outgoing", "show") => await OutgoingCommands.ShowAsync(client, restArgs, jsonOutput),
        ("outgoing", "push") => await OutgoingCommands.PushAsync(client, restArgs, jsonOutput),
        ("secret", "list") => await SecretCommands.ListAsync(client, jsonOutput),
        ("mcp", "list") => await McpCommands.ListAsync(client, jsonOutput),
        ("mcp", "set") => await McpCommands.SetAsync(client, restArgs, jsonOutput),
        ("mcp", "delete") => await McpCommands.DeleteAsync(client, restArgs, jsonOutput),
        _ => Error($"Unknown command: {domain} {action}")
    };
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"API error: {ex.Message}");
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int Error(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

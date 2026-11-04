using System.Net;
using System.Text.Json;
using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// Write commands of <c>sidehub-cli</c> a user drives from a terminal they opened (reviews, approvals, executions, task
/// queue, push, workflow import): the route, method and JSON body each sends, against a local fake API, and the usage
/// errors that never reach it.
/// </summary>
public sealed class CliWriteCommandsTests : IDisposable
{
    private const string WorkspaceId = "11111111-1111-1111-1111-111111111111";
    private const string PendingReview = """
        {"id":"rv-1","agentId":"agent-1","source":"run","provider":"claude","title":"Fix login","status":"pending",
         "rounds":[{"index":1,"files":[],"filesChanged":1,"insertions":2,"deletions":0,"commits":[],"peerReviews":[]},
                   {"index":2,"files":[],"filesChanged":1,"insertions":1,"deletions":1,"commits":[],"peerReviews":[]}],
         "decisions":[]}
        """;

    private readonly FakeSideHubApi _api = new();
    private readonly SideHubApiClient _client;

    public CliWriteCommandsTests() => _client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId, "agent-1");

    public void Dispose()
    {
        _client.Dispose();
        _api.Dispose();
    }

    // --- Reviews ---

    [Theory]
    [InlineData("validate", "validated")]
    [InlineData("dismiss", "dismissed")]
    public async Task Review_decide_posts_the_action(string action, string status)
    {
        _api.Respond("/decision", $$"""{"review":{"id":"rv-1","agentId":"agent-1","status":"{{status}}"},"fixRunId":null,"unchanged":false}""");

        Assert.Equal(0, await ReviewCommands.DecideAsync(_client, ["rv-1", action], json: false));

        Assert.Equal("POST /api/reviews/rv-1/decision", Assert.Single(_api.Methods));
        var body = _api.Calls[0].Json;
        Assert.Equal(action, body.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("comment").ValueKind);
    }

    [Fact]
    public async Task Review_request_fix_sends_the_comment()
    {
        _api.Respond("/decision", """{"review":{"id":"rv-1","agentId":"agent-1","status":"fix-requested"},"fixRunId":"run-9","unchanged":false}""");

        Assert.Equal(0, await ReviewCommands.DecideAsync(_client, ["rv-1", "request-fix", "--comment", "Handle the empty password"], json: false));

        var body = _api.Calls[0].Json;
        Assert.Equal("request-fix", body.GetProperty("action").GetString());
        Assert.Equal("Handle the empty password", body.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Review_decide_says_nothing_changed_on_a_review_already_decided()
    {
        _api.Respond("/decision", """{"review":{"id":"rv-1","status":"validated"},"fixRunId":null,"unchanged":true}""");

        Assert.Equal(1, await ReviewCommands.DecideAsync(_client, ["rv-1", "dismiss"], json: false));
        Assert.Equal("POST /api/reviews/rv-1/decision", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Review_decide_explains_a_review_with_nothing_to_review_yet()
    {
        _api.Respond("/decision", """{"review":{"id":"rv-1","status":null},"fixRunId":null,"unchanged":true}""");

        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            Assert.Equal(1, await ReviewCommands.DecideAsync(_client, ["rv-1", "validate"], json: false));
        }
        finally
        {
            Console.SetError(previous);
        }
        Assert.Contains("has nothing to review yet", stderr.ToString());
    }

    [Fact]
    public async Task Review_decide_shows_the_conflict_of_a_fix_that_cannot_start()
    {
        _api.Respond("/decision", """{"error":"The agent is offline.","review":{"id":"rv-1"}}""", HttpStatusCode.Conflict);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ReviewCommands.DecideAsync(_client, ["rv-1", "request-fix", "--comment", "x"], json: false));
        Assert.Equal("The agent is offline.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rv-1")]
    [InlineData("rv-1|approve")]
    [InlineData("rv-1|request-fix")]
    [InlineData("rv-1|request-fix|--comment| ")]
    public async Task Review_decide_usage_errors_never_call_the_api(string line)
    {
        var args = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, await ReviewCommands.DecideAsync(_client, args, json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Review_peer_asks_for_the_last_round_by_default()
    {
        _api.Respond("/reviews/rv-1", PendingReview);
        _api.Respond("/peer-reviews", """{"review":{"id":"rv-1"},"runId":"run-7"}""");

        Assert.Equal(0, await ReviewCommands.PeerAsync(_client, ["rv-1", "--cli", "codex", "--model", "gpt-5.5"], json: false));

        Assert.Equal(["GET /api/reviews/rv-1", "POST /api/reviews/rv-1/peer-reviews"], _api.Methods);
        var body = _api.Calls[1].Json;
        Assert.Equal(2, body.GetProperty("round").GetInt32());
        Assert.Equal("codex", body.GetProperty("provider").GetString());
        Assert.Equal("gpt-5.5", body.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Review_peer_with_a_round_posts_it_directly()
    {
        _api.Respond("/peer-reviews", """{"review":{"id":"rv-1"},"runId":"run-7"}""");

        Assert.Equal(0, await ReviewCommands.PeerAsync(_client, ["rv-1", "--round", "1", "--cli", "claude"], json: true));

        var call = Assert.Single(_api.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal(1, call.Json.GetProperty("round").GetInt32());
        Assert.Equal("claude", call.Json.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, call.Json.GetProperty("model").ValueKind);
    }

    [Theory]
    [InlineData("rv-1")]
    [InlineData("rv-1|--cli|agy")]
    [InlineData("rv-1|--cli|claude|--round|0")]
    [InlineData("--cli|claude")]
    public async Task Review_peer_usage_errors_never_call_the_api(string line)
    {
        var args = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, await ReviewCommands.PeerAsync(_client, args, json: false));
        Assert.Empty(_api.Calls);
    }

    // --- Approvals ---

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Approval_decisions_post_to_the_step(string decision)
    {
        _api.Respond($"/{decision}", """{"executionStatus":"running","decision":"approved","revisedStepId":null}""");

        var code = decision == "approve"
            ? await ApprovalCommands.ApproveAsync(_client, ["e-1", "s-1", "--comment", "Looks good"], json: false)
            : await ApprovalCommands.RejectAsync(_client, ["e-1", "s-1", "--comment", "Looks good"], json: false);

        Assert.Equal(0, code);
        var call = Assert.Single(_api.Calls);
        Assert.Equal($"POST /api/workflow-executions/e-1/steps/s-1/{decision}", _api.Methods[0]);
        Assert.Equal("Looks good", call.Json.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Approval_request_changes_sends_the_comment_only()
    {
        _api.Respond("/request-changes", """{"executionStatus":"running","decision":"changes-requested","revisedStepId":"s-0"}""");

        Assert.Equal(0, await ApprovalCommands.RequestChangesAsync(_client, ["e-1", "s-1", "--comment", "Shorter intro"], json: false));

        var call = Assert.Single(_api.Calls);
        Assert.Equal("POST /api/workflow-executions/e-1/steps/s-1/request-changes", _api.Methods[0]);
        Assert.Equal("""{"comment":"Shorter intro"}""", call.Body);
    }

    [Theory]
    [InlineData("e-1|s-1")]
    [InlineData("e-1|--comment|x")]
    public async Task Approval_request_changes_usage_errors_never_call_the_api(string line)
    {
        var args = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, await ApprovalCommands.RequestChangesAsync(_client, args, json: false));
        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    public async Task Approval_tool_posts_to_the_tool_approval(string decision)
    {
        _api.Respond($"/{decision}", """{"toolApprovalId":"t-1","status":"approved"}""");

        Assert.Equal(0, await ApprovalCommands.ToolAsync(_client, [decision, "t-1"], json: false));

        var call = Assert.Single(_api.Calls);
        Assert.Equal($"POST /api/tool-approvals/t-1/{decision}", _api.Methods[0]);
        Assert.Equal(JsonValueKind.Null, call.Json.GetProperty("comment").ValueKind);
    }

    [Theory]
    [InlineData("t-1")]
    [InlineData("approve")]
    [InlineData("reject|t-1")]
    public async Task Approval_tool_usage_errors_never_call_the_api(string line)
    {
        var args = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, await ApprovalCommands.ToolAsync(_client, args, json: false));
        Assert.Empty(_api.Calls);
    }

    // --- Execution control ---

    [Fact]
    public async Task Workflow_cancel_posts_the_reason()
    {
        _api.Respond("/cancel", """{"ok":true}""");

        Assert.Equal(0, await WorkflowCommands.CancelAsync(_client, ["e-1", "--reason", "Wrong input"], json: false));

        Assert.Equal("POST /api/workflow-executions/e-1/cancel", Assert.Single(_api.Methods));
        Assert.Equal("""{"reason":"Wrong input"}""", _api.Calls[0].Body);
    }

    [Fact]
    public async Task Workflow_retry_posts_to_the_step()
    {
        _api.Respond("/retry", """{"ok":true}""");

        Assert.Equal(0, await WorkflowCommands.RetryAsync(_client, ["e-1", "s-2"], json: false));
        Assert.Equal("POST /api/workflow-executions/e-1/steps/s-2/retry", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Workflow_step_override_complete_sends_the_output_page()
    {
        _api.Respond("/manual-complete", """{"executionStatus":"running"}""");

        Assert.Equal(0, await WorkflowCommands.StepOverrideAsync(_client, ["e-1", "s-1", "complete", "--output-id", "d-1"], json: false));

        Assert.Equal("POST /api/workflow-executions/e-1/steps/s-1/manual-complete", Assert.Single(_api.Methods));
        Assert.Equal("""{"outputDriveItemId":"d-1"}""", _api.Calls[0].Body);
    }

    [Fact]
    public async Task Workflow_step_override_fail_sends_the_reason()
    {
        _api.Respond("/manual-fail", """{"ok":true}""");

        Assert.Equal(0, await WorkflowCommands.StepOverrideAsync(_client, ["e-1", "s-1", "fail", "--reason", "Stuck"], json: false));

        Assert.Equal("POST /api/workflow-executions/e-1/steps/s-1/manual-fail", Assert.Single(_api.Methods));
        Assert.Equal("""{"reason":"Stuck"}""", _api.Calls[0].Body);
    }

    [Theory]
    [InlineData("e-1|s-1|complete")]
    [InlineData("e-1|s-1|skip")]
    [InlineData("e-1|complete|--output-id|d-1")]
    public async Task Workflow_step_override_usage_errors_never_call_the_api(string line)
    {
        var args = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, await WorkflowCommands.StepOverrideAsync(_client, args, json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Workflow_validate_step_posts_to_the_step()
    {
        _api.Respond("/validate", """{"ok":true,"validationRequestedAt":"2026-10-13T08:00:00Z"}""");

        Assert.Equal(0, await WorkflowCommands.ValidateStepAsync(_client, ["e-1", "s-1"], json: false));
        Assert.Equal("POST /api/workflow-executions/e-1/steps/s-1/validate", Assert.Single(_api.Methods));
    }

    // --- Workflows ---

    [Fact]
    public async Task Workflow_import_sends_the_exported_document()
    {
        _api.Respond("/workflows/import", """{"id":"wf-2","name":"SEO"}""", HttpStatusCode.Created);
        var file = Path.Combine(Path.GetTempPath(), $"sidehub-import-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """{"schema":"sidehub.workflow/v1","name":"SEO","steps":[]}""");
        try
        {
            Assert.Equal(0, await WorkflowCommands.ImportAsync(_client, ["--file", file, "--name", "SEO copy"], json: false));

            Assert.Equal($"POST /api/workspaces/{WorkspaceId}/workflows/import", Assert.Single(_api.Methods));
            var body = _api.Calls[0].Json;
            Assert.Equal("sidehub.workflow/v1", body.GetProperty("document").GetProperty("schema").GetString());
            Assert.Equal("SEO copy", body.GetProperty("name").GetString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Workflow_import_refuses_a_file_that_is_not_json()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sidehub-import-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, "name: SEO");
        try
        {
            Assert.Equal(1, await WorkflowCommands.ImportAsync(_client, ["--file", file], json: false));
            Assert.Equal(1, await WorkflowCommands.ImportAsync(_client, [], json: false));
            Assert.Empty(_api.Calls);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Workflow_duplicate_names_the_copy_like_the_web_app()
    {
        _api.Respond("/workflows/wf-1", """{"id":"wf-1","name":"SEO"}""");
        _api.Respond("/duplicate", """{"id":"wf-2","name":"SEO (copy)"}""", HttpStatusCode.Created);

        Assert.Equal(0, await WorkflowCommands.DuplicateAsync(_client, ["wf-1"], json: false));

        Assert.Equal(["GET /api/workflows/wf-1", "POST /api/workflows/wf-1/duplicate"], _api.Methods);
        Assert.Equal("""{"name":"SEO (copy)"}""", _api.Calls[1].Body);
    }

    [Fact]
    public async Task Workflow_duplicate_with_a_name_posts_it_directly()
    {
        _api.Respond("/duplicate", """{"id":"wf-2","name":"SEO v2"}""", HttpStatusCode.Created);

        Assert.Equal(0, await WorkflowCommands.DuplicateAsync(_client, ["--name", "SEO v2", "wf-1"], json: false));

        Assert.Equal("POST /api/workflows/wf-1/duplicate", Assert.Single(_api.Methods));
        Assert.Equal("""{"name":"SEO v2"}""", _api.Calls[0].Body);
    }

    // --- Task queue ---

    private const string EmptyQueue = """{"agentId":"agent-1","state":"idle","pauseReason":null,"current":null,"items":[],"supported":true}""";

    [Fact]
    public async Task Queue_add_queues_each_task_in_order_and_lets_SideHub_write_their_prompt()
    {
        _api.Respond("/task-queue/items", EmptyQueue);

        Assert.Equal(0, await QueueCommands.AddAsync(_client, ["t-1", "t-2", "--cli", "codex", "--no-commit"], json: false));

        Assert.Equal(["POST /api/agents/agent-1/task-queue/items", "POST /api/agents/agent-1/task-queue/items"], _api.Methods);
        Assert.Equal("""{"taskId":"t-1","cli":"codex","prompt":null,"commit":false}""", _api.Calls[0].Body);
        Assert.Equal("t-2", _api.Calls[1].Json.GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task Queue_add_with_a_prompt_sends_it_as_is_on_the_given_agent()
    {
        _api.Respond("/task-queue/items", EmptyQueue);

        Assert.Equal(0, await QueueCommands.AddAsync(_client, ["t-1", "--prompt", "Do it", "--agent", "agent-2"], json: true));

        var call = Assert.Single(_api.Calls);
        Assert.Equal("POST /api/agents/agent-2/task-queue/items", _api.Methods[0]);
        Assert.Equal("""{"taskId":"t-1","cli":"claude","prompt":"Do it","commit":true}""", call.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("t-1|--cli|cursor")]
    [InlineData("t-1|t-2|--prompt|Do it")]
    public async Task Queue_add_usage_errors_never_call_the_api(string line)
    {
        var args = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, await QueueCommands.AddAsync(_client, args, json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Queue_remove_deletes_the_item()
    {
        _api.Respond("/items/i-1", EmptyQueue);

        Assert.Equal(0, await QueueCommands.RemoveAsync(_client, ["i-1"], json: false));
        Assert.Equal("DELETE /api/agents/agent-1/task-queue/items/i-1", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Queue_move_patches_the_position()
    {
        _api.Respond("/position", EmptyQueue);

        Assert.Equal(0, await QueueCommands.MoveAsync(_client, ["i-1", "--to", "0"], json: false));

        Assert.Equal("PATCH /api/agents/agent-1/task-queue/items/i-1/position", Assert.Single(_api.Methods));
        Assert.Equal("""{"index":0}""", _api.Calls[0].Body);
    }

    [Fact]
    public async Task Queue_move_to_agent_posts_the_target_and_the_cli()
    {
        _api.Respond("/move-to-agent", EmptyQueue);

        Assert.Equal(0, await QueueCommands.MoveAsync(_client, ["i-1", "--to-agent", "agent-2", "--cli", "codex"], json: false));

        Assert.Equal("POST /api/agents/agent-1/task-queue/items/i-1/move-to-agent", Assert.Single(_api.Methods));
        Assert.Equal("""{"targetAgentId":"agent-2","cli":"codex"}""", _api.Calls[0].Body);
    }

    [Theory]
    [InlineData("i-1")]
    [InlineData("i-1|--to|-1")]
    [InlineData("--to|1")]
    [InlineData("i-1|--to|1|--to-agent|agent-2")]
    public async Task Queue_move_usage_errors_never_call_the_api(string line)
    {
        var args = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, await QueueCommands.MoveAsync(_client, args, json: false));
        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("skip")]
    public async Task Queue_actions_post_to_their_route(string action)
    {
        _api.Respond($"/task-queue/{action}", EmptyQueue);

        var code = action switch
        {
            "pause" => await QueueCommands.PauseAsync(_client, [], json: false),
            "resume" => await QueueCommands.ResumeAsync(_client, [], json: false),
            _ => await QueueCommands.SkipAsync(_client, [], json: false)
        };

        Assert.Equal(0, code);
        Assert.Equal($"POST /api/agents/agent-1/task-queue/{action}", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Queue_without_an_agent_never_calls_the_api()
    {
        using var client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId, defaultAgentId: null);

        Assert.Equal(1, await QueueCommands.PauseAsync(client, [], json: false));
        Assert.Equal(1, await QueueCommands.ShowAsync(client, [], json: false));
        Assert.Empty(_api.Calls);
    }

    // --- Push ---

    [Fact]
    public async Task Outgoing_push_sends_the_head_it_read()
    {
        _api.Respond("/outgoing", """
            {"agentId":"agent-1","agentName":"vps","branch":"main","remote":"origin","remoteBranch":"main","hasUpstream":true,
             "head":"abc123def456","ahead":2,"behind":0,"commits":[{"sha":"abc123def456","subject":"Fix","review":null}],
             "queueIdle":true,"ready":true}
            """);
        _api.Respond("/push", """{"outcome":"pushed","detail":null,"outgoing":null}""");

        Assert.Equal(0, await OutgoingCommands.PushAsync(_client, [], json: false));

        Assert.Equal(["GET /api/agents/agent-1/outgoing", "POST /api/agents/agent-1/push"], _api.Methods);
        Assert.Equal("""{"head":"abc123def456"}""", _api.Calls[1].Body);
    }

    [Fact]
    public async Task Outgoing_push_does_not_post_when_nothing_is_ahead()
    {
        _api.Respond("/outgoing", """{"agentId":"agent-1","branch":"main","head":"abc","ahead":0,"commits":[]}""");

        Assert.Equal(0, await OutgoingCommands.PushAsync(_client, ["--agent", "agent-1"], json: false));
        Assert.Equal("GET /api/agents/agent-1/outgoing", Assert.Single(_api.Methods));
    }

    [Fact]
    public async Task Outgoing_push_fails_when_the_agent_refuses()
    {
        _api.Respond("/outgoing", """{"agentId":"agent-1","branch":"main","head":"abc","ahead":1,"commits":[]}""");
        _api.Respond("/push", """{"outcome":"rejected","detail":"! [rejected] main -> main (fetch first)","outgoing":null}""");

        Assert.Equal(1, await OutgoingCommands.PushAsync(_client, [], json: false));
    }

    [Fact]
    public async Task Workflow_add_step_needs_an_output_path_and_never_calls_the_api_without_it()
    {
        var code = await WorkflowCommands.AddStepAsync(_client, ["wf-1", "--name", "Write", "--prompt", "Write a note"], json: false);

        Assert.Equal(1, code);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Workflow_add_step_posts_the_step_with_its_output_path()
    {
        _api.Respond("/workflows/wf-1/steps", """{"id":"wf-1","steps":[{"id":"s-0"},{"id":"s-1"}]}""");

        var code = await WorkflowCommands.AddStepAsync(_client,
            ["wf-1", "--name", "Write", "--prompt", "Write a note", "--output-path", "note.md"], json: false);

        Assert.Equal(0, code);
        var call = Assert.Single(_api.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("note.md", call.Json.GetProperty("outputPath").GetString());
    }
}

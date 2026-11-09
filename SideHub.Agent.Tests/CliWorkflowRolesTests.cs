using System.Text.Json;
using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// Roles of a workflow (SideHub doc 72) in <c>sidehub-cli</c>: the holders a launch sends, and how <c>workflow get</c>
/// and <c>workflow execution-get</c> show roles, holders and a decision taken on behalf of a role.
/// </summary>
public sealed class CliWorkflowRolesTests : IDisposable
{
    private const string WorkspaceId = "11111111-1111-1111-1111-111111111111";
    private readonly FakeSideHubApi _api = new();
    private readonly SideHubApiClient _client;

    public CliWorkflowRolesTests() => _client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId, "agent-1");

    public void Dispose()
    {
        _client.Dispose();
        _api.Dispose();
    }

    [Fact]
    public async Task Workflow_run_sends_the_holders_of_each_role()
    {
        _api.Respond("/run", """{"id":"e-1"}""");

        Assert.Equal(0, await WorkflowCommands.RunAsync(_client,
            ["wf-1", "--role", "qa=alice@example.com", "--input", "topic=SEO", "--role", "po=bob@example.com,carol@example.com"], json: false));

        Assert.Equal("POST /api/workflows/wf-1/run", Assert.Single(_api.Methods));
        var body = _api.Calls[0].Json;
        Assert.Equal("SEO", body.GetProperty("inputs").GetProperty("topic").GetString());
        var roles = body.GetProperty("roleHolders");
        Assert.Equal(["alice@example.com"], roles.GetProperty("qa").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(["bob@example.com", "carol@example.com"], roles.GetProperty("po").EnumerateArray().Select(h => h.GetString()));
    }

    [Fact]
    public async Task Automation_run_without_roles_leaves_the_default_holders()
    {
        _api.Respond("/run", """{"id":"e-1"}""");

        Assert.Equal(0, await AutomationCommands.RunAsync(_client, ["wf-1"], json: false));

        Assert.False(_api.Calls[0].Json.TryGetProperty("roleHolders", out _));
    }

    [Fact]
    public async Task Automation_run_sends_the_holders_too()
    {
        _api.Respond("/run", """{"id":"e-1"}""");

        Assert.Equal(0, await AutomationCommands.RunAsync(_client, ["wf-1", "--role", "architect=eve@example.com"], json: false));

        var holders = _api.Calls[0].Json.GetProperty("roleHolders").GetProperty("architect");
        Assert.Equal(["eve@example.com"], holders.EnumerateArray().Select(h => h.GetString()));
    }

    [Fact]
    public async Task A_malformed_role_is_refused_before_any_call()
    {
        Assert.Equal(1, await WorkflowCommands.RunAsync(_client, ["wf-1", "--role", "qa"], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Workflow_get_reads_the_members_to_name_the_default_holders()
    {
        _api.Respond("/workflows/wf-1", Workflow);
        _api.Respond("/members", Members);

        Assert.Equal(0, await WorkflowCommands.GetAsync(_client, ["wf-1"], json: false));
        Assert.Equal(["/api/workflows/wf-1", $"/api/workspaces/{WorkspaceId}/members"], _api.Requests);
    }

    [Fact]
    public async Task Workflow_get_without_default_holders_does_not_read_the_members()
    {
        _api.Respond("/workflows/wf-1", """{"id":"wf-1","roles":[{"key":"qa","label":"QA","defaultHolders":[]}],"steps":[]}""");

        Assert.Equal(0, await WorkflowCommands.GetAsync(_client, ["wf-1"], json: false));
        Assert.Equal("/api/workflows/wf-1", Assert.Single(_api.Requests));
    }

    [Fact]
    public async Task Default_holders_are_named_by_their_e_mail()
    {
        _api.Respond("/members", Members);
        var workflow = Parse(Workflow);

        var lines = WorkflowRoles.DescribeWorkflowRoles(workflow, await WorkflowRoles.MemberEmailsAsync(_client, workflow)).ToList();

        // Bob left the workspace: the member list no longer names him, his id stays.
        Assert.Equal([
            "Roles (workflow run --role key=<e-mail>, default holders otherwise):",
            $"  qa (QA): default holders: alice@example.com, {Bob}",
            "      Checks the acceptance criteria",
            "  po (Product Owner): no default holder",
            "  dev: default holders: alice@example.com",
        ], lines);
    }

    [Fact]
    public async Task Default_holders_keep_their_id_when_the_members_cannot_be_read()
    {
        _api.Respond("/members", """{"error":"forbidden"}""", System.Net.HttpStatusCode.Forbidden);
        var workflow = Parse(Workflow);

        var emails = await WorkflowRoles.MemberEmailsAsync(_client, workflow);

        Assert.Empty(emails);
        Assert.Contains($"  qa (QA): default holders: {Alice}, {Bob}", WorkflowRoles.DescribeWorkflowRoles(workflow, emails));
    }

    [Fact]
    public void A_workflow_without_roles_shows_no_role_section()
    {
        Assert.Empty(WorkflowRoles.DescribeWorkflowRoles(Parse("""{"id":"wf-1","steps":[]}""")));
    }

    [Fact]
    public void A_step_names_its_role_with_the_label_of_the_workflow()
    {
        var workflow = Parse(Workflow);
        var steps = workflow.GetProperty("steps").EnumerateArray().ToList();

        Assert.Equal("qa (QA)", WorkflowRoles.StepRole(steps[0], workflow));
        Assert.Null(WorkflowRoles.StepRole(steps[1], workflow));
        Assert.Equal("pm", WorkflowRoles.StepRole(Parse("""{"role":"pm"}"""), workflow));
    }

    [Fact]
    public void Execution_holders_show_each_role_with_its_members()
    {
        var execution = Parse("""
            {"id":"e-1","roleHolders":[
              {"key":"qa","label":"QA","description":null,
               "holders":[{"userId":"aaaaaaaa-0000-0000-0000-000000000001","email":"alice@example.com","active":true}]},
              {"key":"po","label":"Product Owner","description":null,
               "holders":[{"userId":"bbbbbbbb-0000-0000-0000-000000000002","email":"bob@example.com","active":false}]},
              {"key":"architect","label":"architect","description":null,
               "holders":[{"userId":"aaaaaaaa-0000-0000-0000-000000000001","email":null,"active":true}]},
              {"key":"dev","label":"dev","description":null,"holders":[]}]}
            """);

        Assert.Equal([
            "Role holders:",
            "  qa (QA): alice@example.com",
            "  po (Product Owner): bob@example.com (inactive)",
            $"  architect: {Alice}",
            "  dev: nobody",
        ], WorkflowRoles.DescribeExecutionHolders(execution));
    }

    [Fact]
    public void An_execution_without_holders_shows_none()
    {
        Assert.Empty(WorkflowRoles.DescribeExecutionHolders(Parse("""{"id":"e-1"}""")));
        Assert.Empty(WorkflowRoles.DescribeExecutionHolders(Parse("""{"id":"e-1","roleHolders":null}""")));
    }

    [Fact]
    public void A_decision_taken_in_place_of_the_holder_says_so()
    {
        Assert.Equal(" on behalf of the qa holder", WorkflowRoles.OnBehalfOf(Parse("""{"decision":"approved","onBehalfOfRole":"qa"}""")));
        Assert.Equal("", WorkflowRoles.OnBehalfOf(Parse("""{"decision":"approved","onBehalfOfRole":null}""")));
        Assert.Equal(" on behalf of the po holder",
            WorkflowRoles.OnBehalfOf(Parse("""{"validatedOnBehalfOfRole":"po"}"""), "validatedOnBehalfOfRole"));
    }

    [Fact]
    public async Task Execution_get_reads_an_execution_with_holders_and_decisions()
    {
        _api.Respond("/workflow-executions/e-1", """
            {"id":"e-1","status":"running",
             "roleHolders":[{"key":"qa","label":"QA","description":null,
               "holders":[{"userId":"aaaaaaaa-0000-0000-0000-000000000001","email":"alice@example.com","active":true}]}],
             "stepExecutions":[
               {"stepId":"s-1","order":0,"stepName":"Check","status":"completed","role":"qa",
                "approval":{"decision":"approved","decidedByEmail":"admin@example.com","onBehalfOfRole":"qa"}},
               {"stepId":"s-2","order":1,"stepName":"Shape","status":"completed","role":"po",
                "validatedByEmail":"bob@example.com","validatedOnBehalfOfRole":null}]}
            """);

        Assert.Equal(0, await WorkflowCommands.ExecutionGetAsync(_client, ["e-1"], json: false));
        Assert.Equal("/api/workflow-executions/e-1", Assert.Single(_api.Requests));
    }

    private const string Alice = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Bob = "bbbbbbbb-0000-0000-0000-000000000002";

    private const string Members = """
        {"members":[
          {"id":"m-1","userId":"aaaaaaaa-0000-0000-0000-000000000001","email":"alice@example.com","role":"member","status":"active"},
          {"id":"m-3","userId":null,"email":"invited@example.com","role":"member","status":"pending"}]}
        """;

    // SideHub returns the default holders of a workflow as user ids.
    private const string Workflow = """
        {"id":"wf-1","name":"Ticket to ready",
         "roles":[
           {"key":"qa","label":"QA","description":"Checks the acceptance criteria",
            "defaultHolders":["aaaaaaaa-0000-0000-0000-000000000001","bbbbbbbb-0000-0000-0000-000000000002"]},
           {"key":"po","label":"Product Owner","description":null,"defaultHolders":[]},
           {"key":"dev","label":"dev","description":null,"defaultHolders":["aaaaaaaa-0000-0000-0000-000000000001"]}],
         "steps":[
           {"id":"s-1","order":0,"name":"Acceptance","kind":"approval","role":"qa"},
           {"id":"s-2","order":1,"name":"Code","kind":"agent"}]}
        """;

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}

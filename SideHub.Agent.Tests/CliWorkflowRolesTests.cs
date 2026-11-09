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
    public async Task Workflow_get_reads_a_workflow_with_roles()
    {
        _api.Respond("/workflows/wf-1", Workflow);

        Assert.Equal(0, await WorkflowCommands.GetAsync(_client, ["wf-1"], json: false));
        Assert.Equal("/api/workflows/wf-1", Assert.Single(_api.Requests));
    }

    [Fact]
    public void Workflow_roles_show_their_label_and_default_holders()
    {
        var lines = WorkflowRoles.DescribeWorkflowRoles(Parse(Workflow)).ToList();

        Assert.Equal([
            "Roles (workflow run --role key=<e-mail>, default holders otherwise):",
            "  qa (QA): default holders: alice@example.com, bob@example.com (inactive)",
            "      Checks the acceptance criteria",
            "  po (Product Owner): no default holder",
            "  dev: default holders: 22222222-2222-2222-2222-222222222222",
        ], lines);
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
    public void Execution_holders_read_a_list_of_roles()
    {
        var execution = Parse("""
            {"id":"e-1","roleHolders":[
              {"key":"qa","label":"QA","holders":[{"userId":"u-1","email":"alice@example.com","isActive":true}]},
              {"key":"po","label":"Product Owner","holders":[{"userId":"u-2","email":"bob@example.com","isActive":false}]},
              {"key":"dev","label":"dev","holders":[]}]}
            """);

        Assert.Equal([
            "Role holders:",
            "  qa (QA): alice@example.com",
            "  po (Product Owner): bob@example.com (inactive)",
            "  dev: nobody",
        ], WorkflowRoles.DescribeExecutionHolders(execution));
    }

    [Fact]
    public void Execution_holders_read_a_map_from_key_to_holders()
    {
        var execution = Parse("""{"roleHolders":{"qa":[{"email":"alice@example.com"}],"architect":["u-3"]}}""");

        Assert.Equal(["Role holders:", "  qa: alice@example.com", "  architect: u-3"], WorkflowRoles.DescribeExecutionHolders(execution));
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
            {"id":"e-1","status":"running","roleHolders":[{"key":"qa","label":"QA","holders":[{"email":"alice@example.com"}]}],
             "stepExecutions":[
               {"stepId":"s-1","order":0,"stepName":"Check","status":"completed","role":"qa",
                "approval":{"decision":"approved","decidedByEmail":"admin@example.com","onBehalfOfRole":"qa"}},
               {"stepId":"s-2","order":1,"stepName":"Shape","status":"completed","role":"po",
                "validatedByEmail":"bob@example.com","validatedOnBehalfOfRole":null}]}
            """);

        Assert.Equal(0, await WorkflowCommands.ExecutionGetAsync(_client, ["e-1"], json: false));
        Assert.Equal("/api/workflow-executions/e-1", Assert.Single(_api.Requests));
    }

    private const string Workflow = """
        {"id":"wf-1","name":"Ticket to ready",
         "roles":[
           {"key":"qa","label":"QA","description":"Checks the acceptance criteria",
            "defaultHolders":[{"userId":"u-1","email":"alice@example.com","isActive":true},{"userId":"u-2","email":"bob@example.com","isActive":false}]},
           {"key":"po","label":"Product Owner","defaultHolders":[]},
           {"key":"dev","label":"dev","defaultHolders":["22222222-2222-2222-2222-222222222222"]}],
         "steps":[
           {"id":"s-1","order":0,"name":"Acceptance","kind":"approval","role":"qa"},
           {"id":"s-2","order":1,"name":"Code","kind":"agent"}]}
        """;

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}

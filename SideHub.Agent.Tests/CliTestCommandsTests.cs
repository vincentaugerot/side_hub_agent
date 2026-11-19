using System.Net;
using System.Text.Json;
using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>
/// <c>sidehub-cli test</c> (SideHub doc 73): the catalog of tests sent as files, an execution launched and followed, and
/// what a run of an execution reports (each step, each value).
/// </summary>
public sealed class CliTestCommandsTests : IDisposable
{
    private const string WorkspaceId = "11111111-1111-1111-1111-111111111111";
    private const string TestId = "22222222-2222-2222-2222-222222222222";
    private const string ExecutionId = "33333333-3333-3333-3333-333333333333";
    private const string TestFile = "title: Log in\nsteps:\n  - when: I log in\n    then: [the cockpit is shown]\n";

    private readonly FakeSideHubApi _api = new();
    private readonly SideHubApiClient _client;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sidehub-test-commands-" + Guid.NewGuid().ToString("N"));
    private readonly string? _execution = Environment.GetEnvironmentVariable(TestCommands.ExecutionVariable);

    public CliTestCommandsTests()
    {
        _client = new SideHubApiClient(_api.Url, "sh_pty_test", WorkspaceId, "agent-1");
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, _execution);
        _client.Dispose();
        _api.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private string File(string content, string name = "test.yaml")
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private const string Catalog = """
        [{"id":"22222222-2222-2222-2222-222222222222","key":"login","title":"Log in","status":"active","priority":"critical","mode":"agent",
          "feature":"Account","steps":[{"title":"s","when":"w","then":["t"],"produces":[]}],"history":[],
          "repositories":[{"id":"r-1","name":"side_hub"}]},
         {"id":"44444444-4444-4444-4444-444444444444","key":"agent-update","title":"Update the agent","status":"active","priority":"normal","mode":"agent",
          "feature":"Agent","steps":[],"history":[],"repositories":[{"id":"r-2","name":"side_hub_agent"}]}]
        """;

    [Fact]
    public async Task List_sends_the_filters_and_narrows_a_repository_given_by_its_name()
    {
        _api.Respond("/tests", Catalog);

        Assert.Equal(0, await TestCommands.ListAsync(_client, ["--repository", "side_hub_agent", "--status", "active", "--feature", "Agent"], json: true));

        Assert.Equal([$"/api/workspaces/{WorkspaceId}/tests?feature=Agent&status=active"], _api.Requests);
        Assert.Equal(1, await TestCommands.ListAsync(_client, ["--status", "broken"], json: false));
    }

    [Fact]
    public async Task Create_from_a_terminal_its_user_opened_makes_an_active_test()
    {
        _api.Respond("/agent/me", """{"token":"terminal","actingUser":{"email":"ana@test.com","role":"owner"}}""");
        _api.Respond("/tests", """{"id":"t-1","key":"log-in","status":"active"}""");

        Assert.Equal(0, await TestCommands.CreateAsync(_client, ["--file", File(TestFile)], json: false));

        Assert.Equal(["GET /api/agent/me", $"POST /api/workspaces/{WorkspaceId}/tests"], _api.Methods);
        Assert.Equal(TestFile, _api.Calls[1].Json.GetProperty("yaml").GetString());
    }

    [Theory]
    [InlineData("""{"token":"run","runId":"r-1"}""")]
    [InlineData("""{"token":"terminal"}""")]
    public async Task Create_from_a_run_or_a_queued_task_only_proposes(string identity)
    {
        _api.Respond("/agent/me", identity);
        _api.Respond("/tests/proposals", """{"id":"t-1","key":"log-in","status":"draft"}""");

        Assert.Equal(0, await TestCommands.CreateAsync(_client, ["--file", File(TestFile)], json: false));

        Assert.Equal($"POST /api/workspaces/{WorkspaceId}/tests/proposals", _api.Methods[1]);
    }

    [Fact]
    public async Task Create_needs_a_file_that_exists()
    {
        Assert.Equal(1, await TestCommands.CreateAsync(_client, [], json: false));
        Assert.Equal(1, await TestCommands.CreateAsync(_client, ["--file", Path.Combine(_folder, "missing.yaml")], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task What_the_backend_refuses_is_told_as_it_says_it()
    {
        _api.Respond("/agent/me", """{"token":"terminal","actingUser":{"email":"ana@test.com","role":"owner"}}""");
        _api.Respond("/tests", """{"error":"The value 'page.titre' is used by 'memory' but no test produces it."}""", HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => TestCommands.CreateAsync(_client, ["--file", File(TestFile)], json: false));

        Assert.Equal("The value 'page.titre' is used by 'memory' but no test produces it.", error.Message);
    }

    [Fact]
    public async Task Update_and_archive_find_a_test_by_its_key()
    {
        _api.Respond("/tests", Catalog);
        _api.Respond($"/tests/{TestId}", """{"key":"login","version":2}""");
        _api.Respond($"/tests/{TestId}/archive", """{"key":"login","status":"archived"}""");

        Assert.Equal(0, await TestCommands.UpdateAsync(_client, ["login", "--file", File(TestFile)], json: false));
        Assert.Equal(0, await TestCommands.ArchiveAsync(_client, ["login", "--yes"], json: false));

        Assert.Equal(
        [
            $"GET /api/workspaces/{WorkspaceId}/tests", $"PUT /api/tests/{TestId}",
            $"GET /api/workspaces/{WorkspaceId}/tests", $"POST /api/tests/{TestId}/archive",
        ], _api.Methods);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => TestCommands.GetAsync(_client, ["nope"], json: true));
        Assert.Contains("sidehub-cli test list", unknown.Message);
    }

    [Fact]
    public async Task Import_sends_the_catalog_and_export_writes_the_file()
    {
        _api.Respond("/tests/import", """{"created":[{"key":"login"}],"updated":[{"key":"signup","version":3}]}""");
        _api.Respond("/repositories", """[{"id":"r-1","name":"side_hub"}]""");
        _api.Respond("/tests/export", """{"yaml":"format: sidehub.qa/v1\nscenarios: []\n"}""");
        var output = Path.Combine(_folder, "out.yaml");

        Assert.Equal(0, await TestCommands.ImportAsync(_client, [File("scenarios: []", "scenarios.yaml")], json: false));
        Assert.Equal(0, await TestCommands.ExportAsync(_client, ["--repository", "SIDE_HUB", "--output", output], json: false));
        Assert.Equal(1, await TestCommands.ExportAsync(_client, ["--repository", "elsewhere"], json: false));

        Assert.Equal("scenarios: []", _api.Calls[0].Json.GetProperty("yaml").GetString());
        Assert.Contains($"/api/workspaces/{WorkspaceId}/tests/export?repositoryId=r-1", _api.Requests);
        Assert.Equal("format: sidehub.qa/v1\nscenarios: []\n", System.IO.File.ReadAllText(output));
    }

    [Fact]
    public async Task Run_sends_the_selection_and_what_plays_it()
    {
        _api.Respond("/test-executions", """{"execution":{"summary":{"id":"e-1","number":7,"environmentName":"Staging","provider":"codex"},"results":[{}],"skipped":[{"key":"redirect","reason":"script"}]}}""");

        Assert.Equal(0, await TestCommands.RunAsync(_client,
            ["--environment", "Staging", "--test", "login", "--test", "signup", "--fallbacks", "--cli", "codex", "--model", "gpt-5"], json: false));

        var body = _api.Calls.Single().Json;
        Assert.Equal("Staging", body.GetProperty("environment").GetString());
        Assert.Equal("codex", body.GetProperty("provider").GetString());
        Assert.Equal("gpt-5", body.GetProperty("model").GetString());
        var selection = body.GetProperty("selection");
        Assert.Equal("tests", selection.GetProperty("kind").GetString());
        Assert.Equal(["login", "signup"], selection.GetProperty("tests").EnumerateArray().Select(t => t.GetString()));
        Assert.False(selection.GetProperty("withProducers").GetBoolean());
    }

    [Theory]
    [InlineData(new string[0], "all", null)]
    [InlineData(new[] { "--critical" }, "critical", null)]
    [InlineData(new[] { "--repository", "side_hub" }, "repository", "side_hub")]
    public async Task Run_without_named_tests_takes_all_the_critical_ones_or_a_repository(string[] args, string kind, string? repository)
    {
        _api.Respond("/test-executions", """{"execution":{"summary":{"id":"e-1","number":1},"results":[],"skipped":[]}}""");

        Assert.Equal(0, await TestCommands.RunAsync(_client, args, json: false));

        var selection = _api.Calls.Single().Json.GetProperty("selection");
        Assert.Equal(kind, selection.GetProperty("kind").GetString());
        Assert.True(selection.GetProperty("withProducers").GetBoolean());
        Assert.Equal(repository, selection.TryGetProperty("repository", out var name) ? name.GetString() : null);
    }

    [Fact]
    public async Task Run_takes_one_kind_of_selection_and_a_known_cli()
    {
        Assert.Equal(1, await TestCommands.RunAsync(_client, ["--critical", "--test", "login"], json: false));
        Assert.Equal(1, await TestCommands.RunAsync(_client, ["--cli", "gemini"], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task A_launch_that_cannot_start_says_why()
    {
        _api.Respond("/test-executions", """{"error":"Agent 'vps' is offline.","code":"agent-offline"}""", HttpStatusCode.Conflict);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => TestCommands.RunAsync(_client, [], json: false));

        Assert.Equal("Agent 'vps' is offline.", error.Message);
    }

    [Fact]
    public async Task Status_reads_the_last_execution_or_one_by_its_number()
    {
        _api.Respond("/test-executions", $$"""[{"id":"{{ExecutionId}}","number":7},{"id":"e-6","number":6}]""");
        _api.Respond($"/test-executions/{ExecutionId}", """{"summary":{"number":7,"verdict":"failed","counts":{"played":1,"failed":1}},"results":[{"key":"login","status":"failed","confirmed":true,"steps":[{"status":"failed","observed":"500"}]}]}""");
        _api.Respond("/test-executions/e-6", """{"summary":{"number":6,"status":"running","counts":{}},"results":[]}""");

        Assert.Equal(0, await TestCommands.StatusAsync(_client, [], json: false));
        Assert.Equal(0, await TestCommands.StatusAsync(_client, ["#6"], json: false));
        Assert.Equal(1, await TestCommands.StatusAsync(_client, ["99"], json: false));

        Assert.Contains($"/api/test-executions/{ExecutionId}", _api.Requests);
        Assert.Contains("/api/test-executions/e-6", _api.Requests);
    }

    [Fact]
    public async Task Result_reports_a_step_with_each_then_in_its_rank()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        _api.Respond("/results", """{"testKey":"login","status":"pending","message":"Step 1 of 2 recorded."}""");

        Assert.Equal(0, await TestCommands.ResultAsync(_client,
        [
            "login", "--step", "1", "--status", "failed",
            "--then", "1=verified: the URL is /cockpit: no redirect", "--then", "3=failed: the agent is offline",
            "--observed", "The agent card says offline", "--console", "TypeError: x", "--console", "ReferenceError: y", "--http", "GET /api/agents 500",
        ], json: false));

        Assert.Equal([$"POST /api/test-executions/{ExecutionId}/results"], _api.Methods);
        var body = _api.Calls.Single().Json;
        Assert.Equal("login", body.GetProperty("test").GetString());
        Assert.Equal(1, body.GetProperty("step").GetInt32());
        Assert.Equal("failed", body.GetProperty("status").GetString());
        var then = body.GetProperty("then").EnumerateArray().ToList();
        Assert.Equal(["verified", "no-evidence", "failed"], then.Select(t => t.GetProperty("status").GetString()));
        Assert.Equal("the URL is /cockpit: no redirect", then[0].GetProperty("evidence").GetString());
        Assert.Equal(JsonValueKind.Null, then[1].GetProperty("evidence").ValueKind);
        Assert.Equal(["TypeError: x", "ReferenceError: y"], body.GetProperty("console").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("The agent card says offline", body.GetProperty("observed").GetString());
    }

    [Theory]
    [InlineData("login", "--step", "0", "--status", "passed")]
    [InlineData("login", "--step", "1", "--status", "ok")]
    [InlineData("login", "--step", "1", "--status", "passed", "--then", "verified: seen")]
    [InlineData("login", "--step", "1", "--status", "passed", "--then", "1=seen")]
    [InlineData("--step", "1", "--status", "passed")]
    public async Task Result_refuses_what_it_cannot_read(params string[] args)
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);

        Assert.Equal(1, await TestCommands.ResultAsync(_client, args, json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Result_and_value_only_work_inside_a_run_of_an_execution()
    {
        Assert.Equal(1, await TestCommands.ResultAsync(_client, ["login", "--step", "1", "--status", "passed"], json: false));
        Assert.Equal(1, await TestCommands.ValueAsync(_client, ["get", "compte.email"], json: false));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Value_sets_and_reads_by_name()
    {
        Environment.SetEnvironmentVariable(TestCommands.ExecutionVariable, ExecutionId);
        _api.Respond("/values/compte.motDePasse", """{"name":"compte.motDePasse","secret":true,"value":"s3cret-pw"}""");

        Assert.Equal(0, await TestCommands.ValueAsync(_client, ["set", "compte.motDePasse", "s3cret-pw", "--secret"], json: false));
        Assert.Equal(0, await TestCommands.ValueAsync(_client, ["get", "compte.motDePasse"], json: true));
        Assert.Equal(1, await TestCommands.ValueAsync(_client, ["set", "compte.motDePasse"], json: false));
        Assert.Equal(1, await TestCommands.ValueAsync(_client, ["drop", "compte.motDePasse"], json: false));

        Assert.Equal(
        [
            $"PUT /api/test-executions/{ExecutionId}/values/compte.motDePasse",
            $"GET /api/test-executions/{ExecutionId}/values/compte.motDePasse",
        ], _api.Methods);
        Assert.Equal("s3cret-pw", _api.Calls[0].Json.GetProperty("value").GetString());
        Assert.True(_api.Calls[0].Json.GetProperty("secret").GetBoolean());
    }

    [Fact]
    public async Task Scripted_results_are_not_imported_yet()
    {
        Assert.Equal(1, await TestCommands.ResultsAsync(_client, ["import", "report.json"], json: false));
        Assert.Empty(_api.Calls);
    }
}

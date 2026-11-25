using System.Text.Json;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary>The task types a workflow with a task input serves (SideHub doc 75), as <c>workflow get</c> shows them.</summary>
public sealed class CliWorkflowTaskTypesTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void The_task_types_served_are_listed()
    {
        Assert.Equal("feature, bug-fix", WorkflowCommands.DescribeTaskTypes(Parse("""{"taskTypes":["feature","bug-fix"]}""")));
    }

    [Theory]
    [InlineData("""{"taskTypes":[]}""")]
    [InlineData("""{}""")]
    public void No_task_types_means_every_type(string workflow)
    {
        Assert.Equal("every type", WorkflowCommands.DescribeTaskTypes(Parse(workflow)));
    }
}

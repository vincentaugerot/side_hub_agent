using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

public class WorkflowCommandPairsTests
{
    [Fact]
    public void CollectsRepeatedPairs_KeepingEqualsSignsInValues()
    {
        string[] args = ["--output-id", "abc", "--value", "score=72", "--value", "url=https://x.io/?a=b", "--value", "score=80"];

        Assert.True(WorkflowCommands.TryCollectPairs(args, "--value", out var pairs, out var error));

        Assert.Null(error);
        Assert.Equal(new Dictionary<string, string> { ["score"] = "80", ["url"] = "https://x.io/?a=b" }, pairs);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("=72")]
    public void RejectsPairsWithoutKey(string pair)
    {
        Assert.False(WorkflowCommands.TryCollectPairs(["--input", pair], "--input", out _, out var error));
        Assert.Contains("key=value", error);
    }

    [Fact]
    public void RejectsFlagWithoutValue()
    {
        Assert.False(WorkflowCommands.TryCollectPairs(["wf", "--input"], "--input", out _, out _));
    }
}

public class WorkflowRolePairsTests
{
    [Fact]
    public void Collects_the_holders_of_each_role_into_a_list()
    {
        string[] args = ["wf-1", "--role", "qa=alice@example.com", "--input", "topic=x", "--role", "po=bob@example.com,carol@example.com",
            "--role", "QA=dave@example.com", "--role", "qa=Alice@example.com"];

        Assert.True(WorkflowRoles.TryCollect(args, out var holders, out var error));

        Assert.Null(error);
        Assert.Equal(["qa", "po"], holders.Keys);
        Assert.Equal(["alice@example.com", "dave@example.com"], holders["qa"]);
        Assert.Equal(["bob@example.com", "carol@example.com"], holders["po"]);
    }

    [Fact]
    public void Without_role_flags_nothing_is_collected()
    {
        Assert.True(WorkflowRoles.TryCollect(["wf-1", "--input", "a=b"], out var holders, out _));
        Assert.Empty(holders);
    }

    [Theory]
    [InlineData("qa")]
    [InlineData("=alice@example.com")]
    [InlineData("qa=")]
    [InlineData("qa= , ")]
    public void Rejects_a_role_without_key_or_holder(string pair)
    {
        Assert.False(WorkflowRoles.TryCollect(["--role", pair], out _, out var error));
        Assert.Contains("key=<member e-mail or id>", error);
    }

    [Fact]
    public void Rejects_the_flag_without_value()
    {
        Assert.False(WorkflowRoles.TryCollect(["wf", "--role"], out _, out _));
    }
}

using SideHub.Agent.Update;

namespace SideHub.Agent.Tests;

/// <summary><c>status</c> and <c>doctor</c> name the agents that run without their connection to SideHub.</summary>
public class ConnectionReportTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 22, 1, 30, DateTimeKind.Utc);

    [Fact]
    public void An_agent_offline_for_hours_is_named_with_how_long()
    {
        var lines = ConnectionReport.Lines(
            [new AgentConnecting("side_hub", new DateTime(2026, 11, 17, 19, 47, 0, DateTimeKind.Utc)), new AgentConnecting("blog", Now.AddMinutes(-28))], Now);

        Assert.Equal(
        [
            "side_hub: not connected to SideHub for 2 h 14 min (since 2026-11-17 19:47 UTC)",
            "blog: not connected to SideHub for 28 min (since 2026-11-17 21:33 UTC)",
        ], lines);
    }

    [Fact]
    public void A_reconnection_going_on_is_not_worth_a_line()
    {
        Assert.Empty(ConnectionReport.Lines([new AgentConnecting("side_hub", Now.AddSeconds(-40))], Now));
        Assert.Empty(ConnectionReport.Lines([], Now));
    }

    [Fact]
    public void The_health_file_of_a_daemon_that_is_gone_says_nothing()
    {
        using var temp = new Update.TempFolder();
        UpdateFiles.Write(UpdateFiles.HealthPath(temp.Path),
            new InstanceHealth(Environment.ProcessId, "1.0.153", true, null, Now, [new AgentConnecting("side_hub", Now.AddHours(-2))]));

        // No PID file in that folder: no daemon runs there
        Assert.Empty(ConnectionReport.Lines(temp.Path, Now));
    }
}

using SideHub.Agent.Update;

namespace SideHub.Agent.Tests.Update;

/// <summary>A daemon that fails at startup says why in its health file: the updater reads the reason from there.</summary>
public class UpdateApplierTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private void Health(bool ok, string? problem, DateTime at) =>
        UpdateFiles.Write(UpdateFiles.HealthPath(_temp.Path), new InstanceHealth(4242, "1.0.0", ok, problem, at));

    [Fact]
    public void A_failure_reported_since_the_restart_gives_its_reason()
    {
        var since = DateTime.UtcNow;
        Health(ok: false, "No usable agent configuration", since.AddSeconds(1));

        Assert.Equal("No usable agent configuration", UpdateApplier.StartupFailure(_temp.Path, since));
    }

    [Fact]
    public void A_failure_from_an_earlier_run_is_not_this_restart_s()
    {
        var since = DateTime.UtcNow;
        Health(ok: false, "No usable agent configuration", since.AddMinutes(-5));

        Assert.Null(UpdateApplier.StartupFailure(_temp.Path, since));
    }

    [Fact]
    public void A_healthy_daemon_or_no_health_file_is_no_failure()
    {
        var since = DateTime.UtcNow;
        Assert.Null(UpdateApplier.StartupFailure(_temp.Path, since));

        Health(ok: true, null, since.AddSeconds(1));
        Assert.Null(UpdateApplier.StartupFailure(_temp.Path, since));
    }

    public void Dispose() => _temp.Dispose();
}

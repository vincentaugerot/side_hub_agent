using System.Diagnostics;
using SideHub.Cli;
using SideHub.Cli.Commands;

namespace SideHub.Agent.Tests;

/// <summary><c>sidehub-cli task done</c>: which task it completes, and when it must wait for a commit.</summary>
public sealed class TaskDoneTests : IDisposable
{
    private static bool GitAvailable => !OperatingSystem.IsWindows() && ExecutableResolver.Resolve("git") is not null;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sidehub-task-done-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Theory]
    [InlineData(new[] { "t1" }, "env", "t1", null)]
    [InlineData(new string[0], "env", "env", null)]
    [InlineData(new[] { "--summary", "Fixed the bug" }, "env", "env", "Fixed the bug")]
    [InlineData(new[] { "--summary", "t2" }, "env", "env", "t2")]
    [InlineData(new[] { "--summary", "Fixed", "t1" }, "env", "t1", "Fixed")]
    [InlineData(new[] { "t1", "--summary", "Fixed", "--json" }, null, "t1", "Fixed")]
    [InlineData(new[] { "--json", "--summary", "  " }, null, null, null)]
    public void Task_id_falls_back_to_the_environment_and_the_summary_is_never_taken_for_it(
        string[] args, string? envTaskId, string? taskId, string? summary)
    {
        Assert.Equal((taskId, summary, (string?)null), TaskCommands.ParseDone(args, envTaskId));
    }

    [Theory]
    [InlineData(new[] { "--summary", "Done", "--open-point", "Which price?" }, "env", "env", "Which price?")]
    [InlineData(new[] { "--open-point", "t2", "t1" }, "env", "t1", "t2")]
    [InlineData(new[] { "--open-point", "  " }, "env", "env", null)]
    public void The_open_point_is_read_and_never_taken_for_the_task_id(
        string[] args, string? envTaskId, string? taskId, string? openPoint)
    {
        var (id, _, point) = TaskCommands.ParseDone(args, envTaskId);
        Assert.Equal(taskId, id);
        Assert.Equal(openPoint, point);
    }

    [Fact]
    public async Task Uncommitted_changes_are_refused_when_a_commit_is_required()
    {
        if (!GitAvailable)
            return;
        InitRepositoryWithCommit();
        await File.WriteAllTextAsync(Path.Combine(_dir.FullName, "pending.txt"), "work");

        Assert.Equal(TaskCommands.UncommittedChangesMessage, await TaskCommands.DoneRefusalAsync(_dir.FullName, commitRequired: true));
    }

    [Fact]
    public async Task Uncommitted_changes_are_accepted_when_no_commit_is_required()
    {
        if (!GitAvailable)
            return;
        InitRepositoryWithCommit();
        await File.WriteAllTextAsync(Path.Combine(_dir.FullName, "pending.txt"), "work");

        Assert.Null(await TaskCommands.DoneRefusalAsync(_dir.FullName, commitRequired: false));
    }

    [Fact]
    public async Task A_clean_work_tree_is_accepted_and_reports_its_head()
    {
        if (!GitAvailable)
            return;
        InitRepositoryWithCommit();

        Assert.Null(await TaskCommands.DoneRefusalAsync(_dir.FullName, commitRequired: true));
        Assert.Equal(Git("rev-parse", "HEAD").Trim(), await GitWorkTree.HeadAsync(_dir.FullName));
    }

    [Fact]
    public async Task Outside_a_repository_nothing_is_refused_and_there_is_no_commit()
    {
        Assert.Null(await TaskCommands.DoneRefusalAsync(_dir.FullName, commitRequired: true));
        Assert.Null(await GitWorkTree.HeadAsync(_dir.FullName));
    }

    [Fact]
    public async Task A_repository_without_commits_has_no_head()
    {
        if (!GitAvailable)
            return;
        Git("init", "-q");

        Assert.Null(await GitWorkTree.HeadAsync(_dir.FullName));
    }

    private void InitRepositoryWithCommit()
    {
        Git("init", "-q");
        File.WriteAllText(Path.Combine(_dir.FullName, "README.md"), "x");
        Git("add", "README.md");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false",
            "commit", "-q", "-m", "init");
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo(ExecutableResolver.Resolve("git")!)
        {
            WorkingDirectory = _dir.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        return output;
    }
}

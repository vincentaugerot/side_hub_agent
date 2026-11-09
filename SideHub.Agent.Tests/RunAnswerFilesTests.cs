using SideHub.Cli.Launch;

namespace SideHub.Agent.Tests;

/// <summary>The file where a run's <c>codex exec</c> writes its last answer (<c>--output-last-message</c>): prepared in
/// an agent-owned folder for each run PTY, read at exit without following a link, deleted with the PTY.</summary>
public sealed class RunAnswerFilesTests : IDisposable
{
    private const string Pty = "run-5b7c3d9e-0f14-4f6e-9a21-8c1e7a520d3b";
    private readonly string _dir = Directory.CreateTempSubdirectory("sidehub-run-answers-").FullName;
    private readonly RunAnswerFiles _files;

    public RunAnswerFilesTests() => _files = new RunAnswerFiles(Path.Combine(_dir, ".sidehub", "run", "answers", "agent-1"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_agent_and_the_launcher_name_the_same_variable() =>
        Assert.Equal(LaunchCommand.RunAnswerFileVariable, RunAnswerFiles.Variable);

    [Fact]
    public void The_answer_codex_wrote_is_read_and_its_file_deleted()
    {
        var path = _files.Prepare(Pty, out var problem);
        Assert.Null(problem);
        Assert.Equal(Path.Combine(_files.Folder, $"{Pty}.txt"), path);
        File.WriteAllText(path!, "Done: 3 pages fixed.\n");

        Assert.Equal("Done: 3 pages fixed.", _files.Read(Pty));
        Assert.True(_files.Delete(Pty));
        Assert.False(File.Exists(path));
        Assert.Null(_files.Read(Pty));
    }

    [Fact]
    public void A_run_whose_cli_wrote_nothing_has_no_answer()
    {
        Assert.Null(_files.Read(Pty)); // folder not created yet
        var path = _files.Prepare(Pty, out _)!;
        Assert.Null(_files.Read(Pty));
        File.WriteAllText(path, "  \n");
        Assert.Null(_files.Read(Pty));
    }

    [Fact]
    public void A_leftover_of_an_earlier_pty_is_not_taken_for_the_answer()
    {
        var path = _files.Prepare(Pty, out _)!;
        File.WriteAllText(path, "an old answer");

        _files.Prepare(Pty, out _);

        Assert.Null(_files.Read(Pty));
    }

    [Fact]
    public void A_link_in_place_of_the_file_is_not_followed()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = _files.Prepare(Pty, out _)!;
        var secret = Path.Combine(_dir, "id_rsa");
        File.WriteAllText(secret, "PRIVATE KEY");
        File.CreateSymbolicLink(path, secret);

        Assert.Throws<IOException>(() => _files.Read(Pty));
    }

    [Fact]
    public void The_folder_is_private()
    {
        if (OperatingSystem.IsWindows()) return;
        _files.Prepare(Pty, out _);

        Assert.Equal(PrivateFiles.PrivateDirectoryMode, File.GetUnixFileMode(_files.Folder));
    }

    [Fact]
    public void Only_a_valid_pty_session_id_names_a_file() =>
        Assert.Null(_files.Prepare("../../etc/passwd", out _));
}

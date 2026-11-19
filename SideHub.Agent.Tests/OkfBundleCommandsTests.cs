using System.IO.Compression;
using SideHub.Cli.Okf;

namespace SideHub.Agent.Tests;

public class OkfBundleCommandsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidehub-okf-bundle-" + Guid.NewGuid().ToString("N"));

    public OkfBundleCommandsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Pack_PutsTheFolderContentAtTheRoot_WithoutHiddenFiles()
    {
        Write("payments/refund-flow.md", "---\ntype: Playbook\n---\n");
        Write("index.md", "# Bundle");
        Write(".git/HEAD", "ref");
        Write("payments/.DS_Store", "x");

        using var archive = new ZipArchive(new MemoryStream(OkfBundleCommands.Pack(_root)), ZipArchiveMode.Read);

        Assert.Equal(new[] { "index.md", "payments/refund-flow.md" }, archive.Entries.Select(e => e.FullName).Order().ToArray());
    }

    [Fact]
    public void Extract_WritesTheBundle_AndRefusesAnEntryLeavingTheFolder()
    {
        var target = Path.Combine(_root, "out");
        Assert.Equal(1, OkfBundleCommands.Extract(Zip("a/b.md"), target));
        Assert.True(File.Exists(Path.Combine(target, "a", "b.md")));

        Assert.Throws<InvalidOperationException>(() => OkfBundleCommands.Extract(Zip("../escape.md"), target));
        Assert.False(File.Exists(Path.Combine(_root, "escape.md")));
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static byte[] Zip(string entryName)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry(entryName).Open()))
            writer.Write("---\ntype: X\n---\n");
        return buffer.ToArray();
    }
}

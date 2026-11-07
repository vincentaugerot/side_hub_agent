namespace SideHub.Cli.Launch;

/// <summary>
/// The folder codex and copilot keep a trust decision for: the main working tree of the git repository that holds the
/// directory, so a sub-folder or a linked worktree (<c>git worktree add</c>) is trusted with its repository, as both CLIs
/// resolve it; the directory itself outside git. Read from the <c>.git</c> entries alone, without running git.
/// </summary>
public static class GitTrustRoot
{
    public static string Of(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            var dotGit = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(dotGit))
                return current.FullName;
            if (File.Exists(dotGit))
                return MainWorkTree(current.FullName, dotGit) ?? current.FullName;
        }
        return directory;
    }

    /// <summary>
    /// A linked worktree's <c>.git</c> file says <c>gitdir: &lt;repo&gt;/.git/worktrees/&lt;name&gt;</c>, whose
    /// <c>commondir</c> leads to the repository's <c>.git</c>: its parent is the main working tree. Null for a
    /// submodule (no <c>commondir</c>: it is its own repository) or a bare repository.
    /// </summary>
    private static string? MainWorkTree(string folder, string dotGitFile)
    {
        const string prefix = "gitdir:";
        if (File.ReadLines(dotGitFile).FirstOrDefault() is not { } line || !line.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var gitDir = Path.GetFullPath(line[prefix.Length..].Trim(), folder);
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile))
            return null;
        var common = Path.TrimEndingDirectorySeparator(Path.GetFullPath(File.ReadAllText(commonDirFile).Trim(), gitDir));
        return Path.GetFileName(common) == ".git" ? Path.GetDirectoryName(common) : null;
    }
}

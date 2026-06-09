using System;
using System.IO;
using System.Threading;
using GitMeta;

namespace GitMeta.Tests;

/// <summary>
/// A throwaway git repo in a unique temp directory (outside any project repo,
/// so <c>git rev-parse</c> can't climb up to a parent). Sets a deterministic
/// identity so commits don't depend on the runner's git config. Reuses the
/// library's internal <see cref="GitProcess"/> to run git.
/// </summary>
internal sealed class GitRepo : IDisposable
{
    public string Root { get; }

    public GitRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "gitmeta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test User");
        Git("config", "commit.gpgsign", "false");
    }

    public void Git(params string[] args)
        => GitProcess.Run(Root, args, CancellationToken.None);

    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Commit(string relativePath, string content, string message)
    {
        Write(relativePath, content);
        Git("add", relativePath);
        Git("commit", "-q", "-m", message);
    }

    /// <summary>Commits with a pinned author date (what <c>%at</c> reports), so
    /// multi-commit tests get distinct, deterministic timestamps.</summary>
    public void CommitAt(string relativePath, string content, string message, long unixTime)
    {
        Write(relativePath, content);
        Git("add", relativePath);
        Git("commit", "-q", "-m", message, $"--date=@{unixTime}");
    }

    public string PathTo(string relativePath) => Path.Combine(Root, relativePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temp tree.
        }
    }
}

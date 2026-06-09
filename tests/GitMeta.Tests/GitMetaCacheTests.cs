using System.IO;
using System.Threading.Tasks;
using GitMeta;

namespace GitMeta.Tests;

public sealed class GitMetaCacheTests
{
    private static bool GitMissing => !GitMetaCache.HasGitBinary();

    [Fact]
    public void Create_SingleCommit_PopulatesLookup()
    {
        if (GitMissing) return;
        using var repo = new GitRepo();
        repo.Commit("hello.txt", "hi\n", "Add hello");

        var cache = GitMetaCache.Create(repo.Root);
        Assert.NotNull(cache);
        Assert.NotEmpty(cache!.RepoRoot);
        Assert.NotEmpty(cache.HeadSha);

        var info = cache.Lookup(repo.PathTo("hello.txt"));
        Assert.NotNull(info);
        Assert.Equal(1, info!.Value.CommitCount);
        Assert.Equal("Test User", info.Value.LastCommitAuthor);
        Assert.Equal("Add hello", info.Value.LastCommitSubject);
        Assert.NotEqual(default, info.Value.LastCommitTime);
        Assert.Equal(info.Value.LastCommitTime, info.Value.FirstSeen); // single commit
    }

    [Fact]
    public void Create_MultipleCommits_Accumulate()
    {
        if (GitMissing) return;
        using var repo = new GitRepo();
        repo.CommitAt("doc.md", "v1\n", "Initial draft", 1_000_000_000);
        repo.CommitAt("doc.md", "v2\n", "Edit pass", 1_000_000_060);
        repo.CommitAt("doc.md", "v3\n", "Final pass", 1_000_000_120);

        var cache = GitMetaCache.Create(repo.Root);
        Assert.NotNull(cache);

        var info = cache!.Lookup(repo.PathTo("doc.md"));
        Assert.NotNull(info);
        Assert.Equal(3, info!.Value.CommitCount);
        Assert.Equal("Final pass", info.Value.LastCommitSubject);
        Assert.True(info.Value.FirstSeen < info.Value.LastCommitTime);
    }

    [Fact]
    public void Create_NonGitTree_ReturnsNull()
    {
        if (GitMissing) return;
        var dir = Path.Combine(Path.GetTempPath(), "gitmeta-tests", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(GitMetaCache.Create(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsTracked_OnlyForIndexedFiles()
    {
        if (GitMissing) return;
        using var repo = new GitRepo();
        repo.Commit("tracked.txt", "in\n", "Add tracked");
        repo.Write("untracked.txt", "out\n");

        var cache = GitMetaCache.Create(repo.Root);
        Assert.NotNull(cache);
        Assert.True(cache!.IsTracked(repo.PathTo("tracked.txt")));
        Assert.False(cache.IsTracked(repo.PathTo("untracked.txt")));
    }

    [Fact]
    public void IsIgnored_MatchesGitignore_ButNotIndexedGitignore()
    {
        if (GitMissing) return;
        using var repo = new GitRepo();
        repo.Write(".gitignore", "*.log\n");
        repo.Git("add", ".gitignore");
        repo.Git("commit", "-q", "-m", "Add gitignore");
        repo.Write("build.log", "noise\n");

        var cache = GitMetaCache.Create(repo.Root);
        Assert.NotNull(cache);
        Assert.True(cache!.IsIgnored(repo.PathTo("build.log")));
        Assert.False(cache.IsIgnored(repo.PathTo(".gitignore"))); // it's in the index
    }

    [Fact]
    public void Lookup_OutsideRepo_ReturnsNull()
    {
        if (GitMissing) return;
        using var repo = new GitRepo();
        repo.Commit("in.txt", "x\n", "Add");

        var cache = GitMetaCache.Create(repo.Root);
        Assert.NotNull(cache);
        Assert.Null(cache!.Lookup("/some/other/place/in.txt"));
    }

    [Fact]
    public async Task CreateAsync_SingleCommit_PopulatesLookup()
    {
        if (GitMissing) return;
        using var repo = new GitRepo();
        repo.Commit("a.txt", "x\n", "Add a");

        var cache = await GitMetaCache.CreateAsync(repo.Root);
        Assert.NotNull(cache);

        var info = cache!.Lookup(repo.PathTo("a.txt"));
        Assert.NotNull(info);
        Assert.Equal(1, info!.Value.CommitCount);
        Assert.Equal("Add a", info.Value.LastCommitSubject);
    }

    [Fact]
    public void HasGitBinary_ReturnsTrueWhenGitPresent()
    {
        if (GitMissing) return;
        Assert.True(GitMetaCache.HasGitBinary());
    }
}

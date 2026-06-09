using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitMeta;

namespace GitMeta.Tests;

public sealed class GitMetaPoolTests
{
    private static bool GitMissing => !GitMetaCache.HasGitBinary();

    private static GitRepo SeededRepo()
    {
        var repo = new GitRepo();
        repo.Commit("a.md", "# a\n", "Add a");
        return repo;
    }

    [Fact]
    public void Get_ReusesCache_ForUnchangedHead()
    {
        if (GitMissing) return;
        using var repo = SeededRepo();
        var pool = new GitMetaPool();

        var c1 = pool.Get(repo.Root);
        var c2 = pool.Get(repo.Root);
        Assert.NotNull(c1);
        Assert.Same(c1, c2); // same instance, no rebuild
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Get_HeadChange_Rebuilds()
    {
        if (GitMissing) return;
        using var repo = SeededRepo();
        var pool = new GitMetaPool();

        var c1 = pool.Get(repo.Root);
        Assert.NotNull(c1);
        var firstHead = c1!.HeadSha;

        repo.Commit("b.md", "# b\n", "Add b");

        var c2 = pool.Get(repo.Root);
        Assert.NotNull(c2);
        Assert.NotSame(c1, c2);
        Assert.NotEqual(firstHead, c2!.HeadSha);
        Assert.NotNull(c2.Lookup(repo.PathTo("b.md")));
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public async Task GetAsync_ReusesCache_ForUnchangedHead()
    {
        if (GitMissing) return;
        using var repo = SeededRepo();
        var pool = new GitMetaPool();

        var c1 = await pool.GetAsync(repo.Root);
        var c2 = await pool.GetAsync(repo.Root);
        Assert.NotNull(c1);
        Assert.Same(c1, c2);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Get_NonGitTree_ReturnsNull_StoresNothing()
    {
        if (GitMissing) return;
        var dir = Path.Combine(Path.GetTempPath(), "gitmeta-tests", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pool = new GitMetaPool();
            Assert.Null(pool.Get(dir));
            Assert.Equal(0, pool.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Warm_Primes()
    {
        if (GitMissing) return;
        using var repo = SeededRepo();
        var pool = new GitMetaPool();

        pool.Warm(repo.Root);
        Assert.Equal(1, pool.Count);

        var c = pool.Get(repo.Root);
        Assert.NotNull(c);
        Assert.NotEmpty(c!.HeadSha);
    }

    [Fact]
    public async Task Get_Concurrent_IsRaceFree()
    {
        if (GitMissing) return;
        using var repo = SeededRepo();
        var pool = new GitMetaPool();

        const int n = 8;
        var tasks = Enumerable.Range(0, n)
            .Select(_ => Task.Run(() => pool.Get(repo.Root)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, c => Assert.NotNull(c));
        // Racing first-builders may transiently differ, but exactly one entry
        // survives.
        Assert.Equal(1, pool.Count);
    }
}

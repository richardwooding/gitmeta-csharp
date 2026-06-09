using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitMeta;

/// <summary>
/// Caches one <see cref="GitMetaCache"/> per canonical repository root,
/// refreshed when HEAD changes. Safe for concurrent use.
/// </summary>
/// <remarks>
/// Designed to live for a long-running process (server, watcher, language
/// tooling) so many metadata queries against the same repo share one scan.
/// HEAD invalidation runs on every <see cref="Get"/> — one
/// <c>git rev-parse HEAD</c> per call — so a commit or checkout between calls
/// is picked up without operator action.
/// </remarks>
public sealed class GitMetaPool
{
    private static readonly string[] RevParseToplevelArgs = { "rev-parse", "--show-toplevel" };
    private static readonly string[] RevParseHeadArgs = { "rev-parse", "HEAD" };

    private readonly object _gate = new();
    private readonly Dictionary<string, GitMetaCache> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of cached entries.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>
    /// Returns a <see cref="GitMetaCache"/> for the git tree containing
    /// <paramref name="root"/>. On a hit with matching HEAD the cached instance
    /// is returned unchanged; otherwise it is rebuilt and stored. Returns
    /// <c>null</c> for a non-git tree — the same contract as
    /// <see cref="GitMetaCache.Create"/>.
    /// </summary>
    public GitMetaCache? Get(string root, CancellationToken cancellationToken = default)
    {
        string canonical;
        try
        {
            canonical = GitProcess.Run(root, RevParseToplevelArgs, cancellationToken).Trim();
        }
        catch (GitCommandException)
        {
            return null;
        }
        if (canonical.Length == 0)
            return null;

        if (TryGetFresh(canonical, () => SafeHead(canonical, cancellationToken), out var hit))
            return hit;

        var fresh = GitMetaCache.Create(root, cancellationToken);
        return fresh is null ? null : Store(canonical, fresh);
    }

    /// <summary>Asynchronous counterpart of <see cref="Get"/>.</summary>
    public async Task<GitMetaCache?> GetAsync(string root, CancellationToken cancellationToken = default)
    {
        string canonical;
        try
        {
            canonical = (await GitProcess.RunAsync(root, RevParseToplevelArgs, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (GitCommandException)
        {
            return null;
        }
        if (canonical.Length == 0)
            return null;

        GitMetaCache? existing;
        lock (_gate)
            _entries.TryGetValue(canonical, out existing);
        if (existing is not null)
        {
            var head = await SafeHeadAsync(canonical, cancellationToken).ConfigureAwait(false);
            if (head is not null && string.Equals(head, existing.HeadSha, StringComparison.Ordinal))
                return existing;
        }

        var fresh = await GitMetaCache.CreateAsync(root, cancellationToken).ConfigureAwait(false);
        return fresh is null ? null : Store(canonical, fresh);
    }

    /// <summary>Pre-builds the cache for <paramref name="root"/> so the first
    /// real query doesn't pay the scan cost.</summary>
    public void Warm(string root, CancellationToken cancellationToken = default)
        => Get(root, cancellationToken);

    /// <summary>Asynchronous counterpart of <see cref="Warm"/>.</summary>
    public Task WarmAsync(string root, CancellationToken cancellationToken = default)
        => GetAsync(root, cancellationToken);

    private bool TryGetFresh(string canonical, Func<string?> headProbe, out GitMetaCache? cache)
    {
        GitMetaCache? existing;
        lock (_gate)
            _entries.TryGetValue(canonical, out existing);
        if (existing is not null)
        {
            var head = headProbe();
            if (head is not null && string.Equals(head, existing.HeadSha, StringComparison.Ordinal))
            {
                cache = existing;
                return true;
            }
        }
        cache = null;
        return false;
    }

    private GitMetaCache Store(string canonical, GitMetaCache fresh)
    {
        lock (_gate)
        {
            // Re-check under the lock: a racing builder may have stored an
            // equivalent entry. The discarded build is reclaimed by the GC.
            if (_entries.TryGetValue(canonical, out var current) &&
                string.Equals(current.HeadSha, fresh.HeadSha, StringComparison.Ordinal))
            {
                return current;
            }
            _entries[canonical] = fresh;
            return fresh;
        }
    }

    private static string? SafeHead(string canonical, CancellationToken cancellationToken)
    {
        try
        {
            return GitProcess.Run(canonical, RevParseHeadArgs, cancellationToken).Trim();
        }
        catch (GitCommandException)
        {
            return null;
        }
    }

    private static async Task<string?> SafeHeadAsync(string canonical, CancellationToken cancellationToken)
    {
        try
        {
            return (await GitProcess.RunAsync(canonical, RevParseHeadArgs, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (GitCommandException)
        {
            return null;
        }
    }
}

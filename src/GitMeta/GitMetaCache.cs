using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GitMeta;

/// <summary>
/// Per-repository scan result. Build via <see cref="Create"/> /
/// <see cref="CreateAsync"/>; consult via <see cref="Lookup"/> /
/// <see cref="IsTracked"/> / <see cref="IsIgnored"/>.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="GitMetaCache"/> scans the whole working tree up front
/// (<c>git ls-files</c>, <c>git ls-files --others --ignored</c>, and a single
/// <c>git log</c> pass keyed by HEAD), then answers per-path lookups from
/// in-memory maps in constant time — dramatically cheaper than per-file
/// <c>git log -1 -- &lt;path&gt;</c> on any non-trivial repo.
/// </para>
/// <para>
/// <see cref="Create"/> returns <c>null</c> (not an exception) when the supplied
/// root isn't inside a git working tree, or when the <c>git</c> binary isn't on
/// PATH — callers must treat <c>null</c> as "no git data; leave fields at their
/// zero values". Hard failures on a present-but-broken git throw
/// <see cref="GitCommandException"/>.
/// </para>
/// <para>
/// The instance is immutable after construction and safe for concurrent reads.
/// Ported from the Go library github.com/richardwooding/gitmeta.
/// </para>
/// </remarks>
public sealed class GitMetaCache
{
    private static readonly string[] RevParseToplevelArgs = { "rev-parse", "--show-toplevel" };
    private static readonly string[] RevParseHeadArgs = { "rev-parse", "HEAD" };
    private static readonly string[] LsTrackedArgs = { "ls-files", "-z" };
    private static readonly string[] LsIgnoredArgs = { "ls-files", "--others", "--ignored", "--exclude-standard", "-z" };
    private static readonly string[] LogArgs =
    {
        "log", "--name-only", "--format=COMMIT\t%H\t%at\t%an\t%s", "--no-renames", "HEAD",
    };

    private readonly string _repoRoot;
    private readonly string? _repoRootAlt;
    private readonly IReadOnlyDictionary<string, FileGitInfo> _files;
    private readonly IReadOnlySet<string> _tracked;
    private readonly IReadOnlySet<string> _ignored;

    private GitMetaCache(
        string repoRoot,
        string? repoRootAlt,
        string headSha,
        Dictionary<string, FileGitInfo> files,
        HashSet<string> tracked,
        HashSet<string> ignored)
    {
        _repoRoot = repoRoot;
        _repoRootAlt = repoRootAlt;
        HeadSha = headSha;
        _files = files;
        _tracked = tracked;
        _ignored = ignored;
    }

    /// <summary>
    /// The repository's top-level absolute directory (git's canonical
    /// <c>rev-parse --show-toplevel</c>). On macOS this is the realpath form
    /// (e.g. <c>/private/tmp/...</c>), which can differ from a symlinked
    /// <c>/tmp/...</c> form a caller might pass.
    /// </summary>
    public string RepoRoot => _repoRoot;

    /// <summary>
    /// The HEAD commit SHA when the cache was built, or an empty string for a
    /// freshly-initialised empty repo. Useful for cross-process invalidation.
    /// </summary>
    public string HeadSha { get; }

    /// <summary>Returns <c>true</c> when the <c>git</c> executable can be launched.</summary>
    public static bool HasGitBinary()
    {
        try
        {
            GitProcess.Run(Environment.CurrentDirectory, new[] { "--version" }, CancellationToken.None);
            return true;
        }
        catch (GitCommandException)
        {
            return false;
        }
    }

    /// <summary>
    /// Scans the git working tree containing <paramref name="root"/>. Returns
    /// <c>null</c> when <paramref name="root"/> is not inside a git tree or git
    /// is absent (the silent-skip path). Throws <see cref="GitCommandException"/>
    /// on a hard failure.
    /// </summary>
    public static GitMetaCache? Create(string root, CancellationToken cancellationToken = default)
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

        string head;
        try
        {
            head = GitProcess.Run(canonical, RevParseHeadArgs, cancellationToken).Trim();
        }
        catch (GitCommandException)
        {
            // Empty repo (initialised, no commits): no HEAD/log, but ls-files
            // still works on the index, and ignore detection needs no HEAD.
            string trackedRaw;
            try
            {
                trackedRaw = GitProcess.Run(canonical, LsTrackedArgs, cancellationToken);
            }
            catch (GitCommandException)
            {
                return null;
            }
            string? ignoredRaw;
            try
            {
                ignoredRaw = GitProcess.Run(canonical, LsIgnoredArgs, cancellationToken);
            }
            catch (GitCommandException)
            {
                ignoredRaw = null;
            }
            return Build(root, canonical, null, trackedRaw, ignoredRaw, null);
        }

        var tracked = GitProcess.Run(canonical, LsTrackedArgs, cancellationToken);
        var ignored = GitProcess.Run(canonical, LsIgnoredArgs, cancellationToken);
        var log = GitProcess.Run(canonical, LogArgs, cancellationToken);
        return Build(root, canonical, head, tracked, ignored, log);
    }

    /// <summary>Asynchronous counterpart of <see cref="Create"/>.</summary>
    public static async Task<GitMetaCache?> CreateAsync(string root, CancellationToken cancellationToken = default)
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

        string head;
        try
        {
            head = (await GitProcess.RunAsync(canonical, RevParseHeadArgs, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (GitCommandException)
        {
            string trackedRaw;
            try
            {
                trackedRaw = await GitProcess.RunAsync(canonical, LsTrackedArgs, cancellationToken).ConfigureAwait(false);
            }
            catch (GitCommandException)
            {
                return null;
            }
            string? ignoredRaw;
            try
            {
                ignoredRaw = await GitProcess.RunAsync(canonical, LsIgnoredArgs, cancellationToken).ConfigureAwait(false);
            }
            catch (GitCommandException)
            {
                ignoredRaw = null;
            }
            return Build(root, canonical, null, trackedRaw, ignoredRaw, null);
        }

        var tracked = await GitProcess.RunAsync(canonical, LsTrackedArgs, cancellationToken).ConfigureAwait(false);
        var ignored = await GitProcess.RunAsync(canonical, LsIgnoredArgs, cancellationToken).ConfigureAwait(false);
        var log = await GitProcess.RunAsync(canonical, LogArgs, cancellationToken).ConfigureAwait(false);
        return Build(root, canonical, head, tracked, ignored, log);
    }

    /// <summary>
    /// Returns git metadata for <paramref name="absolutePath"/>, or <c>null</c>
    /// when it isn't tracked by git in this working tree (untracked, ignored, or
    /// outside the repo).
    /// </summary>
    public FileGitInfo? Lookup(string absolutePath)
    {
        var rel = ToRel(absolutePath);
        if (rel is null)
            return null;
        return _files.TryGetValue(rel, out var info) ? info : null;
    }

    /// <summary>
    /// Try-pattern form of <see cref="Lookup"/>. Returns <c>true</c> and sets
    /// <paramref name="info"/> when metadata exists.
    /// </summary>
    public bool TryLookup(string absolutePath, out FileGitInfo info)
    {
        var result = Lookup(absolutePath);
        info = result ?? default;
        return result.HasValue;
    }

    /// <summary>True when <paramref name="absolutePath"/> is in git's index.</summary>
    public bool IsTracked(string absolutePath)
    {
        var rel = ToRel(absolutePath);
        return rel is not null && _tracked.Contains(rel);
    }

    /// <summary>
    /// True when <paramref name="absolutePath"/> is matched by a git ignore rule
    /// but not in the index. Tracked files are never reported as ignored, matching
    /// git's own <c>check-ignore</c> semantics.
    /// </summary>
    public bool IsIgnored(string absolutePath)
    {
        var rel = ToRel(absolutePath);
        return rel is not null && _ignored.Contains(rel);
    }

    private static GitMetaCache Build(
        string userRoot,
        string canonical,
        string? headSha,
        string trackedRaw,
        string? ignoredRaw,
        string? logRaw)
    {
        var tracked = SplitNul(trackedRaw);
        var ignored = ignoredRaw is null ? new HashSet<string>(StringComparer.Ordinal) : SplitNul(ignoredRaw);
        var files = logRaw is null
            ? new Dictionary<string, FileGitInfo>(StringComparer.Ordinal)
            : ParseLog(logRaw);
        return new GitMetaCache(canonical, AltRoot(userRoot, canonical), headSha ?? string.Empty, files, tracked, ignored);
    }

    /// <summary>
    /// Parses one <c>git log --name-only</c> pass. Commits arrive newest-first,
    /// so the first sighting of a path fixes its last-commit fields, while every
    /// sighting overwrites first-seen (the oldest wins) and bumps the count.
    /// </summary>
    private static Dictionary<string, FileGitInfo> ParseLog(string raw)
    {
        var files = new Dictionary<string, FileGitInfo>(StringComparer.Ordinal);
        var curTime = default(DateTimeOffset);
        var curAuthor = string.Empty;
        var curSubject = string.Empty;
        var haveCommit = false;

        foreach (var line in raw.Split('\n'))
        {
            if (line.Length == 0)
                continue;
            if (line.StartsWith("COMMIT\t", StringComparison.Ordinal))
            {
                if (TryParseCommit(line.Substring("COMMIT\t".Length), out curTime, out curAuthor, out curSubject))
                    haveCommit = true;
                continue;
            }
            if (!haveCommit)
                continue;

            if (files.TryGetValue(line, out var info))
            {
                files[line] = info with { FirstSeen = curTime, CommitCount = info.CommitCount + 1 };
            }
            else
            {
                files[line] = new FileGitInfo
                {
                    LastCommitTime = curTime,
                    LastCommitAuthor = curAuthor,
                    LastCommitSubject = curSubject,
                    FirstSeen = curTime,
                    CommitCount = 1,
                };
            }
        }
        return files;
    }

    /// <summary>
    /// Parses <c>&lt;sha&gt;\t&lt;unix-time&gt;\t&lt;author&gt;\t&lt;subject&gt;</c>.
    /// The subject keeps any embedded tabs, matching the Go original.
    /// </summary>
    private static bool TryParseCommit(string rest, out DateTimeOffset time, out string author, out string subject)
    {
        time = default;
        author = string.Empty;
        subject = string.Empty;

        var t1 = rest.IndexOf('\t');
        if (t1 < 0)
            return false;
        var afterSha = rest.Substring(t1 + 1);
        var t2 = afterSha.IndexOf('\t');
        if (t2 < 0)
            return false;
        var tsText = afterSha.Substring(0, t2);
        var afterTs = afterSha.Substring(t2 + 1);
        var t3 = afterTs.IndexOf('\t');
        if (t3 < 0)
            return false;

        if (!long.TryParse(tsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            return false;

        time = DateTimeOffset.FromUnixTimeSeconds(unix);
        author = afterTs.Substring(0, t3);
        subject = afterTs.Substring(t3 + 1);
        return true;
    }

    /// <summary>Splits NUL-delimited <c>-z</c> output, dropping empty records.</summary>
    private static HashSet<string> SplitNul(string raw)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (raw.Length == 0)
            return set;
        foreach (var entry in raw.Split('\0'))
        {
            if (entry.Length != 0)
                set.Add(entry);
        }
        return set;
    }

    /// <summary>
    /// The as-supplied root as an absolute, forward-slash path when it differs
    /// from git's canonical view (the macOS /tmp ↔ /private/tmp symlink case),
    /// else <c>null</c>. <see cref="Path.GetFullPath(string)"/> normalises
    /// lexically without resolving symlinks, matching Go's <c>filepath.Abs</c>.
    /// </summary>
    private static string? AltRoot(string userRoot, string canonical)
    {
        string abs;
        try
        {
            abs = Path.GetFullPath(userRoot);
        }
        catch
        {
            return null;
        }
        var normalised = TrimTrailingSlash(abs.Replace('\\', '/'));
        return string.Equals(normalised, canonical, StringComparison.Ordinal) ? null : normalised;
    }

    private static string TrimTrailingSlash(string path)
    {
        var end = path.Length;
        while (end > 1 && path[end - 1] == '/')
            end--;
        return end == path.Length ? path : path.Substring(0, end);
    }

    /// <summary>
    /// Converts an absolute path to a forward-slash repo-relative key, or
    /// <c>null</c> when it isn't inside the repo. Tries the canonical root first
    /// then the as-supplied alt root (the macOS symlink fallback).
    /// </summary>
    private string? ToRel(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return null;
        var rel = RelUnder(_repoRoot, absolutePath);
        if (rel is not null)
            return rel;
        return _repoRootAlt is null ? null : RelUnder(_repoRootAlt, absolutePath);
    }

    private static string? RelUnder(string baseDir, string absolutePath)
    {
        if (baseDir.Length == 0)
            return null;
        var path = absolutePath.Replace('\\', '/');
        if (!path.StartsWith(baseDir, StringComparison.Ordinal))
            return null;
        if (path.Length == baseDir.Length)
            return string.Empty; // exactly the root
        if (path[baseDir.Length] != '/')
            return null; // sibling, not a child
        return path.Substring(baseDir.Length + 1);
    }
}

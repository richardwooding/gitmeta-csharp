using System;

namespace GitMeta;

/// <summary>
/// Per-file git metadata resolved by <see cref="GitMetaCache"/> for a tracked
/// path. Zero values are meaningful: a fresh commit's <see cref="CommitCount"/>
/// is 1 and <see cref="FirstSeen"/> equals <see cref="LastCommitTime"/>.
/// </summary>
public readonly record struct FileGitInfo
{
    /// <summary>Timestamp (UTC) of the most recent commit touching the file.</summary>
    public DateTimeOffset LastCommitTime { get; init; }

    /// <summary>Author name of the most recent commit touching the file.</summary>
    public string LastCommitAuthor { get; init; }

    /// <summary>Subject (first line) of the most recent commit touching the file.</summary>
    public string LastCommitSubject { get; init; }

    /// <summary>Timestamp (UTC) of the oldest commit that touched the file.</summary>
    public DateTimeOffset FirstSeen { get; init; }

    /// <summary>Number of commits that touched the file (a churn proxy).</summary>
    public int CommitCount { get; init; }
}

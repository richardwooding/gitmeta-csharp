using System;

namespace GitMeta;

/// <summary>
/// Thrown when a git subprocess fails (a non-zero exit, or the git binary
/// could not be started). The message carries the command's stderr where
/// available. Callers that construct a <see cref="GitMetaCache"/> generally
/// never see this for the "not a git tree / no git" case — that degrades to a
/// <c>null</c> cache — but ls-files / log failures on a present-but-broken git
/// surface as this exception.
/// </summary>
public sealed class GitCommandException : Exception
{
    /// <summary>Creates the exception with a diagnostic message.</summary>
    public GitCommandException(string message) : base(message) { }

    /// <summary>Creates the exception with a diagnostic message and inner cause.</summary>
    public GitCommandException(string message, Exception innerException)
        : base(message, innerException) { }
}

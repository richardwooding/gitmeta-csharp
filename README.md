# GitMeta

[![CI](https://github.com/richardwooding/gitmeta-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/richardwooding/gitmeta-csharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512bd4.svg)](https://dotnet.microsoft.com/)

Fast **per-file git metadata** for .NET — last-commit time / author / subject,
first-seen, commit count (churn), and tracked / ignored status — resolved by
scanning a working tree **once** and answering per-path lookups in constant
time. **Zero dependencies** (shells out to the system `git` binary).

The batch design is the point: one scan runs `git ls-files` + a single
`git log` pass up front, so a 10k-file / 5k-commit repo costs **one** git
invocation (~½ s) instead of 10k `git log -1 -- <path>` calls (~100 s).

This is a C# port of the Go library
[richardwooding/gitmeta](https://github.com/richardwooding/gitmeta).

## Install

```sh
dotnet add package GitMeta
```

## One-shot cache

`Create` returns `null` when `root` isn't a git working tree (or git isn't on
PATH) — treat that as "no git data", not an error. Hard failures on a
present-but-broken git throw `GitCommandException`.

```csharp
using GitMeta;

var cache = GitMetaCache.Create("/path/to/repo");
if (cache is null)
    return; // not a git working tree

if (cache.Lookup("/path/to/repo/Program.cs") is { } info)
{
    Console.WriteLine($"{info.LastCommitTime:yyyy-MM-dd} " +
                      $"{info.LastCommitAuthor} — {info.CommitCount} commits");
}

cache.IsTracked(path); // bool
cache.IsIgnored(path); // bool
```

`Lookup` returns a nullable `FileGitInfo`:

```csharp
public readonly record struct FileGitInfo
{
    public DateTimeOffset LastCommitTime { get; init; } // UTC
    public string LastCommitAuthor { get; init; }
    public string LastCommitSubject { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public int CommitCount { get; init; }               // churn proxy
}
```

Why git rather than filesystem timestamps? A fresh clone sets every file's
mtime to checkout time — so "recently changed" / "hot file" questions need git
history, not the filesystem.

## Async

Every I/O entry point has an async counterpart taking a `CancellationToken`;
the in-memory `Lookup` / `IsTracked` / `IsIgnored` stay synchronous.

```csharp
var cache = await GitMetaCache.CreateAsync("/path/to/repo", cancellationToken);
```

## Pool — reuse across calls

A `GitMetaPool` keeps one cache per repo and **re-validates on HEAD change**,
so repeated lookups over an unchanging tree don't re-scan. Ideal for a
long-running process (server, watcher, analyzer) answering many queries. Safe
for concurrent use.

```csharp
var pool = new GitMetaPool();
var cache = pool.Get(root);                 // built once per repo, refreshed when HEAD moves
var cache2 = await pool.GetAsync(root, ct);  // async variant
```

## Requirements

- **.NET 8.0+**, zero third-party dependencies.
- The system **`git`** binary on `PATH` (`GitMetaCache.HasGitBinary()` reports
  its presence; `Create` returns `null` when git is absent or the path isn't a
  working tree).
- Built and tested on Linux / macOS. POSIX-style path handling; Windows is not
  a primary target.

## Differences from the Go original

- Both **synchronous and asynchronous** APIs (`Create` / `CreateAsync`,
  `Get` / `GetAsync`), each accepting a `CancellationToken` in place of Go's
  `context.Context`.
- Times are `DateTimeOffset` (UTC) rather than `time.Time`.
- The Go nil-`*Cache` contract becomes a nullable return: `Create` yields
  `GitMetaCache?`, and `null` is the "no git data" signal.

## License

MIT — see [LICENSE](LICENSE).

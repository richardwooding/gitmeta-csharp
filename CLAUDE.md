# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`GitMeta` is a zero-dependency C# library that resolves **per-file git metadata** (last-commit time/author/subject, first-seen, commit count, tracked/ignored status) by scanning a working tree once and answering per-path lookups in constant time. It shells out to the system `git` binary — no third-party deps. It is a port of the Go library [richardwooding/gitmeta](https://github.com/richardwooding/gitmeta); consult that repo for the original design notes.

## Commands

```sh
dotnet build                                  # build the solution
dotnet test                                   # build + run all tests
dotnet test -c Release --no-build             # tests (after a Release build)
dotnet format --verify-no-changes             # formatting gate (CI enforces it)
dotnet format                                  # auto-format
dotnet pack -c Release src/GitMeta/GitMeta.csproj   # produce the NuGet package
```

The library targets **net8.0** (LTS, broad consumer reach). The SDK is pinned in `global.json` to **10.0.300** (`rollForward: latestMinor`); CI installs it via `global-json-file`. The test project also targets net8.0 but sets `<RollForward>Major</RollForward>` so the test host runs on a newer installed runtime (e.g. .NET 10) when no 8.x runtime is present.

Tests create throwaway git repos under the OS temp dir and make real commits, so a git identity must exist (CI sets a global one). Tests early-return when `git` isn't on PATH (`GitMetaCache.HasGitBinary()`). Multi-commit tests pin commit timestamps with `git commit --date=@<unix>` so they're deterministic without sleeping. The test project sees library internals via `<InternalsVisibleTo Include="GitMeta.Tests" />` and reuses `GitProcess` to drive git.

## Architecture

The whole design rests on **batching git invocations**. The naive approach — `git log -1 -- <path>` per file — is O(files) subprocesses. Instead the scan runs a fixed handful of git commands once and builds in-memory maps, making each subsequent `Lookup`/`IsTracked`/`IsIgnored` a dictionary read.

Source files (namespace `GitMeta`, in `src/GitMeta/`):

- **`FileGitInfo.cs`** — the per-file metadata `readonly record struct`. Times are `DateTimeOffset` (UTC).
- **`GitProcess.cs`** — `internal static` git runner. `Run` (sync) and `RunAsync` (async) share the same start/collect/validate shape: both read stdout+stderr concurrently to avoid a pipe deadlock, register the `CancellationToken` to kill the process, and throw `GitCommandException` on a non-zero exit (or a failure to start git).
- **`GitMetaCache.cs`** — the per-repo scan result. `Create` / `CreateAsync` (returning `GitMetaCache?`) run `rev-parse --show-toplevel`, `rev-parse HEAD`, two `ls-files` passes (tracked; others+ignored), and one `git log --name-only` pass, then hand the raw strings to a shared `Build`/`ParseLog`/`SplitNul` core. The log is parsed newest-first: the first sighting of a path fixes `LastCommit*`, every sighting overwrites `FirstSeen` (oldest wins) and bumps `CommitCount`. Also holds `HasGitBinary` and the dual-root path resolution.
- **`GitMetaPool.cs`** — caches one `GitMetaCache` per canonical repo root, keyed by `git rev-parse --show-toplevel`. `Get` / `GetAsync` re-run `rev-parse HEAD` each call and rebuild only when HEAD moved. Thread-safe via a lock; a racing rebuild's loser is discarded (reclaimed by the GC).

### Invariants

1. **`null` cache means "no git data", not an error.** `Create` returns `null` (not an exception) when `root` isn't in a git tree or `git` is absent — the *common, expected* path. This is the C# analogue of the Go original's `(nil, nil)`. ls-files/log failures on a present-but-broken git throw `GitCommandException`; cancellation throws `OperationCanceledException`. An empty repo (init, no commits) yields a non-null cache with empty file metadata so tracked/ignored still answer.

2. **Immutable after construction.** A `GitMetaCache` exposes read-only dictionaries/sets and is safe for concurrent reads. Pool entries are shared instances.

3. **Dual-root path resolution for the macOS symlink case.** git canonicalizes `/tmp/...` to `/private/tmp/...`, but a caller's path often retains the symlinked form. The cache stores both `RepoRoot` (git's canonical view) and an alt root (`Path.GetFullPath` of the as-supplied root — lexical, does not resolve symlinks — when it differs). `ToRel` tries both prefixes. Keys throughout are repo-relative forward-slash paths (the form `ls-files` emits); absolute inputs are normalised to forward slashes before comparison.

### Sync/async duplication

`Create`/`CreateAsync` and `Get`/`GetAsync` are deliberate mirror images: identical control flow, differing only in `GitProcess.Run` vs `await GitProcess.RunAsync`. All parsing/build logic is pure and shared. When changing the fetch/branching logic, update **both** paths.

## Non-goals

- Windows is not a primary target (POSIX-style path handling).
- No NuGet publish in CI (the package is produced by `dotnet pack` but not pushed).

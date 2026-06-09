using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitMeta;

/// <summary>
/// Runs the system <c>git</c> binary and returns its standard output. The
/// working directory is set per call so commands run against the right repo
/// regardless of the process cwd. Both blocking and async variants share the
/// same start/collect/validate shape.
/// </summary>
internal static class GitProcess
{
    private static ProcessStartInfo BuildStartInfo(string workingDirectory, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private static Process Start(string workingDirectory, string[] args)
    {
        var process = new Process { StartInfo = BuildStartInfo(workingDirectory, args) };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            // Covers "git not on PATH" and an invalid working directory.
            throw new GitCommandException($"gitmeta: could not start git: {ex.Message}", ex);
        }
        return process;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort teardown; ignore races and platform quirks.
        }
    }

    /// <summary>Runs git synchronously and returns stdout. Throws
    /// <see cref="GitCommandException"/> on a non-zero exit.</summary>
    public static string Run(string workingDirectory, string[] args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Start(workingDirectory, args);
        using var registration = cancellationToken.Register(static state => TryKill((Process)state!), process);

        // Read both streams concurrently to avoid a pipe-buffer deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw Failure(args, process.ExitCode, stderr);
        return stdout;
    }

    /// <summary>Runs git asynchronously and returns stdout. Throws
    /// <see cref="GitCommandException"/> on a non-zero exit.</summary>
    public static async Task<string> RunAsync(string workingDirectory, string[] args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Start(workingDirectory, args);
        using var registration = cancellationToken.Register(static state => TryKill((Process)state!), process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw Failure(args, process.ExitCode, stderr);
        return stdout;
    }

    private static GitCommandException Failure(string[] args, int exitCode, string stderr)
        => new($"gitmeta: git {string.Join(' ', args)} exited {exitCode}: {stderr.Trim()}");
}

using System.Diagnostics;

namespace SimpleVCLib;

/// <summary>
/// The result of one CLI invocation, as seen by a command-runner override
/// (<see cref="VCLib.SetCommandRunner"/>).
/// </summary>
public sealed record CommandResult(int ExitCode, string Output, string Error);

internal static class CommandRunner
{
    internal record Result(int ExitCode, string Output, string Error);

    private static Func<string, string[], CommandResult>? _override;

    /// <summary>
    /// Override the runner for all VC operations - lets tests inject canned CLI
    /// output (e.g. <c>p4 -ztag fstat</c> transcripts) so provider logic is
    /// unit-testable without the VCS installed. Null restores real execution.
    /// </summary>
    internal static void SetOverride(Func<string, string[], CommandResult>? runner) =>
        _override = runner;

    /// <summary>
    /// Run a CLI command synchronously, capturing stdout and stderr.
    /// Arguments are passed as an array to avoid shell injection and handle spaces in paths.
    /// </summary>
    internal static Result Run(string command, string[] args, int timeoutMs = 10000,
                               string? workingDirectory = null, bool trimOutput = true)
    {
        if (_override is not null)
        {
            var canned = _override(command, args);
            return new Result(canned.ExitCode, canned.Output, canned.Error);
        }

        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start process: {command}");

            // Read output asynchronously to prevent deadlocks on large output.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return new Result(-1, "", "Command timed out");
            }

            // WaitForExit(ms) does not guarantee async streams are flushed; call again.
            process.WaitForExit();

            // Output is trimmed by default for convenience; pass trimOutput:false when
            // byte exactness matters (e.g. `git status -z`, whose first entry can begin
            // with a significant space that trimming would strip).
            var output = trimOutput ? outputTask.Result.Trim() : outputTask.Result;
            return new Result(process.ExitCode, output, errorTask.Result.Trim());
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message);
        }
    }

    /// <summary>
    /// Async twin of <see cref="Run"/> - same result, awaited without blocking a thread.
    /// Uses ConfigureAwait(false) throughout so the sync wrappers that bridge to it cannot
    /// deadlock on a caller's synchronization context.
    /// </summary>
    internal static async Task<Result> RunAsync(string command, string[] args, int timeoutMs = 10000,
                                                string? workingDirectory = null, bool trimOutput = true)
    {
        if (_override is not null)
        {
            var canned = _override(command, args);
            return new Result(canned.ExitCode, canned.Output, canned.Error);
        }

        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start process: {command}");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new Result(-1, "", "Command timed out");
            }

            var stdout = await outputTask.ConfigureAwait(false);
            var stderr = await errorTask.ConfigureAwait(false);
            return new Result(process.ExitCode, trimOutput ? stdout.Trim() : stdout, stderr.Trim());
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message);
        }
    }
}

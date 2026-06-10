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
                               string? workingDirectory = null)
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

            return new Result(process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message);
        }
    }
}

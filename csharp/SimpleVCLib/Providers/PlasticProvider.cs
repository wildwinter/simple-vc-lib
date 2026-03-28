namespace SimpleVCLib;

/// <summary>
/// Plastic SCM / Unity Version Control provider.
/// Uses the <c>cm</c> CLI (Plastic SCM command-line client).
/// Files under Plastic SCM are read-only until checked out.
/// </summary>
public class PlasticProvider : IVCProvider
{
    private static readonly FilesystemProvider _fs = new();
    public string Name => "plastic";

    public VCResult PrepareToWrite(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (!IsTracked(filePath))
            return _fs.PrepareToWrite(filePath);

        var result = Cm(["co", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok();

        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked") || combined.Contains("exclusive"))
            return VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked");
        if (combined.Contains("out of date") || combined.Contains("not latest"))
            return VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date — update before editing");

        return VCResult.Error($"Cannot check out '{filePath}': {result.Error ?? result.Output}");
    }

    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        if (IsTracked(filePath)) return VCResult.Ok();

        var result = Cm(["add", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File added to Plastic SCM");
        return VCResult.Error($"Cannot add '{filePath}' to Plastic SCM: {result.Error ?? result.Output}");
    }

    public VCResult DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (IsTracked(filePath))
        {
            var result = Cm(["remove", filePath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot delete '{filePath}' from Plastic SCM: {result.Error ?? result.Output}");
        }

        return _fs.DeleteFile(filePath);
    }

    public VCResult DeleteFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return VCResult.Ok();

        if (IsTracked(folderPath))
        {
            var result = Cm(["remove", folderPath]);
            if (result.ExitCode != 0)
                return VCResult.Error($"Cannot delete folder '{folderPath}' from Plastic SCM: {result.Error ?? result.Output}");
        }

        if (Directory.Exists(folderPath))
            return _fs.DeleteFolder(folderPath);

        return VCResult.Ok();
    }

    // -------------------------------------------------------------------------

    private static bool IsTracked(string path)
    {
        var result = Cm(["status", "--short", path]);
        if (result.ExitCode != 0) return false;
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 && !lines[0].TrimStart().StartsWith('?');
    }

    private static CommandRunner.Result Cm(string[] args) =>
        CommandRunner.Run("cm", args);
}

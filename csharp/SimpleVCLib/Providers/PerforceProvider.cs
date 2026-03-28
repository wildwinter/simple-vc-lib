namespace SimpleVCLib;

/// <summary>
/// Perforce (Helix Core) provider.
/// Uses the <c>p4</c> CLI. Assumes the workspace is already configured in the
/// environment (P4PORT, P4USER, P4CLIENT, or via p4 config / tickets).
/// </summary>
public class PerforceProvider : IVCProvider
{
    private static readonly FilesystemProvider _fs = new();
    public string Name => "perforce";

    public VCResult PrepareToWrite(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (!IsInDepot(filePath))
            return _fs.PrepareToWrite(filePath);

        var result = P4(["edit", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok();

        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked by"))
            return VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked by another user");
        if (combined.Contains("out of date"))
            return VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date — sync before editing");

        return VCResult.Error($"Cannot open '{filePath}' for editing: {result.Error ?? result.Output}");
    }

    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        var fstat = Fstat(filePath);
        if (fstat is not null) return VCResult.Ok();

        var result = P4(["add", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File opened for add in Perforce");
        return VCResult.Error($"Cannot add '{filePath}' to Perforce: {result.Error ?? result.Output}");
    }

    public VCResult DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (IsInDepot(filePath))
        {
            var result = P4(["delete", filePath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot delete '{filePath}' from Perforce: {result.Error ?? result.Output}");
        }

        return _fs.DeleteFile(filePath);
    }

    public VCResult DeleteFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return VCResult.Ok();

        // The /... wildcard schedules the entire depot subtree for deletion.
        var depotPath = folderPath.Replace('\\', '/') + "/...";
        P4(["delete", depotPath]); // Non-tracked paths return non-zero; that is expected.

        if (Directory.Exists(folderPath))
            return _fs.DeleteFolder(folderPath);

        return VCResult.Ok();
    }

    // -------------------------------------------------------------------------

    private static string? Fstat(string filePath)
    {
        var result = P4(["fstat", filePath]);
        return result.ExitCode == 0 ? result.Output : null;
    }

    private static bool IsInDepot(string filePath) =>
        Fstat(filePath) is not null;

    private static CommandRunner.Result P4(string[] args) =>
        CommandRunner.Run("p4", args);
}

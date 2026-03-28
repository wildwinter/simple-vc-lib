namespace SimpleVCLib;

/// <summary>
/// Subversion (SVN) provider.
/// SVN files are normally writable. Files with the <c>svn:needs-lock</c> property
/// are read-only until locked; PrepareToWrite handles this via <c>svn lock</c>.
/// </summary>
public class SvnProvider : IVCProvider
{
    private static readonly FilesystemProvider _fs = new();
    public string Name => "svn";

    public VCResult PrepareToWrite(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        var fsResult = _fs.PrepareToWrite(filePath);
        if (fsResult.Success) return VCResult.Ok();

        // File is read-only — only expected for files with svn:needs-lock set.
        if (!IsTracked(filePath))
            return VCResult.Error($"Cannot make file writable: {filePath}");

        var result = Svn(["lock", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File locked in SVN");

        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked by") || combined.Contains("steal lock"))
            return VCResult.Failure(VCStatus.Locked,
                $"File is locked by another user: {result.Error ?? result.Output}");
        if (combined.Contains("out of date"))
            return VCResult.Failure(VCStatus.OutOfDate,
                $"File is out of date — update before locking: {result.Error ?? result.Output}");

        return VCResult.Error($"svn lock failed: {result.Error ?? result.Output}");
    }

    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"File does not exist after write: {filePath}");

        if (IsTracked(filePath)) return VCResult.Ok();

        var result = Svn(["add", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File added to SVN");
        return VCResult.Error($"svn add failed: {result.Error ?? result.Output}");
    }

    public VCResult DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (IsTracked(filePath))
        {
            var result = Svn(["delete", "--force", filePath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"svn delete failed: {result.Error ?? result.Output}");
        }

        return _fs.DeleteFile(filePath);
    }

    public VCResult DeleteFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return VCResult.Ok();

        if (IsTracked(folderPath))
        {
            var result = Svn(["delete", "--force", folderPath]);
            if (result.ExitCode != 0)
                return VCResult.Error($"svn delete failed: {result.Error ?? result.Output}");
            return VCResult.Ok();
        }

        return _fs.DeleteFolder(folderPath);
    }

    // -------------------------------------------------------------------------

    private static bool IsTracked(string path)
    {
        var result = Svn(["info", path]);
        return result.ExitCode == 0;
    }

    private static CommandRunner.Result Svn(string[] args) =>
        CommandRunner.Run("svn", args);
}

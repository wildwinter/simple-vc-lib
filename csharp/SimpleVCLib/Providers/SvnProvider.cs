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
            return VCResult.Error($"Cannot make '{filePath}' writable");

        var result = Svn(["lock", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File locked in SVN");

        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked by") || combined.Contains("steal lock"))
            return VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked by another user");
        if (combined.Contains("out of date"))
            return VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date — update before locking");

        return VCResult.Error($"Cannot lock '{filePath}' in SVN: {result.Error ?? result.Output}");
    }

    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        if (IsTracked(filePath)) return VCResult.Ok();

        // An unversioned file inside a working copy has a versioned parent directory.
        // If the parent is not tracked either, the file is outside the working copy entirely.
        var parentDir = Path.GetDirectoryName(filePath);
        if (parentDir == null || !IsTracked(parentDir))
            return _fs.FinishedWrite(filePath);

        var result = Svn(["add", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File added to SVN");
        // File is ignored — treat as outside the working copy.
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return _fs.FinishedWrite(filePath);
        return VCResult.Error($"Cannot add '{filePath}' to SVN: {result.Error ?? result.Output}");
    }

    public VCResult DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (IsTracked(filePath))
        {
            var result = Svn(["delete", "--force", filePath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot delete '{filePath}' from SVN: {result.Error ?? result.Output}");
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
                return VCResult.Error($"Cannot delete folder '{folderPath}' from SVN: {result.Error ?? result.Output}");
            return VCResult.Ok();
        }

        return _fs.DeleteFolder(folderPath);
    }

    public VCResult RenameFile(string oldPath, string newPath)
    {
        if (!File.Exists(oldPath)) return VCResult.Ok();
        if (IsTracked(oldPath))
        {
            var result = Svn(["move", "--force", oldPath, newPath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot rename '{oldPath}' in SVN: {result.Error ?? result.Output}");
        }
        return _fs.RenameFile(oldPath, newPath);
    }

    public VCResult RenameFolder(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath)) return VCResult.Ok();
        if (IsTracked(oldPath))
        {
            var result = Svn(["move", "--force", oldPath, newPath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot rename folder '{oldPath}' in SVN: {result.Error ?? result.Output}");
        }
        return _fs.RenameFolder(oldPath, newPath);
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

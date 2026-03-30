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

        if (IsInDepot(filePath)) return VCResult.Ok();

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
            if (result.ExitCode != 0)
                return VCResult.Error($"Cannot delete '{filePath}' from Perforce: {result.Error ?? result.Output}");
            // p4 delete marks for deletion but leaves the file on disk as read-only.
            // Physically remove it, consistent with git rm and svn delete.
            if (File.Exists(filePath))
            {
                try
                {
                    new FileInfo(filePath).IsReadOnly = false;
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    return VCResult.Error($"Cannot remove '{filePath}' from disk: {ex.Message}");
                }
            }
            return VCResult.Ok();
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

    public VCResult RenameFile(string oldPath, string newPath)
    {
        if (!File.Exists(oldPath)) return VCResult.Ok();
        if (IsInDepot(oldPath))
        {
            var result = P4(["move", oldPath, newPath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot rename '{oldPath}' in Perforce: {result.Error ?? result.Output}");
        }
        return _fs.RenameFile(oldPath, newPath);
    }

    public VCResult RenameFolder(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath)) return VCResult.Ok();
        // p4 move with /... wildcard handles tracked files and physically moves them.
        var src = oldPath.Replace('\\', '/') + "/...";
        var dst = newPath.Replace('\\', '/') + "/...";
        P4(["move", src, dst]);
        // Move any untracked files that p4 left behind.
        if (Directory.Exists(oldPath))
        {
            try
            {
                CopyDirectory(oldPath, newPath);
                Directory.Delete(oldPath, recursive: true);
            }
            catch (Exception ex)
            {
                return VCResult.Error($"Cannot rename folder '{oldPath}': {ex.Message}");
            }
        }
        return VCResult.Ok();
    }

    // -------------------------------------------------------------------------

    private static string? Fstat(string filePath)
    {
        var result = P4(["fstat", filePath]);
        return result.ExitCode == 0 ? result.Output : null;
    }

    private static bool IsInDepot(string filePath)
    {
        var fstat = Fstat(filePath);
        // p4 fstat exits 0 for workspace-mapped files even if never submitted to the depot,
        // returning only clientFile/isMapped. Only treat the file as depot-tracked when
        // depot metadata (headRev or depotFile) is present.
        return fstat is not null && (fstat.Contains("headRev") || fstat.Contains("depotFile"));
    }

    private static CommandRunner.Result P4(string[] args) =>
        CommandRunner.Run("p4", args);

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }
}

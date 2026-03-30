namespace SimpleVCLib;

/// <summary>
/// Git provider.
/// Git does not use file locking, so PrepareToWrite only ensures the file is
/// writable at the OS level. FinishedWrite stages new files with git add.
/// </summary>
public class GitProvider : IVCProvider
{
    private static readonly FilesystemProvider _fs = new();
    public string Name => "git";

    public VCResult PrepareToWrite(string filePath)
    {
        return _fs.PrepareToWrite(filePath);
    }

    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        if (IsTracked(filePath)) return VCResult.Ok();

        var result = Git(["add", filePath], Path.GetDirectoryName(filePath)!);
        if (result.ExitCode == 0) return VCResult.Ok("File added to git");
        return VCResult.Error($"Cannot add '{filePath}' to git: {result.Error ?? result.Output}");
    }

    public VCResult DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (IsTracked(filePath))
        {
            var result = Git(["rm", "--force", filePath], Path.GetDirectoryName(filePath)!);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot delete '{filePath}' from git: {result.Error ?? result.Output}");
        }

        return _fs.DeleteFile(filePath);
    }

    public VCResult DeleteFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return VCResult.Ok();

        var listResult = Git(["ls-files", folderPath], folderPath);
        if (listResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(listResult.Output))
        {
            var rmResult = Git(["rm", "-r", "--force", folderPath], folderPath);
            if (rmResult.ExitCode != 0)
                return VCResult.Error($"Cannot delete folder '{folderPath}' from git: {rmResult.Error ?? rmResult.Output}");
        }

        // Delete any remaining untracked files git rm left behind.
        if (Directory.Exists(folderPath))
            return _fs.DeleteFolder(folderPath);

        return VCResult.Ok();
    }

    public VCResult RenameFile(string oldPath, string newPath)
    {
        if (!File.Exists(oldPath)) return VCResult.Ok();
        if (IsTracked(oldPath))
        {
            var result = Git(["mv", oldPath, newPath], Path.GetDirectoryName(oldPath)!);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot rename '{oldPath}' in git: {result.Error ?? result.Output}");
        }
        return _fs.RenameFile(oldPath, newPath);
    }

    public VCResult RenameFolder(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath)) return VCResult.Ok();
        var result = Git(["mv", oldPath, newPath], Path.GetDirectoryName(oldPath)!);
        if (result.ExitCode == 0) return VCResult.Ok();
        // Fall back to filesystem rename for untracked folders.
        return _fs.RenameFolder(oldPath, newPath);
    }

    // -------------------------------------------------------------------------

    private static bool IsTracked(string filePath)
    {
        var cwd = Path.GetDirectoryName(filePath) ?? ".";
        var result = Git(["ls-files", "--error-unmatch", filePath], cwd);
        return result.ExitCode == 0;
    }

    private static CommandRunner.Result Git(string[] args, string cwd) =>
        CommandRunner.Run("git", args, workingDirectory: cwd);
}

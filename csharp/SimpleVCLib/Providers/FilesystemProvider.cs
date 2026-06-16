namespace SimpleVCLib;

/// <summary>
/// Plain filesystem provider: no VC system.
/// Used as the fallback when no VC is detected, and as a base for read-only checks
/// in other providers.
/// </summary>
public class FilesystemProvider : IVCProvider
{
    public string Name => "filesystem";

    public VCResult PrepareToWrite(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        var info = new FileInfo(filePath);
        if (!info.IsReadOnly) return VCResult.Ok();

        try
        {
            info.IsReadOnly = false;
            return VCResult.Ok("File made writable");
        }
        catch (Exception ex)
        {
            return VCResult.Error($"Cannot make '{filePath}' writable: {ex.Message}");
        }
    }

    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");
        return VCResult.Ok();
    }

    // Filesystem operations are local and synchronous; the async twins exist only so
    // callers can treat every provider uniformly. They do no real awaiting.
    public Task<VCResult> PrepareToWriteAsync(string filePath) => Task.FromResult(PrepareToWrite(filePath));
    public Task<VCResult> FinishedWriteAsync(string filePath) => Task.FromResult(FinishedWrite(filePath));
    public Task<VCResult> DeleteFileAsync(string filePath) => Task.FromResult(DeleteFile(filePath));
    public Task<VCResult> DeleteFolderAsync(string folderPath) => Task.FromResult(DeleteFolder(folderPath));
    public Task<VCResult> RenameFileAsync(string oldPath, string newPath) => Task.FromResult(RenameFile(oldPath, newPath));
    public Task<VCResult> RenameFolderAsync(string oldPath, string newPath) => Task.FromResult(RenameFolder(oldPath, newPath));

    public VCResult DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();
        try
        {
            File.Delete(filePath);
            return VCResult.Ok();
        }
        catch (Exception ex)
        {
            return VCResult.Error($"Cannot delete '{filePath}': {ex.Message}");
        }
    }

    public VCResult DeleteFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return VCResult.Ok();
        try
        {
            Directory.Delete(folderPath, recursive: true);
            return VCResult.Ok();
        }
        catch (Exception ex)
        {
            return VCResult.Error($"Cannot delete folder '{folderPath}': {ex.Message}");
        }
    }

    public VCResult RenameFile(string oldPath, string newPath)
    {
        if (!File.Exists(oldPath)) return VCResult.Ok();
        try
        {
            File.Move(oldPath, newPath);
            return VCResult.Ok();
        }
        catch (Exception ex)
        {
            return VCResult.Error($"Cannot rename '{oldPath}' to '{newPath}': {ex.Message}");
        }
    }

    public VCResult RenameFolder(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath)) return VCResult.Ok();
        try
        {
            Directory.Move(oldPath, newPath);
            return VCResult.Ok();
        }
        catch (Exception ex)
        {
            return VCResult.Error($"Cannot rename folder '{oldPath}' to '{newPath}': {ex.Message}");
        }
    }
    /// <summary>
    /// Status for a batch of files. No VCS: just the writable bit.
    /// </summary>
    public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false) =>
        filePaths.Select(p => new VCFileStatus(
            Path.GetFullPath(p), "filesystem", FileStatusHelpers.WritableBit(p))).ToList();

    /// <summary>Async twin of <see cref="Status"/>. No commands to spawn - completes immediately.</summary>
    public Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false) =>
        Task.FromResult(Status(filePaths, remote));
}

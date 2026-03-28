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
}

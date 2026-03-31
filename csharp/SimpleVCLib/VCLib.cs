using System.IO;
using System.Linq;
using System.Text;

namespace SimpleVCLib;

/// <summary>
/// Main entry point for the simple-vc-lib API.
/// All methods auto-detect the version control system from the file path,
/// unless an explicit provider has been set via <see cref="SetProvider"/>.
/// </summary>
public static class VCLib
{
    private static IVCProvider? _overrideProvider;

    /// <summary>
    /// Override the provider used for all operations.
    /// Useful for testing or in environments where auto-detection is unreliable.
    /// </summary>
    public static void SetProvider(IVCProvider provider) =>
        _overrideProvider = provider;

    /// <summary>
    /// Clear any provider override, restoring auto-detection.
    /// </summary>
    public static void ClearProvider()
    {
        _overrideProvider = null;
        Detector.ClearCache();
    }

    /// <summary>
    /// Return the provider that would be used for <paramref name="path"/>.
    /// </summary>
    public static IVCProvider GetProvider(string path) =>
        _overrideProvider ?? Detector.Detect(path);

    /// <inheritdoc cref="IVCProvider.PrepareToWrite"/>
    public static VCResult PrepareToWrite(string filePath) =>
        GetProvider(filePath).PrepareToWrite(filePath);

    /// <inheritdoc cref="IVCProvider.FinishedWrite"/>
    public static VCResult FinishedWrite(string filePath) =>
        GetProvider(filePath).FinishedWrite(filePath);

    /// <inheritdoc cref="IVCProvider.DeleteFile"/>
    public static VCResult DeleteFile(string filePath) =>
        GetProvider(filePath).DeleteFile(filePath);

    /// <inheritdoc cref="IVCProvider.DeleteFolder"/>
    public static VCResult DeleteFolder(string folderPath) =>
        GetProvider(folderPath).DeleteFolder(folderPath);

    /// <inheritdoc cref="IVCProvider.RenameFile"/>
    public static VCResult RenameFile(string oldPath, string newPath) =>
        GetProvider(oldPath).RenameFile(oldPath, newPath);

    /// <inheritdoc cref="IVCProvider.RenameFolder"/>
    public static VCResult RenameFolder(string oldPath, string newPath) =>
        GetProvider(oldPath).RenameFolder(oldPath, newPath);

    /// <summary>
    /// Write text to a file, handling VC checkout and registration automatically.
    /// Calls <see cref="PrepareToWrite"/>, writes the file, then calls <see cref="FinishedWrite"/>.
    /// Works whether or not the file already exists.
    /// <para>
    /// If the file already exists and its content matches <paramref name="content"/>, no VCS
    /// operations are performed and the file is not written. Set <paramref name="forceWrite"/>
    /// to <c>true</c> to skip this check and always write.
    /// </para>
    /// On failure, returns the result from whichever step failed.
    /// </summary>
    /// <param name="filePath">Path to write.</param>
    /// <param name="content">Text content to write.</param>
    /// <param name="encoding">Text encoding to use; defaults to UTF-8 without BOM.</param>
    /// <param name="forceWrite">When <c>true</c>, always write even if content is unchanged.</param>
    public static VCResult WriteTextFile(string filePath, string content, Encoding? encoding = null, bool forceWrite = false)
    {
        var enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!forceWrite && File.Exists(filePath))
        {
            try
            {
                var existing = File.ReadAllText(filePath, enc);
                if (existing == content) return VCResult.Ok();
            }
            catch
            {
                // If the file can't be read, fall through to the normal write path.
            }
        }
        var prep = GetProvider(filePath).PrepareToWrite(filePath);
        if (!prep.Success) return prep;
        try
        {
            File.WriteAllText(filePath, content, enc);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
        return GetProvider(filePath).FinishedWrite(filePath);
    }

    /// <summary>
    /// Write binary data to a file, handling VC checkout and registration automatically.
    /// Calls <see cref="PrepareToWrite"/>, writes the file, then calls <see cref="FinishedWrite"/>.
    /// Works whether or not the file already exists.
    /// <para>
    /// If the file already exists and its content matches <paramref name="data"/>, no VCS
    /// operations are performed and the file is not written. Set <paramref name="forceWrite"/>
    /// to <c>true</c> to skip this check and always write.
    /// </para>
    /// On failure, returns the result from whichever step failed.
    /// </summary>
    /// <param name="filePath">Path to write.</param>
    /// <param name="data">Binary data to write.</param>
    /// <param name="forceWrite">When <c>true</c>, always write even if content is unchanged.</param>
    public static VCResult WriteBinaryFile(string filePath, byte[] data, bool forceWrite = false)
    {
        if (!forceWrite && File.Exists(filePath))
        {
            try
            {
                var existing = File.ReadAllBytes(filePath);
                if (existing.SequenceEqual(data)) return VCResult.Ok();
            }
            catch
            {
                // If the file can't be read, fall through to the normal write path.
            }
        }
        var prep = GetProvider(filePath).PrepareToWrite(filePath);
        if (!prep.Success) return prep;
        try
        {
            File.WriteAllBytes(filePath, data);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
        return GetProvider(filePath).FinishedWrite(filePath);
    }
}

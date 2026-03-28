namespace SimpleVCLib;

/// <summary>
/// Common interface for all version control providers.
/// </summary>
public interface IVCProvider
{
    string Name { get; }

    /// <summary>
    /// Prepare a file for writing.
    /// <para>
    /// If the file does not exist, returns Ok immediately (ready to create).
    /// If the file exists and is writable, returns Ok immediately.
    /// If the file is read-only, attempts to check it out / unlock it.
    /// </para>
    /// </summary>
    VCResult PrepareToWrite(string filePath);

    /// <summary>
    /// Notify that a file has been written.
    /// <para>
    /// If the file is new (not yet tracked), adds it to the repository.
    /// If the file was already tracked, this is a no-op.
    /// </para>
    /// </summary>
    VCResult FinishedWrite(string filePath);

    /// <summary>
    /// Delete a file, scheduling it for deletion in VC if tracked.
    /// </summary>
    VCResult DeleteFile(string filePath);

    /// <summary>
    /// Delete a folder and all its contents, scheduling tracked files for VC deletion.
    /// </summary>
    VCResult DeleteFolder(string folderPath);
}

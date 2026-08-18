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

    /// <summary>Async twin of <see cref="PrepareToWrite"/>.</summary>
    Task<VCResult> PrepareToWriteAsync(string filePath);

    /// <summary>Async twin of <see cref="FinishedWrite"/>.</summary>
    Task<VCResult> FinishedWriteAsync(string filePath);

    /// <summary>
    /// Delete a file, scheduling it for deletion in VC if tracked.
    /// </summary>
    VCResult DeleteFile(string filePath);

    /// <summary>
    /// Delete a folder and all its contents, scheduling tracked files for VC deletion.
    /// </summary>
    VCResult DeleteFolder(string folderPath);

    /// <summary>
    /// Rename a file, informing VC of the change if the file is tracked.
    /// No-op if the source does not exist.
    /// </summary>
    VCResult RenameFile(string oldPath, string newPath);

    /// <summary>
    /// Rename a folder, informing VC of the change for all tracked contents.
    /// No-op if the source does not exist.
    /// </summary>
    VCResult RenameFolder(string oldPath, string newPath);

    /// <summary>Async twin of <see cref="DeleteFile"/>.</summary>
    Task<VCResult> DeleteFileAsync(string filePath);

    /// <summary>Async twin of <see cref="DeleteFolder"/>.</summary>
    Task<VCResult> DeleteFolderAsync(string folderPath);

    /// <summary>Async twin of <see cref="RenameFile"/>.</summary>
    Task<VCResult> RenameFileAsync(string oldPath, string newPath);

    /// <summary>Async twin of <see cref="RenameFolder"/>.</summary>
    Task<VCResult> RenameFolderAsync(string oldPath, string newPath);

    /// <summary>
    /// Status for a batch of files: tracked / writable / dirty / locked-by /
    /// opened-by-me / out-of-date, per file, in input order. Batched: one spawn per
    /// provider / repository, not one per file.
    /// <para>
    /// When <paramref name="remote"/> is true, providers that need a server round-trip
    /// for lock owners / out-of-date (SVN, Plastic) may make one; by default the read
    /// stays local where possible. Perforce and git-LFS carry that data for free and
    /// report it either way.
    /// </para>
    /// </summary>
    IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false);

    /// <summary>Async twin of <see cref="Status"/> - spawned without blocking a thread.</summary>
    Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false);

    /// <summary>
    /// Who this VCS believes the current user is, as it would name them in
    /// <see cref="VCFileStatus.LockedBy"/> ("bob@bob-ws", "alovelace").
    /// <para>
    /// Null when the provider cannot say: the filesystem provider always, git with no configured
    /// <c>user.name</c>, and SVN ever - it tells our lock from someone else's by comparing lock
    /// TOKENS, so it never learns a name to report.
    /// </para>
    /// <para>
    /// Meant for SEEDING a name the person can then change, not for using as an identity: a
    /// workspace account is not always a name somebody wants on their words.
    /// </para>
    /// </summary>
    /// <para>
    /// DEFAULTED so that adding it did not break every existing implementor - a third-party
    /// provider, or a test fake - which is what an interface member without a body would have
    /// done to a published library. The JS port says the same thing with optional chaining.
    /// </para>
    string? CurrentUser(string? pathHint = null) => null;

    /// <summary>Async twin of <see cref="CurrentUser"/>. Defaulted for the same reason.</summary>
    Task<string?> CurrentUserAsync(string? pathHint = null) => Task.FromResult<string?>(null);
}

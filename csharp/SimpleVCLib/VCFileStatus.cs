namespace SimpleVCLib;

/// <summary>
/// Per-file status, as returned by <see cref="VCLib.FileStatus"/> /
/// a provider's <see cref="IVCProvider.Status"/>.
/// </summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="System">The VC system name ("git", "perforce", "plastic", "svn", "filesystem").</param>
/// <param name="Writable">Writable on disk right now (the read-only bit - lock workflows key off this).</param>
/// <param name="Tracked">Known to the VCS (tracked / in the depot). Null when the provider cannot say.</param>
/// <param name="OpenedByMe">Opened / checked out / locked by the current user.</param>
/// <param name="LockedBy">Who else has it open or locked (e.g. "bob@bob-ws").</param>
/// <param name="OutOfDate">A newer revision exists on the server.</param>
/// <param name="Dirty">
/// Has pending local VC changes: a tracked file that is modified / staged / opened /
/// added / deleted but not yet committed. Untracked files are not dirty (they surface
/// via <see cref="Tracked"/> = false). The cheap, local notion - it does not detect a
/// file edited outside VC. Null when the provider cannot say.
/// </param>
public sealed record VCFileStatus(
    string FilePath,
    string System,
    bool Writable,
    bool? Tracked = null,
    bool? OpenedByMe = null,
    IReadOnlyList<string>? LockedBy = null,
    bool? OutOfDate = null,
    bool? Dirty = null);

internal static class FileStatusHelpers
{
    /// <summary>
    /// The read-only bit: cheap, local, and the primary editability signal under
    /// lock-based workflows. A file not on disk yet counts as writable.
    /// </summary>
    internal static bool WritableBit(string filePath)
    {
        try
        {
            return !File.Exists(filePath) || !new FileInfo(filePath).IsReadOnly;
        }
        catch
        {
            return true;
        }
    }
}

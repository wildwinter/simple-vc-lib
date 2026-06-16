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
    /// Override the command runner used for all VC operations - lets tests inject
    /// canned CLI output (e.g. <c>p4 -ztag fstat</c> transcripts) so provider logic
    /// is unit-testable without the VCS installed. The same pattern as
    /// <see cref="SetProvider"/>. Pass null to restore real execution.
    /// </summary>
    public static void SetCommandRunner(Func<string, string[], CommandResult>? runner) =>
        CommandRunner.SetOverride(runner);

    /// <summary>Clear any command-runner override, restoring real execution.</summary>
    public static void ClearCommandRunner() =>
        CommandRunner.SetOverride(null);

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

    /// <inheritdoc cref="IVCProvider.DeleteFileAsync"/>
    public static Task<VCResult> DeleteFileAsync(string filePath) =>
        GetProvider(filePath).DeleteFileAsync(filePath);

    /// <inheritdoc cref="IVCProvider.DeleteFolderAsync"/>
    public static Task<VCResult> DeleteFolderAsync(string folderPath) =>
        GetProvider(folderPath).DeleteFolderAsync(folderPath);

    /// <inheritdoc cref="IVCProvider.RenameFileAsync"/>
    public static Task<VCResult> RenameFileAsync(string oldPath, string newPath) =>
        GetProvider(oldPath).RenameFileAsync(oldPath, newPath);

    /// <inheritdoc cref="IVCProvider.RenameFolderAsync"/>
    public static Task<VCResult> RenameFolderAsync(string oldPath, string newPath) =>
        GetProvider(oldPath).RenameFolderAsync(oldPath, newPath);

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
    /// <summary>
    /// Write a batch of text files through VC, creating parent directories, and
    /// report EVERY outcome - a refused write comes back with its why ("locked by
    /// bob@bob-ws"), never a bare access exception, and one refusal does not stop
    /// the rest. Each write goes through <see cref="WriteTextFile"/> (prepare ->
    /// write -> finished, with the unchanged-content short-circuit).
    /// </summary>
    public static VCWriteBatchResult WriteTextFiles(IReadOnlyList<VCFileWrite> files, Encoding? encoding = null)
    {
        var results = new List<VCWriteOutcome>();
        foreach (var file in files)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(file.FilePath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                results.Add(new VCWriteOutcome(file.FilePath, false, VCStatus.Error, e.Message));
                continue;
            }
            var result = WriteTextFile(file.FilePath, file.Content, encoding);
            results.Add(new VCWriteOutcome(file.FilePath, result.Success, result.Status, result.Message));
        }
        return new VCWriteBatchResult(results.All(r => r.Success), results);
    }

    /// <inheritdoc cref="IVCProvider.PrepareToWriteAsync"/>
    public static Task<VCResult> PrepareToWriteAsync(string filePath) =>
        GetProvider(filePath).PrepareToWriteAsync(filePath);

    /// <inheritdoc cref="IVCProvider.FinishedWriteAsync"/>
    public static Task<VCResult> FinishedWriteAsync(string filePath) =>
        GetProvider(filePath).FinishedWriteAsync(filePath);

    /// <summary>Async twin of <see cref="WriteTextFile"/>.</summary>
    public static async Task<VCResult> WriteTextFileAsync(
        string filePath, string content, Encoding? encoding = null, bool forceWrite = false)
    {
        var enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!forceWrite && File.Exists(filePath))
        {
            try
            {
                var existing = await File.ReadAllTextAsync(filePath, enc).ConfigureAwait(false);
                if (existing == content) return VCResult.Ok();
            }
            catch { /* unreadable - fall through to the normal write path */ }
        }
        var prep = await GetProvider(filePath).PrepareToWriteAsync(filePath).ConfigureAwait(false);
        if (!prep.Success) return prep;
        try
        {
            await File.WriteAllTextAsync(filePath, content, enc).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
        return await GetProvider(filePath).FinishedWriteAsync(filePath).ConfigureAwait(false);
    }

    /// <summary>Async twin of <see cref="WriteBinaryFile"/>.</summary>
    public static async Task<VCResult> WriteBinaryFileAsync(string filePath, byte[] data, bool forceWrite = false)
    {
        if (!forceWrite && File.Exists(filePath))
        {
            try
            {
                var existing = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
                if (existing.SequenceEqual(data)) return VCResult.Ok();
            }
            catch { /* unreadable - fall through to the normal write path */ }
        }
        var prep = await GetProvider(filePath).PrepareToWriteAsync(filePath).ConfigureAwait(false);
        if (!prep.Success) return prep;
        try
        {
            await File.WriteAllBytesAsync(filePath, data).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
        return await GetProvider(filePath).FinishedWriteAsync(filePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Async twin of <see cref="WriteTextFiles"/>. Files are processed sequentially - VC
    /// checkout commands on one workspace are not safe to run concurrently - so behaviour
    /// matches the sync version exactly.
    /// </summary>
    public static async Task<VCWriteBatchResult> WriteTextFilesAsync(
        IReadOnlyList<VCFileWrite> files, Encoding? encoding = null)
    {
        var results = new List<VCWriteOutcome>();
        foreach (var file in files)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(file.FilePath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                results.Add(new VCWriteOutcome(file.FilePath, false, VCStatus.Error, e.Message));
                continue;
            }
            var result = await WriteTextFileAsync(file.FilePath, file.Content, encoding).ConfigureAwait(false);
            results.Add(new VCWriteOutcome(file.FilePath, result.Success, result.Status, result.Message));
        }
        return new VCWriteBatchResult(results.All(r => r.Success), results);
    }

    /// <summary>
    /// Status for a batch of files: tracked / writable / dirty / locked-by /
    /// opened-by-me / out-of-date, per file, in input order. Paths are grouped by
    /// provider so a whole project costs a spawn or two, not one per file (Perforce:
    /// ONE <c>p4 -ztag fstat</c>; git: one <c>git status</c> + one
    /// <c>git lfs locks</c> per repository). The writable bit is always reported -
    /// in lock-based workflows it is the cheap local signal for "is this editable
    /// right now?".
    /// <para>
    /// By default the read stays as local as each provider allows. Pass
    /// <paramref name="remote"/> = true to also fetch server-side <c>lockedBy</c> /
    /// <c>outOfDate</c> for SVN (<c>svn status -u</c>) and Plastic (<c>cm fileinfo</c>) -
    /// an extra round-trip. Perforce and git-LFS report that data either way.
    /// </para>
    /// </summary>
    public static IReadOnlyList<VCFileStatus> FileStatus(IReadOnlyList<string> filePaths, bool remote = false)
    {
        var groups = new Dictionary<string, (IVCProvider Provider, List<string> Paths)>();
        foreach (var filePath in filePaths)
        {
            var provider = GetProvider(filePath);
            if (!groups.TryGetValue(provider.Name, out var group))
                groups[provider.Name] = group = (provider, new List<string>());
            group.Paths.Add(filePath);
        }

        var byInput = new Dictionary<string, VCFileStatus>();
        foreach (var (provider, paths) in groups.Values)
        {
            var statuses = provider.Status(paths, remote);
            for (var i = 0; i < paths.Count; i++) byInput[paths[i]] = statuses[i];
        }
        return filePaths.Select(p => byInput[p]).ToList();
    }

    /// <summary>
    /// Async twin of <see cref="FileStatus"/>. Spawns without blocking a thread, and -
    /// because providers are independent - runs the per-provider reads concurrently, so a
    /// project spanning several repos/working copies finishes in about the time of its
    /// slowest provider rather than their sum.
    /// </summary>
    public static async Task<IReadOnlyList<VCFileStatus>> FileStatusAsync(
        IReadOnlyList<string> filePaths, bool remote = false)
    {
        var groups = new Dictionary<string, (IVCProvider Provider, List<string> Paths)>();
        foreach (var filePath in filePaths)
        {
            var provider = GetProvider(filePath);
            if (!groups.TryGetValue(provider.Name, out var group))
                groups[provider.Name] = group = (provider, new List<string>());
            group.Paths.Add(filePath);
        }

        var byInput = new System.Collections.Concurrent.ConcurrentDictionary<string, VCFileStatus>();
        await Task.WhenAll(groups.Values.Select(async g =>
        {
            var statuses = await g.Provider.StatusAsync(g.Paths, remote).ConfigureAwait(false);
            for (var i = 0; i < g.Paths.Count; i++) byInput[g.Paths[i]] = statuses[i];
        })).ConfigureAwait(false);
        return filePaths.Select(p => byInput[p]).ToList();
    }
}

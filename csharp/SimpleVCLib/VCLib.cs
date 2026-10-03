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
        GitProvider.ClearTrackedCache();
    }

    /// <summary>
    /// Forget everything cached about the working copy: which VCS is where, and
    /// which paths git has in its index.
    /// <para>
    /// Both are answered once and remembered, because a tool that autosaves asks
    /// them on every write and each answer costs a subprocess. Call this when the
    /// ground moves - a different project opened, a working copy re-cloned -
    /// rather than relying on process lifetime.
    /// </para>
    /// </summary>
    public static void ClearVcCaches()
    {
        Detector.ClearCache();
        GitProvider.ClearTrackedCache();
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
        // One resolution for both halves of the write: it is the same question,
        // and asking twice was two directory walks (and once, two `p4 info` runs).
        var provider = GetProvider(filePath);
        var prep = provider.PrepareToWrite(filePath);
        if (!prep.Success) return prep;
        try
        {
            File.WriteAllText(filePath, content, enc);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
        return provider.FinishedWrite(filePath);
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
        // One resolution for both halves of the write: it is the same question,
        // and asking twice was two directory walks (and once, two `p4 info` runs).
        var provider = GetProvider(filePath);
        var prep = provider.PrepareToWrite(filePath);
        if (!prep.Success) return prep;
        try
        {
            File.WriteAllBytes(filePath, data);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
        return provider.FinishedWrite(filePath);
    }
    /// <summary>
    /// Write a batch of text files through VC, creating parent directories, and
    /// report EVERY outcome - a refused write comes back with its why ("locked by
    /// bob@bob-ws"), never a bare access exception, and one refusal does not stop
    /// the rest. Each write goes through <see cref="WriteTextFile"/> (prepare ->
    /// write -> finished, with the unchanged-content short-circuit).
    /// <para>
    /// With <paramref name="allOrNothing"/>, the batch is checked out as a whole before
    /// anything is written: files whose content is already right are skipped (they need no
    /// checkout), parent directories are created, then <see cref="PrepareToWriteFiles"/>
    /// prepares the rest. If any file cannot be prepared, nothing is written and nothing is
    /// left checked out. Only once every file is prepared are they written and
    /// <see cref="FinishedWrite"/> called on each. VC cannot make the disk writes themselves
    /// atomic, so a write or FinishedWrite that fails at that last stage is reported against
    /// its own file and the others still go ahead.
    /// </para>
    /// </summary>
    public static VCWriteBatchResult WriteTextFiles(
        IReadOnlyList<VCFileWrite> files, Encoding? encoding = null, bool allOrNothing = false)
    {
        if (allOrNothing) return WriteTextFilesAllOrNothing(files, encoding);
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
        // One resolution for both halves; see the sync twin.
        var provider = GetProvider(filePath);
        var prep = await provider.PrepareToWriteAsync(filePath).ConfigureAwait(false);
        if (!prep.Success) return prep;
        try
        {
            await File.WriteAllTextAsync(filePath, content, enc).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
        return await provider.FinishedWriteAsync(filePath).ConfigureAwait(false);
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
        // One resolution for both halves; see the sync twin.
        var provider = GetProvider(filePath);
        var prep = await provider.PrepareToWriteAsync(filePath).ConfigureAwait(false);
        if (!prep.Success) return prep;
        try
        {
            await File.WriteAllBytesAsync(filePath, data).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
        return await provider.FinishedWriteAsync(filePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Async twin of <see cref="WriteTextFiles"/>. Files are processed sequentially - VC
    /// checkout commands on one workspace are not safe to run concurrently - so behaviour
    /// matches the sync version exactly.
    /// </summary>
    public static async Task<VCWriteBatchResult> WriteTextFilesAsync(
        IReadOnlyList<VCFileWrite> files, Encoding? encoding = null, bool allOrNothing = false)
    {
        if (allOrNothing) return await WriteTextFilesAllOrNothingAsync(files, encoding).ConfigureAwait(false);
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

    // -- All-or-nothing batch checkout ----------------------------------------------

    /// <summary>
    /// The batch's paths once each, keyed by full path so <c>a.txt</c> and <c>/wc/a.txt</c>
    /// are one file. The first spelling seen is the one handed to the provider.
    /// </summary>
    private static List<string> UniqueByPath(IReadOnlyList<string> filePaths)
    {
        var seen = new HashSet<string>();
        var unique = new List<string>();
        foreach (var filePath in filePaths)
            if (seen.Add(Path.GetFullPath(filePath))) unique.Add(filePath);
        return unique;
    }

    /// <summary>One outcome per INPUT path, in input order, looked up by full path.</summary>
    private static VCWriteBatchResult ReportBatch(IReadOnlyList<string> filePaths, Dictionary<string, VCResult> byKey)
    {
        var results = filePaths.Select(p =>
        {
            var r = byKey[Path.GetFullPath(p)];
            return new VCWriteOutcome(p, r.Success, r.Status, r.Message);
        }).ToList();
        return new VCWriteBatchResult(results.All(r => r.Success), results);
    }

    /// <summary>
    /// The preflight verdict: a refusal for every existing file someone else holds or that
    /// is behind the server, and a "not prepared" for everything else. Null when nothing is
    /// refused and the batch can go ahead.
    /// </summary>
    private static Dictionary<string, VCResult>? PreflightRefusals(List<string> unique, IReadOnlyList<VCFileStatus> statuses)
    {
        var byKey = new Dictionary<string, VCResult>();
        for (var i = 0; i < unique.Count; i++)
        {
            var filePath = unique[i];
            var status = statuses[i];
            if (!File.Exists(filePath)) continue;
            if (status.LockedBy is { Count: > 0 } holders)
                byKey[Path.GetFullPath(filePath)] = VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked by {string.Join(", ", holders)}");
            else if (status.OutOfDate == true)
                byKey[Path.GetFullPath(filePath)] = VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date; get the latest revision before editing");
        }
        if (byKey.Count == 0) return null;
        foreach (var filePath in unique)
            byKey.TryAdd(Path.GetFullPath(filePath),
                VCResult.Error($"'{filePath}' was not prepared because another file in the batch was refused"));
        return byKey;
    }

    /// <summary>What an undone path reports: the undo, and whether the undo itself worked.</summary>
    private static VCResult UndoneOutcome(string filePath, VCResult undo) =>
        undo.Success
            ? VCResult.Error($"Checkout of '{filePath}' was undone because another file in the batch failed")
            : VCResult.Error($"Checkout of '{filePath}' could not be undone after another file in the batch failed: {undo.Message}");

    /// <summary>Run a provider's undo, turning a throw into a result.</summary>
    private static VCResult UndoOne(IVCProvider provider, string filePath, VCFileStatus before)
    {
        try
        {
            return provider.UndoPrepareToWrite(filePath, before);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
    }

    /// <summary>Async twin of <see cref="UndoOne"/>.</summary>
    private static async Task<VCResult> UndoOneAsync(IVCProvider provider, string filePath, VCFileStatus before)
    {
        try
        {
            return await provider.UndoPrepareToWriteAsync(filePath, before).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return VCResult.Error(e.Message);
        }
    }

    /// <summary>
    /// Prepare a whole batch of files for writing, or none of them.
    /// <list type="number">
    /// <item>One batched status read over every path (<see cref="FileStatus"/> with
    /// <c>remote: true</c>, so SVN and Plastic ask the server who holds what). Any file on disk
    /// that is <see cref="VCFileStatus.LockedBy"/> someone is refused as
    /// <see cref="VCStatus.Locked"/>, naming the holders; any that is
    /// <see cref="VCFileStatus.OutOfDate"/> is refused as <see cref="VCStatus.OutOfDate"/>. One
    /// refusal means nothing is checked out.</item>
    /// <item>Otherwise each file is prepared in turn with the provider's
    /// <see cref="IVCProvider.PrepareToWrite"/>, sequentially, since VC commands on one
    /// workspace are not safe to run concurrently.</item>
    /// <item>If one fails, every file this call already prepared is undone, newest first, with
    /// the provider's <see cref="IVCProvider.UndoPrepareToWrite"/>. A failed undo is reported in
    /// that file's message, never thrown.</item>
    /// </list>
    /// <para>
    /// Under Perforce, LockedBy lists everyone else who has the file OPEN, not only those
    /// holding an exclusive lock, so another user's plain <c>p4 edit</c> refuses the batch. That
    /// is deliberate: it is what hosts already show as "locked by".
    /// </para>
    /// <para>
    /// An undo only reverses what this call did. A file the user already had open, checked out
    /// or locked (<see cref="VCFileStatus.OpenedByMe"/>) is never reverted, so after a failure it
    /// is left exactly as it was found.
    /// </para>
    /// <para>
    /// Each input path gets one outcome, in input order (duplicates are prepared once and
    /// reported for each spelling).
    /// </para>
    /// </summary>
    public static VCWriteBatchResult PrepareToWriteFiles(IReadOnlyList<string> filePaths)
    {
        var unique = UniqueByPath(filePaths);
        var statuses = unique.Count > 0 ? FileStatus(unique, remote: true) : [];
        var refused = PreflightRefusals(unique, statuses);
        if (refused is not null) return ReportBatch(filePaths, refused);

        var byKey = new Dictionary<string, VCResult>();
        var prepared = new List<(string FilePath, IVCProvider Provider, VCFileStatus Before)>();
        for (var i = 0; i < unique.Count; i++)
        {
            var filePath = unique[i];
            var provider = GetProvider(filePath);
            var result = provider.PrepareToWrite(filePath);
            byKey[Path.GetFullPath(filePath)] = result;
            if (result.Success)
            {
                prepared.Add((filePath, provider, statuses[i]));
                continue;
            }
            for (var j = prepared.Count - 1; j >= 0; j--)
            {
                var done = prepared[j];
                byKey[Path.GetFullPath(done.FilePath)] = UndoneOutcome(done.FilePath, UndoOne(done.Provider, done.FilePath, done.Before));
            }
            foreach (var unreached in unique.Skip(i + 1))
                byKey[Path.GetFullPath(unreached)] =
                    VCResult.Error($"'{unreached}' was not prepared because another file in the batch failed");
            break;
        }
        return ReportBatch(filePaths, byKey);
    }

    /// <summary>
    /// Async twin of <see cref="PrepareToWriteFiles"/>. The status read runs concurrently
    /// across providers, as <see cref="FileStatusAsync"/> does; the checkouts and undos stay
    /// sequential.
    /// </summary>
    public static async Task<VCWriteBatchResult> PrepareToWriteFilesAsync(IReadOnlyList<string> filePaths)
    {
        var unique = UniqueByPath(filePaths);
        var statuses = unique.Count > 0 ? await FileStatusAsync(unique, remote: true).ConfigureAwait(false) : [];
        var refused = PreflightRefusals(unique, statuses);
        if (refused is not null) return ReportBatch(filePaths, refused);

        var byKey = new Dictionary<string, VCResult>();
        var prepared = new List<(string FilePath, IVCProvider Provider, VCFileStatus Before)>();
        for (var i = 0; i < unique.Count; i++)
        {
            var filePath = unique[i];
            var provider = GetProvider(filePath);
            var result = await provider.PrepareToWriteAsync(filePath).ConfigureAwait(false);
            byKey[Path.GetFullPath(filePath)] = result;
            if (result.Success)
            {
                prepared.Add((filePath, provider, statuses[i]));
                continue;
            }
            for (var j = prepared.Count - 1; j >= 0; j--)
            {
                var done = prepared[j];
                var undo = await UndoOneAsync(done.Provider, done.FilePath, done.Before).ConfigureAwait(false);
                byKey[Path.GetFullPath(done.FilePath)] = UndoneOutcome(done.FilePath, undo);
            }
            foreach (var unreached in unique.Skip(i + 1))
                byKey[Path.GetFullPath(unreached)] =
                    VCResult.Error($"'{unreached}' was not prepared because another file in the batch failed");
            break;
        }
        return ReportBatch(filePaths, byKey);
    }

    /// <summary>True when the file is already on disk with exactly this content (the WriteTextFile short-circuit).</summary>
    private static bool ContentUnchanged(string filePath, string content, Encoding enc)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            return File.ReadAllText(filePath, enc) == content;
        }
        catch
        {
            return false; // Unreadable: write it the normal way.
        }
    }

    /// <summary>Async twin of <see cref="ContentUnchanged"/>.</summary>
    private static async Task<bool> ContentUnchangedAsync(string filePath, string content, Encoding enc)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            return await File.ReadAllTextAsync(filePath, enc).ConfigureAwait(false) == content;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A batch that stopped before anything was written: failures keep their own outcome, the rest say why they were left.</summary>
    private static VCWriteBatchResult AbandonBatch(IReadOnlyList<VCFileWrite> files, VCResult?[] results, List<int> pending)
    {
        foreach (var i in pending)
            results[i] ??= VCResult.Error($"'{files[i].FilePath}' was not written because another file in the batch could not be prepared");
        return FinishBatch(files, results);
    }

    /// <summary>One outcome per input file, in input order.</summary>
    private static VCWriteBatchResult FinishBatch(IReadOnlyList<VCFileWrite> files, VCResult?[] results)
    {
        var outcomes = files.Select((f, i) => new VCWriteOutcome(f.FilePath, results[i]!.Success, results[i]!.Status, results[i]!.Message)).ToList();
        return new VCWriteBatchResult(outcomes.All(r => r.Success), outcomes);
    }

    /// <summary>Creates each pending file's folder; false (with the failure recorded) if any could not be.</summary>
    private static bool CreateFolders(IReadOnlyList<VCFileWrite> files, VCResult?[] results, List<int> pending)
    {
        var ok = true;
        foreach (var i in pending)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(files[i].FilePath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                results[i] = VCResult.Error(e.Message);
                ok = false;
            }
        }
        return ok;
    }

    /// <summary><see cref="WriteTextFiles"/> with allOrNothing: see its doc comment.</summary>
    private static VCWriteBatchResult WriteTextFilesAllOrNothing(IReadOnlyList<VCFileWrite> files, Encoding? encoding)
    {
        var enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var results = new VCResult?[files.Count];
        var pending = new List<int>();
        for (var i = 0; i < files.Count; i++)
        {
            if (ContentUnchanged(files[i].FilePath, files[i].Content, enc)) results[i] = VCResult.Ok();
            else pending.Add(i);
        }

        if (!CreateFolders(files, results, pending)) return AbandonBatch(files, results, pending);

        var prep = PrepareToWriteFiles(pending.Select(i => files[i].FilePath).ToList());
        if (!prep.Success)
        {
            for (var j = 0; j < pending.Count; j++)
            {
                var r = prep.Results[j];
                results[pending[j]] = new VCResult(r.Success, r.Status, r.Message);
            }
            return FinishBatch(files, results);
        }

        foreach (var i in pending)
        {
            var file = files[i];
            try
            {
                File.WriteAllText(file.FilePath, file.Content, enc);
            }
            catch (Exception e)
            {
                results[i] = VCResult.Error(e.Message);
                continue;
            }
            results[i] = GetProvider(file.FilePath).FinishedWrite(file.FilePath);
        }
        return FinishBatch(files, results);
    }

    /// <summary>Async twin of <see cref="WriteTextFilesAllOrNothing"/>.</summary>
    private static async Task<VCWriteBatchResult> WriteTextFilesAllOrNothingAsync(IReadOnlyList<VCFileWrite> files, Encoding? encoding)
    {
        var enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var results = new VCResult?[files.Count];
        var pending = new List<int>();
        for (var i = 0; i < files.Count; i++)
        {
            if (await ContentUnchangedAsync(files[i].FilePath, files[i].Content, enc).ConfigureAwait(false)) results[i] = VCResult.Ok();
            else pending.Add(i);
        }

        if (!CreateFolders(files, results, pending)) return AbandonBatch(files, results, pending);

        var prep = await PrepareToWriteFilesAsync(pending.Select(i => files[i].FilePath).ToList()).ConfigureAwait(false);
        if (!prep.Success)
        {
            for (var j = 0; j < pending.Count; j++)
            {
                var r = prep.Results[j];
                results[pending[j]] = new VCResult(r.Success, r.Status, r.Message);
            }
            return FinishBatch(files, results);
        }

        foreach (var i in pending)
        {
            var file = files[i];
            try
            {
                await File.WriteAllTextAsync(file.FilePath, file.Content, enc).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                results[i] = VCResult.Error(e.Message);
                continue;
            }
            results[i] = await GetProvider(file.FilePath).FinishedWriteAsync(file.FilePath).ConfigureAwait(false);
        }
        return FinishBatch(files, results);
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
    /// <summary>
    /// Who this VCS believes the current user is, as it would name them in
    /// <see cref="VCFileStatus.LockedBy"/> ("bob@bob-ws", "alovelace"). Null when the provider
    /// cannot say: the filesystem provider always, git with no configured <c>user.name</c>, and
    /// SVN ever - it identifies locks by token, never by name.
    /// <para>
    /// <paramref name="pathHint"/> picks WHICH working copy is asked, the same way every other
    /// call here resolves a provider from a path; it defaults to the process's own directory. A
    /// host that has pinned a provider with <see cref="SetProvider"/> gets that one regardless.
    /// </para>
    /// <para>
    /// Meant for SEEDING a name the person can then change, not for using as an identity.
    /// </para>
    /// </summary>
    public static string? CurrentUser(string? pathHint = null)
    {
        var where = pathHint ?? Directory.GetCurrentDirectory();
        return GetProvider(where).CurrentUser(where);
    }

    /// <summary>Async twin of <see cref="CurrentUser"/>.</summary>
    public static Task<string?> CurrentUserAsync(string? pathHint = null)
    {
        var where = pathHint ?? Directory.GetCurrentDirectory();
        return GetProvider(where).CurrentUserAsync(where);
    }

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

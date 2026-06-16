using System.Text.RegularExpressions;

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

        var fstat = Fstat(filePath);
        if (!IsInDepotFstat(fstat))
            return _fs.PrepareToWrite(filePath);

        // Re-created on disk while scheduled for delete: cancel the delete first.
        // `p4 edit` on a pending-delete file only warns (exit 0) and leaves the
        // delete pending, so the file would still be deleted at next submit.
        var action = PendingAction(fstat);
        if (action is "delete" or "move/delete")
            return ReopenAfterPendingDelete(filePath, fstat, action);

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

        var fstat = Fstat(filePath);

        // File is outside the workspace mapping entirely — no Perforce action needed.
        if (fstat is null)
            return _fs.FinishedWrite(filePath);

        // File has a pending delete in a changelist but was just re-written to disk.
        // Cancel the delete (keeping the new local content) then reopen for edit.
        var action = PendingAction(fstat);
        if (action is "delete" or "move/delete")
            return ReopenAfterPendingDelete(filePath, fstat, action);

        if (IsInDepotFstat(fstat)) return VCResult.Ok();

        var result = P4(["add", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File opened for add in Perforce");
        // File is ignored (e.g. matches a .p4ignore pattern) — treat as outside the depot.
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return _fs.FinishedWrite(filePath);
        return VCResult.Error($"Cannot add '{filePath}' to Perforce: {result.Error ?? result.Output}");
    }

    /// <summary>Async twin of <see cref="PrepareToWrite"/>.</summary>
    public async Task<VCResult> PrepareToWriteAsync(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        var fstat = await FstatAsync(filePath).ConfigureAwait(false);
        if (!IsInDepotFstat(fstat))
            return await _fs.PrepareToWriteAsync(filePath).ConfigureAwait(false);

        var action = PendingAction(fstat);
        if (action is "delete" or "move/delete")
            return await ReopenAfterPendingDeleteAsync(filePath, fstat, action).ConfigureAwait(false);

        var result = await P4Async(["edit", filePath]).ConfigureAwait(false);
        if (result.ExitCode == 0) return VCResult.Ok();

        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked by"))
            return VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked by another user");
        if (combined.Contains("out of date"))
            return VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date — sync before editing");

        return VCResult.Error($"Cannot open '{filePath}' for editing: {result.Error ?? result.Output}");
    }

    /// <summary>Async twin of <see cref="FinishedWrite"/>.</summary>
    public async Task<VCResult> FinishedWriteAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        var fstat = await FstatAsync(filePath).ConfigureAwait(false);
        if (fstat is null)
            return await _fs.FinishedWriteAsync(filePath).ConfigureAwait(false);

        var action = PendingAction(fstat);
        if (action is "delete" or "move/delete")
            return await ReopenAfterPendingDeleteAsync(filePath, fstat, action).ConfigureAwait(false);

        if (IsInDepotFstat(fstat)) return VCResult.Ok();

        var result = await P4Async(["add", filePath]).ConfigureAwait(false);
        if (result.ExitCode == 0) return VCResult.Ok("File opened for add in Perforce");
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return await _fs.FinishedWriteAsync(filePath).ConfigureAwait(false);
        return VCResult.Error($"Cannot add '{filePath}' to Perforce: {result.Error ?? result.Output}");
    }

    // Delete/rename carry intricate pending-changelist handling; rather than duplicate it,
    // the async twins run the tested sync method on a thread-pool thread (the p4 subprocess
    // wait parks a pooled thread).
    public Task<VCResult> DeleteFileAsync(string filePath) => Task.Run(() => DeleteFile(filePath));
    public Task<VCResult> DeleteFolderAsync(string folderPath) => Task.Run(() => DeleteFolder(folderPath));
    public Task<VCResult> RenameFileAsync(string oldPath, string newPath) => Task.Run(() => RenameFile(oldPath, newPath));
    public Task<VCResult> RenameFolderAsync(string oldPath, string newPath) => Task.Run(() => RenameFolder(oldPath, newPath));

    public VCResult DeleteFile(string filePath)
    {
        // No early-out on a missing local file: the file may still be opened in a
        // pending changelist (e.g. added then deleted between submits), and that
        // stale entry would abort the eventual submit and leave it locked.
        var fstat = Fstat(filePath);
        var action = PendingAction(fstat);

        // Open for add (never submitted): there is nothing in the depot to delete.
        // `p4 delete` would only warn (exit 0) and leave the add pending, so
        // revert it, then remove the local file.
        if (action == "add")
        {
            P4(["revert", "-k", filePath]);
            return RemoveLocal(filePath) ?? VCResult.Ok();
        }

        // Target half of a pending rename: cancel the move (reverting the move/add
        // half clears both ends, keeping disk files), schedule the original path
        // for delete (p4 delete works without a local copy), and remove this one.
        if (action == "move/add")
        {
            var source = MovedCounterpart(fstat);
            P4(["revert", "-k", filePath]);
            if (source is not null) P4(["delete", source]);
            return RemoveLocal(filePath) ?? VCResult.Ok();
        }

        // Already scheduled for delete: just make sure the local copy is gone.
        if (action is "delete" or "move/delete")
            return RemoveLocal(filePath) ?? VCResult.Ok();

        // Open for edit/integrate/etc: revert before scheduling the delete, since
        // `p4 delete` refuses files that are already open (warns, exit 0).
        if (action is not null)
            P4(["revert", "-k", filePath]);

        if (IsInDepotFstat(fstat))
        {
            // Remove the local copy first: `p4 delete` refuses to clobber a writable
            // local file (e.g. right after the edit revert above), but happily
            // schedules the delete when the local copy is already gone.
            var localError = RemoveLocal(filePath);
            if (localError is not null) return localError;
            var result = P4(["delete", filePath]);
            if (result.ExitCode != 0)
                return VCResult.Error($"Cannot delete '{filePath}' from Perforce: {result.Error ?? result.Output}");
            return VCResult.Ok();
        }

        if (!File.Exists(filePath)) return VCResult.Ok();
        return _fs.DeleteFile(filePath);
    }

    public VCResult DeleteFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return VCResult.Ok();

        // The /... wildcard schedules the entire depot subtree for deletion.
        var isWin = Path.DirectorySeparatorChar == '\\';
        var depotPath = (isWin ? folderPath.Replace('\\', '/') : folderPath) + "/...";
        P4(["delete", depotPath]); // Non-tracked paths return non-zero; that is expected.

        if (Directory.Exists(folderPath))
            return _fs.DeleteFolder(folderPath);

        return VCResult.Ok();
    }

    public VCResult RenameFile(string oldPath, string newPath)
    {
        if (!File.Exists(oldPath)) return VCResult.Ok();

        // A pending state on the destination blocks the rename: p4 move refuses
        // to target an opened or existing file ('can't move to an existing file',
        // and only warns at exit 0). Clear it first.
        var dstFstat = Fstat(newPath);
        var dstAction = PendingAction(dstFstat);
        if (dstAction is "add" or "move/add")
        {
            // The destination was created in this changelist: deleting it unwinds
            // the pending add (and any rename pair) and removes the stale local copy.
            var cleared = DeleteFile(newPath);
            if (!cleared.Success) return cleared;
        }
        else if (dstAction is "delete" or "move/delete")
        {
            // The destination exists at head and is scheduled for delete; p4 move
            // cannot target it even so. Emulate the rename: cancel the delete, carry
            // the source content over, reopen the destination for edit, and delete
            // the source path. (The edit must come after the file is in place —
            // p4 edit fails trying to chmod a missing local file.)
            P4(["revert", "-k", newPath]);
            var staleError = RemoveLocal(newPath);
            if (staleError is not null) return staleError;
            var movedResult = _fs.RenameFile(oldPath, newPath);
            if (!movedResult.Success) return movedResult;
            var editResult = P4(["edit", newPath]);
            if (editResult.ExitCode != 0)
                return VCResult.Error($"Cannot rename '{oldPath}' over pending-delete '{newPath}': {editResult.Error ?? editResult.Output}");
            return DeleteFile(oldPath);
        }

        var fstat = Fstat(oldPath);
        if (!IsInDepotFstat(fstat)) return _fs.RenameFile(oldPath, newPath);

        var action = PendingAction(fstat);
        if (action is "delete" or "move/delete")
        {
            // Re-created on disk while scheduled for delete: cancel the delete so
            // the file can be reopened and moved.
            var reopened = ReopenAfterPendingDelete(oldPath, fstat, action);
            if (!reopened.Success) return reopened;
        }
        else if (action is null)
        {
            P4(["edit", oldPath]);
        }
        // Files already open for add/edit/move-add can be moved directly.

        var result = P4(["move", oldPath, newPath]);
        // p4 move reports several refusals at exit 0 ('not opened for edit',
        // 'can't move to an existing file', 'is synced; use -f') — require the
        // positive 'moved from' confirmation instead of blacklisting them.
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (result.ExitCode == 0 && combined.Contains("moved from")) return VCResult.Ok();
        return VCResult.Error($"Cannot rename '{oldPath}' in Perforce: {result.Error ?? result.Output}");
    }

    public VCResult RenameFolder(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath)) return VCResult.Ok();
        // p4 move with /... wildcard handles tracked files and physically moves them.
        var isWin = Path.DirectorySeparatorChar == '\\';
        var src = (isWin ? oldPath.Replace('\\', '/') : oldPath) + "/...";
        var dst = (isWin ? newPath.Replace('\\', '/') : newPath) + "/...";
        P4(["edit", src]);
        P4(["move", src, dst]);
        // Move any untracked files that p4 left behind.
        if (Directory.Exists(oldPath))
        {
            try
            {
                CopyDirectory(oldPath, newPath);
                ClearReadOnly(oldPath);
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

    private static async Task<string?> FstatAsync(string filePath)
    {
        var result = await P4Async(["fstat", filePath]).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : null;
    }

    private static readonly Regex PendingActionField = new(@"^\.\.\. action (\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex MovedFileField = new(@"^\.\.\. movedFile (\S+)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The pending changelist action for a file ('add', 'edit', 'delete',
    /// 'move/add', 'move/delete', ...) or null if the file is not opened.
    /// </summary>
    private static string? PendingAction(string? fstat)
    {
        if (fstat is null) return null;
        var match = PendingActionField.Match(fstat);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Depot path of the other half of a pending move, or null.</summary>
    private static string? MovedCounterpart(string? fstat)
    {
        if (fstat is null) return null;
        var match = MovedFileField.Match(fstat);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The file is scheduled for delete (or is the source of a pending rename) but
    /// exists again on disk. Cancel the pending delete keeping the local content,
    /// then reopen the file for edit.
    /// <para>
    /// p4 refuses to revert a move/delete source on its own ('has been moved, not
    /// reverted', exit 0); reverting the move/add half clears both ends of the pair.
    /// </para>
    /// </summary>
    private static VCResult ReopenAfterPendingDelete(string filePath, string? fstat, string action)
    {
        if (action == "move/delete")
        {
            var target = MovedCounterpart(fstat);
            P4(["revert", "-k", target ?? filePath]);
            // The renamed half is now untracked but still on disk; reopen it for add
            // so the earlier rename isn't silently dropped from the changelist.
            if (target is not null) P4(["add", target]);
        }
        else
        {
            P4(["revert", "-k", filePath]);
        }
        var editResult = P4(["edit", filePath]);
        if (editResult.ExitCode == 0) return VCResult.Ok("File reopened for edit after pending delete was reverted");
        return VCResult.Error($"Cannot reopen '{filePath}' for edit after reverting pending delete: {editResult.Error ?? editResult.Output}");
    }

    /// <summary>Async twin of <see cref="ReopenAfterPendingDelete"/>.</summary>
    private static async Task<VCResult> ReopenAfterPendingDeleteAsync(string filePath, string? fstat, string action)
    {
        if (action == "move/delete")
        {
            var target = MovedCounterpart(fstat);
            await P4Async(["revert", "-k", target ?? filePath]).ConfigureAwait(false);
            if (target is not null) await P4Async(["add", target]).ConfigureAwait(false);
        }
        else
        {
            await P4Async(["revert", "-k", filePath]).ConfigureAwait(false);
        }
        var editResult = await P4Async(["edit", filePath]).ConfigureAwait(false);
        if (editResult.ExitCode == 0) return VCResult.Ok("File reopened for edit after pending delete was reverted");
        return VCResult.Error($"Cannot reopen '{filePath}' for edit after reverting pending delete: {editResult.Error ?? editResult.Output}");
    }

    /// <summary>Removes the local copy if present; null on success, error result on failure.</summary>
    private static VCResult? RemoveLocal(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            new FileInfo(filePath).IsReadOnly = false;
            File.Delete(filePath);
            return null;
        }
        catch (Exception ex)
        {
            return VCResult.Error($"Cannot remove '{filePath}' from disk: {ex.Message}");
        }
    }

    private static bool IsInDepotFstat(string? fstat)
    {
        if (fstat is null) return false;

        // p4 fstat exits 0 for workspace-mapped files even if never submitted to the depot,
        // returning only clientFile/isMapped. Only treat the file as depot-tracked when
        // depot metadata (headRev or depotFile) is present.
        var hasDepotFile = fstat.Contains("headRev") || fstat.Contains("depotFile");
        if (!hasDepotFile) return false;

        // If the file was submitted as deleted at head revision, it's effectively untracked
        // unless it is currently reopened in a changelist.
        var isDeletedAtHead = fstat.Contains("headAction delete") || fstat.Contains("headAction move/delete");
        var isOpened = fstat.Contains("... action ");

        if (isDeletedAtHead && !isOpened) return false;

        return true;
    }

    private static CommandRunner.Result P4(string[] args) =>
        CommandRunner.Run("p4", args);

    private static Task<CommandRunner.Result> P4Async(string[] args) =>
        CommandRunner.RunAsync("p4", args);

    private static void ClearReadOnly(string dirPath)
    {
        foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }
    /// <summary>
    /// Status for a batch of files in ONE <c>p4 -ztag fstat</c> spawn.
    /// <para>
    /// Tracked-ness follows the same rules as the write path: workspace-mapped is
    /// not depot-tracked; deleted-at-head is untracked unless currently reopened.
    /// Lock / checkout info comes from otherOpen / otherLock / ourLock / action;
    /// staleness from haveRev &lt; headRev.
    /// </para>
    /// </summary>
    public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false)
    {
        // remote is ignored: p4 fstat is already a server call carrying lock/staleness.
        _ = remote;
        var result = P4(["-ztag", "fstat", .. filePaths]);
        return BuildPerforceStatuses(result.Output, filePaths);
    }

    /// <summary>Async twin of <see cref="Status"/>: one <c>p4 -ztag fstat</c>, awaited.</summary>
    public async Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false)
    {
        _ = remote;
        var result = await P4Async(["-ztag", "fstat", .. filePaths]).ConfigureAwait(false);
        return BuildPerforceStatuses(result.Output, filePaths);
    }

    /// <summary>Assemble per-file statuses from one <c>p4 -ztag fstat</c> output. Pure.</summary>
    private static IReadOnlyList<VCFileStatus> BuildPerforceStatuses(string output, IReadOnlyList<string> filePaths)
    {
        var records = output.Length > 0 ? ParseZtag(output) : new List<ZtagRecord>();

        var byClientFile = new Dictionary<string, ZtagRecord>();
        foreach (var record in records)
        {
            if (record.Fields.TryGetValue("clientFile", out var clientFile))
                byClientFile[Path.GetFullPath(clientFile)] = record;
        }

        var statuses = new List<VCFileStatus>();
        foreach (var filePath in filePaths)
        {
            var abs = Path.GetFullPath(filePath);
            var writable = FileStatusHelpers.WritableBit(abs);
            if (!byClientFile.TryGetValue(abs, out var record))
            {
                // fstat returned nothing for it: outside the client view / unknown to p4.
                statuses.Add(new VCFileStatus(abs, "perforce", writable, Tracked: false, Dirty: false));
                continue;
            }

            var headAction = record.Fields.GetValueOrDefault("headAction", "");
            var deletedAtHead = headAction is "delete" or "move/delete";
            var openedByMe = record.Fields.ContainsKey("action");
            var tracked = (record.Fields.ContainsKey("headRev") || record.Fields.ContainsKey("depotFile"))
                          && (!deletedAtHead || openedByMe);

            var otherOpen = record.Multi.GetValueOrDefault("otherOpen", new List<string>());
            var otherLock = record.Multi.GetValueOrDefault("otherLock", new List<string>());
            // otherLockN names the locker when present; otherwise an exclusive (+l)
            // filetype means any other opener effectively holds it.
            var holders = otherLock.Count > 0 ? otherLock
                        : record.Fields.ContainsKey("otherLock") ? otherOpen : new List<string>();
            var lockedBy = holders.Count > 0 ? holders : otherOpen;

            _ = int.TryParse(record.Fields.GetValueOrDefault("haveRev", "0"), out var haveRev);
            _ = int.TryParse(record.Fields.GetValueOrDefault("headRev", "0"), out var headRev);

            statuses.Add(new VCFileStatus(
                abs, "perforce", writable,
                Tracked: tracked,
                OpenedByMe: openedByMe || record.Fields.ContainsKey("ourLock") ? true : null,
                LockedBy: lockedBy.Count > 0 ? lockedBy : null,
                OutOfDate: headRev > haveRev && !deletedAtHead ? true : null,
                // Opened in a pending changelist (edit/add/delete) = pending local change.
                // A file changed on disk without being opened is not detected here.
                Dirty: openedByMe));
        }
        return statuses;
    }

    internal sealed record ZtagRecord(Dictionary<string, string> Fields, Dictionary<string, List<string>> Multi);

    private static readonly Regex IndexedField = new(@"^([A-Za-z/]+?)(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// Parse <c>p4 -ztag</c> output: <c>... field value</c> lines; a blank line
    /// separates records. Indexed fields (otherOpen0..N) collect by prefix.
    /// </summary>
    internal static List<ZtagRecord> ParseZtag(string output)
    {
        var records = new List<ZtagRecord>();
        ZtagRecord? current = null;
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith("... ", StringComparison.Ordinal))
            {
                if (line.Trim().Length == 0) current = null; // record separator
                continue;
            }
            if (current is null)
            {
                current = new ZtagRecord(new Dictionary<string, string>(), new Dictionary<string, List<string>>());
                records.Add(current);
            }
            var body = line[4..];
            var space = body.IndexOf(' ');
            var field = space == -1 ? body : body[..space];
            var value = space == -1 ? "" : body[(space + 1)..];
            var indexed = IndexedField.Match(field);
            if (indexed.Success)
            {
                var prefix = indexed.Groups[1].Value;
                if (!current.Multi.TryGetValue(prefix, out var list))
                    current.Multi[prefix] = list = new List<string>();
                list.Add(value);
            }
            else
            {
                current.Fields[field] = value;
            }
        }
        return records;
    }
}

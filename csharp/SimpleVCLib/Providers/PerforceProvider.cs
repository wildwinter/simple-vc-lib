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

        if (!IsInDepot(filePath))
            return _fs.PrepareToWrite(filePath);

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
        if (fstat.Contains("... action delete"))
        {
            P4(["revert", "-k", filePath]);
            var editResult = P4(["edit", filePath]);
            if (editResult.ExitCode == 0) return VCResult.Ok("File reopened for edit after pending delete was reverted");
            return VCResult.Error($"Cannot reopen '{filePath}' for edit after reverting pending delete: {editResult.Error ?? editResult.Output}");
        }

        if (IsInDepotFstat(fstat)) return VCResult.Ok();

        var result = P4(["add", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File opened for add in Perforce");
        // File is ignored (e.g. matches a .p4ignore pattern) — treat as outside the depot.
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return _fs.FinishedWrite(filePath);
        return VCResult.Error($"Cannot add '{filePath}' to Perforce: {result.Error ?? result.Output}");
    }

    public VCResult DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (IsInDepot(filePath))
        {
            var result = P4(["delete", filePath]);
            if (result.ExitCode != 0)
                return VCResult.Error($"Cannot delete '{filePath}' from Perforce: {result.Error ?? result.Output}");
            // p4 delete marks for deletion but leaves the file on disk as read-only.
            // Physically remove it, consistent with git rm and svn delete.
            if (File.Exists(filePath))
            {
                try
                {
                    new FileInfo(filePath).IsReadOnly = false;
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    return VCResult.Error($"Cannot remove '{filePath}' from disk: {ex.Message}");
                }
            }
            return VCResult.Ok();
        }

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
        if (IsInDepot(oldPath))
        {
            P4(["edit", oldPath]);
            var result = P4(["move", oldPath, newPath]);
            var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
            if (result.ExitCode == 0 && !combined.Contains("not opened for")) return VCResult.Ok();
            return VCResult.Error($"Cannot rename '{oldPath}' in Perforce: {result.Error ?? result.Output}");
        }
        return _fs.RenameFile(oldPath, newPath);
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

    private static bool IsInDepot(string filePath) => IsInDepotFstat(Fstat(filePath));

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
    public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths)
    {
        var result = P4(["-ztag", "fstat", .. filePaths]);
        var records = result.Output.Length > 0 ? ParseZtag(result.Output) : new List<ZtagRecord>();

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
                statuses.Add(new VCFileStatus(abs, "perforce", writable, Tracked: false));
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
                OutOfDate: headRev > haveRev && !deletedAtHead ? true : null));
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

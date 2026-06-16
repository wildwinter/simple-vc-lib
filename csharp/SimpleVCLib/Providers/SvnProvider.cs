using System.Text.RegularExpressions;

namespace SimpleVCLib;

/// <summary>
/// Subversion (SVN) provider.
/// SVN files are normally writable. Files with the <c>svn:needs-lock</c> property
/// are read-only until locked; PrepareToWrite handles this via <c>svn lock</c>.
/// </summary>
public partial class SvnProvider : IVCProvider
{
    private static readonly FilesystemProvider _fs = new();
    public string Name => "svn";

    public VCResult PrepareToWrite(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        var fsResult = _fs.PrepareToWrite(filePath);
        if (fsResult.Success) return VCResult.Ok();

        // File is read-only — only expected for files with svn:needs-lock set.
        if (!IsTracked(filePath))
            return VCResult.Error($"Cannot make '{filePath}' writable");

        var result = Svn(["lock", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File locked in SVN");

        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked by") || combined.Contains("steal lock"))
            return VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked by another user");
        if (combined.Contains("out of date"))
            return VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date — update before locking");

        return VCResult.Error($"Cannot lock '{filePath}' in SVN: {result.Error ?? result.Output}");
    }

    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        if (IsTracked(filePath)) return VCResult.Ok();

        // An unversioned file inside a working copy has a versioned parent directory.
        // If the parent is not tracked either, the file is outside the working copy entirely.
        var parentDir = Path.GetDirectoryName(filePath);
        if (parentDir == null || !IsTracked(parentDir))
            return _fs.FinishedWrite(filePath);

        var result = Svn(["add", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File added to SVN");
        // File is ignored — treat as outside the working copy.
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return _fs.FinishedWrite(filePath);
        return VCResult.Error($"Cannot add '{filePath}' to SVN: {result.Error ?? result.Output}");
    }

    /// <summary>Async twin of <see cref="PrepareToWrite"/>.</summary>
    public async Task<VCResult> PrepareToWriteAsync(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        var fsResult = await _fs.PrepareToWriteAsync(filePath).ConfigureAwait(false);
        if (fsResult.Success) return VCResult.Ok();

        if (!await IsTrackedAsync(filePath).ConfigureAwait(false))
            return VCResult.Error($"Cannot make '{filePath}' writable");

        var result = await SvnAsync(["lock", filePath]).ConfigureAwait(false);
        if (result.ExitCode == 0) return VCResult.Ok("File locked in SVN");

        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked by") || combined.Contains("steal lock"))
            return VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked by another user");
        if (combined.Contains("out of date"))
            return VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date — update before locking");

        return VCResult.Error($"Cannot lock '{filePath}' in SVN: {result.Error ?? result.Output}");
    }

    /// <summary>Async twin of <see cref="FinishedWrite"/>.</summary>
    public async Task<VCResult> FinishedWriteAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        if (await IsTrackedAsync(filePath).ConfigureAwait(false)) return VCResult.Ok();

        var parentDir = Path.GetDirectoryName(filePath);
        if (parentDir == null || !await IsTrackedAsync(parentDir).ConfigureAwait(false))
            return await _fs.FinishedWriteAsync(filePath).ConfigureAwait(false);

        var result = await SvnAsync(["add", filePath]).ConfigureAwait(false);
        if (result.ExitCode == 0) return VCResult.Ok("File added to SVN");
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return await _fs.FinishedWriteAsync(filePath).ConfigureAwait(false);
        return VCResult.Error($"Cannot add '{filePath}' to SVN: {result.Error ?? result.Output}");
    }

    // Delete/rename reuse the tested sync logic on a thread-pool thread.
    public Task<VCResult> DeleteFileAsync(string filePath) => Task.Run(() => DeleteFile(filePath));
    public Task<VCResult> DeleteFolderAsync(string folderPath) => Task.Run(() => DeleteFolder(folderPath));
    public Task<VCResult> RenameFileAsync(string oldPath, string newPath) => Task.Run(() => RenameFile(oldPath, newPath));
    public Task<VCResult> RenameFolderAsync(string oldPath, string newPath) => Task.Run(() => RenameFolder(oldPath, newPath));

    public VCResult DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (IsTracked(filePath))
        {
            var result = Svn(["delete", "--force", filePath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot delete '{filePath}' from SVN: {result.Error ?? result.Output}");
        }

        return _fs.DeleteFile(filePath);
    }

    public VCResult DeleteFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return VCResult.Ok();

        if (IsTracked(folderPath))
        {
            var result = Svn(["delete", "--force", folderPath]);
            if (result.ExitCode != 0)
                return VCResult.Error($"Cannot delete folder '{folderPath}' from SVN: {result.Error ?? result.Output}");
            return VCResult.Ok();
        }

        return _fs.DeleteFolder(folderPath);
    }

    public VCResult RenameFile(string oldPath, string newPath)
    {
        if (!File.Exists(oldPath)) return VCResult.Ok();
        if (IsTracked(oldPath))
        {
            var result = Svn(["move", "--force", oldPath, newPath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot rename '{oldPath}' in SVN: {result.Error ?? result.Output}");
        }
        return _fs.RenameFile(oldPath, newPath);
    }

    public VCResult RenameFolder(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath)) return VCResult.Ok();
        if (IsTracked(oldPath))
        {
            var result = Svn(["move", "--force", oldPath, newPath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot rename folder '{oldPath}' in SVN: {result.Error ?? result.Output}");
        }
        return _fs.RenameFolder(oldPath, newPath);
    }

    // -------------------------------------------------------------------------

    private static bool IsTracked(string path)
    {
        var result = Svn(["info", path]);
        return result.ExitCode == 0;
    }

    private static async Task<bool> IsTrackedAsync(string path) =>
        (await SvnAsync(["info", path]).ConfigureAwait(false)).ExitCode == 0;

    private static CommandRunner.Result Svn(string[] args) =>
        CommandRunner.Run("svn", args);

    private static Task<CommandRunner.Result> SvnAsync(string[] args) =>
        CommandRunner.RunAsync("svn", args);

    [GeneratedRegex(@"<entry\b[^>]*\bpath=""([^""]*)""[^>]*>(.*?)</entry>", RegexOptions.Singleline)]
    private static partial Regex EntryRegex();
    [GeneratedRegex(@"<wc-status\b([^>]*)>(.*?)</wc-status>", RegexOptions.Singleline)]
    private static partial Regex WcStatusContainerRegex();
    [GeneratedRegex(@"<wc-status\b([^>]*?)/>")]
    private static partial Regex WcStatusSelfCloseRegex();
    [GeneratedRegex(@"<repos-status\b([^>]*)>(.*?)</repos-status>", RegexOptions.Singleline)]
    private static partial Regex ReposStatusContainerRegex();
    [GeneratedRegex(@"<repos-status\b([^>]*?)/>")]
    private static partial Regex ReposStatusSelfCloseRegex();
    [GeneratedRegex(@"<lock\b[^>]*>.*?<owner>([^<]*)</owner>", RegexOptions.Singleline)]
    private static partial Regex LockOwnerRegex();
    [GeneratedRegex(@"\bitem=""([^""]*)""")]
    private static partial Regex ItemRegex();
    [GeneratedRegex(@"\bprops=""([^""]*)""")]
    private static partial Regex PropsRegex();

    private sealed record SvnInfo(
        bool? Tracked, bool? Dirty,
        bool OpenedByMe = false, IReadOnlyList<string>? LockedBy = null, bool OutOfDate = false);

    /// <summary>item values meaning a tracked file with pending local changes.</summary>
    private static readonly HashSet<string> SvnDirtyItems =
        ["modified", "added", "deleted", "replaced", "conflicted", "missing", "incomplete"];
    /// <summary>item values meaning the path is not under version control.</summary>
    private static readonly HashSet<string> SvnUntrackedItems = ["unversioned", "ignored"];

    /// <summary>
    /// Status for a batch of files in ONE <c>svn status --xml -v</c> spawn. <c>-v</c>
    /// lists clean versioned files too (item="normal"), so tracked-clean is
    /// distinguished from untracked; <c>--xml</c> is the stable machine format.
    /// <para>
    /// With <paramref name="remote"/> = true it adds <c>-u</c>, a server round-trip that
    /// makes svn emit a <c>&lt;repos-status&gt;</c> per entry: <c>item != "none"</c> means
    /// a newer revision exists (<c>OutOfDate</c>), and a <c>&lt;lock&gt;</c> there names the
    /// holder. A <c>&lt;lock&gt;</c> inside <c>&lt;wc-status&gt;</c> is our own token
    /// (<c>OpenedByMe</c>); a server lock without it is someone else's (<c>LockedBy</c>).
    /// </para>
    /// </summary>
    public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false) =>
        BuildSvnStatuses(Svn(SvnStatusArgs(filePaths, remote)).Output, filePaths);

    /// <summary>Async twin of <see cref="Status"/>: one <c>svn status --xml</c>, awaited.</summary>
    public async Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false)
    {
        var result = await SvnAsync(SvnStatusArgs(filePaths, remote)).ConfigureAwait(false);
        return BuildSvnStatuses(result.Output, filePaths);
    }

    /// <summary>Build the <c>svn status</c> argument list (adds <c>-u</c> for a remote read).</summary>
    private static string[] SvnStatusArgs(IReadOnlyList<string> filePaths, bool remote)
    {
        var targets = filePaths.Select(Path.GetFullPath).ToArray();
        return remote
            ? ["status", "--xml", "-v", "-u", .. targets]
            : ["status", "--xml", "-v", .. targets];
    }

    /// <summary>Assemble per-file statuses from <c>svn status --xml</c> output. Pure.</summary>
    private static IReadOnlyList<VCFileStatus> BuildSvnStatuses(string output, IReadOnlyList<string> filePaths)
    {
        var byPath = new Dictionary<string, SvnInfo>();
        var byBase = new Dictionary<string, List<SvnInfo>>();
        // A bad (non-working-copy) target makes svn exit non-zero, but it still emits
        // XML for the rest - parse whatever we get and fall back per file.
        if (output.Length > 0)
        {
            foreach (Match entry in EntryRegex().Matches(output))
            {
                var entryPath = Path.GetFullPath(DecodeXmlAttr(entry.Groups[1].Value));
                var info = ParseSvnEntry(entry.Groups[2].Value);
                if (info is null) continue;
                byPath[entryPath] = info;
                var baseName = Path.GetFileName(entryPath);
                if (!byBase.TryGetValue(baseName, out var list)) byBase[baseName] = list = [];
                list.Add(info);
            }
        }

        return filePaths.Select(filePath =>
        {
            var abs = Path.GetFullPath(filePath);
            var writable = FileStatusHelpers.WritableBit(abs);
            if (!byPath.TryGetValue(abs, out var info))
            {
                // svn may canonicalize the echoed path (symlinked parents); fall back
                // to a basename match when it is unambiguous.
                if (byBase.TryGetValue(Path.GetFileName(abs), out var sameName) && sameName.Count == 1)
                    info = sameName[0];
                else
                    return new VCFileStatus(abs, "svn", writable);
            }
            return new VCFileStatus(abs, "svn", writable,
                Tracked: info.Tracked, Dirty: info.Dirty,
                OpenedByMe: info.OpenedByMe ? true : null,
                LockedBy: info.LockedBy,
                OutOfDate: info.OutOfDate ? true : null);
        }).ToList();
    }

    /// <summary>
    /// Parse one <c>&lt;entry&gt;</c> body into tracked / dirty plus, when <c>-u</c> added
    /// a <c>&lt;repos-status&gt;</c>, openedByMe / lockedBy / outOfDate.
    /// </summary>
    private static SvnInfo? ParseSvnEntry(string body)
    {
        var wc = ExtractTag(body, WcStatusContainerRegex(), WcStatusSelfCloseRegex());
        if (wc is null) return null;
        var (wcAttrs, wcInner) = wc.Value;
        var item = ItemRegex().Match(wcAttrs) is { Success: true } im ? im.Groups[1].Value : "";
        var props = PropsRegex().Match(wcAttrs) is { Success: true } pm ? pm.Groups[1].Value : "";
        var (tracked, dirty) = ClassifySvnStatus(item, props);

        var openedByMe = false;
        IReadOnlyList<string>? lockedBy = null;
        var outOfDate = false;
        var repos = ExtractTag(body, ReposStatusContainerRegex(), ReposStatusSelfCloseRegex());
        if (repos is not null)
        {
            var (reposAttrs, reposInner) = repos.Value;
            // repos-status only appears with -u. item != 'none' => server has changes we lack.
            var reposItem = ItemRegex().Match(reposAttrs) is { Success: true } rm ? rm.Groups[1].Value : "";
            if (reposItem.Length > 0 && reposItem != "none") outOfDate = true;
            // A <lock> inside wc-status is our own held token; inside repos-status it is the
            // authoritative server holder. Ours => openedByMe; theirs (no wc token) => lockedBy.
            var heldByUs = wcInner.Contains("<lock", StringComparison.Ordinal);
            var owner = LockOwnerRegex().Match(reposInner) is { Success: true } lo ? lo.Groups[1].Value : "";
            if (heldByUs) openedByMe = true;
            else if (owner.Length > 0) lockedBy = [DecodeXmlAttr(owner)];
        }
        return new SvnInfo(tracked, dirty, openedByMe, lockedBy, outOfDate);
    }

    /// <summary>Extract a tag's (attrs, inner) - container form first, then self-closing.</summary>
    private static (string Attrs, string Inner)? ExtractTag(string body, Regex container, Regex selfClose)
    {
        if (container.Match(body) is { Success: true } c) return (c.Groups[1].Value, c.Groups[2].Value);
        if (selfClose.Match(body) is { Success: true } s) return (s.Groups[1].Value, "");
        return null;
    }

    /// <summary>
    /// Map an <c>svn status</c> wc-status (item + props) to tracked / dirty. <c>props</c>
    /// carries property-only modifications ("modified" / "conflicted").
    /// </summary>
    private static (bool? Tracked, bool? Dirty) ClassifySvnStatus(string item, string props)
    {
        if (SvnUntrackedItems.Contains(item)) return (false, false);
        if (item == "normal" || SvnDirtyItems.Contains(item))
            return (true, SvnDirtyItems.Contains(item) || props is "modified" or "conflicted");
        // 'external', 'none', or anything unrecognised: can't say.
        return (null, null);
    }

    /// <summary>Decode the handful of XML entities svn emits in path attributes.</summary>
    private static string DecodeXmlAttr(string value) =>
        value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
             .Replace("&apos;", "'").Replace("&amp;", "&"); // &amp; last
}

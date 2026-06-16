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

    private static CommandRunner.Result Svn(string[] args) =>
        CommandRunner.Run("svn", args);

    [GeneratedRegex(@"<entry\b[^>]*\bpath=""([^""]*)""[^>]*>(.*?)</entry>", RegexOptions.Singleline)]
    private static partial Regex EntryRegex();
    [GeneratedRegex(@"<wc-status\b([^>]*)>")]
    private static partial Regex WcStatusRegex();
    [GeneratedRegex(@"\bitem=""([^""]*)""")]
    private static partial Regex ItemRegex();
    [GeneratedRegex(@"\bprops=""([^""]*)""")]
    private static partial Regex PropsRegex();

    /// <summary>item values meaning a tracked file with pending local changes.</summary>
    private static readonly HashSet<string> SvnDirtyItems =
        ["modified", "added", "deleted", "replaced", "conflicted", "missing", "incomplete"];
    /// <summary>item values meaning the path is not under version control.</summary>
    private static readonly HashSet<string> SvnUntrackedItems = ["unversioned", "ignored"];

    /// <summary>
    /// Status for a batch of files in ONE <c>svn status --xml -v</c> spawn. <c>-v</c>
    /// lists clean versioned files too (item="normal"), so tracked-clean is
    /// distinguished from untracked; <c>--xml</c> is the stable machine format. Lock
    /// owners and out-of-date (<c>svn status -u</c>, a server round-trip) remain TODO.
    /// </summary>
    public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths)
    {
        var targets = filePaths.Select(Path.GetFullPath).ToArray();
        // A bad (non-working-copy) target makes svn exit non-zero, but it still emits
        // XML for the rest - parse whatever we get and fall back per file.
        var result = Svn(["status", "--xml", "-v", .. targets]);
        var byPath = new Dictionary<string, (bool? Tracked, bool? Dirty)>();
        var byBase = new Dictionary<string, List<(bool? Tracked, bool? Dirty)>>();
        if (result.Output.Length > 0)
        {
            foreach (Match entry in EntryRegex().Matches(result.Output))
            {
                var entryPath = Path.GetFullPath(DecodeXmlAttr(entry.Groups[1].Value));
                var wcStatus = WcStatusRegex().Match(entry.Groups[2].Value);
                if (!wcStatus.Success) continue;
                var attrs = wcStatus.Groups[1].Value;
                var item = ItemRegex().Match(attrs) is { Success: true } im ? im.Groups[1].Value : "";
                var props = PropsRegex().Match(attrs) is { Success: true } pm ? pm.Groups[1].Value : "";
                var info = ClassifySvnStatus(item, props);
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
            return new VCFileStatus(abs, "svn", writable, Tracked: info.Tracked, Dirty: info.Dirty);
        }).ToList();
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

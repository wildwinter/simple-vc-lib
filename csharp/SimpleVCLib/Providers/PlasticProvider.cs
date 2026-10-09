using System.Text.RegularExpressions;

namespace SimpleVCLib;

/// <summary>
/// Plastic SCM / Unity Version Control provider.
/// Uses the <c>cm</c> CLI (Plastic SCM command-line client).
/// Files under Plastic SCM are read-only until checked out.
/// </summary>
public partial class PlasticProvider : IVCProvider
{
    private static readonly FilesystemProvider _fs = new();
    public string Name => "plastic";

    /// <summary><c>cm whoami</c> - the same call the lock check makes to tell our lock from another's.</summary>
    public string? CurrentUser(string? pathHint = null)
    {
        var u = PlasticWhoami();
        return u.Length > 0 ? u : null;
    }

    /// <summary>Async twin of <see cref="CurrentUser"/>.</summary>
    public async Task<string?> CurrentUserAsync(string? pathHint = null)
    {
        var u = await PlasticWhoamiAsync().ConfigureAwait(false);
        return u.Length > 0 ? u : null;
    }

    /// <summary>
    /// Check a controlled file out before it is written. A file already checked out (or
    /// otherwise pending) in this workspace is not checked out again: a second <c>cm co</c>
    /// on it fails, and under lock rules it fails as "locked" by our own checkout. Private,
    /// ignored, and out-of-workspace files only need to be writable.
    /// </summary>
    public VCResult PrepareToWrite(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();
        if (LocalState(filePath) != CmState.Controlled) return _fs.PrepareToWrite(filePath);
        return CheckoutResult(filePath, Cm(["co", filePath]));
    }

    /// <summary>Add a new private file to Plastic SCM after it is written.</summary>
    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");
        if (LocalState(filePath) != CmState.Private) return _fs.FinishedWrite(filePath);
        return AddResult(filePath, Cm(["add", filePath]));
    }

    /// <summary>Async twin of <see cref="PrepareToWrite"/>.</summary>
    public async Task<VCResult> PrepareToWriteAsync(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();
        if (await LocalStateAsync(filePath).ConfigureAwait(false) != CmState.Controlled)
            return await _fs.PrepareToWriteAsync(filePath).ConfigureAwait(false);
        return CheckoutResult(filePath, await CmAsync(["co", filePath]).ConfigureAwait(false));
    }

    /// <summary>Async twin of <see cref="FinishedWrite"/>.</summary>
    public async Task<VCResult> FinishedWriteAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");
        if (await LocalStateAsync(filePath).ConfigureAwait(false) != CmState.Private)
            return await _fs.FinishedWriteAsync(filePath).ConfigureAwait(false);
        return AddResult(filePath, await CmAsync(["add", filePath]).ConfigureAwait(false));
    }

    /// <summary>The result of the <c>cm co</c> that checks a controlled file out.</summary>
    private static VCResult CheckoutResult(string filePath, CommandRunner.Result result)
    {
        if (result.ExitCode == 0) return VCResult.Ok();
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked") || combined.Contains("exclusive"))
            return VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked");
        if (combined.Contains("out of date") || combined.Contains("not latest"))
            return VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date — update before editing");
        return VCResult.Error($"Cannot check out '{filePath}': {result.Error ?? result.Output}");
    }

    /// <summary>The result of the <c>cm add</c> that puts a new private file under version control.</summary>
    private static VCResult AddResult(string filePath, CommandRunner.Result result) =>
        result.ExitCode == 0
            ? VCResult.Ok("File added to Plastic SCM")
            : VCResult.Error($"Cannot add '{filePath}' to Plastic SCM: {result.Error ?? result.Output}");

    /// <summary>
    /// Undo what <see cref="PrepareToWrite"/> did, for an all-or-nothing batch that has to
    /// back out. A controlled file this batch checked out is released with
    /// <c>cm undocheckout</c>.
    /// <para>
    /// A file <paramref name="before"/> reports as OpenedByMe (our lock) or Dirty (already
    /// checked out or changed) is left alone: that state is the user's, and
    /// <c>cm undocheckout</c> would discard their changes. Private files get the filesystem undo.
    /// </para>
    /// </summary>
    public VCResult UndoPrepareToWrite(string filePath, VCFileStatus before)
    {
        if (!File.Exists(filePath) || before.OpenedByMe == true) return VCResult.Ok();
        if (!IsTracked(filePath)) return _fs.UndoPrepareToWrite(filePath, before);
        if (before.Dirty == true) return VCResult.Ok();
        return UndoCheckoutResult(filePath, Cm(["undocheckout", filePath]));
    }

    /// <summary>Async twin of <see cref="UndoPrepareToWrite"/>.</summary>
    public async Task<VCResult> UndoPrepareToWriteAsync(string filePath, VCFileStatus before)
    {
        if (!File.Exists(filePath) || before.OpenedByMe == true) return VCResult.Ok();
        if (!await IsTrackedAsync(filePath).ConfigureAwait(false))
            return await _fs.UndoPrepareToWriteAsync(filePath, before).ConfigureAwait(false);
        if (before.Dirty == true) return VCResult.Ok();
        return UndoCheckoutResult(filePath, await CmAsync(["undocheckout", filePath]).ConfigureAwait(false));
    }

    /// <summary>The result of the <c>cm undocheckout</c> that undoes a batch checkout.</summary>
    private static VCResult UndoCheckoutResult(string filePath, CommandRunner.Result result) =>
        result.ExitCode == 0
            ? VCResult.Ok("Checkout undone in Plastic SCM")
            : VCResult.Error($"Cannot undo checkout of '{filePath}' in Plastic SCM: {result.Error ?? result.Output}");

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
            var result = Cm(["remove", filePath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot delete '{filePath}' from Plastic SCM: {result.Error ?? result.Output}");
        }

        return _fs.DeleteFile(filePath);
    }

    public VCResult DeleteFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return VCResult.Ok();

        if (IsTracked(folderPath))
        {
            var result = Cm(["remove", folderPath]);
            if (result.ExitCode != 0)
                return VCResult.Error($"Cannot delete folder '{folderPath}' from Plastic SCM: {result.Error ?? result.Output}");
        }

        if (Directory.Exists(folderPath))
            return _fs.DeleteFolder(folderPath);

        return VCResult.Ok();
    }

    public VCResult RenameFile(string oldPath, string newPath)
    {
        if (!File.Exists(oldPath)) return VCResult.Ok();
        if (IsTracked(oldPath))
        {
            var result = Cm(["mv", oldPath, newPath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot rename '{oldPath}' in Plastic SCM: {result.Error ?? result.Output}");
        }
        return _fs.RenameFile(oldPath, newPath);
    }

    public VCResult RenameFolder(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath)) return VCResult.Ok();
        if (IsTracked(oldPath))
        {
            var result = Cm(["mv", oldPath, newPath]);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot rename folder '{oldPath}' in Plastic SCM: {result.Error ?? result.Output}");
        }
        return _fs.RenameFolder(oldPath, newPath);
    }

    // -------------------------------------------------------------------------

    /// <summary>One path's state in the workspace; see <see cref="LocalStateFrom"/>.</summary>
    private enum CmState { Outside, Private, Ignored, CheckedOut, Controlled }

    /// <summary>Tracked by Plastic SCM: controlled, whether or not it is checked out.</summary>
    private static bool IsTracked(string path) => Tracked(LocalState(path));

    private static async Task<bool> IsTrackedAsync(string path) =>
        Tracked(await LocalStateAsync(path).ConfigureAwait(false));

    private static bool Tracked(CmState state) => state is CmState.Controlled or CmState.CheckedOut;

    private static CmState LocalState(string path) => LocalStateFrom(Cm(PlasticStatusArgs([path])), path);

    private static async Task<CmState> LocalStateAsync(string path) =>
        LocalStateFrom(await CmAsync(PlasticStatusArgs([path])).ConfigureAwait(false), path);

    /// <summary>
    /// One path's state in the workspace, from <c>cm status --machinereadable --all --ignored</c>:
    /// Outside when cm fails (not in a workspace, or cm is missing); Private / Ignored when
    /// listed PR / IG; CheckedOut when it already has a pending controlled change here
    /// (checked out, added, copied, replaced, or moved), so it needs no checkout; otherwise
    /// Controlled (unchanged files are not listed at all, and a file changed without a
    /// checkout is listed CH).
    /// <para>
    /// <c>cm status --short</c> cannot answer this: it lists only paths with changes, so an
    /// unchanged controlled file reads as untracked.
    /// </para>
    /// </summary>
    private static CmState LocalStateFrom(CommandRunner.Result result, string path)
    {
        if (result.ExitCode != 0) return CmState.Outside;
        var info = FindStatusInfo(result.Output, path);
        if (info is null) return CmState.Controlled;
        if (!info.Value.Tracked) return info.Value.Ignored ? CmState.Ignored : CmState.Private;
        return info.Value.CheckedOut ? CmState.CheckedOut : CmState.Controlled;
    }

    /// <summary>
    /// The <c>cm status</c> line for one path: matched by absolute path, else by file name
    /// when exactly one listed path has it. A folder's listing can include its contents, so
    /// the first line is not necessarily the path asked about.
    /// </summary>
    private static CmStatusInfo? FindStatusInfo(string output, string path)
    {
        var abs = Path.GetFullPath(path);
        var name = Path.GetFileName(abs);
        var sameName = new List<CmStatusInfo>();
        foreach (var line in output.Split('\n'))
        {
            var parsed = ParseCmStatusLine(line);
            if (parsed is null) continue;
            foreach (var p in parsed.Value.Paths)
            {
                var listed = Path.GetFullPath(p);
                if (PathComparer.Equals(listed, abs)) return parsed.Value.Info;
                if (Path.GetFileName(listed) == name) sameName.Add(parsed.Value.Info);
            }
        }
        return sameName.Count == 1 ? sameName[0] : null;
    }

    /// <summary>Compares absolute paths as cm prints them: case-insensitive on Windows.</summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static CommandRunner.Result Cm(string[] args) =>
        CommandRunner.Run("cm", args);

    private static Task<CommandRunner.Result> CmAsync(string[] args) =>
        CommandRunner.RunAsync("cm", args);

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedPathRegex();

    /// <summary>cm status codes for a controlled file with a pending change.</summary>
    private static readonly HashSet<string> PlasticDirtyCodes =
        ["CH", "CO", "AD", "CP", "RP", "MV", "DE", "LD", "LM"];
    /// <summary>codes meaning the path is not under version control.</summary>
    private static readonly HashSet<string> PlasticUntrackedCodes = ["PR", "IG"];
    /// <summary>codes for a pending controlled change in this workspace that needs no <c>cm co</c>.</summary>
    private static readonly HashSet<string> PlasticCheckedOutCodes = ["CO", "AD", "CP", "RP", "MV"];

    /// <summary>What one <c>cm status</c> line says about its path(s).</summary>
    private readonly record struct CmStatusInfo(bool Tracked, bool Dirty, bool CheckedOut, bool Ignored);

    /// <summary>fileinfo format whose fields the parser reads positionally (see UEPlasticPlugin).</summary>
    private const string FileinfoFormat =
        "{RevisionChangeset};{RevisionHeadChangeset};{RepSpec};{LockedBy};{LockedWhere};{ServerPath}";

    private sealed record PlasticRemote(bool OutOfDate, bool OpenedByMe, IReadOnlyList<string>? LockedBy);

    /// <summary>
    /// Status for a batch of files in ONE <c>cm status --machinereadable --all --ignored</c>
    /// spawn. The machine format lists one item per line as
    /// <c>&lt;2-letter code&gt; &lt;path&gt;</c> (absolute paths, quoted when they contain spaces).
    /// <para>
    /// Flag choice matters: <c>cm status</c> defaults to <c>--controlledchanged</c>, which
    /// omits a content-modified-but-not-checked-out file (CH) and local deletes/moves.
    /// <c>--all</c> adds changed + localdeleted + localmoved + private; <c>--ignored</c> adds
    /// IG. Together they surface every dirty and every untracked item, so a not-listed file
    /// can be read as clean-and-controlled.
    /// </para>
    /// <para>
    /// With <paramref name="remote"/> = true it follows up with ONE <c>cm fileinfo</c> over
    /// the controlled files (plus one <c>cm whoami</c>) to fill <c>OutOfDate</c> (loaded
    /// changeset &lt; head) and lock holder (<c>OpenedByMe</c> when that's us, else
    /// <c>LockedBy: ["user@workspace"]</c>).
    /// </para>
    /// <para>
    /// NOTE: status codes, flags, and the fileinfo format are validated against the Unity
    /// VCS CLI docs / UEPlasticPlugin, not a live workspace - worth one real smoke test.
    /// </para>
    /// </summary>
    public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false)
    {
        var result = Cm(PlasticStatusArgs(filePaths));
        var bases = BuildPlasticBases(result, filePaths);
        var remoteByPath = remote ? FetchPlasticRemote(bases) : null;
        return ComposePlasticStatuses(bases, remoteByPath);
    }

    /// <summary>Async twin of <see cref="Status"/>: <c>cm status</c> (+ fileinfo/whoami when remote).</summary>
    public async Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false)
    {
        var result = await CmAsync(PlasticStatusArgs(filePaths)).ConfigureAwait(false);
        var bases = BuildPlasticBases(result, filePaths);
        var remoteByPath = remote ? await FetchPlasticRemoteAsync(bases).ConfigureAwait(false) : null;
        return ComposePlasticStatuses(bases, remoteByPath);
    }

    private static string[] PlasticStatusArgs(IReadOnlyList<string> filePaths) =>
        ["status", "--machinereadable", "--all", "--ignored", .. filePaths.Select(Path.GetFullPath)];

    private readonly record struct PlasticBase(string Abs, bool Writable, bool? Tracked, bool? Dirty);

    /// <summary>Assemble the local (tracked / dirty) base statuses from <c>cm status</c> output.</summary>
    private static List<PlasticBase> BuildPlasticBases(CommandRunner.Result result, IReadOnlyList<string> filePaths)
    {
        var byPath = new Dictionary<string, CmStatusInfo>(PathComparer);
        var byBase = new Dictionary<string, List<CmStatusInfo>>();
        if (result.ExitCode == 0 && result.Output.Length > 0)
        {
            foreach (var line in result.Output.Split('\n'))
            {
                var parsed = ParseCmStatusLine(line);
                if (parsed is null) continue;
                var (info, paths) = parsed.Value;
                foreach (var p in paths)
                {
                    var abs = Path.GetFullPath(p);
                    byPath[abs] = info;
                    var baseName = Path.GetFileName(abs);
                    if (!byBase.TryGetValue(baseName, out var list)) byBase[baseName] = list = [];
                    list.Add(info);
                }
            }
        }

        return filePaths.Select(filePath =>
        {
            var abs = Path.GetFullPath(filePath);
            var writable = FileStatusHelpers.WritableBit(abs);
            CmStatusInfo? info = null;
            if (byPath.TryGetValue(abs, out var direct)) info = direct;
            else if (byBase.TryGetValue(Path.GetFileName(abs), out var same) && same.Count == 1) info = same[0];

            bool? tracked = null, dirty = null;
            if (info is not null) (tracked, dirty) = (info.Value.Tracked, info.Value.Dirty);
            // Not listed by `cm status --all --ignored` = controlled, no pending change.
            else if (File.Exists(abs)) (tracked, dirty) = (true, false);
            return new PlasticBase(abs, writable, tracked, dirty);
        }).ToList();
    }

    /// <summary>Combine local bases with any remote (fileinfo) info into the final records.</summary>
    private static List<VCFileStatus> ComposePlasticStatuses(
        List<PlasticBase> bases, Dictionary<string, PlasticRemote>? remoteByPath) =>
        bases.Select(b =>
        {
            PlasticRemote? r = remoteByPath is not null && remoteByPath.TryGetValue(b.Abs, out var rr) ? rr : null;
            return new VCFileStatus(b.Abs, "plastic", b.Writable, Tracked: b.Tracked, Dirty: b.Dirty,
                OpenedByMe: r?.OpenedByMe == true ? true : null,
                LockedBy: r?.LockedBy,
                OutOfDate: r?.OutOfDate == true ? true : null);
        }).ToList();

    /// <summary>
    /// Apply <c>cm fileinfo</c> output to the controlled files, zipped by index: out-of-date from
    /// loaded-vs-head changeset, and lock holder (OpenedByMe if that's me, else LockedBy).
    /// </summary>
    private static Dictionary<string, PlasticRemote> ApplyFileinfo(List<string> controlled, string output, string me)
    {
        var map = new Dictionary<string, PlasticRemote>();
        var lines = output.Split('\n').Where(l => l.Length > 0).ToList();
        for (var i = 0; i < controlled.Count && i < lines.Count; i++)
        {
            var f = lines[i].Split(';');
            if (f.Length < 5) continue;
            // A negative head means an unshelved/special revision - not a staleness signal.
            var outOfDate = int.TryParse(f[0], out var rev) && int.TryParse(f[1], out var head)
                            && head >= 0 && rev < head;
            var lockedByName = f[3];
            var lockedWhere = f[4];
            var openedByMe = false;
            IReadOnlyList<string>? lockedBy = null;
            if (lockedByName.Length > 0)
            {
                if (me.Length > 0 && lockedByName == me) openedByMe = true;
                else lockedBy = [lockedWhere.Length > 0 ? $"{lockedByName}@{lockedWhere}" : lockedByName];
            }
            if (outOfDate || openedByMe || lockedBy is not null)
                map[controlled[i]] = new PlasticRemote(outOfDate, openedByMe, lockedBy);
        }
        return map;
    }

    private static List<string> ControlledFiles(IReadOnlyList<PlasticBase> bases) =>
        bases.Where(b => b.Tracked == true).Select(b => b.Abs).ToList();

    private static string[] FileinfoArgs(List<string> controlled) =>
        ["fileinfo", $"--format={FileinfoFormat}", .. controlled];

    /// <summary>Fill OutOfDate / OpenedByMe / LockedBy from <c>cm fileinfo</c> (sync).</summary>
    private static Dictionary<string, PlasticRemote> FetchPlasticRemote(IReadOnlyList<PlasticBase> bases)
    {
        var controlled = ControlledFiles(bases);
        if (controlled.Count == 0) return new Dictionary<string, PlasticRemote>();
        var fi = Cm(FileinfoArgs(controlled));
        if (fi.ExitCode != 0 || fi.Output.Length == 0) return new Dictionary<string, PlasticRemote>();
        return ApplyFileinfo(controlled, fi.Output, PlasticWhoami());
    }

    /// <summary>Async twin of <see cref="FetchPlasticRemote"/>.</summary>
    private static async Task<Dictionary<string, PlasticRemote>> FetchPlasticRemoteAsync(IReadOnlyList<PlasticBase> bases)
    {
        var controlled = ControlledFiles(bases);
        if (controlled.Count == 0) return new Dictionary<string, PlasticRemote>();
        var fi = await CmAsync(FileinfoArgs(controlled)).ConfigureAwait(false);
        if (fi.ExitCode != 0 || fi.Output.Length == 0) return new Dictionary<string, PlasticRemote>();
        return ApplyFileinfo(controlled, fi.Output, await PlasticWhoamiAsync().ConfigureAwait(false));
    }

    /// <summary>The current Plastic user, for telling our own lock from someone else's.</summary>
    private static string PlasticWhoami()
    {
        var r = Cm(["whoami"]);
        return r.ExitCode == 0 ? r.Output.Trim() : "";
    }

    /// <summary>Async twin of <see cref="PlasticWhoami"/>.</summary>
    private static async Task<string> PlasticWhoamiAsync()
    {
        var r = await CmAsync(["whoami"]).ConfigureAwait(false);
        return r.ExitCode == 0 ? r.Output.Trim() : "";
    }

    /// <summary>
    /// Parse one <c>cm status --machinereadable</c> line into a classification and the
    /// path(s) it concerns. Returns null for header / blank / unrecognised lines.
    /// A move (<c>MV "src" "dst"</c>) carries two quoted paths; both are flagged. A combined
    /// code such as <c>CO+CH</c> (a checked-out file whose contents changed, printed with
    /// <c>--iscochanged</c>) counts as each of its parts.
    /// </summary>
    private static (CmStatusInfo Info, List<string> Paths)? ParseCmStatusLine(string line)
    {
        var trimmed = line.Trim();
        var space = trimmed.IndexOf(' ');
        if (space == -1) return null;
        var codes = trimmed[..space].Split('+');
        var dirty = codes.Any(PlasticDirtyCodes.Contains);
        var untracked = codes.Any(PlasticUntrackedCodes.Contains);
        if (!dirty && !untracked) return null; // STATUS header, blank, or unknown code
        var rest = trimmed[(space + 1)..].Trim();
        var quoted = QuotedPathRegex().Matches(rest).Select(m => m.Groups[1].Value).ToList();
        var paths = quoted.Count > 0 ? quoted : [rest];
        var checkedOut = !untracked && codes.Any(PlasticCheckedOutCodes.Contains);
        return (new CmStatusInfo(!untracked, dirty, checkedOut, codes.Contains("IG")), paths);
    }
}

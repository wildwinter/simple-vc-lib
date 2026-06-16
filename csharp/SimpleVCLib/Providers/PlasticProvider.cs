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

    public VCResult PrepareToWrite(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (!IsTracked(filePath))
            return _fs.PrepareToWrite(filePath);

        var result = Cm(["co", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok();

        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked") || combined.Contains("exclusive"))
            return VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked");
        if (combined.Contains("out of date") || combined.Contains("not latest"))
            return VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date — update before editing");

        return VCResult.Error($"Cannot check out '{filePath}': {result.Error ?? result.Output}");
    }

    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        // cm status --short: exit non-zero = outside workspace, exit 0 with '?' = untracked inside workspace.
        var statusResult = Cm(["status", "--short", filePath]);
        if (statusResult.ExitCode != 0)
            return _fs.FinishedWrite(filePath);

        var lines = statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var isTracked = lines.Length > 0 && !lines[0].TrimStart().StartsWith('?');
        if (isTracked) return VCResult.Ok();

        var result = Cm(["add", filePath]);
        if (result.ExitCode == 0) return VCResult.Ok("File added to Plastic SCM");
        // File is ignored — treat as outside the workspace.
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return _fs.FinishedWrite(filePath);
        return VCResult.Error($"Cannot add '{filePath}' to Plastic SCM: {result.Error ?? result.Output}");
    }

    /// <summary>Async twin of <see cref="PrepareToWrite"/>.</summary>
    public async Task<VCResult> PrepareToWriteAsync(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (!await IsTrackedAsync(filePath).ConfigureAwait(false))
            return await _fs.PrepareToWriteAsync(filePath).ConfigureAwait(false);

        var result = await CmAsync(["co", filePath]).ConfigureAwait(false);
        if (result.ExitCode == 0) return VCResult.Ok();

        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("locked") || combined.Contains("exclusive"))
            return VCResult.Failure(VCStatus.Locked, $"'{filePath}' is locked");
        if (combined.Contains("out of date") || combined.Contains("not latest"))
            return VCResult.Failure(VCStatus.OutOfDate, $"'{filePath}' is out of date — update before editing");

        return VCResult.Error($"Cannot check out '{filePath}': {result.Error ?? result.Output}");
    }

    /// <summary>Async twin of <see cref="FinishedWrite"/>.</summary>
    public async Task<VCResult> FinishedWriteAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        var statusResult = await CmAsync(["status", "--short", filePath]).ConfigureAwait(false);
        if (statusResult.ExitCode != 0)
            return await _fs.FinishedWriteAsync(filePath).ConfigureAwait(false);

        if (TrackedFromShortStatus(statusResult)) return VCResult.Ok();

        var result = await CmAsync(["add", filePath]).ConfigureAwait(false);
        if (result.ExitCode == 0) return VCResult.Ok("File added to Plastic SCM");
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return await _fs.FinishedWriteAsync(filePath).ConfigureAwait(false);
        return VCResult.Error($"Cannot add '{filePath}' to Plastic SCM: {result.Error ?? result.Output}");
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

    private static bool IsTracked(string path) =>
        TrackedFromShortStatus(Cm(["status", "--short", path]));

    private static async Task<bool> IsTrackedAsync(string path) =>
        TrackedFromShortStatus(await CmAsync(["status", "--short", path]).ConfigureAwait(false));

    /// <summary>A <c>cm status --short</c> result is tracked when it lists the file without a '?' prefix.</summary>
    private static bool TrackedFromShortStatus(CommandRunner.Result result)
    {
        if (result.ExitCode != 0) return false;
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 && !lines[0].TrimStart().StartsWith('?');
    }

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
        var byPath = new Dictionary<string, (bool Tracked, bool Dirty)>();
        var byBase = new Dictionary<string, List<(bool Tracked, bool Dirty)>>();
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
            (bool Tracked, bool Dirty)? info = null;
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
    /// A move (<c>MV "src" "dst"</c>) carries two quoted paths; both are flagged.
    /// </summary>
    private static ((bool Tracked, bool Dirty) Info, List<string> Paths)? ParseCmStatusLine(string line)
    {
        var trimmed = line.Trim();
        var space = trimmed.IndexOf(' ');
        if (space == -1) return null;
        var code = trimmed[..space];
        var dirty = PlasticDirtyCodes.Contains(code);
        var untracked = PlasticUntrackedCodes.Contains(code);
        if (!dirty && !untracked) return null; // STATUS header, blank, or unknown code
        var rest = trimmed[(space + 1)..].Trim();
        var quoted = QuotedPathRegex().Matches(rest).Select(m => m.Groups[1].Value).ToList();
        var paths = quoted.Count > 0 ? quoted : [rest];
        return ((!untracked, dirty), paths);
    }
}

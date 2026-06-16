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

    private static bool IsTracked(string path)
    {
        var result = Cm(["status", "--short", path]);
        if (result.ExitCode != 0) return false;
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 && !lines[0].TrimStart().StartsWith('?');
    }

    private static CommandRunner.Result Cm(string[] args) =>
        CommandRunner.Run("cm", args);

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedPathRegex();

    /// <summary>cm status codes for a controlled file with a pending change.</summary>
    private static readonly HashSet<string> PlasticDirtyCodes =
        ["CH", "CO", "AD", "CP", "RP", "MV", "DE", "LD", "LM"];
    /// <summary>codes meaning the path is not under version control.</summary>
    private static readonly HashSet<string> PlasticUntrackedCodes = ["PR", "IG"];

    /// <summary>
    /// Status for a batch of files in ONE <c>cm status --machinereadable --all --ignored</c>
    /// spawn. The machine format lists one item per line as
    /// <c>&lt;2-letter code&gt; &lt;path&gt;</c> (absolute paths, quoted when they contain spaces).
    /// <para>
    /// Flag choice matters: <c>cm status</c> defaults to <c>--controlledchanged</c>, which
    /// omits a content-modified-but-not-checked-out file (CH) and local deletes/moves.
    /// <c>--all</c> adds changed + localdeleted + localmoved + private; <c>--ignored</c> adds
    /// IG. Together they surface every dirty and every untracked item, so a not-listed file
    /// can be read as clean-and-controlled. Lock owners and out-of-date remain TODO.
    /// </para>
    /// <para>
    /// NOTE: status codes and flag semantics are validated against the Unity VCS CLI docs,
    /// not a live workspace - worth one real smoke test on a Plastic install.
    /// </para>
    /// </summary>
    public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths)
    {
        var targets = filePaths.Select(Path.GetFullPath).ToArray();
        var result = Cm(["status", "--machinereadable", "--all", "--ignored", .. targets]);
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
            if (!byPath.TryGetValue(abs, out var info))
            {
                if (byBase.TryGetValue(Path.GetFileName(abs), out var sameName) && sameName.Count == 1)
                    info = sameName[0];
                else if (File.Exists(abs))
                    // Not listed by `cm status --all --ignored` = controlled, no pending change.
                    return new VCFileStatus(abs, "plastic", writable, Tracked: true, Dirty: false);
                else
                    return new VCFileStatus(abs, "plastic", writable);
            }
            return new VCFileStatus(abs, "plastic", writable, Tracked: info.Tracked, Dirty: info.Dirty);
        }).ToList();
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

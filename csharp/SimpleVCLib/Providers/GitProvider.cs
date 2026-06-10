using System.Text.Json;

namespace SimpleVCLib;

/// <summary>
/// Git provider.
/// Git does not use file locking, so PrepareToWrite only ensures the file is
/// writable at the OS level. FinishedWrite stages new files with git add.
/// </summary>
public class GitProvider : IVCProvider
{
    private static readonly FilesystemProvider _fs = new();
    public string Name => "git";

    public VCResult PrepareToWrite(string filePath)
    {
        return _fs.PrepareToWrite(filePath);
    }

    public VCResult FinishedWrite(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");

        if (IsTracked(filePath)) return VCResult.Ok();

        var cwd = Path.GetDirectoryName(filePath)!;

        // File is outside the git repo entirely — no git action needed.
        if (!IsInRepo(cwd))
            return _fs.FinishedWrite(filePath);

        var result = Git(["add", filePath], cwd);
        if (result.ExitCode == 0) return VCResult.Ok("File added to git");
        // File is ignored by .gitignore — treat as outside the repo.
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return _fs.FinishedWrite(filePath);
        return VCResult.Error($"Cannot add '{filePath}' to git: {result.Error ?? result.Output}");
    }

    public VCResult DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return VCResult.Ok();

        if (IsTracked(filePath))
        {
            var result = Git(["rm", "--force", filePath], Path.GetDirectoryName(filePath)!);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot delete '{filePath}' from git: {result.Error ?? result.Output}");
        }

        return _fs.DeleteFile(filePath);
    }

    public VCResult DeleteFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return VCResult.Ok();

        var listResult = Git(["ls-files", folderPath], folderPath);
        if (listResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(listResult.Output))
        {
            var rmResult = Git(["rm", "-r", "--force", folderPath], folderPath);
            if (rmResult.ExitCode != 0)
                return VCResult.Error($"Cannot delete folder '{folderPath}' from git: {rmResult.Error ?? rmResult.Output}");
        }

        // Delete any remaining untracked files git rm left behind.
        if (Directory.Exists(folderPath))
            return _fs.DeleteFolder(folderPath);

        return VCResult.Ok();
    }

    public VCResult RenameFile(string oldPath, string newPath)
    {
        if (!File.Exists(oldPath)) return VCResult.Ok();
        if (IsTracked(oldPath))
        {
            var result = Git(["mv", oldPath, newPath], Path.GetDirectoryName(oldPath)!);
            if (result.ExitCode == 0) return VCResult.Ok();
            return VCResult.Error($"Cannot rename '{oldPath}' in git: {result.Error ?? result.Output}");
        }
        return _fs.RenameFile(oldPath, newPath);
    }

    public VCResult RenameFolder(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath)) return VCResult.Ok();
        var result = Git(["mv", oldPath, newPath], Path.GetDirectoryName(oldPath)!);
        if (result.ExitCode == 0) return VCResult.Ok();
        // Fall back to filesystem rename for untracked folders.
        return _fs.RenameFolder(oldPath, newPath);
    }

    // -------------------------------------------------------------------------

    private static bool IsTracked(string filePath)
    {
        var cwd = Path.GetDirectoryName(filePath) ?? ".";
        var result = Git(["ls-files", "--error-unmatch", filePath], cwd);
        return result.ExitCode == 0;
    }

    private static bool IsInRepo(string dir)
    {
        var result = Git(["rev-parse", "--git-dir"], dir);
        return result.ExitCode == 0;
    }

    private static CommandRunner.Result Git(string[] args, string cwd) =>
        CommandRunner.Run("git", ["-C", cwd, ..args], workingDirectory: cwd);
    /// <summary>
    /// Status for a batch of files: per repository root, ONE
    /// <c>git status --porcelain -z</c> (tracked-ness) plus - when git-lfs is in
    /// play - ONE <c>git lfs locks --verify --json</c> (<c>--verify</c> splits
    /// ours vs theirs). git itself has no locks; lock-based git workflows are
    /// git-lfs locks.
    /// <para>
    /// All matching is done on REPO-RELATIVE paths (via <c>rev-parse
    /// --show-prefix</c>), never by joining absolute paths - so symlinked
    /// ancestors (macOS <c>/var</c> -> <c>/private/var</c>) cannot break the
    /// lookup.
    /// </para>
    /// </summary>
    public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths)
    {
        // Group by repository root so one repo costs two spawns, not 2-per-file.
        // Per directory, one `rev-parse --show-toplevel --show-prefix` yields both
        // the canonical root and the dir's repo-relative prefix.
        var infoByDir = new Dictionary<string, (string? Root, string Prefix)>();
        var groups = new Dictionary<string, List<(string Input, string RelKey)>>();
        foreach (var filePath in filePaths)
        {
            var abs = Path.GetFullPath(filePath);
            var dir = Path.GetDirectoryName(abs)!;
            if (!infoByDir.TryGetValue(dir, out var info))
            {
                var result = Git(["rev-parse", "--show-toplevel", "--show-prefix"], dir);
                if (result.ExitCode == 0)
                {
                    var lines = result.Output.Split('\n');
                    info = (lines[0].Trim(), lines.Length > 1 ? lines[1].Trim() : "");
                }
                else
                {
                    info = (null, "");
                }
                infoByDir[dir] = info;
            }
            var key = info.Root ?? "(none)";
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new List<(string, string)>();
            group.Add((filePath, info.Prefix + Path.GetFileName(abs)));
        }

        var byInput = new Dictionary<string, VCFileStatus>();
        foreach (var (key, files) in groups)
        {
            if (key == "(none)")
            {
                // Not inside a repository - report writability only.
                foreach (var (input, _) in files)
                    byInput[input] = new VCFileStatus(Path.GetFullPath(input), "git", FileStatusHelpers.WritableBit(input));
                continue;
            }
            var root = key;

            // -z output: `XY <path>\0` entries, paths relative to the repo root.
            var st = Git(["status", "--porcelain", "-z", "--", .. files.Select(f => f.RelKey)], root);
            var states = new Dictionary<string, string>();
            if (st.ExitCode == 0 && st.Output.Length > 0)
            {
                foreach (var entry in st.Output.Split('\0'))
                {
                    if (entry.Length < 4) continue;
                    states[entry[3..]] = entry[..2];
                }
            }

            // LFS locks (optional - skipped silently when lfs is absent or errors).
            // Lock paths are repo-relative already.
            var ours = new HashSet<string>();
            var theirs = new Dictionary<string, List<string>>();
            var locks = Git(["lfs", "locks", "--verify", "--json"], root);
            if (locks.ExitCode == 0 && locks.Output.StartsWith('{'))
            {
                try
                {
                    using var parsed = JsonDocument.Parse(locks.Output);
                    if (parsed.RootElement.TryGetProperty("ours", out var oursEl))
                        foreach (var l in oursEl.EnumerateArray())
                            ours.Add(l.GetProperty("path").GetString()!);
                    if (parsed.RootElement.TryGetProperty("theirs", out var theirsEl))
                        foreach (var l in theirsEl.EnumerateArray())
                        {
                            var rel = l.GetProperty("path").GetString()!;
                            var owner = l.TryGetProperty("owner", out var o) && o.TryGetProperty("name", out var n)
                                ? n.GetString() ?? "unknown" : "unknown";
                            if (!theirs.TryGetValue(rel, out var list)) theirs[rel] = list = new List<string>();
                            list.Add(owner);
                        }
                }
                catch (JsonException)
                {
                    // Unparseable lock output - status is still useful without it.
                }
            }

            foreach (var (input, relKey) in files)
            {
                // Absent from porcelain output = clean & tracked; '??' = untracked.
                var tracked = !states.TryGetValue(relKey, out var xy) || xy != "??";
                byInput[input] = new VCFileStatus(
                    Path.GetFullPath(input), "git", FileStatusHelpers.WritableBit(input),
                    Tracked: tracked,
                    OpenedByMe: ours.Contains(relKey) ? true : null,
                    LockedBy: theirs.TryGetValue(relKey, out var owners) ? owners : null);
            }
        }

        return filePaths.Select(p => byInput[p]).ToList();
    }
}

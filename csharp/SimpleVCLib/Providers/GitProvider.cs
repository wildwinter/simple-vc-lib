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

    // git PrepareToWrite is pure local fs (no spawn), so the async twin just wraps it.
    public Task<VCResult> PrepareToWriteAsync(string filePath) => Task.FromResult(PrepareToWrite(filePath));

    /// <summary>Async twin of <see cref="FinishedWrite"/>.</summary>
    public async Task<VCResult> FinishedWriteAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return VCResult.Error($"'{filePath}' does not exist after write");
        if (await IsTrackedAsync(filePath).ConfigureAwait(false)) return VCResult.Ok();

        var cwd = Path.GetDirectoryName(filePath)!;
        if (!await IsInRepoAsync(cwd).ConfigureAwait(false))
            return await _fs.FinishedWriteAsync(filePath).ConfigureAwait(false);

        var result = await GitAsync(["add", filePath], cwd).ConfigureAwait(false);
        if (result.ExitCode == 0) return VCResult.Ok("File added to git");
        var combined = $"{result.Output} {result.Error}".ToLowerInvariant();
        if (combined.Contains("ignored")) return await _fs.FinishedWriteAsync(filePath).ConfigureAwait(false);
        return VCResult.Error($"Cannot add '{filePath}' to git: {result.Error ?? result.Output}");
    }

    // Delete/rename reuse the tested sync logic on a thread-pool thread (the subprocess
    // wait parks a pooled thread; no duplication of the orchestration).
    public Task<VCResult> DeleteFileAsync(string filePath) => Task.Run(() => DeleteFile(filePath));
    public Task<VCResult> DeleteFolderAsync(string folderPath) => Task.Run(() => DeleteFolder(folderPath));
    public Task<VCResult> RenameFileAsync(string oldPath, string newPath) => Task.Run(() => RenameFile(oldPath, newPath));
    public Task<VCResult> RenameFolderAsync(string oldPath, string newPath) => Task.Run(() => RenameFolder(oldPath, newPath));

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

    private static async Task<bool> IsTrackedAsync(string filePath)
    {
        var cwd = Path.GetDirectoryName(filePath) ?? ".";
        var result = await GitAsync(["ls-files", "--error-unmatch", filePath], cwd).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private static async Task<bool> IsInRepoAsync(string dir) =>
        (await GitAsync(["rev-parse", "--git-dir"], dir).ConfigureAwait(false)).ExitCode == 0;

    private static CommandRunner.Result Git(string[] args, string cwd, bool trimOutput = true) =>
        CommandRunner.Run("git", ["-C", cwd, ..args], workingDirectory: cwd, trimOutput: trimOutput);

    private static Task<CommandRunner.Result> GitAsync(string[] args, string cwd, bool trimOutput = true) =>
        CommandRunner.RunAsync("git", ["-C", cwd, ..args], workingDirectory: cwd, trimOutput: trimOutput);
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
    public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false)
    {
        // remote is ignored: git's one status + one lfs-locks call already carry
        // everything git can report (git itself has no server-side lock/staleness).
        _ = remote;
        var infoByDir = new Dictionary<string, (string? Root, string Prefix)>();
        var groups = new Dictionary<string, List<(string Input, string RelKey)>>();
        foreach (var filePath in filePaths)
        {
            var abs = Path.GetFullPath(filePath);
            var dir = Path.GetDirectoryName(abs)!;
            if (!infoByDir.TryGetValue(dir, out var info))
            {
                info = GitDirInfo(Git(["rev-parse", "--show-toplevel", "--show-prefix"], dir));
                infoByDir[dir] = info;
            }
            AddToGitGroup(groups, info, filePath, abs);
        }

        var byInput = new Dictionary<string, VCFileStatus>();
        foreach (var (key, files) in groups)
        {
            if (key == "(none)") { ReportGitWritableOnly(files, byInput); continue; }
            var st = Git(["status", "--porcelain", "-z", "--", .. files.Select(f => f.RelKey)], key, trimOutput: false);
            var locks = Git(["lfs", "locks", "--verify", "--json"], key);
            AssembleGitStatuses(files, GitStates(st), GitLocks(locks), byInput);
        }
        return filePaths.Select(p => byInput[p]).ToList();
    }

    /// <summary>
    /// Async twin of <see cref="Status"/>. Within each repo the porcelain status and the
    /// git-lfs lock list are independent, so they run concurrently.
    /// </summary>
    public async Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false)
    {
        _ = remote;
        var infoByDir = new Dictionary<string, (string? Root, string Prefix)>();
        var groups = new Dictionary<string, List<(string Input, string RelKey)>>();
        foreach (var filePath in filePaths)
        {
            var abs = Path.GetFullPath(filePath);
            var dir = Path.GetDirectoryName(abs)!;
            if (!infoByDir.TryGetValue(dir, out var info))
            {
                info = GitDirInfo(await GitAsync(["rev-parse", "--show-toplevel", "--show-prefix"], dir).ConfigureAwait(false));
                infoByDir[dir] = info;
            }
            AddToGitGroup(groups, info, filePath, abs);
        }

        var byInput = new Dictionary<string, VCFileStatus>();
        foreach (var (key, files) in groups)
        {
            if (key == "(none)") { ReportGitWritableOnly(files, byInput); continue; }
            var stTask = GitAsync(["status", "--porcelain", "-z", "--", .. files.Select(f => f.RelKey)], key, trimOutput: false);
            var locksTask = GitAsync(["lfs", "locks", "--verify", "--json"], key);
            await Task.WhenAll(stTask, locksTask).ConfigureAwait(false);
            AssembleGitStatuses(files, GitStates(stTask.Result), GitLocks(locksTask.Result), byInput);
        }
        return filePaths.Select(p => byInput[p]).ToList();
    }

    /// <summary>Parse one `rev-parse --show-toplevel --show-prefix` result into (root, prefix).</summary>
    private static (string? Root, string Prefix) GitDirInfo(CommandRunner.Result result)
    {
        if (result.ExitCode != 0) return (null, "");
        var lines = result.Output.Split('\n');
        return (lines[0].Trim(), lines.Length > 1 ? lines[1].Trim() : "");
    }

    /// <summary>Bucket a file under its repo root (or "(none)" when outside any repo).</summary>
    private static void AddToGitGroup(Dictionary<string, List<(string Input, string RelKey)>> groups,
        (string? Root, string Prefix) info, string filePath, string abs)
    {
        var key = info.Root ?? "(none)";
        if (!groups.TryGetValue(key, out var group)) groups[key] = group = new List<(string, string)>();
        group.Add((filePath, info.Prefix + Path.GetFileName(abs)));
    }

    /// <summary>Files outside any repo: report the writable bit only.</summary>
    private static void ReportGitWritableOnly(List<(string Input, string RelKey)> files, Dictionary<string, VCFileStatus> byInput)
    {
        foreach (var (input, _) in files)
            byInput[input] = new VCFileStatus(Path.GetFullPath(input), "git", FileStatusHelpers.WritableBit(input));
    }

    /// <summary>`git status --porcelain -z` -> map of repoRelPath -> "XY".</summary>
    private static Dictionary<string, string> GitStates(CommandRunner.Result st)
    {
        var states = new Dictionary<string, string>();
        if (st.ExitCode == 0 && st.Output.Length > 0)
            foreach (var entry in st.Output.Split('\0'))
            {
                if (entry.Length < 4) continue;
                states[entry[3..]] = entry[..2];
            }
        return states;
    }

    /// <summary>`git lfs locks --verify --json` -> (ours, theirs).</summary>
    private static (HashSet<string> Ours, Dictionary<string, List<string>> Theirs) GitLocks(CommandRunner.Result locks)
    {
        var ours = new HashSet<string>();
        var theirs = new Dictionary<string, List<string>>();
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
        return (ours, theirs);
    }

    /// <summary>Build per-file statuses for one repo's files from its porcelain + lock data.</summary>
    private static void AssembleGitStatuses(List<(string Input, string RelKey)> files,
        Dictionary<string, string> states,
        (HashSet<string> Ours, Dictionary<string, List<string>> Theirs) locks,
        Dictionary<string, VCFileStatus> byInput)
    {
        foreach (var (input, relKey) in files)
        {
            // Absent from porcelain = clean & tracked; '??' = untracked; any other code
            // (M/A/D/R/MM/...) = a tracked file with pending changes.
            var present = states.TryGetValue(relKey, out var xy);
            byInput[input] = new VCFileStatus(
                Path.GetFullPath(input), "git", FileStatusHelpers.WritableBit(input),
                Tracked: !present || xy != "??",
                OpenedByMe: locks.Ours.Contains(relKey) ? true : null,
                LockedBy: locks.Theirs.TryGetValue(relKey, out var owners) ? owners : null,
                Dirty: present && xy != "??");
        }
    }
}

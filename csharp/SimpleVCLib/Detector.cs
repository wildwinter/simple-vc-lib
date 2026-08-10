using System.Text.Json;

namespace SimpleVCLib;

/// <summary>
/// Auto-detects the active version control system for a given path.
/// </summary>
public static class Detector
{
    private static readonly string[] ValidSystems =
        ["git", "perforce", "plastic", "svn", "filesystem"];

    // Maps a VCS root directory to its provider instance.
    // Populated on first detection; avoids repeated directory walks for the same repo.
    private static readonly Dictionary<string, IVCProvider> _rootCache = new();

    /// <summary>
    /// Directories whose answer needed the Perforce probe: the ones with no
    /// marker directory anywhere above them.
    /// <para>
    /// The root cache above only ever learns the POSITIVE answers, so a path
    /// outside any working copy was re-walked and re-probed on every call, and
    /// the probe is a `p4 info` subprocess. A tool that autosaves paid one per
    /// write. Nothing about that answer changes between two keystrokes.
    /// </para>
    /// <para>
    /// Keyed by the EXACT starting directory and never consulted for ancestors,
    /// unlike the root cache: "no marker above this directory" says nothing
    /// about a repository nested somewhere below it.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, IVCProvider> _probedCache = new();

    /// <summary>Clear the detection cache. Called when the provider override is cleared.</summary>
    public static void ClearCache()
    {
        _rootCache.Clear();
        _probedCache.Clear();
    }

    /// <summary>
    /// Detect and return the appropriate provider for <paramref name="path"/>.
    /// <para>
    /// Detection order:
    /// <list type="number">
    ///   <item>SIMPLE_VC environment variable</item>
    ///   <item>.vcconfig JSON file, walking up from the file's directory</item>
    ///   <item>VC marker directories (.git, .plastic, .svn)</item>
    ///   <item>Perforce (via `p4 info`)</item>
    ///   <item>Filesystem fallback</item>
    /// </list>
    /// </para>
    /// </summary>
    public static IVCProvider Detect(string path)
    {
        var startDir = GetDirectory(path);

        var envSystem = Environment.GetEnvironmentVariable("SIMPLE_VC")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(envSystem))
        {
            var p = CreateProvider(envSystem);
            if (p is not null) return p;
        }

        var dir = startDir;
        while (dir is not null)
        {
            var configPath = Path.Combine(dir, ".vcconfig");
            if (File.Exists(configPath))
            {
                var p = TryLoadConfig(configPath);
                if (p is not null) return p;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        if (startDir is not null && _probedCache.TryGetValue(startDir, out var probed)) return probed;

        // Check whether any ancestor is a known VCS root before doing any I/O.
        dir = startDir;
        while (dir is not null)
        {
            if (_rootCache.TryGetValue(dir, out var cached)) return cached;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        // Walk up looking for VC marker directories/files.
        dir = startDir;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) ||
                File.Exists(Path.Combine(dir, ".git")))          // git worktrees use a file
            {
                var p = new GitProvider();
                _rootCache[dir] = p;
                return p;
            }

            if (Directory.Exists(Path.Combine(dir, ".plastic")))
            {
                var p = new PlasticProvider();
                _rootCache[dir] = p;
                return p;
            }

            if (Directory.Exists(Path.Combine(dir, ".svn")))
            {
                var p = new SvnProvider();
                _rootCache[dir] = p;
                return p;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        // Perforce has no marker directory — detect via CLI.
        var p4 = CommandRunner.Run("p4", ["info"], timeoutMs: 3000);
        IVCProvider answer = (p4.ExitCode == 0 && p4.Output.Contains("Client name:"))
            ? new PerforceProvider()
            : new FilesystemProvider();
        if (startDir is not null) _probedCache[startDir] = answer;
        return answer;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string GetDirectory(string path)
    {
        if (Directory.Exists(path)) return path;
        if (File.Exists(path))      return Path.GetDirectoryName(path) ?? path;
        // Path doesn't exist yet — treat as a file and use its parent directory.
        return Path.GetDirectoryName(path) ?? path;
    }

    private static IVCProvider? TryLoadConfig(string configPath)
    {
        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("system", out var systemElement))
            {
                var system = systemElement.GetString()?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(system))
                    return CreateProvider(system);
            }
        }
        catch { /* Malformed config — ignore */ }
        return null;
    }

    private static IVCProvider? CreateProvider(string system) => system switch
    {
        "git"        => new GitProvider(),
        "perforce"   => new PerforceProvider(),
        "plastic"    => new PlasticProvider(),
        "svn"        => new SvnProvider(),
        "filesystem" => new FilesystemProvider(),
        _            => null,
    };
}

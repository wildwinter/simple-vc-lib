using System.Diagnostics;
using SimpleVCLib;
using Xunit;

namespace SimpleVCLib.Tests;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

internal static class TestHelpers
{
    /// <summary>Create a temporary directory and return its path.</summary>
    public static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Returns true if svn and svnadmin are available on this machine.</summary>
    public static bool SvnAvailable()
    {
        static int ExitCode(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                return p.ExitCode;
            }
            catch { return -1; }
        }
        return ExitCode("svn", "--version --quiet") == 0 &&
               ExitCode("svnadmin", "--version --quiet") == 0;
    }

    /// <summary>
    /// Create a temporary SVN repository and a working copy checked out from it.
    /// Returns the working-copy path.
    /// </summary>
    public static string InitSvnRepo()
    {
        static (int exitCode, string output) Run(string exe, string args, string? cwd = null)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = cwd ?? Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }

        var repoDir = MakeTempDir();
        var wcDir = MakeTempDir();

        Run("svnadmin", $"create \"{repoDir}\"");
        Run("svn", $"checkout \"file://{repoDir}\" \"{wcDir}\"");

        // Commit an initial file so the repo has a revision.
        File.WriteAllText(Path.Combine(wcDir, "initial.txt"), "initial");
        Run("svn", "add initial.txt", wcDir);
        Run("svn", "commit -m initial --username test --no-auth-cache", wcDir);

        return wcDir;
    }

    /// <summary>Initialise a bare git repo in <paramref name="dir"/>.</summary>
    public static void InitGitRepo(string dir)
    {
        static void Run(string args, string cwd)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
        }
        Run("init", dir);
        Run("config user.email test@example.com", dir);
        Run("config user.name \"Test User\"", dir);
        // Create an initial commit so HEAD exists.
        File.WriteAllText(Path.Combine(dir, "README.md"), "test");
        Run("add README.md", dir);
        Run("commit -m init", dir);
    }

    /// <summary>Make a file read-only using the .NET API (cross-platform).</summary>
    public static void MakeReadOnly(string filePath) =>
        new FileInfo(filePath).IsReadOnly = true;
}

// ---------------------------------------------------------------------------
// FilesystemProvider tests
// ---------------------------------------------------------------------------

public class FilesystemProviderTests : IDisposable
{
    private readonly string _tempDir = TestHelpers.MakeTempDir();

    public FilesystemProviderTests() => VCLib.SetProvider(new FilesystemProvider());

    [Fact]
    public void PrepareToWrite_NewFile_ReturnsOk()
    {
        var result = VCLib.PrepareToWrite(Path.Combine(_tempDir, "newfile.txt"));
        Assert.True(result.Success);
        Assert.Equal(VCStatus.Ok, result.Status);
    }

    [Fact]
    public void PrepareToWrite_ExistingWritableFile_ReturnsOk()
    {
        var filePath = Path.Combine(_tempDir, "writable.txt");
        File.WriteAllText(filePath, "content");
        var result = VCLib.PrepareToWrite(filePath);
        Assert.True(result.Success);
    }

    [Fact]
    public void PrepareToWrite_ReadOnlyFile_MakesItWritable()
    {
        var filePath = Path.Combine(_tempDir, "readonly.txt");
        File.WriteAllText(filePath, "content");
        TestHelpers.MakeReadOnly(filePath);

        var result = VCLib.PrepareToWrite(filePath);
        Assert.True(result.Success, result.Message);
        Assert.False(new FileInfo(filePath).IsReadOnly);
    }

    [Fact]
    public void FinishedWrite_ExistingFile_ReturnsOk()
    {
        var filePath = Path.Combine(_tempDir, "written.txt");
        File.WriteAllText(filePath, "content");
        var result = VCLib.FinishedWrite(filePath);
        Assert.True(result.Success);
    }

    [Fact]
    public void FinishedWrite_MissingFile_ReturnsError()
    {
        var result = VCLib.FinishedWrite(Path.Combine(_tempDir, "ghost.txt"));
        Assert.False(result.Success);
        Assert.Equal(VCStatus.Error, result.Status);
    }

    [Fact]
    public void DeleteFile_ExistingFile_DeletesIt()
    {
        var filePath = Path.Combine(_tempDir, "todelete.txt");
        File.WriteAllText(filePath, "bye");
        var result = VCLib.DeleteFile(filePath);
        Assert.True(result.Success);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void DeleteFile_MissingFile_ReturnsOk()
    {
        var result = VCLib.DeleteFile(Path.Combine(_tempDir, "ghost.txt"));
        Assert.True(result.Success);
    }

    [Fact]
    public void DeleteFolder_ExistingFolder_DeletesIt()
    {
        var folderPath = Path.Combine(_tempDir, "myfolder");
        Directory.CreateDirectory(folderPath);
        File.WriteAllText(Path.Combine(folderPath, "a.txt"), "a");

        var result = VCLib.DeleteFolder(folderPath);
        Assert.True(result.Success);
        Assert.False(Directory.Exists(folderPath));
    }

    [Fact]
    public void DeleteFolder_MissingFolder_ReturnsOk()
    {
        var result = VCLib.DeleteFolder(Path.Combine(_tempDir, "nonexistent"));
        Assert.True(result.Success);
    }

    void IDisposable.Dispose()
    {
        VCLib.ClearProvider();
        foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            new FileInfo(f).IsReadOnly = false;
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}

// ---------------------------------------------------------------------------
// GitProvider tests (uses a temporary git repository)
// ---------------------------------------------------------------------------

public class GitProviderTests : IDisposable
{
    private readonly string _repoDir = TestHelpers.MakeTempDir();

    public GitProviderTests()
    {
        TestHelpers.InitGitRepo(_repoDir);
        VCLib.SetProvider(new GitProvider());
    }

    public void Dispose()
    {
        VCLib.ClearProvider();
        // Reset read-only flags before cleanup.
        foreach (var f in Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories))
            try { new FileInfo(f).IsReadOnly = false; } catch { }
        if (Directory.Exists(_repoDir))
            Directory.Delete(_repoDir, recursive: true);
    }

    [Fact]
    public void PrepareToWrite_NewFile_ReturnsOk()
    {
        var result = VCLib.PrepareToWrite(Path.Combine(_repoDir, "newfile.txt"));
        Assert.True(result.Success);
    }

    [Fact]
    public void PrepareToWrite_ReadOnlyFile_MakesItWritable()
    {
        var filePath = Path.Combine(_repoDir, "readonly.txt");
        File.WriteAllText(filePath, "content");
        TestHelpers.MakeReadOnly(filePath);

        var result = VCLib.PrepareToWrite(filePath);
        Assert.True(result.Success, result.Message);
        Assert.False(new FileInfo(filePath).IsReadOnly);
    }

    [Fact]
    public void FinishedWrite_NewFile_AddsToGit()
    {
        var filePath = Path.Combine(_repoDir, "staged.txt");
        File.WriteAllText(filePath, "hello");

        var result = VCLib.FinishedWrite(filePath);
        Assert.True(result.Success, result.Message);

        // Verify the file is staged.
        var psi = new ProcessStartInfo("git", $"status --short \"{filePath}\"")
        {
            WorkingDirectory = _repoDir,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.StartsWith("A", output);
    }

    [Fact]
    public void FinishedWrite_AlreadyTrackedFile_ReturnsOk()
    {
        // README.md was committed in InitGitRepo.
        var result = VCLib.FinishedWrite(Path.Combine(_repoDir, "README.md"));
        Assert.True(result.Success);
    }

    [Fact]
    public void DeleteFile_UntrackedFile_DeletesIt()
    {
        var filePath = Path.Combine(_repoDir, "untracked.txt");
        File.WriteAllText(filePath, "temp");
        var result = VCLib.DeleteFile(filePath);
        Assert.True(result.Success);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void DeleteFolder_MixedFolder_DeletesEverything()
    {
        var folderPath = Path.Combine(_repoDir, "mixedfolder");
        Directory.CreateDirectory(folderPath);

        var tracked = Path.Combine(folderPath, "tracked.txt");
        var untracked = Path.Combine(folderPath, "untracked.txt");
        File.WriteAllText(tracked, "tracked");
        File.WriteAllText(untracked, "untracked");

        // Stage the tracked file.
        var psi = new ProcessStartInfo("git", $"add \"{tracked}\"")
        {
            WorkingDirectory = _repoDir, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();

        var result = VCLib.DeleteFolder(folderPath);
        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(folderPath));
    }
}

// ---------------------------------------------------------------------------
// SvnProvider tests (uses a temporary SVN repository via file:// URL)
// Skipped automatically when svn/svnadmin are not installed.
// ---------------------------------------------------------------------------

public class SvnProviderTests : IDisposable
{
    private readonly bool _available;
    private readonly string _wcDir;

    public SvnProviderTests()
    {
        _available = TestHelpers.SvnAvailable();
        _wcDir = _available ? TestHelpers.InitSvnRepo() : string.Empty;
        if (_available)
            VCLib.SetProvider(new SvnProvider());
    }

    public void Dispose()
    {
        VCLib.ClearProvider();
        if (_wcDir != string.Empty && Directory.Exists(_wcDir))
            Directory.Delete(_wcDir, recursive: true);
    }

    private static (int ExitCode, string Output) Svn(string args, string cwd)
    {
        var psi = new ProcessStartInfo("svn", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [Fact]
    public void FinishedWrite_NewFile_AddsToSvn()
    {
        if (!_available) return;

        var filePath = Path.Combine(_wcDir, "new.txt");
        File.WriteAllText(filePath, "hello");

        var result = VCLib.FinishedWrite(filePath);
        Assert.True(result.Success, result.Message);

        var (_, output) = Svn($"status \"{filePath}\"", _wcDir);
        Assert.StartsWith("A", output);
    }

    [Fact]
    public void FinishedWrite_AlreadyTrackedFile_ReturnsOk()
    {
        if (!_available) return;

        // initial.txt was committed in InitSvnRepo.
        var result = VCLib.FinishedWrite(Path.Combine(_wcDir, "initial.txt"));
        Assert.True(result.Success);
    }

    [Fact]
    public void DeleteFile_CommittedFile_DeletesIt()
    {
        if (!_available) return;

        var filePath = Path.Combine(_wcDir, "todelete.txt");
        File.WriteAllText(filePath, "bye");
        Svn($"add \"{filePath}\"", _wcDir);
        Svn("commit -m \"add todelete\" --username test --no-auth-cache", _wcDir);

        var result = VCLib.DeleteFile(filePath);
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(filePath));

        var (_, output) = Svn($"status \"{filePath}\"", _wcDir);
        Assert.StartsWith("D", output);
    }

    [Fact]
    public void DeleteFolder_CommittedFolder_DeletesIt()
    {
        if (!_available) return;

        var dirPath = Path.Combine(_wcDir, "mydir");
        Directory.CreateDirectory(dirPath);
        File.WriteAllText(Path.Combine(dirPath, "f.txt"), "x");
        Svn($"add \"{dirPath}\"", _wcDir);
        Svn("commit -m \"add mydir\" --username test --no-auth-cache", _wcDir);

        var result = VCLib.DeleteFolder(dirPath);
        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(dirPath));
    }

    [Fact]
    public void AutoDetect_SvnWorkingCopy_UsesSvnProvider()
    {
        if (!_available) return;

        VCLib.ClearProvider();  // Remove explicit provider — rely on auto-detection.
        var filePath = Path.Combine(_wcDir, "detected.txt");
        File.WriteAllText(filePath, "x");

        var result = VCLib.FinishedWrite(filePath);
        Assert.True(result.Success, result.Message);

        var (_, output) = Svn($"status \"{filePath}\"", _wcDir);
        Assert.StartsWith("A", output);

        VCLib.SetProvider(new SvnProvider());  // Restore for remaining tests.
    }
}

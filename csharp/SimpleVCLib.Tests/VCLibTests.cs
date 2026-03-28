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

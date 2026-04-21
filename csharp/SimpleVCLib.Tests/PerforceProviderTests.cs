using System.Diagnostics;
using SimpleVCLib;
using Xunit;

namespace SimpleVCLib.Tests;

/// <summary>
/// Perforce integration tests.
///
/// These tests require a configured Perforce workspace on the local machine.
/// They are skipped automatically when p4 is not available or no workspace
/// is configured. Run the full test suite normally:
///
///   cd csharp
///   dotnet test
///
/// Set P4_TEST_DIR to point at a mapped workspace directory:
///   P4_TEST_DIR=C:\path\to\workspace dotnet test
///
/// If P4_TEST_DIR is not set, falls back to the workspace root from p4 info.
/// The suite is skipped automatically if neither is available.
///
/// Requirements:
///   - p4 on PATH
///   - P4PORT / P4USER / P4CLIENT configured (env vars, p4 config, or p4 tickets)
///   - P4_TEST_DIR (or the auto-detected workspace root) must be a mapped depot path
///
/// The tests create a temporary subdirectory inside the workspace root, submit
/// files to the depot to simulate a real tracked state, exercise the API, then
/// clean up by reverting and deleting everything they submitted.
/// </summary>
public class PerforceProviderTests : IDisposable
{
    private readonly bool _available;
    private readonly string _testDir;

    public PerforceProviderTests()
    {
        _available = P4Available();
        if (_available)
        {
            var root = Environment.GetEnvironmentVariable("P4_TEST_DIR") ?? WorkspaceRoot();
            _available = root is not null;
            _testDir = root is not null
                ? Path.Combine(root, "_simple_vc_lib_test")
                : string.Empty;
        }
        else
        {
            _testDir = string.Empty;
        }

        if (_available)
        {
            Directory.CreateDirectory(_testDir);
            VCLib.SetProvider(new PerforceProvider());
        }
    }

    public void Dispose()
    {
        VCLib.ClearProvider();
        if (!_available || string.IsNullOrEmpty(_testDir)) return;

        // Revert all open files in the test directory.
        P4($"revert \"{_testDir}\\...\"");
        // Mark all submitted depot files under testDir for deletion.
        var del = P4Checked($"delete \"{_testDir}\\...\"");
        // Submit the deletions only if p4 delete opened something.
        if (del.ExitCode == 0)
            P4("submit -d \"cleanup: remove simple-vc-lib test dir\"");
        // Remove the local directory regardless.
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void PrepareToWrite_SubmittedFile_OpensForEdit()
    {
        if (!_available) return;

        var filePath = Path.Combine(_testDir, "ptw-test.txt");
        SubmitFile(filePath);

        var result = VCLib.PrepareToWrite(filePath);
        Assert.True(result.Success, result.Message);

        // Verify the file is open for edit in Perforce.
        var fstat = P4Checked($"fstat \"{filePath}\"");
        Assert.Contains("action", fstat.Output);
    }

    [Fact]
    public void PrepareToWrite_NewFile_ReturnsOk()
    {
        if (!_available) return;

        var result = VCLib.PrepareToWrite(Path.Combine(_testDir, "nonexistent.txt"));
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void FinishedWrite_NewFile_AddsToPerforce()
    {
        if (!_available) return;

        var filePath = Path.Combine(_testDir, "fw-new.txt");
        File.WriteAllText(filePath, "new content");

        var result = VCLib.FinishedWrite(filePath);
        Assert.True(result.Success, result.Message);

        // Verify the file is open for add in Perforce.
        var fstat = P4Checked($"fstat \"{filePath}\"");
        Assert.Contains("add", fstat.Output);
    }

    [Fact]
    public void FinishedWrite_FileOutsideWorkspace_ReturnsOkWithoutP4Add()
    {
        if (!_available) return;

        var outsideDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outsideDir);
        try
        {
            var filePath = Path.Combine(outsideDir, "outside.txt");
            File.WriteAllText(filePath, "not in workspace");

            var result = VCLib.FinishedWrite(filePath);
            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void WriteTextFile_SubmittedFile_ChecksOutAndWrites()
    {
        if (!_available) return;

        var filePath = Path.Combine(_testDir, "write-test.txt");
        SubmitFile(filePath, "original");

        var result = VCLib.WriteTextFile(filePath, "updated content");
        Assert.True(result.Success, result.Message);
        Assert.Equal("updated content", File.ReadAllText(filePath));
    }

    [Fact]
    public void DeleteFile_SubmittedFile_OpensForDelete()
    {
        if (!_available) return;

        var filePath = Path.Combine(_testDir, "del-test.txt");
        SubmitFile(filePath);

        var result = VCLib.DeleteFile(filePath);
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(filePath));

        var fstat = P4Checked($"fstat \"{filePath}\"");
        Assert.Contains("delete", fstat.Output);
    }

    [Fact]
    public void DeleteFile_UntrackedFile_DeletesFromDiskOnly()
    {
        if (!_available) return;

        var filePath = Path.Combine(_testDir, "untracked-del.txt");
        File.WriteAllText(filePath, "local only");

        var result = VCLib.DeleteFile(filePath);
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void RenameFile_SubmittedFile_UsesP4Move()
    {
        if (!_available) return;

        var oldPath = Path.Combine(_testDir, "ren-src.txt");
        var newPath = Path.Combine(_testDir, "ren-dst.txt");
        SubmitFile(oldPath, "content");

        var result = VCLib.RenameFile(oldPath, newPath);
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));

        // Destination should be open for add as part of the p4 move.
        var fstat = P4Checked($"fstat \"{newPath}\"");
        Assert.Contains("add", fstat.Output);
    }

    [Fact]
    public void RenameFolder_SubmittedFolder_UsesP4Move()
    {
        if (!_available) return;

        var oldDir = Path.Combine(_testDir, "ren-folder-src");
        var newDir = Path.Combine(_testDir, "ren-folder-dst");
        Directory.CreateDirectory(oldDir);
        SubmitFile(Path.Combine(oldDir, "a.txt"), "a");
        SubmitFile(Path.Combine(oldDir, "b.txt"), "b");

        var result = VCLib.RenameFolder(oldDir, newDir);
        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(oldDir));
        Assert.True(File.Exists(Path.Combine(newDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(newDir, "b.txt")));
    }

    [Fact]
    public void FinishedWrite_PendingDeleteFile_ReopensForEdit()
    {
        if (!_available) return;

        // Simulate the TTS scenario: a file is submitted, then marked for delete
        // (e.g. by a cleanup pass), then regenerated on disk and FinishedWrite is called.
        var filePath = Path.Combine(_testDir, "fw-pending-delete.txt");
        SubmitFile(filePath, "original content");

        // Mark for delete — this removes the local file and opens a pending delete.
        P4($"delete \"{filePath}\"");
        Assert.False(File.Exists(filePath));

        // Re-create the file on disk (as WriteBinaryFile with forceWrite:true would do).
        File.WriteAllText(filePath, "regenerated content");

        // FinishedWrite must cancel the pending delete and reopen for edit.
        var result = VCLib.FinishedWrite(filePath);
        Assert.True(result.Success, result.Message);

        // File must be present on disk with the new content.
        Assert.True(File.Exists(filePath));
        Assert.Equal("regenerated content", File.ReadAllText(filePath));

        // P4 state must be "edit", not "delete".
        var fstat = P4Checked($"fstat \"{filePath}\"");
        Assert.Contains("action edit", fstat.Output);
        Assert.DoesNotContain("action delete", fstat.Output);
    }

    [Fact]
    public void DeleteFolder_SubmittedFolder_RemovesFromDepot()
    {
        if (!_available) return;

        var dirPath = Path.Combine(_testDir, "del-folder");
        Directory.CreateDirectory(dirPath);
        SubmitFile(Path.Combine(dirPath, "x.txt"), "x");
        SubmitFile(Path.Combine(dirPath, "y.txt"), "y");

        var result = VCLib.DeleteFolder(dirPath);
        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(dirPath));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool P4Available()
    {
        try
        {
            var psi = new ProcessStartInfo("p4", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? WorkspaceRoot()
    {
        var psi = new ProcessStartInfo("p4", "info")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var match = System.Text.RegularExpressions.Regex.Match(output, @"Client root:\s+(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private record P4Result(int ExitCode, string Output, string Error);

    private static P4Result P4Checked(string args)
    {
        var psi = new ProcessStartInfo("p4", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new P4Result(p.ExitCode, output, error);
    }

    private static void P4(string args)
    {
        var psi = new ProcessStartInfo("p4", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static void SubmitFile(string filePath, string content = "test content")
    {
        File.WriteAllText(filePath, content);
        P4($"add \"{filePath}\"");
        P4($"submit -d \"add {filePath} (simple-vc-lib test)\"");
        // After submit the file is read-only on disk — standard Perforce behaviour.
    }
}

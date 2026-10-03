using System.Diagnostics;
using SimpleVCLib;
using Xunit;

namespace SimpleVCLib.Tests;

// ---------------------------------------------------------------------------
// All-or-nothing batch checkout: PrepareToWriteFiles and
// WriteTextFiles(allOrNothing). Mirrors js/test/batch.test.js.
// ---------------------------------------------------------------------------

public class BatchCheckoutTests : IDisposable
{
    public void Dispose()
    {
        VCLib.ClearProvider();
        VCLib.ClearCommandRunner();
    }

    /// <summary>Create each named file in a fresh temp dir and return their paths.</summary>
    private static string[] Seed(string[] names, string content = "old")
    {
        var dir = TestHelpers.MakeTempDir();
        return names.Select(name =>
        {
            var filePath = Path.Combine(dir, name);
            File.WriteAllText(filePath, content);
            return filePath;
        }).ToArray();
    }

    private static bool IsReadOnly(string filePath) => new FileInfo(filePath).IsReadOnly;

    private static void MakeReadOnly(string filePath) => new FileInfo(filePath).IsReadOnly = true;

    /// <summary>Record every command, answering from <paramref name="answer"/> (default: exit 0, no output).</summary>
    private static List<string> RecordingRunner(Func<string, string[], CommandResult?>? answer = null)
    {
        var calls = new List<string>();
        VCLib.SetCommandRunner((command, args) =>
        {
            calls.Add($"{command} {string.Join(' ', args)}");
            return answer?.Invoke(command, args) ?? new CommandResult(0, "", "");
        });
        return calls;
    }

    /// <summary>
    /// A provider that records every call by file name. <c>statuses</c> replaces a file's
    /// status, <c>failPrepare</c> / <c>failUndo</c> / <c>throwUndo</c> name the files whose
    /// prepare or undo goes wrong.
    /// </summary>
    private class FakeProvider(
        Dictionary<string, Func<VCFileStatus, VCFileStatus>>? statuses = null,
        string[]? failPrepare = null) : IVCProvider
    {
        public List<string> Calls { get; } = [];
        public string Name => "git";

        public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false) =>
            filePaths.Select(p =>
            {
                var st = new VCFileStatus(Path.GetFullPath(p), Name, true);
                return statuses is not null && statuses.TryGetValue(Path.GetFileName(p), out var edit) ? edit(st) : st;
            }).ToList();
        public Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false) =>
            Task.FromResult(Status(filePaths, remote));

        public VCResult PrepareToWrite(string filePath)
        {
            Calls.Add($"prepare {Path.GetFileName(filePath)}");
            return (failPrepare ?? []).Contains(Path.GetFileName(filePath))
                ? VCResult.Error($"cannot check out {Path.GetFileName(filePath)}")
                : VCResult.Ok();
        }
        public VCResult FinishedWrite(string filePath)
        {
            Calls.Add($"finished {Path.GetFileName(filePath)}");
            return VCResult.Ok();
        }
        public Task<VCResult> PrepareToWriteAsync(string filePath) => Task.FromResult(PrepareToWrite(filePath));
        public Task<VCResult> FinishedWriteAsync(string filePath) => Task.FromResult(FinishedWrite(filePath));
        public VCResult DeleteFile(string filePath) => VCResult.Ok();
        public VCResult DeleteFolder(string folderPath) => VCResult.Ok();
        public VCResult RenameFile(string oldPath, string newPath) => VCResult.Ok();
        public VCResult RenameFolder(string oldPath, string newPath) => VCResult.Ok();
        public Task<VCResult> DeleteFileAsync(string filePath) => Task.FromResult(DeleteFile(filePath));
        public Task<VCResult> DeleteFolderAsync(string folderPath) => Task.FromResult(DeleteFolder(folderPath));
        public Task<VCResult> RenameFileAsync(string oldPath, string newPath) => Task.FromResult(RenameFile(oldPath, newPath));
        public Task<VCResult> RenameFolderAsync(string oldPath, string newPath) => Task.FromResult(RenameFolder(oldPath, newPath));
    }

    /// <summary>The fake with an undo, which may fail or throw for the named files.</summary>
    private sealed class UndoingFakeProvider(
        Dictionary<string, Func<VCFileStatus, VCFileStatus>>? statuses = null,
        string[]? failPrepare = null,
        string[]? failUndo = null,
        string[]? throwUndo = null) : FakeProvider(statuses, failPrepare), IVCProvider
    {
        public VCResult UndoPrepareToWrite(string filePath, VCFileStatus before)
        {
            Calls.Add($"undo {Path.GetFileName(filePath)}");
            if ((throwUndo ?? []).Contains(Path.GetFileName(filePath))) throw new InvalidOperationException("undo blew up");
            return (failUndo ?? []).Contains(Path.GetFileName(filePath))
                ? VCResult.Error("revert refused")
                : VCResult.Ok();
        }
        public Task<VCResult> UndoPrepareToWriteAsync(string filePath, VCFileStatus before) =>
            Task.FromResult(UndoPrepareToWrite(filePath, before));
    }

    private static Dictionary<string, Func<VCFileStatus, VCFileStatus>> StatusOf(string name, Func<VCFileStatus, VCFileStatus> edit) =>
        new() { [name] = edit };

    // -- PrepareToWriteFiles: preflight ------------------------------------------

    [Fact]
    public void RefusesFileLockedByAnotherUserAndPreparesNothing()
    {
        var files = Seed(["a.txt", "b.txt", "c.txt"]);
        var fake = new UndoingFakeProvider(StatusOf("b.txt", s => s with { LockedBy = ["bob@bob-ws", "eve@eve-ws"] }));
        VCLib.SetProvider(fake);

        var batch = VCLib.PrepareToWriteFiles(files);
        Assert.False(batch.Success);
        Assert.Empty(fake.Calls);
        Assert.Equal(files, batch.Results.Select(r => r.FilePath));
        Assert.Equal(VCStatus.Locked, batch.Results[1].Status);
        Assert.Contains("is locked by bob@bob-ws, eve@eve-ws", batch.Results[1].Message);
        foreach (var r in new[] { batch.Results[0], batch.Results[2] })
        {
            Assert.False(r.Success);
            Assert.Equal(VCStatus.Error, r.Status);
            Assert.Contains("another file in the batch was refused", r.Message);
        }
    }

    [Fact]
    public void RefusesFileThatIsOutOfDate()
    {
        var files = Seed(["a.txt", "b.txt"]);
        var fake = new UndoingFakeProvider(StatusOf("a.txt", s => s with { OutOfDate = true }));
        VCLib.SetProvider(fake);

        var batch = VCLib.PrepareToWriteFiles(files);
        Assert.False(batch.Success);
        Assert.Empty(fake.Calls);
        Assert.Equal(VCStatus.OutOfDate, batch.Results[0].Status);
        Assert.Contains("out of date", batch.Results[0].Message);
        Assert.Equal(VCStatus.Error, batch.Results[1].Status);
    }

    [Fact]
    public void DoesNotRefuseFileNotOnDiskYet()
    {
        var a = Seed(["a.txt"])[0];
        var missing = Path.Combine(TestHelpers.MakeTempDir(), "new.txt");
        var fake = new UndoingFakeProvider(StatusOf("new.txt", s => s with { LockedBy = ["bob@bob-ws"] }));
        VCLib.SetProvider(fake);

        var batch = VCLib.PrepareToWriteFiles([a, missing]);
        Assert.True(batch.Success);
        Assert.Equal(["prepare a.txt", "prepare new.txt"], fake.Calls);
    }

    [Fact]
    public async Task AsyncRefusesLockedFileAndPreparesNothing()
    {
        var files = Seed(["a.txt", "b.txt"]);
        var fake = new UndoingFakeProvider(StatusOf("b.txt", s => s with { LockedBy = ["bob@bob-ws"] }));
        VCLib.SetProvider(fake);

        var batch = await VCLib.PrepareToWriteFilesAsync(files);
        Assert.False(batch.Success);
        Assert.Empty(fake.Calls);
        Assert.Equal(VCStatus.Locked, batch.Results[1].Status);
        Assert.Contains("is locked by bob@bob-ws", batch.Results[1].Message);
    }

    // -- PrepareToWriteFiles: checkout and undo ----------------------------------

    [Fact]
    public void PreparesEveryFileInOrderWhenNothingFails()
    {
        var files = Seed(["a.txt", "b.txt", "c.txt"]);
        var fake = new UndoingFakeProvider();
        VCLib.SetProvider(fake);

        var batch = VCLib.PrepareToWriteFiles(files);
        Assert.True(batch.Success);
        Assert.Equal(["prepare a.txt", "prepare b.txt", "prepare c.txt"], fake.Calls);
        Assert.All(batch.Results, r => Assert.Equal(VCStatus.Ok, r.Status));
    }

    [Fact]
    public void UndoesEarlierCheckoutsInReverseWhenThirdOfFourFails()
    {
        var files = Seed(["a.txt", "b.txt", "c.txt", "d.txt"]);
        var fake = new UndoingFakeProvider(failPrepare: ["c.txt"]);
        VCLib.SetProvider(fake);

        var batch = VCLib.PrepareToWriteFiles(files);
        Assert.False(batch.Success);
        Assert.Equal(["prepare a.txt", "prepare b.txt", "prepare c.txt", "undo b.txt", "undo a.txt"], fake.Calls);
        foreach (var r in new[] { batch.Results[0], batch.Results[1] })
        {
            Assert.False(r.Success);
            Assert.Equal(VCStatus.Error, r.Status);
            Assert.Contains("was undone because another file in the batch failed", r.Message);
        }
        Assert.Equal("cannot check out c.txt", batch.Results[2].Message);
        Assert.False(batch.Results[3].Success);
        Assert.Contains("was not prepared because another file in the batch failed", batch.Results[3].Message);
    }

    [Fact]
    public void ReportsUndoThatFailsWithoutThrowing()
    {
        var files = Seed(["a.txt", "b.txt", "c.txt"]);
        var fake = new UndoingFakeProvider(failPrepare: ["c.txt"], failUndo: ["b.txt"], throwUndo: ["a.txt"]);
        VCLib.SetProvider(fake);

        var batch = VCLib.PrepareToWriteFiles(files);
        Assert.False(batch.Success);
        Assert.Contains("could not be undone", batch.Results[1].Message);
        Assert.Contains("revert refused", batch.Results[1].Message);
        Assert.Contains("could not be undone", batch.Results[0].Message);
        Assert.Contains("undo blew up", batch.Results[0].Message);
    }

    [Fact]
    public void WorksWithProviderThatHasNoUndoPrepareToWrite()
    {
        var files = Seed(["a.txt", "b.txt"]);
        var fake = new FakeProvider(failPrepare: ["b.txt"]);
        VCLib.SetProvider(fake);

        var batch = VCLib.PrepareToWriteFiles(files);
        Assert.False(batch.Success);
        Assert.Equal(["prepare a.txt", "prepare b.txt"], fake.Calls);
        Assert.Contains("was undone", batch.Results[0].Message);
    }

    [Fact]
    public void PreparesRepeatedPathOnceAndReportsItForEveryInput()
    {
        var files = Seed(["a.txt", "b.txt"]);
        var fake = new UndoingFakeProvider();
        VCLib.SetProvider(fake);

        var batch = VCLib.PrepareToWriteFiles([files[0], files[1], files[0]]);
        Assert.True(batch.Success);
        Assert.Equal(["prepare a.txt", "prepare b.txt"], fake.Calls);
        Assert.Equal([files[0], files[1], files[0]], batch.Results.Select(r => r.FilePath));
    }

    [Fact]
    public async Task AsyncUndoesEarlierCheckoutsInReverseWhenThirdOfFourFails()
    {
        var files = Seed(["a.txt", "b.txt", "c.txt", "d.txt"]);
        var fake = new UndoingFakeProvider(failPrepare: ["c.txt"], failUndo: ["a.txt"]);
        VCLib.SetProvider(fake);

        var batch = await VCLib.PrepareToWriteFilesAsync(files);
        Assert.False(batch.Success);
        Assert.Equal(["prepare a.txt", "prepare b.txt", "prepare c.txt", "undo b.txt", "undo a.txt"], fake.Calls);
        Assert.Contains("revert refused", batch.Results[0].Message);
        Assert.Contains("was undone", batch.Results[1].Message);
        Assert.Contains("was not prepared", batch.Results[3].Message);
    }

    [Fact]
    public async Task AsyncWorksWithProviderThatHasNoUndoPrepareToWrite()
    {
        var files = Seed(["a.txt", "b.txt"]);
        VCLib.SetProvider(new FakeProvider(failPrepare: ["b.txt"]));

        var batch = await VCLib.PrepareToWriteFilesAsync(files);
        Assert.False(batch.Success);
        Assert.Contains("was undone", batch.Results[0].Message);
    }

    // -- WriteTextFiles(allOrNothing) --------------------------------------------

    [Fact]
    public void AllOrNothingWritesEveryFileAndCallsFinishedWrite()
    {
        var a = Seed(["a.txt"])[0];
        var fresh = Path.Combine(TestHelpers.MakeTempDir(), "sub", "fresh.txt");
        var fake = new UndoingFakeProvider();
        VCLib.SetProvider(fake);

        var batch = VCLib.WriteTextFiles([new VCFileWrite(a, "new A"), new VCFileWrite(fresh, "fresh")], allOrNothing: true);
        Assert.True(batch.Success);
        Assert.Equal("new A", File.ReadAllText(a));
        Assert.Equal("fresh", File.ReadAllText(fresh));
        Assert.Equal(["prepare a.txt", "prepare fresh.txt", "finished a.txt", "finished fresh.txt"], fake.Calls);
    }

    [Fact]
    public void AllOrNothingSkipsUnchangedFilesWithoutCheckingThemOut()
    {
        var files = Seed(["a.txt", "b.txt"], "same");
        var fake = new UndoingFakeProvider();
        VCLib.SetProvider(fake);

        var batch = VCLib.WriteTextFiles([new VCFileWrite(files[0], "same"), new VCFileWrite(files[1], "different")], allOrNothing: true);
        Assert.True(batch.Success);
        Assert.Equal(["prepare b.txt", "finished b.txt"], fake.Calls);
        Assert.Equal(files, batch.Results.Select(r => r.FilePath));
    }

    [Fact]
    public void AllOrNothingWritesNothingWhenOneFileCannotBeCheckedOut()
    {
        var files = Seed(["a.txt", "b.txt"]);
        var fresh = Path.Combine(TestHelpers.MakeTempDir(), "fresh.txt");
        var fake = new UndoingFakeProvider(failPrepare: ["b.txt"]);
        VCLib.SetProvider(fake);

        var batch = VCLib.WriteTextFiles([
            new VCFileWrite(files[0], "new A"),
            new VCFileWrite(files[1], "new B"),
            new VCFileWrite(fresh, "fresh"),
        ], allOrNothing: true);
        Assert.False(batch.Success);
        Assert.Equal("old", File.ReadAllText(files[0]));
        Assert.Equal("old", File.ReadAllText(files[1]));
        Assert.False(File.Exists(fresh));
        Assert.Equal(["prepare a.txt", "prepare b.txt", "undo a.txt"], fake.Calls);
        Assert.Equal("cannot check out b.txt", batch.Results[1].Message);
    }

    [Fact]
    public void AllOrNothingWritesNothingWhenFileIsLockedByAnotherUser()
    {
        var files = Seed(["a.txt", "b.txt"]);
        var fake = new UndoingFakeProvider(StatusOf("b.txt", s => s with { LockedBy = ["bob@bob-ws"] }));
        VCLib.SetProvider(fake);

        var batch = VCLib.WriteTextFiles([new VCFileWrite(files[0], "new A"), new VCFileWrite(files[1], "new B")], allOrNothing: true);
        Assert.False(batch.Success);
        Assert.Equal(VCStatus.Locked, batch.Results[1].Status);
        Assert.Equal("old", File.ReadAllText(files[0]));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void WithoutAllOrNothingStillCarriesOnPastARefusal()
    {
        var files = Seed(["a.txt", "b.txt"]);
        VCLib.SetProvider(new UndoingFakeProvider(failPrepare: ["a.txt"]));

        var batch = VCLib.WriteTextFiles([new VCFileWrite(files[0], "new A"), new VCFileWrite(files[1], "new B")]);
        Assert.False(batch.Success);
        Assert.Equal("old", File.ReadAllText(files[0]));
        Assert.Equal("new B", File.ReadAllText(files[1]));
    }

    [Fact]
    public async Task AllOrNothingAsyncWritesEveryFileOrNothing()
    {
        var files = Seed(["a.txt", "b.txt"]);
        var good = new UndoingFakeProvider();
        VCLib.SetProvider(good);
        var written = await VCLib.WriteTextFilesAsync([new VCFileWrite(files[0], "new A"), new VCFileWrite(files[1], "new B")], allOrNothing: true);
        Assert.True(written.Success);
        Assert.Equal(["prepare a.txt", "prepare b.txt", "finished a.txt", "finished b.txt"], good.Calls);

        var bad = new UndoingFakeProvider(failPrepare: ["b.txt"]);
        VCLib.SetProvider(bad);
        var refused = await VCLib.WriteTextFilesAsync([new VCFileWrite(files[0], "newer A"), new VCFileWrite(files[1], "newer B")], allOrNothing: true);
        Assert.False(refused.Success);
        Assert.Equal("new A", File.ReadAllText(files[0]));
        Assert.Equal(["prepare a.txt", "prepare b.txt", "undo a.txt"], bad.Calls);
    }

    // -- Real providers: the read-only bit comes back ----------------------------

    private static VCResult? RefuseB(string filePath) =>
        Path.GetFileName(filePath) == "b.txt" ? VCResult.Error("no b") : null;

    /// <summary>The real filesystem provider, refusing to prepare any file called b.txt.</summary>
    private sealed class FilesystemRefusingB : IVCProvider
    {
        private readonly FilesystemProvider _real = new();
        public string Name => _real.Name;
        public VCResult PrepareToWrite(string filePath) => RefuseB(filePath) ?? _real.PrepareToWrite(filePath);
        public Task<VCResult> PrepareToWriteAsync(string filePath) => Task.FromResult(PrepareToWrite(filePath));
        public VCResult UndoPrepareToWrite(string filePath, VCFileStatus before) => _real.UndoPrepareToWrite(filePath, before);
        public Task<VCResult> UndoPrepareToWriteAsync(string filePath, VCFileStatus before) => _real.UndoPrepareToWriteAsync(filePath, before);
        public VCResult FinishedWrite(string filePath) => _real.FinishedWrite(filePath);
        public Task<VCResult> FinishedWriteAsync(string filePath) => _real.FinishedWriteAsync(filePath);
        public VCResult DeleteFile(string filePath) => _real.DeleteFile(filePath);
        public VCResult DeleteFolder(string folderPath) => _real.DeleteFolder(folderPath);
        public VCResult RenameFile(string oldPath, string newPath) => _real.RenameFile(oldPath, newPath);
        public VCResult RenameFolder(string oldPath, string newPath) => _real.RenameFolder(oldPath, newPath);
        public Task<VCResult> DeleteFileAsync(string filePath) => _real.DeleteFileAsync(filePath);
        public Task<VCResult> DeleteFolderAsync(string folderPath) => _real.DeleteFolderAsync(folderPath);
        public Task<VCResult> RenameFileAsync(string oldPath, string newPath) => _real.RenameFileAsync(oldPath, newPath);
        public Task<VCResult> RenameFolderAsync(string oldPath, string newPath) => _real.RenameFolderAsync(oldPath, newPath);
        public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false) => _real.Status(filePaths, remote);
        public Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false) => _real.StatusAsync(filePaths, remote);
    }

    /// <summary>The real git provider, refusing to prepare any file called b.txt.</summary>
    private sealed class GitRefusingB : IVCProvider
    {
        private readonly GitProvider _real = new();
        public string Name => _real.Name;
        public VCResult PrepareToWrite(string filePath) => RefuseB(filePath) ?? _real.PrepareToWrite(filePath);
        public Task<VCResult> PrepareToWriteAsync(string filePath) => Task.FromResult(PrepareToWrite(filePath));
        public VCResult UndoPrepareToWrite(string filePath, VCFileStatus before) => _real.UndoPrepareToWrite(filePath, before);
        public Task<VCResult> UndoPrepareToWriteAsync(string filePath, VCFileStatus before) => _real.UndoPrepareToWriteAsync(filePath, before);
        public VCResult FinishedWrite(string filePath) => _real.FinishedWrite(filePath);
        public Task<VCResult> FinishedWriteAsync(string filePath) => _real.FinishedWriteAsync(filePath);
        public VCResult DeleteFile(string filePath) => _real.DeleteFile(filePath);
        public VCResult DeleteFolder(string folderPath) => _real.DeleteFolder(folderPath);
        public VCResult RenameFile(string oldPath, string newPath) => _real.RenameFile(oldPath, newPath);
        public VCResult RenameFolder(string oldPath, string newPath) => _real.RenameFolder(oldPath, newPath);
        public Task<VCResult> DeleteFileAsync(string filePath) => _real.DeleteFileAsync(filePath);
        public Task<VCResult> DeleteFolderAsync(string folderPath) => _real.DeleteFolderAsync(folderPath);
        public Task<VCResult> RenameFileAsync(string oldPath, string newPath) => _real.RenameFileAsync(oldPath, newPath);
        public Task<VCResult> RenameFolderAsync(string oldPath, string newPath) => _real.RenameFolderAsync(oldPath, newPath);
        public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false) => _real.Status(filePaths, remote);
        public Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false) => _real.StatusAsync(filePaths, remote);
    }

    [Fact]
    public void FilesystemReadOnlyFileIsReadOnlyAgainAfterFailureElsewhere()
    {
        var files = Seed(["a.txt", "b.txt"]);
        MakeReadOnly(files[0]);
        VCLib.SetProvider(new FilesystemRefusingB());

        var batch = VCLib.WriteTextFiles([new VCFileWrite(files[0], "new A"), new VCFileWrite(files[1], "new B")], allOrNothing: true);
        Assert.False(batch.Success);
        Assert.True(IsReadOnly(files[0]));
        Assert.Equal("old", File.ReadAllText(files[0]));
        Assert.Contains("was undone", batch.Results[0].Message);
    }

    [Fact]
    public async Task FilesystemAsyncRestoresReadOnlyBitToo()
    {
        var files = Seed(["a.txt", "b.txt"]);
        MakeReadOnly(files[0]);
        VCLib.SetProvider(new FilesystemRefusingB());

        var batch = await VCLib.PrepareToWriteFilesAsync(files);
        Assert.False(batch.Success);
        Assert.True(IsReadOnly(files[0]));
    }

    [Fact]
    public void FilesystemFileThatWasWritableIsLeftWritable()
    {
        var files = Seed(["a.txt", "b.txt"]);
        VCLib.SetProvider(new FilesystemRefusingB());

        VCLib.PrepareToWriteFiles(files);
        Assert.False(IsReadOnly(files[0]));
    }

    [Fact]
    public void FilesystemWritesReadOnlyFileWhenWholeBatchCanBePrepared()
    {
        var files = Seed(["a.txt", "b.txt"]);
        MakeReadOnly(files[0]);
        VCLib.SetProvider(new FilesystemProvider());

        var batch = VCLib.WriteTextFiles([new VCFileWrite(files[0], "new A"), new VCFileWrite(files[1], "new B")], allOrNothing: true);
        Assert.True(batch.Success);
        Assert.Equal("new A", File.ReadAllText(files[0]));
        Assert.Equal("new B", File.ReadAllText(files[1]));
    }

    [Fact]
    public void GitReadOnlyTrackedFileIsReadOnlyAgainAfterFailureElsewhere()
    {
        var dir = TestHelpers.MakeTempDir();
        TestHelpers.InitGitRepo(dir);
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        File.WriteAllText(a, "old");
        File.WriteAllText(b, "old");
        Run("git", $"-C \"{dir}\" add .");
        Run("git", $"-C \"{dir}\" commit -m track");
        MakeReadOnly(a);
        VCLib.SetProvider(new GitRefusingB());

        var batch = VCLib.WriteTextFiles([new VCFileWrite(a, "new A"), new VCFileWrite(b, "new B")], allOrNothing: true);
        Assert.False(batch.Success);
        Assert.True(IsReadOnly(a));
        Assert.Equal("old", File.ReadAllText(a));

        static void Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
        }
    }

    // -- Real providers against canned CLI output: the exact undo commands --------

    private static string DepotFstat(string filePath) =>
        $"... depotFile //depot/{Path.GetFileName(filePath)}\n... clientFile {filePath}\n... headRev 3\n... haveRev 3";

    [Fact]
    public void PerforceRevertsWithRevertMinusADepotFileThisBatchOpened()
    {
        var a = Seed(["a.txt"])[0];
        var calls = RecordingRunner((command, args) =>
            args[0] == "fstat" ? new CommandResult(0, DepotFstat(a), "") : null);

        var result = new PerforceProvider().UndoPrepareToWrite(a, new VCFileStatus(a, "perforce", false, Tracked: true));
        Assert.True(result.Success, result.Message);
        Assert.Contains($"p4 revert -a {a}", calls);
    }

    [Fact]
    public async Task PerforceNeverRevertsFileAlreadyOpenedByMe()
    {
        var a = Seed(["a.txt"])[0];
        var calls = RecordingRunner();
        var before = new VCFileStatus(a, "perforce", true, Tracked: true, OpenedByMe: true);

        Assert.True(new PerforceProvider().UndoPrepareToWrite(a, before).Success);
        Assert.True((await new PerforceProvider().UndoPrepareToWriteAsync(a, before)).Success);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task PerforceReportsRevertThatFails()
    {
        var a = Seed(["a.txt"])[0];
        RecordingRunner((command, args) => args[0] switch
        {
            "fstat" => new CommandResult(0, DepotFstat(a), ""),
            "revert" => new CommandResult(1, "", "server unreachable"),
            _ => null,
        });

        var result = await new PerforceProvider().UndoPrepareToWriteAsync(a, new VCFileStatus(a, "perforce", false));
        Assert.False(result.Success);
        Assert.Contains("server unreachable", result.Message);
    }

    [Fact]
    public void PerforceFailedBatchRevertsWhatItOpenedAndLeavesWhatIHadOpen()
    {
        var files = Seed(["a.txt", "b.txt", "c.txt"]);
        var (a, b, c) = (files[0], files[1], files[2]);
        var ztag = string.Join("\n\n",
            $"... depotFile //depot/a.txt\n... clientFile {a}\n... headRev 1\n... haveRev 1\n... action edit",
            $"... depotFile //depot/b.txt\n... clientFile {b}\n... headRev 1\n... haveRev 1",
            $"... depotFile //depot/c.txt\n... clientFile {c}\n... headRev 1\n... haveRev 1");
        var calls = RecordingRunner((command, args) =>
        {
            if (args[0] == "-ztag") return new CommandResult(0, ztag, "");
            if (args[0] == "fstat") return new CommandResult(0, DepotFstat(args[1]), "");
            if (args[0] == "edit" && args[1] == c) return new CommandResult(1, "", "no permission");
            return null;
        });
        VCLib.SetProvider(new PerforceProvider());

        var batch = VCLib.PrepareToWriteFiles(files);
        Assert.False(batch.Success);
        Assert.Equal([$"p4 revert -a {b}"], calls.Where(x => x.Contains("revert")));
        Assert.Contains("no permission", batch.Results[2].Message);
    }

    [Fact]
    public void PerforceAnotherUserHavingFileOpenRefusesBatch()
    {
        var files = Seed(["a.txt", "b.txt"]);
        var ztag = string.Join("\n\n",
            $"... depotFile //depot/a.txt\n... clientFile {files[0]}\n... headRev 1\n... haveRev 1",
            $"... depotFile //depot/b.txt\n... clientFile {files[1]}\n... headRev 1\n... haveRev 1\n... otherOpen0 bob@bob-ws\n... otherOpen 1");
        var calls = RecordingRunner((command, args) =>
            args[0] == "-ztag" ? new CommandResult(0, ztag, "") : null);
        VCLib.SetProvider(new PerforceProvider());

        var batch = VCLib.PrepareToWriteFiles(files);
        Assert.False(batch.Success);
        Assert.Equal(VCStatus.Locked, batch.Results[1].Status);
        Assert.Contains("bob@bob-ws", batch.Results[1].Message);
        Assert.DoesNotContain(calls, x => x.StartsWith("p4 edit"));
    }

    [Fact]
    public void SvnReleasesNeedsLockFileWithUnlockAndMakesItReadOnlyAgain()
    {
        var a = Seed(["a.txt"])[0];
        var calls = RecordingRunner();

        var result = new SvnProvider().UndoPrepareToWrite(a, new VCFileStatus(a, "svn", false, Tracked: true));
        Assert.True(result.Success, result.Message);
        Assert.Contains($"svn unlock {a}", calls);
        Assert.True(IsReadOnly(a));
    }

    [Fact]
    public async Task SvnFileOnlyMadeWritableJustGetsReadOnlyBitBack()
    {
        var a = Seed(["a.txt"])[0];
        RecordingRunner((command, args) =>
            args[0] == "unlock" ? new CommandResult(1, "", "svn: E195013: 'a.txt' is not locked in this working copy") : null);

        var result = await new SvnProvider().UndoPrepareToWriteAsync(a, new VCFileStatus(a, "svn", false, Tracked: true));
        Assert.True(result.Success, result.Message);
        Assert.True(IsReadOnly(a));
    }

    [Fact]
    public async Task SvnNeverUnlocksFileAlreadyMineOrOneThatWasWritable()
    {
        var a = Seed(["a.txt"])[0];
        var calls = RecordingRunner();
        var svn = new SvnProvider();

        Assert.True(svn.UndoPrepareToWrite(a, new VCFileStatus(a, "svn", false, OpenedByMe: true)).Success);
        Assert.True((await svn.UndoPrepareToWriteAsync(a, new VCFileStatus(a, "svn", false, OpenedByMe: true))).Success);
        Assert.True(svn.UndoPrepareToWrite(a, new VCFileStatus(a, "svn", true)).Success);
        Assert.Empty(calls);
        Assert.False(IsReadOnly(a));
    }

    [Fact]
    public async Task PlasticUndoesCheckoutThisBatchMadeWithUndocheckout()
    {
        var a = Seed(["a.txt"])[0];
        var calls = RecordingRunner((command, args) =>
            args[0] == "status" ? new CommandResult(0, $"CO {a}", "") : null);
        var before = new VCFileStatus(a, "plastic", false, Tracked: true, Dirty: false);

        Assert.True(new PlasticProvider().UndoPrepareToWrite(a, before).Success);
        Assert.True((await new PlasticProvider().UndoPrepareToWriteAsync(a, before)).Success);
        Assert.Equal([$"cm undocheckout {a}", $"cm undocheckout {a}"], calls.Where(x => x.StartsWith("cm undocheckout")));
    }

    [Fact]
    public void PlasticNeverUndoesCheckoutIAlreadyHad()
    {
        var a = Seed(["a.txt"])[0];
        var calls = RecordingRunner((command, args) =>
            args[0] == "status" ? new CommandResult(0, $"CO {a}", "") : null);
        var plastic = new PlasticProvider();

        Assert.True(plastic.UndoPrepareToWrite(a, new VCFileStatus(a, "plastic", true, Tracked: true, OpenedByMe: true)).Success);
        Assert.True(plastic.UndoPrepareToWrite(a, new VCFileStatus(a, "plastic", true, Tracked: true, Dirty: true)).Success);
        Assert.DoesNotContain(calls, x => x.StartsWith("cm undocheckout"));
    }
}

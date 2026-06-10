using System.Diagnostics;
using SimpleVCLib;
using Xunit;

// VCLib's overrides (SetProvider / SetCommandRunner) are global static state, so
// test classes that use them cannot run in parallel with classes doing real VC
// operations - serialize the assembly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SimpleVCLib.Tests;

// ---------------------------------------------------------------------------
// FileStatus / WriteTextFiles - mirrors js/test/status.test.js.
// Perforce cases run against CANNED `p4 -ztag fstat` transcripts via the
// command-runner override (no p4 needed); git cases use a real temp repository.
// ---------------------------------------------------------------------------

public class StatusTests : IDisposable
{
    public void Dispose()
    {
        VCLib.ClearProvider();
        VCLib.ClearCommandRunner();
    }

    private static string Ztag(params string[][] records) =>
        string.Join("\n\n", records.Select(r => string.Join("\n", r)));

    /// <summary>A canned runner that answers `p4 -ztag fstat` with the given output.</summary>
    private static Func<string, string[], CommandResult> CannedP4(string fstatOutput) =>
        (command, args) =>
            command == "p4" && args.Length >= 2 && args[0] == "-ztag" && args[1] == "fstat"
                ? new CommandResult(0, fstatOutput, "")
                : new CommandResult(1, "", $"unexpected command: {command} {string.Join(' ', args)}");

    // -- git (real repository) ------------------------------------------------

    private static (string Dir, string Tracked, string Untracked) MakeGitRepo()
    {
        var dir = TestHelpers.MakeTempDir();
        TestHelpers.InitGitRepo(dir);
        var tracked = Path.Combine(dir, "tracked.txt");
        File.WriteAllText(tracked, "tracked");
        Run("git", $"-C \"{dir}\" add tracked.txt");
        Run("git", $"-C \"{dir}\" commit -m track");
        var untracked = Path.Combine(dir, "untracked.txt");
        File.WriteAllText(untracked, "untracked");
        return (dir, tracked, untracked);

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

    [Fact]
    public void GitStatusDistinguishesTrackedFromUntracked()
    {
        var (_, tracked, untracked) = MakeGitRepo();
        var statuses = VCLib.FileStatus([tracked, untracked]);
        Assert.Equal("git", statuses[0].System);
        Assert.True(statuses[0].Tracked);
        Assert.True(statuses[0].Writable);
        Assert.False(statuses[1].Tracked);
    }

    [Fact]
    public void GitStatusReportsReadOnlyFileAsNotWritable()
    {
        var (_, tracked, _) = MakeGitRepo();
        new FileInfo(tracked).IsReadOnly = true;
        var statuses = VCLib.FileStatus([tracked]);
        Assert.False(statuses[0].Writable);
    }

    // -- Perforce (canned -ztag fstat transcripts) ----------------------------

    [Fact]
    public void P4StatusReportsExclusiveLockByAnotherUser()
    {
        var clientFile = Path.Combine(TestHelpers.MakeTempDir(), "a.txt");
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(Ztag([
            "... depotFile //depot/proj/a.txt",
            $"... clientFile {clientFile}",
            "... isMapped ",
            "... headAction edit",
            "... headRev 7",
            "... haveRev 7",
            "... otherOpen0 bob@bob-ws",
            "... otherOpen 1",
            "... otherLock ",
            "... otherLock0 bob@bob-ws",
        ])));
        var st = VCLib.FileStatus([clientFile])[0];
        Assert.Equal("perforce", st.System);
        Assert.True(st.Tracked);
        Assert.Equal(["bob@bob-ws"], st.LockedBy);
        Assert.Null(st.OpenedByMe);
    }

    [Fact]
    public void P4StatusReportsMyCheckoutAsOpenedByMe()
    {
        var clientFile = Path.Combine(TestHelpers.MakeTempDir(), "b.txt");
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(Ztag([
            "... depotFile //depot/proj/b.txt",
            $"... clientFile {clientFile}",
            "... headRev 3",
            "... haveRev 3",
            "... action edit",
            "... change default",
        ])));
        var st = VCLib.FileStatus([clientFile])[0];
        Assert.True(st.Tracked);
        Assert.True(st.OpenedByMe);
        Assert.Null(st.LockedBy);
    }

    [Fact]
    public void P4StatusTreatsDeletedAtHeadAsUntracked()
    {
        var clientFile = Path.Combine(TestHelpers.MakeTempDir(), "gone.txt");
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(Ztag([
            "... depotFile //depot/proj/gone.txt",
            $"... clientFile {clientFile}",
            "... headAction delete",
            "... headRev 9",
        ])));
        Assert.False(VCLib.FileStatus([clientFile])[0].Tracked);
    }

    [Fact]
    public void P4StatusFlagsOutOfDateWhenHaveRevLagsHeadRev()
    {
        var clientFile = Path.Combine(TestHelpers.MakeTempDir(), "c.txt");
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(Ztag([
            "... depotFile //depot/proj/c.txt",
            $"... clientFile {clientFile}",
            "... headAction edit",
            "... headRev 5",
            "... haveRev 4",
        ])));
        var st = VCLib.FileStatus([clientFile])[0];
        Assert.True(st.Tracked);
        Assert.True(st.OutOfDate);
    }

    [Fact]
    public void P4StatusTreatsNeverSubmittedMappedFileAsUntracked()
    {
        var clientFile = Path.Combine(TestHelpers.MakeTempDir(), "new.txt");
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(Ztag([
            $"... clientFile {clientFile}",
            "... isMapped ",
        ])));
        Assert.False(VCLib.FileStatus([clientFile])[0].Tracked);
    }

    [Fact]
    public void P4StatusTreatsUnknownFileAsUntracked()
    {
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(""));
        var st = VCLib.FileStatus([Path.Combine(TestHelpers.MakeTempDir(), "x.txt")])[0];
        Assert.Equal("perforce", st.System);
        Assert.False(st.Tracked);
    }

    [Fact]
    public void P4StatusBatchesEveryPathIntoOneFstatCall()
    {
        var fstatCalls = 0;
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner((command, args) =>
        {
            fstatCalls++;
            Assert.Equal(3, args.Length - 2); // -ztag fstat + all three paths in one spawn
            return new CommandResult(0, "", "");
        });
        VCLib.FileStatus(["/ws/a.txt", "/ws/b.txt", "/ws/c.txt"]);
        Assert.Equal(1, fstatCalls);
    }

    // -- filesystem fallback ---------------------------------------------------

    [Fact]
    public void FilesystemStatusReportsJustTheWritableBit()
    {
        VCLib.SetProvider(new FilesystemProvider());
        var dir = TestHelpers.MakeTempDir();
        var file = Path.Combine(dir, "plain.txt");
        File.WriteAllText(file, "x");
        var statuses = VCLib.FileStatus([file, Path.Combine(dir, "not-there.txt")]);
        Assert.Equal("filesystem", statuses[0].System);
        Assert.True(statuses[0].Writable);
        Assert.Null(statuses[0].Tracked);
        Assert.True(statuses[1].Writable); // not on disk yet - nothing forbids writing it
    }

    // -- WriteTextFiles ----------------------------------------------------------

    [Fact]
    public void WriteTextFilesWritesBatchCreatingDirectories()
    {
        VCLib.SetProvider(new FilesystemProvider());
        var dir = TestHelpers.MakeTempDir();
        var batch = VCLib.WriteTextFiles([
            new VCFileWrite(Path.Combine(dir, "sub", "a.txt"), "A"),
            new VCFileWrite(Path.Combine(dir, "deeper", "still", "b.txt"), "B"),
        ]);
        Assert.True(batch.Success);
        Assert.Equal(2, batch.Results.Count);
        Assert.Equal("A", File.ReadAllText(Path.Combine(dir, "sub", "a.txt")));
    }

    [Fact]
    public void WriteTextFilesReportsRefusalWithWhyAndKeepsGoing()
    {
        VCLib.SetProvider(new LockedFakeProvider());
        var dir = TestHelpers.MakeTempDir();
        var batch = VCLib.WriteTextFiles([
            new VCFileWrite(Path.Combine(dir, "locked.txt"), "x"),
            new VCFileWrite(Path.Combine(dir, "free.txt"), "y"),
        ]);
        Assert.False(batch.Success);
        var failure = batch.Results.Single(r => !r.Success);
        Assert.Equal(VCStatus.Locked, failure.Status);
        Assert.Contains("locked by bob@bob-ws", failure.Message);
        Assert.False(File.Exists(Path.Combine(dir, "locked.txt"))); // refused write not forced
        Assert.True(File.Exists(Path.Combine(dir, "free.txt")));    // the rest proceeded
    }

    /// <summary>A provider that refuses writes to *locked.txt, for failure-path tests.</summary>
    private sealed class LockedFakeProvider : IVCProvider
    {
        public string Name => "git";
        public VCResult PrepareToWrite(string filePath) =>
            filePath.EndsWith("locked.txt", StringComparison.Ordinal)
                ? VCResult.Failure(VCStatus.Locked, "'locked.txt' is locked by bob@bob-ws")
                : VCResult.Ok();
        public VCResult FinishedWrite(string filePath) => VCResult.Ok();
        public VCResult DeleteFile(string filePath) => VCResult.Ok();
        public VCResult DeleteFolder(string folderPath) => VCResult.Ok();
        public VCResult RenameFile(string oldPath, string newPath) => VCResult.Ok();
        public VCResult RenameFolder(string oldPath, string newPath) => VCResult.Ok();
        public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths) =>
            filePaths.Select(p => new VCFileStatus(Path.GetFullPath(p), Name, true)).ToList();
    }
}

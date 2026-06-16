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

    [Fact]
    public void GitStatusFlagsModifiedTrackedFileAsDirty()
    {
        var (_, tracked, untracked) = MakeGitRepo();
        File.WriteAllText(tracked, "tracked + local edit");
        var statuses = VCLib.FileStatus([tracked, untracked]);
        Assert.True(statuses[0].Tracked);
        Assert.True(statuses[0].Dirty);
        Assert.False(statuses[1].Dirty); // untracked surfaces via Tracked:false, not Dirty
    }

    [Fact]
    public void GitStatusReportsCommittedUnmodifiedFileAsNotDirty()
    {
        var (_, tracked, _) = MakeGitRepo();
        var st = VCLib.FileStatus([tracked])[0];
        Assert.True(st.Tracked);
        Assert.False(st.Dirty);
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
        Assert.True(st.Dirty); // opened in a pending changelist = pending local change
        Assert.Null(st.LockedBy);
    }

    [Fact]
    public void P4StatusReportsSyncedUnopenedFileAsNotDirty()
    {
        var clientFile = Path.Combine(TestHelpers.MakeTempDir(), "d.txt");
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(Ztag([
            "... depotFile //depot/proj/d.txt",
            $"... clientFile {clientFile}",
            "... headAction edit",
            "... headRev 2",
            "... haveRev 2",
        ])));
        var st = VCLib.FileStatus([clientFile])[0];
        Assert.True(st.Tracked);
        Assert.False(st.Dirty);
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

    // -- SVN (canned `svn status --xml -v` transcripts) -----------------------

    /// <summary>A canned runner answering `svn status --xml -v` with the given XML.</summary>
    private static Func<string, string[], CommandResult> CannedSvn(string xml) =>
        (command, args) =>
            command == "svn" && args.Length > 0 && args[0] == "status" && args.Contains("--xml")
                ? new CommandResult(0, xml, "")
                : new CommandResult(1, "", $"unexpected: {command} {string.Join(' ', args)}");

    private static string SvnEntry(string path, string item, string props = "none") =>
        $"<entry path=\"{path}\"><wc-status props=\"{props}\" item=\"{item}\" revision=\"4\">" +
        "<commit revision=\"4\"><author>alice</author></commit></wc-status></entry>";

    private static string SvnStatusXml(params string[] entries) =>
        "<?xml version=\"1.0\"?>\n<status>\n<target path=\"/ws/wc\">\n" +
        string.Join("\n", entries) + "\n</target>\n</status>";

    [Fact]
    public void SvnStatusClassifiesNormalModifiedUnversioned()
    {
        var dir = TestHelpers.MakeTempDir();
        var clean = Path.Combine(dir, "clean.txt");
        var edited = Path.Combine(dir, "edited.txt");
        var fresh = Path.Combine(dir, "new.txt");
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner(CannedSvn(SvnStatusXml(
            SvnEntry(clean, "normal"),
            SvnEntry(edited, "modified"),
            SvnEntry(fresh, "unversioned"))));
        var statuses = VCLib.FileStatus([clean, edited, fresh]);
        Assert.Equal("svn", statuses[0].System);
        Assert.True(statuses[0].Tracked);
        Assert.False(statuses[0].Dirty);
        Assert.True(statuses[1].Tracked);
        Assert.True(statuses[1].Dirty);
        Assert.False(statuses[2].Tracked);
        Assert.False(statuses[2].Dirty);
    }

    [Fact]
    public void SvnStatusTreatsPropertyOnlyModificationAsDirty()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "props.txt");
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner(CannedSvn(SvnStatusXml(SvnEntry(file, "normal", "modified"))));
        var st = VCLib.FileStatus([file])[0];
        Assert.True(st.Tracked);
        Assert.True(st.Dirty);
    }

    [Fact]
    public void SvnStatusReportsWritableOnlyForPathNotMentioned()
    {
        var dir = TestHelpers.MakeTempDir();
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner(CannedSvn(SvnStatusXml(SvnEntry(Path.Combine(dir, "other.txt"), "normal"))));
        var st = VCLib.FileStatus([Path.Combine(dir, "missing-from-output.txt")])[0];
        Assert.Equal("svn", st.System);
        Assert.Null(st.Tracked);
        Assert.Null(st.Dirty);
    }

    // -- Plastic (canned `cm status --machinereadable` transcripts) -----------

    /// <summary>A canned runner answering `cm status --machinereadable` with the given output.</summary>
    private static Func<string, string[], CommandResult> CannedCm(string output) =>
        (command, args) =>
            command == "cm" && args.Length > 0 && args[0] == "status" && args.Contains("--machinereadable")
                ? new CommandResult(0, output, "")
                : new CommandResult(1, "", $"unexpected: {command} {string.Join(' ', args)}");

    [Fact]
    public void PlasticStatusClassifiesChangedCheckedOutPrivate()
    {
        var dir = TestHelpers.MakeTempDir();
        var changed = Path.Combine(dir, "changed.txt");
        var checkedOut = Path.Combine(dir, "checkedout.txt");
        var priv = Path.Combine(dir, "new.txt");
        VCLib.SetProvider(new PlasticProvider());
        VCLib.SetCommandRunner(CannedCm(string.Join('\n',
            "STATUS 23 /main rep:default@server",
            $"CH \"{changed}\"",
            $"CO \"{checkedOut}\"",
            $"PR \"{priv}\"")));
        var statuses = VCLib.FileStatus([changed, checkedOut, priv]);
        Assert.Equal("plastic", statuses[0].System);
        Assert.True(statuses[0].Tracked);
        Assert.True(statuses[0].Dirty);
        Assert.True(statuses[1].Dirty); // checked out = opened = pending local change
        Assert.False(statuses[2].Tracked);
        Assert.False(statuses[2].Dirty);
    }

    [Fact]
    public void PlasticStatusTreatsUnlistedControlledFileAsClean()
    {
        var dir = TestHelpers.MakeTempDir();
        var file = Path.Combine(dir, "clean.txt");
        File.WriteAllText(file, "x");
        VCLib.SetProvider(new PlasticProvider());
        VCLib.SetCommandRunner(CannedCm("STATUS 1 /main rep:default@server"));
        var st = VCLib.FileStatus([file])[0];
        Assert.True(st.Tracked);
        Assert.False(st.Dirty);
    }

    [Fact]
    public void PlasticStatusParsesUnquotedPaths()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "changed.txt");
        VCLib.SetProvider(new PlasticProvider());
        VCLib.SetCommandRunner(CannedCm($"CH {file}"));
        var st = VCLib.FileStatus([file])[0];
        Assert.True(st.Dirty);
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

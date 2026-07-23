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

    // -- git-lfs lock query gating (#26) --------------------------------------
    //
    // `git lfs locks` always contacts the lock server (a remote round-trip that pops a
    // credential window on Windows). It must run ONLY when the repo uses LFS file
    // locking (a `lockable` .gitattributes entry) AND remote was requested. GitProvider
    // is forced via SetProvider so no real .git is needed; a canned runner records
    // whether `git lfs locks` was ever invoked. The .gitattributes is a real file, since
    // the gate reads it directly (not through the command runner). Mirrors js/test.

    /// <summary>Canned git: rev-parse points at <paramref name="dir"/> (repo root = dir),
    /// status is clean, and `lfs locks` flips <paramref name="calledLfsLocks"/>.</summary>
    private static Func<string, string[], CommandResult> CannedGit(string dir, Action onLfsLocks) =>
        (command, args) =>
        {
            if (command != "git") return new CommandResult(1, "", $"unexpected: {command}");
            var a = args.Length >= 2 && args[0] == "-C" ? args[2..] : args; // drop leading -C <cwd>
            if (a.Length > 0 && a[0] == "rev-parse") return new CommandResult(0, $"{dir}\n\n", "");
            if (a.Length > 0 && a[0] == "status") return new CommandResult(0, "", "");
            if (a.Length > 1 && a[0] == "lfs" && a[1] == "locks") { onLfsLocks(); return new CommandResult(0, "{\"ours\":[],\"theirs\":[]}", ""); }
            return new CommandResult(0, "", "");
        };

    private static bool RunGitStatusQueriedLocks(bool remote, bool hasLockable)
    {
        var dir = TestHelpers.MakeTempDir();
        if (hasLockable) File.WriteAllText(Path.Combine(dir, ".gitattributes"), "*.bin filter=lfs lockable\n");
        var called = false;
        VCLib.SetProvider(new GitProvider());
        VCLib.SetCommandRunner(CannedGit(dir, () => called = true));
        VCLib.FileStatus([Path.Combine(dir, "asset.bin")], remote: remote);
        return called;
    }

    [Fact]
    public void GitLfsLocksNotQueriedWithoutRemoteEvenInLockBasedRepo() =>
        Assert.False(RunGitStatusQueriedLocks(remote: false, hasLockable: true));

    [Fact]
    public void GitLfsLocksNotQueriedWithRemoteWhenNoLockableAttributes() =>
        Assert.False(RunGitStatusQueriedLocks(remote: true, hasLockable: false));

    [Fact]
    public void GitLfsLocksQueriedOnlyWhenLockBasedAndRemote() =>
        Assert.True(RunGitStatusQueriedLocks(remote: true, hasLockable: true));

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

    // -- SVN remote (svn status -u): lockedBy / outOfDate ---------------------

    [Fact]
    public void SvnRemoteReportsLockedByFromServerLock()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "locked.txt");
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner(CannedSvn(SvnStatusXml(
            $"<entry path=\"{file}\">" +
            "<wc-status item=\"normal\" props=\"normal\" revision=\"1\"><commit revision=\"1\"><author>ian</author></commit></wc-status>" +
            "<repos-status props=\"none\" item=\"none\"><lock><token>t</token><owner>bob</owner></lock></repos-status>" +
            "</entry>")));
        var st = VCLib.FileStatus([file], remote: true)[0];
        Assert.True(st.Tracked);
        Assert.Equal(["bob"], st.LockedBy);
        Assert.Null(st.OpenedByMe);
    }

    [Fact]
    public void SvnRemoteFlagsOutOfDate()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "stale.txt");
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner(CannedSvn(SvnStatusXml(
            $"<entry path=\"{file}\">" +
            "<wc-status item=\"normal\" props=\"none\" revision=\"1\"><commit revision=\"1\"><author>ian</author></commit></wc-status>" +
            "<repos-status props=\"none\" item=\"modified\"></repos-status>" +
            "</entry>")));
        var st = VCLib.FileStatus([file], remote: true)[0];
        Assert.True(st.OutOfDate);
        Assert.Null(st.LockedBy);
    }

    [Fact]
    public void SvnRemoteReportsOpenedByMeForOurOwnLock()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "mine.txt");
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner(CannedSvn(SvnStatusXml(
            $"<entry path=\"{file}\">" +
            "<wc-status item=\"normal\" props=\"none\" revision=\"1\"><commit revision=\"1\"><author>ian</author></commit>" +
            "<lock><token>t</token><owner>ian</owner></lock></wc-status>" +
            "<repos-status props=\"none\" item=\"none\"><lock><token>t</token><owner>ian</owner></lock></repos-status>" +
            "</entry>")));
        var st = VCLib.FileStatus([file], remote: true)[0];
        Assert.True(st.OpenedByMe);
        Assert.Null(st.LockedBy);
    }

    [Fact]
    public void SvnDefaultDoesNotRequestRemote()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "clean.txt");
        var sawU = false;
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner((command, args) =>
        {
            if (args.Contains("-u")) sawU = true;
            return new CommandResult(0, SvnStatusXml(SvnEntry(file, "normal")), "");
        });
        var st = VCLib.FileStatus([file])[0];
        Assert.False(sawU);
        Assert.Null(st.LockedBy);
        Assert.Null(st.OutOfDate);
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

    // -- Plastic remote (cm fileinfo + cm whoami) -----------------------------

    /// <summary>Dispatches the three cm subcommands the remote path issues.</summary>
    private static Func<string, string[], CommandResult> CannedCmRemote(string status, string fileinfo, string whoami) =>
        (command, args) =>
        {
            if (command != "cm") return new CommandResult(1, "", "not cm");
            return args[0] switch
            {
                "status" => new CommandResult(0, status, ""),
                "fileinfo" => new CommandResult(0, fileinfo, ""),
                "whoami" => new CommandResult(0, whoami, ""),
                _ => new CommandResult(1, "", $"unexpected: {string.Join(' ', args)}"),
            };
        };

    [Fact]
    public void PlasticRemoteFillsOutOfDateLockedByAndOpenedByMe()
    {
        var dir = TestHelpers.MakeTempDir();
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        VCLib.SetProvider(new PlasticProvider());
        VCLib.SetCommandRunner(CannedCmRemote(
            status: $"CH \"{a}\"\nCO \"{b}\"",
            fileinfo: "3;7;rep@srv;bob;bob-ws;/a.txt\n7;7;rep@srv;ian;ian-ws;/b.txt",
            whoami: "ian"));
        var statuses = VCLib.FileStatus([a, b], remote: true);
        Assert.True(statuses[0].OutOfDate);                 // loaded cset 3 < head 7
        Assert.Equal(["bob@bob-ws"], statuses[0].LockedBy); // bob != me
        Assert.Null(statuses[0].OpenedByMe);
        Assert.True(statuses[1].OpenedByMe);                // locked by ian == me
        Assert.Null(statuses[1].LockedBy);
        Assert.Null(statuses[1].OutOfDate);                 // cset 7 == head 7
    }

    [Fact]
    public void PlasticDefaultDoesNotCallFileinfo()
    {
        var a = Path.Combine(TestHelpers.MakeTempDir(), "a.txt");
        var calledFileinfo = false;
        VCLib.SetProvider(new PlasticProvider());
        VCLib.SetCommandRunner((command, args) =>
        {
            if (command == "cm" && args[0] == "fileinfo") calledFileinfo = true;
            if (command == "cm" && args[0] == "status") return new CommandResult(0, $"CH \"{a}\"", "");
            return new CommandResult(0, "", "");
        });
        var st = VCLib.FileStatus([a])[0];
        Assert.False(calledFileinfo);
        Assert.True(st.Dirty);
        Assert.Null(st.OutOfDate);
        Assert.Null(st.LockedBy);
    }

    // -- FileStatusAsync (async twin) -----------------------------------------

    [Fact]
    public async Task FileStatusAsyncMatchesFileStatusForRealGitRepo()
    {
        var (_, tracked, untracked) = MakeGitRepo();
        File.WriteAllText(tracked, "tracked + edit");
        IReadOnlyList<string> paths = [tracked, untracked];
        var sync = VCLib.FileStatus(paths);
        var async = await VCLib.FileStatusAsync(paths);
        Assert.Equal(sync, async);
        Assert.True(async[0].Tracked);
        Assert.True(async[0].Dirty);
        Assert.False(async[1].Tracked);
    }

    [Fact]
    public async Task FileStatusAsyncDrivesPerforceCannedRunner()
    {
        var clientFile = Path.Combine(TestHelpers.MakeTempDir(), "b.txt");
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(Ztag([
            "... depotFile //depot/proj/b.txt",
            $"... clientFile {clientFile}",
            "... headRev 3",
            "... haveRev 3",
            "... action edit",
        ])));
        var st = (await VCLib.FileStatusAsync([clientFile]))[0];
        Assert.True(st.Tracked);
        Assert.True(st.OpenedByMe);
        Assert.True(st.Dirty);
    }

    [Fact]
    public async Task FileStatusAsyncSupportsRemoteForSvn()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "locked.txt");
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner(CannedSvn(SvnStatusXml(
            $"<entry path=\"{file}\">" +
            "<wc-status item=\"normal\" props=\"normal\" revision=\"1\"><commit revision=\"1\"><author>ian</author></commit></wc-status>" +
            "<repos-status props=\"none\" item=\"none\"><lock><token>t</token><owner>bob</owner></lock></repos-status>" +
            "</entry>")));
        var st = (await VCLib.FileStatusAsync([file], remote: true))[0];
        Assert.Equal(["bob"], st.LockedBy);
    }

    [Fact]
    public async Task FileStatusAsyncSupportsRemoteForPlastic()
    {
        var a = Path.Combine(TestHelpers.MakeTempDir(), "a.txt");
        VCLib.SetProvider(new PlasticProvider());
        VCLib.SetCommandRunner(CannedCmRemote(
            status: $"CH \"{a}\"",
            fileinfo: "3;7;rep@srv;bob;bob-ws;/a.txt",
            whoami: "ian"));
        var st = (await VCLib.FileStatusAsync([a], remote: true))[0];
        Assert.True(st.OutOfDate);
        Assert.Equal(["bob@bob-ws"], st.LockedBy);
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

    // -- async write path ------------------------------------------------------

    [Fact]
    public async Task WriteTextFilesAsyncWritesBatchAndReportsSuccess()
    {
        VCLib.SetProvider(new FilesystemProvider());
        var dir = TestHelpers.MakeTempDir();
        var batch = await VCLib.WriteTextFilesAsync([
            new VCFileWrite(Path.Combine(dir, "sub", "a.txt"), "A"),
            new VCFileWrite(Path.Combine(dir, "deeper", "b.txt"), "B"),
        ]);
        Assert.True(batch.Success);
        Assert.Equal(2, batch.Results.Count);
        Assert.Equal("A", File.ReadAllText(Path.Combine(dir, "sub", "a.txt")));
    }

    [Fact]
    public async Task WriteTextFilesAsyncReportsRefusalAndKeepsGoing()
    {
        VCLib.SetProvider(new LockedFakeProvider());
        var dir = TestHelpers.MakeTempDir();
        var batch = await VCLib.WriteTextFilesAsync([
            new VCFileWrite(Path.Combine(dir, "locked.txt"), "x"),
            new VCFileWrite(Path.Combine(dir, "free.txt"), "y"),
        ]);
        Assert.False(batch.Success);
        var failure = batch.Results.Single(r => !r.Success);
        Assert.Equal(VCStatus.Locked, failure.Status);
        Assert.False(File.Exists(Path.Combine(dir, "locked.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "free.txt")));
    }

    [Fact]
    public async Task WriteTextFileAsyncRegistersNewFileWithRealGit()
    {
        var (dir, _, _) = MakeGitRepo();
        var file = Path.Combine(dir, "fresh.txt");
        var result = await VCLib.WriteTextFileAsync(file, "hello async");
        Assert.True(result.Success);
        Assert.Equal("hello async", File.ReadAllText(file));
        VCLib.ClearProvider();
        var st = (await VCLib.FileStatusAsync([file]))[0];
        Assert.True(st.Tracked);
    }

    [Fact]
    public async Task DeleteFileAsyncRemovesUntrackedFileViaFilesystem()
    {
        VCLib.SetProvider(new FilesystemProvider());
        var dir = TestHelpers.MakeTempDir();
        var file = Path.Combine(dir, "gone.txt");
        File.WriteAllText(file, "x");
        var result = await VCLib.DeleteFileAsync(file);
        Assert.True(result.Success);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task RenameFileAsyncRenamesTrackedFileViaRealGit()
    {
        var (dir, tracked, _) = MakeGitRepo();
        var renamed = Path.Combine(dir, "renamed.txt");
        var result = await VCLib.RenameFileAsync(tracked, renamed);
        Assert.True(result.Success);
        Assert.False(File.Exists(tracked));
        Assert.True(File.Exists(renamed));
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
        public IReadOnlyList<VCFileStatus> Status(IReadOnlyList<string> filePaths, bool remote = false) =>
            filePaths.Select(p => new VCFileStatus(Path.GetFullPath(p), Name, true)).ToList();
        public Task<IReadOnlyList<VCFileStatus>> StatusAsync(IReadOnlyList<string> filePaths, bool remote = false) =>
            Task.FromResult(Status(filePaths, remote));
    }
}

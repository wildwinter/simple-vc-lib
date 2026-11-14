using System.Diagnostics;
using SimpleVCLib;
using Xunit;

namespace SimpleVCLib.Tests;

// ---------------------------------------------------------------------------
// Ignored on file status, Perforce exclusive-open refusals, and the clean-up of a
// file a failed write created - mirrors js/test/ignored.test.js. Git runs against a
// real repository; Perforce, SVN and Plastic use canned transcripts taken from real
// p4 2026.1 and svn output (Plastic from the docs only).
// ---------------------------------------------------------------------------

public class IgnoredTests : IDisposable
{
    public void Dispose()
    {
        VCLib.ClearProvider();
        VCLib.ClearCommandRunner();
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static string MakeIgnoringRepo()
    {
        var dir = TestHelpers.MakeTempDir();
        TestHelpers.InitGitRepo(dir);
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "*.key\nsecret/\n");
        File.WriteAllText(Path.Combine(dir, "tracked.txt"), "t");
        Git(dir, "add", ".gitignore", "tracked.txt");
        Git(dir, "commit", "-m", "init");
        Directory.CreateDirectory(Path.Combine(dir, "secret", "deeper"));
        File.WriteAllText(Path.Combine(dir, "secret", "deeper", "service.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "a.key"), "k");
        File.WriteAllText(Path.Combine(dir, "plain.txt"), "p");
        return dir;
    }

    // -- git (real repository) ------------------------------------------------

    [Fact]
    public void GitReportsIgnoredByPatternAndByFolder()
    {
        var dir = MakeIgnoringRepo();
        VCLib.SetProvider(new GitProvider());
        var s = VCLib.FileStatus([
            Path.Combine(dir, "a.key"), Path.Combine(dir, "secret", "deeper", "service.json"),
            Path.Combine(dir, "plain.txt"), Path.Combine(dir, "tracked.txt"),
        ]);
        Assert.Equal((false, true, false), (s[0].Tracked, s[0].Ignored, s[0].Dirty));
        Assert.Equal((false, true, false), (s[1].Tracked, s[1].Ignored, s[1].Dirty));
        Assert.Equal((false, false), (s[2].Tracked, s[2].Ignored));
        Assert.Equal((true, false), (s[3].Tracked, s[3].Ignored));
    }

    [Fact]
    public async Task GitAsyncMatches()
    {
        var dir = MakeIgnoringRepo();
        VCLib.SetProvider(new GitProvider());
        var s = await VCLib.FileStatusAsync([Path.Combine(dir, "secret", "deeper", "service.json"), Path.Combine(dir, "plain.txt")]);
        Assert.True(s[0].Ignored);
        Assert.False(s[1].Ignored);
    }

    [Fact]
    public void GitLeavesIgnoredNullOutsideAnyRepository()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "x.key");
        File.WriteAllText(file, "x");
        VCLib.SetProvider(new GitProvider());
        var s = VCLib.FileStatus([file]);
        Assert.Null(s[0].Ignored);
        Assert.Null(s[0].Tracked);
    }

    // -- Perforce (canned) ----------------------------------------------------

    /// <summary>
    /// A canned p4: <c>-ztag fstat</c> answers with the given stdout/stderr (exit 0, as p4
    /// does for "no such file(s)"), <c>ignores</c> with the listed ignored paths.
    /// </summary>
    private static Func<string, string[], CommandResult> CannedP4(
        string fstatOut, string fstatErr, string[] ignored, List<string[]>? calls = null) =>
        (command, args) =>
        {
            calls?.Add(args);
            if (args.Length >= 2 && args[0] == "-ztag" && args[1] == "fstat") return new CommandResult(0, fstatOut, fstatErr);
            if (args[0] == "ignores")
                return new CommandResult(0, string.Join("\n", args.Skip(2).Where(ignored.Contains).Select(p => $"{p} ignored")), "");
            return new CommandResult(1, "", $"unexpected: {command} {string.Join(' ', args)}");
        };

    [Fact]
    public void PerforceReportsOutsideViewOrRootAsIgnoredWithoutAskingIgnores()
    {
        var calls = new List<string[]>();
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4("",
            "/ws/excl/e.txt - file(s) not in client view.\nPath '/home/me/key.json' is not under client's root '/ws'.", [], calls));
        var s = VCLib.FileStatus(["/ws/excl/e.txt", "/home/me/key.json"]);
        Assert.Equal((false, true), (s[0].Tracked, s[0].Ignored));
        Assert.Equal((false, true), (s[1].Tracked, s[1].Ignored));
        Assert.DoesNotContain(calls, a => a[0] == "ignores");
    }

    [Fact]
    public void PerforceChecksInViewUntrackedFilesInOneIgnoresCall()
    {
        var calls = new List<string[]>();
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(
            "... depotFile //depot/sanity.txt\n... clientFile /ws/sanity.txt\n... isMapped \n... headAction add\n... headType text\n... headRev 1\n... haveRev 1",
            "/ws/a.key - no such file(s).\n/ws/plain.txt - no such file(s).",
            ["/ws/a.key"], calls));
        var s = VCLib.FileStatus(["/ws/a.key", "/ws/plain.txt", "/ws/sanity.txt"]);
        Assert.Equal((false, true), (s[0].Tracked, s[0].Ignored));
        Assert.Equal((false, false), (s[1].Tracked, s[1].Ignored));
        Assert.Equal((true, false), (s[2].Tracked, s[2].Ignored));
        var ignoresCalls = calls.Where(a => a[0] == "ignores").ToList();
        Assert.Single(ignoresCalls);
        Assert.Equal(new[] { "ignores", "-i", "/ws/a.key", "/ws/plain.txt" }, ignoresCalls[0]);
    }

    [Fact]
    public async Task PerforceLeavesIgnoredNullWhenIgnoresFails()
    {
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner((command, args) => args[0] == "-ztag"
            ? new CommandResult(0, "", "/ws/a.key - no such file(s).")
            : new CommandResult(1, "", "boom"));
        var s = await VCLib.FileStatusAsync(["/ws/a.key"]);
        Assert.False(s[0].Tracked);
        Assert.Null(s[0].Ignored);
    }

    [Fact]
    public void PerforceReadsNestedOtherOpenFields()
    {
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(CannedP4(string.Join("\n",
            "... depotFile //depot/excl.bin", "... clientFile /ws/excl.bin", "... isMapped ",
            "... headAction add", "... headType binary+l", "... headRev 1", "... haveRev 1",
            "... ... otherOpen0 bob@bob-ws", "... ... otherAction0 edit", "... ... otherChange0 default",
            "... ... otherOpen 1"), "", []));
        var s = VCLib.FileStatus(["/ws/excl.bin"]);
        Assert.Equal(new[] { "bob@bob-ws" }, s[0].LockedBy!);
    }

    private static string ExclusiveFstat(string file) => string.Join("\n",
        $"... depotFile //depot/{Path.GetFileName(file)}", $"... clientFile {file}", "... isMapped ",
        "... headAction add", "... headType binary+l", "... headRev 1", "... haveRev 1",
        "... ... otherOpen0 bob@bob-ws", "... ... otherAction0 edit", "... ... otherOpen 1");

    [Fact]
    public async Task PerforceMapsRefusedExclusiveEditToLocked()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "excl.bin");
        File.WriteAllText(file, "x");
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner((command, args) => args[0] switch
        {
            "fstat" => new CommandResult(0, ExclusiveFstat(file), ""),
            "edit" => new CommandResult(0,
                "//depot/excl.bin - can't edit exclusive file already opened for edit\n... //depot/excl.bin - also opened by bob@bob-ws", ""),
            _ => new CommandResult(1, "", "unexpected"),
        });
        foreach (var result in new[] { VCLib.PrepareToWrite(file), await VCLib.PrepareToWriteAsync(file) })
        {
            Assert.Equal(VCStatus.Locked, result.Status);
            Assert.Contains("bob@bob-ws", result.Message);
        }
    }

    [Fact]
    public void PerforceMapsRefusedExclusiveAddToLocked()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "new.bin");
        File.WriteAllText(file, "x");
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner((command, args) => args[0] switch
        {
            "fstat" => new CommandResult(0, "", $"{file} - no such file(s)."),
            "add" => new CommandResult(0,
                "//depot/new.bin - can't add exclusive file already opened for add\n... //depot/new.bin - also opened by bob@bob-ws", ""),
            _ => new CommandResult(1, "", "unexpected"),
        });
        var result = VCLib.FinishedWrite(file);
        Assert.Equal(VCStatus.Locked, result.Status);
        Assert.Contains("bob@bob-ws", result.Message);
    }

    [Fact]
    public async Task PerforceRefusesToDeleteExclusiveFileOpenedElsewhere()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "excl.bin");
        File.WriteAllText(file, "x");
        var calls = new List<string>();
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner((command, args) =>
        {
            lock (calls) calls.Add(args[0]);
            return args[0] == "fstat" ? new CommandResult(0, ExclusiveFstat(file), "") : new CommandResult(1, "", "unexpected");
        });
        Assert.Equal(VCStatus.Locked, VCLib.DeleteFile(file).Status);
        Assert.Equal(VCStatus.Locked, (await VCLib.DeleteFileAsync(file)).Status);
        Assert.True(File.Exists(file));
        Assert.Equal(new List<string> { "fstat", "fstat" }, calls);
    }

    [Fact]
    public async Task PerforceRefusedAddThroughWriteRemovesTheNewFile()
    {
        var dir = TestHelpers.MakeTempDir();
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner((command, args) => args[0] switch
        {
            "fstat" => new CommandResult(0, "", $"{args[^1]} - no such file(s)."),
            "add" => new CommandResult(0,
                "//depot/x - can't add exclusive file already opened for add\n... //depot/x - also opened by bob@bob-ws", ""),
            _ => new CommandResult(1, "", "unexpected"),
        });
        var file = Path.Combine(dir, "take.wav");
        var result = VCLib.WriteBinaryFile(file, [1, 2, 3]);
        Assert.Equal(VCStatus.Locked, result.Status);
        Assert.Contains("removed", result.Message);
        Assert.False(File.Exists(file));

        var fileAsync = Path.Combine(dir, "take2.wav");
        var asyncResult = await VCLib.WriteTextFileAsync(fileAsync, "audio");
        Assert.Equal(VCStatus.Locked, asyncResult.Status);
        Assert.False(File.Exists(fileAsync));
    }

    // -- SVN (canned) ---------------------------------------------------------

    private static string Entry(string path, string item) =>
        $"<entry path=\"{path}\"><wc-status props=\"none\" item=\"{item}\"></wc-status></entry>";

    private static string Xml(params string[] entries) =>
        $"<?xml version=\"1.0\"?>\n<status>\n<target path=\"/ws/wc\">\n{string.Join("\n", entries)}\n</target>\n</status>";

    [Fact]
    public void SvnReportsIgnoredUnversionedAndNormal()
    {
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner((_, _) => new CommandResult(0,
            Xml(Entry("/ws/wc/a.key", "ignored"), Entry("/ws/wc/plain.txt", "unversioned"), Entry("/ws/wc/t.txt", "normal")), ""));
        var s = VCLib.FileStatus(["/ws/wc/a.key", "/ws/wc/plain.txt", "/ws/wc/t.txt"]);
        Assert.Equal((false, true), (s[0].Tracked, s[0].Ignored));
        Assert.Equal((false, false), (s[1].Tracked, s[1].Ignored));
        Assert.Equal((true, false), (s[2].Tracked, s[2].Ignored));
    }

    /// <summary>A fake working copy on disk (a root with <c>.svn</c>), holding the given file.</summary>
    private static (string Wc, string File) WorkingCopyWith(params string[] rel)
    {
        var wc = Path.GetFullPath(TestHelpers.MakeTempDir());
        Directory.CreateDirectory(Path.Combine(wc, ".svn"));
        var file = Path.Combine([wc, .. rel]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "k");
        return (wc, file);
    }

    [Fact]
    public void SvnPlacesAFileInsideAnIgnoredFolderByItsFoldersUpToTheRoot()
    {
        var (wc, file) = WorkingCopyWith("sec", "deep", "k.json");
        var calls = new List<string[]>();
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner((_, args) =>
        {
            calls.Add(args);
            return args.Contains("--depth")
                ? new CommandResult(0, Xml(Entry(Path.Combine(wc, "sec"), "ignored"), Entry(wc, "normal")), "")
                : new CommandResult(1, Xml(), "svn: warning: W155010: The node was not found.");
        });
        var s = VCLib.FileStatus([file]);
        Assert.Equal((false, true), (s[0].Tracked, s[0].Ignored));
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "status", "--xml", "-v", "--depth", "empty", Path.Combine(wc, "sec", "deep"), Path.Combine(wc, "sec"), wc }, calls[1]);
    }

    [Fact]
    public async Task SvnFileUnderUnversionedFolderIsUntrackedNotIgnored()
    {
        var (wc, file) = WorkingCopyWith("newdir", "n.txt");
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner((_, args) => args.Contains("--depth")
            ? new CommandResult(0, Xml(Entry(Path.Combine(wc, "newdir"), "unversioned")), "")
            : new CommandResult(1, Xml(), "W155010"));
        var s = await VCLib.FileStatusAsync([file]);
        Assert.Equal((false, false), (s[0].Tracked, s[0].Ignored));
    }

    [Fact]
    public void SvnMakesNoSecondReadForMissingOrOutsideFiles()
    {
        var outside = Path.Combine(TestHelpers.MakeTempDir(), "x.txt");
        File.WriteAllText(outside, "x");
        var calls = 0;
        VCLib.SetProvider(new SvnProvider());
        VCLib.SetCommandRunner((_, _) => { calls++; return new CommandResult(1, Xml(), "W155010"); });
        VCLib.FileStatus(["/ws/wc/not-yet.txt", outside]);
        Assert.Equal(1, calls);
    }

    // -- Plastic (canned, from the docs only) and filesystem ------------------

    [Fact]
    public void PlasticReportsIgAsIgnoredAndPrAsNot()
    {
        VCLib.SetProvider(new PlasticProvider());
        VCLib.SetCommandRunner((_, _) => new CommandResult(0,
            "STATUS 23 /main rep:default@server\nIG;/ws/wc/a.key;False;NO_MERGES\nPR;/ws/wc/new.txt;False;NO_MERGES", ""));
        var s = VCLib.FileStatus(["/ws/wc/a.key", "/ws/wc/new.txt"]);
        Assert.Equal((false, true), (s[0].Tracked, s[0].Ignored));
        Assert.Equal((false, false), (s[1].Tracked, s[1].Ignored));
    }

    [Fact]
    public void FilesystemLeavesIgnoredNull()
    {
        VCLib.SetProvider(new FilesystemProvider());
        Assert.Null(VCLib.FileStatus([Path.Combine(TestHelpers.MakeTempDir(), "x.key")])[0].Ignored);
    }

    // -- A failed write removes a file it created, and only that ---------------

    /// <summary>The real filesystem provider, whose FinishedWrite always fails with the given status.</summary>
    private sealed class RefusingFinish(VCStatus status = VCStatus.Locked) : IVCProvider
    {
        private readonly FilesystemProvider _real = new();
        private readonly VCResult Refused = VCResult.Failure(status, "add refused");
        public string Name => _real.Name;
        public VCResult PrepareToWrite(string filePath) => _real.PrepareToWrite(filePath);
        public Task<VCResult> PrepareToWriteAsync(string filePath) => _real.PrepareToWriteAsync(filePath);
        public VCResult UndoPrepareToWrite(string filePath, VCFileStatus before) => _real.UndoPrepareToWrite(filePath, before);
        public Task<VCResult> UndoPrepareToWriteAsync(string filePath, VCFileStatus before) => _real.UndoPrepareToWriteAsync(filePath, before);
        public VCResult FinishedWrite(string filePath) => Refused;
        public Task<VCResult> FinishedWriteAsync(string filePath) => Task.FromResult(Refused);
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
    public async Task FailedWriteKeepsANewFileWhenNotLocked()
    {
        var dir = TestHelpers.MakeTempDir();
        VCLib.SetProvider(new RefusingFinish(VCStatus.Error));
        Assert.False(VCLib.WriteTextFile(Path.Combine(dir, "a.txt"), "x").Success);
        Assert.False((await VCLib.WriteTextFileAsync(Path.Combine(dir, "b.txt"), "y")).Success);
        Assert.Equal("x", File.ReadAllText(Path.Combine(dir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "b.txt")));
    }

    [Fact]
    public void FailedWriteRemovesTheFileItCreated()
    {
        var dir = TestHelpers.MakeTempDir();
        VCLib.SetProvider(new RefusingFinish());
        Assert.False(VCLib.WriteTextFile(Path.Combine(dir, "a.txt"), "x").Success);
        Assert.False(VCLib.WriteBinaryFile(Path.Combine(dir, "b.bin"), [1, 2]).Success);
        Assert.False(File.Exists(Path.Combine(dir, "a.txt")));
        Assert.False(File.Exists(Path.Combine(dir, "b.bin")));
    }

    [Fact]
    public async Task FailedWriteNeverRemovesAFileThatWasThereBefore()
    {
        var file = Path.Combine(TestHelpers.MakeTempDir(), "existing.txt");
        File.WriteAllText(file, "old");
        VCLib.SetProvider(new RefusingFinish());
        Assert.False(VCLib.WriteTextFile(file, "new").Success);
        Assert.True(File.Exists(file));
        Assert.False((await VCLib.WriteTextFileAsync(file, "newer")).Success);
        Assert.Equal("newer", File.ReadAllText(file));
    }

    [Fact]
    public async Task FailedAllOrNothingWriteRemovesTheFileItCreated()
    {
        var dir = TestHelpers.MakeTempDir();
        VCLib.SetProvider(new RefusingFinish());
        var batch = VCLib.WriteTextFiles([new VCFileWrite(Path.Combine(dir, "n.txt"), "x")], allOrNothing: true);
        Assert.False(batch.Success);
        Assert.False(File.Exists(Path.Combine(dir, "n.txt")));
        var asyncBatch = await VCLib.WriteTextFilesAsync([new VCFileWrite(Path.Combine(dir, "m.txt"), "x")], allOrNothing: true);
        Assert.False(asyncBatch.Success);
        Assert.False(File.Exists(Path.Combine(dir, "m.txt")));
    }
}

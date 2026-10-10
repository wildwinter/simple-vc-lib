using SimpleVCLib;
using Xunit;

namespace SimpleVCLib.Tests;

// ---------------------------------------------------------------------------
// Plastic SCM writes against a simulated `cm`: a workspace with read-only files
// and a lock rule, so checking out a file that is already checked out fails as
// a real exclusive checkout does. Mirrors js/test/plastic.test.js.
// ---------------------------------------------------------------------------

public class PlasticProviderTests : IDisposable
{
    public PlasticProviderTests() => VCLib.SetProvider(new PlasticProvider());

    public void Dispose()
    {
        VCLib.ClearProvider();
        VCLib.ClearCommandRunner();
    }

    private static bool IsReadOnly(string filePath) => new FileInfo(filePath).IsReadOnly;

    /// <summary>A committed, unchanged, read-only file, as a workspace with "files read-only" holds it.</summary>
    private static string ControlledFile(string dir, string name)
    {
        var filePath = Path.Combine(dir, name);
        File.WriteAllText(filePath, "old");
        new FileInfo(filePath).IsReadOnly = true;
        return filePath;
    }

    /// <summary>
    /// A fake <c>cm</c> over one workspace. <paramref name="states"/> maps absolute paths to a
    /// status code ("CO", "CH", "PR", ...); a path with no entry is controlled and unchanged, so
    /// <c>cm status</c> does not list it. Paths outside <paramref name="root"/> are outside the workspace.
    /// </summary>
    private static List<string> FakeCm(string root, Dictionary<string, string>? states = null)
    {
        states ??= [];
        var calls = new List<string>();
        var rootAbs = Path.GetFullPath(root);
        VCLib.SetCommandRunner((command, args) =>
        {
            calls.Add($"{command} {args[0]}");
            var paths = args.Skip(1).Where(a => !a.StartsWith("--")).Select(Path.GetFullPath).ToList();
            if (paths.Any(p => !p.StartsWith(rootAbs)))
                return new CommandResult(1, "", "The path is not in a workspace.");
            switch (args[0])
            {
                case "status":
                    // The real machine format (as the Unreal Plastic plugin reads it): code;path;merge flags.
                    if (!args.Contains("--fieldseparator=;")) return new CommandResult(1, "", "expected --fieldseparator=;");
                    var lines = paths.Where(states.ContainsKey).Select(p => $"{states[p]};{p};False;NO_MERGES");
                    return new CommandResult(0, string.Join('\n', ["STATUS;7;game;local", .. lines]), "");
                case "co":
                    if (states.TryGetValue(paths[0], out var code) && code == "CO")
                        return new CommandResult(1, "", $"The item {paths[0]} is exclusively checked out by you in this workspace.");
                    states[paths[0]] = "CO";
                    new FileInfo(paths[0]).IsReadOnly = false;
                    return new CommandResult(0, $"Checking out {paths[0]}", "");
                case "add":
                    states[paths[0]] = "AD";
                    return new CommandResult(0, "", "");
                case "remove":
                    return new CommandResult(0, "", "");
                default:
                    return new CommandResult(1, "", $"unexpected: cm {string.Join(' ', args)}");
            }
        });
        return calls;
    }

    [Fact]
    public void ChecksOutAnUnchangedControlledFileBeforeTheFirstWrite()
    {
        var root = TestHelpers.MakeTempDir();
        var file = ControlledFile(root, "scene.patterx");
        var calls = FakeCm(root);

        var result = VCLib.WriteTextFile(file, "new");
        Assert.True(result.Success, result.Message);
        Assert.Contains("cm co", calls);
    }

    [Fact]
    public async Task SavesAFileAgainAfterCheckingItOutWithoutASecondCheckout()
    {
        var root = TestHelpers.MakeTempDir();
        var file = ControlledFile(root, "scene.patterx");
        var states = new Dictionary<string, string>();
        var calls = FakeCm(root, states);

        Assert.True(VCLib.WriteTextFile(file, "first").Success);
        Assert.Equal("CO", states[Path.GetFullPath(file)]);

        var second = VCLib.WriteTextFile(file, "second");
        Assert.True(second.Success, second.Message);
        var third = await VCLib.WriteTextFileAsync(file, "third");
        Assert.True(third.Success, third.Message);
        Assert.Single(calls, c => c == "cm co");
    }

    [Fact]
    public void SavesABatchOfAlreadyCheckedOutFilesAllOrNothing()
    {
        var root = TestHelpers.MakeTempDir();
        var a = ControlledFile(root, "a.patterx");
        var b = ControlledFile(root, "b.patterflow");
        FakeCm(root);

        Assert.True(VCLib.WriteTextFiles([new(a, "1"), new(b, "1")], allOrNothing: true).Success);
        var again = VCLib.WriteTextFiles([new(a, "2"), new(b, "2")], allOrNothing: true);
        Assert.True(again.Success, string.Join("; ", again.Results.Select(r => r.Message)));
    }

    [Fact]
    public void ReadsACombinedCoChCodeAsCheckedOut()
    {
        var root = TestHelpers.MakeTempDir();
        var file = ControlledFile(root, "scene.patterx");
        new FileInfo(file).IsReadOnly = false;
        var calls = FakeCm(root, new() { [Path.GetFullPath(file)] = "CO+CH" });

        Assert.True(VCLib.PrepareToWrite(file).Success);
        Assert.DoesNotContain("cm co", calls);
    }

    [Fact]
    public void ChecksOutAFileChangedWithoutACheckoutSoItTakesTheLock()
    {
        var root = TestHelpers.MakeTempDir();
        var file = ControlledFile(root, "scene.patterx");
        var calls = FakeCm(root, new() { [Path.GetFullPath(file)] = "CH" });

        Assert.True(VCLib.PrepareToWrite(file).Success);
        Assert.Contains("cm co", calls);
    }

    [Fact]
    public void StillRefusesAFileSomeoneElseHolds()
    {
        var file = ControlledFile(TestHelpers.MakeTempDir(), "scene.patterx");
        VCLib.SetCommandRunner((command, args) => args[0] == "co"
            ? new CommandResult(1, "", "The item is exclusively checked out by sam@laptop.")
            : new CommandResult(0, "", ""));

        var result = VCLib.PrepareToWrite(file);
        Assert.False(result.Success);
        Assert.Equal(VCStatus.Locked, result.Status);
    }

    [Fact]
    public void AddsANewPrivateFileAfterWritingItAndLeavesIgnoredOnesAlone()
    {
        var root = TestHelpers.MakeTempDir();
        var fresh = Path.Combine(root, "new.patterflow");
        var ignored = Path.Combine(root, "ignored.patterc");
        // cm lists a file it has never seen as private once it is on disk
        var states = new Dictionary<string, string> { [Path.GetFullPath(fresh)] = "PR", [Path.GetFullPath(ignored)] = "IG" };
        var calls = FakeCm(root, states);

        Assert.True(VCLib.WriteTextFile(fresh, "x").Success);
        Assert.Equal("AD", states[Path.GetFullPath(fresh)]);
        Assert.True(VCLib.WriteTextFile(ignored, "x").Success);
        Assert.Single(calls, c => c == "cm add");
    }

    [Fact]
    public void OnlyMakesAFileOutsideAnyWorkspaceWritable()
    {
        var root = TestHelpers.MakeTempDir();
        var outside = ControlledFile(TestHelpers.MakeTempDir(), "loose.txt");
        var calls = FakeCm(root);

        Assert.True(VCLib.WriteTextFile(outside, "x").Success);
        Assert.False(IsReadOnly(outside));
        Assert.DoesNotContain("cm co", calls);
    }

    [Fact]
    public void DeletesAnUnchangedControlledFileWithCmRemove()
    {
        var root = TestHelpers.MakeTempDir();
        var file = ControlledFile(root, "scene.patterx");
        var calls = FakeCm(root);

        Assert.True(VCLib.DeleteFile(file).Success);
        Assert.Contains("cm remove", calls);
    }

    [Fact]
    public void MatchesAFolderByItsOwnLineNotTheFirstLineOfItsListing()
    {
        var folder = Path.Combine(TestHelpers.MakeTempDir(), "scenes");
        Directory.CreateDirectory(folder);
        var file = ControlledFile(folder, "a.patterflow");
        var calls = new List<string>();
        VCLib.SetCommandRunner((command, args) =>
        {
            calls.Add($"{command} {args[0]}");
            return args[0] == "status"
                ? new CommandResult(0, $"PR;{Path.Combine(folder, "scratch.txt")};False;NO_MERGES\nCO;{file};False;NO_MERGES", "")
                : new CommandResult(0, "", "");
        });

        // the folder itself is unlisted, so controlled; its private child must not make it private
        Assert.True(new PlasticProvider().DeleteFolder(folder).Success);
        Assert.Contains("cm remove", calls);
    }

    // -- The lines a real cm prints, from the Unreal Plastic plugin's own examples. The first version assumed
    // quoted paths, so on a real workspace no line matched its file and every save checked out again. -----

    private static List<string> Answering(string output)
    {
        var calls = new List<string>();
        VCLib.SetCommandRunner((command, args) =>
        {
            calls.Add($"{command} {args[0]}");
            return args[0] == "status" ? new CommandResult(0, output, "") : new CommandResult(0, "", "");
        });
        return calls;
    }

    [Fact]
    public void ReadsACheckedOutFileWithSpacesAndMergeFlagsAsCheckedOut()
    {
        var file = ControlledFile(TestHelpers.MakeTempDir(), "my scene.patterx");
        var calls = Answering($"STATUS;41;UEPlasticPluginDev;localhost:8087\nCO;{Path.GetFullPath(file)};False;NO_MERGES");
        Assert.True(VCLib.PrepareToWrite(file).Success);
        Assert.DoesNotContain("cm co", calls);
    }

    [Fact]
    public void ReadsCoChAndChApart()
    {
        var file = ControlledFile(TestHelpers.MakeTempDir(), "scene.patterx");
        var calls = Answering($"CO+CH;{Path.GetFullPath(file)};False;NO_MERGES");
        Assert.True(VCLib.PrepareToWrite(file).Success);
        Assert.DoesNotContain("cm co", calls);
        calls = Answering($"CH;{Path.GetFullPath(file)};False;NO_MERGES");
        Assert.True(VCLib.PrepareToWrite(file).Success);
        Assert.Contains("cm co", calls);
    }

    [Fact]
    public void ReadsAMoveByItsDestinationWithOrWithoutTheFlags()
    {
        var dir = TestHelpers.MakeTempDir();
        var moved = ControlledFile(dir, "moved.patterx");
        foreach (var tail in new[] { ";False;NO_MERGES", "" })
        {
            var calls = Answering($"MV;100%;{Path.Combine(dir, "old.patterx")};{Path.GetFullPath(moved)}{tail}");
            Assert.True(VCLib.PrepareToWrite(moved).Success);
            Assert.DoesNotContain("cm co", calls);
        }
    }

    [Fact]
    public void KeepsAPathWithTheSeparatorInItWhole()
    {
        var file = ControlledFile(TestHelpers.MakeTempDir(), "a;b.patterx");
        var calls = Answering($"CO;{Path.GetFullPath(file)};False;NO_MERGES");
        Assert.True(VCLib.PrepareToWrite(file).Success);
        Assert.DoesNotContain("cm co", calls);
    }

    [Fact]
    public void StillReadsALineWithNoSeparatorAsCodeAndPath()
    {
        var file = ControlledFile(TestHelpers.MakeTempDir(), "scene.patterx");
        var calls = Answering($"CO {Path.GetFullPath(file)} False NO_MERGES");
        Assert.True(VCLib.PrepareToWrite(file).Success);
        Assert.DoesNotContain("cm co", calls);
    }
}

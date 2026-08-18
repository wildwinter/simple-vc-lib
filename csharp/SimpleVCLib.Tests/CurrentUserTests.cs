using SimpleVCLib;
using Xunit;

namespace SimpleVCLib.Tests;

// ---------------------------------------------------------------------------
// CurrentUser / CurrentUserAsync - mirrors js/test/currentuser.test.js, case for
// case, so the two ports cannot answer this differently.
//
// The point of it is SEEDING an identity box the person can then change, so the
// failure that matters is a confident WRONG answer rather than a missing one.
// Every provider that cannot say returns null, and these pin which ones those are.
// ---------------------------------------------------------------------------

public class CurrentUserTests : IDisposable
{
    public void Dispose()
    {
        VCLib.ClearProvider();
        VCLib.ClearCommandRunner();
    }

    /// <summary>Answers one command and refuses everything else, so a test cannot pass by accident.</summary>
    private static Func<string, string[], CommandResult> Canned(string command, string argsPrefix, string output, int exitCode = 0) =>
        (cmd, args) =>
        {
            var a = args.Length >= 2 && args[0] == "-C" ? args[2..] : args; // git prefixes -C <cwd>
            var joined = string.Join(" ", a);
            if (cmd == command && joined.StartsWith(argsPrefix, StringComparison.Ordinal))
                return new CommandResult(exitCode, output, "");
            return new CommandResult(1, "", $"unexpected: {cmd} {joined}");
        };

    [Fact]
    public void FilesystemHasNoUsers()
    {
        VCLib.SetProvider(new FilesystemProvider());
        Assert.Null(VCLib.CurrentUser());
    }

    [Fact]
    public void GitReportsUserName()
    {
        VCLib.SetProvider(new GitProvider());
        VCLib.SetCommandRunner(Canned("git", "config user.name", "Ada Lovelace\n"));
        Assert.Equal("Ada Lovelace", VCLib.CurrentUser());
    }

    [Fact]
    public void GitWithNoConfiguredNameIsNullNotEmpty()
    {
        // A fresh install with no global config. An empty box beats a user called "".
        VCLib.SetProvider(new GitProvider());
        VCLib.SetCommandRunner(Canned("git", "config user.name", "\n", exitCode: 1));
        Assert.Null(VCLib.CurrentUser());
    }

    [Fact]
    public void PerforceReportsTheUserNameFromInfo()
    {
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(Canned("p4", "info",
            "User name: alovelace\nClient name: ada_ws\nServer address: perforce:1666\n"));
        Assert.Equal("alovelace", VCLib.CurrentUser());
    }

    [Fact]
    public void PerforceIsNullWhenUnconfigured()
    {
        VCLib.SetProvider(new PerforceProvider());
        VCLib.SetCommandRunner(Canned("p4", "info", "", exitCode: 1));
        Assert.Null(VCLib.CurrentUser());
    }

    [Fact]
    public void PlasticReportsWhoami()
    {
        VCLib.SetProvider(new PlasticProvider());
        VCLib.SetCommandRunner(Canned("cm", "whoami", "ada@studio\n"));
        Assert.Equal("ada@studio", VCLib.CurrentUser());
    }

    [Fact]
    public void SvnNeverLearnsAName()
    {
        // It tells our lock from someone else's by comparing lock TOKENS, so there is no username in
        // the provider to expose. Guessing one would be worse than saying nothing.
        VCLib.SetProvider(new SvnProvider());
        Assert.Null(VCLib.CurrentUser());
    }

    [Fact]
    public async Task AsyncGivesTheSameAnswer()
    {
        VCLib.SetProvider(new GitProvider());
        VCLib.SetCommandRunner(Canned("git", "config user.name", "Ada Lovelace\n"));
        Assert.Equal("Ada Lovelace", await VCLib.CurrentUserAsync());
    }

    [Fact]
    public async Task AsyncIsNullForAProviderThatCannotSay()
    {
        VCLib.SetProvider(new FilesystemProvider());
        Assert.Null(await VCLib.CurrentUserAsync());
    }
}

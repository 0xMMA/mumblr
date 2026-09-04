namespace Mumblr.App.Tests;

public class ProgramTests
{
    // The test host runs *from* the build output, so the real application directory is the working
    // directory here and the install-folder guard would fire on every case. These tests are about
    // argument parsing, so they name an application directory that is somewhere else entirely.
    private static readonly string Elsewhere = Path.Combine(Path.GetTempPath(), "mumblr-install", "current");

    private static string Resolve(string[] args, string? currentDirectory = null) =>
        Program.ResolveTargetDirectory(args, currentDirectory ?? Directory.GetCurrentDirectory(), Elsewhere);

    [Fact]
    public void A_dot_means_the_current_directory()
    {
        Resolve(["."]).ShouldBe(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void No_argument_means_the_current_directory()
    {
        Resolve([]).ShouldBe(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void An_explicit_folder_is_resolved_to_an_absolute_path()
    {
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var resolved = Resolve([temp]);

        Path.IsPathRooted(resolved).ShouldBeTrue();
        resolved.TrimEnd(Path.DirectorySeparatorChar).ShouldBe(temp);
    }

    [Fact]
    public void Velopack_and_other_switches_are_not_mistaken_for_a_folder()
    {
        Resolve(["--veloapp-install", "1.0.0"]).ShouldBe(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void The_install_folder_is_never_the_target()
    {
        // Started from the shortcut, the working directory is the Velopack `current` folder, which
        // the next update replaces wholesale - a dictation written there is deleted by the first
        // update that lands.
        var app = Path.Combine(Path.GetTempPath(), "mumblr-app", "current");

        var resolved = Program.ResolveTargetDirectory([], currentDirectory: app, applicationDirectory: app);

        resolved.ShouldNotBe(app);
        Directory.Exists(resolved).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_below_the_install_folder_is_refused_too()
    {
        var app = Path.Combine(Path.GetTempPath(), "mumblr-app", "current");
        var inside = Path.Combine(app, "notes");

        Program.ResolveTargetDirectory([inside], currentDirectory: app, applicationDirectory: app)
            .ShouldNotBe(inside);
    }

    [Fact]
    public void A_folder_that_merely_starts_with_the_same_characters_is_still_allowed()
    {
        // "current-notes" is a sibling of "current", not a child of it.
        var app = Path.Combine(Path.GetTempPath(), "mumblr-app", "current");
        var sibling = Path.Combine(Path.GetTempPath(), "mumblr-app", "current-notes");

        Program.ResolveTargetDirectory([sibling], currentDirectory: app, applicationDirectory: app)
            .ShouldBe(sibling);
    }

    [Fact]
    public void An_ordinary_working_directory_is_left_alone()
    {
        var repo = Path.Combine(Path.GetTempPath(), "some-repo");
        var app = Path.Combine(Path.GetTempPath(), "mumblr-app", "current");

        Program.ResolveTargetDirectory([], currentDirectory: repo, applicationDirectory: app)
            .ShouldBe(repo);
    }
}

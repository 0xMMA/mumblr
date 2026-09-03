namespace Mumblr.App.Tests;

public class ProgramTests
{
    [Fact]
    public void A_dot_means_the_current_directory()
    {
        Program.ResolveTargetDirectory(["."]).ShouldBe(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void No_argument_means_the_current_directory()
    {
        Program.ResolveTargetDirectory([]).ShouldBe(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void An_explicit_folder_is_resolved_to_an_absolute_path()
    {
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var resolved = Program.ResolveTargetDirectory([temp]);

        Path.IsPathRooted(resolved).ShouldBeTrue();
        resolved.TrimEnd(Path.DirectorySeparatorChar).ShouldBe(temp);
    }

    [Fact]
    public void Velopack_and_other_switches_are_not_mistaken_for_a_folder()
    {
        Program.ResolveTargetDirectory(["--veloapp-install", "1.0.0"]).ShouldBe(Directory.GetCurrentDirectory());
    }
}

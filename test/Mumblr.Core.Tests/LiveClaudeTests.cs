using System.Text.RegularExpressions;
using Mumblr.Core.Commands;
using Mumblr.Core.Config;

namespace Mumblr.Core.Tests;

/// <summary>
/// The only tests that spawn the real <c>claude</c>. The rest of the suite asserts what mumblr
/// sends; these assert what comes back when the shipped prompts meet a real model, which is
/// the only way to see whether an English instruction over German dictation stays German.
///
/// Opt in explicitly - each run is a real Opus call on your subscription:
///
///     MUMBLR_LIVE_TESTS=1 dotnet test --filter FullyQualifiedName~LiveClaude
///
/// The assertions are deliberately weak: a model's output is not stable, the invariants are.
/// </summary>
public class LiveClaudeTests
{
    private const string OptIn = "MUMBLR_LIVE_TESTS";

    private readonly ITestOutputHelper output;

    public LiveClaudeTests(ITestOutputHelper output) => this.output = output;

    private const string GermanDictation =
        "Also ich hab mir überlegt dass wir das Feature mit den Vertical Slices anders bauen " +
        "sollten weil das mit dem Aspire Dashboard und OpenTelemetry funktioniert ja eigentlich " +
        "schon ganz gut aber die Handler sind halt viel zu fett geworden. Wir müssten die " +
        "Commands vom Query Teil trennen und dann macht Claude Code das Refactoring vielleicht " +
        "auch einfacher wenn die Ordner klarer sind.";

    /// <summary>
    /// Ten sentences of thinking out loud: fillers, one repetition (the folder structure twice),
    /// one contradiction (Aspire is not needed / the Aspire dashboard is required) and one gap
    /// (which endpoints, unsaid).
    /// </summary>
    private const string RamblingDictation =
        "Also ähm ich glaube wir sollten den Order Service auf Vertical Slices umbauen. " +
        "Jeder Slice bekommt seinen eigenen Ordner mit Command, Handler und Endpoint. " +
        "Das mit Aspire brauchen wir dafür eigentlich nicht, das können wir erstmal weglassen. " +
        "Die Tests sollen mit Shouldly geschrieben werden, nicht mit FluentAssertions. " +
        "Ach ja und die Ordnerstruktur, also jeder Slice in seinem eigenen Ordner, das ist wichtig. " +
        "Die alten Endpoints müssen dabei weiter funktionieren, zumindest die wichtigen. " +
        "Wichtig ist auch dass das Aspire Dashboard am Ende läuft, sonst sehen wir die Traces nicht. " +
        "Migrations bitte nicht anfassen, die Datenbank bleibt wie sie ist. " +
        "Und ähm keine neuen NuGet Pakete ohne Rückfrage. " +
        "Das wäre es erstmal, ich glaube das reicht für den ersten Schritt.";

    [Fact]
    public async Task The_prompt_command_shapes_German_dictation_into_a_German_prompt_with_open_questions()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(OptIn) is "1", $"{OptIn}=1 arms this test.");

        var cancellation = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("mumblr-live-");
        try
        {
            var file = Path.Combine(directory.FullName, "dictated.md");
            await File.WriteAllTextAsync(file, RamblingDictation + Environment.NewLine, cancellation);

            var prompt = new MumblrConfig().PrebuiltCommands.Single(command => command.Label == "Prompt");
            var result = await new ClaudeCommandRunner(() => new ClaudeConfig()).RunAsync(prompt.Text, file, cancellation);
            var shaped = await File.ReadAllTextAsync(file, cancellation);

            output.WriteLine("IN:  " + RamblingDictation);
            output.WriteLine("OUT:\n" + shaped.Trim());
            output.WriteLine("SUMMARY: " + result.Summary);
            output.WriteLine("MODEL: " + result.Model);

            result.Success.ShouldBeTrue(result.Summary);

            // The terms and the language survive.
            shaped.ShouldContain("Vertical Slice");
            shaped.ShouldContain("Shouldly");
            shaped.ShouldContain("Aspire");
            Regex.IsMatch(shaped, @"\bthe\b").ShouldBeFalse();

            // The contradiction and the gap come back as questions, not as decisions.
            Regex.IsMatch(shaped, @"Open questions|Offene Fragen", RegexOptions.IgnoreCase).ShouldBeTrue();
            shaped.ShouldContain("?");
            shaped.ShouldNotContain("<");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_grammar_command_returns_German_with_the_English_terms_intact()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(OptIn) is "1", $"{OptIn}=1 arms this test.");

        var cancellation = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("mumblr-live-");
        try
        {
            var file = Path.Combine(directory.FullName, "dictated.md");
            await File.WriteAllTextAsync(file, GermanDictation + Environment.NewLine, cancellation);

            var grammar = new MumblrConfig().PrebuiltCommands.Single(command => command.Label == "Grammar");
            var result = await new ClaudeCommandRunner(() => new ClaudeConfig()).RunAsync(grammar.Text, file, cancellation);
            var edited = await File.ReadAllTextAsync(file, cancellation);

            output.WriteLine("IN:  " + GermanDictation);
            output.WriteLine("OUT: " + edited.Trim());
            output.WriteLine("SUMMARY: " + result.Summary);
            output.WriteLine("MODEL: " + result.Model);

            result.Success.ShouldBeTrue(result.Summary);
            edited.Trim().ShouldNotBe(GermanDictation);

            // The terms the author used in English stay English.
            edited.ShouldContain("Vertical Slice");
            edited.ShouldContain("OpenTelemetry");
            edited.ShouldContain("Claude Code");
            edited.ShouldContain("Aspire");

            // And the text around them stays German.
            Regex.IsMatch(edited, @"\bund\b").ShouldBeTrue();
            Regex.IsMatch(edited, @"\bdie\b").ShouldBeTrue();
            Regex.IsMatch(edited, @"\bthe\b").ShouldBeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

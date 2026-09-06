using Mumblr.Core.Config;
using Mumblr.Core.Text;

namespace Mumblr.Core.Tests;

/// <summary>
/// What <see cref="ConfigStore.Load"/> makes of a file someone edited by hand or an older build
/// wrote. Two rules: a null means the default for that key and costs nothing else, and a stored
/// value that still equals an older shipped default follows the current one.
/// </summary>
public sealed class ConfigMigrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mumblr-migration-{Guid.NewGuid():N}");
    private readonly string path;

    public ConfigMigrationTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "config.json");
    }

    private MumblrConfig Load(string json)
    {
        File.WriteAllText(path, json);
        return new ConfigStore(path).Load();
    }

    // ---------------------------------------------------------------- nulls

    [Theory]
    // Reference types.
    [InlineData("""{"claude":null}""")]
    [InlineData("""{"claude":{"headerPrompt":null}}""")]
    [InlineData("""{"prebuiltCommands":null}""")]
    [InlineData("""{"stt":{"languages":null},"keyterms":null,"dictionary":null,"hotkeys":null}""")]
    // Value types. These throw inside the deserializer, which used to cost the whole file.
    [InlineData("""{"sttMode":null}""")]
    [InlineData("""{"hotkeys":{"enabled":null}}""")]
    [InlineData("""{"stt":{"noVerbatim":null,"vadSilenceThresholdSecs":null}}""")]
    [InlineData("""{"claude":{"timeoutSeconds":null,"safeMode":null,"restricted":null,"useJsonSchema":null}}""")]
    public void A_hand_edited_null_means_the_default_and_keeps_every_other_setting(string json)
    {
        // The Config button invites hand editing, and "x": null is what an editor leaves behind.
        // Losing that one key to a default is fine; losing the file is not.
        var config = Load(json.Insert(1, "\"microphoneDeviceId\":\"dev-1\","));

        config.MicrophoneDeviceId.ShouldBe("dev-1");
        config.Claude.HeaderPrompt.ShouldBe(ClaudeConfig.DefaultHeaderPrompt);
        config.PrebuiltCommands.Select(command => command.Label).ShouldBe(["Grammar", "Prompt"]);
        config.Stt.Languages.ShouldBe(["de", "en"]);
        config.Stt.NoVerbatim.ShouldBeTrue();
        config.Hotkeys.Enabled.ShouldBeTrue();
        config.Claude.TimeoutSeconds.ShouldBe(180);
        config.Keyterms.ShouldNotBeEmpty();
        config.Dictionary.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_null_dictionary_replacement_is_dropped_rather_than_carried_into_the_recording()
    {
        // It survived the load and threw on the first committed segment - on the UI thread,
        // mid-recording, with no handler above it.
        var config = Load("""{"dictionary":{"clod code":null,"dotnet":".NET"}}""");

        new TextPostProcessor(config.Dictionary).Apply("clod code and dotnet").ShouldBe("clod code and .NET");
    }

    [Fact]
    public void A_null_entry_in_the_command_list_is_dropped()
    {
        var config = Load("""{"prebuiltCommands":[null,{"label":"Shorter","text":"Halve it."}]}""");

        config.PrebuiltCommands.Select(command => command.Label).ShouldBe(["Shorter"]);
    }

    [Fact]
    public void A_config_that_is_not_json_at_all_still_starts_the_app()
    {
        Should.NotThrow(() => Load("{ this is not json"));
    }

    // ---------------------------------------------------------------- shipped defaults

    [Fact]
    public void A_header_prompt_that_is_an_older_shipped_default_becomes_the_current_one()
    {
        var prompt = System.Text.Json.JsonSerializer.Serialize(ShippedDefaults.OldestHeaderPrompt);
        var config = Load("{\"claude\":{\"headerPrompt\":" + prompt + "}}");

        config.Claude.HeaderPrompt.ShouldBe(ClaudeConfig.DefaultHeaderPrompt);
        // Written back, so the migration runs once, not on every start.
        File.ReadAllText(path).ShouldContain("language the file is in");
    }

    [Fact]
    public void An_edited_header_prompt_is_left_alone()
    {
        var config = Load("""{"claude":{"headerPrompt":"Be terse. Edit only the file named."}}""");

        config.Claude.HeaderPrompt.ShouldBe("Be terse. Edit only the file named.");
    }

    [Theory]
    [InlineData("Grammatik")]
    [InlineData("Grammar")]
    public void The_German_grammar_command_becomes_the_current_shipped_list(string label)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(ShippedDefaults.Grammar014Text);
        var config = Load("{\"prebuiltCommands\":[{\"label\":\"" + label + "\",\"text\":" + text + "}]}");

        var shipped = new MumblrConfig().PrebuiltCommands;
        config.PrebuiltCommands.Select(command => (command.Label, command.Text))
            .ShouldBe(shipped.Select(command => (command.Label, command.Text)));
    }

    [Fact]
    public void A_list_holding_only_the_current_grammar_text_gains_the_prompt_button()
    {
        // What 0.2.0 wrote before the Prompt button existed - derived from the current Grammar
        // text, so this pins the rule rather than the historic wording.
        var grammar = new MumblrConfig().PrebuiltCommands.Single(command => command.Label == "Grammar");
        var text = System.Text.Json.JsonSerializer.Serialize(grammar.Text);
        var config = Load("{\"prebuiltCommands\":[{\"label\":\"Grammar\",\"text\":" + text + "}]}");

        config.PrebuiltCommands.Select(command => command.Label).ShouldBe(["Grammar", "Prompt"]);
    }

    [Fact]
    public void A_command_list_the_user_extended_is_left_alone()
    {
        var text = System.Text.Json.JsonSerializer.Serialize(ShippedDefaults.Grammar014Text);
        var config = Load("{\"prebuiltCommands\":[{\"label\":\"Grammar\",\"text\":" + text +
            "},{\"label\":\"Shorter\",\"text\":\"Kürze das auf die Hälfte.\"}]}");

        config.PrebuiltCommands.Count.ShouldBe(2);
        config.PrebuiltCommands[0].Text.ShouldBe(ShippedDefaults.Grammar014Text);
    }

    [Fact]
    public void The_current_defaults_are_not_migrated()
    {
        ConfigMigration.Apply(new MumblrConfig()).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- fingerprints

    /// <summary>
    /// A fingerprint cannot be checked by eye, and a wrong one is silent: no install migrates and
    /// no other test fails. Each is pinned here against the literal text it stands for.
    /// </summary>
    [Theory]
    [InlineData("739f135f7fd0565f41286a1059ad25e12f584e36d7f329a8bfa080013feea16b", ShippedDefaults.OldestHeaderPrompt)]
    [InlineData("261ddd57fb11286ba823ffd222bcaa55f7ba577de6241521844539e257ca1718", ShippedDefaults.Header015)]
    [InlineData("f86effd995a7d95dffc8645418d589bc0e1ff0e72add2990060d8fa65ff0548a", ShippedDefaults.Header016)]
    [InlineData("84a8df986008a764277ff623513586392dd0b0967753594b9d9b15f247bac245", ShippedDefaults.HeaderUnreleased)]
    public void Every_legacy_header_fingerprint_matches_the_text_it_stands_for(string fingerprint, string text) =>
        ConfigMigration.Fingerprint(text).ShouldBe(fingerprint);

    [Theory]
    [InlineData("0d45fee38f31d3ec6abdb4752c41f85b65fef4aea1e04b63305f171edd10bbb7", ShippedDefaults.Grammar013PreLabel, ShippedDefaults.Grammar013PreText)]
    [InlineData("dc020f150a7e5b707451dbfbe03fee372ebcb7c4da450f9624523feb8ad7c3fd", ShippedDefaults.Grammar013Label, ShippedDefaults.Grammar013Text)]
    [InlineData("14d753baf07a808a40b0b2ae5f339298e137d882389cde231a601c5f39a04740", ShippedDefaults.Grammar014Label, ShippedDefaults.Grammar014Text)]
    [InlineData("4537c9610250d9716a116db5363c505115170ff399cd7cd5e1f0081503402be4", ShippedDefaults.GrammarEnglishFirstLabel, ShippedDefaults.GrammarEnglishFirstText)]
    [InlineData("76fcd2710e6754d122e58c31f3929d65a79a81a490c255e21231dee23018f26b", ShippedDefaults.GrammarEnglishAloneLabel, ShippedDefaults.GrammarEnglishAloneText)]
    public void Every_legacy_command_fingerprint_matches_the_text_it_stands_for(string fingerprint, string label, string text) =>
        ConfigMigration.Fingerprint([new PrebuiltCommand { Label = label, Text = text }]).ShouldBe(fingerprint);

    /// <summary>Layout only, so the indentation of a raw string literal cannot change a fingerprint.</summary>
    [Fact]
    public void The_fingerprint_ignores_layout_but_not_words()
    {
        ConfigMigration.Fingerprint("  one\n\n  two  ").ShouldBe(ConfigMigration.Fingerprint("one two"));
        ConfigMigration.Fingerprint("one two").ShouldNotBe(ConfigMigration.Fingerprint("one twos"));
    }

    public void Dispose() => TestDirectories.Delete(directory);
}

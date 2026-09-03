using Mumblr.Core.Config;
using Mumblr.Core.Documents;
using Mumblr.Core.Hotkeys;

namespace Mumblr.Core.Tests;

public class DictationDocumentTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mumblr-{Guid.NewGuid():N}");

    [Fact]
    public void Creates_the_markdown_file_immediately()
    {
        var document = DictationDocument.Create(directory, new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));

        Path.GetFileName(document.MarkdownPath).ShouldStartWith("dictated-");
        Path.GetExtension(document.MarkdownPath).ShouldBe(".md");
        File.Exists(document.MarkdownPath).ShouldBeTrue();
    }

    [Fact]
    public void Puts_the_wav_next_to_the_markdown()
    {
        var document = DictationDocument.Create(directory);

        Path.GetDirectoryName(document.WavPath).ShouldBe(Path.GetDirectoryName(document.MarkdownPath));
        Path.GetFileNameWithoutExtension(document.WavPath)
            .ShouldBe(Path.GetFileNameWithoutExtension(document.MarkdownPath));
    }

    [Fact]
    public void A_second_document_in_the_same_second_gets_its_own_name()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        var first = DictationDocument.Create(directory, now);
        var second = DictationDocument.Create(directory, now);

        second.MarkdownPath.ShouldNotBe(first.MarkdownPath);
    }

    [Fact]
    public void Flush_and_read_round_trip_the_buffer()
    {
        var document = DictationDocument.Create(directory);

        document.Flush("erste Zeile");

        document.Read().ShouldBe("erste Zeile");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

public class SnapshotStoreTests
{
    [Fact]
    public void Reverts_to_the_content_from_before_the_command()
    {
        var store = new SnapshotStore();
        store.Push("vorher", "letzten Satz loeschen");

        store.CanRevert.ShouldBeTrue();
        store.TryPop(out var snapshot).ShouldBeTrue();
        snapshot.Content.ShouldBe("vorher");
        snapshot.Label.ShouldBe("letzten Satz loeschen");
        store.CanRevert.ShouldBeFalse();
    }

    [Fact]
    public void Reverts_commands_in_reverse_order()
    {
        var store = new SnapshotStore();
        store.Push("eins", "a");
        store.Push("zwei", "b");

        store.TryPop(out var first);
        store.TryPop(out var second);

        first.Content.ShouldBe("zwei");
        second.Content.ShouldBe("eins");
    }

    [Fact]
    public void Keeps_only_the_most_recent_snapshots()
    {
        var store = new SnapshotStore(capacity: 2);
        store.Push("eins", "a");
        store.Push("zwei", "b");
        store.Push("drei", "c");

        store.Count.ShouldBe(2);
        store.TryPop(out var newest);
        newest.Content.ShouldBe("drei");
        store.TryPop(out var older);
        older.Content.ShouldBe("zwei");
    }

    [Fact]
    public void Popping_an_empty_store_is_safe()
    {
        new SnapshotStore().TryPop(out _).ShouldBeFalse();
    }
}

public class ConfigStoreTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"mumblr-{Guid.NewGuid():N}", "config.json");

    [Fact]
    public void Writes_defaults_on_first_load()
    {
        var store = new ConfigStore(path);

        var config = store.Load();

        File.Exists(path).ShouldBeTrue();
        config.SttMode.ShouldBe(Mumblr.Core.Stt.SttMode.Realtime);
        config.Claude.Model.ShouldBe("opus");
        config.Claude.Effort.ShouldBe("high");
        config.Stt.NoVerbatim.ShouldBeTrue();
        config.Stt.LanguageCode.ShouldBeNull();
    }

    [Fact]
    public void Round_trips_every_configurable_field()
    {
        var store = new ConfigStore(path);
        var config = store.Load();
        config.MicrophoneDeviceId = "{0.0.1.00000000}.{abc}";
        config.SttMode = Mumblr.Core.Stt.SttMode.Batch;
        config.Keyterms = ["Aspire"];
        config.Dictionary = new Dictionary<string, string> { ["clod"] = "Claude" };
        config.Hotkeys.ToggleRecording = "Ctrl+Shift+R";
        config.Claude.Model = "sonnet";

        store.Save(config);
        var reloaded = new ConfigStore(path).Load();

        reloaded.MicrophoneDeviceId.ShouldBe("{0.0.1.00000000}.{abc}");
        reloaded.SttMode.ShouldBe(Mumblr.Core.Stt.SttMode.Batch);
        reloaded.Keyterms.ShouldBe(["Aspire"]);
        reloaded.Dictionary["clod"].ShouldBe("Claude");
        reloaded.Hotkeys.ToggleRecording.ShouldBe("Ctrl+Shift+R");
        reloaded.Claude.Model.ShouldBe("sonnet");
    }

    [Fact]
    public void A_broken_config_falls_back_to_defaults_instead_of_crashing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        new ConfigStore(path).Load().Claude.Model.ShouldBe("opus");
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

public class HotkeyDefinitionTests
{
    [Fact]
    public void Parses_modifiers_and_the_key()
    {
        var hotkey = HotkeyDefinition.Parse("Ctrl+Alt+Space");

        hotkey.Modifiers.ShouldBe(HotkeyModifiers.Control | HotkeyModifiers.Alt);
        hotkey.VirtualKey.ShouldBe(0x20);
        hotkey.Text.ShouldBe("Ctrl+Alt+Space");
    }

    [Theory]
    [InlineData("A", 'A')]
    [InlineData("z", 'Z')]
    [InlineData("5", '5')]
    public void Parses_letters_and_digits(string input, char expected)
    {
        HotkeyDefinition.Parse(input).VirtualKey.ShouldBe(expected);
    }

    [Theory]
    [InlineData("F1", 0x70)]
    [InlineData("F12", 0x7B)]
    public void Parses_function_keys(string input, int expected)
    {
        HotkeyDefinition.Parse(input).VirtualKey.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Nope")]
    [InlineData("Ctrl+A+B")]
    public void Rejects_garbage(string input)
    {
        HotkeyDefinition.TryParse(input, out _).ShouldBeFalse();
    }

    [Fact]
    public void Normalises_the_display_text()
    {
        HotkeyDefinition.Parse("alt+ctrl+d").Text.ShouldBe("Ctrl+Alt+D");
    }
}

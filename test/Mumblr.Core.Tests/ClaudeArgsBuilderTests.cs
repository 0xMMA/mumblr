using Mumblr.Core.Commands;
using Mumblr.Core.Config;

namespace Mumblr.Core.Tests;

public class ClaudeArgsBuilderTests
{
    private static readonly string FilePath = Path.Combine(Path.GetTempPath(), "repo", "dictated-20260904-120000.md");

    [Fact]
    public void Uses_print_mode_with_the_configured_model_and_effort()
    {
        var args = ClaudeArgsBuilder.Build(new ClaudeConfig(), "letzten Satz loeschen", FilePath).ToList();

        args.ShouldContain("--print");
        args[args.IndexOf("--model") + 1].ShouldBe("opus");
        args[args.IndexOf("--effort") + 1].ShouldBe("high");
    }

    [Fact]
    public void A_blank_model_or_effort_falls_back_instead_of_emitting_an_empty_flag()
    {
        // A config written by an older build, or hand-edited, must not silently downgrade the
        // command to whatever `claude --model ""` happens to do.
        var config = new ClaudeConfig { Model = "  ", Effort = string.Empty };

        var args = ClaudeArgsBuilder.Build(config, "aufraeumen", FilePath).ToList();

        args[args.IndexOf("--model") + 1].ShouldBe("opus");
        args[args.IndexOf("--effort") + 1].ShouldBe("high");
    }

    [Fact]
    public void An_explicit_model_still_wins()
    {
        var config = new ClaudeConfig { Model = "sonnet", Effort = "low" };

        var args = ClaudeArgsBuilder.Build(config, "aufraeumen", FilePath).ToList();

        args[args.IndexOf("--model") + 1].ShouldBe("sonnet");
        args[args.IndexOf("--effort") + 1].ShouldBe("low");
        config.Describe().ShouldBe("sonnet / low effort");
    }

    [Fact]
    public void Requests_structured_json_output_for_the_command_log()
    {
        var args = ClaudeArgsBuilder.Build(new ClaudeConfig(), "aufraeumen", FilePath).ToList();

        args[args.IndexOf("--output-format") + 1].ShouldBe("json");
        args[args.IndexOf("--json-schema") + 1].ShouldContain("summary");
    }

    [Fact]
    public void Limits_the_tools_and_never_prompts()
    {
        var args = ClaudeArgsBuilder.Build(new ClaudeConfig(), "aufraeumen", FilePath).ToList();

        var allowed = args.Skip(args.IndexOf("--allowedTools") + 1).Take(2).ToList();
        allowed.ShouldBe(["Read", "Edit"]);
        args.ShouldContain("--disallowedTools");
        args.ShouldContain("Bash");
        args[args.IndexOf("--permission-prompts") + 1].ShouldBe("none");
        args[args.IndexOf("--permission-mode") + 1].ShouldBe("acceptEdits");
    }

    [Fact]
    public void Passes_the_header_prompt_as_a_system_prompt_append()
    {
        var config = new ClaudeConfig { HeaderPrompt = "be a prompt assistant" };

        var args = ClaudeArgsBuilder.Build(config, "aufraeumen", FilePath).ToList();

        args[args.IndexOf("--append-system-prompt") + 1].ShouldBe("be a prompt assistant");
    }

    [Fact]
    public void Prompt_carries_the_command_and_the_absolute_path()
    {
        var prompt = ClaudeArgsBuilder.BuildPrompt("ersetze X durch Y", FilePath);

        prompt.ShouldContain("ersetze X durch Y");
        prompt.ShouldContain(FilePath);
    }

    [Fact]
    public void Structured_output_can_be_turned_off()
    {
        var args = ClaudeArgsBuilder.Build(new ClaudeConfig { UseJsonSchema = false }, "x", FilePath).ToList();

        args.ShouldNotContain("--json-schema");
        args[args.IndexOf("--output-format") + 1].ShouldBe("json");
    }

    [Fact]
    public void Restricted_is_opt_in()
    {
        ClaudeArgsBuilder.Build(new ClaudeConfig(), "x", FilePath).ShouldNotContain("--restricted");
        ClaudeArgsBuilder.Build(new ClaudeConfig { Restricted = true }, "x", FilePath).ShouldContain("--restricted");
    }

    [Fact]
    public void Rejects_an_empty_command()
    {
        Should.Throw<ArgumentException>(() => ClaudeArgsBuilder.Build(new ClaudeConfig(), "  ", FilePath));
    }

    [Fact]
    public void Extracts_the_summary_from_the_structured_result()
    {
        var stdout = """{"type":"result","is_error":false,"result":"{\"summary\":\"Removed the last sentence.\"}"}""";

        ClaudeArgsBuilder.ExtractSummary(stdout).ShouldBe("Removed the last sentence.");
    }

    [Fact]
    public void Falls_back_to_the_plain_result_when_the_schema_was_ignored()
    {
        var stdout = """{"type":"result","is_error":false,"result":"Removed the last sentence."}""";

        ClaudeArgsBuilder.ExtractSummary(stdout).ShouldBe("Removed the last sentence.");
    }

    [Fact]
    public void Detects_an_error_turn()
    {
        ClaudeArgsBuilder.IsErrorResult("""{"is_error":true,"result":"nope"}""").ShouldBeTrue();
        ClaudeArgsBuilder.IsErrorResult("""{"is_error":false,"result":"ok"}""").ShouldBeFalse();
        ClaudeArgsBuilder.IsErrorResult("not json").ShouldBeFalse();
    }
}

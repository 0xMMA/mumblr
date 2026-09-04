using Mumblr.App.Updates;
using System.Net.Http;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mumblr.App.ViewModels;
using Mumblr.Core.Commands;
using Mumblr.Core.Config;
using Mumblr.Core.Hotkeys;
using Mumblr.Core.State;
using Mumblr.Core.Stt;

namespace Mumblr.App.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), $"mumblr-{Guid.NewGuid():N}");
    private readonly FakeEditorHost editor = new();
    private readonly FakeDeviceEnumerator devices = new();
    private readonly FakeCapture capture = new();
    private readonly FakeHotkeyService hotkeys = new();
    private readonly FakeSttEngineFactory engines = new();
    private readonly FakeClaudeRunner claude = new();
    private readonly FakeUpdateService updates = new();
    private readonly ConfigStore configStore;
    private readonly MumblrConfig config;
    private MainViewModel? viewModel;

    public MainViewModelTests()
    {
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable(ApiKeyProvider.PrimaryVariable, "test-key");

        configStore = new ConfigStore(Path.Combine(workspace, "config.json"));
        config = configStore.Load();
        config.MicrophoneDeviceId = "dev-1";
        config.MicrophoneDeviceName = "Yeti";
        configStore.Save(config);
    }

    private MainViewModel CreateViewModel()
    {
        viewModel = new MainViewModel(workspace, editor, configStore, devices, capture, hotkeys, claude, engines, updates);
        viewModel.Initialize();
        return viewModel;
    }

    private static async Task PumpAsync()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Creates_the_dictation_file_on_start()
    {
        var viewModel = CreateViewModel();

        File.Exists(viewModel.DocumentPath).ShouldBeTrue();
        Path.GetFileName(viewModel.DocumentPath).ShouldStartWith("dictated-");
    }

    [AvaloniaFact]
    public void Selects_the_configured_microphone_instead_of_a_default()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedDevice.ShouldNotBeNull();
        viewModel.SelectedDevice!.Id.ShouldBe("dev-1");
    }

    [AvaloniaFact]
    public void Shows_the_picker_when_the_configured_microphone_is_gone()
    {
        devices.Devices.Clear();
        devices.Devices.Add(new Mumblr.Core.Audio.AudioDeviceInfo("dev-2", "Webcam"));

        var viewModel = CreateViewModel();

        viewModel.SelectedDevice.ShouldBeNull();
        viewModel.IsWarning.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Recording_locks_the_editor_and_starts_the_engine()
    {
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        viewModel.IsRecording.ShouldBeTrue();
        editor.IsReadOnly.ShouldBeTrue();
        engines.Last!.Started.ShouldBeTrue();
        capture.StartedWith.ShouldBe(["dev-1"]);
    }

    [AvaloniaFact]
    public async Task Refuses_to_record_without_a_microphone()
    {
        devices.Devices.Clear();
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        viewModel.IsRecording.ShouldBeFalse();
        viewModel.IsWarning.ShouldBeTrue();
        editor.IsReadOnly.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Committed_segments_land_at_the_insert_marker()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Anfang Ende";
        editor.CaretOffset = 6; // right after "Anfang"

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Commit("mitte");
        await PumpAsync();

        editor.Text.ShouldBe("Anfang mitte Ende");
    }

    [AvaloniaFact]
    public async Task Later_segments_follow_the_earlier_ones_in_order()
    {
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Commit("eins");
        engines.Last.Commit("zwei");
        await PumpAsync();

        editor.Text.ShouldBe("eins zwei");
    }

    [AvaloniaFact]
    public async Task Applies_the_dictionary_to_committed_text()
    {
        config.Dictionary = new Dictionary<string, string> { ["clod code"] = "Claude Code" };
        configStore.Save(config);

        var viewModel = CreateViewModel();
        viewModel.ReloadConfigCommand.Execute(null);

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Commit("dann macht clod code das");
        await PumpAsync();

        editor.Text.ShouldBe("dann macht Claude Code das");
    }

    [AvaloniaFact]
    public async Task Partials_only_reach_the_preview_line()
    {
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Partial("halb fertig");
        await PumpAsync();

        viewModel.PreviewText.ShouldBe("halb fertig");
        editor.Text.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Batch_text_arrives_on_stop()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedSttMode = SttMode.Batch;

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Options!.ModelId.ShouldBe("scribe_v2");
        engines.Last.TextOnStop = "der ganze Take";

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        editor.Text.ShouldBe("der ganze Take");
    }

    [AvaloniaFact]
    public async Task Stop_copies_the_buffer_and_flushes_the_file()
    {
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Commit("fertiger Text");
        await PumpAsync();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        viewModel.IsRecording.ShouldBeFalse();
        editor.IsReadOnly.ShouldBeFalse();
        editor.Clipboard.ShouldBe("fertiger Text");
        File.ReadAllText(viewModel.DocumentPath).ShouldBe("fertiger Text");
    }

    [AvaloniaFact]
    public async Task Audio_reaches_both_the_wav_file_and_the_engine()
    {
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        capture.Emit(new byte[320]);
        await PumpAsync();

        engines.Last!.PushedBytes.ShouldBe(320);
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        viewModel.Shutdown();

        var wav = Path.ChangeExtension(viewModel.DocumentPath, ".wav");
        File.Exists(wav).ShouldBeTrue();
        new FileInfo(wav).Length.ShouldBe(44 + 320);
    }

    [AvaloniaFact]
    public async Task Hold_to_talk_runs_claude_and_reloads_the_file()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Erster Satz. Zweiter Satz.";
        claude.FileContentAfterRun = "Erster Satz.";

        hotkeys.PressCommandKey();
        await PumpAsync();
        capture.Emit(new byte[640]);
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        claude.Calls.Count.ShouldBe(1);
        claude.Calls[0].Command.ShouldBe("letzten Satz loeschen");
        claude.Calls[0].Path.ShouldBe(viewModel.DocumentPath);
        editor.Text.ShouldBe("Erster Satz.");
        viewModel.CommandLog.Count.ShouldBe(1);
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Succeeded);
        viewModel.CommandLog[0].Response.ShouldBe("Removed the last sentence.");
    }

    [AvaloniaFact]
    public async Task A_command_from_recording_pauses_and_resumes_channel_one()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        var firstEngine = engines.Last!;

        hotkeys.PressCommandKey();
        await PumpAsync();

        firstEngine.Stopped.ShouldBeTrue();
        viewModel.IsCommanding.ShouldBeTrue();

        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        viewModel.IsRecording.ShouldBeTrue();
        viewModel.IsCommanding.ShouldBeFalse();
        engines.Last.ShouldNotBe(firstEngine);
        engines.Last!.Started.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task The_command_log_never_touches_the_content_buffer()
    {
        var viewModel = CreateViewModel();
        editor.Text = "unveraendert";
        claude.Behaviour = (_, _) => new CommandResult(true, "nothing to do", "{}", TimeSpan.Zero);

        hotkeys.PressCommandKey();
        await PumpAsync();
        capture.Emit(new byte[640]);
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        editor.Text.ShouldBe("unveraendert");
        viewModel.CommandLog[0].CommandText.ShouldBe("letzten Satz loeschen");
    }

    [AvaloniaFact]
    public async Task Revert_restores_the_snapshot_from_before_the_command()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Erster Satz. Zweiter Satz.";
        claude.FileContentAfterRun = "Erster Satz.";

        hotkeys.PressCommandKey();
        await PumpAsync();
        capture.Emit(new byte[640]);
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        editor.Text.ShouldBe("Erster Satz.");
        viewModel.CanRevert.ShouldBeTrue();

        viewModel.RevertLastCommandCommand.Execute(null);

        editor.Text.ShouldBe("Erster Satz. Zweiter Satz.");
        File.ReadAllText(viewModel.DocumentPath).ShouldBe("Erster Satz. Zweiter Satz.");
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Reverted);
        viewModel.CanRevert.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task A_failing_command_is_logged_and_leaves_the_state_machine_idle()
    {
        var viewModel = CreateViewModel();
        engines.ClipFailure = new InvalidOperationException("no key");

        hotkeys.PressCommandKey();
        await PumpAsync();
        capture.Emit(new byte[640]);
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        viewModel.IsCommanding.ShouldBeFalse();
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Failed);
        claude.Calls.ShouldBeEmpty();
        viewModel.IsWarning.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task The_toggle_hotkey_starts_and_stops_recording()
    {
        var viewModel = CreateViewModel();

        hotkeys.Trigger(HotkeyAction.ToggleRecording);
        await PumpAsync();
        viewModel.IsRecording.ShouldBeTrue();

        hotkeys.Trigger(HotkeyAction.ToggleRecording);
        await PumpAsync();
        viewModel.IsRecording.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task The_copy_hotkey_copies_without_stopping()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Zwischenstand";

        hotkeys.Trigger(HotkeyAction.Copy);
        await PumpAsync();

        editor.Clipboard.ShouldBe("Zwischenstand");
    }

    [AvaloniaFact]
    public void Switching_the_stt_mode_is_persisted()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedSttMode = SttMode.Batch;

        new ConfigStore(configStore.ConfigPath).Load().SttMode.ShouldBe(SttMode.Batch);
    }

    [AvaloniaFact]
    public async Task A_rejected_transcription_survives_the_stop_message()
    {
        // The whole reason issue #1 looked silent: the request was refused, and the routine
        // "Stopped - buffer copied" line then overwrote the only trace of it.
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.FailureOnStop = new HttpRequestException("400 Some keyword contains invalid characters");

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("invalid characters");
        viewModel.StatusMessage.ShouldContain("buffer copied");
    }

    [AvaloniaFact]
    public async Task A_realtime_error_over_an_open_socket_survives_the_stop_message()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        engines.Last!.Fail(new InvalidOperationException("Each keyterm must be at most 20 characters."));
        await PumpAsync();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("20 characters");
    }

    [AvaloniaFact]
    public async Task An_ordinary_stop_still_reports_plainly()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.IsWarning.ShouldBeFalse();
        viewModel.StatusMessage.ShouldBe("Stopped - buffer copied to the clipboard.");
    }

    [AvaloniaFact]
    public async Task Keyterms_go_to_the_backend_as_repeated_parameters()
    {
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        engines.Last!.Options!.KeytermsEncoding.ShouldBe("repeated");
    }

    [AvaloniaFact]
    public async Task The_command_log_names_the_model_that_answered()
    {
        var viewModel = CreateViewModel();

        hotkeys.PressCommandKey();
        await PumpAsync();
        capture.Emit(new byte[640]);
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        viewModel.CommandLog[0].Engine.ShouldBe("opus / high effort");
    }

    [AvaloniaFact]
    public async Task A_prebuilt_command_runs_without_the_microphone()
    {
        var viewModel = CreateViewModel();
        editor.Text = "erster satz zweiter satz";
        claude.FileContentAfterRun = "Erster Satz. Zweiter Satz.";

        var prebuilt = viewModel.PrebuiltCommands[0];
        await viewModel.RunPrebuiltCommand.ExecuteAsync(prebuilt);
        await PumpAsync();

        claude.Calls.Single().Command.ShouldBe(prebuilt.Text);
        editor.Text.ShouldBe("Erster Satz. Zweiter Satz.");
        viewModel.CommandLog[0].Source.ShouldBe(prebuilt.Label);
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Succeeded);
        viewModel.IsCommanding.ShouldBeFalse();
        viewModel.IsRecording.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task A_prebuilt_command_pauses_and_resumes_a_recording()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        var firstEngine = engines.Last!;

        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        firstEngine.Stopped.ShouldBeTrue();
        viewModel.IsRecording.ShouldBeTrue();
        engines.Last.ShouldNotBe(firstEngine);
    }

    [AvaloniaFact]
    public async Task A_prebuilt_command_can_be_reverted_like_a_spoken_one()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Original.";
        claude.FileContentAfterRun = "Umgeschrieben.";

        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();
        editor.Text.ShouldBe("Umgeschrieben.");

        viewModel.RevertLastCommandCommand.Execute(null);

        editor.Text.ShouldBe("Original.");
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Reverted);
    }

    [AvaloniaFact]
    public async Task A_prebuilt_command_is_refused_while_another_command_runs()
    {
        var viewModel = CreateViewModel();
        hotkeys.PressCommandKey();
        await PumpAsync();

        // The hold is still down, so the session is Commanding and the buttons must not fire.
        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        viewModel.CommandLog.Count.ShouldBe(1);
        claude.Calls.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void Prebuilt_commands_come_from_the_config()
    {
        var viewModel = CreateViewModel();

        viewModel.HasPrebuiltCommands.ShouldBeTrue();
        viewModel.PrebuiltCommands[0].Label.ShouldBe("Grammatik");
        viewModel.PrebuiltCommands[0].Text.ShouldContain("Grammatik");
    }

    [AvaloniaFact]
    public void The_status_bar_reports_the_key_the_device_and_the_build()
    {
        var viewModel = CreateViewModel();

        viewModel.HasApiKey.ShouldBeTrue();
        viewModel.ApiStatusText.ShouldBe("API key");
        viewModel.MicrophoneLabel.ShouldBe("Yeti");
        viewModel.SttStatusText.ShouldBe("Realtime - idle");
        viewModel.VersionButtonText.ShouldStartWith("v");
    }

    [AvaloniaFact]
    public void A_missing_key_is_reported_without_ever_holding_the_value()
    {
        Environment.SetEnvironmentVariable(ApiKeyProvider.PrimaryVariable, null);
        Environment.SetEnvironmentVariable(ApiKeyProvider.FallbackVariable, null);

        var viewModel = CreateViewModel();

        viewModel.HasApiKey.ShouldBeFalse();
        viewModel.ApiStatusText.ShouldBe("no API key");
        viewModel.IsWarning.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task The_backend_state_follows_the_recording()
    {
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        viewModel.EngineStatus.ShouldBe("connected");

        engines.Last!.Fail(new InvalidOperationException("rejected"));
        await PumpAsync();
        viewModel.EngineStatus.ShouldBe("error");

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();
        viewModel.EngineStatus.ShouldBe("idle");
    }

    [AvaloniaFact]
    public async Task Batch_says_it_is_buffering_rather_than_connected()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedSttMode = SttMode.Batch;

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        viewModel.SttStatusText.ShouldBe("Batch - buffering");
    }

    [AvaloniaFact]
    public async Task The_character_count_follows_the_buffer()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        engines.Last!.Commit("Erster Satz.");
        await PumpAsync();

        viewModel.CharacterCount.ShouldBe(editor.Text.Length);
        viewModel.CharacterCount.ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public async Task Copying_mid_recording_does_not_erase_the_failure()
    {
        // The bug the review found: Copy, Revert and Reload all call Inform, so IsWarning could
        // not carry "this recording failed". Ctrl+Alt+C is a documented global hotkey, so the
        // sequence below is an ordinary Tuesday, not a contrivance.
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        engines.Last!.Fail(new InvalidOperationException("Each keyterm must be at most 20 characters."));
        await PumpAsync();

        await viewModel.CopyCommand.ExecuteAsync(null);
        await PumpAsync();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("20 characters");
    }

    [AvaloniaFact]
    public async Task A_failure_from_an_earlier_recording_does_not_haunt_the_next_one()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Fail(new InvalidOperationException("rejected"));
        await PumpAsync();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.IsWarning.ShouldBeFalse();
        viewModel.StatusMessage.ShouldBe("Stopped - buffer copied to the clipboard.");
    }

    [AvaloniaFact]
    public async Task A_failing_command_warns_and_survives_the_resume_message()
    {
        var viewModel = CreateViewModel();
        claude.Behaviour = (_, _) => new CommandResult(false, "claude refused", "{}", TimeSpan.FromSeconds(2));

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("claude refused");
        viewModel.IsRecording.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task A_second_command_cannot_start_while_the_first_is_being_prepared()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        // Hold the pause open, so the second entry point fires inside the window rather than after.
        var pause = new TaskCompletionSource();
        engines.Last!.StopGate = pause;

        var first = viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        viewModel.PressCommandButton();
        await PumpAsync();

        pause.SetResult();
        await first;
        await PumpAsync();

        claude.Calls.Count.ShouldBe(1);
        viewModel.CommandLog.Count.ShouldBe(1);
    }

    [AvaloniaFact]
    public void The_character_count_follows_typing()
    {
        var viewModel = CreateViewModel();

        editor.Type("getippt");

        viewModel.CharacterCount.ShouldBe("getippt".Length);
    }

    [AvaloniaFact]
    public async Task The_character_count_follows_a_revert()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Erster Satz. Zweiter Satz.";
        claude.FileContentAfterRun = "Erster Satz.";

        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();
        viewModel.CharacterCount.ShouldBe("Erster Satz.".Length);

        viewModel.RevertLastCommandCommand.Execute(null);

        viewModel.CharacterCount.ShouldBe("Erster Satz. Zweiter Satz.".Length);
    }

    [AvaloniaFact]
    public async Task The_command_log_names_the_model_that_actually_answered()
    {
        var viewModel = CreateViewModel();
        claude.Behaviour = (_, _) => new CommandResult(true, "done", "{}", TimeSpan.FromSeconds(3), "claude-opus-4-6");

        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        viewModel.CommandLog[0].Engine.ShouldBe("claude-opus-4-6");
    }

    [AvaloniaFact]
    public async Task A_check_that_could_not_ask_never_claims_to_be_current()
    {
        var viewModel = CreateViewModel();
        updates.Outcome = UpdateService.UpdateCheck.Failed;

        await viewModel.UseVersionButtonCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("Could not reach");
        viewModel.HasUpdate.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task An_available_update_turns_the_version_button_into_an_install()
    {
        var viewModel = CreateViewModel();
        updates.Outcome = UpdateService.UpdateCheck.Available;
        updates.AvailableVersion = "0.9.9";

        await viewModel.UseVersionButtonCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.HasUpdate.ShouldBeTrue();
        viewModel.VersionButtonText.ShouldBe("update to 0.9.9");

        await viewModel.UseVersionButtonCommand.ExecuteAsync(null);
        updates.Applied.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task An_unpackaged_build_says_so_instead_of_claiming_to_be_current()
    {
        var viewModel = CreateViewModel();
        updates.Outcome = UpdateService.UpdateCheck.NotInstalled;

        await viewModel.UseVersionButtonCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.StatusMessage.ShouldContain("replacing the folder");
        viewModel.IsWarning.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task An_up_to_date_check_says_the_running_version()
    {
        var viewModel = CreateViewModel();
        updates.Outcome = UpdateService.UpdateCheck.UpToDate;

        await viewModel.UseVersionButtonCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.StatusMessage.ShouldBe($"v{viewModel.Version} is the latest build.");
        viewModel.IsWarning.ShouldBeFalse();
    }

    [AvaloniaTheory]
    [InlineData("0.1.2+bebdcd0", "0.1.2")]
    [InlineData("0.1.3-alpha.0.4+abc123", "0.1.3-alpha.0.4")]
    [InlineData("0.1.2", "0.1.2")]
    public void The_commit_hash_is_trimmed_off_the_displayed_version(string informational, string expected)
    {
        MainViewModel.ResolveVersion(informational, "9.9.9").ShouldBe(expected);
    }

    [AvaloniaFact]
    public void Without_an_informational_version_the_assembly_version_stands_in()
    {
        MainViewModel.ResolveVersion(null, "1.2.3").ShouldBe("1.2.3");
        MainViewModel.ResolveVersion("  ", null).ShouldBe("dev");
    }

    public void Dispose()
    {
        // The WAV file stays open for the whole session, so the view model has to go first:
        // Windows refuses to delete a directory that still holds an open handle.
        viewModel?.Shutdown();

        // One test clears both key variables process-wide; the class constructor only restores the
        // primary one, and the next class to run would inherit the hole.
        Environment.SetEnvironmentVariable(ApiKeyProvider.PrimaryVariable, null);
        Environment.SetEnvironmentVariable(ApiKeyProvider.FallbackVariable, null);

        if (Directory.Exists(workspace))
            Directory.Delete(workspace, recursive: true);
    }
}

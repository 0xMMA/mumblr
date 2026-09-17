using Mumblr.App.Updates;
using System.Net.Http;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mumblr.App.ViewModels;
using Mumblr.Core.Commands;
using Mumblr.Core.Config;
using Mumblr.Core.Hotkeys;
using Mumblr.Core.Prompts;
using Mumblr.Core.State;
using Mumblr.Core.Stt;

namespace Mumblr.App.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private sealed class NoWatcher : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private readonly string workspace = Path.Combine(Path.GetTempPath(), $"mumblr-{Guid.NewGuid():N}");
    private readonly FakeEditorHost editor = new();
    private readonly FakeDeviceEnumerator devices = new();
    private readonly FakeCapture capture = new();
    private readonly FakeHotkeyService hotkeys = new();
    private readonly FakeSttEngineFactory engines = new();
    private readonly FakeClaudeRunner claude = new();
    private readonly FakeUpdateService updates = new();
    private readonly FakeAttention attention = new();
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

    /// <summary>
    /// A directory where the file should be: the atomic save cannot move over it on any platform.
    /// (A read-only attribute would do on Windows only; Linux lets a rename replace such a file.)
    /// </summary>
    private void MakeConfigUnwritable()
    {
        File.Delete(configStore.ConfigPath);
        Directory.CreateDirectory(configStore.ConfigPath);
    }

    /// <summary>
    /// A second mumblr window saving the shared file. Its own store, so its own idea of what was
    /// last written - which is what tells this window's echo from someone else's change.
    /// </summary>
    private void AnotherWindowWrites(Action<MumblrConfig> change)
    {
        var other = new ConfigStore(configStore.ConfigPath);
        var theirs = other.Load();
        change(theirs);
        other.Save(theirs);
    }

    private string PromptDirectory => Path.Combine(workspace, "prompts");

    private MainViewModel CreateViewModel()
    {
        // No real FileSystemWatcher: it would post events into the dispatcher these tests pump,
        // from a thread-pool thread, on timing the test does not control. What the watchers feed is
        // driven directly through OnConfigFileChanged and OnPromptsChanged instead.
        viewModel = new MainViewModel(
            workspace, editor, configStore, devices, capture, hotkeys, claude, engines, updates, attention,
            prompts: new PromptLibrary(PromptDirectory),
            fileWatcherFactory: (_, _, _) => new NoWatcher());
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
    public async Task Stop_flushes_the_file_and_leaves_the_clipboard_alone()
    {
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Commit("fertiger Text");
        await PumpAsync();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        viewModel.IsRecording.ShouldBeFalse();
        editor.IsReadOnly.ShouldBeFalse();
        File.ReadAllText(viewModel.DocumentPath).ShouldBe("fertiger Text");

        // A take is several record/stop cycles plus a command; copying on each stop would
        // overwrite the clipboard with a version nobody asked for.
        editor.Clipboard.ShouldBeNull();
    }

    [AvaloniaFact]
    public async Task Repeated_takes_never_touch_the_clipboard()
    {
        var viewModel = CreateViewModel();

        for (var take = 0; take < 3; take++)
        {
            await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
            engines.Last!.Commit($"Satz {take}");
            await PumpAsync();
            await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        }

        editor.Clipboard.ShouldBeNull();

        await viewModel.CopyCommand.ExecuteAsync(null);

        editor.Clipboard.ShouldBe(editor.Text);
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
    public async Task A_command_started_inside_the_stop_window_ends_the_take_instead_of_resuming_it()
    {
        // Stopping a realtime backend waits for the last segment, or five seconds. A hold pressed
        // in there reaches Commanding before the stop reaches the machine, so TryStopRecording
        // failed silently and the command handed the session back to Recording: Stop was swallowed
        // and the taskbar started flashing again over a recording the user had ended.
        var viewModel = CreateViewModel();
        editor.Text = "Erster Satz. Zweiter Satz.";
        claude.FileContentAfterRun = "Erster Satz.";
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        var pause = new TaskCompletionSource();
        engines.Last!.StopGate = pause;

        var stopping = viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        hotkeys.PressCommandKey();
        await PumpAsync();
        viewModel.IsCommanding.ShouldBeTrue();

        pause.SetResult();
        await stopping;
        capture.Emit(new byte[640]);
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        // Both presses are honoured: the command ran, changed the file, and the recording is over.
        claude.Calls.Count.ShouldBe(1);
        editor.Text.ShouldBe("Erster Satz.");
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Succeeded);
        viewModel.IsCommanding.ShouldBeFalse();
        viewModel.IsRecording.ShouldBeFalse();
        engines.Created.Count.ShouldBe(1);
        attention.Wanted.ShouldBeFalse();
        viewModel.WindowTitle.ShouldBe("mumblr");
    }

    [AvaloniaFact]
    public async Task A_prebuilt_command_started_inside_the_stop_window_ends_the_take_too()
    {
        // The everyday shape of the same bug: stop, then reach for Grammar before the backend has
        // let go.
        var viewModel = CreateViewModel();
        editor.Text = "Erster Satz. Zweiter Satz.";
        claude.FileContentAfterRun = "Erster Satz.";
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        var pause = new TaskCompletionSource();
        engines.Last!.StopGate = pause;

        var stopping = viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        var grammar = viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        pause.SetResult();
        await stopping;
        await grammar;
        await PumpAsync();

        claude.Calls.Count.ShouldBe(1);
        editor.Text.ShouldBe("Erster Satz.");
        viewModel.IsRecording.ShouldBeFalse();
        engines.Created.Count.ShouldBe(1);
        attention.Wanted.ShouldBeFalse();
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
        // stop line then overwrote the only trace of it.
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.FailureOnStop = new HttpRequestException("400 Some keyword contains invalid characters");

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("invalid characters");
        viewModel.StatusMessage.ShouldContain("stopped");
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
        viewModel.StatusMessage.ShouldBe("Stopped.");
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
        // Label and text are both English: one is UI, the other an instruction to Claude.
        viewModel.PrebuiltCommands[0].Label.ShouldBe("Grammar");
        viewModel.PrebuiltCommands[0].Text.ShouldContain("grammar");
        viewModel.PrebuiltCommands[1].Label.ShouldBe("Prompt");
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
        viewModel.StatusMessage.ShouldBe("Stopped.");
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

        viewModel.CommandLog[0].Engine.ShouldBe("claude-opus-4-6 / high effort");
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
        viewModel.VersionButtonText.ShouldBe("update to 0.9.9 and restart");

        await viewModel.UseVersionButtonCommand.ExecuteAsync(null);
        updates.Applied.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task A_channel_with_no_release_is_not_reported_as_being_up_to_date()
    {
        // A preview install whose channel holds nothing - the betas were deleted, or none was ever
        // cut. Velopack answers that with the same null it uses for "you have the newest".
        var viewModel = CreateViewModel();
        updates.Outcome = UpdateService.UpdateCheck.NoReleases;

        await viewModel.UseVersionButtonCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("No release on this build's channel");
        viewModel.HasUpdate.ShouldBeFalse();
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

    [AvaloniaFact]
    public async Task A_key_up_during_a_prebuilt_command_cannot_end_it()
    {
        // EndCommandAsync used to check only whether *a* command was active, never which one, so
        // the hold key's release failed a prebuilt command's entry, unlocked the editor and
        // resumed channel 1 underneath it.
        var viewModel = CreateViewModel();
        editor.Text = "Vorher.";
        claude.FileContentAfterRun = "Nachher.";
        var gate = new TaskCompletionSource<CommandResult>();
        claude.AsyncBehaviour = (_, _) => gate.Task;

        var running = viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        viewModel.ReleaseCommandButton();
        await PumpAsync();

        // While claude is still working the entry must still be Running and the editor still
        // locked. The release used to fail this entry and resume channel 1 underneath it.
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Running);
        viewModel.IsCommanding.ShouldBeTrue();
        editor.IsReadOnly.ShouldBeTrue();

        gate.SetResult(new CommandResult(true, "done", "{}", TimeSpan.FromSeconds(1)));
        await running;
        await PumpAsync();

        viewModel.CommandLog.Count.ShouldBe(1);
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Succeeded);
        viewModel.IsCommanding.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task A_hold_shorter_than_the_pause_still_ends_the_command()
    {
        // Stopping a realtime backend waits for the last segment; a short hold ends entirely
        // inside that window, and the release used to be dropped on the floor - leaving the
        // session in Commanding with the microphone still running.
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        var pause = new TaskCompletionSource();
        engines.Last!.StopGate = pause;

        viewModel.PressCommandButton();
        viewModel.ReleaseCommandButton();
        await PumpAsync();

        pause.SetResult();
        await PumpAsync();

        viewModel.IsCommanding.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Copy_is_refused_while_claude_is_editing_the_file()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Der Buffer.";
        var gate = new TaskCompletionSource<CommandResult>();
        claude.AsyncBehaviour = (_, _) => gate.Task;

        var running = viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        await viewModel.CopyCommand.ExecuteAsync(null);

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("Claude is editing");
        editor.Clipboard.ShouldBeNull();

        gate.SetResult(new CommandResult(true, "done", "{}", TimeSpan.FromSeconds(1)));
        await running;
        await PumpAsync();
    }

    [AvaloniaFact]
    public async Task The_batch_backend_can_report_an_error_state()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedSttMode = SttMode.Batch;
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.FailureOnStop = new HttpRequestException("400 rejected");

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        // Batch raises its failures only out of StopAsync, so this state was unreachable while
        // the status was cleared before that await.
        viewModel.EngineStatus.ShouldBe("error");
    }

    [AvaloniaFact]
    public async Task A_command_that_changed_nothing_says_so()
    {
        // claude can succeed and still do nothing - ask a question, decide there is nothing to
        // change. Reporting that as "done" makes a no-op look like work.
        var viewModel = CreateViewModel();
        editor.Text = "Unveraendert.";
        claude.Behaviour = (_, _) => new CommandResult(true, "Nothing needed changing.", "{}", TimeSpan.FromSeconds(2));

        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Failed);
        viewModel.CommandLog[0].Response.ShouldStartWith("No change made.");
        viewModel.IsWarning.ShouldBeTrue();
        editor.Text.ShouldBe("Unveraendert.");
    }

    // ---------------------------------------------------------------- hotkey switch

    [AvaloniaFact]
    public async Task Hotkeys_off_ignores_the_chords_and_unregisters_them()
    {
        var viewModel = CreateViewModel();

        viewModel.HotkeysEnabled = false;
        hotkeys.Trigger(HotkeyAction.ToggleRecording);
        await PumpAsync();

        // Belt and braces: the service is stopped, and a chord that fires anyway is dropped.
        hotkeys.Started.ShouldBeNull();
        viewModel.IsRecording.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Hotkeys_off_ignores_the_hold_key()
    {
        var viewModel = CreateViewModel();

        viewModel.HotkeysEnabled = false;
        hotkeys.PressCommandKey();
        await PumpAsync();

        viewModel.IsCommanding.ShouldBeFalse();
        viewModel.CommandLog.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Hotkeys_on_again_registers_the_chords()
    {
        var viewModel = CreateViewModel();
        viewModel.HotkeysEnabled = false;

        viewModel.HotkeysEnabled = true;
        hotkeys.Trigger(HotkeyAction.ToggleRecording);
        await PumpAsync();

        hotkeys.Started.ShouldNotBeNull();
        viewModel.IsRecording.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Hotkeys_off_shows_in_the_hint_and_survives_a_restart()
    {
        var first = CreateViewModel();
        first.HotkeysEnabled = false;

        first.HotkeyHint.ShouldBe("hotkeys off");
        first.HotkeySwitchText.ShouldBe("hotkeys: off");
        hotkeys.Stops.ShouldBe(1);
        configStore.Load().Hotkeys.Enabled.ShouldBeFalse();

        first.Shutdown();
        var second = CreateViewModel();

        second.HotkeysEnabled.ShouldBeFalse();
        second.HotkeyHint.ShouldBe("hotkeys off");
        hotkeys.Started.ShouldBeNull();
        hotkeys.Stops.ShouldBeGreaterThan(1);
    }

    [AvaloniaFact]
    public async Task The_switch_is_refused_while_a_command_is_starting()
    {
        // The window between key-down and the Commanding state - up to five seconds while a
        // realtime backend is paused - is where IsCommanding is still false but a hold is live.
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        var pause = new TaskCompletionSource();
        engines.Last!.StopGate = pause;

        hotkeys.PressCommandKey();
        await PumpAsync();
        viewModel.IsCommanding.ShouldBeFalse();
        viewModel.CanToggleHotkeys.ShouldBeFalse();

        viewModel.HotkeysEnabled = false;

        viewModel.HotkeysEnabled.ShouldBeTrue();
        viewModel.IsWarning.ShouldBeTrue();
        hotkeys.Stops.ShouldBe(0);

        pause.SetResult();
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        viewModel.CanToggleHotkeys.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Turning_hotkeys_off_when_the_config_cannot_be_saved_still_unhooks_and_warns()
    {
        // The switch is a privacy control: "off" must mean off even when persisting it fails.
        var viewModel = CreateViewModel();
        MakeConfigUnwritable();

        viewModel.HotkeysEnabled = false;

        hotkeys.Stops.ShouldBe(1);
        viewModel.HotkeysEnabled.ShouldBeFalse();
        viewModel.HotkeyHint.ShouldBe("hotkeys off");
        viewModel.IsWarning.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task A_hotkey_service_that_refuses_to_start_does_not_advertise_the_chords()
    {
        // The hint used to name chords that were never registered, and the reason - which arrives
        // through the dispatcher - was the only sign anything was wrong.
        hotkeys.StartRefusal = "The previous hotkey thread did not stop - restart mumblr.";

        var viewModel = CreateViewModel();

        viewModel.HotkeyHint.ShouldBe("hotkeys unavailable");
        viewModel.HotkeyHint.ShouldNotContain("Ctrl+Alt+Space");

        await PumpAsync();
        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("did not stop");
    }

    [AvaloniaFact]
    public void A_stop_that_does_not_go_through_is_reported()
    {
        var viewModel = CreateViewModel();
        hotkeys.StopResult = false;

        viewModel.HotkeysEnabled = false;

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("restart");
    }

    [AvaloniaFact]
    public async Task A_config_reload_is_refused_while_a_command_runs()
    {
        // ApplyHotkeys restarts the service, which unhooks the hold key under a running hold.
        var viewModel = CreateViewModel();
        var gate = new TaskCompletionSource<CommandResult>();
        claude.AsyncBehaviour = (_, _) => gate.Task;

        var running = viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();
        var startsBefore = hotkeys.Starts;

        viewModel.ReloadConfigCommand.Execute(null);

        viewModel.IsWarning.ShouldBeTrue();
        hotkeys.Starts.ShouldBe(startsBefore);
        hotkeys.Stops.ShouldBe(0);

        gate.SetResult(new CommandResult(true, "done", "{}", TimeSpan.FromSeconds(1)));
        await running;
    }

    [AvaloniaFact]
    public async Task Hotkeys_off_ignores_a_stray_key_up_during_a_mouse_hold()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Erster Satz. Zweiter Satz.";
        claude.FileContentAfterRun = "Erster Satz.";
        viewModel.HotkeysEnabled = false;

        viewModel.PressCommandButton();
        await PumpAsync();
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        viewModel.IsCommanding.ShouldBeTrue();
        claude.Calls.ShouldBeEmpty();

        capture.Emit(new byte[640]);
        viewModel.ReleaseCommandButton();
        await PumpAsync();

        claude.Calls.Count.ShouldBe(1);
    }

    [AvaloniaFact]
    public void A_config_reload_honours_the_switch()
    {
        var viewModel = CreateViewModel();
        var edited = configStore.Load();
        edited.Hotkeys.Enabled = false;
        configStore.Save(edited);

        viewModel.ReloadConfigCommand.Execute(null);

        viewModel.HotkeysEnabled.ShouldBeFalse();
        hotkeys.Started.ShouldBeNull();
        hotkeys.Stops.ShouldBe(1);
    }

    [AvaloniaFact]
    public async Task Hotkeys_cannot_be_turned_off_while_a_command_runs()
    {
        // Unhooking mid-hold would swallow the key-up and leave the command running until the
        // pause window ends, so the switch is refused while anything is in flight.
        var viewModel = CreateViewModel();
        var gate = new TaskCompletionSource<CommandResult>();
        claude.AsyncBehaviour = (_, _) => gate.Task;

        var running = viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();
        viewModel.IsCommanding.ShouldBeTrue();

        viewModel.HotkeysEnabled = false;

        viewModel.HotkeysEnabled.ShouldBeTrue();
        viewModel.IsWarning.ShouldBeTrue();
        hotkeys.Started.ShouldNotBeNull();

        gate.SetResult(new CommandResult(true, "done", "{}", TimeSpan.FromSeconds(1)));
        await running;
    }

    [AvaloniaFact]
    public async Task Turning_hotkeys_off_does_not_stop_a_running_recording()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        viewModel.HotkeysEnabled = false;
        await PumpAsync();

        viewModel.IsRecording.ShouldBeTrue();
        engines.Last!.Stopped.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task The_mouse_hold_button_works_with_hotkeys_off()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Erster Satz. Zweiter Satz.";
        claude.FileContentAfterRun = "Erster Satz.";
        viewModel.HotkeysEnabled = false;

        viewModel.PressCommandButton();
        await PumpAsync();
        capture.Emit(new byte[640]);
        viewModel.ReleaseCommandButton();
        await PumpAsync();

        claude.Calls.Count.ShouldBe(1);
        editor.Text.ShouldBe("Erster Satz.");
    }

    // ---------------------------------------------------------------- prompts as files

    [AvaloniaFact]
    public void The_command_buttons_come_from_the_prompt_files()
    {
        var viewModel = CreateViewModel();

        viewModel.PrebuiltCommands.Select(c => c.Label).ShouldBe(["Grammar", "Prompt"]);
        Directory.GetFiles(PromptDirectory, "*.md").Length.ShouldBe(2);
    }

    [AvaloniaFact]
    public void The_prompts_leave_config_json_once_they_are_files()
    {
        // Otherwise they sit in the file the Config button opens, where editing them does nothing.
        CreateViewModel();

        new ConfigStore(configStore.ConfigPath).Load().PrebuiltCommands.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void A_prompt_added_on_disk_becomes_a_button()
    {
        var viewModel = CreateViewModel();
        new PromptLibrary(PromptDirectory).Write("shorter", "Shorter", 5, "Halve the length.");

        viewModel.OnPromptsChanged();

        viewModel.PrebuiltCommands.Select(c => c.Label).ShouldBe(["Shorter", "Grammar", "Prompt"]);
    }

    [AvaloniaFact]
    public async Task A_button_built_from_a_file_sends_what_the_file_says()
    {
        var viewModel = CreateViewModel();
        new PromptLibrary(PromptDirectory).Write("shorter", "Shorter", 5, "Halve the length.");
        viewModel.OnPromptsChanged();

        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        claude.Calls.ShouldHaveSingleItem().Command.ShouldBe("Halve the length.");
        viewModel.CommandLog[0].Source.ShouldBe("Shorter");
    }

    [AvaloniaFact]
    public void A_prompt_file_that_holds_no_prompt_is_named()
    {
        // A button that is simply not there looks exactly like one that was never written.
        var viewModel = CreateViewModel();
        File.WriteAllText(Path.Combine(PromptDirectory, "empty.md"), "---\nlabel: Empty\n---\n");

        viewModel.OnPromptsChanged();

        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("empty.md");
    }

    [AvaloniaFact]
    public void The_several_events_one_write_raises_do_not_churn_the_buttons()
    {
        var viewModel = CreateViewModel();
        var before = viewModel.PrebuiltCommands[0];

        viewModel.OnPromptsChanged();
        viewModel.OnPromptsChanged();

        viewModel.PrebuiltCommands[0].ShouldBeSameAs(before);
    }

    // ---------------------------------------------------------------- the shared config file

    [AvaloniaFact]
    public void A_config_written_by_another_window_is_picked_up()
    {
        // One config.json, one per-repo window each: the second window's microphone used to reach
        // the first only on a restart, and whoever saved last silently won.
        var viewModel = CreateViewModel();

        AnotherWindowWrites(c => c.SttMode = SttMode.Batch);
        viewModel.OnConfigFileChanged();

        viewModel.SelectedSttMode.ShouldBe(SttMode.Batch);
        viewModel.StatusMessage.ShouldBe("Config changed on disk - reloaded.");
    }

    [AvaloniaFact]
    public void A_save_by_this_window_is_not_read_back_as_a_change()
    {
        // Every setting is saved as the whole file, so the window's own writes raise the same
        // events. Reloading them would restart the hotkey service on every microphone pick.
        var viewModel = CreateViewModel();
        viewModel.SelectedSttMode = SttMode.Batch;
        var statusBefore = viewModel.StatusMessage;
        var startsBefore = hotkeys.Starts;

        viewModel.OnConfigFileChanged();

        viewModel.StatusMessage.ShouldBe(statusBefore);
        hotkeys.Starts.ShouldBe(startsBefore);
    }

    [AvaloniaFact]
    public async Task A_config_change_during_a_take_waits_for_the_stop()
    {
        // Nobody asked for this reload, so it does not get to swap the backend, the microphone or
        // the hotkeys under a recording that is already running with the old ones.
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        AnotherWindowWrites(c => c.SttMode = SttMode.Batch);
        viewModel.OnConfigFileChanged();

        viewModel.SelectedSttMode.ShouldBe(SttMode.Realtime);

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.SelectedSttMode.ShouldBe(SttMode.Batch);

        // One sentence, not two: the stop said "Stopped." and the reload used to overwrite it.
        viewModel.StatusMessage.ShouldBe("Stopped. The config changed on disk and was reloaded.");
    }

    [AvaloniaFact]
    public async Task A_config_change_does_not_land_while_a_hold_is_only_starting()
    {
        // Between the hold key going down and the Commanding state there are up to five seconds
        // while channel 1 is paused. The machine still reads Recording and activeCommand is still
        // null in there, but a key is physically held: reloading would reinstall the keyboard hook
        // underneath it and the key-up would never be seen.
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        AnotherWindowWrites(c => c.SttMode = SttMode.Batch);
        viewModel.OnConfigFileChanged();

        var pause = new TaskCompletionSource();
        engines.Last!.StopGate = pause;

        hotkeys.PressCommandKey();
        await PumpAsync();

        // The stop lands while the hold is still starting, and takes the session to Idle.
        var startsBefore = hotkeys.Starts;
        var stopping = viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await PumpAsync();

        viewModel.SelectedSttMode.ShouldBe(SttMode.Realtime);
        hotkeys.Starts.ShouldBe(startsBefore);

        pause.SetResult();
        await stopping;
        capture.Emit(new byte[640]);
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        viewModel.SelectedSttMode.ShouldBe(SttMode.Batch);
    }

    [AvaloniaFact]
    public void A_hotkey_switch_flipped_in_another_window_is_said_out_loud()
    {
        // The switch is a privacy control over a system-wide keyboard hook. Propagating "off" is
        // the point; "on" arriving by itself, from a window the user is not looking at, is the
        // half that must not be silent.
        var viewModel = CreateViewModel();
        viewModel.HotkeysEnabled.ShouldBeTrue();

        AnotherWindowWrites(c => c.Hotkeys.Enabled = false);
        viewModel.OnConfigFileChanged();

        viewModel.HotkeysEnabled.ShouldBeFalse();
        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("hotkeys are off");

        AnotherWindowWrites(c => c.Hotkeys.Enabled = true);
        viewModel.OnConfigFileChanged();

        viewModel.HotkeysEnabled.ShouldBeTrue();
        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("hotkeys are on");
    }

    [AvaloniaFact]
    public void A_config_that_cannot_be_parsed_is_not_adopted_as_defaults()
    {
        // Adopting defaults here loses every keyterm, prompt and chord in memory - and the next
        // setting change writes them over the file the user is in the middle of fixing.
        var viewModel = CreateViewModel();
        viewModel.SelectedSttMode = SttMode.Batch;

        File.WriteAllText(configStore.ConfigPath, "{ this is not json");
        viewModel.OnConfigFileChanged();

        viewModel.SelectedSttMode.ShouldBe(SttMode.Batch);
        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("could not be read");
    }

    [AvaloniaFact]
    public void The_reload_button_refuses_a_config_it_cannot_parse()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedSttMode = SttMode.Batch;

        File.WriteAllText(configStore.ConfigPath, "{ this is not json");
        viewModel.ReloadConfigCommand.Execute(null);

        viewModel.SelectedSttMode.ShouldBe(SttMode.Batch);
        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("could not be read");
    }

    [AvaloniaFact]
    public void Applying_another_windows_config_does_not_write_it_straight_back()
    {
        // A key this build does not know - which is what a preview build's config looks like to a
        // stable one now that the two ship side by side. MumblrConfig drops unknown properties on
        // load, so a reload that echoed the file back would delete it, wake the other window, and
        // the two would write at each other for as long as both are open.
        var viewModel = CreateViewModel();
        AnotherWindowWrites(c => c.SttMode = SttMode.Batch);

        var raw = File.ReadAllText(configStore.ConfigPath).TrimEnd();
        File.WriteAllText(configStore.ConfigPath, raw[..raw.LastIndexOf('}')] + ",\n  \"somethingNewer\": 1\n}");

        viewModel.OnConfigFileChanged();

        viewModel.SelectedSttMode.ShouldBe(SttMode.Batch);
        File.ReadAllText(configStore.ConfigPath).ShouldContain("somethingNewer");
    }

    [AvaloniaFact]
    public async Task A_config_change_during_a_command_waits_for_it_to_finish()
    {
        var viewModel = CreateViewModel();
        editor.Text = "Erster Satz. Zweiter Satz.";
        claude.FileContentAfterRun = "Erster Satz.";
        // Read out here rather than asserted inside the runner: a failed assertion in there is
        // caught by the command's own error handling and the test would pass on the wrong reason.
        SttMode? modeWhileClaudeRan = null;
        claude.AsyncBehaviour = async (_, _) =>
        {
            AnotherWindowWrites(c => c.SttMode = SttMode.Batch);
            viewModel!.OnConfigFileChanged();
            modeWhileClaudeRan = viewModel.SelectedSttMode;
            await Task.Yield();
            return new CommandResult(true, "Removed the last sentence.", "{}", TimeSpan.FromSeconds(1));
        };

        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();

        claude.Calls.Count.ShouldBe(1);
        modeWhileClaudeRan.ShouldBe(SttMode.Realtime);
        viewModel.SelectedSttMode.ShouldBe(SttMode.Batch);
    }

    // ---------------------------------------------------------------- attention

    [AvaloniaFact]
    public async Task Recording_asks_for_attention_and_stopping_releases_it()
    {
        var viewModel = CreateViewModel();

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        attention.Wanted.ShouldBeTrue();
        attention.Begins.ShouldBe(1);
        viewModel.WindowTitle.ShouldBe("\u25CF Recording - mumblr");

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        attention.Wanted.ShouldBeFalse();
        attention.Begins.ShouldBe(1);
        viewModel.WindowTitle.ShouldBe("mumblr");
    }

    [AvaloniaFact]
    public async Task Shutting_down_during_a_recording_releases_the_attention()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        viewModel.Shutdown();

        attention.Wanted.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task A_command_on_its_own_does_not_ask_for_attention()
    {
        // Claude working costs money too, but it ends on its own; the flash is for the one
        // state only the user can end.
        var viewModel = CreateViewModel();

        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);

        attention.Begins.ShouldBe(0);
        viewModel.WindowTitle.ShouldBe("mumblr");
    }

    [AvaloniaFact]
    public async Task A_command_in_the_middle_of_a_recording_pauses_the_attention_and_resumes_it()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        hotkeys.PressCommandKey();
        await PumpAsync();
        viewModel.IsCommanding.ShouldBeTrue();
        attention.Wanted.ShouldBeFalse();

        hotkeys.ReleaseCommandKey();
        await PumpAsync();
        viewModel.IsRecording.ShouldBeTrue();
        attention.Wanted.ShouldBeTrue();
    }

    // ---------------------------------------------------------------- raw dictation

    private string RawPath => viewModel!.DocumentPath[..^".md".Length] + ".raw.md";

    private async Task DictateAsync(MainViewModel viewModel, params string[] segments)
    {
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        foreach (var segment in segments)
            engines.Last!.Commit(segment);
        await PumpAsync();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task Raw_grows_with_each_segment_and_survives_a_command()
    {
        var viewModel = CreateViewModel();
        claude.FileContentAfterRun = "Anders.";

        await DictateAsync(viewModel, "Erster Satz.", "Zweiter Satz.");
        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);

        editor.Text.ShouldBe("Anders.");
        File.ReadAllText(RawPath).ShouldBe("Erster Satz. Zweiter Satz.");
    }

    [AvaloniaFact]
    public async Task Raw_restores_the_dictation_after_two_commands_and_is_revertible()
    {
        var viewModel = CreateViewModel();
        await DictateAsync(viewModel, "Eins zwei.");
        claude.FileContentAfterRun = "A";
        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        claude.FileContentAfterRun = "B";
        await viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);

        viewModel.CanRestoreRaw.ShouldBeTrue();
        viewModel.RestoreRawCommand.Execute(null);

        editor.Text.ShouldBe("Eins zwei.");
        File.ReadAllText(viewModel.DocumentPath).ShouldBe("Eins zwei.");
        // Not a prebuilt command and never sent to Claude, so the "prebuilt:" slot stays empty.
        viewModel.CommandLog[0].Source.ShouldBeEmpty();
        viewModel.CommandLog[0].CommandText.ShouldContain("raw");
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Succeeded);
        viewModel.CanRestoreRaw.ShouldBeFalse();

        viewModel.CanRevert.ShouldBeTrue();
        viewModel.RevertLastCommandCommand.Execute(null);

        editor.Text.ShouldBe("B");
        viewModel.CommandLog[0].Status.ShouldBe(CommandStatus.Reverted);
    }

    [AvaloniaFact]
    public async Task Raw_is_disabled_until_the_buffer_differs_from_what_was_said()
    {
        var viewModel = CreateViewModel();
        viewModel.CanRestoreRaw.ShouldBeFalse();

        await DictateAsync(viewModel, "Gesagt.");
        viewModel.CanRestoreRaw.ShouldBeFalse();

        editor.Text = "Gesagt. Getippt.";
        viewModel.CanRestoreRaw.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Raw_is_not_offered_when_the_buffer_differs_only_by_take_separators()
    {
        // Raw joins takes with a paragraph break, the buffer with a space; that is not "no longer
        // what was said".
        var viewModel = CreateViewModel();

        await DictateAsync(viewModel, "eins");
        await DictateAsync(viewModel, "zwei");

        editor.Text.ShouldBe("eins zwei");
        File.ReadAllText(RawPath).ShouldBe("eins\n\nzwei");
        viewModel.CanRestoreRaw.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task A_raw_write_failure_warns_and_keeps_the_segment_in_the_buffer()
    {
        var viewModel = CreateViewModel();
        Directory.CreateDirectory(RawPath); // a directory where the raw file should go

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Commit("eins");
        engines.Last.Commit("zwei");
        await PumpAsync();

        // Both segments are in the buffer: the failure costs the raw file, not the dictation,
        // and not the rest of the queue behind it.
        editor.Text.ShouldBe("eins zwei");
        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("raw");

        // And the warning survives the end of the recording, which informs over the status line.
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        viewModel.IsRecording.ShouldBeFalse();
        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("raw");
    }

    [AvaloniaFact]
    public async Task Raw_is_refused_while_recording_and_while_claude_works()
    {
        var viewModel = CreateViewModel();
        await DictateAsync(viewModel, "Gesagt.");
        editor.Text = "Anders.";

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        viewModel.CanRestoreRaw.ShouldBeFalse();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        var gate = new TaskCompletionSource<CommandResult>();
        claude.AsyncBehaviour = (_, _) => gate.Task;
        var running = viewModel.RunPrebuiltCommand.ExecuteAsync(viewModel.PrebuiltCommands[0]);
        await PumpAsync();
        viewModel.CanRestoreRaw.ShouldBeFalse();
        gate.SetResult(new CommandResult(true, "done", "{}", TimeSpan.FromSeconds(1)));
        await running;
    }

    [AvaloniaFact]
    public async Task A_command_in_the_middle_of_a_recording_does_not_split_the_take_but_a_new_recording_does()
    {
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Commit("eins");
        await PumpAsync();

        hotkeys.PressCommandKey();
        await PumpAsync();
        capture.Emit(new byte[640]);
        hotkeys.ReleaseCommandKey();
        await PumpAsync();
        viewModel.IsRecording.ShouldBeTrue();

        engines.Last!.Commit("zwei");
        await PumpAsync();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await DictateAsync(viewModel, "drei");

        File.ReadAllText(RawPath).ShouldBe("eins zwei\n\ndrei");
    }

    // ---------------------------------------------------------------- language picker

    [AvaloniaFact]
    public void The_language_picker_offers_auto_and_the_configured_codes()
    {
        var viewModel = CreateViewModel();

        viewModel.Languages.ShouldBe(["auto", "de", "en"]);
        viewModel.SelectedLanguage.ShouldBe("auto");
        viewModel.SttStatusText.ShouldNotContain("auto");
    }

    [AvaloniaFact]
    public async Task Picking_a_language_persists_and_reaches_the_next_session()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedLanguage = "de";

        new ConfigStore(configStore.ConfigPath).Load().Stt.LanguageCode.ShouldBe("de");
        viewModel.SttStatusText.ShouldEndWith("de");

        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Options!.LanguageCode.ShouldBe("de");
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);

        viewModel.SelectedLanguage = "auto";

        new ConfigStore(configStore.ConfigPath).Load().Stt.LanguageCode.ShouldBeNull();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Options!.LanguageCode.ShouldBeNull();
    }

    [AvaloniaFact]
    public async Task A_language_picked_during_a_recording_applies_to_the_next_session()
    {
        // A running websocket has its language already; the picker is disabled in the window
        // while recording, and a change that gets through anyway waits for the next session.
        var viewModel = CreateViewModel();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        var running = engines.Last!;

        viewModel.SelectedLanguage = "en";

        running.Options!.LanguageCode.ShouldBeNull();
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        engines.Last!.Options!.LanguageCode.ShouldBe("en");
    }

    [AvaloniaFact]
    public void The_language_picker_treats_any_casing_of_auto_as_auto()
    {
        config.Stt.LanguageCode = "AUTO";
        configStore.Save(config);

        var viewModel = CreateViewModel();

        viewModel.SttStatusText.ShouldNotContain("AUTO");
        Mumblr.Core.Stt.SttSessionOptionsFactory.ForRecording(configStore.Load(), SttMode.Realtime)
            .LanguageCode.ShouldBeNull();
    }

    [AvaloniaFact]
    public void A_null_language_list_in_the_config_does_not_stop_the_app()
    {
        config.Stt.Languages = null!;
        configStore.Save(config);

        var viewModel = CreateViewModel();

        viewModel.Languages.ShouldBe(["auto", "de", "en"]);
    }

    [AvaloniaFact]
    public void A_config_save_failure_on_a_device_change_warns_instead_of_crashing()
    {
        devices.Devices.Add(new Mumblr.Core.Audio.AudioDeviceInfo("dev-2", "Webcam"));
        var viewModel = CreateViewModel();
        MakeConfigUnwritable();

        viewModel.SelectedDevice = viewModel.Devices.Single(device => device.Id == "dev-2");

        viewModel.SelectedDevice!.Id.ShouldBe("dev-2");
        viewModel.IsWarning.ShouldBeTrue();
        viewModel.StatusMessage.ShouldContain("config");
    }

    [AvaloniaFact]
    public void An_unknown_configured_code_is_offered_as_is()
    {
        config.Stt.LanguageCode = "fr";
        configStore.Save(config);

        var viewModel = CreateViewModel();

        viewModel.Languages.ShouldBe(["auto", "de", "en", "fr"]);
        viewModel.SelectedLanguage.ShouldBe("fr");
    }

    [AvaloniaFact]
    public void A_config_reload_re_reads_the_picker()
    {
        var viewModel = CreateViewModel();
        var edited = configStore.Load();
        edited.Stt.LanguageCode = "en";
        edited.Stt.Languages = ["de", "en", "fr"];
        configStore.Save(edited);

        viewModel.ReloadConfigCommand.Execute(null);

        viewModel.Languages.ShouldBe(["auto", "de", "en", "fr"]);
        viewModel.SelectedLanguage.ShouldBe("en");
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

        TestDirectories.Delete(workspace);
    }
}

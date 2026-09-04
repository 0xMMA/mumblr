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
        viewModel = new MainViewModel(workspace, editor, configStore, devices, capture, hotkeys, claude, engines);
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

    public void Dispose()
    {
        // The WAV file stays open for the whole session, so the view model has to go first:
        // Windows refuses to delete a directory that still holds an open handle.
        viewModel?.Shutdown();

        if (Directory.Exists(workspace))
            Directory.Delete(workspace, recursive: true);
    }
}

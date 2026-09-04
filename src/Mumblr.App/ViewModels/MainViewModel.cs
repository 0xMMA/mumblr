using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mumblr.App.Audio;
using Mumblr.App.Hotkeys;
using Mumblr.App.Updates;
using Mumblr.Core.Audio;
using Mumblr.Core.Commands;
using Mumblr.Core.Config;
using Mumblr.Core.Documents;
using Mumblr.Core.Hotkeys;
using Mumblr.Core.State;
using Mumblr.Core.Stt;
using Mumblr.Core.Text;

namespace Mumblr.App.ViewModels;

/// <summary>
/// Wires the state machine to the two channels: dictation into the editor, spoken commands into
/// <c>claude -p</c>. Every writer transition goes through <see cref="SessionStateMachine"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IEditorHost editor;
    private readonly ConfigStore configStore;
    private readonly SessionStateMachine machine = new();
    private readonly SnapshotStore snapshots = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly IAudioDeviceEnumerator deviceEnumerator;
    private readonly IAudioCapture capture;
    private readonly IHotkeyService hotkeys;
    private readonly IClaudeCommandRunner claudeRunner;
    private readonly ISttEngineFactory engineFactory;
    private readonly UpdateService updates = new();
    private readonly ConcurrentQueue<string> pendingSegments = new();
    private readonly object wavGate = new();

    private MumblrConfig config;
    private TextPostProcessor postProcessor;
    private DictationDocument? document;
    private WavWriter? wavWriter;
    private ISttEngine? engine;
    private MemoryStream? commandClip;
    private CommandLogItem? activeCommand;
    private bool capturingCommandClip;
    private int insertOffset;
    private bool suppressConfigSave = true;

    public MainViewModel(string targetDirectory, IEditorHost editor)
        : this(targetDirectory, editor, ConfigStore.Default(), CreateDeviceEnumerator(), CreateCapture(), CreateHotkeys())
    {
    }

    public MainViewModel(
        string targetDirectory,
        IEditorHost editor,
        ConfigStore configStore,
        IAudioDeviceEnumerator deviceEnumerator,
        IAudioCapture capture,
        IHotkeyService hotkeys,
        IClaudeCommandRunner? claudeRunner = null,
        ISttEngineFactory? engineFactory = null)
    {
        this.editor = editor;
        this.configStore = configStore;
        this.deviceEnumerator = deviceEnumerator;
        this.capture = capture;
        this.hotkeys = hotkeys;

        TargetDirectory = targetDirectory;
        config = configStore.Load();
        postProcessor = new TextPostProcessor(config.Dictionary);
        this.claudeRunner = claudeRunner ?? new ClaudeCommandRunner(() => config.Claude);
        this.engineFactory = engineFactory ?? new ElevenLabsSttEngineFactory(http);
        selectedSttMode = config.SttMode;

        this.capture.DataAvailable += OnAudioCaptured;
        this.capture.LevelChanged += OnLevelChanged;
        this.capture.Failed += OnCaptureFailed;

        this.hotkeys.Triggered += OnHotkey;
        this.hotkeys.CommandKeyDown += () => Dispatcher.UIThread.Post(() => _ = BeginCommandAsync());
        this.hotkeys.CommandKeyUp += () => Dispatcher.UIThread.Post(() => _ = EndCommandAsync());
        this.hotkeys.RegistrationFailed += message => Dispatcher.UIThread.Post(() => Warn(message));

        machine.StateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshState);
    }

    public string TargetDirectory { get; }

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];

    public ObservableCollection<CommandLogItem> CommandLog { get; } = [];

    /// <summary>One button per entry in the config, rebuilt whenever the config is reloaded.</summary>
    public ObservableCollection<PrebuiltCommand> PrebuiltCommands { get; } = [];

    public bool HasPrebuiltCommands => PrebuiltCommands.Count > 0;

    public IReadOnlyList<SttMode> SttModes { get; } = [SttMode.Realtime, SttMode.Batch];

    [ObservableProperty]
    private AudioDeviceInfo? selectedDevice;

    [ObservableProperty]
    private SttMode selectedSttMode;

    [ObservableProperty]
    private double level;

    [ObservableProperty]
    private string previewText = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool isWarning;

    [ObservableProperty]
    private string documentPath = string.Empty;

    [ObservableProperty]
    private string stateLabel = "Idle";

    [ObservableProperty]
    private bool isRecording;

    [ObservableProperty]
    private bool isCommanding;

    [ObservableProperty]
    private string hotkeyHint = string.Empty;

    [ObservableProperty]
    private string updateVersion = string.Empty;

    public bool HasUpdate => UpdateVersion.Length > 0;

    public bool HasPreview => PreviewText.Length > 0;

    public bool CanRevert => snapshots.CanRevert && machine.State != SessionState.Commanding;

    public string RecordButtonText => IsRecording ? "Stop" : "Record";

    /// <summary>Creates the dictation file and brings up devices and hotkeys.</summary>
    public void Initialize()
    {
        document = DictationDocument.Create(TargetDirectory);
        DocumentPath = document.MarkdownPath;

        RefreshDevices();
        RefreshPrebuiltCommands();
        ApplyHotkeys();
        RefreshState();

        if (ApiKeyProvider.TryGet() is null)
            Warn($"No API key. Set {ApiKeyProvider.PrimaryVariable} (or {ApiKeyProvider.FallbackVariable}) and restart.");
        else if (!IsWarning)
            // A device warning from RefreshDevices matters more than the file name, which the
            // toolbar shows anyway.
            Inform($"Writing to {Path.GetFileName(document.MarkdownPath)}");

        suppressConfigSave = false;

        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        var version = await updates.CheckAsync();
        if (version is not null)
            UpdateVersion = version;
    }

    partial void OnUpdateVersionChanged(string value) => OnPropertyChanged(nameof(HasUpdate));

    [RelayCommand]
    private void InstallUpdate()
    {
        Flush();
        capture.Stop();
        updates.ApplyAndRestart();
    }

    /// <summary>Hold-to-talk from the UI, for when the global hook is not an option.</summary>
    public void PressCommandButton() => _ = BeginCommandAsync();

    public void ReleaseCommandButton() => _ = EndCommandAsync();

    // ---------------------------------------------------------------- channel 1

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (machine.State == SessionState.Recording)
            await StopRecordingAsync();
        else
            await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (!machine.CanStartRecording || document is null)
            return;

        if (SelectedDevice is null)
        {
            Warn("Pick a microphone first - mumblr never falls back to the Windows default device.");
            return;
        }

        if (ApiKeyProvider.TryGet() is null)
        {
            Warn($"No API key. Set {ApiKeyProvider.PrimaryVariable} and restart.");
            return;
        }

        try
        {
            // The insert marker is taken once, at record start; committed segments go there, not to
            // wherever the caret happens to be later.
            insertOffset = Math.Clamp(editor.CaretOffset, 0, editor.Text.Length);

            await StartEngineAsync();
            lock (wavGate)
                wavWriter ??= new WavWriter(document.WavPath);
            capture.Start(SelectedDevice.Id);

            machine.TryStartRecording();
            RefreshState();
            Inform(SelectedSttMode == SttMode.Realtime ? "Recording - realtime" : "Recording - batch, text arrives on stop");
        }
        catch (Exception ex)
        {
            Warn($"Could not start recording: {ex.Message}");
            await SafeStopEngineAsync();
            capture.Stop();
        }
    }

    private async Task StopRecordingAsync()
    {
        if (!machine.CanStopRecording)
            return;

        capture.Stop();
        Level = 0;
        PreviewText = string.Empty;

        await SafeStopEngineAsync();
        DrainSegments();

        machine.TryStopRecording();
        RefreshState();

        Flush();
        await CopyToClipboardAsync(announce: false);

        // Starting a recording clears the warning flag, so one still set here belongs to this
        // recording - a rejected request, a dead microphone. The routine stop message must not
        // wipe it: that is what made a failing transcription look like a silent one.
        if (IsWarning)
            StatusMessage += " - stopped, buffer copied to the clipboard.";
        else
            Inform("Stopped - buffer copied to the clipboard.");
    }

    private async Task StartEngineAsync()
    {
        var options = SttSessionOptionsFactory.ForRecording(config, SelectedSttMode);

        engine = engineFactory.Create(SelectedSttMode);

        engine.SegmentCommitted += OnSegmentCommitted;
        engine.PartialTranscript += OnPartialTranscript;
        engine.Failed += OnEngineFailed;

        await engine.StartAsync(options);
    }

    private async Task SafeStopEngineAsync()
    {
        var current = engine;
        if (current is null)
            return;

        engine = null;

        try
        {
            await current.StopAsync();
        }
        catch (Exception ex)
        {
            Warn($"Transcription failed: {ex.Message}");
        }
        finally
        {
            current.SegmentCommitted -= OnSegmentCommitted;
            current.PartialTranscript -= OnPartialTranscript;
            current.Failed -= OnEngineFailed;
            await current.DisposeAsync();
        }
    }

    private void OnAudioCaptured(ReadOnlyMemory<byte> pcm)
    {
        if (capturingCommandClip)
        {
            commandClip?.Write(pcm.Span);
            return;
        }

        lock (wavGate)
            wavWriter?.Write(pcm.Span);

        var current = engine;
        if (current is not null)
            _ = PushAsync(current, pcm);
    }

    private async Task PushAsync(ISttEngine target, ReadOnlyMemory<byte> pcm)
    {
        try
        {
            await target.PushAudioAsync(pcm);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Warn($"Audio upload failed: {ex.Message}"));
        }
    }

    private void OnSegmentCommitted(string text)
    {
        pendingSegments.Enqueue(text);
        Dispatcher.UIThread.Post(DrainSegments);
    }

    private void OnPartialTranscript(string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            PreviewText = text;
            OnPropertyChanged(nameof(HasPreview));
        });

    private void OnEngineFailed(Exception ex) => Dispatcher.UIThread.Post(() => Warn(ex.Message));

    private void OnLevelChanged(float rms) =>
        Dispatcher.UIThread.Post(() => Level = Math.Clamp(rms * 4.0, 0, 1));

    private void OnCaptureFailed(Exception ex) =>
        Dispatcher.UIThread.Post(() => Warn($"Microphone stopped: {ex.Message}"));

    /// <summary>Appends every committed segment at the insert marker, in order.</summary>
    private void DrainSegments()
    {
        while (pendingSegments.TryDequeue(out var raw))
        {
            var text = postProcessor.Apply(raw).Trim();
            if (text.Length == 0)
                continue;

            var offset = Math.Clamp(insertOffset, 0, editor.Text.Length);
            var separator = NeedsSeparator(editor.Text, offset) ? " " : string.Empty;
            var chunk = separator + text;

            editor.Insert(offset, chunk);
            insertOffset = offset + chunk.Length;
        }

        PreviewText = string.Empty;
        OnPropertyChanged(nameof(HasPreview));
    }

    private static bool NeedsSeparator(string text, int offset) =>
        offset > 0 && !char.IsWhiteSpace(text[offset - 1]);

    // ---------------------------------------------------------------- channel 2

    /// <summary>
    /// What both command sources share before there is any command text: pause channel 1, take
    /// the state machine into Commanding, flush the buffer and open a log entry.
    /// </summary>
    private async Task<CommandLogItem?> PrepareCommandAsync()
    {
        if (!machine.CanStartCommand || document is null || activeCommand is not null)
            return null;

        if (machine.State == SessionState.Recording)
        {
            // Channel 1 pauses: stop the mic, close the take so its text lands in the file.
            capture.Stop();
            await SafeStopEngineAsync();
            DrainSegments();
        }

        machine.TryStartCommand();
        RefreshState();

        Flush();

        activeCommand = new CommandLogItem();
        CommandLog.Insert(0, activeCommand);
        return activeCommand;
    }

    private async Task BeginCommandAsync()
    {
        if (!machine.CanStartCommand || document is null || activeCommand is not null)
            return;

        // Only a spoken command needs a microphone. A prebuilt one runs without one.
        if (SelectedDevice is null)
        {
            Warn("Pick a microphone first.");
            return;
        }

        if (await PrepareCommandAsync() is null)
            return;

        commandClip = new MemoryStream();
        capturingCommandClip = true;

        try
        {
            capture.Start(SelectedDevice.Id);
        }
        catch (Exception ex)
        {
            capturingCommandClip = false;
            FailCommand($"Microphone unavailable: {ex.Message}");
        }
    }

    private async Task EndCommandAsync()
    {
        if (activeCommand is null || document is null)
            return;

        var entry = activeCommand;
        capturingCommandClip = false;
        capture.Stop();
        Level = 0;

        var clip = commandClip;
        commandClip = null;

        if (clip is null || clip.Length == 0)
        {
            FailCommand("Nothing recorded - hold the command key while speaking.");
            return;
        }

        entry.Status = CommandStatus.Transcribing;

        string commandText;
        try
        {
            var transcriber = engineFactory.CreateClipTranscriber();
            commandText = await transcriber.TranscribeAsync(clip.ToArray(), SttSessionOptionsFactory.ForCommandClip(config));
        }
        catch (Exception ex)
        {
            FailCommand($"Command transcription failed: {ex.Message}");
            return;
        }
        finally
        {
            clip.Dispose();
        }

        commandText = postProcessor.Apply(commandText).Trim();
        if (commandText.Length == 0)
        {
            FailCommand("Command was empty.");
            return;
        }

        await RunCommandAsync(entry, commandText);
    }

    /// <summary>
    /// The other half both sources share: snapshot, hand the text to <c>claude -p</c>, reload the
    /// file it edited. A prebuilt command differs from a spoken one only in where the text came
    /// from, so everything after that point is identical by construction.
    /// </summary>
    private async Task RunCommandAsync(CommandLogItem entry, string commandText)
    {
        if (document is null)
            return;

        entry.CommandText = commandText;
        entry.Engine = config.Claude.Describe();
        entry.Status = CommandStatus.Running;
        StatusMessage = "Claude is working...";

        // Snapshot before the call so the command can be undone.
        Flush();
        snapshots.Push(editor.Text, commandText);
        OnPropertyChanged(nameof(CanRevert));

        CommandResult result;
        try
        {
            result = await claudeRunner.RunAsync(commandText, document.MarkdownPath);
        }
        catch (Exception ex)
        {
            FailCommand($"claude failed: {ex.Message}");
            return;
        }

        entry.Duration = $"{result.Duration.TotalSeconds:0.0}s";
        entry.Response = result.Summary;
        entry.Status = result.Success ? CommandStatus.Succeeded : CommandStatus.Failed;

        if (result.Success)
        {
            // claude edited the file; the file is now the truth, so reload it into the buffer.
            var reloaded = document.Read();
            editor.Text = reloaded;
            insertOffset = reloaded.Length;
        }

        activeCommand = null;
        await FinishCommandAsync();
        Inform(result.Success ? result.Summary : $"Command failed: {result.Summary}");
    }

    private void FailCommand(string message)
    {
        if (activeCommand is not null)
        {
            activeCommand.Status = CommandStatus.Failed;
            activeCommand.Response = message;
            if (activeCommand.CommandText == "(listening...)")
                activeCommand.CommandText = "(no command)";
        }

        activeCommand = null;
        Warn(message);
        _ = FinishCommandAsync();
    }

    private async Task FinishCommandAsync()
    {
        machine.TryFinishCommand();
        RefreshState();

        if (machine.State == SessionState.Recording)
        {
            // Channel 1 resumes where the text now ends.
            try
            {
                insertOffset = editor.Text.Length;
                await StartEngineAsync();
                if (SelectedDevice is not null)
                    capture.Start(SelectedDevice.Id);

                Inform("Recording resumed.");
            }
            catch (Exception ex)
            {
                Warn($"Could not resume recording: {ex.Message}");
                machine.TryStopRecording();
                RefreshState();
            }
        }
    }

    [RelayCommand]
    private async Task RunPrebuiltAsync(PrebuiltCommand? prebuilt)
    {
        if (prebuilt is null || string.IsNullOrWhiteSpace(prebuilt.Text))
            return;

        var entry = await PrepareCommandAsync();
        if (entry is null)
            return;

        entry.Source = prebuilt.Label;
        await RunCommandAsync(entry, prebuilt.Text.Trim());
    }

    [RelayCommand]
    private void RevertLastCommand()
    {
        if (document is null || machine.State == SessionState.Commanding)
            return;

        if (!snapshots.TryPop(out var snapshot))
        {
            Inform("Nothing to revert.");
            return;
        }

        editor.Text = snapshot.Content;
        document.Flush(snapshot.Content);
        insertOffset = snapshot.Content.Length;

        var entry = CommandLog.FirstOrDefault(item => item.CommandText == snapshot.Label && item.Status == CommandStatus.Succeeded);
        if (entry is not null)
            entry.Status = CommandStatus.Reverted;

        OnPropertyChanged(nameof(CanRevert));
        Inform($"Reverted: {snapshot.Label}");
    }

    // ---------------------------------------------------------------- output

    [RelayCommand]
    private async Task CopyAsync() => await CopyToClipboardAsync(announce: true);

    private async Task CopyToClipboardAsync(bool announce)
    {
        Flush();

        var copied = await editor.CopyToClipboardAsync(editor.Text);
        if (announce)
            Inform(copied ? "Copied to the clipboard." : "Clipboard unavailable.");
    }

    /// <summary>Writes the in-memory buffer to the file. Called on every state change and on copy.</summary>
    private void Flush() => document?.Flush(editor.Text);

    // ---------------------------------------------------------------- devices, config, state

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var device in deviceEnumerator.GetCaptureDevices())
            Devices.Add(device);

        var configured = deviceEnumerator.Find(config.MicrophoneDeviceId);
        if (configured is not null)
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == configured.Id);
            return;
        }

        SelectedDevice = null;
        if (Devices.Count == 0)
            Warn("No capture devices found.");
        else if (!string.IsNullOrEmpty(config.MicrophoneDeviceId))
            Warn($"Configured microphone '{config.MicrophoneDeviceName}' is gone - pick another one.");
        else
            Inform("Pick a microphone to start.");
    }

    [RelayCommand]
    private void OpenConfig()
    {
        try
        {
            configStore.Save(config);
            Process.Start(new ProcessStartInfo(configStore.ConfigPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn($"Could not open the config: {ex.Message}");
        }
    }

    private void RefreshPrebuiltCommands()
    {
        PrebuiltCommands.Clear();
        foreach (var prebuilt in config.PrebuiltCommands)
            if (!string.IsNullOrWhiteSpace(prebuilt.Label) && !string.IsNullOrWhiteSpace(prebuilt.Text))
                PrebuiltCommands.Add(prebuilt);

        OnPropertyChanged(nameof(HasPrebuiltCommands));
    }

    [RelayCommand]
    private void ReloadConfig()
    {
        config = configStore.Load();
        postProcessor = new TextPostProcessor(config.Dictionary);

        suppressConfigSave = true;
        SelectedSttMode = config.SttMode;
        suppressConfigSave = false;

        ApplyHotkeys();
        RefreshDevices();
        RefreshPrebuiltCommands();
        Inform("Config reloaded.");
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (suppressConfigSave || value is null)
            return;

        config.MicrophoneDeviceId = value.Id;
        config.MicrophoneDeviceName = value.Name;
        configStore.Save(config);
    }

    partial void OnSelectedSttModeChanged(SttMode value)
    {
        if (suppressConfigSave)
            return;

        config.SttMode = value;
        configStore.Save(config);
    }

    partial void OnPreviewTextChanged(string value) => OnPropertyChanged(nameof(HasPreview));

    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordButtonText));

    private void ApplyHotkeys()
    {
        hotkeys.Start(config.Hotkeys);
        HotkeyHint = hotkeys.IsSupported
            ? $"{config.Hotkeys.ToggleRecording} record  ·  hold {config.Hotkeys.CommandHoldKey} command  ·  " +
              $"{config.Hotkeys.Copy} copy  ·  {config.Hotkeys.RevertCommand} revert"
            : "Global hotkeys need Windows - use the buttons.";
    }

    private void OnHotkey(HotkeyAction action) => Dispatcher.UIThread.Post(() =>
    {
        switch (action)
        {
            case HotkeyAction.ToggleRecording:
                _ = ToggleRecordingAsync();
                break;
            case HotkeyAction.Copy:
                _ = CopyAsync();
                break;
            case HotkeyAction.RevertCommand:
                RevertLastCommand();
                break;
        }
    });

    private void RefreshState()
    {
        IsRecording = machine.State == SessionState.Recording;
        IsCommanding = machine.State == SessionState.Commanding;
        StateLabel = machine.State switch
        {
            SessionState.Recording => "Recording",
            SessionState.Commanding => "Claude is working",
            _ => "Idle",
        };

        editor.IsReadOnly = machine.IsEditorLocked;
        OnPropertyChanged(nameof(CanRevert));
        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    private void Inform(string message)
    {
        StatusMessage = message;
        IsWarning = false;
    }

    private void Warn(string message)
    {
        StatusMessage = message;
        IsWarning = true;
    }

    public void Shutdown()
    {
        try
        {
            capture.Stop();
            Flush();
        }
        catch (Exception)
        {
            // Shutting down; nothing left to report to.
        }

        Dispose();
    }

    public void Dispose()
    {
        hotkeys.Dispose();
        capture.Dispose();

        lock (wavGate)
        {
            wavWriter?.Dispose();
            wavWriter = null;
        }

        commandClip?.Dispose();
        http.Dispose();
    }

    private static IAudioDeviceEnumerator CreateDeviceEnumerator() =>
        OperatingSystem.IsWindows() ? new WasapiDeviceEnumerator() : new NullAudioDeviceEnumerator();

    private static IAudioCapture CreateCapture() =>
        OperatingSystem.IsWindows() ? new WasapiAudioCapture() : new NullAudioCapture();

    private static IHotkeyService CreateHotkeys() =>
        OperatingSystem.IsWindows() ? new Win32HotkeyService() : new NullHotkeyService();
}

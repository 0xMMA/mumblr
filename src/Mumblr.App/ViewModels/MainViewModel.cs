using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
    private readonly IUpdateService updates;
    private readonly ConcurrentQueue<string> pendingSegments = new();
    private readonly object wavGate = new();

    private MumblrConfig config;
    private TextPostProcessor postProcessor;
    private DictationDocument? document;
    private WavWriter? wavWriter;
    private ISttEngine? engine;
    private MemoryStream? commandClip;
    private CommandLogItem? activeCommand;

    /// <summary>Set by the failure paths of the running recording; cleared only when one starts.</summary>
    private string? recordingFailure;

    /// <summary>Guards the window inside PrepareCommandAsync where pausing channel 1 is awaited.</summary>
    private bool commandStarting;
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
        ISttEngineFactory? engineFactory = null,
        IUpdateService? updates = null)
    {
        this.updates = updates ?? new UpdateService();
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
        this.editor.TextChanged += UpdateCounters;

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

    /// <summary>What channel 1's backend is doing right now, for the status bar.</summary>
    [ObservableProperty]
    private string engineStatus = "idle";

    [ObservableProperty]
    private int characterCount;

    [ObservableProperty]
    private bool hasApiKey;

    public bool HasUpdate => UpdateVersion.Length > 0;

    /// <summary>
    /// The running build, so a bug report can name the version it came from. MinVer writes the
    /// informational version, which says "0.1.2-alpha.0.7" for a build between tags where the
    /// assembly version would flatten that to a released-looking 0.1.2. The commit hash after the
    /// "+" is dropped: it belongs in a report, not in a status bar.
    /// </summary>
    public string Version { get; } = ResolveVersion(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3));

    internal static string ResolveVersion(string? informational, string? assemblyVersion)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return string.IsNullOrWhiteSpace(assemblyVersion) ? "dev" : assemblyVersion;

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    /// <summary>Presence only. mumblr never reads the key from config, and never displays it.</summary>
    public string ApiStatusText => HasApiKey ? "API key" : "no API key";

    public string MicrophoneLabel => SelectedDevice?.Name ?? "no microphone";

    public string SttStatusText => $"{SelectedSttMode} - {EngineStatus}";

    public string VersionButtonText => HasUpdate ? $"update to {UpdateVersion}" : $"v{Version}";

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

        HasApiKey = ApiKeyProvider.TryGet() is not null;
        UpdateCounters();

        if (!HasApiKey)
            Warn($"No API key. Set {ApiKeyProvider.PrimaryVariable} (or {ApiKeyProvider.FallbackVariable}) and restart.");
        else if (!IsWarning)
            // A device warning from RefreshDevices matters more than the file name, which the
            // toolbar shows anyway.
            Inform($"Writing to {Path.GetFileName(document.MarkdownPath)}");

        suppressConfigSave = false;

        _ = CheckForUpdatesAsync();
    }

    private async Task<UpdateService.UpdateCheck> CheckForUpdatesAsync()
    {
        var outcome = await updates.CheckAsync();
        if (outcome == UpdateService.UpdateCheck.Available && updates.AvailableVersion is { } version)
            UpdateVersion = version;

        return outcome;
    }

    partial void OnUpdateVersionChanged(string value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(VersionButtonText));
    }

    partial void OnHasApiKeyChanged(bool value) => OnPropertyChanged(nameof(ApiStatusText));

    partial void OnEngineStatusChanged(string value) => OnPropertyChanged(nameof(SttStatusText));

    /// <summary>The version button is the update button once there is something to install.</summary>
    [RelayCommand]
    private async Task UseVersionButtonAsync()
    {
        if (HasUpdate)
        {
            InstallUpdate();
            return;
        }

        Inform("Checking for updates...");

        switch (await CheckForUpdatesAsync())
        {
            case UpdateService.UpdateCheck.Available:
                Inform($"Update {UpdateVersion} available.");
                break;
            case UpdateService.UpdateCheck.UpToDate:
                Inform($"v{Version} is the latest build.");
                break;
            case UpdateService.UpdateCheck.NotInstalled:
                Inform("This build updates by replacing the folder, not from inside the app.");
                break;
            default:
                // Saying "up to date" here would be a claim the app never actually checked.
                Warn("Could not reach the release feed. Check the releases page manually.");
                break;
        }
    }

    [RelayCommand]
    private void OpenProjectPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn($"Could not open the project page: {ex.Message}");
        }
    }

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

        recordingFailure = null;

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

        // IsWarning cannot carry this: Copy, Revert and Reload all call Inform and are reachable
        // in the middle of a take, so a Ctrl+Alt+C forty seconds after a refused session would
        // erase the only trace of it. This latch is set by the failure paths of this recording
        // and cleared when the next one starts, by nothing else.
        if (recordingFailure is { Length: > 0 })
            Warn($"{recordingFailure} - stopped, buffer copied to the clipboard.");
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

        EngineStatus = "connecting";
        await engine.StartAsync(options);

        // Realtime returns from StartAsync once the websocket is up, so "connected" is a fact
        // rather than a guess. Batch holds the audio locally until stop, and says so.
        EngineStatus = SelectedSttMode == SttMode.Realtime ? "connected" : "buffering";
    }

    private async Task SafeStopEngineAsync()
    {
        var current = engine;
        if (current is null)
            return;

        engine = null;
        EngineStatus = "idle";

        try
        {
            await current.StopAsync();
        }
        catch (Exception ex)
        {
            FailRecording($"Transcription failed: {ex.Message}");
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
            Dispatcher.UIThread.Post(() => FailRecording($"Audio upload failed: {ex.Message}"));
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

    private void OnEngineFailed(Exception ex) => Dispatcher.UIThread.Post(() =>
    {
        // A failure posted from a backend that has since been stopped must not put the bar back
        // into "error" after the session is idle.
        if (engine is not null)
            EngineStatus = "error";

        FailRecording(ex.Message);
    });

    /// <summary>Cheap facts for the status bar; the buffer is the only source that can change.</summary>
    private void UpdateCounters() => CharacterCount = editor.Text.Length;

    /// <summary>
    /// The failure that belongs to the recording currently running, if any. Cleared when a
    /// recording starts and by nothing else, so no unrelated status message can lose it.
    /// </summary>
    private void FailRecording(string message)
    {
        recordingFailure = message;
        Warn(message);
    }

    private void OnLevelChanged(float rms) =>
        Dispatcher.UIThread.Post(() => Level = Math.Clamp(rms * 4.0, 0, 1));

    private void OnCaptureFailed(Exception ex) =>
        Dispatcher.UIThread.Post(() => FailRecording($"Microphone stopped: {ex.Message}"));

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
        UpdateCounters();
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
        // Claimed before the await below, not after: stopping a realtime backend waits for the
        // last segment or five seconds, and the hold key and a prebuilt button are separate
        // paths. Two of them inside that window would start two claude processes on one file.
        if (commandStarting || !machine.CanStartCommand || document is null || activeCommand is not null)
            return null;

        commandStarting = true;

        try
        {
            if (machine.State == SessionState.Recording)
            {
                // Channel 1 pauses: stop the mic, close the take so its text lands in the file.
                capture.Stop();
                await SafeStopEngineAsync();
                DrainSegments();
            }

            Level = 0;
            PreviewText = string.Empty;
            OnPropertyChanged(nameof(HasPreview));

            if (!machine.TryStartCommand())
                return null;

            RefreshState();
            Flush();

            activeCommand = new CommandLogItem();
            CommandLog.Insert(0, activeCommand);
            return activeCommand;
        }
        finally
        {
            commandStarting = false;
        }
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
        {
            FailCommand("No dictation file.");
            return;
        }

        entry.CommandText = commandText;
        entry.Engine = config.Claude.Describe();  // replaced below by whatever actually answered
        entry.Status = CommandStatus.Running;
        Inform("Claude is working...");

        CommandResult result;
        try
        {
            // Inside the try: a failing Flush or snapshot would otherwise throw out of a
            // RelayCommand and leave the session in Commanding with a read-only editor.
            Flush();
            snapshots.Push(editor.Text, commandText);
            OnPropertyChanged(nameof(CanRevert));

            result = await claudeRunner.RunAsync(commandText, document.MarkdownPath);
        }
        catch (Exception ex)
        {
            FailCommand($"claude failed: {ex.Message}");
            return;
        }

        entry.Duration = $"{result.Duration.TotalSeconds:0.0}s";
        entry.Response = result.Summary;
        entry.Engine = result.Model is { Length: > 0 } ? result.Model : config.Claude.Describe();
        entry.Status = result.Success ? CommandStatus.Succeeded : CommandStatus.Failed;

        if (result.Success)
        {
            try
            {
                // claude edited the file; the file is now the truth, so reload it into the buffer.
                var reloaded = document.Read();
                editor.Text = reloaded;
                insertOffset = reloaded.Length;
                UpdateCounters();
            }
            catch (Exception ex)
            {
                FailCommand($"Could not read the file back: {ex.Message}");
                return;
            }
        }

        activeCommand = null;

        if (result.Success)
        {
            await FinishCommandAsync();
            Inform(result.Summary);
        }
        else
        {
            // Order matters: FinishCommandAsync ends a resumed recording with "Recording
            // resumed.", which would erase the failure if it ran after the warning.
            await FinishCommandAsync();
            Warn($"Command failed: {result.Summary}");
        }
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

        // The warning goes up after the resume, for the same reason.
        _ = FinishCommandAsync().ContinueWith(
            _ => Warn(message),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
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
                // The engine may already be up - capture.Start is what usually throws here - and
                // an abandoned one keeps its websocket and its event subscriptions.
                await SafeStopEngineAsync();
                capture.Stop();
                FailRecording($"Could not resume recording: {ex.Message}");
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
        {
            Warn("Busy - wait for the running command to finish.");
            return;
        }

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
        foreach (var prebuilt in config.PrebuiltCommands ?? [])
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
        OnPropertyChanged(nameof(MicrophoneLabel));

        if (suppressConfigSave || value is null)
            return;

        config.MicrophoneDeviceId = value.Id;
        config.MicrophoneDeviceName = value.Name;
        configStore.Save(config);
    }

    partial void OnSelectedSttModeChanged(SttMode value)
    {
        OnPropertyChanged(nameof(SttStatusText));

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

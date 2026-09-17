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
using Mumblr.App.Attention;
using Mumblr.App.Config;
using Mumblr.App.Audio;
using Mumblr.App.Hotkeys;
using Mumblr.App.Updates;
using Mumblr.Core.Audio;
using Mumblr.Core.Commands;
using Mumblr.Core.Config;
using Mumblr.Core.Documents;
using Mumblr.Core.Hotkeys;
using Mumblr.Core.Prompts;
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

    /// <summary>Where the command buttons come from: markdown files in the user's own directory.</summary>
    private readonly PromptLibrary prompts;
    private readonly SessionStateMachine machine = new();
    private readonly SnapshotStore snapshots = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly IAudioDeviceEnumerator deviceEnumerator;
    private readonly IAudioCapture capture;
    private readonly IHotkeyService hotkeys;
    private readonly IAttentionService attention;
    private readonly IClaudeCommandRunner claudeRunner;
    private readonly ISttEngineFactory engineFactory;
    private readonly IUpdateService updates;
    private readonly ConcurrentQueue<string> pendingSegments = new();
    private readonly object wavGate = new();
    private readonly object clipGate = new();

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

    /// <summary>
    /// Carries a stop across a command that started inside its pause window. Stopping a realtime
    /// backend waits for the last segment or five seconds, and a command pressed in there reaches
    /// Commanding first - so TryStopRecording fails and the command would resume the recording the
    /// user just ended. Set before that await, consumed by whoever gets to finish the stop.
    /// </summary>
    private bool stopRequested;

    /// <summary>True while the global chords are actually registered, so tooltips can name them.</summary>
    private bool hotkeysActive;

    /// <summary>Another window wrote the shared config while this one was busy. Applied on the way back to Idle.</summary>
    private bool configReloadPending;

    /// <summary>Why the prompt files could not be written on start, if they could not.</summary>
    private string? promptSeedingFailure;

    /// <summary>The window is gone, so nothing may arm a watcher that nobody would ever dispose.</summary>
    private bool disposed;

    /// <summary>
    /// The prompt files that held no prompt the last time they were read. Kept so the window says
    /// so when that changes and not on every event: the reload runs on every config change and at
    /// the end of every take, and a warning there erases whatever the window was actually saying.
    /// </summary>
    private string[] skippedPrompts = [];

    /// <summary>The last warning written about those files, so only that line is taken back.</summary>
    private string? lastSkipWarning;

    /// <summary>
    /// The config file exists but could not be parsed, so this session is running on defaults. Said
    /// out loud on start: the next setting change writes those defaults over the file, and a typo
    /// that costs every keyterm and every prompt should not do so in silence.
    ///
    /// Not readonly: the reload button is how the file gets repaired without a restart, and what
    /// this guards - the prompt seeding - has to become possible again when it is.
    /// </summary>
    private bool configBroken;

    private IDisposable? configWatcher;
    private IDisposable? promptWatcher;

    /// <summary>
    /// How a watcher is built, given a directory and a filter. A seam, not a setting: the headless
    /// tests would otherwise run real FileSystemWatchers over their workspace and post events into
    /// the dispatcher they pump.
    /// </summary>
    private readonly Func<string, string, Action, IDisposable> fileWatcherFactory;

    /// <summary>The window between a command being asked for and the Commanding state.</summary>
    private bool CommandStarting
    {
        get => commandStarting;
        set
        {
            commandStarting = value;
            OnPropertyChanged(nameof(CanToggleHotkeys));
            ToggleHotkeysCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// The entry the spoken path owns. EndCommandAsync ends this one or nothing: a key-up that
    /// arrives while a prebuilt command runs used to fail that command's entry, unlock the editor
    /// and resume channel 1 underneath it.
    /// </summary>
    private CommandLogItem? spokenCommand;

    /// <summary>
    /// A key-up during the pause window - stopping a realtime backend waits up to five seconds -
    /// arrives before there is anything to end. Latched here so the release is not simply lost,
    /// which would leave the session stuck in Commanding with the microphone still running.
    /// </summary>
    private bool commandReleasedEarly;
    private bool capturingCommandClip;
    private int insertOffset;
    private bool suppressConfigSave = true;

    public MainViewModel(string targetDirectory, IEditorHost editor)
        : this(targetDirectory, editor, ConfigStore.Default(), CreateDeviceEnumerator(), CreateCapture(), CreateHotkeys(),
            attention: editor as IAttentionService)
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
        IUpdateService? updates = null,
        IAttentionService? attention = null,
        PromptLibrary? prompts = null,
        Func<string, string, Action, IDisposable>? fileWatcherFactory = null)
    {
        this.prompts = prompts ?? PromptLibrary.Default();
        this.fileWatcherFactory = fileWatcherFactory
            ?? ((directory, filter, changed) => new FileChangeWatcher(directory, filter, changed));
        this.updates = updates ?? new UpdateService();
        this.attention = attention ?? new NullAttention();
        this.editor = editor;
        this.configStore = configStore;
        this.deviceEnumerator = deviceEnumerator;
        this.capture = capture;
        this.hotkeys = hotkeys;

        TargetDirectory = targetDirectory;

        var loaded = configStore.LoadDetailed();
        configBroken = loaded.Broken;
        config = loaded.Config;
        postProcessor = new TextPostProcessor(config.Dictionary);
        this.claudeRunner = claudeRunner ?? new ClaudeCommandRunner(() => config.Claude);
        this.engineFactory = engineFactory ?? new ElevenLabsSttEngineFactory(http);
        selectedSttMode = config.SttMode;
        hotkeysEnabled = config.Hotkeys.Enabled;
        RefreshLanguages();

        this.capture.DataAvailable += OnAudioCaptured;
        this.capture.LevelChanged += OnLevelChanged;
        this.capture.Failed += OnCaptureFailed;
        this.editor.TextChanged += UpdateCounters;

        this.hotkeys.Triggered += OnHotkey;
        // Both ends of the hold are gated. A hold that began under the switch cannot outlive it -
        // the flip is refused while a command is starting or running - so a key-up that arrives
        // while off belongs to nothing, and letting it through would cut a mouse hold short.
        this.hotkeys.CommandKeyDown += () => Dispatcher.UIThread.Post(() => { if (HotkeysEnabled) _ = BeginCommandAsync(); });
        this.hotkeys.CommandKeyUp += () => Dispatcher.UIThread.Post(() => { if (HotkeysEnabled) _ = EndCommandAsync(); });
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

    public const string AutoLanguage = "auto";

    /// <summary>Auto plus the configured codes, plus whatever the config names that the list does not.</summary>
    public ObservableCollection<string> Languages { get; } = [];

    /// <summary>
    /// The transcription language for the next recording. A running websocket has its language
    /// already, so a change during a recording waits for the next session by construction.
    /// </summary>
    [ObservableProperty]
    private string? selectedLanguage;

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

    private bool hotkeysEnabled;

    /// <summary>What the taskbar button and Alt+Tab show. Recording has to be visible from other windows.</summary>
    [ObservableProperty]
    private string windowTitle = "mumblr";

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

    public string SttStatusText =>
        $"{SelectedSttMode} - {EngineStatus}" + (IsAutoLanguage(SelectedLanguage) ? string.Empty : $" \u00B7 {SelectedLanguage}");

    private static bool IsAutoLanguage(string? code) =>
        string.IsNullOrWhiteSpace(code) || string.Equals(code.Trim(), AutoLanguage, StringComparison.OrdinalIgnoreCase);

    public string VersionButtonText => HasUpdate ? $"update to {UpdateVersion} and restart" : $"v{Version}";

    public string VersionButtonTooltip => HasUpdate
        ? $"Install {UpdateVersion} and restart mumblr"
        : "Check for updates";

    public bool HasPreview => PreviewText.Length > 0;

    public bool CanRevert => snapshots.CanRevert && machine.State != SessionState.Commanding;

    /// <summary>
    /// Something was said, nobody is writing, and the buffer is no longer what was said. Compared
    /// word by word: raw joins takes with a paragraph break and the buffer with a space, and that
    /// is not a difference anyone dictated.
    /// </summary>
    public bool CanRestoreRaw =>
        document is { RawText.Length: > 0 } && machine.State == SessionState.Idle && !SameWords(editor.Text, document.RawText);

    /// <summary>
    /// Word by word without splitting either string: this runs on every keystroke, through the
    /// binding, over the whole buffer.
    /// </summary>
    private static bool SameWords(string a, string b)
    {
        var (i, j) = (0, 0);

        while (true)
        {
            while (i < a.Length && char.IsWhiteSpace(a[i])) i++;
            while (j < b.Length && char.IsWhiteSpace(b[j])) j++;

            if (i == a.Length || j == b.Length)
                return i == a.Length && j == b.Length;

            while (i < a.Length && !char.IsWhiteSpace(a[i]))
            {
                if (j == b.Length || char.IsWhiteSpace(b[j]) || a[i] != b[j])
                    return false;

                i++;
                j++;
            }

            if (j < b.Length && !char.IsWhiteSpace(b[j]))
                return false;
        }
    }

    /// <summary>
    /// The kill switch for the global hotkeys. The status bar button flips it through
    /// <see cref="ToggleHotkeysCommand"/>; there is deliberately no two-way binding, because a
    /// value refused inside its own change handler never reaches the control that wrote it.
    /// The refusal here is for every other caller and happens before the value changes.
    /// </summary>
    public bool HotkeysEnabled
    {
        get => hotkeysEnabled;
        set
        {
            if (hotkeysEnabled == value)
                return;

            if (!value && !CanToggleHotkeys && !suppressConfigSave)
            {
                Warn("Busy - wait for the running command to finish.");
                return;
            }

            hotkeysEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeySwitchText));

            if (suppressConfigSave)
                return;

            // Act first, persist second: this is a privacy control, and "off" has to mean off even
            // when the config file cannot be written. Every warning below outranks the confirmation.
            config.Hotkeys.Enabled = value;
            Inform(value ? "Hotkeys on." : "Hotkeys off. Nothing outside this window can start a recording.");
            ApplyHotkeys();
            TrySaveConfig();
        }
    }

    public string HotkeySwitchText => HotkeysEnabled ? "hotkeys: on" : "hotkeys: off";

    /// <summary>Unhooking under a live hold would swallow its key-up; see <see cref="CommandStarting"/>.</summary>
    public bool CanToggleHotkeys => !IsCommanding && !commandStarting;

    [RelayCommand(CanExecute = nameof(CanToggleHotkeys))]
    private void ToggleHotkeys() => HotkeysEnabled = !HotkeysEnabled;

    public string RecordButtonText => IsRecording ? "Stop" : "Record";

    public string RecordButtonTooltip => IsRecording
        ? "Stop the recording. The text stays in the buffer and in the file - copy it when you are done."
              + Chord(config.Hotkeys.ToggleRecording)
        : "Start dictating. Text is appended where the caret is now, and the editor locks until you stop."
              + Chord(config.Hotkeys.ToggleRecording);

    public string CommandButtonTooltip =>
        "Hold, say what to change - \"delete the last sentence\", \"clean this up\" - then release. "
        + "The clip goes to claude -p, which edits the file; the spoken words themselves never land in the text. "
        + "Watch it in the command log, undo it with Revert last."
        + Chord(config.Hotkeys.CommandHoldKey, "hold ");

    public string CopyButtonTooltip =>
        "Copy the whole buffer to the clipboard."
        + Chord(config.Hotkeys.Copy);

    /// <summary>Names the chord only while it is actually registered, so the tip never lies.</summary>
    private string Chord(string chord, string verb = "") =>
        hotkeysActive && !string.IsNullOrWhiteSpace(chord) ? $"  ({verb}{chord})" : string.Empty;

    private void RefreshButtonTooltips()
    {
        OnPropertyChanged(nameof(RecordButtonTooltip));
        OnPropertyChanged(nameof(CommandButtonTooltip));
        OnPropertyChanged(nameof(CopyButtonTooltip));
    }

    /// <summary>Creates the dictation file and brings up devices and hotkeys.</summary>
    public void Initialize()
    {
        document = DictationDocument.Create(TargetDirectory);
        DocumentPath = document.MarkdownPath;

        // The first run after the upgrade writes what config.json carried into files, and a fresh
        // install gets the shipped two. Once, and only when the directory is not there at all.
        //
        // Never over a config that did not parse: this session is running on defaults then, and
        // saving them back would replace the user's file - with their prompts still inside it -
        // before the warning below has even been drawn.
        SeedPrompts();

        RefreshDevices();
        RefreshPrebuiltCommands();
        ApplyHotkeys();
        RefreshState();

        HasApiKey = ApiKeyProvider.TryGet() is not null;
        UpdateCounters();

        if (configBroken)
            Warn("config.json could not be read - running on defaults. Fix the file and press the reload button, "
                 + "or the next setting you change writes the defaults over it.");
        else if (!HasApiKey)
            Warn($"No API key. Set {ApiKeyProvider.PrimaryVariable} (or {ApiKeyProvider.FallbackVariable}) and restart.");
        else if (promptSeedingFailure is { Length: > 0 })
            // Said here rather than where it happened: RefreshDevices is loud about a microphone
            // that is gone, and used to warn over the one notice that the migration did not run.
            // A missing key outranks it, and costs nothing - the next start reports it again.
            Warn(promptSeedingFailure);
        else if (!IsWarning)
            // A device warning from RefreshDevices matters more than the file name, which the
            // toolbar shows anyway.
            Inform($"Writing to {Path.GetFileName(document.MarkdownPath)}");

        suppressConfigSave = false;

        configWatcher = fileWatcherFactory(
            Path.GetDirectoryName(configStore.ConfigPath) ?? string.Empty,
            Path.GetFileName(configStore.ConfigPath),
            () => Dispatcher.UIThread.Post(OnConfigFileChanged));

        // Seeding arms one itself when it has just created the folder; this is for every other
        // start, where the folder was already there.
        if (promptWatcher is null)
            ArmPromptWatcher();

        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// The shared config.json changed on disk - another window picked a microphone, or the file was
    /// edited while this window was open. Public because the watcher is deliberately thin: it says
    /// "something happened", and every decision about what that is worth lives here.
    /// </summary>
    public void OnConfigFileChanged()
    {
        // An event already in flight when the window closed. Dispose clears this, and the work
        // below reaches the hotkey service, which by then is gone.
        if (configWatcher is null)
            return;

        // Our own saves raise the same events, and one write raises several.
        if (!configStore.ChangedOnDisk())
            return;

        // Nobody asked for this reload, so it waits for a quiet moment. Applying it would restart
        // the hotkey service under a running hold and swap the microphone and the STT mode under a
        // take that is already recording with the old ones.
        if (IsBusy)
        {
            configReloadPending = true;
            return;
        }

        if (!LoadConfigFromDisk())
        {
            Warn("config.json changed on disk but could not be read - nothing was changed.");
            return;
        }

        if (!IsWarning)
            Inform("Config changed on disk - reloaded.");
    }

    /// <summary>
    /// Applies a reload that arrived while the session was busy, now that it is not. Silent: the
    /// caller is in the middle of saying something of its own, and phrases both.
    /// </summary>
    private bool ApplyPendingConfigReload()
    {
        if (!configReloadPending || IsBusy)
            return false;

        return LoadConfigFromDisk();
    }

    /// <summary>
    /// Anything that a reload must not land in the middle of. commandStarting is the one that is
    /// easy to miss: between the hold key going down and the Commanding state there is a window -
    /// up to five seconds while channel 1 is paused - where the machine still reads Recording or
    /// Idle and activeCommand is still null, but a key is physically held down. Reloading there
    /// reinstalls the keyboard hook underneath it and the key-up is never seen.
    /// </summary>
    private bool IsBusy => machine.State != SessionState.Idle || commandStarting || activeCommand is not null;

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
        OnPropertyChanged(nameof(VersionButtonTooltip));
    }

    partial void OnHasApiKeyChanged(bool value) => OnPropertyChanged(nameof(ApiStatusText));

    partial void OnEngineStatusChanged(string value) => OnPropertyChanged(nameof(SttStatusText));

    /// <summary>The version button is the update button once there is something to install.</summary>
    [RelayCommand]
    private async Task UseVersionButtonAsync()
    {
        if (HasUpdate)
        {
            // Restarting on top of a running command would abandon claude mid-edit.
            if (machine.State == SessionState.Commanding)
            {
                Warn("Claude is still working - wait before updating.");
                return;
            }

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
            case UpdateService.UpdateCheck.NoReleases:
                // A preview build whose channel holds nothing. Silence here would read as
                // "you have the newest", which is the one thing it certainly does not mean.
                Warn("No release on this build's channel. Check the releases page.");
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

    /// <summary>
    /// Hold-to-talk from the UI, for when the global hook is not an option. The button is never
    /// disabled: a control that switches to disabled on its own press may never raise the
    /// matching release, which would strand the session in Commanding. The press is refused here
    /// instead, where refusing costs nothing.
    /// </summary>
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
        stopRequested = false;
        document.BeginTake();

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

        // Claimed before the await, exactly like commandStarting in PrepareCommandAsync and for
        // the mirror image of its reason: a command key pressed while this stop waits for the
        // backend takes the machine to Commanding, and the stop then has nowhere to land.
        stopRequested = true;

        capture.Stop();
        Level = 0;
        PreviewText = string.Empty;

        await SafeStopEngineAsync();
        DrainSegments();

        // A command got in. It was pressed deliberately, so it runs - and FinishCommandAsync ends
        // the take afterwards instead of resuming it. Both presses are honoured that way; saying
        // "Stopped." here, over a command that is only starting, would honour neither.
        if (machine.State == SessionState.Commanding)
            return;

        // Somebody already finished this stop while the backend was closing - a command that
        // failed to start takes that route - and the session has moved on since. Without this the
        // stop would be completed twice, and a second recording started in the meantime would be
        // torn out from under itself: the machine would go to Idle while this method's capture.Stop
        // and SafeStopEngineAsync, both from before the await, had only ever touched the old ones.
        if (!stopRequested)
            return;

        CompleteStop();
    }

    /// <summary>
    /// Ends the take: out of Recording, buffer to disk, and a word about how it went. Reached
    /// straight from Stop, or from FinishCommandAsync when a command overtook one.
    /// </summary>
    private void CompleteStop()
    {
        stopRequested = false;

        machine.TryStopRecording();
        RefreshState();

        Flush();

        // Stop deliberately leaves the clipboard alone. A take is rarely one recording: you stop,
        // think, record again, and run a command over the result. Copying on every stop would
        // overwrite whatever you were holding, over and over, with a version you did not ask for.
        // Copy is a button and a hotkey, used once, when the text is actually done.

        // IsWarning cannot carry this: Copy, Revert and Reload all call Inform and are reachable
        // in the middle of a take, so a Ctrl+Alt+C forty seconds after a refused session would
        // erase the only trace of it. This latch is set by the failure paths of this recording
        // and cleared when the next one starts, by nothing else.
        // A reload that has been waiting for this moment goes in before the status line is
        // written, so the stop and the reload are one sentence rather than two, one of them gone.
        var reloaded = ApplyPendingConfigReload();

        if (recordingFailure is { Length: > 0 })
            Warn($"{recordingFailure} - stopped.");
        else if (!IsWarning)
            Inform(reloaded ? "Stopped. The config changed on disk and was reloaded." : "Stopped.");
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

        try
        {
            await current.StopAsync();
            EngineStatus = "idle";
        }
        catch (Exception ex)
        {
            // Batch raises its failures here and nowhere else, so clearing the status before this
            // await meant the batch backend could never show one.
            EngineStatus = "error";
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
            // Same gate discipline as the WAV writer below: the capture thread writes here while
            // the UI thread reads the clip out and disposes it.
            lock (clipGate)
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
        if (engine is not null)
            EngineStatus = "error";

        FailRecording(ex.Message);
    });

    /// <summary>Cheap facts for the status bar; the buffer is the only source that can change.</summary>
    private void UpdateCounters()
    {
        CharacterCount = editor.Text.Length;
        OnPropertyChanged(nameof(CanRestoreRaw));
    }

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

            // After the buffer, never before it: a raw file that cannot be written must not cost
            // the dictation a segment, and a throw here would drop every later one in the queue.
            try
            {
                document?.AppendRaw(text);
            }
            catch (Exception ex)
            {
                // Through the latch, not straight to the status line: informing at the end of the
                // recording would otherwise erase the only trace that raw is now incomplete.
                FailRecording($"The raw dictation file could not be written: {ex.Message}");
            }
        }

        PreviewText = string.Empty;
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(CanRestoreRaw));
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

        CommandStarting = true;

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
            CommandStarting = false;
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

        commandReleasedEarly = false;

        var entry = await PrepareCommandAsync();
        if (entry is null)
            return;

        spokenCommand = entry;
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
            return;
        }

        // The hold ended while channel 1 was still being paused, so the release had nothing to
        // act on yet. Honour it now rather than leaving the session in Commanding.
        if (commandReleasedEarly)
        {
            commandReleasedEarly = false;
            await EndCommandAsync();
        }
    }

    private async Task EndCommandAsync()
    {
        if (commandStarting)
        {
            // The hold ended before the command finished starting; PrepareCommandAsync hands over
            // as soon as it has an entry.
            commandReleasedEarly = true;
            return;
        }

        if (spokenCommand is null || activeCommand is null || document is null)
            return;

        var entry = activeCommand;
        spokenCommand = null;
        capturingCommandClip = false;
        capture.Stop();
        Level = 0;

        MemoryStream? clip;
        lock (clipGate)
        {
            clip = commandClip;
            commandClip = null;
        }

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
            lock (clipGate)
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
        entry.Engine = result.Model is { Length: > 0 }
            ? $"{result.Model} / {config.Claude.ResolveEffort()} effort"
            : config.Claude.Describe();
        entry.Status = result.Success ? CommandStatus.Succeeded : CommandStatus.Failed;

        if (result.Success)
        {
            try
            {
                // claude edited the file; the file is now the truth, so reload it into the buffer.
                var before = editor.Text;
                var reloaded = document.Read();
                editor.Text = reloaded;
                insertOffset = reloaded.Length;
                UpdateCounters();

                // A run that asked a question, or decided there was nothing to do, still returns
                // success and a sentence. Reporting that as "done" makes a no-op look like work.
                if (string.Equals(before, reloaded, StringComparison.Ordinal))
                {
                    entry.Status = CommandStatus.Failed;
                    entry.Response = $"No change made. {result.Summary}";
                    activeCommand = null;
                    spokenCommand = null;
                    await FinishCommandAsync();
                    Warn("Command changed nothing.");
                    return;
                }
            }
            catch (Exception ex)
            {
                FailCommand($"Could not read the file back: {ex.Message}");
                return;
            }
        }

        activeCommand = null;
        spokenCommand = null;

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
        spokenCommand = null;

        // The warning goes up after the resume, for the same reason.
        _ = FinishCommandAsync().ContinueWith(
            _ => Warn(message),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task FinishCommandAsync()
    {
        // A refused transition means someone else already finished this command. Resuming again
        // would start a second engine while the first one is still live.
        if (!machine.TryFinishCommand())
            return;

        RefreshState();

        if (machine.State == SessionState.Recording)
        {
            // Stop was pressed while channel 1 was being paused for this command, so it never
            // reached the machine. Finish it here rather than resuming a recording the user ended.
            if (stopRequested)
            {
                CompleteStop();
                return;
            }

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

        // The latch belongs to a stop that is no longer happening: the session came back to Idle
        // some other way, and nothing between here and the next StartRecordingAsync would clear it.
        if (machine.State != SessionState.Recording)
            stopRequested = false;

        // Refused unless the session actually came to rest, so a resumed recording keeps it waiting.
        if (ApplyPendingConfigReload() && !IsWarning)
            Inform("Config changed on disk - reloaded.");
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
        spokenCommand = null;
        await RunCommandAsync(entry, prebuilt.Text.Trim());
    }

    /// <summary>
    /// Puts the words as spoken back into the buffer, whatever the commands did since. The current
    /// text is snapshotted first, so this is one more revertible step, not a way to lose work.
    /// </summary>
    [RelayCommand]
    private void RestoreRaw()
    {
        if (document is null || !CanRestoreRaw)
            return;

        const string label = "Restore the raw dictation";
        snapshots.Push(editor.Text, label);

        var raw = document.RawText;
        editor.Text = raw;
        document.Flush(raw);
        insertOffset = raw.Length;

        // Source stays empty: that slot names the prebuilt button that fired, and this never went
        // anywhere near Claude.
        CommandLog.Insert(0, new CommandLogItem
        {
            CommandText = label,
            Status = CommandStatus.Succeeded,
            Response = "The buffer is what speech-to-text produced again. Revert brings the previous text back.",
        });

        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(CanRestoreRaw));
        Inform("Raw dictation restored.");
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
    private async Task CopyAsync()
    {
        // Copy was the one control left live during Commanding, and it flushes: the buffer would
        // have been written straight over the edit claude was making to the same file.
        if (machine.State == SessionState.Commanding)
        {
            Warn("Claude is editing the file - wait for the command to finish.");
            return;
        }

        await CopyToClipboardAsync(announce: true);
    }

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
            // Saved first so the file holds what the window holds - but never over a file that has
            // moved on. Another window's change may be sitting in configReloadPending, and writing
            // this window's older copy over it is the last-writer-wins the watcher exists to end.
            if (!configStore.ChangedOnDisk())
                configStore.Save(config);

            using var _ = Process.Start(new ProcessStartInfo(configStore.ConfigPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn($"Could not open the config: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the prompt folder in the file manager. A feature whose whole point is "these are your
    /// files" needs a way to reach them that is not a path in a README. Seeds first, so clicking it
    /// while the prompts are missing writes them rather than opening an empty folder.
    /// </summary>
    [RelayCommand]
    private void OpenPrompts()
    {
        if (!SeedPrompts())
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(prompts.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn($"Could not open the prompts folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the prompt files if that has not happened yet. False when it could not: either the
    /// writing failed, or this session is running on a config that did not parse and seeding it
    /// would save those defaults over the user's file.
    /// </summary>
    private bool SeedPrompts()
    {
        if (PromptSeeding.HasRun(prompts.Directory))
            return true;


        if (configBroken)
        {
            Warn("config.json could not be read, so the prompt files are not written yet. Fix it, then reload.");
            return false;
        }

        try
        {
            if (PromptSeeding.SeedIfMissing(prompts, config))
                TrySaveConfig();

            // The folder may not have been there a moment ago, and a watcher pointed at a
            // directory that does not exist watches nothing and never recovers.
            ArmPromptWatcher();

            RefreshPrebuiltCommands();
            return true;
        }
        catch (Exception ex)
        {
            promptSeedingFailure = $"Could not write the prompt files: {ex.Message}";
            Warn(promptSeedingFailure);
            return false;
        }
    }

    /// <summary>
    /// Points a watcher at the prompt folder, replacing any earlier one. A FileSystemWatcher over
    /// a directory that does not exist watches nothing and never recovers, so the one that was
    /// built before the folder existed has to be replaced once it does.
    /// </summary>
    private void ArmPromptWatcher()
    {
        // A reload can reach this from a command that finishes after the window was closed.
        if (disposed)
            return;

        promptWatcher?.Dispose();
        promptWatcher = fileWatcherFactory(
            prompts.Directory,
            "*.md",
            () => Dispatcher.UIThread.Post(OnPromptsChanged));
    }

    /// <summary>
    /// Rebuilds the command buttons from the prompt files. Does nothing at all when they have not
    /// changed: one save raises several events, and replacing the collection would flicker the
    /// buttons for no reason.
    /// </summary>
    private void RefreshPrebuiltCommands()
    {
        var loaded = prompts.Load();

        var unchanged = loaded.Prompts.Count == PrebuiltCommands.Count
            && loaded.Prompts.Zip(PrebuiltCommands).All(pair =>
                pair.First.Label == pair.Second.Label && pair.First.Text == pair.Second.Text);

        if (!unchanged)
        {
            PrebuiltCommands.Clear();
            foreach (var prompt in loaded.Prompts)
                PrebuiltCommands.Add(new PrebuiltCommand { Label = prompt.Label, Text = prompt.Text });

            OnPropertyChanged(nameof(HasPrebuiltCommands));
        }

        // A file that holds no prompt costs its button and nothing else, but it costs it silently -
        // and a button that is simply not there is indistinguishable from one never written. Said
        // when the set changes, not while it stays the same: this runs at the end of every take.
        var skipped = loaded.Skipped.Select(s => $"{Path.GetFileName(s.Path)} {s.Reason}").ToArray();
        var changedSet = !skipped.SequenceEqual(skippedPrompts, StringComparer.Ordinal);
        var hadSkipped = skippedPrompts.Length > 0;
        skippedPrompts = skipped;

        if (!changedSet)
            return;

        if (skipped.Length > 0)
        {
            lastSkipWarning = skipped.Length == 1
                ? $"{skipped[0]} and has no button."
                : $"{skipped.Length} prompt files have no button: {string.Join(", ", skipped)}.";

            // Never over a warning that is already up. This fires from a watcher, at any moment,
            // and a prompt file without a prompt is worth less than a config that cannot be read -
            // which is the line that keeps its reader from walking into the loss it describes.
            if (!IsWarning || StatusMessage == lastSkipWarning)
                Warn(lastSkipWarning);
        }
        else if (hadSkipped && StatusMessage == lastSkipWarning)
            // The warning names files. Leaving it up after they were fixed or deleted would point
            // at files that are not there any more - but only this line gets replaced. Informing
            // blind would clear IsWarning over a broken config or a missing API key, which are the
            // two things the window most needs to keep saying.
            Inform("Every prompt file reads now.");
    }

    /// <summary>
    /// A prompt file was written, renamed or deleted - by an editor, or by another window. Public
    /// for the same reason as OnConfigFileChanged: the watcher only says that something happened.
    /// </summary>
    public void OnPromptsChanged()
    {
        if (promptWatcher is null)
            return;

        // No IsBusy guard, unlike the config: a running prebuilt command holds its own
        // PrebuiltCommand instance, taken when the button was clicked, so rebuilding the row
        // cannot change the text that is already on its way to claude. The buttons are allowed to
        // follow the files immediately.
        RefreshPrebuiltCommands();
    }

    [RelayCommand]
    private void ReloadConfig()
    {
        // Applying the hotkeys restarts the service, which unhooks the hold key under a running
        // hold - and swapping the config under a running claude call is no better.
        if (commandStarting || activeCommand is not null)
        {
            Warn("Busy - wait for the running command to finish before reloading the config.");
            return;
        }

        if (!LoadConfigFromDisk())
        {
            Warn("config.json could not be read - nothing was changed, the file is yours to fix.");
            return;
        }

        // RefreshDevices may have warned that the configured microphone is gone; that outranks
        // the news that the config was read.
        if (!IsWarning)
            Inform("Config reloaded.");
    }

    /// <summary>
    /// Reads the file and applies everything in it that can change without a restart. False when
    /// the file could not be parsed, and then nothing is applied: the alternative is adopting
    /// defaults and writing them back over the file on the next microphone pick, which turns a
    /// typo into the loss of every setting - and of the text the user was about to fix.
    /// </summary>
    private bool LoadConfigFromDisk()
    {
        var loaded = configStore.LoadDetailed();
        configReloadPending = false;

        if (loaded.Broken)
        {
            configBroken = true;
            return false;
        }

        configBroken = false;

        config = loaded.Config;
        postProcessor = new TextPostProcessor(config.Dictionary);

        var hotkeysBefore = HotkeysEnabled;

        // Saving stays suppressed until the last of these has run. RefreshDevices assigns
        // SelectedDevice, which saves - so a reload used to echo the other window's file straight
        // back to disk, waking that window in turn.
        suppressConfigSave = true;
        SelectedSttMode = config.SttMode;
        HotkeysEnabled = config.Hotkeys.Enabled;
        RefreshLanguages();

        ApplyHotkeys();
        RefreshDevices();

        // The seeding is retried here, not only on start: a config that did not parse skips it,
        // and the reload button is how that config gets repaired without a restart.
        SeedPrompts();

        RefreshPrebuiltCommands();
        suppressConfigSave = false;

        // The switch is a privacy control, and this path installs or removes a system-wide keyboard
        // hook without anyone touching this window - the setter's own message is suppressed here,
        // because as far as it knows this is a load. Say it out loud, over anything else this
        // reload has to report: "off" reaching every window is the point, and "on" arriving by
        // itself is the half that had better not be silent.
        if (hotkeysBefore != HotkeysEnabled)
            Warn(HotkeysEnabled
                ? "Config changed: the global hotkeys are on. The chords work while another window has focus."
                : "Config changed: the global hotkeys are off. Nothing outside this window can start a recording.");

        return true;
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        OnPropertyChanged(nameof(MicrophoneLabel));

        if (suppressConfigSave || value is null)
            return;

        config.MicrophoneDeviceId = value.Id;
        config.MicrophoneDeviceName = value.Name;
        TrySaveConfig();
    }

    /// <summary>Rebuilds the picker from the config and selects what the config names. Callers suppress saving.</summary>
    private void RefreshLanguages()
    {
        Languages.Clear();
        Languages.Add(AutoLanguage);

        foreach (var code in (config.Stt.Languages ?? []).Select(code => code?.Trim() ?? string.Empty).Where(code => code.Length > 0))
            if (!Languages.Contains(code))
                Languages.Add(code);

        // An unknown code in the config is still the user's choice: offer it as-is rather than
        // silently replacing it with auto.
        var current = config.Stt.LanguageCode?.Trim();
        if (!string.IsNullOrEmpty(current) && !Languages.Contains(current))
            Languages.Add(current);

        SelectedLanguage = string.IsNullOrEmpty(current) ? AutoLanguage : current;
    }

    partial void OnSelectedLanguageChanged(string? value)
    {
        OnPropertyChanged(nameof(SttStatusText));

        // Rebuilding the list pushes a null through the binding; that is not a choice.
        if (suppressConfigSave || value is null)
            return;

        config.Stt.LanguageCode = IsAutoLanguage(value) ? null : value.Trim();
        TrySaveConfig();
    }

    /// <summary>
    /// A setting that cannot be persisted still applies to this session; the user is told rather
    /// than crashed. The config folder being read-only or locked is rare, and not worth the app.
    /// </summary>
    private void TrySaveConfig()
    {
        try
        {
            configStore.Save(config);
        }
        catch (Exception ex)
        {
            Warn($"Applied, but the config could not be saved: {ex.Message}");
        }
    }

    partial void OnSelectedSttModeChanged(SttMode value)
    {
        OnPropertyChanged(nameof(SttStatusText));

        if (suppressConfigSave)
            return;

        config.SttMode = value;
        TrySaveConfig();
    }

    partial void OnPreviewTextChanged(string value) => OnPropertyChanged(nameof(HasPreview));

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordButtonText));
        OnPropertyChanged(nameof(RecordButtonTooltip));
    }

    private void ApplyHotkeys()
    {
        if (!config.Hotkeys.Enabled)
        {
            hotkeysActive = false;
            RefreshButtonTooltips();
            HotkeyHint = "hotkeys off";
            if (!hotkeys.Stop())
                Warn("The keyboard hook could not be removed - restart mumblr to be sure the hotkeys are off.");
            return;
        }

        // The hint names the chords only when they are actually registered. RegistrationFailed
        // carries the reason to the status line, but it arrives through the dispatcher, so the
        // hint has to be decided from the result rather than from what the status line says.
        var started = hotkeys.Start(config.Hotkeys);
        hotkeysActive = started;
        RefreshButtonTooltips();

        HotkeyHint = started
            ? $"{config.Hotkeys.ToggleRecording} record  ·  hold {config.Hotkeys.CommandHoldKey} command  ·  " +
              $"{config.Hotkeys.Copy} copy  ·  {config.Hotkeys.RevertCommand} revert"
            : hotkeys.IsSupported
                ? "hotkeys unavailable"
                : "Global hotkeys need Windows - use the buttons.";
    }

    private void OnHotkey(HotkeyAction action) => Dispatcher.UIThread.Post(() =>
    {
        // The service is stopped while the switch is off; this guard is for a chord that
        // arrives anyway, and it does not trust the unhook.
        if (!HotkeysEnabled)
            return;

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

    /// <summary>Edge memory for the attention calls. Its own field: IsRecording has a public setter.</summary>
    private bool wasRecording;

    private void RefreshState()
    {
        var recording = machine.State == SessionState.Recording;
        if (recording && !wasRecording)
            attention.Begin();
        else if (!recording && wasRecording)
            attention.End();

        wasRecording = recording;
        IsRecording = recording;
        WindowTitle = recording ? "\u25CF Recording - mumblr" : "mumblr";
        IsCommanding = machine.State == SessionState.Commanding;
        StateLabel = machine.State switch
        {
            SessionState.Recording => "Recording",
            SessionState.Commanding => "Claude is working",
            _ => "Idle",
        };

        editor.IsReadOnly = machine.IsEditorLocked;
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(CanRestoreRaw));
        OnPropertyChanged(nameof(CanToggleHotkeys));
        ToggleHotkeysCommand.NotifyCanExecuteChanged();
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
        attention.End();

        try
        {
            capture.Stop();
            SafeStopEngineAsync().GetAwaiter().GetResult();
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
        disposed = true;

        configWatcher?.Dispose();
        configWatcher = null;

        promptWatcher?.Dispose();
        promptWatcher = null;

        hotkeys.Dispose();
        capture.Dispose();

        lock (wavGate)
        {
            wavWriter?.Dispose();
            wavWriter = null;
        }

        lock (clipGate)
        {
            commandClip?.Dispose();
            commandClip = null;
        }

        http.Dispose();
    }

    private static IAudioDeviceEnumerator CreateDeviceEnumerator() =>
        OperatingSystem.IsWindows() ? new WasapiDeviceEnumerator() : new NullAudioDeviceEnumerator();

    private static IAudioCapture CreateCapture() =>
        OperatingSystem.IsWindows() ? new WasapiAudioCapture() : new NullAudioCapture();

    private static IHotkeyService CreateHotkeys() =>
        OperatingSystem.IsWindows() ? new Win32HotkeyService() : new NullHotkeyService();
}

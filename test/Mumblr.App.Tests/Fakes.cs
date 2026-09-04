using System.Collections.Concurrent;
using Mumblr.App.Updates;
using Mumblr.App.ViewModels;
using Mumblr.Core.Audio;
using Mumblr.Core.Commands;
using Mumblr.Core.Config;
using Mumblr.Core.Hotkeys;
using Mumblr.Core.Stt;

namespace Mumblr.App.Tests;

public sealed class FakeEditorHost : IEditorHost
{
    public event Action? TextChanged;

    private string text = string.Empty;

    public string Text
    {
        get => text;
        set
        {
            if (text == value)
                return;

            text = value;
            TextChanged?.Invoke();
        }
    }

    public int CaretOffset { get; set; }
    public bool IsReadOnly { get; set; }
    public string? Clipboard { get; private set; }

    public void Insert(int offset, string text) => Text = Text.Insert(Math.Clamp(offset, 0, Text.Length), text);

    /// <summary>Stands in for a keystroke: the user editing the buffer, not mumblr writing to it.</summary>
    public void Type(string appended) => Text += appended;

    /// <summary>
    /// A real clipboard write goes through the platform and yields. Returning a completed task
    /// would let a test pass on an ordering the app never actually has.
    /// </summary>
    public async Task<bool> CopyToClipboardAsync(string text)
    {
        await Task.Yield();
        Clipboard = text;
        return true;
    }
}

public sealed class FakeDeviceEnumerator : IAudioDeviceEnumerator
{
    public List<AudioDeviceInfo> Devices { get; } = [new("dev-1", "Yeti")];

    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() => Devices;

    public AudioDeviceInfo? Find(string? deviceId) => Devices.FirstOrDefault(d => d.Id == deviceId);
}

public sealed class FakeCapture : IAudioCapture
{
    public event Action<ReadOnlyMemory<byte>>? DataAvailable;
    public event Action<float>? LevelChanged;
#pragma warning disable CS0067
    public event Action<Exception>? Failed;
#pragma warning restore CS0067

    public bool IsCapturing { get; private set; }
    public List<string> StartedWith { get; } = [];

    public void Start(string deviceId)
    {
        StartedWith.Add(deviceId);
        IsCapturing = true;
    }

    public void Stop() => IsCapturing = false;

    public void Emit(byte[] pcm)
    {
        DataAvailable?.Invoke(pcm);
        LevelChanged?.Invoke(0.25f);
    }

    public void Dispose() => Stop();
}

public sealed class FakeHotkeyService : IHotkeyService
{
    public event Action<HotkeyAction>? Triggered;
    public event Action? CommandKeyDown;
    public event Action? CommandKeyUp;
#pragma warning disable CS0067
    public event Action<string>? RegistrationFailed;
#pragma warning restore CS0067

    public bool IsSupported => true;
    public HotkeyConfig? Started { get; private set; }

    public void Start(HotkeyConfig config) => Started = config;
    public void Stop() => Started = null;
    public void Dispose() => Stop();

    public void Trigger(HotkeyAction action) => Triggered?.Invoke(action);
    public void PressCommandKey() => CommandKeyDown?.Invoke();
    public void ReleaseCommandKey() => CommandKeyUp?.Invoke();
}

public sealed class FakeSttEngine : ISttEngine
{
    public SttMode Mode { get; init; } = SttMode.Realtime;
    public bool SupportsPartials => Mode == SttMode.Realtime;

    public event Action<string>? PartialTranscript;
    public event Action<string>? SegmentCommitted;
    public event Action<Exception>? Failed;

    public SttSessionOptions? Options { get; private set; }
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public long PushedBytes { get; private set; }

    /// <summary>Emitted from <see cref="StopAsync"/>, the way the batch backend behaves.</summary>
    public string? TextOnStop { get; set; }

    /// <summary>Thrown from <see cref="StopAsync"/>, the way a rejected batch request behaves.</summary>
    public Exception? FailureOnStop { get; set; }

    public Task StartAsync(SttSessionOptions options, CancellationToken cancellationToken = default)
    {
        Options = options;
        Started = true;
        return Task.CompletedTask;
    }

    public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default)
    {
        PushedBytes += pcm16.Length;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Held open to reproduce the real pause window: stopping the realtime backend waits for the
    /// last segment, or five seconds. Everything that can start a command is live in there.
    /// </summary>
    public TaskCompletionSource? StopGate { get; set; }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (StopGate is not null)
            await StopGate.Task;

        Stopped = true;
        if (FailureOnStop is not null)
            throw FailureOnStop;

        if (TextOnStop is { Length: > 0 })
            SegmentCommitted?.Invoke(TextOnStop);
    }

    public void Commit(string text) => SegmentCommitted?.Invoke(text);

    public void Partial(string text) => PartialTranscript?.Invoke(text);

    /// <summary>The realtime backend's own failure path: an error message over an open socket.</summary>
    public void Fail(Exception ex) => Failed?.Invoke(ex);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeSttEngineFactory : ISttEngineFactory
{
    public ConcurrentQueue<FakeSttEngine> Created { get; } = new();
    public FakeSttEngine? Last { get; private set; }
    public string ClipText { get; set; } = "letzten Satz loeschen";
    public Exception? ClipFailure { get; set; }

    public ISttEngine Create(SttMode mode)
    {
        var engine = new FakeSttEngine { Mode = mode };
        Created.Enqueue(engine);
        Last = engine;
        return engine;
    }

    public IClipTranscriber CreateClipTranscriber() => new FakeClipTranscriber(this);

    private sealed class FakeClipTranscriber(FakeSttEngineFactory owner) : IClipTranscriber
    {
        public Task<string> TranscribeAsync(byte[] pcm16, SttSessionOptions options, CancellationToken cancellationToken = default)
        {
            if (owner.ClipFailure is not null)
                throw owner.ClipFailure;

            return Task.FromResult(owner.ClipText);
        }
    }
}

public sealed class FakeUpdateService : IUpdateService
{
    public UpdateService.UpdateCheck Outcome { get; set; } = UpdateService.UpdateCheck.UpToDate;
    public string? AvailableVersion { get; set; }
    public bool Applied { get; private set; }
    public int Checks { get; private set; }

    public Task<UpdateService.UpdateCheck> CheckAsync()
    {
        Checks++;
        return Task.FromResult(Outcome);
    }

    public void ApplyAndRestart() => Applied = true;
}

public sealed class FakeClaudeRunner : IClaudeCommandRunner
{
    public List<(string Command, string Path)> Calls { get; } = [];
    public Func<string, string, CommandResult>? Behaviour { get; set; }

    /// <summary>For holding a command open while something else is attempted against the UI.</summary>
    public Func<string, string, Task<CommandResult>>? AsyncBehaviour { get; set; }

    /// <summary>What the fake writes into the file, standing in for Claude's edit.</summary>
    public string? FileContentAfterRun { get; set; }

    public async Task<CommandResult> RunAsync(string commandText, string absoluteFilePath, CancellationToken cancellationToken = default)
    {
        Calls.Add((commandText, absoluteFilePath));

        if (AsyncBehaviour is not null)
        {
            var held = await AsyncBehaviour(commandText, absoluteFilePath);
            if (FileContentAfterRun is not null)
                File.WriteAllText(absoluteFilePath, FileContentAfterRun);

            return held;
        }

        if (FileContentAfterRun is not null)
            File.WriteAllText(absoluteFilePath, FileContentAfterRun);

        return Behaviour?.Invoke(commandText, absoluteFilePath)
               ?? new CommandResult(true, "Removed the last sentence.", "{}", TimeSpan.FromSeconds(9));
    }
}

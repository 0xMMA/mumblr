using Mumblr.Core.Config;

namespace Mumblr.Core.Hotkeys;

/// <summary>Logical hotkey actions. The platform layer maps them onto real key registrations.</summary>
public enum HotkeyAction
{
    ToggleRecording,
    Copy,
    RevertCommand,
}

/// <summary>
/// Global hotkeys that must fire while the IDE or terminal has focus, plus the hold-to-talk key
/// for channel 2 which needs key-up and therefore a low level hook.
/// </summary>
public interface IHotkeyService : IDisposable
{
    event Action<HotkeyAction>? Triggered;

    /// <summary>Command key went down. Start recording the command clip.</summary>
    event Action? CommandKeyDown;

    /// <summary>Command key came back up. Stop the clip and run it.</summary>
    event Action? CommandKeyUp;

    /// <summary>Registration failures, e.g. a combination another app already owns.</summary>
    event Action<string>? RegistrationFailed;

    bool IsSupported { get; }

    void Start(HotkeyConfig config);
    void Stop();
}

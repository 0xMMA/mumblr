using System;
using Mumblr.Core.Config;
using Mumblr.Core.Hotkeys;

namespace Mumblr.App.Hotkeys;

/// <summary>Stand-in on non-Windows: the UI buttons still work, the global hotkeys do not.</summary>
public sealed class NullHotkeyService : IHotkeyService
{
#pragma warning disable CS0067 // never raised without a platform hook
    public event Action<HotkeyAction>? Triggered;
    public event Action? CommandKeyDown;
    public event Action? CommandKeyUp;
#pragma warning restore CS0067
    public event Action<string>? RegistrationFailed;

    public bool IsSupported => false;

    public void Start(HotkeyConfig config) => RegistrationFailed?.Invoke("Global hotkeys need Windows.");

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}

---
id: 080-hotkey-kill-switch
track: guard
---

# One click turns the global hotkeys off

## Intent
The hotkeys are global by design - `RegisterHotKey` for toggle, copy and revert, a low level
keyboard hook for the hold key - so recording starts while the IDE has focus. The flip side:
a game, a screen share, someone else at the keyboard, a chord that collides with another tool,
and a recording starts in the background that nobody wanted, billed per minute and holding
whatever was said in the room. Today the only ways out are closing the app or editing the
config.

One control in the window, one click: hotkeys off, and the status bar says so. One click:
back on.

## Acceptance
- [ ] A toggle in the toolbar with an unmistakable on/off state. Off means
      `IHotkeyService.Stop()` has run: nothing registered, keyboard hook gone. Verified by hand
      on Windows - a chord does nothing while a terminal has focus.
- [ ] The view model ignores `Triggered`, `CommandKeyDown` and `CommandKeyUp` while off, even
      if the platform service fires them anyway. The switch does not trust the unhook.
- [ ] The status bar hint reads `hotkeys off` instead of the chord list while disabled.
- [ ] The toggle is disabled during Commanding. Unhooking mid-hold would swallow the key-up
      and leave the command running until the pause window ends.
- [ ] Record, the mouse hold-to-command button and every other control keep working with
      hotkeys off. Switching off never stops a recording that is already running.
- [ ] Persisted as `hotkeys.enabled` (default true); survives restart and config reload, and
      `ApplyHotkeys` honours it.
- [ ] Headless tests: events ignored while off; chords registered again after on; toggle
      refused in Commanding; config round trip.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- Persisted, not per session. Whoever turned them off had a reason that outlives the process,
  and the status bar makes the state visible, so "why do my hotkeys not work" resolves in one
  glance.
- Minimising or losing focus disables nothing. Recording from inside the IDE is the point.

## Out of scope
- Rebinding chords in the UI; the config stays the editor for that.
- Any change to what the chords do.

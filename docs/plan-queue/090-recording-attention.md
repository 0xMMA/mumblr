---
id: 090-recording-attention
track: guard
---

# A running recording is visible from every other window

## Intent
Start recording, tab into the IDE, get absorbed. Twenty minutes later the microphone is still
open, the realtime session still streaming, every minute billed, and the taskbar shows a
quiet "mumblr" like any idle app. The window knows it is recording; nothing outside it does.

Recording has to be visible from the taskbar, not only from inside the window.

## Acceptance
- [ ] While Recording the window title carries the state (`● Recording - mumblr`), which is
      what the taskbar button and Alt+Tab show. Idle and Commanding restore the plain title.
- [ ] While Recording and not the foreground window, the taskbar button flashes
      (`FlashWindowEx`, `FLASHW_ALL | FLASHW_TIMERNOFG`) until the window is brought to the
      front. Losing focus again while still recording starts it again.
- [ ] Stopping the recording stops the flashing (`FLASHW_STOP`) whatever the focus is.
- [ ] Commanding does not flash. It costs money too, but it ends on its own; flashing is for
      the one state only the user can end.
- [ ] The Win32 part sits behind a small interface so the view model is testable headless:
      title per state; attention requested on recording plus focus loss; cleared on stop and
      on activation.
- [ ] Verified on Windows by hand: tab away during a recording, the button flashes and stays
      highlighted; tab back, it clears. The observed behaviour goes into the Log.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- Windows flashes the button a system-defined number of times, then leaves it highlighted
  until the window comes to the front. That is the platform's idea of blinking; the
  highlighted state is what survives, and it is enough.
- No tray icon, no overlay icon on the taskbar button (`ITaskbarList3` is COM and its own
  piece of work), no sound.

## Out of scope
- A recording time limit or auto-stop. Attention first; if the wallet still hurts after
  that, a limit is its own task.

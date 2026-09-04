---
id: 040-status-bar
track: main
---

# A status bar that answers "is this thing working" at a glance

## Intent
The status bar today is one message plus an update button. It says what just happened; it
does not say what state the app is in. The sibling project `recap` (../recap,
`src/Recap.Desktop/Views/MainWindow.axaml`) solves the same problem for the same user with a
row of small always-visible facts: recording state, active language, counters, an API
indicator that is green or red, a clickable version, and a GitHub link.

The concrete cost of not having this: GitHub issue #1 was a request that never reached
ElevenLabs, and nothing in the window said so. An API indicator and a visible backend state
turn that class of bug into something you see instead of something you deduce.

## Acceptance
- [ ] Version, read from the assembly, sits at the right of the bar. Clicking it checks for
      updates — the current update button folds into it.
- [ ] An API indicator shows whether `ELEVENLABS_API_KEY` (or the `XI_API_KEY` fallback) is
      present, coloured, never showing the key or any part of it.
- [ ] The active STT mode and the backend's connection state are visible while recording.
- [ ] Session facts that cost nothing to track are shown: the selected microphone and the
      character count of the buffer.
- [ ] A GitHub link button opens the project page.
- [ ] The existing warning colouring still wins over informational messages — the
      "configured microphone is gone" warning must not be overwritten, and its headless test
      still passes.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- Borrow recap's layout and its idea of which facts matter. Do not copy its code: different
  view model, different converters, and mumblr has no segment list.
- Presence of the key only. mumblr never reads a key from config by design, and the bar must
  not become the first place a key is displayed.
- The bar stays one line. If it does not fit, drop counters before dropping state.

## Out of scope
- A settings window. Config stays a JSON file with a reload button.
- Any live API health probe or quota display — that is a request to ElevenLabs mumblr has no
  reason to make.

## Log
- The bar now carries microphone, STT mode plus backend state, character count and an API-key
  indicator on the left, the message in the middle, and hotkey hint, version and a GitHub link on
  the right. The separate update button folded into the version button: it reads `v0.1.0` and
  checks on click, and turns into `update to <version>` once there is something to install.
- `EngineStatus` is a fact, not a guess. Realtime returns from `StartAsync` only once the websocket
  is up, so "connected" means connected; batch holds audio locally until stop and says "buffering"
  instead of claiming a connection it does not have. A failure sets "error" and stays there until
  the next start.
- The API indicator is presence only - green or red, never the value. mumblr reads the key from the
  environment and the bar must not become the first place it is displayed.
- Adding a status write is adding a chance to erase a warning: `Initialize` and `StopRecordingAsync`
  both had that bug. Nothing added here writes over `StatusMessage`; the new facts are their own
  properties, so the warning path is untouched and its test still passes.

---
id: 140-hotkeys-off-on-first-run
track: guard
---

# A fresh install starts with the global hotkeys off

## Intent
The chords are global, and on a first run nobody has chosen them: one that collides with a game or
another tool starts a recording nobody wanted. A fresh install starts with the kill switch from 080
off; the buttons cover everything, and the status bar toggle turns the chords on once the user
wants them.

## Acceptance
- [ ] A first load with no `config.json` writes `hotkeys.enabled: false`, and the second start
      reads it back as off.
- [ ] A config that lacks the key keeps the chords on - it predates the switch, and an upgrade must
      not take them away. Existing installs are untouched: their file already carries the key.
- [ ] README and `RELEASE_NOTES.md` say so.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- The class default stays `true`. Only the first-run branch in `ConfigStore` sets it off, because
  a missing key means "from before the switch", not "new user".

## Log
- A config that cannot be parsed now starts with the chords off too, not only a missing file. It
  used to fall back to the class default, which turned the keyboard hook on for a file that said
  off - and the next setting change wrote that over the file.
- The App test fixtures start from a fresh config, so they turn the chords on explicitly; without
  that, every hotkey test pressed into nothing and the suite hung.
- The switch tooltip names the chords from the config. With them off nothing else in the window
  says what they are, and a fresh install starts there.

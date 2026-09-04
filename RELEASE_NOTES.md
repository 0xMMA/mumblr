## mumblr

Ultra specific voice recorder for development work: talk, get markdown, hand it to Claude Code.

**Portable:** download `mumblr-<version>-win-Portable.zip`, unzip anywhere, put the folder on your `PATH`,
then run `mumblr .` in the repo you are working in.

**Installer:** `mumblr-<version>-win-Setup.exe` installs per user. In-app updates need a release
feed the app can read - see the repository if the version button reports that it could not reach
one.

### Fixed in 0.1.4

- **A key press on the command key while another command was running derailed it.** The release
  ended whatever command happened to be active - not the one it started - marking it failed,
  unlocking the editor and resuming dictation underneath a claude call that was still writing to
  the file. A second one could start a second transcription backend on top of the first.
- **A hold shorter than the pause it triggers is no longer swallowed.** Stopping the realtime
  backend takes up to five seconds; a quick press and release fell entirely inside that window and
  left the session stuck with the microphone running.
- **Copy is refused while claude is editing the file.** It flushed the buffer straight over the
  edit in progress.
- The prebuilt command button is called **Grammar**, not "Grammatik". The window is English; the
  command it sends is still German, because the text it edits is. A `config.json` written by an
  earlier version keeps the old label - rename it there, or delete the entry and let it be
  recreated.
- The update button says it will restart, and refuses to while a command is running.
- The command log keeps the effort next to the model, and the batch backend can report an error
  state at all.
- Closing the window during a recording no longer leaves a websocket open, and running the tests
  no longer creates a folder in your home directory.

### Fixed in 0.1.3

- **Dictations are no longer written into the app's own install folder.** Started from the start
  menu shortcut, mumblr wrote into the Velopack `current` directory, which the next update
  replaces wholesale - the file and its WAV would have been deleted by the first update that
  landed. A target inside the application directory now falls back to `Documents\mumblr`.
- A rejected transcription survives everything that happens afterwards. Copying, reverting or
  reloading the config mid-recording used to erase the error message, and the stop line then
  reported a clean stop over an empty buffer.
- A failing command warns instead of reporting in informational grey, and the "Recording resumed."
  message no longer overwrites it.
- The command log names the model that actually answered, read out of the CLI's own envelope,
  rather than the one the config asked for.
- The character count follows what you type, not only what mumblr writes.
- A prebuilt button and the hold key can no longer both start a command in the moment channel 1 is
  being paused - that could run two `claude -p` processes on one file.
- A typo in `keytermsEncoding` falls back to the encoding that works instead of the one that made
  every request fail.

### Fixed in 0.1.2

- Release assets carry their version in the filename.
- The update check no longer claims to be the latest build when it never managed to ask.

### Fixed in 0.1.1

- **Dictation works again.** 0.1.0 packed the keyterm list into a single value, which ElevenLabs
  read as one oversized keyterm and refused - every fresh install failed on its very first
  recording. Keyterms now go out as repeated parameters, and a term the API would reject is
  dropped instead of killing the request.
- A rejected request is no longer invisible: the stop message stopped overwriting the error that
  explained it.
- The status bar now shows the microphone, the STT backend's actual state, a key-present
  indicator, the character count and the running version.
- Prebuilt command buttons: the edits you dictate word for word every day are a click, with no
  microphone and no transcription round trip.
- A blank model or effort in the config can no longer downgrade a command; it falls back to Opus
  at high effort.

### What is in this build

- `mumblr .` creates `dictated-<timestamp>.md` in the folder you pass, with the WAV next to it
- Channel 1: dictation through ElevenLabs Scribe v2, realtime (default) or batch, `no_verbatim` on,
  auto language detection, user maintained keyterms and dictionary replacements
- Channel 2: hold the command key, speak an edit ("delete the last sentence", "clean this up"),
  and a local `claude -p` applies it to the file - with a command log, a snapshot and a revert hotkey
- Explicit microphone choice with a level meter; mumblr never follows the Windows default device
- Global hotkeys that work while your IDE has focus

### Before the first run

Set your ElevenLabs key as an environment variable - mumblr never reads it from a config file:

```
setx ELEVENLABS_API_KEY "your-key"
```

`claude` must be on your `PATH` for channel 2.

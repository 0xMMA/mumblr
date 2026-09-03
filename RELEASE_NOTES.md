## mumblr

Ultra specific voice recorder for development work: talk, get markdown, hand it to Claude Code.

**Portable:** download `mumblr-win-Portable.zip`, unzip anywhere, put the folder on your `PATH`,
then run `mumblr .` in the repo you are working in.

**Installer:** `mumblr-win-Setup.exe` installs per user and updates itself.

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

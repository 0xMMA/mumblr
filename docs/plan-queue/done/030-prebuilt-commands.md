---
id: 030-prebuilt-commands
track: main
---

# Prebuilt command buttons

## Intent
The loop that actually happened all day: record a passage, hold the command key, and say
"Mach Grammatik, Satzbau und Satzordnung ordentlich". Same sentence every time. Speaking a
fixed string into a microphone so it can be transcribed back into the same fixed string is
pure ceremony, and it adds a batch STT round trip plus its transcription risk to a command
whose text was never in doubt.

A prebuilt command is that string on a button: click, and the text goes straight into
`claude -p` with no microphone involved.

## Acceptance
- [ ] `MumblrConfig` gains a prebuilt command list (label + command text), shipped with the
      grammar/sentence-order command as the first entry.
- [ ] Each entry renders as a button in the command panel. Buttons are disabled while the
      app is Commanding, like the hold-to-command control.
- [ ] Clicking one runs the identical path a spoken command takes — snapshot, editor lock,
      flush, `claude -p`, reload into the buffer, command log entry, revert support. The
      only difference is where the command text came from.
- [ ] A prebuilt command works from Idle and from Recording; from Recording it pauses and
      resumes channel 1 exactly as a spoken command does.
- [ ] The command log marks the entry as prebuilt and names which one ran.
- [ ] Headless tests cover: fired from Idle, fired from Recording (pause/resume), refused
      while Commanding, and revert after a prebuilt command.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- The default entry's text is German. Michael dictates German with English technical terms,
  and the header prompt already tells Claude to keep the author's language.
- Same code path as spoken commands, no parallel implementation. Extract the "run this
  command text" step so both callers share it; do not copy the orchestration.
- Config file plus the existing reload button is the editing story for now. No prompt
  library UI — the spec lists that as a non-goal.
- Buttons are not hotkeys in this task. If a hotkey is wanted later it hangs off the same
  entry list.

## Out of scope
- Prompt management UI, chaining several commands, per-file or per-project presets.
- Any change to channel 1.

## Log
- The two sources share both halves of the flow by construction, not by copy: `PrepareCommandAsync`
  (pause channel 1, enter Commanding, flush, open a log entry) and `RunCommandAsync` (snapshot,
  `claude -p`, reload the file, finish). `BeginCommandAsync`/`EndCommandAsync` are now just the
  microphone and STT sandwich between them, and a prebuilt command is the same sandwich with the
  filling handed over instead of recorded.
- The microphone guard moved into the spoken path only. A prebuilt command must run on a machine
  with no usable capture device - refusing it there would be refusing the one command that needs
  nothing from the hardware.
- Buttons bind to `IsCommanding` for their enabled state, the same signal the hold button uses, so
  a second command cannot start while one is in flight. Covered by a test that fires a button
  mid-hold.
- One entry ships. Adding more is a config edit plus the existing reload button; a management UI
  stays a non-goal.

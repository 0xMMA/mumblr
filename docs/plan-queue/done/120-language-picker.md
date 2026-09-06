---
id: 120-language-picker
track: stt
---

# Pick the transcription language in the window

## Intent
`stt.languageCode` exists, defaults to unset (auto-detect) and changes only by editing the
config and reloading. Auto-detect handles German with English terms well enough that this
never hurt - until it does: a short English take in a German session, a mumbled first
sentence, and Scribe locks onto the wrong language for the segment. recap solved this with a
picker in the toolbar: auto, the default language, or a manual pick, applied to the next
call.

## Acceptance
- [ ] A picker in the toolbar next to the STT mode: `auto` plus the codes from a new
      `stt.languages` list (default `["de", "en"]`; any code ElevenLabs accepts can be added
      there). Selection writes `stt.languageCode` (`auto` means unset) and persists.
- [ ] Applies to the next STT session: realtime sends it on the next websocket, batch on the
      next POST. Disabled while Recording, like the mode picker - a running websocket has its
      language already.
- [ ] The status bar shows the active language whenever it is not `auto`.
- [ ] Config reload re-reads the picker. An unknown code in `languageCode` is still selectable
      and shown as-is, never silently replaced.
- [ ] The window at `MinWidth` fits the new control without clipping the mic picker or the
      level bar. Raise `MinWidth` if it does not, and say so in the Log.
- [ ] Tests: the request carries `language_code` when set and omits it on `auto`, both
      engines, through the existing request tests; picker refused in Recording; config round
      trip; unknown code survives.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- The picker is STT only. The header prompt does not name a language (070); the file's own
  language is the rule for Claude, whatever Scribe was told.
- A list in the config rather than a free-text box. The codes ElevenLabs accepts are not
  guessable and a typo costs a request. The list is the user's; the default is two entries.
- Default stays `auto`. Every session so far ran on it.

## Out of scope
- Switching language mid-recording.
- Translating anything.

## Log
- "Picker refused in Recording" became "applies to the next session". The combo is disabled
  while recording, and a refusal inside the view model would only desynchronise a disabled
  control; the session options are read at engine start, so a change during a recording waits
  for the next one by construction. A test pins that.
- The language rides on the STT status text ("Realtime - connected · de") instead of a new
  status bar field, so auto costs no width at all.
- `MinWidth` went from 1040 to 1160: the picker and its label are about 120 px of toolbar that
  cannot shrink. The default width is 1180, so the window opens the same as before.
- Rebuilding the list on config reload pushes a null through the ComboBox binding; the change
  handler treats null as "not a choice" and the rebuild runs under the save suppression.
- Windows by-hand check open: the toolbar at 1160 px with a long microphone name.
- Review: a hand-written "auto" or a padded code went on the wire as-is; the session options
  factory now maps blank and auto to unset and trims the rest. The picker governs spoken commands
  too (the command clip reads the same setting) - tooltip, README and release notes say so now.
  A null `languages` list no longer stops the app; nulls anywhere in the config mean the default.

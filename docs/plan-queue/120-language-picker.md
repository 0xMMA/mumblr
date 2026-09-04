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

---
id: 070-english-command-prompts
track: main
---

# The shipped prompts are English; the dictation keeps its language

## Intent
The Grammar button ships a German prompt, and the README quotes it as the sentence the tool
was built around. Neither belongs there any more: the window is English, the README is
English, and the prompt is an instruction to Claude, not content. It reads as if the tool
were German.

The reason it was German is the one thing that must survive the change: a command must never
change the language of the text it works on. German dictation with English technical terms
comes back as German dictation with English technical terms - not translated, not "tidied"
into English because the instruction was English.

## Acceptance
- [ ] The shipped Grammar command text is English and says, in the command itself, that the
      content and its language stay as they are.
- [ ] `DefaultHeaderPrompt` no longer claims the file holds German. It states the language
      rule once, as the rule of every command: the output is in the language the file is in,
      whatever that is, and technical terms stay in the language the author used them in.
- [ ] README: the quoted German sentence is gone; the `prebuiltCommands` row and the Grammar
      paragraph say "prompt English, content untouched". `AGENTS.md` Language section: the
      example no longer describes the prompt text as German dictation - the prompt is an
      instruction and English; the content it operates on keeps its language.
- [ ] A config.json written by 0.1.x carries the German text as the user's own entry. The
      loader does not rewrite it. `RELEASE_NOTES.md` says to delete the `prebuiltCommands`
      entry to pick up the new default.
- [ ] Run Grammar once over a German paragraph with English terms (needs `claude` on PATH).
      The result is German, the terms are unchanged. Paragraph and summary line go into the
      Log.
- [ ] A test pins the language rule into both the command text and the header prompt, so a
      rewording cannot drop it silently.
- [ ] `dotnet build && dotnet test` green.

## Decisions
- The rule lives in the header prompt and is repeated in the Grammar command. The header
  protects every command, spoken ones included; the repetition protects the one command
  whose whole job is rewriting sentences.
- No translation feature. "Translate this to English" is a spoken command a user can give;
  a default must never do it uninvited.

## Out of scope
- Picking the STT language (120).
- Any new prebuilt command (110).

## Log
- The live check is a test, not a one-off: `LiveClaudeTests` in Mumblr.Core.Tests, armed by the
  same `MUMBLR_LIVE_TESTS=1` gate as the ElevenLabs tests, skipped otherwise. It runs the shipped
  Grammar command through the real `ClaudeCommandRunner` (which is platform neutral, so it runs
  on the Linux dev box too) and asserts invariants only: the English terms survive verbatim, the
  German function words are still there, no " the " appears.
- First run, 11 s, Opus: "Also ich hab mir überlegt dass wir das Feature mit den Vertical Slices
  anders bauen sollten weil das mit dem Aspire Dashboard und OpenTelemetry funktioniert ja
  eigentlich schon ganz gut aber die Handler sind halt viel zu fett geworden. ..." came back as
  "Also, ich hab mir überlegt, dass wir das Feature mit den Vertical Slices anders bauen sollten.
  Das mit dem Aspire Dashboard und OpenTelemetry funktioniert ja eigentlich schon ganz gut, aber
  die Handler sind halt viel zu fett geworden. Wir müssten den Command- vom Query-Teil trennen,
  ..." Summary: "Added the missing commas, split the overlong run-on sentence after the dangling
  'weil' clause, and tightened 'die Commands vom Query Teil' to 'den Command- vom Query-Teil'."
- The envelope's `modelUsage` lists haiku next to opus. The edit is Opus; the CLI runs its own
  helper calls on haiku. The log column shows both, which is honest, if surprising.
- The header prompt's rule is one sentence: "The result is in the language the file is in,
  whatever that is, and a technical term stays in the language the author used it in." It no
  longer says what the file holds, so 120 can pick any STT language without touching it.
- Release notes open a "Changed in 0.2.0" section. Six queued features make the next tag a
  minor bump, not a patch.
- Review: the header prompt and the prebuilt list are persisted in full on first run, so no
  existing install would have seen either change; "delete the entry" would have emptied the
  list. `ConfigMigration` now replaces a stored value that still equals any earlier shipped
  default (fingerprints of three header prompts and two command lists) and leaves an edited one
  alone. The Grammar text no longer reads as "translate the terms"; the header yields to a
  command that asks for a translation; the live test cleans up and uses word boundaries.
- Second review: the config's own null handling was the weak point behind this change. Nulls are
  now stripped at the JSON level before deserialization, so a hand-edited `"sttMode": null` costs
  that key its default instead of silently replacing the whole file with defaults - which is what
  used to happen, because the deserializer's throw was indistinguishable from a broken config.
  `config.json` is written to a per-process temp file and moved into place.

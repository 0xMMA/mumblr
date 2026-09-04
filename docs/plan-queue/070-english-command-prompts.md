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

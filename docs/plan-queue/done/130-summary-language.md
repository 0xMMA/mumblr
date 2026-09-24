---
id: 130-summary-language
track: main
---

# The command summary comes back in the language of the dictation

## Intent
The one-line summary `claude -p` returns for the command log was pinned to English by the header
prompt, following the rule that every string in the window is English. Over German dictation that
put a sentence about the author's text in a language the text is not in. Issue #4 asked which rule
wins; the answer is the dictation's: the summary is content about content and sits beside it.

## Acceptance
- [ ] `DefaultHeaderPrompt` asks for the summary in the language the author dictated in, without
      naming a language.
- [ ] The replaced header is a legacy default in `ConfigMigration`, its fingerprint pinned against
      the literal text in `ShippedDefaults`, so an unedited install follows and an edited one stays.
- [ ] A test pins the rule so a rewording cannot bring "English sentence" back.
- [ ] README, `RELEASE_NOTES.md` and the language rule in `AGENTS.md` say so.
- [ ] `dotnet build && dotnet test` green; the live Claude tests armed once.

## Decisions
- "The language the author dictated in", not "the language the file is in": after a spoken
  "translate this to English" the file is English, and the summary still speaks to the author.

## Out of scope
- Any other string in the window. The rule for UI text is unchanged.

## Log
- The first wording ("the language the author dictated in") was ambiguous for the shipped buttons:
  Grammar and Prompt send English commands, and the header says the command was spoken. It now
  reads "the language the dictation was in before your edit - whatever language the command is in".
- The summary also lands in the status line and after English prefixes ("No change made.",
  "Command failed:"). It keeps the dictation's language there too; the AGENTS.md language rule says
  so, and the prefixes stay English.
- Live tests armed: Grammar and Prompt over German dictation and a spoken "Übersetz das Ganze ins
  Englische." all came back with a German summary. The live assertion is positive now (a German
  function word or an umlaut) - "no `the`" alone passed a typical English summary.

---
id: 150-transcript-aware-commands
track: main
---

# Commands know the dictation is a speech-to-text transcript

## Intent
The header prompt told Claude that the spoken command may be garbled, but called the file only
"dictation". Grammar's "change nothing about the content" then reads a misheard word or a name in
two spellings as content and protects it. Give the model the context and let it judge - the "high
freedom" end of the skill authoring guidance - rather than growing the Grammar prompt into a rule
list.

## Acceptance
- [ ] The header says the file is a speech-to-text transcript, that restoring what was said is not
      a change of content, and that this applies only within what the command changes.
- [ ] The replaced 0.3.1 header is a legacy default, its fingerprint pinned against the literal
      text in `ShippedDefaults`.
- [ ] Grammar and Prompt are unchanged.
- [ ] Live tests: Grammar fixes a garbled product name the text also spells right; a narrow command
      ("Lösch den letzten Satz.") leaves the garbled name elsewhere alone.
- [ ] `dotnet build && dotnet test` green; README and `RELEASE_NOTES.md` say so.

## Decisions
- In the header, not in Grammar: the header migrates to every unedited install, Grammar is a file
  the user owns and reaches new installs only. And every command benefits, spoken ones included.
- A user-specific vocabulary (the keyterm list as Claude's spelling reference) is a later feature
  and needs more thought.

## Out of scope
- Passing the keyterms or any vocabulary to Claude.

## Log
- The first live run merged "Jannik" and "Yannick" into one person. Michael: rather leave a double
  spelling than merge two people. The header now says two spellings of a name may be two people
  and to leave both when unsure; the live test asserts both survive, and the next run kept them
  and said why in the summary.
- Review: the live dictation gained two rare, correctly spelled terms (Velopack, MinVer) that a
  model told to expect mishearings must leave alone; the misheard-name test ran three times in a
  row, stable. A new theory loads every shipped header and checks it migrates - the pinned
  fingerprints only prove the hex matches the text, not that the hex is in the set.
- No red run against the 0.3.1 header was made, so the release note says Grammar "could" protect
  a mishearing rather than that it did.
